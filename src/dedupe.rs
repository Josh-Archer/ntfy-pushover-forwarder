use crate::config::Config;
use chrono::{DateTime, Utc};
use std::{
    collections::{HashMap, HashSet},
    path::Path,
};
use tokio::sync::Mutex;

#[derive(Default)]
pub struct Store {
    entries: HashMap<String, DateTime<Utc>>,
    cursors: HashMap<String, String>,
    in_flight: HashSet<String>,
}
#[derive(Debug, PartialEq)]
pub enum Claim {
    Acquired,
    Duplicate,
    InProgress,
}
impl Store {
    pub fn load(config: &Config) -> Self {
        let mut store = Self::default();
        if !config.deduplication_store_path.is_empty() {
            match std::fs::read(&config.deduplication_store_path) {
                Ok(data) => match serde_json::from_slice(&data) {
                    Ok(entries) => store.entries = entries,
                    Err(_) => tracing::warn!("Invalid dedupe file; starting empty"),
                },
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
                Err(_) => tracing::warn!("Cannot read dedupe file; starting empty"),
            }
            let cursor_path = cursor_path(config);
            match std::fs::read(&cursor_path) {
                Ok(data) => match serde_json::from_slice(&data) {
                    Ok(cursors) => store.cursors = cursors,
                    Err(_) => {
                        tracing::warn!(path = %cursor_path.display(), "Invalid cursor file; starting without saved cursors")
                    }
                },
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
                Err(_) => {
                    tracing::warn!(path = %cursor_path.display(), "Cannot read cursor file; starting without saved cursors")
                }
            }
        }
        store.prune(config, Utc::now());
        store
    }
    pub fn cursor(&self, topic: &str) -> Option<&str> {
        self.cursors.get(topic).map(String::as_str)
    }
    pub async fn set_cursor(store: &Mutex<Self>, topic: &str, cursor: &str, config: &Config) {
        let mut store = store.lock().await;
        store.cursors.insert(topic.into(), cursor.into());
        if config.deduplication_store_path.is_empty() {
            return;
        }
        let path = cursor_path(config);
        let result = async {
            if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
                tokio::fs::create_dir_all(parent).await?;
            }
            let temp = format!("{}.tmp", path.display());
            tokio::fs::write(&temp, serde_json::to_vec(&store.cursors)?).await?;
            tokio::fs::rename(temp, path).await?;
            anyhow::Ok(())
        }
        .await;
        if result.is_err() {
            tracing::warn!("Cannot persist cursor file");
        }
    }
    pub fn begin(&mut self, key: &str, config: &Config, now: DateTime<Utc>) -> Claim {
        if config.deduplication_window_seconds <= 0 {
            return Claim::Acquired;
        }
        if self.entries.get(key).is_some_and(|time| {
            (now - *time).num_milliseconds()
                <= config.deduplication_window_seconds.saturating_mul(1000)
        }) {
            return Claim::Duplicate;
        }
        if !self.in_flight.insert(key.into()) {
            return Claim::InProgress;
        }
        Claim::Acquired
    }
    fn prune(&mut self, config: &Config, now: DateTime<Utc>) {
        self.entries.retain(|_, time| {
            config.deduplication_window_seconds > 0
                && (now - *time).num_milliseconds()
                    <= config.deduplication_window_seconds.saturating_mul(1000)
        });
        while self.entries.len() > config.deduplication_max_entries {
            let key = self
                .entries
                .iter()
                .min_by_key(|(_, time)| *time)
                .unwrap()
                .0
                .clone();
            self.entries.remove(&key);
        }
    }
    pub async fn complete(store: &Mutex<Self>, key: &str, success: bool, config: &Config) {
        // Hold the async lock across persistence to serialize snapshots and atomic renames.
        let mut store = store.lock().await;
        store.in_flight.remove(key);
        if !success || config.deduplication_window_seconds <= 0 {
            return;
        }
        let now = Utc::now();
        store.entries.insert(key.into(), now);
        store.prune(config, now);
        if !config.deduplication_store_path.is_empty() {
            let result = async {
                let path = Path::new(&config.deduplication_store_path);
                if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
                    tokio::fs::create_dir_all(parent).await?;
                }
                let temp = format!("{}.tmp", config.deduplication_store_path);
                tokio::fs::write(&temp, serde_json::to_vec(&store.entries)?).await?;
                tokio::fs::rename(temp, path).await?;
                anyhow::Ok(())
            }
            .await;
            if result.is_err() {
                tracing::warn!("Cannot persist dedupe file");
            }
        }
    }
}
fn cursor_path(config: &Config) -> std::path::PathBuf {
    let dedupe = Path::new(&config.deduplication_store_path);
    dedupe.with_file_name("cursors.json")
}
