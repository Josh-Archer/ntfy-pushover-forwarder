use super::*;
use crate::{
    config::Config,
    dedupe::{Claim, Store},
    message::{Action, Message, truncate_chars},
};
use chrono::{Duration, Utc};
use std::path::Path;

fn config() -> Config {
    Config {
        ntfy_url: "http://localhost:9999".into(),
        pushover_user_key: "user".into(),
        pushover_default_token: "app".into(),
        topics: vec!["alerts".into()],
        queue_store_path: std::env::temp_dir()
            .join(format!(
                "ntfy-queue-test-{}-{}.json",
                std::process::id(),
                rand::random::<u64>()
            ))
            .to_string_lossy()
            .into(),
        ..Config::default()
    }
}
#[test]
fn environment_overrides_json_and_preserves_map_keys_and_string_secrets() {
    let dir = std::env::temp_dir().join(format!("ntfy-config-{}", std::process::id()));
    std::fs::write(&dir, r#"{"Forwarder":{"NtfyUrl":"http://localhost","Topics":["old","keep"],"SoundMap":{"tv":"bike"}}}"#).unwrap();
    let env = [
        ("forwarder__pushoveruserkey", "123"),
        ("Forwarder__PushoverDefaultToken", "true"),
        ("Forwarder__Topics__0", "new"),
        ("Forwarder__TopicTokens__media-updates", "special"),
        ("Forwarder__SendRecoveryAlerts", "false"),
    ];
    let cfg = Config::load(&dir, env.into_iter().map(|(k, v)| (k.into(), v.into()))).unwrap();
    std::fs::remove_file(dir).unwrap();
    assert_eq!(cfg.pushover_user_key, "123");
    assert_eq!(cfg.pushover_default_token, "true");
    assert_eq!(cfg.topics, ["new", "keep"]);
    assert_eq!(cfg.token("media-updates"), "special");
    assert_eq!(cfg.sound_map["tv"], "bike");
    assert!(!cfg.send_recovery_alerts);
}
#[test]
fn config_rejects_missing_credentials_and_invalid_settings() {
    assert!(Config::load(Path::new("/nonexistent"), std::iter::empty()).is_err());
    let env = [
        ("Forwarder__NtfyUrl", "file:///tmp"),
        ("Forwarder__Topics__999999", "bad"),
    ];
    assert!(
        Config::load(
            Path::new("/nonexistent"),
            env.into_iter().map(|(k, v)| (k.into(), v.into()))
        )
        .is_err()
    );
}
#[test]
fn config_rejects_dedupe_file_named_cursors_json() {
    let path = std::env::temp_dir()
        .join(format!("ntfy-config-collision-{}", std::process::id()))
        .join("cursors.json");
    let env = [
        ("Forwarder__NtfyUrl", "http://localhost"),
        ("Forwarder__PushoverUserKey", "user"),
        ("Forwarder__PushoverDefaultToken", "app"),
        ("Forwarder__Topics", "[\"alerts\"]"),
        ("Forwarder__DeduplicationStorePath", path.to_str().unwrap()),
    ];
    assert!(
        Config::load(
            Path::new("/nonexistent"),
            env.into_iter()
                .map(|(key, value)| (key.into(), value.into()))
        )
        .is_err()
    );
}
#[test]
fn priority_defaults_and_full_mapping() {
    for (priority, expected) in [
        (None, 0),
        (Some(1), -2),
        (Some(2), -1),
        (Some(3), 0),
        (Some(4), 1),
        (Some(5), 2),
        (Some(10), 2),
    ] {
        assert_eq!(
            Message {
                priority,
                ..Message::default()
            }
            .priority(),
            expected
        );
    }
}
#[test]
fn formatting_escapes_markdown_but_preserves_explicit_html() {
    let mut msg: Message =
        serde_json::from_str(r#"{"message":"<b>& `x<y`", "tags":["MD"]}"#).unwrap();
    assert_eq!(
        msg.formatted(),
        ("&lt;b&gt;&amp; <code>x&lt;y</code>".into(), true)
    );
    msg.tags = Some(vec!["HTML".into()]);
    assert_eq!(msg.formatted(), ("<b>& `x<y`".into(), true));
    msg.tags = None;
    assert!(!msg.formatted().1);
}
#[test]
fn fingerprint_preserves_dotnet_format() {
    let mut msg: Message =
        serde_json::from_str(r#"{"id":" same ","message":"body","tags":["b","a"]}"#).unwrap();
    assert_eq!(msg.fingerprint("t"), "id:same");
    msg.id.clear();
    let first = msg.fingerprint("t");
    msg.tags.as_mut().unwrap().reverse();
    assert_eq!(first, msg.fingerprint("t"));
    assert_eq!(
        first,
        "hash:FBAC77D690B87F12F0A2D094528EC91DC66F42A1F651E6B2EB64AA9C70CEBD14"
    );
}
#[test]
fn action_and_url_mapping_precedence_and_fallbacks() {
    let json = r#"{
        "id": "1",
        "actions": [
            {"id": "act1", "action": "broadcast", "label": "No URL"},
            {"id": "act2", "action": "view", "label": " Open Web ", "url": " https://example.com/web ", "clear": true, "extra": "ignored"}
        ],
        "click": "https://example.com/click"
    }"#;
    let msg: Message = serde_json::from_str(json).unwrap();
    assert_eq!(msg.actions().len(), 2);
    assert_eq!(msg.actions()[0].target_url(), None);
    assert_eq!(msg.actions()[0].button_label(), Some("No URL"));
    assert_eq!(
        msg.actions()[1].target_url(),
        Some("https://example.com/web")
    );
    assert_eq!(msg.actions()[1].button_label(), Some("Open Web"));
    assert_eq!(msg.actions()[1].clear, Some(true));
    assert_eq!(
        msg.url(),
        Some(("https://example.com/web", Some("Open Web")))
    );

    let explicit_action = Action {
        id: Some("id1".into()),
        action: Some("view".into()),
        label: Some("Label".into()),
        url: Some("https://example.com/custom".into()),
        clear: Some(true),
    };
    assert_eq!(
        explicit_action.target_url(),
        Some("https://example.com/custom")
    );
    assert_eq!(explicit_action.button_label(), Some("Label"));

    let msg: Message =
        serde_json::from_str(r#"{"actions":[{"action":"view","url":"https://example.com"}]}"#)
            .unwrap();
    assert_eq!(msg.url(), Some(("https://example.com", None)));

    let msg: Message = serde_json::from_str(
        r#"{"actions":[{"action":"view","url":"https://example.com","label":"   "}]}"#,
    )
    .unwrap();
    assert_eq!(msg.url(), Some(("https://example.com", None)));

    let msg: Message = serde_json::from_str(
        r#"{"actions":[{"action":"view","url":"   ","label":"btn"}],"click":"https://example.com/click"}"#,
    )
    .unwrap();
    assert_eq!(msg.url(), Some(("https://example.com/click", None)));

    let msg: Message = serde_json::from_str(
        r#"{"actions":[{"action":"broadcast"}],"click":" https://example.com/c "}"#,
    )
    .unwrap();
    assert_eq!(msg.url(), Some(("https://example.com/c", None)));

    let msg: Message = serde_json::from_str(r#"{"click":"   "}"#).unwrap();
    assert_eq!(msg.url(), None);

    let msg = Message::default();
    assert_eq!(msg.url(), None);
}

#[test]
fn action_url_only_takes_view_or_http_ignoring_broadcast_and_others() {
    // Missing action type with URL is ignored
    let msg: Message =
        serde_json::from_str(r#"{"actions":[{"url":"https://example.com"}]}"#).unwrap();
    assert_eq!(msg.url(), None);

    // Broadcast action with URL is ignored
    let msg: Message = serde_json::from_str(
        r#"{"actions":[{"action":"broadcast","url":"https://example.com/broadcast","label":"Broadcast"}]}"#,
    )
    .unwrap();
    assert_eq!(msg.url(), None);

    // Unknown action type with URL is ignored
    let msg: Message = serde_json::from_str(
        r#"{"actions":[{"action":"custom_action","url":"https://example.com/custom","label":"Custom"}]}"#,
    )
    .unwrap();
    assert_eq!(msg.url(), None);

    // Broadcast with URL does not prevent fallback to click
    let msg: Message = serde_json::from_str(
        r#"{
            "actions":[{"action":"broadcast","url":"https://example.com/broadcast","label":"Broadcast"}],
            "click":"https://example.com/click"
        }"#,
    )
    .unwrap();
    assert_eq!(msg.url(), Some(("https://example.com/click", None)));

    // Http action is supported
    let msg: Message = serde_json::from_str(
        r#"{"actions":[{"action":"http","url":"https://example.com/api","label":"Trigger API"}]}"#,
    )
    .unwrap();
    assert_eq!(
        msg.url(),
        Some(("https://example.com/api", Some("Trigger API")))
    );

    // Case-insensitive action type matching (e.g. VIEW, HTTP)
    let msg: Message = serde_json::from_str(
        r#"{"actions":[{"action":"VIEW","url":"https://example.com/upper","label":"Upper"}]}"#,
    )
    .unwrap();
    assert_eq!(
        msg.url(),
        Some(("https://example.com/upper", Some("Upper")))
    );

    let msg: Message = serde_json::from_str(
        r#"{"actions":[{"action":"Http","url":"https://example.com/mixed","label":"Mixed"}]}"#,
    )
    .unwrap();
    assert_eq!(
        msg.url(),
        Some(("https://example.com/mixed", Some("Mixed")))
    );
}

#[test]
fn multiple_actions_selection_and_ordering() {
    // Broadcast action with a URL appearing before a later view action does not win; view action is selected
    let json = r#"{
        "actions": [
            {"action": "broadcast", "label": "Android Broadcast", "url": "https://example.com/broadcast"},
            {"action": "view", "label": "Open Dashboard", "url": "https://example.com/dashboard"}
        ]
    }"#;
    let msg: Message = serde_json::from_str(json).unwrap();
    assert_eq!(
        msg.url(),
        Some(("https://example.com/dashboard", Some("Open Dashboard")))
    );

    // Broadcast action with a URL appearing before a later http action does not win; http action is selected
    let json = r#"{
        "actions": [
            {"action": "broadcast", "label": "Android Broadcast", "url": "https://example.com/broadcast"},
            {"action": "http", "label": "Ack Alert", "url": "https://example.com/ack"}
        ]
    }"#;
    let msg: Message = serde_json::from_str(json).unwrap();
    assert_eq!(
        msg.url(),
        Some(("https://example.com/ack", Some("Ack Alert")))
    );

    // First view action lacks URL, followed by broadcast with URL, followed by view action with URL
    let json = r#"{
        "actions": [
            {"action": "view", "label": "No URL Here"},
            {"action": "broadcast", "label": "Broadcast", "url": "https://example.com/broadcast"},
            {"action": "view", "label": "Second View", "url": "https://example.com/second"}
        ]
    }"#;
    let msg: Message = serde_json::from_str(json).unwrap();
    assert_eq!(
        msg.url(),
        Some(("https://example.com/second", Some("Second View")))
    );

    // Multiple valid actions select the first matching view/http action
    let json = r#"{
        "actions": [
            {"action": "view", "label": "First View", "url": "https://example.com/first"},
            {"action": "http", "label": "Second Action", "url": "https://example.com/second"}
        ]
    }"#;
    let msg: Message = serde_json::from_str(json).unwrap();
    assert_eq!(
        msg.url(),
        Some(("https://example.com/first", Some("First View")))
    );
}

#[test]
fn action_url_and_title_truncation_and_char_boundary_safety() {
    // Standalone helper unit tests
    assert_eq!(truncate_chars("", 512), "");
    assert_eq!(truncate_chars("short", 512), "short");
    let exact_100 = "a".repeat(100);
    assert_eq!(truncate_chars(&exact_100, 100), exact_100);
    let exact_512 = "a".repeat(512);
    assert_eq!(truncate_chars(&exact_512, 512), exact_512);

    let overlong_620 = "u".repeat(620);
    let truncated_512 = truncate_chars(&overlong_620, 512);
    assert_eq!(truncated_512.chars().count(), 512);
    assert_eq!(truncated_512, &overlong_620[..512]);

    let overlong_150 = "t".repeat(150);
    let truncated_100 = truncate_chars(&overlong_150, 100);
    assert_eq!(truncated_100.chars().count(), 100);
    assert_eq!(truncated_100, &overlong_150[..100]);

    // Char-boundary safety with 4-byte UTF-8 emoji at boundaries
    // 511 ASCII chars + 4-byte emoji (🦀) + trailing characters -> 512 chars exactly
    let multi_url = format!("{}🦀extra", "x".repeat(511));
    let truncated_url = truncate_chars(&multi_url, 512);
    assert_eq!(truncated_url.chars().count(), 512);
    assert!(truncated_url.ends_with('🦀'));
    assert_eq!(truncated_url.len(), 511 + 4);

    // 99 ASCII chars + 4-byte emoji (🚀) + trailing characters -> 100 chars exactly
    let multi_title = format!("{}🚀extra", "y".repeat(99));
    let truncated_title = truncate_chars(&multi_title, 100);
    assert_eq!(truncated_title.chars().count(), 100);
    assert!(truncated_title.ends_with('🚀'));
    assert_eq!(truncated_title.len(), 99 + 4);

    // 120 3-byte Japanese hiragana characters truncated to 100 chars
    let japanese = "あ".repeat(120);
    let truncated_jp = truncate_chars(&japanese, 100);
    assert_eq!(truncated_jp.chars().count(), 100);
    assert_eq!(truncated_jp, "あ".repeat(100));
    assert_eq!(truncated_jp.len(), 300);

    // Message::url() truncation on action url and label
    let json = format!(
        r#"{{"actions":[{{"action":"view","label":"{}","url":"https://example.com/{}"}}]}}"#,
        "L".repeat(150),
        "a".repeat(600)
    );
    let msg: Message = serde_json::from_str(&json).unwrap();
    let (url, title) = msg.url().unwrap();
    assert_eq!(url.chars().count(), 512);
    assert_eq!(
        url,
        &format!("https://example.com/{}", "a".repeat(600))[..512]
    );
    assert_eq!(title.unwrap().chars().count(), 100);
    assert_eq!(title.unwrap(), "L".repeat(100));

    // Message::url() truncation on fallback click URL
    let click_url = format!("https://example.com/click/{}", "c".repeat(600));
    let msg: Message = serde_json::from_str(&format!(r#"{{"click":"{click_url}"}}"#)).unwrap();
    let (url, title) = msg.url().unwrap();
    assert_eq!(url.chars().count(), 512);
    assert_eq!(url, &click_url[..512]);
    assert_eq!(title, None);

    // Message::url() char-boundary safe truncation with multi-byte emoji on fallback click
    let multi_click = format!("https://example.com/{}🦀extra", "c".repeat(511 - 20));
    let msg: Message = serde_json::from_str(&format!(r#"{{"click":"{multi_click}"}}"#)).unwrap();
    let (url, _) = msg.url().unwrap();
    assert_eq!(url.chars().count(), 512);
    assert!(url.ends_with('🦀'));
}
#[tokio::test]
async fn dedupe_reservations_release_on_failure_and_expire() {
    let cfg = config();
    let store = tokio::sync::Mutex::new(Store::default());
    let now = Utc::now();
    assert_eq!(store.lock().await.begin("a", &cfg, now), Claim::Acquired);
    assert_eq!(store.lock().await.begin("a", &cfg, now), Claim::InProgress);
    Store::complete(&store, "a", false, &cfg).await;
    assert_eq!(store.lock().await.begin("a", &cfg, now), Claim::Acquired);
    Store::complete(&store, "a", true, &cfg).await;
    assert_eq!(store.lock().await.begin("a", &cfg, now), Claim::Duplicate);
    assert_eq!(
        store
            .lock()
            .await
            .begin("a", &cfg, now + Duration::seconds(301)),
        Claim::Acquired
    );
}
#[tokio::test]
async fn durable_dedupe_reads_dotnet_timestamps_and_bounds_entries() {
    let path = std::env::temp_dir().join(format!("ntfy-dedupe-{}.json", std::process::id()));
    std::fs::write(
        &path,
        format!(
            r#"{{"id:legacy":"{}"}}"#,
            Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Nanos, false)
        ),
    )
    .unwrap();
    let cfg = Config {
        deduplication_store_path: path.to_str().unwrap().into(),
        deduplication_max_entries: 1,
        ..config()
    };
    let store = tokio::sync::Mutex::new(Store::load(&cfg));
    assert_eq!(
        store.lock().await.begin("id:legacy", &cfg, Utc::now()),
        Claim::Duplicate
    );
    Store::complete(&store, "id:new", true, &cfg).await;
    let mut loaded = Store::load(&cfg);
    assert_eq!(loaded.begin("id:new", &cfg, Utc::now()), Claim::Duplicate);
    assert_eq!(loaded.begin("id:legacy", &cfg, Utc::now()), Claim::Acquired);
    std::fs::remove_file(path).unwrap();
}
#[tokio::test]
async fn durable_cursors_restore_per_topic_after_restart() {
    let dir = std::env::temp_dir().join(format!("ntfy-cursors-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let path = dir.join("dedupe.json");
    let cfg = Config {
        deduplication_store_path: path.to_str().unwrap().into(),
        ..config()
    };
    let store = tokio::sync::Mutex::new(Store::load(&cfg));
    Store::set_cursor(&store, "alerts", "ntfy-id-1", &cfg).await;
    Store::set_cursor(&store, "other", "ntfy-id-2", &cfg).await;

    let restarted = Store::load(&cfg);
    assert_eq!(restarted.cursor("alerts"), Some("ntfy-id-1"));
    assert_eq!(restarted.cursor("other"), Some("ntfy-id-2"));
    assert_eq!(restarted.cursor("missing"), None);
    let forwarder = Forwarder::new(cfg).unwrap();
    assert_eq!(
        forwarder.topics.lock().await["alerts"].cursor.as_deref(),
        Some("ntfy-id-1")
    );
    std::fs::remove_dir_all(dir).unwrap();
}
#[tokio::test]
async fn cursor_temp_artifacts_do_not_replace_good_file_and_corrupt_file_is_safe() {
    let dir = std::env::temp_dir().join(format!("ntfy-cursor-atomic-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let dedupe_path = dir.join("dedupe.json");
    let cfg = Config {
        deduplication_store_path: dedupe_path.to_str().unwrap().into(),
        ..config()
    };
    let cursor_path = dir.join("cursors.json");
    let store = tokio::sync::Mutex::new(Store::default());
    Store::set_cursor(&store, "alerts", "good-cursor", &cfg).await;

    for torn_temp in [b"".as_slice(), b"{\"alerts\":\"partial".as_slice()] {
        std::fs::write(dir.join("cursors.json.tmp"), torn_temp).unwrap();
        let loaded = Store::load(&cfg);
        assert_eq!(loaded.cursor("alerts"), Some("good-cursor"));
    }

    std::fs::write(&cursor_path, b"{\"alerts\":").unwrap();
    let loaded = Store::load(&cfg);
    assert_eq!(loaded.cursor("alerts"), None);
    let forwarder = Forwarder::new(cfg).unwrap();
    assert_eq!(
        forwarder.topics.lock().await["alerts"].cursor.as_deref(),
        None
    );
    std::fs::remove_dir_all(dir).unwrap();
}
#[tokio::test]
async fn health_and_metrics_report_per_topic_state() {
    let app = Forwarder::new(Config {
        topics: vec!["a".into(), "b".into()],
        ..config()
    })
    .unwrap();
    assert_eq!(
        health(State(app.clone())).await.0,
        StatusCode::SERVICE_UNAVAILABLE
    );
    app.topics.lock().await.get_mut("a").unwrap().connected = true;
    let (status, Json(body)) = health(State(app.clone())).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["status"], "Degraded");
    app.topics.lock().await.get_mut("b").unwrap().connected = true;
    assert_eq!(health(State(app.clone())).await.1.0["status"], "Healthy");
    assert!(
        metrics(State(app))
            .await
            .1
            .contains("ntfy_forwarder_messages_received_total{topic=\"a\"} 0")
    );
}

#[test]
fn stream_url_resumes_from_cursor_or_initial_window() {
    let forwarder = Forwarder::new(config()).unwrap();
    let url = forwarder.stream_url("alerts", None).unwrap();
    assert_eq!(url.as_str(), "http://localhost:9999/alerts/json?since=10m");
    let url = forwarder.stream_url("alerts", Some("ntfy-id-1")).unwrap();
    assert_eq!(
        url.as_str(),
        "http://localhost:9999/alerts/json?since=ntfy-id-1"
    );
    let live = Forwarder::new(Config {
        initial_since: String::new(),
        ..config()
    })
    .unwrap();
    assert_eq!(
        live.stream_url("alerts", None).unwrap().as_str(),
        "http://localhost:9999/alerts/json"
    );
}

#[tokio::test]
async fn delivery_metrics_escape_labels_and_report_pending_age() {
    let topic = "quoted\"topic\\line\n";
    let app = Forwarder::new(Config {
        topics: vec![topic.into()],
        ..config()
    })
    .unwrap();
    {
        let mut topics = app.topics.lock().await;
        let state = topics.get_mut(topic).unwrap();
        state.pending_since = Some(std::time::Instant::now() - std::time::Duration::from_secs(5));
        state.last_successful_delivery_timestamp_seconds = 123;
        state.delivery_attempts = 4;
        state.permanent_delivery_failures = 1;
        state.transient_delivery_failures = 2;
    }
    let (_, body) = metrics(State(app)).await;
    let labels = r#"{topic="quoted\"topic\\line\n",destination="pushover"}"#;
    for (name, expected) in [
        ("pending_messages", 1.0),
        ("last_successful_delivery_timestamp_seconds", 123.0),
        ("delivery_attempts_total", 4.0),
        ("permanent_delivery_failures_total", 1.0),
        ("transient_delivery_failures_total", 2.0),
    ] {
        let prefix = format!("ntfy_forwarder_{name}{labels} ");
        let value: f64 = body
            .lines()
            .find_map(|line| line.strip_prefix(&prefix))
            .unwrap()
            .parse()
            .unwrap();
        assert_eq!(value, expected);
    }
    let prefix = format!("ntfy_forwarder_oldest_pending_age_seconds{labels} ");
    let age: f64 = body
        .lines()
        .find_map(|line| line.strip_prefix(&prefix))
        .unwrap()
        .parse()
        .unwrap();
    assert!(age >= 5.0);
}
