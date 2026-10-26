mod config;
mod dedupe;
mod forwarder;
mod message;
mod queue;
use axum::{
    Json, Router,
    extract::State,
    http::{StatusCode, header},
    routing::get,
};
use forwarder::{Forwarder, METRICS};
use std::sync::Arc;

#[tokio::main(flavor = "current_thread")]
async fn main() -> anyhow::Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()),
        )
        .init();
    let config = config::Config::load(std::path::Path::new("appsettings.json"), std::env::vars())?;
    let args: Vec<String> = std::env::args().skip(1).collect();
    if !args.is_empty() {
        anyhow::ensure!(
            args.first().map(String::as_str) == Some("queue"),
            "Usage: queue list | queue replay ID | queue delete ID"
        );
        let mut queue = queue::Queue::open(&config)?;
        match args.get(1).map(String::as_str) {
            Some("list") if args.len() == 2 => {
                println!("{}", serde_json::to_string_pretty(queue.failed())?)
            }
            Some("replay") if args.len() == 3 => queue.replay(&args[2], &config.topics)?,
            Some("delete") if args.len() == 3 => queue.delete(&args[2])?,
            _ => anyhow::bail!("Usage: queue list | queue replay ID | queue delete ID"),
        }
        return Ok(());
    }
    let port = config.http_port;
    let app = Forwarder::new(config)?;
    let mut tasks = tokio::task::JoinSet::new();
    for topic in &app.config.topics {
        tasks.spawn(app.clone().listen(topic.clone()));
    }
    if port > 0 {
        let listener =
            tokio::net::TcpListener::bind((std::net::Ipv4Addr::UNSPECIFIED, port)).await?;
        let router = Router::new()
            .route("/healthz", get(health))
            .route("/metrics", get(metrics))
            .with_state(app);
        tasks.spawn(async move {
            if let Err(e) = axum::serve(listener, router).await {
                tracing::error!(error = %e, "HTTP server stopped");
            }
        });
    }
    tracing::info!(port, "Forwarder started");
    tokio::select! {
        _ = shutdown() => {},
        result = tasks.join_next() => anyhow::bail!("Service task stopped unexpectedly: {result:?}"),
    }
    tasks.abort_all();
    while tasks.join_next().await.is_some() {}
    Ok(())
}
async fn shutdown() {
    #[cfg(unix)]
    {
        let mut term = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
            .expect("register SIGTERM");
        tokio::select! { _ = term.recv() => {}, _ = tokio::signal::ctrl_c() => {} }
    }
    #[cfg(not(unix))]
    {
        let _ = tokio::signal::ctrl_c().await;
    }
}
async fn health(State(app): State<Arc<Forwarder>>) -> (StatusCode, Json<serde_json::Value>) {
    let topics = app.topics.lock().await;
    let active = topics.values().filter(|s| s.connected).count();
    let status = if active == topics.len() {
        "Healthy"
    } else if active == 0 {
        "Unhealthy"
    } else {
        "Degraded"
    };
    (
        if active == 0 {
            StatusCode::SERVICE_UNAVAILABLE
        } else {
            StatusCode::OK
        },
        Json(serde_json::json!({"status": status, "checks": [
            {"name": "self", "status": "Healthy", "description": "forwarder process up"},
            {"name": "topics", "status": status, "description": format!("{active} of {} topic loops active", topics.len())}
        ]})),
    )
}
async fn metrics(
    State(app): State<Arc<Forwarder>>,
) -> ([(header::HeaderName, &'static str); 1], String) {
    let mut failed = std::collections::HashMap::<String, usize>::new();
    for entry in app.queue.lock().await.failed() {
        *failed.entry(entry.topic.clone()).or_default() += 1;
    }
    let topics = app.topics.lock().await;
    let mut text = String::new();
    use std::fmt::Write;
    for (index, name) in METRICS.iter().enumerate() {
        let _ = writeln!(text, "# TYPE ntfy_forwarder_{name}_total counter");
        for (topic, state) in topics.iter() {
            let topic = topic
                .replace('\\', "\\\\")
                .replace('"', "\\\"")
                .replace('\n', "\\n");
            let _ = writeln!(
                text,
                "ntfy_forwarder_{name}_total{{topic=\"{topic}\"}} {}",
                state.counters[index]
            );
        }
    }
    for (name, kind, help) in [
        (
            "dead_letter_messages",
            "gauge",
            "Durable permanent rejections retained for explicit replay or deletion.",
        ),
        (
            "last_successful_delivery_timestamp_seconds",
            "gauge",
            "Unix timestamp of the last successful Pushover message delivery; zero until the first success in this process.",
        ),
        (
            "pending_messages",
            "gauge",
            "Messages being delivered or retained in the durable queue; excludes upstream ntfy backlog.",
        ),
        (
            "oldest_pending_age_seconds",
            "gauge",
            "Age of the oldest locally pending message, including recovered queue age; zero when idle.",
        ),
        (
            "delivery_attempts_total",
            "counter",
            "Message HTTP delivery attempts, excluding recovery alerts.",
        ),
        (
            "permanent_delivery_failures_total",
            "counter",
            "Delivery batches ending in a permanent downstream rejection.",
        ),
        (
            "transient_delivery_failures_total",
            "counter",
            "Delivery batches exhausting attempts after transient failures.",
        ),
    ] {
        let _ = writeln!(text, "# HELP ntfy_forwarder_{name} {help}");
        let _ = writeln!(text, "# TYPE ntfy_forwarder_{name} {kind}");
        for (topic, state) in topics.iter() {
            let dead_letters = failed.get(topic).copied().unwrap_or_default();
            let topic = topic
                .replace('\\', "\\\\")
                .replace('"', "\\\"")
                .replace('\n', "\\n");
            let value = match name {
                "dead_letter_messages" => dead_letters as f64,
                "last_successful_delivery_timestamp_seconds" => {
                    state.last_successful_delivery_timestamp_seconds as f64
                }
                "pending_messages" => u8::from(state.pending_since.is_some()) as f64,
                "oldest_pending_age_seconds" => state
                    .pending_since
                    .map(|since| since.elapsed().as_secs_f64())
                    .unwrap_or_default(),
                "delivery_attempts_total" => state.delivery_attempts as f64,
                "permanent_delivery_failures_total" => state.permanent_delivery_failures as f64,
                "transient_delivery_failures_total" => state.transient_delivery_failures as f64,
                _ => unreachable!(),
            };
            let _ = writeln!(
                text,
                "ntfy_forwarder_{name}{{topic=\"{topic}\",destination=\"pushover\"}} {value}"
            );
        }
    }
    if !app.config.grok_bot_webhook_url.is_empty() {
        for name in [
            "grok_bot_deliveries_total",
            "grok_bot_failures_total",
            "grok_bot_retries_total",
        ] {
            let _ = writeln!(text, "# TYPE ntfy_forwarder_{name} counter");
            for (topic, state) in topics.iter() {
                let topic = topic
                    .replace('\\', "\\\\")
                    .replace('"', "\\\"")
                    .replace('\n', "\\n");
                let count = match name {
                    "grok_bot_deliveries_total" => state.grok_bot_deliveries,
                    "grok_bot_failures_total" => state.grok_bot_failures,
                    _ => state.grok_bot_retries,
                };
                let _ = writeln!(
                    text,
                    "ntfy_forwarder_{name}{{topic=\"{topic}\",destination=\"grokbot\"}} {count}"
                );
            }
        }
    }
    (
        [(
            header::CONTENT_TYPE,
            "text/plain; version=0.0.4; charset=utf-8",
        )],
        text,
    )
}
#[cfg(test)]
mod tests;
