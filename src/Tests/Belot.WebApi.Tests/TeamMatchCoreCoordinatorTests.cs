namespace Belot.WebApi.Tests;

using Belot.Engine.Game;
using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

using Xunit;

public sealed class TeamMatchCoreCoordinatorTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(21)]
    public void OnlyFourToTwentyScheduledDealsAreAccepted(int scheduledDeals)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TeamMatchCoreCoordinator(scheduledDeals, 25));
    }

    [Fact]
    public void BoardsGiveBothTablesExactlyTheSameAbsoluteHandsAndDealer()
    {
        var coordinator = new TeamMatchCoreCoordinator(4, 715, PlayerPosition.West);
        AssertSameCurrentDeal(coordinator, 1);
        var starting = coordinator.GetSeatView(1, PlayerPosition.South);
        Assert.Equal(PlayerPosition.West, starting.FirstToPlayInTheRound);

        PassOneBoard(coordinator, 1);
        Assert.Equal(2, coordinator.GetSeatView(1, PlayerPosition.South).RoundNumber);
        Assert.Equal(1, coordinator.GetSeatView(2, PlayerPosition.South).RoundNumber);

        PassOneBoard(coordinator, 2);
        AssertSameCurrentDeal(coordinator, 2);
        Assert.Equal(PlayerPosition.South, coordinator.GetSeatView(2, PlayerPosition.South).FirstToPlayInTheRound);

        // A third board can be prepared and played at table 1 while table 2 stays on board 2.
        PassOneBoard(coordinator, 1);
        Assert.Equal(3, coordinator.GetSeatView(1, PlayerPosition.South).RoundNumber);
        Assert.Equal(2, coordinator.GetSeatView(2, PlayerPosition.South).RoundNumber);
        PassOneBoard(coordinator, 2);
        AssertSameCurrentDeal(coordinator, 3);
    }

    [Fact]
    public void TablesProgressIndependentlyUntilSharedFinalBoundary()
    {
        var coordinator = new TeamMatchCoreCoordinator(4, 916);
        for (var round = 1; round <= 4; round++)
        {
            PassOneBoard(coordinator, 1);
        }

        Assert.True(coordinator.IsAwaitingSharedBoundary(1));
        Assert.False(coordinator.IsFinished);
        Assert.Equal(4, coordinator.GetRoundsPlayed(1));
        Assert.Equal(0, coordinator.GetRoundsPlayed(2));
        Assert.Equal(BelotDecision.None, coordinator.GetSeatView(1, PlayerPosition.South).Decision);
        Assert.Equal(
            BelotActResult.NotYourTurn,
            coordinator.Act(1, PlayerPosition.South, BelotAction.Bid(BidType.Pass)));

        for (var round = 1; round <= 4; round++)
        {
            PassOneBoard(coordinator, 2);
        }

        Assert.True(coordinator.IsFinished);
        Assert.Equal(4, coordinator.AllowedDeals);
        Assert.Equal(4, coordinator.GetRoundsPlayed(2));
        Assert.Equal(0, coordinator.GetHangingPoints(1));
        Assert.Equal(0, coordinator.GetHangingPoints(2));
        Assert.True(coordinator.GetSeatView(1, PlayerPosition.South).IsMatchFinished);
        Assert.True(coordinator.GetSeatView(2, PlayerPosition.South).IsMatchFinished);
        Assert.Equal(
            BelotActResult.MatchFinished,
            coordinator.Act(1, PlayerPosition.South, BelotAction.Bid(BidType.Pass)));
    }

    [Fact]
    public void SharedBoardIsKeptPrivateAndAuditCopyCannotModifyNextTable()
    {
        var coordinator = new TeamMatchCoreCoordinator(4, 405);
        var first = coordinator.GetBoardForAudit(1).ToArray();
        Assert.Equal(32, first.Distinct().Count());

        var copy = coordinator.GetBoardForAudit(1).ToArray();
        copy[0] = copy[1];

        Assert.Equal(first, coordinator.GetBoardForAudit(1));
        Assert.Equal(5, coordinator.GetSeatView(1, PlayerPosition.South).Hand.Count);
        Assert.Equal(5, coordinator.GetSeatView(2, PlayerPosition.South).Hand.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.GetSeatView(3, PlayerPosition.South));
    }

    private static void AssertSameCurrentDeal(TeamMatchCoreCoordinator coordinator, int round)
    {
        var seats = new[]
        {
            PlayerPosition.South, PlayerPosition.East, PlayerPosition.North, PlayerPosition.West,
        };

        foreach (var seat in seats)
        {
            var one = coordinator.GetSeatView(1, seat);
            var two = coordinator.GetSeatView(2, seat);
            Assert.Equal(round, one.RoundNumber);
            Assert.Equal(round, two.RoundNumber);
            Assert.Equal(one.FirstToPlayInTheRound, two.FirstToPlayInTheRound);
            Assert.Equal(one.Hand, two.Hand);
            Assert.Equal(5, one.Hand.Count);
        }
    }

    private static void PassOneBoard(TeamMatchCoreCoordinator coordinator, int table)
    {
        var before = coordinator.GetRoundsPlayed(table);
        for (var i = 0; i < 4; i++)
        {
            var snapshot = coordinator.GetSeatView(table, PlayerPosition.South);
            Assert.Equal(BelotDecision.Bid, snapshot.Decision);
            Assert.Equal(BelotActResult.Ok, coordinator.Act(table, snapshot.ToMove, BelotAction.Bid(BidType.Pass)));
        }

        Assert.Equal(before + 1, coordinator.GetRoundsPlayed(table));
    }
}
