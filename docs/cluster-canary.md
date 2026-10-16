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

## Reproduce the fixture

These commands target the Home cluster's `monitoring` namespace and existing `ntfy-secret`. The sample values pin the image tested on 2026-10-16. Update the immutable tag/digest for subsequent builds.

```bash
kubectl -n monitoring create configmap ntfy-rust-canary-sink \
  --from-file=sink.py=scripts/cluster_sink.py --dry-run=client -o yaml | kubectl apply -f -
kubectl apply -f tests/cluster/sink.yaml
# Grant only the isolated test topic; the existing credential is otherwise read-only on it.
kubectl -n monitoring exec deploy/ntfy -- \
  ntfy access backup-service rust-canary-20261016-66ea3c2 rw
helm upgrade --install ntfy-rust-canary charts/ntfy-pushover-forwarder \
  -n monitoring -f tests/cluster/values.yaml --post-renderer scripts/render_cluster_canary.py
kubectl -n monitoring rollout status deploy/ntfy-rust-canary
```

Run the following port-forwards in separate terminals, then run the probe:

```bash
kubectl -n monitoring port-forward --address 127.0.0.1 service/ntfy-rust-canary-sink 19880:8080
kubectl -n monitoring port-forward --address 127.0.0.1 service/ntfy-rust-canary 19881:8080
python3 scripts/cluster_probe.py
```

The post-renderer strips all real Pushover Secret references, supplies fake user/app fields, and forces the Pushover endpoint to the sink. It also sets a read-only root filesystem. The sink keeps the ntfy token inside its pod environment and accepts publications only for the fixed test topic. The probe requires a fresh sink and saves redacted evidence under `/tmp`.

For the persistence check, publish a successful event titled `rust-canary-persisted`, confirm its ID in `/data/dedupe.json`, replace the canary pod, and check the file again. Temporarily point `forwarder.ntfyUrl` at `http://ntfy-rust-canary-sink.monitoring.svc.cluster.local:8080/replay` to replay that actual cached ntfy event. Disable canary probes during this finite-stream fixture, then verify the dedupe counter increments without another sink post. Restore the original values afterward.

Clean up only the named test resources, including the test-only PVC:

```bash
helm uninstall ntfy-rust-canary -n monitoring
kubectl -n monitoring delete pvc ntfy-rust-canary-dedupe --ignore-not-found
kubectl delete -f tests/cluster/sink.yaml
kubectl -n monitoring delete configmap ntfy-rust-canary-sink
kubectl -n monitoring exec deploy/ntfy -- \
  ntfy access --reset backup-service rust-canary-20261016-66ea3c2
```

The existing production release and its PVC must remain untouched. See [the recorded cluster result](cluster-validation-2026-10-16.md) for observed behavior and limitations.
