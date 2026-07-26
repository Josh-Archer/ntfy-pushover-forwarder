using NtfyPushoverForwarder;

namespace NtfyPushoverForwarder.Tests;

public class PriorityMapperTests
{
    [Theory]
    [InlineData(null, 3)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    public void ResolveNtfyPriority_UsesDefaultForNull(int? input, int expected)
        => Assert.Equal(expected, PriorityMapper.ResolveNtfyPriority(input));

    [Theory]
    [InlineData(null, 4, false)] // default 3 < 4
    [InlineData(null, 3, true)]
    [InlineData(null, 1, true)]
    [InlineData(5, 4, true)]
    [InlineData(2, 4, false)]
    public void MeetsMinimum_HandlesNullAsDefault(int? priority, int minimum, bool expected)
        => Assert.Equal(expected, PriorityMapper.MeetsMinimum(priority, minimum));

    [Theory]
    [InlineData(1, -2)]
    [InlineData(2, -1)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(null, 0)] // default ntfy 3 → pushover 0
    [InlineData(0, -2)]
    [InlineData(99, 2)]
    public void ToPushover_FullMap(int? ntfy, int pushover)
        => Assert.Equal(pushover, PriorityMapper.ToPushover(ntfy));
}
