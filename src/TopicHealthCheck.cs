using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NtfyPushoverForwarder.Models;

namespace NtfyPushoverForwarder;

/// <summary>
/// Health check that reports Unhealthy if all configured topic loops have stopped/disconnected,
/// Degraded if some topic loops have stopped, and Healthy if all topic loops are active.
/// </summary>
public sealed class TopicHealthCheck : IHealthCheck
{
    private readonly TopicConnectionTracker _tracker;
    private readonly IOptions<ForwarderOptions> _options;

    public TopicHealthCheck(TopicConnectionTracker tracker, IOptions<ForwarderOptions> options)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var configured = _options.Value?.Topics?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToArray() ?? Array.Empty<string>();

        if (configured.Length == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy("No topic loops configured"));
        }

        var active = new List<string>();
        var disconnected = new List<string>();

        foreach (var topic in configured)
        {
            if (_tracker.IsConnected(topic))
            {
                active.Add(topic);
            }
            else
            {
                disconnected.Add(topic);
            }
        }

        var data = new Dictionary<string, object>
        {
            ["configured"] = configured,
            ["active"] = active.ToArray(),
            ["disconnected"] = disconnected.ToArray()
        };

        if (active.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"0 of {configured.Length} topic loops active (disconnected: {string.Join(", ", disconnected)})",
                data: data));
        }

        if (disconnected.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"{active.Count} of {configured.Length} topic loops active (disconnected: {string.Join(", ", disconnected)})",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"All {configured.Length} topic loops active",
            data: data));
    }
}
