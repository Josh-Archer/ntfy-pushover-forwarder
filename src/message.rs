use serde::Deserialize;
use sha2::{Digest, Sha256};

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Eq)]
#[serde(default)]
pub struct Action {
    pub id: Option<String>,
    pub action: Option<String>,
    pub label: Option<String>,
    pub url: Option<String>,
    pub clear: Option<bool>,
}

impl Action {
    pub fn target_url(&self) -> Option<&str> {
        self.url.as_deref().map(str::trim).filter(|u| !u.is_empty())
    }

    pub fn button_label(&self) -> Option<&str> {
        self.label
            .as_deref()
            .map(str::trim)
            .filter(|l| !l.is_empty())
    }
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(default)]
pub struct Message {
    pub id: String,
    pub event: String,
    pub title: Option<String>,
    pub message: Option<String>,
    pub priority: Option<i32>,
    pub tags: Option<Vec<String>>,
    pub click: Option<String>,
    pub actions: Option<Vec<Action>>,
    pub attachment: Option<Attachment>,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Eq)]
#[serde(default)]
pub struct Attachment {
    pub url: Option<String>,
    #[serde(rename = "type")]
    pub media_type: Option<String>,
}
impl Message {
    pub fn title(&self, topic: &str) -> String {
        self.title
            .as_ref()
            .filter(|s| !s.is_empty())
            .cloned()
            .unwrap_or_else(|| format!("ntfy: {topic}"))
    }
    pub fn body(&self) -> &str {
        self.message
            .as_deref()
            .filter(|s| !s.is_empty())
            .unwrap_or("No message body")
    }
    pub fn tags(&self) -> &[String] {
        self.tags.as_deref().unwrap_or_default()
    }
    pub fn actions(&self) -> &[Action] {
        self.actions.as_deref().unwrap_or_default()
    }
    pub fn priority(&self) -> i32 {
        self.priority.unwrap_or(3).saturating_sub(3).clamp(-2, 2)
    }
    pub fn url(&self) -> Option<(&str, Option<&str>)> {
        for action in self.actions() {
            if let Some(url) = action.target_url() {
                return Some((url, action.button_label()));
            }
        }
        self.click
            .as_deref()
            .map(str::trim)
            .filter(|c| !c.is_empty())
            .map(|click| (click, None))
    }
    pub fn fingerprint(&self, topic: &str) -> String {
        if !self.id.trim().is_empty() {
            return format!("id:{}", self.id.trim());
        }
        let mut tags = self.tags().to_vec();
        tags.sort();
        let payload = [
            topic.to_string(),
            self.title(topic),
            self.body().into(),
            tags.join(","),
            self.priority.map(|p| p.to_string()).unwrap_or_default(),
            self.click.clone().unwrap_or_default(),
        ]
        .join("\u{1f}");
        format!("hash:{:X}", Sha256::digest(payload.as_bytes()))
    }
    pub fn formatted(&self) -> (String, bool) {
        let markdown = self
            .tags()
            .iter()
            .any(|t| t.eq_ignore_ascii_case("markdown") || t.eq_ignore_ascii_case("md"));
        let html = markdown || self.tags().iter().any(|t| t.eq_ignore_ascii_case("html"));
        if !markdown {
            return (self.body().into(), html);
        }
        let body = self
            .body()
            .split('`')
            .enumerate()
            .map(|(i, part)| {
                let escaped = part
                    .replace('&', "&amp;")
                    .replace('<', "&lt;")
                    .replace('>', "&gt;")
                    .replace('"', "&quot;")
                    .replace('\'', "&#39;");
                if i % 2 == 1 {
                    format!("<code>{escaped}</code>")
                } else {
                    escaped
                }
            })
            .collect::<String>();
        (body, html)
    }
}
