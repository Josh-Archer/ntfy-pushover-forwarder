using NtfyPushoverForwarder;
using NtfyPushoverForwarder.Models;

namespace NtfyPushoverForwarder.Tests;

public class MessageFingerprintTests
{
    [Fact]
    public void PrefersMessageIdWhenPresent()
    {
        var msg = new NtfyMessage { Id = "abc123", Priority = 3 };
        var fp = MessageFingerprint.Build("t", "title", "body", ["a"], msg);
        Assert.Equal("id:abc123", fp);
    }

    [Fact]
    public void SameBodyDifferentIdsAreNotEqual()
    {
        var a = MessageFingerprint.Build("t", "title", "body", [], new NtfyMessage { Id = "1" });
        var b = MessageFingerprint.Build("t", "title", "body", [], new NtfyMessage { Id = "2" });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void FallsBackToContentHashWithoutId()
    {
        var msg = new NtfyMessage { Id = "", Priority = 3, Click = "http://x" };
        var fp = MessageFingerprint.Build("t", "title", "body", ["z", "a"], msg);
        Assert.StartsWith("hash:", fp);

        var same = MessageFingerprint.Build("t", "title", "body", ["a", "z"], msg);
        Assert.Equal(fp, same);
    }
}
