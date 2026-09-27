using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class DebugStopTrackerTests
{
    [Fact]
    public void RepeatedCaptureKeepsIdUntilDebuggerTransitions()
    {
        var stops = new DebugStopTracker();
        var first = stops.EnsureBreak();
        var revision = stops.Revision;

        Assert.Equal(first, stops.EnsureBreak());
        Assert.True(stops.Matches(first, revision));

        stops.Invalidate();
        Assert.False(stops.Matches(first, revision));
        Assert.Null(stops.SnapshotId);

        var second = stops.EnterBreak();
        Assert.NotEqual(first, second);
        Assert.True(stops.Matches(second, stops.Revision));
    }
}
