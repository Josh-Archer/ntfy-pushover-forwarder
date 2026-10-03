namespace NtfyPushoverForwarder.Tests;

public class TopicConnectionTrackerTests
{
    [Fact]
    public void IsConnected_ReturnsFalseForUnknownOrDisconnectedTopic()
    {
        var tracker = new TopicConnectionTracker();
        Assert.False(tracker.IsConnected("unknown"));

        tracker.GetOrCreate("topic1");
        Assert.False(tracker.IsConnected("topic1"));

        tracker.MarkConnected("topic1");
        Assert.True(tracker.IsConnected("topic1"));

        tracker.MarkDisconnected("topic1");
        Assert.False(tracker.IsConnected("topic1"));
    }

    [Fact]
    public void GetStatus_ReturnsCorrectActiveAndTotalCounts()
    {
        var tracker = new TopicConnectionTracker();
        tracker.MarkConnected("topic1");

        var (active, total) = tracker.GetStatus(["topic1", "topic2", "topic3"]);
        Assert.Equal(1, active);
        Assert.Equal(3, total);

        tracker.MarkConnected("topic2");
        (active, total) = tracker.GetStatus(["topic1", "topic2", "topic3"]);
        Assert.Equal(2, active);
        Assert.Equal(3, total);
    }

    [Fact]
    public void GetAllStates_ReturnsSnapshotOfAllTrackedTopics()
    {
        var tracker = new TopicConnectionTracker();
        tracker.MarkConnected("topic1");
        tracker.MarkDisconnected("topic2");

        var states = tracker.GetAllStates();
        Assert.Equal(2, states.Count);
        Assert.True(states["topic1"].Connected);
        Assert.False(states["topic2"].Connected);
    }
}
