using System.Diagnostics.Metrics;

namespace NtfyPushoverForwarder;

/// <summary>
/// OpenTelemetry-compatible meters for the forwarder (scraped via /metrics).
/// </summary>
public sealed class ForwarderMetrics
{
    public const string MeterName = "NtfyPushoverForwarder";

    private readonly Counter<long> _messagesReceived;
    private readonly Counter<long> _messagesForwarded;
    private readonly Counter<long> _messagesDroppedPriority;
    private readonly Counter<long> _messagesDroppedDedupe;
    private readonly Counter<long> _forwardFailures;
    private readonly Counter<long> _reconnects;
    private readonly Counter<long> _recoveryAlerts;

    public ForwarderMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _messagesReceived = meter.CreateCounter<long>("ntfy_forwarder_messages_received", description: "Messages received from ntfy");
        _messagesForwarded = meter.CreateCounter<long>("ntfy_forwarder_messages_forwarded", description: "Messages successfully handed to Pushover");
        _messagesDroppedPriority = meter.CreateCounter<long>("ntfy_forwarder_messages_dropped_priority", description: "Dropped below minimum priority");
        _messagesDroppedDedupe = meter.CreateCounter<long>("ntfy_forwarder_messages_dropped_dedupe", description: "Dropped as duplicates");
        _forwardFailures = meter.CreateCounter<long>("ntfy_forwarder_forward_failures", description: "Pushover forward failures");
        _reconnects = meter.CreateCounter<long>("ntfy_forwarder_sse_reconnects", description: "SSE reconnect attempts");
        _recoveryAlerts = meter.CreateCounter<long>("ntfy_forwarder_recovery_alerts", description: "Recovery alerts sent");
    }

    public void Received(string topic) => _messagesReceived.Add(1, new KeyValuePair<string, object?>("topic", topic));
    public void Forwarded(string topic) => _messagesForwarded.Add(1, new KeyValuePair<string, object?>("topic", topic));
    public void DroppedPriority(string topic) => _messagesDroppedPriority.Add(1, new KeyValuePair<string, object?>("topic", topic));
    public void DroppedDedupe(string topic) => _messagesDroppedDedupe.Add(1, new KeyValuePair<string, object?>("topic", topic));
    public void ForwardFailed(string topic) => _forwardFailures.Add(1, new KeyValuePair<string, object?>("topic", topic));
    public void Reconnect(string topic) => _reconnects.Add(1, new KeyValuePair<string, object?>("topic", topic));
    public void RecoveryAlert(string topic) => _recoveryAlerts.Add(1, new KeyValuePair<string, object?>("topic", topic));
}
