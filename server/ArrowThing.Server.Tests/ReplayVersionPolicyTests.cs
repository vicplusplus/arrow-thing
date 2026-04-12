using ArrowThing.Server.Games;

namespace ArrowThing.Server.Tests;

public class ReplayVersionPolicyTests
{
    [Fact]
    public void IsAllowed_AtOrAboveMinimum_ReturnsTrue()
    {
        Assert.True(ReplayVersionPolicy.IsAllowed(ReplayVersionPolicy.MinReplayVersion));
        Assert.True(ReplayVersionPolicy.IsAllowed(ReplayVersionPolicy.MinReplayVersion + 1));
        Assert.True(ReplayVersionPolicy.IsAllowed(int.MaxValue));
    }

    [Fact]
    public void IsAllowed_BelowMinimum_ReturnsFalse()
    {
        Assert.False(ReplayVersionPolicy.IsAllowed(ReplayVersionPolicy.MinReplayVersion - 1));
        Assert.False(ReplayVersionPolicy.IsAllowed(1));
        Assert.False(ReplayVersionPolicy.IsAllowed(0));
        Assert.False(ReplayVersionPolicy.IsAllowed(-1));
    }

    [Fact]
    public void RejectionMessage_IncludesMinVersion_AndUpdateGuidance()
    {
        var message = ReplayVersionPolicy.RejectionMessage(1);
        Assert.Contains(ReplayVersionPolicy.MinReplayVersion.ToString(), message);
        Assert.Contains("update", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectionMessage_IncludesReportedVersion()
    {
        var message = ReplayVersionPolicy.RejectionMessage(2);
        Assert.Contains("2", message);
    }
}
