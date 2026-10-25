//! A bounded, single-writer snapshot journal. Never remove accepted work on I/O error.
use crate::{config::Config, dedupe::atomic_write, message::Message};
use anyhow::{Context, Result, ensure};
use fs2::FileExt;
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    fs::{File, OpenOptions},
    path::PathBuf,
};

#[derive(Clone, Serialize, Deserialize)]
pub struct Entry {
    #[serde(default)]
    pub queue_id: String,
    #[serde(default)]
    pub topic: String,
    #[serde(default)]
    pub replay: bool,
    #[serde(default)]
    pub first_attempt_at_ms: Option<i64>,
    #[serde(default)]
    pub last_attempt_at_ms: Option<i64>,
    #[serde(default)]
    pub rejected_at_ms: Option<i64>,
    #[serde(default)]
    pub failure_reason: Option<String>,
    pub message: Message,
    pub accepted_at: i64,
    #[serde(default)]
    pub attempts: u32,
    #[serde(default)]
    pub next_attempt_at_ms: i64,
    pub destinations: BTreeMap<String, String>,
}
#[derive(Clone, Default, Serialize, Deserialize)]
struct Snapshot {
    pending: BTreeMap<String, Entry>,
    cursors: BTreeMap<String, String>,
    failed: Vec<Entry>,
}
pub struct Queue {
    path: PathBuf,
    max_bytes: usize,
    write_failed: bool,
    state: Snapshot,
    _lock: File,
}
impl Queue {
    pub fn open(config: &Config) -> Result<Self> {
        let path = if !config.queue_store_path.is_empty() {
            PathBuf::from(&config.queue_store_path)
        } else if !config.deduplication_store_path.is_empty() {
            PathBuf::from(&config.deduplication_store_path).with_file_name("queue.json")
        } else {
            PathBuf::from("queue.json")
        };
        ensure!(
            path != std::path::Path::new(&config.deduplication_store_path),
            "Queue and dedupe paths must differ"
        );
        ensure!(
            !matches!(
                path.file_name().and_then(|n| n.to_str()),
                Some("cursors.json" | "forwarder-cursors.json")
            ),
            "Queue path must not overwrite cursors"
        );
        if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
            std::fs::create_dir_all(parent)?;
        }
        let lock = OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(path.with_extension("lock"))?;
        lock.try_lock_exclusive()
            .context("Queue already has a writer")?;
        let state = match std::fs::read(&path) {
            Ok(data) => serde_json::from_slice(&data)
                .context("Invalid durable queue; restore from backup")?,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Snapshot::default(),
            Err(e) => return Err(e.into()),
        };
        Ok(Self {
            path,
            max_bytes: config.queue_max_bytes,
            write_failed: false,
            state,
            _lock: lock,
        })
    }
    /// Test hook: point writes at `path` and clear the write-failure latch.
    #[cfg(test)]
    pub(crate) fn set_path_for_test(&mut self, path: PathBuf) {
        self.path = path;
        self.write_failed = false;
    }
    #[cfg(test)]
    pub(crate) fn path_for_test(&self) -> PathBuf {
        self.path.clone()
    }
    fn save(&mut self, next: Snapshot) -> Result<()> {
        ensure!(
            !self.write_failed,
            "Queue write outcome uncertain; repair storage and restart before further writes"
        );
        let bytes = serde_json::to_vec(&next)?;
        if bytes.len() > self.max_bytes {
            tracing::error!(
                "Durable queue full; intake paused until storage is increased or failed entries are explicitly deleted"
            );
            anyhow::bail!("Durable queue full; intake paused");
        }
        if let Err(error) = atomic_write(&self.path, &bytes) {
            // A rename may have succeeded before directory fsync failed. Never
            // overwrite that possibly committed state using a stale snapshot.
            self.write_failed = true;
            tracing::error!("Durable queue write failed; accepted work retained and intake paused");
            return Err(error);
        }
        self.state = next;
        Ok(())
    }
    pub fn pending(&self, topic: &str) -> Option<Entry> {
        self.state.pending.get(topic).cloned()
    }
    pub fn cursor(&self, topic: &str) -> Option<&str> {
        self.state.cursors.get(topic).map(String::as_str)
    }
    #[cfg(test)]
    pub fn accept(&mut self, topic: &str, message: &Message) -> Result<()> {
        self.accept_to(topic, message, &[])
    }
    /// Accept with additional destinations, recorded atomically with the payload.
    pub fn accept_to(&mut self, topic: &str, message: &Message, extra: &[&str]) -> Result<()> {
        ensure!(
            !self.state.pending.contains_key(topic),
            "Topic already has pending delivery"
        );
        let mut next = self.state.clone();
        next.pending.insert(
            topic.into(),
            Entry {
                queue_id: format!("{:032x}", rand::random::<u128>()),
                topic: topic.into(),
                replay: false,
                first_attempt_at_ms: None,
                last_attempt_at_ms: None,
                rejected_at_ms: None,
                failure_reason: None,
                message: message.clone(),
                accepted_at: chrono::Utc::now().timestamp(),
                attempts: 0,
                next_attempt_at_ms: 0,
                destinations: std::iter::once("pushover")
                    .chain(extra.iter().copied())
                    .map(|d| (d.into(), "pending".into()))
                    .collect(),
            },
        );
        self.save(next)
    }
    pub fn attempt(&mut self, topic: &str) -> Result<u32> {
        let mut next = self.state.clone();
        let entry = next.pending.get_mut(topic).context("No pending delivery")?;
        let attempt = entry.attempts;
        entry.attempts = entry.attempts.saturating_add(1);
        let now = chrono::Utc::now().timestamp_millis();
        entry.first_attempt_at_ms.get_or_insert(now);
        entry.last_attempt_at_ms = Some(now);
        self.save(next)?;
        Ok(attempt)
    }
    pub fn defer(&mut self, topic: &str, delay: std::time::Duration) -> Result<()> {
        let mut next = self.state.clone();
        next.pending
            .get_mut(topic)
            .context("No pending delivery")?
            .next_attempt_at_ms = chrono::Utc::now()
            .timestamp_millis()
            .saturating_add(delay.as_millis() as i64);
        self.save(next)
    }
    pub fn destination(&self, topic: &str, name: &str) -> Option<&str> {
        self.state
            .pending
            .get(topic)?
            .destinations
            .get(name)
            .map(String::as_str)
    }
    pub fn mark(&mut self, topic: &str, name: &str, state: &str) -> Result<()> {
        let mut next = self.state.clone();
        next.pending
            .get_mut(topic)
            .context("No pending delivery")?
            .destinations
            .insert(name.into(), state.into());
        self.save(next)
    }
    pub fn failure(&mut self, topic: &str, reason: String) -> Result<()> {
        let mut next = self.state.clone();
        next.pending
            .get_mut(topic)
            .context("No pending delivery")?
            .failure_reason = Some(reason);
        self.save(next)
    }
    pub fn failed(&self) -> &[Entry] {
        &self.state.failed
    }
    pub fn replay(&mut self, id: &str, topics: &[String]) -> Result<()> {
        let mut next = self.state.clone();
        let index = next
            .failed
            .iter()
            .position(|e| e.queue_id == id)
            .context("Unknown failed queue ID")?;
        let mut entry = next.failed.remove(index);
        ensure!(
            topics.contains(&entry.topic),
            "Failed topic must be configured before replay"
        );
        ensure!(
            !next.pending.contains_key(&entry.topic),
            "Drain pending topic before replay"
        );
        ensure!(
            entry
                .destinations
                .get("pushover")
                .is_some_and(|s| s == "permanent"),
            "No failed Pushover destination to replay"
        );
        entry
            .destinations
            .insert("pushover".into(), "pending".into());
        entry.replay = true;
        entry.next_attempt_at_ms = 0;
        next.pending.insert(entry.topic.clone(), entry);
        self.save(next)
    }
    pub fn delete(&mut self, id: &str) -> Result<()> {
        let mut next = self.state.clone();
        let index = next
            .failed
            .iter()
            .position(|e| e.queue_id == id)
            .context("Unknown failed queue ID")?;
        next.failed.remove(index);
        self.save(next)
    }
    pub fn reject(&mut self, topic: &str, id: &str) -> Result<()> {
        let mut next = self.state.clone();
        if let Some(mut entry) = next.pending.remove(topic) {
            entry
                .destinations
                .insert("pushover".into(), "permanent".into());
            entry.rejected_at_ms = Some(chrono::Utc::now().timestamp_millis());
            if !entry.replay && !id.trim().is_empty() {
                next.cursors.insert(topic.into(), id.into());
            }
            next.failed.push(entry);
        }
        self.save(next)
    }
    pub fn acknowledge(&mut self, topic: &str, id: &str) -> Result<()> {
        let mut next = self.state.clone();
        if next.pending.remove(topic).is_some_and(|e| !e.replay) {
            next.cursors.insert(topic.into(), id.into());
        }
        self.save(next)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn config() -> Config {
        Config {
            queue_store_path: std::env::temp_dir()
                .join(format!("durable-{}.json", rand::random::<u64>()))
                .to_string_lossy()
                .into(),
            ..Config::default()
        }
    }
    #[test]
    fn restart_recovers_without_upstream_and_atomic_ack() {
        let cfg = config();
        let mut q = Queue::open(&cfg).unwrap();
        let m = Message {
            id: "expired-upstream-id".into(),
            ..Message::default()
        };
        q.accept("alerts", &m).unwrap();
        assert!(Queue::open(&cfg).is_err());
        drop(q);
        std::fs::write(
            PathBuf::from(&cfg.queue_store_path).with_file_name(format!(
                "{}.tmp",
                PathBuf::from(&cfg.queue_store_path)
                    .file_name()
                    .unwrap()
                    .to_string_lossy()
            )),
            b"torn",
        )
        .unwrap();
        let mut q = Queue::open(&cfg).unwrap();
        assert_eq!(q.pending("alerts").unwrap().message.id, m.id);
        q.acknowledge("alerts", &m.id).unwrap();
        drop(q);
        let q = Queue::open(&cfg).unwrap();
        assert!(q.pending("alerts").is_none());
        assert_eq!(q.cursor("alerts"), Some(m.id.as_str()));
    }
    #[test]
    fn retry_deadline_and_attempts_survive_restart() {
        let cfg = config();
        let mut q = Queue::open(&cfg).unwrap();
        q.accept("a", &Message::default()).unwrap();
        assert_eq!(q.attempt("a").unwrap(), 0);
        q.defer("a", std::time::Duration::from_secs(120)).unwrap();
        drop(q);
        let mut q = Queue::open(&cfg).unwrap();
        assert!(q.pending("a").unwrap().next_attempt_at_ms > chrono::Utc::now().timestamp_millis());
        assert_eq!(q.attempt("a").unwrap(), 1);
    }
    #[test]
    fn permanent_metadata_replay_and_explicit_retention() {
        let cfg = config();
        let mut q = Queue::open(&cfg).unwrap();
        let m = Message {
            id: "bad".into(),
            message: Some("private payload".into()),
            ..Message::default()
        };
        q.accept("a", &m).unwrap();
        q.attempt("a").unwrap();
        q.failure("a", "HTTP 400".into()).unwrap();
        q.reject("a", "bad").unwrap();
        let id = q.failed()[0].queue_id.clone();
        q.accept(
            "a",
            &Message {
                id: "later".into(),
                ..Message::default()
            },
        )
        .unwrap();
        assert!(q.replay(&id, &["a".into()]).is_err());
        q.acknowledge("a", "later").unwrap();
        drop(q);
        let mut q = Queue::open(&cfg).unwrap();
        let failed = &q.failed()[0];
        assert_eq!(failed.failure_reason.as_deref(), Some("HTTP 400"));
        assert!(failed.first_attempt_at_ms.is_some());
        assert!(failed.last_attempt_at_ms.is_some());
        assert!(failed.rejected_at_ms.is_some());
        assert_eq!(failed.message.message.as_deref(), Some("private payload"));
        // Future destinations' successful acknowledgements must survive replay.
        q.state.failed[0]
            .destinations
            .insert("other".into(), "delivered".into());
        q.replay(&id, &["a".into()]).unwrap();
        assert_eq!(q.pending("a").unwrap().destinations["other"], "delivered");
        q.acknowledge("a", "bad").unwrap();
        assert_eq!(q.cursor("a"), Some("later"));
        assert!(q.failed().is_empty());
        assert!(q.replay(&id, &["a".into()]).is_err());
        q.accept("a", &m).unwrap();
        q.reject("a", "bad").unwrap();
        let id = q.failed()[0].queue_id.clone();
        q.state.failed[0]
            .destinations
            .insert("pushover".into(), "delivered".into());
        assert!(q.replay(&id, &["a".into()]).is_err());
        q.max_bytes = 1;
        assert!(q.accept("b", &m).is_err());
        assert_eq!(q.failed().len(), 1);
        q.max_bytes = cfg.queue_max_bytes;
        q.delete(&id).unwrap();
        drop(q);
        assert!(Queue::open(&cfg).unwrap().failed().is_empty());
    }
    #[test]
    fn full_or_failed_storage_does_not_mutate_accepted_work() {
        let cfg = config();
        let mut q = Queue::open(&cfg).unwrap();
        q.accept("a", &Message::default()).unwrap();
        q.max_bytes = 1;
        assert!(q.accept("b", &Message::default()).is_err());
        assert!(q.pending("a").is_some());
        assert!(q.pending("b").is_none());
        assert!(q.acknowledge("a", "id").is_err());
        assert!(q.pending("a").is_some());
        q.max_bytes = usize::MAX;
        q.path = PathBuf::from(&cfg.queue_store_path).join("invalid-child");
        assert!(q.acknowledge("a", "id").is_err());
        assert!(q.pending("a").is_some());
        q.path = PathBuf::from(&cfg.queue_store_path);
        assert!(q.acknowledge("a", "id").is_err());
        drop(q);
        assert!(Queue::open(&cfg).unwrap().pending("a").is_some());
    }
    #[test]
    fn delete_removes_only_target_dead_letter() {
        let cfg = config();
        let mut q = Queue::open(&cfg).unwrap();
        for topic in ["a", "b", "c"] {
            q.accept(topic, &Message::default()).unwrap();
            q.reject(topic, "bad").unwrap();
        }
        let ids: Vec<String> = q.failed().iter().map(|e| e.queue_id.clone()).collect();
        assert_eq!(ids.len(), 3);
        q.delete(&ids[1]).unwrap();
        let left: Vec<String> = q.failed().iter().map(|e| e.queue_id.clone()).collect();
        assert_eq!(left, vec![ids[0].clone(), ids[2].clone()]);
        drop(q);
        let reopened: Vec<String> = Queue::open(&cfg)
            .unwrap()
            .failed()
            .iter()
            .map(|e| e.queue_id.clone())
            .collect();
        assert_eq!(reopened, vec![ids[0].clone(), ids[2].clone()]);
    }
}
