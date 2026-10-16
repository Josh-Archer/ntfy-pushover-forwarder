use super::*;
use crate::{
    config::Config,
    dedupe::{Claim, Store},
    message::Message,
};
use chrono::{Duration, Utc};
use std::path::Path;

fn config() -> Config {
    Config {
        ntfy_url: "http://localhost:9999".into(),
        pushover_user_key: "user".into(),
        pushover_default_token: "app".into(),
        topics: vec!["alerts".into()],
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
