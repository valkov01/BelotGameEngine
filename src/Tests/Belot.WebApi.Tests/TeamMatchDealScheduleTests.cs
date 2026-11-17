namespace Belot.WebApi.Tests;

using Xunit;

public sealed class TeamMatchDealScheduleTests
{
    [Fact]
    public void RepeatsExtraDealsUntilHangingClearsAtBothTables()
    {
        var schedule = new TeamMatchDealSchedule(4);
        Assert.Equal(4, schedule.ScheduledDeals);
        Assert.Equal(4, schedule.AllowedDeals);

        // Table 1 is already waiting at configured 4/4, but Table 2 is still playing.
        Assert.Equal(TeamMatchBoundaryDecision.Waiting, schedule.Resolve(4, 3, 5, 0));
        Assert.Equal(4, schedule.AllowedDeals);
        Assert.False(schedule.IsFinished);

        // Only table 1 has hanging => BOTH tables get exactly the same board 5.
        Assert.Equal(TeamMatchBoundaryDecision.Extend, schedule.Resolve(4, 4, 5, 0));
        Assert.Equal(5, schedule.AllowedDeals);
        Assert.Equal(4, schedule.ScheduledDeals);
        Assert.False(schedule.IsFinished);

        // Extra board 5 creates hanging points at the other table, another
        // identical shared board 6 is required (no extra-deal hard cap).
        Assert.Equal(TeamMatchBoundaryDecision.Waiting, schedule.Resolve(5, 4, 0, 2));
        Assert.Equal(TeamMatchBoundaryDecision.Extend, schedule.Resolve(5, 5, 0, 2));
        Assert.Equal(6, schedule.AllowedDeals);
        Assert.False(schedule.IsFinished);

        // Both tables are now clear after their shared board 6.
        Assert.Equal(TeamMatchBoundaryDecision.Complete, schedule.Resolve(6, 6, 0, 0));
        Assert.True(schedule.IsFinished);
        Assert.Equal(6, schedule.AllowedDeals);
        Assert.Equal(TeamMatchBoundaryDecision.Complete, schedule.Resolve(6, 6, 0, 0));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(10)]
    [InlineData(20)]
    public void AtConfiguredEndWithoutHangingBothTablesFinishNormally(int scheduled)
    {
        var schedule = new TeamMatchDealSchedule(scheduled);
        Assert.Equal(
            TeamMatchBoundaryDecision.Complete,
            schedule.Resolve(scheduled, scheduled, 0, 0));
        Assert.True(schedule.IsFinished);
        Assert.Equal(scheduled, schedule.AllowedDeals);
    }

    [Fact]
    public void CannotResolveAnImpossibleProgressOrNegativeHangingState()
    {
        var schedule = new TeamMatchDealSchedule(4);
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.Resolve(5, 4, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.Resolve(4, 4, -1, 0));
        Assert.False(schedule.IsFinished);
        Assert.Equal(4, schedule.AllowedDeals);
    }
}
