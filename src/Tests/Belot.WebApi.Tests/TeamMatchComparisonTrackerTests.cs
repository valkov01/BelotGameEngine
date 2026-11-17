namespace Belot.WebApi.Tests;

using Xunit;

public sealed class TeamMatchComparisonTrackerTests
{
    [Fact]
    public void FounderTwentySixAndEighteenEightExampleScoresTwentyEightToTwentyFour()
    {
        var scoring = new TeamMatchComparisonTracker();
        scoring.RecordPair(
            1,
            new TeamMatchTableDealPoints(20, 6),
            new TeamMatchTableDealPoints(18, 8));

        var result = scoring.Project(barometerEnabled: true, matchComplete: false, scheduledDealCount: 4);
        var board = Assert.Single(result.ComparedDeals);
        Assert.Equal(20, board.Table1SouthNorth);
        Assert.Equal(6, board.Table1EastWest);
        Assert.Equal(18, board.Table2SouthNorth);
        Assert.Equal(8, board.Table2EastWest);
        Assert.Equal(28, board.Team1DealPoints);
        Assert.Equal(24, board.Team2DealPoints);
        Assert.Equal(28, result.Team1TotalPoints);
        Assert.Equal(24, result.Team2TotalPoints);
        Assert.Equal(4, result.Team1Margin);
    }

    [Fact]
    public void FounderBarometerExampleScoresThirtyTwoToTwentyAndAccumulatesByBoard()
    {
        var scoring = new TeamMatchComparisonTracker();
        scoring.RecordPair(
            1,
            new TeamMatchTableDealPoints(20, 6),
            new TeamMatchTableDealPoints(14, 12));
        scoring.RecordPair(
            2,
            new TeamMatchTableDealPoints(0, 0),
            new TeamMatchTableDealPoints(8, 18));

        var result = scoring.Project(true, false, 4);
        Assert.Equal(32, result.ComparedDeals[0].Team1DealPoints);
        Assert.Equal(20, result.ComparedDeals[0].Team2DealPoints);
        Assert.Equal(18, result.ComparedDeals[1].Team1DealPoints);
        Assert.Equal(8, result.ComparedDeals[1].Team2DealPoints);
        Assert.Equal(50, result.Team1TotalPoints);
        Assert.Equal(28, result.Team2TotalPoints);
        Assert.Equal(22, result.Team1Margin);
        Assert.Equal(2, scoring.PairedDealCount);
    }

    [Fact]
    public void PassedTableContributesZeroAndOtherTableFollowsItsAbsoluteSeatMapping()
    {
        var scoring = new TeamMatchComparisonTracker();
        scoring.RecordPair(
            1,
            new TeamMatchTableDealPoints(0, 0),
            new TeamMatchTableDealPoints(8, 18));
        var result = Assert.Single(scoring.Project(true, false, 4).ComparedDeals);
        Assert.Equal(18, result.Team1DealPoints); // Table 2 EW belongs to Team 1
        Assert.Equal(8, result.Team2DealPoints);  // Table 2 NS belongs to Team 2
        Assert.Equal(10, result.Team1CumulativeMargin);
    }

    [Fact]
    public void BarometerOffHidesEveryComparisonUntilTrueMatchCompletion()
    {
        var scoring = new TeamMatchComparisonTracker();
        scoring.RecordPair(1, new TeamMatchTableDealPoints(20, 6), new TeamMatchTableDealPoints(18, 8));
        var hidden = scoring.Project(false, false, 4);
        Assert.False(hidden.BarometerEnabled);
        Assert.False(hidden.IsMatchComplete);
        Assert.Null(hidden.Team1TotalPoints);
        Assert.Null(hidden.Team2TotalPoints);
        Assert.Null(hidden.Team1Margin);
        Assert.Empty(hidden.ComparedDeals);
        Assert.Equal(4, hidden.ScheduledDealCount);

        var final = scoring.Project(false, true, 4);
        Assert.True(final.IsMatchComplete);
        Assert.Equal(28, final.Team1TotalPoints);
        Assert.Equal(24, final.Team2TotalPoints);
        Assert.Single(final.ComparedDeals);
    }

    [Fact]
    public void OutOfOrderOrDuplicateBoardCannotDoubleCountAndSnapshotsCannotMutateStoredResults()
    {
        var scoring = new TeamMatchComparisonTracker();
        scoring.RecordPair(1, new TeamMatchTableDealPoints(20, 6), new TeamMatchTableDealPoints(18, 8));
        Assert.Throws<InvalidOperationException>(() =>
            scoring.RecordPair(1, new TeamMatchTableDealPoints(20, 6), new TeamMatchTableDealPoints(18, 8)));
        Assert.Throws<InvalidOperationException>(() =>
            scoring.RecordPair(3, new TeamMatchTableDealPoints(0, 0), new TeamMatchTableDealPoints(0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            scoring.RecordPair(2, new TeamMatchTableDealPoints(-1, 0), new TeamMatchTableDealPoints(0, 0)));

        var snapshot = scoring.Project(true, false, 4);
        var copiedRows = Assert.IsType<TeamMatchComparedDeal[]>(snapshot.ComparedDeals);
        copiedRows[0] = new TeamMatchComparedDeal(1, 999, 999, 999, 999, 999, 999, 999, 999, 0);

        var after = scoring.Project(true, false, 4);
        Assert.Equal(28, Assert.Single(after.ComparedDeals).Team1DealPoints);
        Assert.Equal(1, scoring.PairedDealCount);
    }

    [Fact]
    public void NoPairedDealsMeansZeroVisibleTotalOnlyWithBarometerOn()
    {
        var scoring = new TeamMatchComparisonTracker();
        Assert.Equal(0, scoring.Project(true, false, 4).Team1TotalPoints);
        Assert.Empty(scoring.Project(true, false, 4).ComparedDeals);
        Assert.Null(scoring.Project(false, false, 4).Team1TotalPoints);
        Assert.Empty(scoring.Project(false, false, 4).ComparedDeals);
    }
}
