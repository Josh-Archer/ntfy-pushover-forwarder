using NtfyPushoverForwarder.Models;

namespace NtfyPushoverForwarder;

public static class MessageFormatting
{
    /// <summary>
    /// Enable Pushover HTML when ntfy tags include html/markdown.
    /// </summary>
    public static bool ShouldUseHtml(NtfyMessage message, string? body = null)
    {
        var tags = message.Tags ?? Array.Empty<string>();
        return tags.Any(t => t.Equals("html", StringComparison.OrdinalIgnoreCase)
                          || t.Equals("markdown", StringComparison.OrdinalIgnoreCase)
                          || t.Equals("md", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Light markdown → HTML for Pushover when markdown tag is present.
    /// </summary>
    public static string ApplyFormatting(string body, NtfyMessage message)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }

        var tags = message.Tags ?? Array.Empty<string>();
        var wantsMd = tags.Any(t => t.Equals("markdown", StringComparison.OrdinalIgnoreCase)
                                    || t.Equals("md", StringComparison.OrdinalIgnoreCase));
        if (!wantsMd)
        {
            return body;
        }

        // Minimal: backticks → monospace; all text outside/inside code is HTML-encoded.
        var parts = body.Split('`');
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1)
            {
                sb.Append("<code>").Append(System.Net.WebUtility.HtmlEncode(parts[i])).Append("</code>");
            }
            else
            {
                sb.Append(System.Net.WebUtility.HtmlEncode(parts[i]));
            }
        }

        return sb.ToString();
    }
}
