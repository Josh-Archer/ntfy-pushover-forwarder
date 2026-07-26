using NtfyPushoverForwarder;

namespace NtfyPushoverForwarder.Tests;

public class SseReconnectPolicyTests
{
    [Fact]
    public void DelaysIncreaseUntilCap()
    {
        var policy = new SseReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));
        var d1 = policy.NextDelay().TotalSeconds;
        var d2 = policy.NextDelay().TotalSeconds;
        var d3 = policy.NextDelay().TotalSeconds;
        Assert.True(d1 >= 1 && d1 < 2.5);
        Assert.True(d2 >= 2 && d2 < 4.5);
        Assert.True(d3 >= 4 && d3 <= 10);
        policy.Reset();
        Assert.Equal(0, policy.Attempt);
    }

    [Fact]
    public void BuildSubscribeUrl_WithAndWithoutSince()
    {
        Assert.Equal(
            "http://ntfy/topic/json",
            SseReconnectPolicy.BuildSubscribeUrl("http://ntfy/", "topic", null));
        Assert.Equal(
            "http://ntfy/topic/json?since=msg1",
            SseReconnectPolicy.BuildSubscribeUrl("http://ntfy", "topic", "msg1"));
    }
}
