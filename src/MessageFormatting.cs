using NtfyPushoverForwarder.Models;

namespace NtfyPushoverForwarder;

public static class MessageFormatting
{
    /// <summary>
    /// Enable Pushover HTML when ntfy tags include html/markdown or body looks like HTML.
    /// </summary>
    public static bool ShouldUseHtml(NtfyMessage message, string body)
    {
        var tags = message.Tags ?? Array.Empty<string>();
        if (tags.Any(t => t.Equals("html", StringComparison.OrdinalIgnoreCase)
                          || t.Equals("markdown", StringComparison.OrdinalIgnoreCase)
                          || t.Equals("md", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return body.Contains('<') && body.Contains('>');
    }

    /// <summary>
    /// Light markdown → HTML for Pushover when markdown tag is present.
    /// </summary>
    public static string ApplyFormatting(string body, NtfyMessage message)
    {
        var tags = message.Tags ?? Array.Empty<string>();
        var wantsMd = tags.Any(t => t.Equals("markdown", StringComparison.OrdinalIgnoreCase)
                                    || t.Equals("md", StringComparison.OrdinalIgnoreCase));
        if (!wantsMd)
        {
            return body;
        }

        // Minimal: backticks → monospace
        var parts = body.Split('`');
        if (parts.Length < 2)
        {
            return body;
        }

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
