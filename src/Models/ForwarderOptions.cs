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
    public string[] Topics { get; set; } = Array.Empty<string>();
    
    public Dictionary<string, string> TopicTokens { get; set; } = new();
    public Dictionary<string, string> SoundMap { get; set; } = new();
    public Dictionary<string, string> LogoMap { get; set; } = new();
}
