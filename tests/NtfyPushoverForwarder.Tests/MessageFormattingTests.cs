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

    [Fact]
    public void AngleBracketsInBodyWithoutTags_DoNotEnableHtml()
    {
        var msg = new NtfyMessage { Tags = [] };
        var body = "Disk usage < 10% and > 90%";
        var formatted = MessageFormatting.ApplyFormatting(body, msg);

        Assert.Equal("Disk usage < 10% and > 90%", formatted);
        Assert.False(MessageFormatting.ShouldUseHtml(msg, formatted));
    }

    [Fact]
    public void MarkdownMode_EscapesAngleBrackets_WithoutBackticks()
    {
        var msg = new NtfyMessage { Tags = ["markdown"] };
        var body = "Disk usage < 10% and > 90% & alert";
        var formatted = MessageFormatting.ApplyFormatting(body, msg);

        Assert.Equal("Disk usage &lt; 10% and &gt; 90% &amp; alert", formatted);
        Assert.True(MessageFormatting.ShouldUseHtml(msg, formatted));
    }

    [Fact]
    public void MarkdownMode_EscapesMarkupBothInsideAndOutsideBackticks()
    {
        var msg = new NtfyMessage { Tags = ["markdown"] };
        var body = "Alert <system>: run `cat <file> | grep 'test'` now";
        var formatted = MessageFormatting.ApplyFormatting(body, msg);

        Assert.Equal("Alert &lt;system&gt;: run <code>cat &lt;file&gt; | grep &#39;test&#39;</code> now", formatted);
        Assert.True(MessageFormatting.ShouldUseHtml(msg, formatted));
    }

    [Fact]
    public void HtmlTag_PreservesRawHtml()
    {
        var msg = new NtfyMessage { Tags = ["html"] };
        var body = "<b>Bold</b> <a href=\"https://example.com\">link</a>";
        var formatted = MessageFormatting.ApplyFormatting(body, msg);

        Assert.Equal(body, formatted);
        Assert.True(MessageFormatting.ShouldUseHtml(msg, formatted));
    }

    [Fact]
    public void MdTag_EnablesHtmlAndEscapesMarkup()
    {
        var msg = new NtfyMessage { Tags = ["md"] };
        var body = "Error in <Component>";
        var formatted = MessageFormatting.ApplyFormatting(body, msg);

        Assert.Equal("Error in &lt;Component&gt;", formatted);
        Assert.True(MessageFormatting.ShouldUseHtml(msg, formatted));
    }

    [Fact]
    public void EmptyOrNullBody_HandledGracefully()
    {
        var msg = new NtfyMessage { Tags = ["markdown"] };
        Assert.Equal("", MessageFormatting.ApplyFormatting("", msg));
        Assert.Null(MessageFormatting.ApplyFormatting(null!, msg));
    }
}
