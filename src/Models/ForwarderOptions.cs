namespace NtfyPushoverForwarder.Models;

public class ForwarderOptions
{
    public string NtfyUrl { get; set; } = string.Empty;
    public string NtfyToken { get; set; } = string.Empty;
    public string PushoverUrl { get; set; } = "https://api.pushover.net/1/messages.json";
    public string PushoverUserKey { get; set; } = string.Empty;
    public string PushoverDefaultToken { get; set; } = string.Empty;
    public int MinimumPriority { get; set; } = 1;
    public int DeduplicationWindowSeconds { get; set; } = 300;
    public int DeduplicationMaxEntries { get; set; } = 1024;

    /// <summary>
    /// When set, dedupe fingerprints are persisted to this file path across restarts.
    /// Empty disables durable storage (in-memory only).
    /// </summary>
    public string DeduplicationStorePath { get; set; } = string.Empty;

    /// <summary>
    /// Send a recovery Pushover notification after SSE reconnect following an outage.
    /// </summary>
    public bool SendRecoveryAlerts { get; set; } = true;

    /// <summary>
    /// HTTP listen port for /healthz and /metrics. 0 disables the HTTP surface.
    /// </summary>
    public int HttpPort { get; set; } = 8080;

    public string[] Topics { get; set; } = Array.Empty<string>();

    public Dictionary<string, string> TopicTokens { get; set; } = new();
    public Dictionary<string, string> SoundMap { get; set; } = new();
    public Dictionary<string, string> LogoMap { get; set; } = new();
}
