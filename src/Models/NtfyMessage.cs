using System.Text.Json.Serialization;

namespace NtfyPushoverForwarder.Models;

public class NtfyMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("time")]
    public long Time { get; set; }

    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("priority")]
    public int? Priority { get; set; }

    [JsonPropertyName("tags")]
    public string[]? Tags { get; set; }

    [JsonPropertyName("click")]
    public string? Click { get; set; }

    [JsonPropertyName("attachment")]
    public NtfyAttachment? Attachment { get; set; }
}

public class NtfyAttachment
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("expires")]
    public long? Expires { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
