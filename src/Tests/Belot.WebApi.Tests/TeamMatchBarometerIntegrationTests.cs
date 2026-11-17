namespace Belot.WebApi.Tests;

using Belot.Engine.Game;
using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

using Xunit;

public sealed class TeamMatchBarometerIntegrationTests
{
    [Fact]
    public void BarometerOnPairsOnlyTheSameFinishedDealEvenWhenOneTableIsAhead()
    {
        var game = new TeamMatchCoreCoordinator(4, 154);
        PlayOneDeal(game, 1, BidType.NoTrumps);

        // Table 1 has scored board 1; Table 2 is still bidding it.
        // No score from the faster table may enter the cross-table barometer.
        var hiddenUntilPaired = game.GetComparedResults();
        Assert.True(hiddenUntilPaired.BarometerEnabled);
        Assert.Empty(hiddenUntilPaired.ComparedDeals);
        Assert.Equal(0, hiddenUntilPaired.Team1TotalPoints);
        Assert.Equal(0, hiddenUntilPaired.Team2TotalPoints);

        var playedAtTable1 = Assert.Single(game.GetSeatView(1, PlayerPosition.South).PreviousRounds);
        PassOneDeal(game, 2);
        var paired = game.GetComparedResults();
        var first = Assert.Single(paired.ComparedDeals);
        Assert.Equal(1, first.DealNumber);
        Assert.Equal(playedAtTable1.SouthNorthPoints, first.Team1DealPoints);
        Assert.Equal(playedAtTable1.EastWestPoints, first.Team2DealPoints);
        Assert.Equal(first.Team1DealPoints, paired.Team1TotalPoints);
        Assert.Equal(first.Team2DealPoints, paired.Team2TotalPoints);
        Assert.False(paired.IsMatchComplete);

        PassOneDeal(game, 1);
        PassOneDeal(game, 1); // T1 moves ahead on boards 2 and 3.
        Assert.Single(game.GetComparedResults().ComparedDeals);

        PassOneDeal(game, 2); // Only board 2 is paired.
        var second = game.GetComparedResults();
        Assert.Equal(2, second.ComparedDeals.Count);
        Assert.Equal(2, second.ComparedDeals[1].DealNumber);
        Assert.Equal(first.Team1DealPoints, second.Team1TotalPoints);
        Assert.Equal(first.Team2DealPoints, second.Team2TotalPoints);
    }

    [Fact]
    public void BarometerOffWithholdsScoresAndDealRowsEvenWhenBoardsAreFullyPaired()
    {
        var game = new TeamMatchCoreCoordinator(4, 216, barometerEnabled: false);
        PlayOneDeal(game, 1, BidType.NoTrumps);
        PassOneDeal(game, 2);

        var hidden = game.GetComparedResults();
        Assert.False(hidden.BarometerEnabled);
        Assert.False(hidden.IsMatchComplete);
        Assert.Null(hidden.Team1TotalPoints);
        Assert.Null(hidden.Team2TotalPoints);
        Assert.Null(hidden.Team1Margin);
        Assert.Empty(hidden.ComparedDeals);

        for (var board = 2; board <= 4; board++)
        {
            PassOneDeal(game, 1);
            if (board == 4)
            {
                Assert.Null(game.GetComparedResults().Team1TotalPoints);
            }
            PassOneDeal(game, 2);
        }

        Assert.True(game.IsFinished);
        var final = game.GetComparedResults();
        Assert.True(final.IsMatchComplete);
        Assert.False(final.BarometerEnabled);
        Assert.Equal(4, final.ComparedDeals.Count);
        var played = game.GetSeatView(1, PlayerPosition.South).PreviousRounds[0];
        Assert.Equal(played.SouthNorthPoints, final.Team1TotalPoints);
        Assert.Equal(played.EastWestPoints, final.Team2TotalPoints);
        Assert.Equal(final.Team1TotalPoints - final.Team2TotalPoints, final.Team1Margin);
    }

    [Fact]
    public void BarometerOffStaysHiddenDuringActualHangingPointExtraDeals()
    {
        TeamMatchCoreCoordinator? selected = null;
        for (var seed = 0; seed < 1200; seed++)
        {
            var candidate = new TeamMatchCoreCoordinator(4, seed, barometerEnabled: false);
            for (var i = 0; i < 3; i++)
            {
                PassOneDeal(candidate, 1);
                PassOneDeal(candidate, 2);
            }

            PlayOneDeal(candidate, 1, BidType.NoTrumps);
            if (candidate.GetHangingPoints(1) > 0)
            {
                selected = candidate;
                break;
            }
        }

        Assert.NotNull(selected);
        var match = selected!;
        PassOneDeal(match, 2);
        Assert.Equal(5, match.AllowedDeals);
        Assert.False(match.IsFinished);
        Assert.False(match.GetComparedResults().IsMatchComplete);
        Assert.Empty(match.GetComparedResults().ComparedDeals);
        Assert.Null(match.GetComparedResults().Team1TotalPoints);

        var safety = 0;
        while (!match.IsFinished && safety++ < 15)
        {
            PlayOneDeal(match, 1, BidType.NoTrumps);
            Assert.Null(match.GetComparedResults().Team1TotalPoints);
            PassOneDeal(match, 2);
            if (!match.IsFinished)
            {
                Assert.Empty(match.GetComparedResults().ComparedDeals);
                Assert.Null(match.GetComparedResults().Team1TotalPoints);
            }
        }

        Assert.True(match.IsFinished);
        Assert.True(match.AllowedDeals > match.ScheduledDeals);
        var final = match.GetComparedResults();
        Assert.True(final.IsMatchComplete);
        Assert.Equal(match.AllowedDeals, final.ComparedDeals.Count);
        Assert.Equal(match.GetSeatView(1, PlayerPosition.South).SouthNorthPoints
            + match.GetSeatView(2, PlayerPosition.South).EastWestPoints, final.Team1TotalPoints);
        Assert.Equal(match.GetSeatView(1, PlayerPosition.South).EastWestPoints
            + match.GetSeatView(2, PlayerPosition.South).SouthNorthPoints, final.Team2TotalPoints);
    }

    private static void PassOneDeal(TeamMatchCoreCoordinator game, int table)
    {
        var before = game.GetRoundsPlayed(table);
        for (var action = 0; action < 4; action++)
        {
            var south = game.GetSeatView(table, PlayerPosition.South);
            Assert.Equal(BelotDecision.Bid, south.Decision);
            Assert.Equal(BelotActResult.Ok, game.Act(table, south.ToMove, BelotAction.Bid(BidType.Pass)));
        }

        Assert.Equal(before + 1, game.GetRoundsPlayed(table));
    }

    private static void PlayOneDeal(TeamMatchCoreCoordinator game, int table, BidType bid)
    {
        var before = game.GetRoundsPlayed(table);
        var guard = 0;
        while (game.GetRoundsPlayed(table) == before)
        {
            Assert.True(++guard < 100);
            var publicSeat = game.GetSeatView(table, PlayerPosition.South);
            var current = game.GetSeatView(table, publicSeat.ToMove);
            BelotAction chosen = current.Decision switch
            {
                BelotDecision.Bid => BelotAction.Bid(current.Bids.Count == 0 ? bid : BidType.Pass),
                BelotDecision.Announce => BelotAction.Declare(null),
                BelotDecision.PlayCard => BelotAction.PlayCard(current.PlayableCards[0]),
                _ => throw new InvalidOperationException("Unexpected state while playing a deal"),
            };
            Assert.Equal(BelotActResult.Ok, game.Act(table, publicSeat.ToMove, chosen));
        }

        Assert.Equal(before + 1, game.GetRoundsPlayed(table));
    }
}
