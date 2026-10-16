# Local resource comparison

Measured 2026-10-16 on Linux x86_64. Baseline: upstream commit `3e4bae9` (.NET SDK 9.0.319, Release publish). Rust: rustc 1.98.0, release profile with thin LTO. These are process measurements, outside containers, on the same host with the same simulator. The host was not isolated from other workloads.

Each result is the median of three new process runs: four idle topic subscriptions for three seconds, followed by 1,000 unique text messages distributed across those topics. No attachments, recovery alerts or priority drops. Default dedupe window and entry limit, HTTP keepalive, fake credentials and local HTTP endpoints. Persistent runs write a JSON dedupe snapshot after every successful delivery to the local temporary filesystem, rather than the production PVC.

| Measurement | .NET | Rust | Reduction |
|---|---:|---:|---:|
| Idle RSS, memory dedupe | 81.46 MiB | 6.18 MiB | 92.4% |
| RSS after 1,000 messages, memory dedupe | 92.19 MiB | 6.56 MiB | 92.9% |
| Process CPU for 1,000 messages, memory dedupe | 0.66 s | 0.04 s | 93.9% |
| Idle RSS, persistent dedupe | 81.29 MiB | 6.06 MiB | 92.5% |
| RSS after 1,000 messages, persistent dedupe | 97.25 MiB | 6.61 MiB | 93.2% |
| Process CPU for 1,000 messages, persistent dedupe | 0.78 s | 0.14 s | 82.1% |

Median wall time for the 1,000-message burst: 1.909 s versus 0.990 s with in-memory dedupe, and 1.865 s versus 1.002 s with persistent dedupe. Idle process CPU was 0.01 s for .NET and below the 0.01 s measurement resolution for Rust over the three-second window. These short idle windows cannot establish a reliable idle CPU percentage.

RSS is sampled at the start, after idle, and after load; it is not peak RSS. Process CPU is Linux `/proc` user plus system time and excludes the simulator and child processes. HTTP responses are drained, and the simulator disables Nagle's algorithm. No real notifications were sent. Raw per-run measurements are in [benchmark-results.json](benchmark-results.json).

Reproduce either variant:

```bash
python3 scripts/integration.py --benchmark -- "$PWD/target/release/ntfy-pushover-forwarder"
python3 scripts/integration.py --benchmark --persistent -- "$PWD/target/release/ntfy-pushover-forwarder"
# Run the same harness with a published .NET baseline:
python3 scripts/integration.py --benchmark --persistent -- /path/to/dotnet /path/to/NtfyPushoverForwarder.dll
```

The previous implementation's 107 tests passed before migration. Rust validation includes 11 unit tests and nine black-box process tests. Additional local checks confirmed dedupe-file interoperability in both directions (.NET to Rust and Rust to .NET), and container forwarding, health, persistence and clean SIGTERM shutdown as UID/GID 1654 with a read-only root filesystem and all capabilities dropped.

The image was built and exercised locally for linux/amd64. The release workflow also builds linux/arm64; that platform has not been exercised locally. No production deployment, delivery to a real Pushover account, or production CPU/memory comparison has been performed. Keep existing Kubernetes limits until container metrics establish representative usage with real attachments, TLS, and PVC latency.
