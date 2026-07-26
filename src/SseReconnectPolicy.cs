namespace NtfyPushoverForwarder;

/// <summary>
/// Exponential backoff with jitter for ntfy SSE reconnects.
/// </summary>
public sealed class SseReconnectPolicy
{
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly Random _random = new();
    private int _attempt;

    public SseReconnectPolicy(TimeSpan? initialDelay = null, TimeSpan? maxDelay = null)
    {
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(1);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(60);
    }

    public int Attempt => _attempt;

    public void Reset() => _attempt = 0;

    public TimeSpan NextDelay()
    {
        // 1, 2, 4, 8... capped at max, plus up to 20% jitter
        var exp = Math.Min(_attempt, 16);
        var baseMs = _initialDelay.TotalMilliseconds * Math.Pow(2, exp);
        baseMs = Math.Min(baseMs, _maxDelay.TotalMilliseconds);
        var jitter = baseMs * 0.2 * _random.NextDouble();
        _attempt++;
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }

    /// <summary>
    /// Build ntfy subscribe URL with optional since resume cursor.
    /// ntfy accepts message id or relative duration (e.g. 10m).
    /// </summary>
    public static string BuildSubscribeUrl(string ntfyUrl, string topic, string? sinceCursor)
    {
        var baseUrl = $"{ntfyUrl.TrimEnd('/')}/{topic}/json";
        if (string.IsNullOrWhiteSpace(sinceCursor))
        {
            return baseUrl;
        }

        return $"{baseUrl}?since={Uri.EscapeDataString(sinceCursor)}";
    }
}
