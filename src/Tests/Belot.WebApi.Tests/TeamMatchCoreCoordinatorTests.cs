namespace Belot.WebApi.Tests;

using Belot.Engine.Game;
using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

using Xunit;

public sealed class TeamMatchCoreCoordinatorTests
{
    [Fact]
    public void ProductionConstructorGeneratesSharedValidBoardsWithoutASeed()
    {
        var match = new TeamMatchCoreCoordinator(4);
        var board1 = match.GetBoardForAudit(1).ToArray();
        Assert.Equal(32, board1.Length);
        Assert.Equal(32, board1.Distinct().Count());
        AssertSameCurrentDeal(match, 1);

        PassOneBoard(match, 1);
        PassOneBoard(match, 2);
        var board2 = match.GetBoardForAudit(2).ToArray();
        Assert.Equal(32, board2.Length);
        Assert.Equal(32, board2.Distinct().Count());
        AssertSameCurrentDeal(match, 2);
    }

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

    [Fact]
    public void TwentyPlayedDealsIgnore151AndAllowDifferentBidsAtIdenticalTables()
    {
        var coordinator = new TeamMatchCoreCoordinator(20, 2026);
        var steps = 0;
        while (!coordinator.IsFinished && steps++ < 8000)
        {
            var progressed = false;
            for (var table = 1; table <= 2; table++)
            {
                var publicProgress = coordinator.GetSeatView(table, PlayerPosition.South);
                if (publicProgress.Decision == BelotDecision.None)
                {
                    continue; // This table is at the final shared boundary.
                }

                var seat = publicProgress.ToMove;
                var own = coordinator.GetSeatView(table, seat);
                BelotAction action;
                if (own.Decision == BelotDecision.Bid)
                {
                    // Different bidding decisions, but the same canonical deck.
                    var openingBid = table == 1 ? BidType.AllTrumps : BidType.NoTrumps;
                    action = BelotAction.Bid(own.Bids.Count == 0 ? openingBid : BidType.Pass);
                }
                else if (own.Decision == BelotDecision.Announce)
                {
                    action = BelotAction.Declare(null);
                }
                else
                {
                    Assert.NotEmpty(own.PlayableCards);
                    action = BelotAction.PlayCard(own.PlayableCards[0]);
                }

                Assert.Equal(BelotActResult.Ok, coordinator.Act(table, seat, action));
                progressed = true;
            }

            Assert.True(progressed || coordinator.IsFinished, "Both tables stalled before completing the match.");
        }

        Assert.True(coordinator.IsFinished, "The scheduled match did not finish within the safety action budget.");
        Assert.True(coordinator.AllowedDeals >= 20);
        Assert.Equal(coordinator.AllowedDeals, coordinator.GetRoundsPlayed(1));
        Assert.Equal(coordinator.AllowedDeals, coordinator.GetRoundsPlayed(2));

        var tableOne = coordinator.GetSeatView(1, PlayerPosition.South);
        var tableTwo = coordinator.GetSeatView(2, PlayerPosition.South);
        Assert.True(tableOne.SouthNorthPoints > 151 || tableOne.EastWestPoints > 151);
        Assert.True(tableTwo.SouthNorthPoints > 151 || tableTwo.EastWestPoints > 151);
        Assert.Equal(coordinator.AllowedDeals, tableOne.PreviousRounds.Count);
        Assert.Equal(coordinator.AllowedDeals, tableTwo.PreviousRounds.Count);
        Assert.Equal(0, coordinator.GetHangingPoints(1));
        Assert.Equal(0, coordinator.GetHangingPoints(2));
    }

    [Fact]
    public void AnActualPlayedHangingDealExtendsBothTablesAndResolvesViaFurtherPlay()
    {
        // Search a bounded, deterministic seed range for an actual 130:130
        // No Trumps round that produces hanging via the REAL engine ScoreManager.
        // The first three deals are deliberately fully passed at both tables.
        TeamMatchCoreCoordinator? chosen = null;
        var hangingSeed = -1;
        for (var seed = 0; seed < 1200; seed++)
        {
            var match = new TeamMatchCoreCoordinator(4, seed);
            for (var board = 1; board <= 3; board++)
            {
                PassOneBoard(match, 1);
                PassOneBoard(match, 2);
            }

            PlayOneBoard(match, 1, BidType.NoTrumps);
            if (match.GetHangingPoints(1) == 0)
            {
                continue;
            }

            chosen = match;
            hangingSeed = seed;
            break;
        }

        Assert.True(chosen != null, "No naturally hanging No Trumps deal found in deterministic seed range.");
        var coordinator = chosen!;
        Assert.True(hangingSeed >= 0);
        Assert.Equal(4, coordinator.GetRoundsPlayed(1));
        Assert.True(coordinator.GetHangingPoints(1) > 0);
        Assert.Equal(4, coordinator.AllowedDeals);
        Assert.False(coordinator.IsFinished);

        // The second table passed: its fourth deal is 0:0. Both advance
        // together because only Table 1 has hanging. Same fifth deck and dealer.
        PassOneBoard(coordinator, 2);
        Assert.Equal(5, coordinator.AllowedDeals);
        Assert.False(coordinator.IsFinished);
        Assert.Equal(4, coordinator.GetRoundsPlayed(2));
        Assert.Equal(5, coordinator.GetSeatView(1, PlayerPosition.South).RoundNumber);
        Assert.Equal(5, coordinator.GetSeatView(2, PlayerPosition.South).RoundNumber);
        Assert.Equal(0, coordinator.GetHangingPoints(2));
        AssertSameCurrentDeal(coordinator, 5);

        // Play true scored extra deals until both tables' hanging points clear;
        // if either hangs again, the product rule extends BOTH tables once more.
        var extra = 0;
        while (!coordinator.IsFinished && extra++ < 15)
        {
            var previousAllowed = coordinator.AllowedDeals;
            PlayOneBoard(coordinator, 1, BidType.NoTrumps);
            PassOneBoard(coordinator, 2);
            if (!coordinator.IsFinished)
            {
                Assert.Equal(previousAllowed + 1, coordinator.AllowedDeals);
                AssertSameCurrentDeal(coordinator, previousAllowed + 1);
            }
        }

        Assert.True(coordinator.IsFinished, "Real hanging points did not resolve within extra-deal guard.");
        Assert.True(coordinator.AllowedDeals > coordinator.ScheduledDeals);
        Assert.Equal(coordinator.AllowedDeals, coordinator.GetRoundsPlayed(1));
        Assert.Equal(coordinator.AllowedDeals, coordinator.GetRoundsPlayed(2));
        Assert.Equal(0, coordinator.GetHangingPoints(1));
        Assert.Equal(0, coordinator.GetHangingPoints(2));
    }

    private static void PlayOneBoard(TeamMatchCoreCoordinator match, int table, BidType contract)
    {
        var before = match.GetRoundsPlayed(table);
        var guard = 0;
        while (match.GetRoundsPlayed(table) == before)
        {
            Assert.True(++guard < 100, "A full scored deal exceeded the decision safety budget.");
            var publicView = match.GetSeatView(table, PlayerPosition.South);
            var seat = publicView.ToMove;
            Assert.NotEqual(PlayerPosition.Unknown, seat);
            var view = match.GetSeatView(table, seat);
            BelotAction action;
            if (view.Decision == BelotDecision.Bid)
            {
                action = BelotAction.Bid(view.Bids.Count == 0 ? contract : BidType.Pass);
            }
            else if (view.Decision == BelotDecision.Announce)
            {
                action = BelotAction.Declare(null);
            }
            else
            {
                Assert.Equal(BelotDecision.PlayCard, view.Decision);
                Assert.NotEmpty(view.PlayableCards);
                action = BelotAction.PlayCard(view.PlayableCards[0]);
            }

            Assert.Equal(BelotActResult.Ok, match.Act(table, seat, action));
        }

        Assert.Equal(before + 1, match.GetRoundsPlayed(table));
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
