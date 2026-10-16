# Cluster canary validation

Build and publish an immutable `canary-<full commit SHA>` image from the Rust feature branch. The canary workflow first runs the unit, process integration, Helm, container build and container smoke checks. It publishes linux/amd64 without moving `latest` or production version tags.

Deploy a separate Helm release in the monitoring namespace with an amd64 node selector, its own dedupe PVC, and a dedicated test topic. Use the existing ntfy credential by Secret reference. Forward only that test topic to an in-cluster HTTP sink with fake Pushover credentials. The sink verifies actual multipart requests, not just readiness. The production forwarder and its volume remain under ArgoCD control.

Acceptance checks:

1. The running canary image matches the published immutable revision/digest and has zero restarts.
2. The canary subscribes to the real in-cluster ntfy service and passes `/healthz`.
3. An authenticated test publication reaches the sink with the expected body, priority, sound, formatting, device, URL and image when supplied.
4. Low-priority events are dropped and replayed IDs are deduplicated.
5. A transient sink failure is retried without losing or reordering a message.
6. Recreating the canary pod preserves its dedupe file and returns healthy.
7. Record actual pod CPU/memory alongside the production baseline, with the sampling window and workload.
8. Remove test workloads and the canary-only PVC after recording results. Preserve the production release and PVC.

Production promotion is a separate GitOps change in the Home repository. Its current chart has replay-related settings in addition to the upstream forwarder configuration; review those contracts before replacing the production image.
