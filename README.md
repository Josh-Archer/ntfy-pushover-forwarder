# ntfy-pushover-forwarder

A small Rust service that forwards self-hosted [ntfy](https://ntfy.sh/) topic streams to [Pushover](https://pushover.net/).

One Tokio runtime thread handles the subscriptions, HTTP clients, health endpoint and metrics. Messages are processed in order per topic. HTTP clients reuse connections; no polling or per-message threads are required.

## Behavior

- Topic-to-app token routing, tag-to-sound mapping and `device:phone` targeting.
- Action buttons (`actions`) of type `view` or `http` and `click` URLs map to Pushover `url` (truncated to 512 characters) and `url_title` (truncated to 100 characters) button fields.
- ntfy priority 1–5 maps to Pushover −2 through 2. Missing priority means 3. Emergency messages set retry=60 and expire=3600.
- Inline images and tag-based fallback logos. Downloads are limited to 2.5 MiB by default, including chunked responses. Only images are attached; a failed download still allows the text alert through.
- An 8 MiB shared logo cache; a 1 MiB limit per incoming JSON line. Image bytes are shared between upload attempts instead of copied.
- Deduplication uses ntfy message IDs, with shared in-flight reservations across topics and optional file persistence. Successful deliveries are recorded; failures release the reservation.
- Bounded retries for network errors, HTTP 429 and server errors. Other HTTP 4xx responses are permanent failures and allow processing to continue.
- Reconnects use exponential backoff with jitter and `since=<last handled ID>`. A topic with no saved cursor (first start, or after its cursor is lost) requests `since=<InitialSince>` (default `10m`, empty for live-only). Per-topic cursors are saved in `cursors.json` beside the dedupe file only when `DeduplicationStorePath` is configured, and are restored after restart. The dedupe path cannot be named `cursors.json`. A failed message is retained in memory for retry before resubscribing, including when it is the first message and there is no cursor yet.
- Optional recovery alerts, `/healthz`, and Prometheus `/metrics` with per-topic counters.

Delivery remains at least once: if Pushover accepts a request but its response is lost, a retry can send a duplicate. Pending messages are retained in memory and are not a durable delivery queue. Cursor persistence requires `DeduplicationStorePath` and stores only the last successfully handled ntfy ID for each topic.

## Run

```bash
cargo build --release --locked
Forwarder__NtfyUrl=https://ntfy.example.com \
Forwarder__PushoverUserKey=your-user-key \
Forwarder__PushoverDefaultToken=your-app-token \
Forwarder__Topics__0=system-alerts \
./target/release/ntfy-pushover-forwarder
```

The process reads `appsettings.json` from its working directory, then applies `Forwarder__...` environment overrides. The Helm chart continues to mount `/app/appsettings.json` and inject credentials from your existing Secret.

```json
{
  "Forwarder": {
    "NtfyUrl": "https://ntfy.example.com",
    "Topics": ["system-alerts", "media-updates"],
    "MinimumPriority": 4,
    "SoundMap": {"siren": "siren", "film_projector": "bike"},
    "LogoMap": {"film_projector": "https://example.com/radarr.png"}
  }
}
```

Keep credentials in environment variables or Kubernetes Secrets. Per-topic tokens use `Forwarder__TopicTokens__media-updates`; sound and logo mappings support the same nested syntax. Scalar names are case insensitive, while topic names and tag map keys retain their case. Indexed topic overrides preserve other configured entries.

| Setting under `Forwarder` | Default / meaning |
|---|---|
| `NtfyUrl` | Required HTTP(S) base URL |
| `NtfyToken` | Optional Bearer token |
| `PushoverUrl` | `https://api.pushover.net/1/messages.json` |
| `PushoverUserKey` | Required |
| `PushoverDefaultToken` | Required unless every topic has a token |
| `Topics` | At least one topic required |
| `TopicTokens`, `SoundMap`, `LogoMap` | Empty maps |
| `MinimumPriority` | `1`; omitted ntfy priorities are treated as `3` |
| `DeduplicationWindowSeconds` | `300`; zero disables dedupe |
| `DeduplicationMaxEntries` | `1024`, minimum 1 |
| `DeduplicationStorePath` | Empty disables dedupe and cursor persistence; when set, cursors use a sibling `cursors.json` file (the dedupe file must have a different name) |
| `InitialSince` | ntfy `since` used for a topic with no saved cursor (default `10m`; empty streams live-only) |
| `SendRecoveryAlerts` | `true` |
| `HttpPort` | `8080`; zero disables the HTTP listener |
| `MaxAttachmentBytes` | `2621440`; negative values restore the default |
| `MaxRetries` | `3` total delivery attempts, minimum 1 |
| `RetryDelayMilliseconds` | `0` |
| `ReconnectInitialDelayMilliseconds` | `1000`; capped at 60 seconds plus up to 20% jitter |

`html` tags preserve markup. `markdown` or `md` tags HTML-escape the body and convert backtick spans to `<code>`. Formatting tags are case insensitive. Action buttons (`actions`) and `click` URLs map to Pushover `url` and `url_title` button fields, preferring the first `view` or `http` action with a URL (non-`view`/`http` actions such as `broadcast` are ignored). URLs are truncated to Pushover's 512-character limit, and button labels (`url_title`) are truncated to 100 characters. Images without an attachment use the first matching `LogoMap` tag, with `mag` as a fallback for topics containing `media`.

The ntfy token is sent only to same-origin message attachments, never to logos or other origins. HTTP redirects are disabled for subscriptions, API posts and image downloads; configure final URLs directly.

## Container and Helm

```bash
docker build -t ntfy-pushover-forwarder .
helm upgrade --install ntfy-forwarder ./charts/ntfy-pushover-forwarder \
  --set image.tag=YOUR_RELEASE --set existingSecret=ntfy-secret
```

The image runs as UID/GID 1654, preserving access to existing dedupe volumes. Helm retains its existing values, Secret keys, service, probes, single replica and Recreate strategy. Persistence supports one writer; do not share the dedupe file between replicas.

## Migration from .NET

The Rust container replaces the .NET runtime. Existing `Forwarder__...` variables, `Forwarder` JSON configuration and dedupe JSON maps containing RFC3339/.NET DateTimeOffset timestamps are supported. Fingerprint keys retain their original format. No cluster deployment is performed by building this repository.

Intentional differences:

- Invalid URLs, missing topics or missing credentials fail startup so deployment errors are visible.
- `HttpPort=0` now disables the listener as documented; the chart defaults to 8080.
- `.NET` runtime metrics are removed. Forwarder counters retain `ntfy_forwarder_*_total` names and `topic` labels; OpenTelemetry scope labels and sample timestamps are omitted. Use container metrics for process CPU and memory.
- Logo cache and incoming lines have explicit memory limits. Downloads do not follow redirects.
- Development-specific .NET configuration files and ASP.NET settings are replaced by `appsettings.json`, `Forwarder__...`, and `RUST_LOG` for logging.

## Validation and resource measurements

Requires Rust 1.98 and Python 3.10+ for the stdlib-only integration harness:

```bash
cargo fmt --check
cargo clippy --locked --all-targets -- -D warnings
cargo test --locked
cargo build --release --locked
python3 scripts/integration.py -- "$PWD/target/release/ntfy-pushover-forwarder"
helm lint charts/ntfy-pushover-forwarder
```

The black-box tests run actual processes against local fake HTTP services. They cover routing, formatting, priority filtering, dedupe races, restart persistence, retries/replay, recovery, images, size limits, and credential boundaries. They never send real notifications.

On Linux, measure four idle topic subscriptions followed by 1,000 text messages, repeating three times:

```bash
python3 scripts/integration.py --benchmark -- "$PWD/target/release/ntfy-pushover-forwarder"
```

Add `--persistent` to include a dedupe file write after each delivery. See [measured results](docs/resource-comparison.md), [cluster validation](docs/cluster-validation-2026-10-16.md), and raw runs for the .NET comparison.

The harness reports RSS and process CPU seconds using `/proc`, plus wall time. The same command can run a published .NET baseline by passing its `dotnet` executable and DLL instead. These local measurements do not establish production savings; remeasure container CPU/memory with representative traffic before reducing Kubernetes limits.
