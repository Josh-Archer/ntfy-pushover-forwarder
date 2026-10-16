# Cluster validation: 2026-10-16

The Rust image was built, published and exercised successfully as a separate canary on the Home cluster. Production remains on the original .NET v0.5.3 image; no production image or GitOps source was changed.

Tested source: `66ea3c2b1238111efc512664affe96dadf4379a4`.

Image: `ghcr.io/josh-archer/ntfy-pushover-forwarder:canary-66ea3c2b1238111efc512664affe96dadf4379a4@sha256:e3dfb3942eb107daa89f2daeb6d9ab1c43c18f501f21f577630450e4fc29aa92` (linux/amd64).

[The image workflow passed verification and publication](https://github.com/Josh-Archer/ntfy-pushover-forwarder/actions/runs/38911342494). Its verification ran Rust formatting, Clippy, 11 unit tests, nine process integration tests, Helm lint, a Docker build and a hardened-container smoke test. The running canary's image ID matched the published digest.

## Live checks

The canary ran in the monitoring namespace with a separate 100 MiB Longhorn PVC, UID 1654, a read-only root filesystem, all capabilities dropped and no service-account token mount. The existing ntfy credential stayed in Kubernetes Secret references. The Pushover fields were fake values and the endpoint was forced to an in-cluster multipart sink.

| Check | Result |
|---|---|
| Actual ntfy service subscription and health | Passed |
| Authenticated publication to a dedicated test topic | Passed |
| Priority filtering and ntfy-to-Pushover emergency priority fields | Passed |
| Markdown escaping, HTML flag, title, sound, device and click fields | Passed |
| HTTP 503 and 429 retry sequence | Passed; attempts stayed in order, then the next event was delivered |
| Permanent HTTP 400 response | Passed; the next event was delivered |
| Image URL attached through real ntfy | Passed; multipart PNG SHA-256 and MIME type matched the original |
| Image credential boundary | Passed; the foreign-origin image host received no ntfy Authorization header |
| Longhorn persistence after pod replacement | Passed; the delivered ID remained in the dedupe JSON file |
| Replay of the actual cached ntfy event after restart | Passed; dedupe counter increased without a second sink post |
| Four production topic subscriptions in filter-only mode | Passed; all four active, no forwarded messages |
| Unexpected container restarts | Zero on the observed canary pods |

Replay used the sink's finite-stream fixture to replay the actual cached test event, since a fresh Rust process normally subscribes live. The fixture selected only the successful test event and reused the same canary PVC. Normal ntfy connectivity was restored and checked before the final filter-only subscription check.

## Resource observations

At **2026-10-16 16:41:08 UTC**, the Rust canary subscribed to all four production topics in filter-only mode (`MinimumPriority=6`, while ntfy priorities range from 1 to 5). Recovery alerts were disabled. No production message bodies were forwarded to the sink or saved in the report.

| Observation | Rust canary | Existing .NET production pod |
|---|---:|---:|
| Container working memory | 2.32 MiB | 73.38 MiB |
| CPU, Prometheus two-minute rate | 0.41 millicores | 1.35 millicores |

The Rust process reported 6,004 KiB RSS at that point. Working memory came from `container_memory_working_set_bytes`; CPU came from `rate(container_cpu_usage_seconds_total[2m])`. Metrics were selected for the current pod, namespace and forwarder container, excluding the sink and old canary pods. The Kubernetes Metrics API was temporarily unavailable for the final sample; Prometheus supplied the recorded metrics.

During the earlier delivery test, sampled Rust working memory ranged from 1.92 to 3.55 MiB over 16:34–16:37 UTC. That phase used one test topic. The later four-topic phase verified the additional connection footprint.

These observations demonstrate a small running footprint, not an equivalent-load production CPU comparison. Production was forwarding its own traffic while the four-topic Rust check filtered it. Use the [controlled local benchmark](resource-comparison.md) for the equivalent 1,000-message CPU comparison, and measure representative production traffic before lowering limits.

## Limits and cleanup

- The cluster ntfy server rejects native file uploads with HTTP 400 / ntfy error 40014 (`attachments not allowed`). Its configuration was preserved; URL attachments passed instead. Native attachment behavior remains covered by the local fake-server tests.
- No real Pushover API request or device notification was sent by the canary. Multipart delivery and failure handling were validated against the in-cluster sink.
- Only linux/amd64 was exercised on the cluster. ARM64 remains untested by this run.
- The canary Helm release, test-only PVC, sink deployment, Service and ConfigMap were removed. The exact temporary write grant for the isolated test topic was reset. Cached test events expire under the existing ntfy retention policy.
- The production pod UID remained unchanged with zero restarts; its original dedupe PVC remained Bound. The production ArgoCD application was Synced and Healthy after cleanup.

The [fixture and reproduction instructions](cluster-canary.md) are included in this branch. Production promotion requires a separate Home GitOps change and review of its newer replay-related settings.
