use crate::{config::Config, message::Message};
use anyhow::{Context, Result, bail, ensure};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::{
    collections::HashMap,
    fs::{self, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
};
use tokio::sync::{Mutex, Notify};

const FORMAT_VERSION: u32 = 1;
const DESTINATION_RESERVE_BYTES: usize = 64;
pub const PUSHOVER: &str = "pushover";

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum EntryState {
    Pending,
    Rejected,
    Acknowledged,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct QueueEntry {
    pub topic: String,
    pub id: String,
    pub payload: Value,
    pub destinations: HashMap<String, EntryState>,
}

impl QueueEntry {
    pub fn message(&self) -> Result<Message> {
        serde_json::from_value(self.payload.clone()).context("Decode queued notification")
    }
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
struct Snapshot {
    version: u32,
    /// Highest source event durably accepted, used to resume ingestion.
    ingested: HashMap<String, String>,
    /// Highest event durably completed, kept separate from ingestion progress.
    completed: HashMap<String, String>,
    entries: Vec<QueueEntry>,
}

pub struct Queue {
    path: PathBuf,
    max_bytes: usize,
    max_entries: usize,
    snapshot: Mutex<Snapshot>,
    changed: Notify,
}

impl Queue {
    pub fn load(config: &Config, legacy_cursors: HashMap<String, String>) -> Result<Self> {
        let path = PathBuf::from(&config.queue_store_path);
        let max_bytes = config.queue_max_bytes;
        let max_entries = config.queue_max_entries;
        let topics = config.topics.clone();
        let snapshot = load_snapshot(&path, max_bytes, max_entries, legacy_cursors)?;
        ensure!(
            snapshot.version == FORMAT_VERSION,
            "Unsupported queue format version"
        );
        for entry in &snapshot.entries {
            ensure!(
                topics.contains(&entry.topic),
                "Queue contains retained messages for an unconfigured topic"
            );
            ensure!(
                entry
                    .destinations
                    .keys()
                    .all(|destination| destination == PUSHOVER),
                "Queue contains an unsupported destination state"
            );
            ensure!(
                entry.destinations.contains_key(PUSHOVER),
                "Queue entry is missing its Pushover delivery state"
            );
            let message: Message = serde_json::from_value(entry.payload.clone())
                .context("Queue entry contains invalid message payload")?;
            ensure!(
                entry.id == message.id,
                "Queue entry ID does not match its payload"
            );
            ensure!(
                entry.destinations.get(PUSHOVER) != Some(&EntryState::Acknowledged),
                "Queue contains an incomplete acknowledgement for its only destination"
            );
        }
        Ok(Self {
            path: PathBuf::from(&config.queue_store_path),
            max_bytes,
            max_entries,
            snapshot: Mutex::new(snapshot),
            changed: Notify::new(),
        })
    }

    pub async fn ingest_cursor(&self, topic: &str) -> Option<String> {
        self.snapshot.lock().await.ingested.get(topic).cloned()
    }

    pub fn completed_cursor_startup(&self, topic: &str) -> Option<String> {
        self.snapshot.try_lock().ok()?.completed.get(topic).cloned()
    }

    pub fn completed_cursors_startup(&self) -> HashMap<String, String> {
        self.snapshot
            .try_lock()
            .map(|snapshot| snapshot.completed.clone())
            .unwrap_or_default()
    }

    pub async fn enqueue(&self, topic: &str, message: Message, payload: Value) -> Result<()> {
        loop {
            // Register before checking capacity so a completion cannot be missed.
            let changed = self.changed.notified();
            tokio::pin!(changed);
            changed.as_mut().enable();
            let mut current = self.snapshot.lock().await;
            if !message.id.is_empty()
                && current
                    .entries
                    .iter()
                    .any(|entry| entry.topic == topic && entry.id == message.id)
            {
                return Ok(());
            }
            let mut candidate = current.clone();
            candidate.entries.push(QueueEntry {
                topic: topic.into(),
                id: message.id.clone(),
                payload: payload.clone(),
                destinations: HashMap::from([(PUSHOVER.into(), EntryState::Pending)]),
            });
            if !message.id.is_empty() {
                candidate.ingested.insert(topic.into(), message.id.clone());
            }
            let encoded_size = serde_json::to_vec(&candidate)?.len();
            let reserved_size = encoded_size.saturating_add(
                candidate
                    .entries
                    .len()
                    .saturating_mul(DESTINATION_RESERVE_BYTES),
            );
            if current.entries.is_empty() && reserved_size > self.max_bytes {
                bail!("Message cannot fit within QueueMaxBytes");
            }
            if candidate.entries.len() > self.max_entries || reserved_size > self.max_bytes {
                drop(current);
                changed.await;
                continue;
            }
            persist_async(self.path.clone(), candidate.clone(), self.max_bytes).await?;
            *current = candidate;
            drop(current);
            self.changed.notify_waiters();
            return Ok(());
        }
    }

    pub async fn head_or_wait(&self, topic: &str) -> QueueEntry {
        loop {
            let changed = self.changed.notified();
            tokio::pin!(changed);
            changed.as_mut().enable();
            if let Some(entry) = self.head_now(topic).await {
                return entry;
            }
            changed.await;
        }
    }

    pub async fn head_now(&self, topic: &str) -> Option<QueueEntry> {
        let current = self.snapshot.lock().await;
        let entry = current.entries.iter().find(|entry| entry.topic == topic)?;
        (entry.destinations.get(PUSHOVER) == Some(&EntryState::Pending)).then(|| entry.clone())
    }

    pub async fn reject(&self, topic: &str, id: &str, destination: &str) -> Result<()> {
        let mut current = self.snapshot.lock().await;
        let mut candidate = current.clone();
        let entry = candidate
            .entries
            .iter_mut()
            .find(|entry| entry.topic == topic && entry.id == id)
            .context("Queue head disappeared before rejection")?;
        entry
            .destinations
            .insert(destination.into(), EntryState::Rejected);
        persist_async(self.path.clone(), candidate.clone(), self.max_bytes).await?;
        *current = candidate;
        drop(current);
        self.changed.notify_waiters();
        Ok(())
    }

    pub async fn complete_destination(
        &self,
        topic: &str,
        id: &str,
        destination: &str,
    ) -> Result<bool> {
        let mut current = self.snapshot.lock().await;
        let mut candidate = current.clone();
        let index = candidate
            .entries
            .iter()
            .position(|entry| entry.topic == topic)
            .context("Queue head disappeared before acknowledgement")?;
        ensure!(
            candidate.entries[index].id == id,
            "Cannot acknowledge out of order"
        );
        candidate.entries[index]
            .destinations
            .insert(destination.into(), EntryState::Acknowledged);
        let all_acknowledged = candidate.entries[index]
            .destinations
            .values()
            .all(|state| state == &EntryState::Acknowledged);
        if all_acknowledged {
            candidate.entries.remove(index);
            if !id.is_empty() {
                candidate.completed.insert(topic.into(), id.into());
            }
        }
        persist_async(self.path.clone(), candidate.clone(), self.max_bytes).await?;
        *current = candidate;
        drop(current);
        self.changed.notify_waiters();
        Ok(all_acknowledged)
    }
}

fn load_snapshot(
    path: &Path,
    max_bytes: usize,
    max_entries: usize,
    legacy: HashMap<String, String>,
) -> Result<Snapshot> {
    let parent = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    ensure!(
        parent.is_dir(),
        "QueueStorePath parent directory must already exist"
    );
    match fs::metadata(path) {
        Ok(metadata) => ensure!(
            metadata.len() as usize <= max_bytes,
            "Queue snapshot exceeds configured QueueMaxBytes"
        ),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            let initial = Snapshot {
                version: FORMAT_VERSION,
                ingested: legacy.clone(),
                completed: legacy,
                entries: Vec::new(),
            };
            persist(path, &initial, max_bytes)?;
            return Ok(initial);
        }
        Err(error) => return Err(error).context("Read queue metadata"),
    }
    let data = fs::read(path).context("Read queue snapshot")?;
    let snapshot: Snapshot = serde_json::from_slice(&data).context("Invalid queue snapshot")?;
    ensure!(
        snapshot.version == FORMAT_VERSION,
        "Unsupported queue format version"
    );
    ensure!(
        snapshot.entries.len() <= max_entries,
        "Queue exceeds QueueMaxEntries"
    );
    Ok(snapshot)
}

async fn persist_async(path: PathBuf, snapshot: Snapshot, max_bytes: usize) -> Result<()> {
    tokio::task::spawn_blocking(move || persist(&path, &snapshot, max_bytes))
        .await
        .context("Persist durable queue task")?
}

fn persist(path: &Path, snapshot: &Snapshot, max_bytes: usize) -> Result<()> {
    let data = serde_json::to_vec(snapshot)?;
    ensure!(
        data.len() <= max_bytes,
        "Queue snapshot exceeds QueueMaxBytes"
    );
    let parent = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    ensure!(
        parent.is_dir(),
        "QueueStorePath parent directory must already exist"
    );
    let temp = path.with_file_name(format!(
        "{}.tmp",
        path.file_name().unwrap_or_default().to_string_lossy()
    ));
    let mut file = OpenOptions::new()
        .create(true)
        .truncate(true)
        .write(true)
        .open(&temp)
        .context("Open queue temporary file")?;
    file.write_all(&data).context("Write queue snapshot")?;
    file.sync_all().context("Sync queue snapshot")?;
    drop(file);
    fs::rename(&temp, path).context("Replace queue snapshot")?;
    fs::File::open(parent)
        .and_then(|directory| directory.sync_all())
        .context("Sync queue directory")?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::Config;

    fn config(path: &Path) -> Config {
        Config {
            queue_enabled: true,
            queue_store_path: path.to_string_lossy().into_owned(),
            topics: vec!["alerts".into()],
            queue_max_bytes: 16_384,
            queue_max_entries: 3,
            ..Config::default()
        }
    }

    fn message(id: &str) -> Message {
        Message {
            id: id.into(),
            event: "message".into(),
            message: Some(id.into()),
            ..Message::default()
        }
    }

    fn payload(id: &str) -> Value {
        serde_json::json!({
            "id": id,
            "event": "message",
            "message": id,
            "extra": {"preserved": true}
        })
    }

    async fn enqueue(queue: &Queue, id: &str) -> Result<()> {
        queue
            .enqueue(
                "alerts",
                message(id),
                serde_json::json!({
                    "id": id,
                    "event": "message",
                    "message": id,
                    "extra": {"preserved": true}
                }),
            )
            .await
    }

    fn path(test: &str) -> PathBuf {
        std::env::temp_dir().join(format!("ntfy-queue-{test}-{}", std::process::id()))
    }

    #[tokio::test]
    async fn restart_recovers_payload_and_separate_ingested_and_completed_cursors() {
        let path = path("restart.json");
        let queue = Queue::load(&config(&path), HashMap::new()).unwrap();
        enqueue(&queue, "expired-upstream-id").await.unwrap();
        drop(queue);
        let queue = Queue::load(&config(&path), HashMap::new()).unwrap();
        assert_eq!(
            queue.ingest_cursor("alerts").await.as_deref(),
            Some("expired-upstream-id")
        );
        assert_eq!(queue.completed_cursor_startup("alerts"), None);
        assert_eq!(
            queue.head_now("alerts").await.unwrap().payload["extra"]["preserved"],
            true
        );
        assert_eq!(
            queue
                .head_now("alerts")
                .await
                .unwrap()
                .message()
                .unwrap()
                .message
                .as_deref(),
            Some("expired-upstream-id")
        );
        std::fs::remove_file(path).unwrap();
    }

    #[tokio::test]
    async fn permanent_failure_is_retained_and_blocks_topic_order() {
        let path = path("rejected.json");
        let queue = Queue::load(&config(&path), HashMap::new()).unwrap();
        enqueue(&queue, "one").await.unwrap();
        enqueue(&queue, "two").await.unwrap();
        queue.reject("alerts", "one", PUSHOVER).await.unwrap();
        assert!(queue.head_now("alerts").await.is_none());
        drop(queue);
        let queue = Queue::load(&config(&path), HashMap::new()).unwrap();
        assert!(queue.head_now("alerts").await.is_none());
        let data = std::fs::read_to_string(&path).unwrap();
        assert!(data.contains("\"id\":\"one\""));
        assert!(data.contains("\"pushover\":\"rejected\""));
        std::fs::remove_file(path).unwrap();
    }

    #[tokio::test]
    async fn completed_head_advances_cursor_atomically_and_keeps_next_head() {
        let path = path("complete.json");
        let queue = Queue::load(&config(&path), HashMap::new()).unwrap();
        enqueue(&queue, "one").await.unwrap();
        enqueue(&queue, "two").await.unwrap();
        queue
            .complete_destination("alerts", "one", PUSHOVER)
            .await
            .unwrap();
        assert_eq!(
            queue.completed_cursor_startup("alerts").as_deref(),
            Some("one")
        );
        assert_eq!(queue.head_now("alerts").await.unwrap().id, "two");
        std::fs::remove_file(path).unwrap();
    }

    #[tokio::test]
    async fn write_error_does_not_advance_ingestion_cursor() {
        let directory = path("write-error");
        std::fs::create_dir_all(&directory).unwrap();
        let path = directory.join("queue.json");
        let queue = Queue::load(&config(&path), HashMap::new()).unwrap();
        std::fs::remove_file(&path).unwrap();
        std::fs::remove_dir(&directory).unwrap();
        std::fs::write(&directory, "occupied").unwrap();
        assert!(
            queue
                .enqueue("alerts", message("not-persisted"), payload("not-persisted"))
                .await
                .is_err()
        );
        assert_eq!(queue.ingest_cursor("alerts").await, None);
        std::fs::remove_file(directory).unwrap();
    }

    #[tokio::test]
    async fn full_queue_waits_for_capacity_without_dropping_message() {
        let path = path("full.json");
        let mut cfg = config(&path);
        cfg.queue_max_entries = 1;
        let queue = std::sync::Arc::new(Queue::load(&cfg, HashMap::new()).unwrap());
        enqueue(&queue, "one").await.unwrap();
        let enqueue = {
            let queue = queue.clone();
            tokio::spawn(async move { enqueue(&queue, "two").await })
        };
        tokio::task::yield_now().await;
        assert!(!enqueue.is_finished());
        queue
            .complete_destination("alerts", "one", PUSHOVER)
            .await
            .unwrap();
        enqueue.await.unwrap().unwrap();
        assert_eq!(queue.head_now("alerts").await.unwrap().id, "two");
        std::fs::remove_file(path).unwrap();
    }

    #[tokio::test]
    async fn byte_limit_reserves_room_for_rejection_and_rejects_unfittable_messages() {
        let path = path("byte-limit.json");
        let mut cfg = config(&path);
        let queue = Queue::load(&cfg, HashMap::new()).unwrap();
        enqueue(&queue, "one").await.unwrap();
        let occupied = std::fs::metadata(&path).unwrap().len() as usize;
        drop(queue);
        std::fs::remove_file(&path).unwrap();
        cfg.queue_max_bytes = occupied + DESTINATION_RESERVE_BYTES;
        let queue = Queue::load(&cfg, HashMap::new()).unwrap();
        enqueue(&queue, "one").await.unwrap();
        queue.reject("alerts", "one", PUSHOVER).await.unwrap();
        assert!(std::fs::metadata(&path).unwrap().len() as usize <= cfg.queue_max_bytes);
        drop(queue);
        std::fs::remove_file(&path).unwrap();
        cfg.queue_max_bytes -= 1;
        let queue = Queue::load(&cfg, HashMap::new()).unwrap();
        let result =
            tokio::time::timeout(std::time::Duration::from_secs(1), enqueue(&queue, "one")).await;
        assert!(
            result
                .expect("unfittable message must not wait for capacity")
                .is_err()
        );
        assert_eq!(queue.ingest_cursor("alerts").await, None);
        std::fs::remove_file(&path).unwrap();
    }

    #[test]
    fn corrupt_or_oversized_snapshot_fails_closed() {
        let path = path("corrupt.json");
        std::fs::write(&path, "not json").unwrap();
        assert!(load_snapshot(&path, 32, 10, HashMap::new()).is_err());
        std::fs::write(&path, vec![b'x'; 33]).unwrap();
        assert!(load_snapshot(&path, 32, 10, HashMap::new()).is_err());
        std::fs::remove_file(path).unwrap();
    }

    #[test]
    fn startup_rejects_acknowledged_single_destination_and_mismatched_payload_id() {
        for (suffix, id, state) in [
            ("acknowledged", "one", EntryState::Acknowledged),
            ("mismatched", "different", EntryState::Pending),
        ] {
            let path = path(&format!("{suffix}.json"));
            let mut snapshot = Snapshot {
                version: FORMAT_VERSION,
                ..Snapshot::default()
            };
            snapshot.entries.push(QueueEntry {
                topic: "alerts".into(),
                id: id.into(),
                payload: payload("one"),
                destinations: HashMap::from([(PUSHOVER.into(), state)]),
            });
            persist(&path, &snapshot, 16_384).unwrap();
            assert!(Queue::load(&config(&path), HashMap::new()).is_err());
            std::fs::remove_file(path).unwrap();
        }
    }
}
