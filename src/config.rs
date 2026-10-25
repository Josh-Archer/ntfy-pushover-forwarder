use anyhow::{Context, Result, ensure};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::{collections::HashMap, path::Path};

#[derive(Clone, Deserialize, Serialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Config {
    pub ntfy_url: String,
    pub ntfy_token: String,
    pub pushover_url: String,
    pub pushover_user_key: String,
    pub pushover_default_token: String,
    pub minimum_priority: i32,
    pub deduplication_window_seconds: i64,
    pub deduplication_max_entries: usize,
    pub deduplication_store_path: String,
    pub queue_store_path: String,
    pub queue_max_bytes: usize,
    pub send_recovery_alerts: bool,
    /// ntfy `since` value used for a topic with no saved cursor (empty = live only).
    pub initial_since: String,
    pub http_port: u16,
    pub topics: Vec<String>,
    pub topic_tokens: HashMap<String, String>,
    pub sound_map: HashMap<String, String>,
    pub logo_map: HashMap<String, String>,
    pub max_attachment_bytes: i64,
    pub max_retries: usize,
    pub retry_delay_milliseconds: u64,
    pub reconnect_initial_delay_milliseconds: u64,
}
impl Default for Config {
    fn default() -> Self {
        Self {
            ntfy_url: String::new(),
            ntfy_token: String::new(),
            pushover_url: "https://api.pushover.net/1/messages.json".into(),
            pushover_user_key: String::new(),
            pushover_default_token: String::new(),
            minimum_priority: 1,
            deduplication_window_seconds: 300,
            deduplication_max_entries: 1024,
            deduplication_store_path: String::new(),
            queue_store_path: String::new(),
            queue_max_bytes: 64 * 1024 * 1024,
            send_recovery_alerts: true,
            initial_since: "10m".into(),
            http_port: 8080,
            topics: vec![],
            topic_tokens: HashMap::new(),
            sound_map: HashMap::new(),
            logo_map: HashMap::new(),
            max_attachment_bytes: 2_621_440,
            max_retries: 3,
            retry_delay_milliseconds: 0,
            reconnect_initial_delay_milliseconds: 1000,
        }
    }
}
impl Config {
    pub fn load(path: &Path, env: impl Iterator<Item = (String, String)>) -> Result<Self> {
        let mut value = serde_json::to_value(Self::default())?;
        match std::fs::read(path) {
            Ok(data) => {
                let root: Value =
                    serde_json::from_slice(&data).context("Invalid appsettings.json")?;
                if let Some(section) = root.as_object().and_then(|o| {
                    o.iter()
                        .find(|(k, _)| k.eq_ignore_ascii_case("Forwarder"))
                        .map(|(_, v)| v)
                }) {
                    merge(&mut value, section);
                }
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(e).context("Read appsettings.json"),
        }
        // Apply indexed .NET-style arrays and map entries without parsing string secrets as JSON.
        for (key, val) in env {
            let parts: Vec<_> = key.split("__").collect();
            if parts.len() < 2 || !parts[0].eq_ignore_ascii_case("Forwarder") {
                continue;
            }
            let Some(name) = value
                .as_object()
                .unwrap()
                .keys()
                .find(|k| k.eq_ignore_ascii_case(parts[1]))
                .cloned()
            else {
                continue;
            };
            let slot = &mut value[&name];
            if parts.len() == 2 {
                *slot = if slot.is_string() {
                    Value::String(val)
                } else if slot.is_boolean() {
                    Value::Bool(
                        val.to_ascii_lowercase()
                            .parse()
                            .with_context(|| format!("Invalid {name}"))?,
                    )
                } else if slot.is_number() {
                    Value::Number(
                        val.parse::<i64>()
                            .with_context(|| format!("Invalid {name}"))?
                            .into(),
                    )
                } else {
                    serde_json::from_str(&val).with_context(|| format!("Invalid {name}"))?
                };
            } else if let Some(array) = slot.as_array_mut() {
                let index: usize = parts[2].parse().context("Invalid Topics index")?;
                ensure!(index < 1024, "Topics index exceeds 1023");
                array.resize(array.len().max(index + 1), Value::String(String::new()));
                array[index] = Value::String(val);
            } else if let Some(map) = slot.as_object_mut() {
                map.insert(parts[2].into(), Value::String(val));
            }
        }
        let mut config: Self =
            serde_json::from_value(value).context("Invalid Forwarder configuration")?;
        let mut seen = std::collections::HashSet::new();
        config
            .topics
            .retain(|t| !t.trim().is_empty() && seen.insert(t.clone()));
        ensure!(!config.topics.is_empty(), "At least one topic is required");
        validate_url(&config.ntfy_url).context("Invalid NtfyUrl")?;
        validate_url(&config.pushover_url).context("Invalid PushoverUrl")?;
        ensure!(
            config.topics.iter().all(|t| !t.contains(['/', '?', '#'])),
            "Topic names cannot contain /, ? or #"
        );
        ensure!(
            config.deduplication_store_path.is_empty()
                || Path::new(&config.deduplication_store_path).file_name()
                    != Some(std::ffi::OsStr::new("cursors.json")),
            "DeduplicationStorePath cannot be named cursors.json"
        );
        ensure!(
            !config.pushover_user_key.is_empty(),
            "PushoverUserKey is required"
        );
        ensure!(
            config.topics.iter().all(|t| !config.token(t).is_empty()),
            "A Pushover token is required for each topic"
        );
        config.deduplication_max_entries = config.deduplication_max_entries.max(1);
        config.max_retries = config.max_retries.max(1);
        if config.max_attachment_bytes < 0 {
            config.max_attachment_bytes = 2_621_440;
        }
        if config.reconnect_initial_delay_milliseconds == 0 {
            config.reconnect_initial_delay_milliseconds = 1000;
        }
        Ok(config)
    }
    pub fn token(&self, topic: &str) -> &str {
        self.topic_tokens
            .get(topic)
            .filter(|s| !s.is_empty())
            .unwrap_or(&self.pushover_default_token)
    }
}
fn validate_url(s: &str) -> Result<()> {
    let u = reqwest::Url::parse(s)?;
    ensure!(
        matches!(u.scheme(), "http" | "https")
            && u.host_str().is_some()
            && u.query().is_none()
            && u.fragment().is_none(),
        "Expected HTTP(S) base URL"
    );
    Ok(())
}
fn merge(target: &mut Value, source: &Value) {
    if let (Some(t), Some(s)) = (target.as_object_mut(), source.as_object()) {
        for (key, value) in s {
            let name = t
                .keys()
                .find(|k| k.eq_ignore_ascii_case(key))
                .cloned()
                .unwrap_or_else(|| key.clone());
            t.insert(name, value.clone());
        }
    }
}
