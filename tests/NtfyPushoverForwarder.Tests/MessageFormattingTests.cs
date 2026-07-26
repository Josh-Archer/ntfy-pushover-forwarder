using NtfyPushoverForwarder;
using NtfyPushoverForwarder.Models;

namespace NtfyPushoverForwarder.Tests;

public class MessageFormattingTests
{
    [Fact]
    public void HtmlTagEnablesHtml()
    {
        var msg = new NtfyMessage { Tags = ["html"] };
        Assert.True(MessageFormatting.ShouldUseHtml(msg, "plain"));
    }

    [Fact]
    public void MarkdownConvertsBackticks()
    {
        var msg = new NtfyMessage { Tags = ["markdown"] };
        var result = MessageFormatting.ApplyFormatting("use `code` here", msg);
        Assert.Contains("<code>code</code>", result);
        Assert.True(MessageFormatting.ShouldUseHtml(msg, result));
    }
}
