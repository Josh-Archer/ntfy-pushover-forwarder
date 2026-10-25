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
- Reconnects use exponential backoff with jitter and `since=<last handled ID>`. A topic with no saved cursor (first start, or after its cursor is lost) requests `since=<InitialSince>` (default `10m`, empty for live-only). Per-topic cursors are saved in `cursors.json` beside the dedupe file only when `DeduplicationStorePath` is configured, and are restored after restart. The dedupe path cannot be named `cursors.json`. Accepted messages are persisted before delivery and recovered before resubscribing, including when upstream history has expired.
- Optional recovery alerts, `/healthz`, and Prometheus `/metrics` with per-topic counters.

Delivery remains at least once: if Pushover accepts a request but its response is lost, a retry can send a duplicate. The durable queue retains one pending payload per topic, with per-destination state and atomic acknowledgement/cursor updates. Permanent rejections are retained on disk. Intake for a topic pauses behind its pending message; upstream messages not yet accepted still depend on ntfy retention.

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
| `DeduplicationStorePath` | Empty disables legacy dedupe/cursor files; the durable queue still persists cursors. When set, legacy cursors use a sibling `cursors.json` file |
| `InitialSince` | ntfy `since` used for a topic with no saved cursor (default `10m`; empty streams live-only) |
| `SendRecoveryAlerts` | `true` |
| `HttpPort` | `8080`; zero disables the HTTP listener |
| `MaxAttachmentBytes` | `2621440`; negative values restore the default |
| `MaxRetries` | `3` total delivery attempts, minimum 1 |
| `QueueStorePath` | Defaults to `queue.json` beside the dedupe file, or in the working directory; mount this directory on durable storage |
| `QueueMaxBytes` | `67108864` (64 MiB); limits serialized pending, rejected, and cursor state |
| `RetryDelayMilliseconds` | `0` selects a 1-second delivery backoff base; positive values retain their meaning as the base delay |
| `ReconnectInitialDelayMilliseconds` | `1000`; capped at 60 seconds plus up to 20% jitter |
| `GrokBotWebhookUrl` | Empty (disabled). Grok Bot webhook routine URL; supply from a Secret |
| `GrokBotWebhookKey` | Required with the URL. Webhook sender key; supply from a Secret, never `appsettings.json` |
| `GrokBotTopics` | Empty routes every topic; otherwise each entry must be a configured topic |
| `GrokBotMinimumPriority` | `4`; omitted ntfy priorities are treated as `3` |

`html` tags preserve markup. `markdown` or `md` tags HTML-escape the body and convert backtick spans to `<code>`. Formatting tags are case insensitive. Action buttons (`actions`) and `click` URLs map to Pushover `url` and `url_title` button fields, preferring the first `view` or `http` action with a URL (non-`view`/`http` actions such as `broadcast` are ignored). URLs are truncated to Pushover's 512-character limit, and button labels (`url_title`) are truncated to 100 characters. Images without an attachment use the first matching `LogoMap` tag, with `mag` as a fallback for topics containing `media`.

The ntfy token is sent only to same-origin message attachments, never to logos or other origins. HTTP redirects are disabled for subscriptions, API posts and image downloads; configure final URLs directly.

## Delivery metrics

Existing `ntfy_forwarder_*_total{topic="..."}` counters retain their labels and semantics. The following additional metrics use `topic` and `destination="pushover"` labels:

| Metric (prefixed `ntfy_forwarder_`) | Meaning |
|---|---|
| `last_successful_delivery_timestamp_seconds` | Unix time of the last successful message delivery; zero until this process delivers a message |
| `pending_messages` | 0 or 1 per topic: a message being delivered or retained for retry, including dedupe reservation waits |
| `oldest_pending_age_seconds` | Monotonic time since the locally pending message first entered delivery; zero when idle |
| `delivery_attempts_total` | HTTP delivery attempts, including retries; excludes recovery alerts |
| `permanent_delivery_failures_total` | Delivery batches ending in a permanent downstream rejection |
| `transient_delivery_failures_total` | Delivery batches that exhausted attempts after transient failures |

These metrics reset on restart. Pending age spans retries and reconnect waits, and includes image download time. Filtered messages, deduplicated messages, and recovery alerts do not update the successful-delivery timestamp. Invalid messages without IDs remain covered by the existing `forward_failures_total` counter. A transient error followed by success within the same batch increments attempts but not the exhausted-batch counter.

`pending_messages` includes recovered durable work and excludes unread messages in ntfy. `dead_letter_messages` counts retained permanent failures. Only the Pushover destination is implemented.

Example Prometheus alert expressions (apply your normal alert duration):

```promql
# A message has remained locally pending for more than five minutes.
ntfy_forwarder_oldest_pending_age_seconds > 300

# Repeated batches failed within ten minutes.
increase(ntfy_forwarder_transient_delivery_failures_total[10m]) > 3

# A downstream rejection needs investigation.
increase(ntfy_forwarder_permanent_delivery_failures_total[10m]) > 0
```

Do not alert on an old last-success timestamp alone: an idle topic can be healthy. Monitor scrape availability separately, since process restarts clear delivery timestamps and counters; pending work and dead-letter gauges recover from disk.

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

Durable queue operations require one process per queue directory (an advisory lock rejects
concurrent writers). Snapshots use file fsync, atomic rename, and directory fsync on a
local filesystem supporting these operations. Reserve space for both the old snapshot
and its replacement. Corrupt queue files fail startup; restore a backup instead of
starting empty. Full storage or write errors pause acceptance and acknowledgement;
alerts are never evicted to make room. Monitor logs and provision storage before ntfy
retention expires. After an I/O error, repair storage and restart: a rename may
have committed before a directory fsync failed, so the process refuses further writes
until disk state is reloaded. The queue stores notification content: restrict volume access.

Existing dedupe and cursor files retain their formats. On upgrade the queue imports
legacy cursors as fallback; no files are deleted. For rollback, stop the process, drain
pending work first, and back up the entire volume. Older versions cannot recover queue
payloads; keep `queue.json` for a later upgrade. At-least-once delivery permits duplicates
if a crash occurs after downstream acceptance but before durable acknowledgement.

Delivery network errors, HTTP 429 and 5xx responses use exponential backoff with
0–20% jitter, capped at 72 seconds including jitter. Valid `Retry-After` seconds
or HTTP dates provide a minimum delay, capped at 24 hours; past dates mean zero,
and malformed values fall back to backoff. Other 4xx responses remain permanent.
Attempts and deadlines survive batch exhaustion (`MaxRetries`) and restart. Only
successful handling resets a message's backoff. Each topic sleeps independently.
SIGINT/SIGTERM cancels sleeps and requests immediately; accepted work remains on
disk for restart (an interrupted request may have reached Pushover).

Permanent failures retain the original payload, topic, queue ID, destination state,
attempt count, first/last attempt timestamps, rejection timestamp, and a safe reason
(`HTTP <status>` or invalid ntfy ID). Response bodies and credentials are not stored.
The `ntfy_forwarder_dead_letter_messages{topic,destination="pushover"}` gauge counts
retained failures across restart; existing failure counters remain process-local.

Stop the service before operating on its queue (including listing), and run the
same binary with the same configuration, working directory, volume, and user:

```sh
ntfy-pushover-forwarder queue list
ntfy-pushover-forwarder queue replay QUEUE_ID
ntfy-pushover-forwarder queue delete QUEUE_ID
```

`list` prints JSON including private payloads; handle its output as sensitive data.
Fix the destination configuration before replaying. Replay moves exactly one selected
failure back to its topic's pending slot; drain that slot first. Restart the service
to deliver it. The operation is atomic and uses the same single-writer lock. Replay
bypasses dedupe and priority filtering, preserves successful destination states, and
never rewinds the ingestion cursor. Only the failed Pushover destination is selected;
Grok routing is not implemented. A failed replay returns to retained failures with
updated metadata. A crash after downstream acceptance can still cause a duplicate.

Retention is explicit: failures never expire or get evicted automatically. All retained
failures share `QueueMaxBytes` with pending work and cursors. When full, intake pauses;
inspect and replay, explicitly delete selected failures, or increase storage and the
limit. `delete` is irreversible; export `queue list` to protected storage first when
an audit copy is needed. Removing a topic from configuration leaves its work on disk;
restore the topic to deliver/replay it.

## Grok Bot destination (optional)

Selected alerts can also be sent to an existing Grok Bot through a webhook routine, independently of Pushover. Create a webhook-triggered routine on the bot, copy its URL and sender key from the routine panel, and store both in the forwarder Secret (`grokbot_webhook_url`, `grokbot_webhook_key`; Helm: `grokBot.enabled: true`).

- Each request is a `POST` of `application/json` with `Authorization: Bearer <key>` and `X-Automation-Key: <key>`, an 8-second timeout, and one attempt per delivery pass. HTTP 2xx means the routine woke.
- The body is `{"kind":"ntfy_alert","untrusted":true,"topic","id","title","message","priority","tags"}`; title and message are truncated. The routine prompt must treat these fields as untrusted data and never as authorization for infrastructure changes.
- A message is routed when Grok Bot is configured, its topic is in `GrokBotTopics` (or the list is empty), its priority is at least both `MinimumPriority` and `GrokBotMinimumPriority`, and it has no `grokbot` tag. Grok Bot should tag anything it publishes to ntfy with `grokbot` to prevent loops.
- Grok Bot state is tracked as its own destination in the durable queue (`pending`, `delivered`, `failed`, `permanent`). Pushover is attempted first and is never blocked by Grok Bot. While Pushover is still retrying, a transient Grok Bot failure is retried with it; once Pushover finishes, a Grok Bot failure is final (`failed`) and counted in `ntfy_forwarder_grok_bot_failures_total`. A delivered destination is never resent, including on Pushover retries. 4xx responses other than 429 are permanent. Dead-letter replay resends only Pushover.
- Unit tests use a protocol-faithful fake gateway only. Delivery to a real bot must be validated separately with the real routine URL and key: publish a test alert to a routed topic and confirm the routine wakes with the expected body.
