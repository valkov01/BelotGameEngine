namespace Belot.Engine.Tests.GameMechanics
{
    using System;
    using System.Linq;

    using Belot.Engine.Cards;
    using Belot.Engine.Game;
    using Belot.Engine.GameMechanics;
    using Belot.Engine.Players;
    using Belot.Engine.Tests.FakeObjects;

    using Xunit;

    public class TeamMatchEngineTests
    {
        [Fact]
        public void ManualRoundsKeepTheSameCanonicalHandsAndRotateAfterEveryAllPass()
        {
            var prepared = Card.AllCards.ToArray();
            var match = new BelotMatch(new BelotMatchOptions
            {
                FirstToPlay = PlayerPosition.South,
                ManualRoundProgression = true,
                DealOrderProvider = _ => prepared,
            });

            match.Start();
            var first = PlayerPosition.South;
            for (var round = 1; round <= 4; round++)
            {
                Assert.Equal(round, match.GetView(PlayerPosition.South).RoundNumber);
                Assert.Equal(first, match.GetView(PlayerPosition.South).FirstToPlayInTheRound);
                for (var i = 0; i < 4; i++)
                {
                    Assert.Equal(BelotDecision.Bid, match.Decision);
                    Assert.Equal(BelotActResult.Ok, match.Act(match.ToMove, BelotAction.Bid(BidType.Pass)));
                }

                Assert.True(match.IsAwaitingRoundAdvance);
                Assert.False(match.IsFinished);
                Assert.Equal(round, match.RoundsPlayed);
                Assert.Equal(PlayerPosition.Unknown, match.ToMove);
                Assert.Equal(BelotActResult.NotYourTurn, match.Act(PlayerPosition.South, BelotAction.Bid(BidType.Pass)));
                if (round < 4)
                {
                    match.AdvanceToNextRound();
                }

                first = first.Next();
            }

            match.CompleteAfterRound();
            Assert.True(match.IsFinished);
            Assert.False(match.IsStopped);
            Assert.Equal(4, match.Result.RoundsPlayed);
            Assert.Equal(4, match.GetRecord().Rounds.Count);
            Assert.All(match.GetRecord().Rounds, round =>
            {
                Assert.Equal(prepared, round.Deal);
                Assert.Equal(0, round.Result.SouthNorthPoints);
                Assert.Equal(0, round.Result.EastWestPoints);
                Assert.Equal(BidType.Pass, round.Result.Contract.Type);
            });
            Assert.Throws<InvalidOperationException>(() => match.AdvanceToNextRound());
        }

        [Fact]
        public void ManualRoundModeIgnores151AndCompletesOnlyWhenCoordinatorSaysSo()
        {
            var players = Enumerable.Range(0, 4)
                .Select(i => new SeededRandomPlayer(300 + i, bidRandomly: true))
                .ToArray();
            var match = new BelotMatch(new BelotMatchOptions
            {
                Random = new Random(722),
                ManualRoundProgression = true,
            });
            match.Start();

            for (var round = 1; round <= 20; round++)
            {
                var guard = 0;
                while (!match.IsAwaitingRoundAdvance)
                {
                    Assert.True(++guard < 200, "Too many decisions for a single 32-card deal.");
                    var seat = match.ToMove;
                    Assert.NotEqual(PlayerPosition.Unknown, seat);
                    Assert.Equal(
                        BelotActResult.Ok,
                        match.Act(seat, players[seat.Index()].Decide(match.GetView(seat))));
                }

                Assert.Equal(round, match.RoundsPlayed);
                Assert.False(match.IsFinished);
                if (round < 20)
                {
                    match.AdvanceToNextRound();
                }
            }

            Assert.True(match.SouthNorthPoints > 151 || match.EastWestPoints > 151);
            match.CompleteAfterRound();
            Assert.True(match.IsFinished);
            Assert.Equal(20, match.Result.RoundsPlayed);
        }

        [Fact]
        public void InvalidPredeterminedDealCannotStartAndMutatingInputCannotChangeStoredDeck()
        {
            var invalid = Card.AllCards.ToArray();
            invalid[0] = invalid[1];

            var rejected = new BelotMatch(new BelotMatchOptions
            {
                ManualRoundProgression = true,
                DealOrderProvider = _ => invalid,
            });
            Assert.Throws<ArgumentException>(() => rejected.Start());

            var original = Card.AllCards.ToArray();
            var match = new BelotMatch(new BelotMatchOptions
            {
                ManualRoundProgression = true,
                DealOrderProvider = _ => original,
            });
            match.Start();
            original[0] = original[1];
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(BelotActResult.Ok, match.Act(match.ToMove, BelotAction.Bid(BidType.Pass)));
            }

            match.CompleteAfterRound();
            var recorded = match.GetRecord().Rounds.Single().Deal;
            Assert.Equal(Card.AllCards, recorded);
        }

        [Fact]
        public void ManualModeRejectsAdvancingOrCompletingUnfinishedDeals()
        {
            var match = new BelotMatch(new BelotMatchOptions { ManualRoundProgression = true });
            Assert.Throws<InvalidOperationException>(() => match.AdvanceToNextRound());
            Assert.Throws<InvalidOperationException>(() => match.CompleteAfterRound());
            match.Start();
            Assert.Throws<InvalidOperationException>(() => match.AdvanceToNextRound());
            Assert.Throws<InvalidOperationException>(() => match.CompleteAfterRound());
        }
    }
}
