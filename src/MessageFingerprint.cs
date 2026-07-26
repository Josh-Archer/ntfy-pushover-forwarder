using System.Security.Cryptography;
using System.Text;
using NtfyPushoverForwarder.Models;

namespace NtfyPushoverForwarder;

public static class MessageFingerprint
{
    /// <summary>
    /// Prefer the stable ntfy message id when present so legitimate identical
    /// alerts (different ids) are not suppressed. Fall back to content hash.
    /// </summary>
    public static string Build(string topic, string title, string messageBody, string[] tags, NtfyMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Id))
        {
            return "id:" + message.Id.Trim();
        }

        var canonicalTags = string.Join(",", tags.OrderBy(tag => tag, StringComparer.Ordinal));
        var payload = string.Join(
            "\u001f",
            topic,
            title,
            messageBody,
            canonicalTags,
            message.Priority?.ToString() ?? string.Empty,
            message.Click ?? string.Empty);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return "hash:" + Convert.ToHexString(bytes);
    }
}
