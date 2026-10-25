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
            state,
            _lock: lock,
        })
    }
    fn save(&mut self, next: Snapshot) -> Result<()> {
        let bytes = serde_json::to_vec(&next)?;
        ensure!(
            bytes.len() <= self.max_bytes,
            "Durable queue full; intake paused"
        );
        atomic_write(&self.path, &bytes)?;
        self.state = next;
        Ok(())
    }
    pub fn pending(&self, topic: &str) -> Option<Entry> {
        self.state.pending.get(topic).cloned()
    }
    pub fn cursor(&self, topic: &str) -> Option<&str> {
        self.state.cursors.get(topic).map(String::as_str)
    }
    pub fn accept(&mut self, topic: &str, message: &Message) -> Result<()> {
        ensure!(
            !self.state.pending.contains_key(topic),
            "Topic already has pending delivery"
        );
        let mut next = self.state.clone();
        next.pending.insert(
            topic.into(),
            Entry {
                message: message.clone(),
                accepted_at: chrono::Utc::now().timestamp(),
                attempts: 0,
                next_attempt_at_ms: 0,
                destinations: BTreeMap::from([("pushover".into(), "pending".into())]),
            },
        );
        self.save(next)
    }
    pub fn attempt(&mut self, topic: &str) -> Result<u32> {
        let mut next = self.state.clone();
        let entry = next.pending.get_mut(topic).context("No pending delivery")?;
        let attempt = entry.attempts;
        entry.attempts = entry.attempts.saturating_add(1);
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
    pub fn reject(&mut self, topic: &str, id: &str) -> Result<()> {
        let mut next = self.state.clone();
        if let Some(mut entry) = next.pending.remove(topic) {
            entry
                .destinations
                .insert("pushover".into(), "permanent".into());
            next.failed.push(entry);
        }
        next.cursors.insert(topic.into(), id.into());
        self.save(next)
    }
    pub fn acknowledge(&mut self, topic: &str, id: &str) -> Result<()> {
        let mut next = self.state.clone();
        next.pending.remove(topic);
        next.cursors.insert(topic.into(), id.into());
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
    }
}
