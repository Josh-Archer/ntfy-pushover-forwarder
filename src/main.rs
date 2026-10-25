mod config;
mod dedupe;
mod forwarder;
mod message;
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
            "last_successful_delivery_timestamp_seconds",
            "gauge",
            "Unix timestamp of the last successful Pushover message delivery; zero until the first success in this process.",
        ),
        (
            "pending_messages",
            "gauge",
            "Messages being delivered or retained for retry in this process; excludes upstream ntfy backlog.",
        ),
        (
            "oldest_pending_age_seconds",
            "gauge",
            "Monotonic age of the oldest locally pending message; zero when idle.",
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
            let topic = topic
                .replace('\\', "\\\\")
                .replace('"', "\\\"")
                .replace('\n', "\\n");
            let value = match name {
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
