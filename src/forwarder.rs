use crate::{
    config::Config,
    dedupe::{Claim, Store},
    message::Message,
};
use anyhow::{Result, bail};
use bytes::Bytes;
use chrono::Utc;
use futures_util::StreamExt;
use reqwest::{
    Client, Url,
    multipart::{Form, Part},
};
use std::{
    collections::HashMap,
    sync::Arc,
    time::{Duration, SystemTime},
};
use tokio::sync::Mutex;
use tokio_util::{
    codec::{FramedRead, LinesCodec},
    io::StreamReader,
};

pub const METRICS: [&str; 7] = [
    "messages_received",
    "messages_forwarded",
    "messages_dropped_priority",
    "messages_dropped_dedupe",
    "forward_failures",
    "sse_reconnects",
    "recovery_alerts",
];
// ntfy messages are small JSON lines. Reject an unbounded line before allocating it all.
const MAX_LINE_BYTES: usize = 1024 * 1024;
const LOGO_CACHE_BYTES: usize = 8 * 1024 * 1024;
#[derive(Default)]
pub struct TopicState {
    pub connected: bool,
    pub cursor: Option<String>,
    pub counters: [u64; 7],
}
#[derive(Clone)]
struct Image {
    data: Bytes,
    mime: String,
    name: String,
}
pub struct Forwarder {
    pub config: Config,
    pub topics: Mutex<HashMap<String, TopicState>>,
    store: Mutex<Store>,
    icons: Mutex<HashMap<String, Image>>,
    stream_client: Client,
    post_client: Client,
    image_client: Client,
}
#[derive(Debug, PartialEq)]
enum Delivery {
    Success,
    Permanent,
    Transient,
}
struct PostResult {
    status: reqwest::StatusCode,
    retry_after: Option<Duration>,
}

impl Forwarder {
    pub fn new(config: Config) -> Result<Arc<Self>> {
        // No redirects on credentialed operations; attachment origins are checked explicitly.
        let base = || {
            Client::builder()
                .connect_timeout(Duration::from_secs(10))
                .redirect(reqwest::redirect::Policy::none())
        };
        let store = Store::load(&config);
        let topics = config
            .topics
            .iter()
            .map(|t| {
                let state = TopicState {
                    cursor: store.cursor(t).map(str::to_owned),
                    ..TopicState::default()
                };
                (t.clone(), state)
            })
            .collect();
        Ok(Arc::new(Self {
            store: Mutex::new(store),
            config,
            topics: Mutex::new(topics),
            icons: Mutex::new(HashMap::new()),
            stream_client: base().build()?,
            post_client: base().timeout(Duration::from_secs(100)).build()?,
            image_client: base().timeout(Duration::from_secs(10)).build()?,
        }))
    }
    async fn count(&self, topic: &str, index: usize) {
        self.topics.lock().await.get_mut(topic).unwrap().counters[index] += 1;
    }
    pub async fn listen(self: Arc<Self>, topic: String) {
        let mut attempt = 0u32;
        let mut outage = false;
        // A failed first message has no preceding cursor. Retain it for retry instead of
        // reconnecting live and silently losing it if ntfy doesn't replay without since=.
        let mut pending: Option<Message> = None;
        loop {
            if let Some(message) = pending.as_ref() {
                match self.deliver(&topic, message).await {
                    Delivery::Transient => {
                        self.count(&topic, 5).await;
                        tokio::time::sleep(backoff(
                            self.config.reconnect_initial_delay_milliseconds,
                            attempt,
                        ))
                        .await;
                        attempt = attempt.saturating_add(1);
                        continue;
                    }
                    Delivery::Success => self.advance_cursor(&topic, &message.id).await,
                    Delivery::Permanent => {}
                }
                pending = None;
            }
            let cursor = self.topics.lock().await[&topic].cursor.clone();
            let result = self
                .consume(
                    &topic,
                    cursor.as_deref(),
                    &mut outage,
                    &mut attempt,
                    &mut pending,
                )
                .await;
            self.topics.lock().await.get_mut(&topic).unwrap().connected = false;
            outage = true;
            self.count(&topic, 5).await;
            if result.is_err() {
                tracing::warn!(topic, "Topic stream interrupted");
            }
            tokio::time::sleep(backoff(
                self.config.reconnect_initial_delay_milliseconds,
                attempt,
            ))
            .await;
            attempt = attempt.saturating_add(1);
        }
    }
    pub(crate) fn stream_url(&self, topic: &str, cursor: Option<&str>) -> Result<Url> {
        let mut url = Url::parse(&format!(
            "{}/{}/json",
            self.config.ntfy_url.trim_end_matches('/'),
            topic
        ))?;
        // Resume from the saved cursor; without one, replay only the configured initial window.
        let since = cursor.or(Some(self.config.initial_since.as_str()).filter(|s| !s.is_empty()));
        if let Some(since) = since {
            url.query_pairs_mut().append_pair("since", since);
        }
        Ok(url)
    }
    async fn consume(
        &self,
        topic: &str,
        cursor: Option<&str>,
        outage: &mut bool,
        attempt: &mut u32,
        pending: &mut Option<Message>,
    ) -> Result<()> {
        let url = self.stream_url(topic, cursor)?;
        let mut request = self.stream_client.get(url);
        if !self.config.ntfy_token.is_empty() {
            request = request.bearer_auth(&self.config.ntfy_token);
        }
        // Stream reads have no overall timeout; ntfy keeps quiet connections alive.
        let response = tokio::time::timeout(Duration::from_secs(30), request.send())
            .await??
            .error_for_status()?;
        self.topics.lock().await.get_mut(topic).unwrap().connected = true;
        if *outage && self.config.send_recovery_alerts {
            self.recovery(topic).await;
        }
        *outage = false;
        let stream = response
            .bytes_stream()
            .map(|r| r.map_err(std::io::Error::other));
        let mut lines = FramedRead::new(
            StreamReader::new(stream),
            LinesCodec::new_with_max_length(MAX_LINE_BYTES),
        );
        while let Some(line) = lines.next().await {
            let line = line?;
            // Reset only after stream activity, preventing tight reconnects on immediate EOF.
            *attempt = 0;
            if line.trim().is_empty() {
                continue;
            }
            let message: Message = match serde_json::from_str(&line) {
                Ok(m) => m,
                Err(_) => {
                    tracing::warn!(topic, "Invalid ntfy JSON line");
                    continue;
                }
            };
            if message.event != "message" {
                continue;
            }
            match self.deliver(topic, &message).await {
                Delivery::Success => self.advance_cursor(topic, &message.id).await,
                Delivery::Permanent => {}
                Delivery::Transient => {
                    *pending = Some(message);
                    bail!("Delivery failed");
                }
            }
        }
        Ok(())
    }
    async fn deliver(&self, topic: &str, message: &Message) -> Delivery {
        self.count(topic, 0).await;
        if message.id.trim().is_empty() {
            self.count(topic, 4).await;
            return Delivery::Permanent;
        }
        if message.priority.unwrap_or(3) < self.config.minimum_priority {
            self.count(topic, 2).await;
            return Delivery::Success;
        }
        let key = message.fingerprint(topic);
        match self
            .store
            .lock()
            .await
            .begin(&key, &self.config, Utc::now())
        {
            Claim::Duplicate => {
                self.count(topic, 3).await;
                return Delivery::Success;
            }
            Claim::InProgress => return Delivery::Transient,
            Claim::Acquired => {}
        }
        let image = self.image(topic, message).await;
        let mut result = Delivery::Transient;
        for attempt in 0..self.config.max_retries {
            let form = self.form(topic, message, image.as_ref());
            let mut retry_after = None;
            match self.post(form).await {
                Ok(response) if response.status.is_success() => {
                    result = Delivery::Success;
                    break;
                }
                Ok(response)
                    if response.status.is_client_error() && response.status.as_u16() != 429 =>
                {
                    result = Delivery::Permanent;
                    break;
                }
                Ok(response) => retry_after = response.retry_after,
                Err(_) => {}
            }
            // Honor the final response too: pending-message replay must not bypass
            // the server cooldown when this delivery batch exhausts its attempts.
            if attempt + 1 < self.config.max_retries || retry_after.is_some() {
                tokio::time::sleep(delivery_backoff(
                    self.config.retry_delay_milliseconds,
                    attempt,
                    retry_after,
                ))
                .await;
            }
        }
        Store::complete(&self.store, &key, result == Delivery::Success, &self.config).await;
        self.count(topic, if result == Delivery::Success { 1 } else { 4 })
            .await;
        result
    }
    async fn advance_cursor(&self, topic: &str, cursor: &str) {
        Store::set_cursor(&self.store, topic, cursor, &self.config).await;
        self.topics.lock().await.get_mut(topic).unwrap().cursor = Some(cursor.into());
    }
    fn form(&self, topic: &str, message: &Message, image: Option<&Image>) -> Form {
        let (body, html) = message.formatted();
        let sound = message
            .tags()
            .iter()
            .find_map(|t| self.config.sound_map.get(t))
            .map(String::as_str)
            .unwrap_or("pushover");
        let mut form = Form::new()
            .text("token", self.config.token(topic).to_owned())
            .text("user", self.config.pushover_user_key.clone())
            .text("title", message.title(topic))
            .text("message", body)
            .text("priority", message.priority().to_string())
            .text("sound", sound.to_owned());
        if html {
            form = form.text("html", "1");
        }
        if let Some(device) = message
            .tags()
            .iter()
            .find_map(|t| t.strip_prefix("device:"))
            .filter(|d| !d.is_empty())
        {
            form = form.text("device", device.to_owned());
        }
        if let Some((url, url_title)) = message.url() {
            form = form.text("url", url.to_owned());
            if let Some(title) = url_title {
                form = form.text("url_title", title.to_owned());
            }
        }
        if message.priority() == 2 {
            form = form.text("retry", "60").text("expire", "3600");
        }
        if let Some(image) = image
            && let Ok(part) = Part::stream_with_length(
                reqwest::Body::from(image.data.clone()),
                image.data.len() as u64,
            )
            .file_name(image.name.clone())
            .mime_str(&image.mime)
        {
            form = form.part("attachment", part);
        }
        form
    }
    async fn post(&self, form: Form) -> reqwest::Result<PostResult> {
        let mut response = self
            .post_client
            .post(&self.config.pushover_url)
            .multipart(form)
            .send()
            .await?;
        let status = response.status();
        let retry_after = response
            .headers()
            .get(reqwest::header::RETRY_AFTER)
            .and_then(|value| value.to_str().ok())
            .and_then(|value| parse_retry_after(value, SystemTime::now()));
        // Drain small API responses to reuse pooled connections, without buffering or
        // logging response bodies that may contain account details.
        let mut read = 0usize;
        while let Ok(Some(chunk)) = response.chunk().await {
            read += chunk.len();
            if read > 65_536 {
                break;
            }
        }
        Ok(PostResult {
            status,
            retry_after,
        })
    }
    async fn recovery(&self, topic: &str) {
        let form = Form::new()
            .text("token", self.config.token(topic).to_owned())
            .text("user", self.config.pushover_user_key.clone())
            .text("title", format!("ntfy recovered: {topic}"))
            .text(
                "message",
                format!("SSE connection to topic '{topic}' restored after outage."),
            )
            .text("priority", "0")
            .text("sound", "magic");
        if self
            .post(form)
            .await
            .is_ok_and(|response| response.status.is_success())
        {
            self.count(topic, 6).await;
        }
    }
    async fn image(&self, topic: &str, message: &Message) -> Option<Image> {
        let attachment = message
            .attachment
            .as_ref()
            .and_then(|a| a.url.as_ref())
            .filter(|s| !s.is_empty());
        let logo = attachment.is_none();
        let address = if let Some(address) = attachment {
            if Url::parse(address).is_ok() {
                address.clone()
            } else {
                format!(
                    "{}/{}",
                    self.config.ntfy_url.trim_end_matches('/'),
                    address.trim_start_matches('/')
                )
            }
        } else {
            message
                .tags()
                .iter()
                .find_map(|t| self.config.logo_map.get(t))
                .or_else(|| {
                    if topic.to_ascii_lowercase().contains("media") {
                        self.config.logo_map.get("mag")
                    } else {
                        None
                    }
                })?
                .clone()
        };
        if logo && let Some(image) = self.icons.lock().await.get(&address) {
            return Some(image.clone());
        }
        let result = self.download(&address, logo, message).await;
        match result {
            Ok(image) => {
                if logo {
                    let mut cache = self.icons.lock().await;
                    let size: usize = cache.values().map(|i| i.data.len()).sum();
                    if size + image.data.len() <= LOGO_CACHE_BYTES {
                        cache.insert(address, image.clone());
                    }
                }
                Some(image)
            }
            Err(_) => {
                tracing::warn!(
                    topic,
                    "Skipping unavailable, unsupported or oversized image"
                );
                None
            }
        }
    }
    async fn download(&self, address: &str, logo: bool, message: &Message) -> Result<Image> {
        let target = Url::parse(address)?;
        let mut request = self.image_client.get(target.clone());
        if !logo
            && !self.config.ntfy_token.is_empty()
            && same_origin(&target, &Url::parse(&self.config.ntfy_url)?)
        {
            request = request.bearer_auth(&self.config.ntfy_token);
        }
        let mut response = request.send().await?.error_for_status()?;
        let mime = response
            .headers()
            .get(reqwest::header::CONTENT_TYPE)
            .and_then(|v| v.to_str().ok())
            .map(|s| s.split(';').next().unwrap().trim().to_string())
            .or_else(|| {
                if logo {
                    Some("image/png".into())
                } else {
                    message
                        .attachment
                        .as_ref()
                        .and_then(|a| a.media_type.clone())
                }
            })
            .unwrap_or_default();
        if !mime.to_ascii_lowercase().starts_with("image/") {
            bail!("Unsupported content type");
        }
        let max = self.config.max_attachment_bytes as usize;
        if response.content_length().is_some_and(|n| n > max as u64) {
            bail!("Image too large");
        }
        let mut data = Vec::new();
        while let Some(chunk) = response.chunk().await? {
            if chunk.len() > max.saturating_sub(data.len()) {
                bail!("Image too large");
            }
            data.extend_from_slice(&chunk);
        }
        if data.is_empty() {
            bail!("Empty image");
        }
        let name = if logo {
            "logo.png"
        } else if mime.to_ascii_lowercase().contains("png") {
            "attachment.png"
        } else {
            "attachment.jpg"
        };
        Ok(Image {
            data: data.into(),
            mime,
            name: name.into(),
        })
    }
}
fn same_origin(a: &Url, b: &Url) -> bool {
    a.scheme() == b.scheme()
        && a.host_str() == b.host_str()
        && a.port_or_known_default() == b.port_or_known_default()
}
fn backoff(initial_ms: u64, attempt: u32) -> Duration {
    let base = initial_ms
        .saturating_mul(1u64 << attempt.min(16))
        .min(60_000);
    Duration::from_millis(base + rand::random_range(0..=base / 5))
}

// Bound untrusted server delays to one day; ordinary backoff tops out at 72s.
const MAX_RETRY_AFTER: Duration = Duration::from_secs(24 * 60 * 60);
fn parse_retry_after(value: &str, now: SystemTime) -> Option<Duration> {
    let value = value.trim();
    let delay = if !value.is_empty() && value.bytes().all(|b| b.is_ascii_digit()) {
        Duration::from_secs(value.parse().ok()?)
    } else {
        httpdate::parse_http_date(value)
            .ok()?
            .duration_since(now)
            .unwrap_or_default()
    };
    Some(delay.min(MAX_RETRY_AFTER))
}
fn delivery_backoff(initial_ms: u64, attempt: usize, retry_after: Option<Duration>) -> Duration {
    let initial_ms = if initial_ms == 0 { 1000 } else { initial_ms };
    backoff(initial_ms, attempt.min(16) as u32).max(retry_after.unwrap_or_default())
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio_util::codec::Decoder;
    #[test]
    fn backoff_is_bounded_and_jittered() {
        for attempt in 0..100 {
            let base = 1000u64.saturating_mul(1 << attempt.min(16)).min(60_000);
            let delay = backoff(1000, attempt).as_millis();
            assert!((base as u128..=base as u128 * 6 / 5).contains(&delay));
        }
    }
    #[test]
    fn retry_after_accepts_seconds_and_all_http_date_formats() {
        let now = SystemTime::UNIX_EPOCH + Duration::from_secs(784111777);
        assert_eq!(
            parse_retry_after(" 120 ", now),
            Some(Duration::from_secs(120))
        );
        for date in [
            "Sun, 06 Nov 1994 08:49:37 GMT",
            "Sunday, 06-Nov-94 08:49:37 GMT",
            "Sun Nov  6 08:49:37 1994",
        ] {
            assert_eq!(
                parse_retry_after(date, now - Duration::from_secs(10)),
                Some(Duration::from_secs(10))
            );
            assert_eq!(
                parse_retry_after(date, now + Duration::from_secs(10)),
                Some(Duration::ZERO)
            );
        }
        for bad in ["", "-1", "+1", "1.5", "tomorrow", "18446744073709551616"] {
            assert_eq!(parse_retry_after(bad, now), None);
        }
        assert_eq!(
            parse_retry_after("18446744073709551615", now),
            Some(MAX_RETRY_AFTER)
        );
    }
    #[test]
    fn delivery_backoff_has_a_safe_default_and_respects_cooldowns() {
        for attempt in [0, 1, 10, usize::MAX] {
            let delay = delivery_backoff(0, attempt, None);
            assert!(delay >= Duration::from_secs(1));
            assert!(delay <= Duration::from_secs(72));
            assert_eq!(
                delivery_backoff(10, attempt, Some(MAX_RETRY_AFTER)),
                MAX_RETRY_AFTER
            );
        }
        assert!(delivery_backoff(u64::MAX, usize::MAX, None) <= Duration::from_secs(72));
        let delay = delivery_backoff(10, 2, None);
        assert!((Duration::from_millis(40)..=Duration::from_millis(48)).contains(&delay));
    }
    #[test]
    fn streams_reject_unbounded_lines() {
        let mut codec = LinesCodec::new_with_max_length(MAX_LINE_BYTES);
        let mut data = bytes::BytesMut::from(vec![b'x'; MAX_LINE_BYTES + 1].as_slice());
        assert!(codec.decode(&mut data).is_err());
    }
    #[test]
    fn auth_origin_requires_scheme_host_and_port_match() {
        let base = Url::parse("https://ntfy.example.com").unwrap();
        for (address, expected) in [
            ("https://ntfy.example.com:443/file", true),
            ("https://ntfy.example.com.evil/file", false),
            ("http://ntfy.example.com/file", false),
            ("https://ntfy.example.com:8443/file", false),
            ("https://other.example.com/file", false),
        ] {
            assert_eq!(same_origin(&Url::parse(address).unwrap(), &base), expected);
        }
    }
}
