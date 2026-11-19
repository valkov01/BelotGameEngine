namespace Belot.WebApi.Tests;

using System.Text.Json;

using Belot.Engine.Cards;
using Belot.Engine.Game;
using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

using Xunit;

public sealed class TeamMatchPublicEventJournalTests
{
    [Fact]
    public void CursorReplayIsOrderedAndExpiredCursorsRequireSnapshot()
    {
        var journal = new TeamMatchPublicEventJournal(2);
        journal.Append(new[]
        {
            new PublicGameEventDto(0, "bid", 1),
            new PublicGameEventDto(0, "declarations", 1),
            new PublicGameEventDto(0, "card", 1),
        });

        var gap = journal.GetEventsAfter(-1);
        Assert.True(gap.SnapshotRequired);
        Assert.Empty(gap.Events);
        Assert.Equal(2, gap.LastEventSequence);

        var continuation = journal.GetEventsAfter(0);
        Assert.False(continuation.SnapshotRequired);
        Assert.Equal(new[] { 1, 2 }, continuation.Events.Select(gameEvent => gameEvent.Sequence));
        Assert.Equal(new[] { "declarations", "card" }, continuation.Events.Select(gameEvent => gameEvent.Type));

        var futureCursor = journal.GetEventsAfter(3);
        Assert.True(futureCursor.SnapshotRequired);
        Assert.Empty(futureCursor.Events);
        Assert.Throws<ArgumentOutOfRangeException>(() => journal.GetEventsAfter(-2));
    }

    [Fact]
    public void AcceptedHumanActionsProduceOrderedPublicAuctionDeclarationAndTrickEvents()
    {
        var seed = FindSeedWithAnnouncements();
        var game = new TeamMatchCoreCoordinator(4, seed);
        var humans = Enum.GetValues<PlayerPosition>()
            .Where(seat => seat is PlayerPosition.South or PlayerPosition.East
                or PlayerPosition.North or PlayerPosition.West)
            .Select(seat => new TeamMatchHumanSeat(1, seat, "table-1-" + seat))
            .ToArray();
        var boundary = new TeamMatchSeatActionBoundary(game, humans);
        var actionNumber = 0;

        while (game.GetRoundsPlayed(1) == 0)
        {
            var southView = game.GetSeatView(1, PlayerPosition.South);
            var seat = southView.ToMove;
            var view = game.GetSeatView(1, seat);
            var action = view.Decision switch
            {
                BelotDecision.Bid => BelotAction.Bid(view.Bids.Count == 0 ? BidType.Clubs : BidType.Pass),
                BelotDecision.Announce => BelotAction.Declare(Array.Empty<Announce>()),
                BelotDecision.PlayCard => BelotAction.PlayCard(view.PlayableCards[0], belote: false),
                _ => throw new InvalidOperationException("Unexpected Team Match decision."),
            };
            var userId = "table-1-" + seat;
            var revision = boundary.GetTableRevision(userId, 1, seat)!.Value;
            var result = boundary.Submit(
                userId,
                1,
                seat,
                revision,
                "action-" + actionNumber++,
                action);
            Assert.Equal(TeamMatchSeatActionStatus.Accepted, result.Status);
        }

        var replay = boundary.GetPublicEventsAfter("table-1-South", 1, PlayerPosition.South, -1)!;
        Assert.False(replay.SnapshotRequired);
        Assert.Equal(
            Enumerable.Range(0, replay.Events.Count),
            replay.Events.Select(gameEvent => gameEvent.Sequence));

        var eventTypes = replay.Events.Select(gameEvent => gameEvent.Type).ToArray();
        Assert.Contains("bid", eventTypes);
        Assert.Contains("contract", eventTypes);
        Assert.Contains("declarations", eventTypes);
        Assert.Contains("card", eventTypes);
        Assert.Contains("trick", eventTypes);
        Assert.Contains("deal-ended", eventTypes);
        Assert.Equal(8, eventTypes.Count(type => type == "trick"));
        Assert.Equal(
            actionNumber,
            replay.Events.Count(gameEvent =>
                (gameEvent.Type is "bid" or "declarations" or "card")
                && gameEvent.Automatic != true));

        var firstCard = Array.IndexOf(eventTypes, "card");
        Assert.True(Array.IndexOf(eventTypes, "bid") < firstCard);
        Assert.True(Array.IndexOf(eventTypes, "contract") < firstCard);
        Assert.True(Array.IndexOf(eventTypes, "declarations") < firstCard);
        Assert.All(
            replay.Events.Where(gameEvent => gameEvent.Type == "trick"),
            gameEvent =>
            {
                Assert.True(gameEvent.Cards!.Count == 4);
                Assert.NotNull(gameEvent.Winner);
            });

        var eventsJson = JsonSerializer.Serialize(replay.Events);
        Assert.DoesNotContain("hand", eventsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("availableBids", eventsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("playableCards", eventsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("table-1-", eventsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateRejectedAndOtherTableActionsDoNotDuplicateOrCrossJournalEvents()
    {
        var game = new TeamMatchCoreCoordinator(4, 38);
        var humans = new[]
        {
            new TeamMatchHumanSeat(1, PlayerPosition.South, "t1-South"),
            new TeamMatchHumanSeat(1, PlayerPosition.East, "t1-East"),
            new TeamMatchHumanSeat(1, PlayerPosition.North, "t1-North"),
            new TeamMatchHumanSeat(1, PlayerPosition.West, "t1-West"),
            new TeamMatchHumanSeat(2, PlayerPosition.East, "t2-East"),
        };
        var boundary = new TeamMatchSeatActionBoundary(game, humans);

        var rejected = boundary.Submit(
            "t1-South",
            1,
            PlayerPosition.South,
            0,
            "wrong-action",
            BelotAction.PlayCard(game.GetSeatView(1, PlayerPosition.South).Hand[0], belote: false));
        Assert.Equal(TeamMatchSeatActionStatus.RejectedByGameRules, rejected.Status);
        Assert.Empty(boundary.GetPublicEventsAfter("t1-South", 1, PlayerPosition.South, -1)!.Events);

        var accepted = boundary.Submit(
            "t1-South",
            1,
            PlayerPosition.South,
            0,
            "accepted-pass",
            BelotAction.Bid(BidType.Pass));
        Assert.Equal(TeamMatchSeatActionStatus.Accepted, accepted.Status);
        var duplicate = boundary.Submit(
            "t1-South",
            1,
            PlayerPosition.South,
            0,
            "accepted-pass",
            BelotAction.Bid(BidType.Pass));
        Assert.Equal(TeamMatchSeatActionStatus.DuplicateCommand, duplicate.Status);

        var tableOne = boundary.GetPublicEventsAfter("t1-South", 1, PlayerPosition.South, -1)!;
        var tableTwo = boundary.GetPublicEventsAfter("t2-East", 2, PlayerPosition.East, -1)!;
        Assert.Single(tableOne.Events);
        Assert.Equal("bid", tableOne.Events[0].Type);
        Assert.Equal(0, tableOne.Events[0].Sequence);
        Assert.Empty(tableTwo.Events);
        Assert.Equal(-1, tableTwo.LastEventSequence);

        var botResult = boundary.SubmitBotForTrustedCoordinator(
            2, PlayerPosition.South, BelotAction.Bid(BidType.Pass));
        Assert.Equal(BelotActResult.Ok, botResult);
        var botReplay = boundary.GetPublicEventsAfter("t2-East", 2, PlayerPosition.East, -1)!;
        var botBid = Assert.Single(botReplay.Events);
        Assert.Equal(0, botBid.Sequence);
        Assert.Equal(PlayerPosition.South.ToString(), botBid.Seat);
        Assert.True(botBid.Automatic);
    }

    [Fact]
    public void SharedMatchCompletionIsJournaledOnBothTables()
    {
        var game = new TeamMatchCoreCoordinator(4, 123);
        var humans = (from table in new[] { 1, 2 }
                     from seat in new[]
                     {
                         PlayerPosition.South, PlayerPosition.East,
                         PlayerPosition.North, PlayerPosition.West,
                     }
                     select new TeamMatchHumanSeat(table, seat, $"t{table}-{seat}")).ToArray();
        var boundary = new TeamMatchSeatActionBoundary(game, humans);
        var commandNumber = 0;

        void PassUntilSharedBoundary(int table)
        {
            while (!game.IsAwaitingSharedBoundary(table) && !game.IsFinished)
            {
                var seat = game.GetSeatView(table, PlayerPosition.South).ToMove;
                var userId = $"t{table}-{seat}";
                var revision = boundary.GetTableRevision(userId, table, seat)!.Value;
                var result = boundary.Submit(
                    userId,
                    table,
                    seat,
                    revision,
                    $"t{table}-pass-{commandNumber++}",
                    BelotAction.Bid(BidType.Pass));
                Assert.Equal(TeamMatchSeatActionStatus.Accepted, result.Status);
            }
        }

        PassUntilSharedBoundary(1);
        Assert.True(game.IsAwaitingSharedBoundary(1));
        Assert.False(game.IsFinished);
        PassUntilSharedBoundary(2);
        Assert.True(game.IsFinished);

        foreach (var (table, userId) in new[] { (1, "t1-South"), (2, "t2-South") })
        {
            var replay = boundary.GetPublicEventsAfter(userId, table, PlayerPosition.South, -1)!;
            Assert.False(replay.SnapshotRequired);
            Assert.Equal(
                Enumerable.Range(0, replay.Events.Count),
                replay.Events.Select(gameEvent => gameEvent.Sequence));
            Assert.Contains(replay.Events, gameEvent => gameEvent.Type == "deal-ended");
        }

        var tableOneReplay = boundary.GetPublicEventsAfter("t1-South", 1, PlayerPosition.South, -1)!;
        Assert.Contains(tableOneReplay.Events, gameEvent =>
            gameEvent.Type == "match-ended" && gameEvent.MatchFinished == true);
        var tableTwoReplay = boundary.GetPublicEventsAfter("t2-South", 2, PlayerPosition.South, -1)!;
        Assert.Contains(tableTwoReplay.Events, gameEvent =>
            gameEvent.Type == "deal-ended" && gameEvent.MatchFinished == true);
    }

    [Fact]
    public void HangingPointsExtensionStartsTheSameExtraDealAtBothTables()
    {
        TeamMatchCoreCoordinator? game = null;
        TeamMatchSeatActionBoundary? boundary = null;
        var commandNumber = 0;
        foreach (var seed in Enumerable.Range(1, 128))
        {
            var candidate = new TeamMatchCoreCoordinator(4, seed);
            var candidateBoundary = CreateBoundary(candidate);
            PassDeals(candidate, candidateBoundary, 1, 3, ref commandNumber);
            PlayWeakClubsDeal(candidate, candidateBoundary, 1, ref commandNumber);
            if (candidate.GetHangingPoints(1) > 0)
            {
                game = candidate;
                boundary = candidateBoundary;
                break;
            }
        }

        Assert.NotNull(game);
        Assert.NotNull(boundary);
        Assert.Equal(4, game!.AllowedDeals);
        Assert.True(game.GetHangingPoints(1) > 0);

        PassDeals(game, boundary!, 2, 4, ref commandNumber);

        Assert.Equal(5, game.AllowedDeals);
        Assert.Equal(5, game.GetSeatView(1, PlayerPosition.South).RoundNumber);
        Assert.Equal(5, game.GetSeatView(2, PlayerPosition.South).RoundNumber);
        foreach (var (table, userId) in new[] { (1, "t1-South"), (2, "t2-South") })
        {
            var replay = boundary.GetPublicEventsAfter(userId, table, PlayerPosition.South, -1)!;
            Assert.Contains(replay.Events, gameEvent =>
                gameEvent.Type == "deal-started" && gameEvent.RoundNumber == 5);
        }
    }

    private static int FindSeedWithAnnouncements()
    {
        foreach (var seed in Enumerable.Range(1, 256))
        {
            var game = new TeamMatchCoreCoordinator(4, seed);
            while (true)
            {
                var seat = game.GetSeatView(1, PlayerPosition.South).ToMove;
                var view = game.GetSeatView(1, seat);
                if (view.Decision != BelotDecision.Bid)
                {
                    break;
                }

                var bid = view.Bids.Count == 0 ? BidType.Clubs : BidType.Pass;
                Assert.Equal(BelotActResult.Ok, game.Act(1, seat, BelotAction.Bid(bid)));
            }

            var found = false;
            while (true)
            {
                var seat = game.GetSeatView(1, PlayerPosition.South).ToMove;
                var view = game.GetSeatView(1, seat);
                if (view.Decision != BelotDecision.Announce)
                {
                    break;
                }

                found |= view.AvailableAnnounces.Count > 0;
                Assert.Equal(
                    BelotActResult.Ok,
                    game.Act(1, seat, BelotAction.Declare(Array.Empty<Announce>())));
            }

            if (found)
            {
                return seed;
            }
        }

        throw new InvalidOperationException("Could not find a deterministic deal with an announce.");
    }

    private static TeamMatchSeatActionBoundary CreateBoundary(TeamMatchCoreCoordinator game)
    {
        var humans = (from table in new[] { 1, 2 }
                     from seat in new[]
                     {
                         PlayerPosition.South, PlayerPosition.East,
                         PlayerPosition.North, PlayerPosition.West,
                     }
                     select new TeamMatchHumanSeat(table, seat, $"t{table}-{seat}")).ToArray();
        return new TeamMatchSeatActionBoundary(game, humans);
    }

    private static void PassDeals(
        TeamMatchCoreCoordinator game,
        TeamMatchSeatActionBoundary boundary,
        int table,
        int dealCount,
        ref int commandNumber)
    {
        while (game.GetRoundsPlayed(table) < dealCount)
        {
            var seat = game.GetSeatView(table, PlayerPosition.South).ToMove;
            var userId = $"t{table}-{seat}";
            var revision = boundary.GetTableRevision(userId, table, seat)!.Value;
            var result = boundary.Submit(
                userId,
                table,
                seat,
                revision,
                $"pass-{commandNumber++}",
                BelotAction.Bid(BidType.Pass));
            Assert.Equal(TeamMatchSeatActionStatus.Accepted, result.Status);
        }
    }

    private static void PlayWeakClubsDeal(
        TeamMatchCoreCoordinator game,
        TeamMatchSeatActionBoundary boundary,
        int table,
        ref int commandNumber)
    {
        var southNorthStrength = new[] { PlayerPosition.South, PlayerPosition.North }
            .SelectMany(seat => game.GetSeatView(table, seat).Hand)
            .Where(card => card.Suit == CardSuit.Club)
            .Sum(card => card.TrumpOrder);
        var eastWestStrength = new[] { PlayerPosition.East, PlayerPosition.West }
            .SelectMany(seat => game.GetSeatView(table, seat).Hand)
            .Where(card => card.Suit == CardSuit.Club)
            .Sum(card => card.TrumpOrder);
        var declaringTeam = southNorthStrength <= eastWestStrength
            ? PlayerPosition.South
            : PlayerPosition.East;

        while (game.GetRoundsPlayed(table) < 4)
        {
            var seat = game.GetSeatView(table, PlayerPosition.South).ToMove;
            var view = game.GetSeatView(table, seat);
            BelotAction action;
            if (view.Decision == BelotDecision.Bid)
            {
                var sameTeam = seat.IsInSameTeamWith(declaringTeam);
                action = view.Contract.Type == BidType.Pass && sameTeam
                    ? BelotAction.Bid(BidType.Clubs)
                    : BelotAction.Bid(BidType.Pass);
            }
            else if (view.Decision == BelotDecision.Announce)
            {
                action = BelotAction.Declare(Array.Empty<Announce>());
            }
            else
            {
                var selected = SelectCardToLoseClubsTrick(view, seat, declaringTeam);
                action = BelotAction.PlayCard(selected, belote: false);
            }

            var userId = $"t{table}-{seat}";
            var revision = boundary.GetTableRevision(userId, table, seat)!.Value;
            var result = boundary.Submit(
                userId, table, seat, revision, $"clubs-{commandNumber++}", action);
            Assert.Equal(TeamMatchSeatActionStatus.Accepted, result.Status);
        }
    }

    private static Card SelectCardToLoseClubsTrick(
        BelotSeatView view,
        PlayerPosition seat,
        PlayerPosition declaringTeam)
    {
        var declaringSide = seat.IsInSameTeamWith(declaringTeam);
        var desiredWinnerTeam = declaringTeam == PlayerPosition.South
            ? PlayerPosition.East
            : PlayerPosition.South;
        var candidates = view.PlayableCards.Select(card => new
        {
            Card = card,
            Winner = WinningSeat(view, seat, card),
        }).ToArray();
        var desiredCandidates = candidates
            .Where(candidate => candidate.Winner.IsInSameTeamWith(desiredWinnerTeam))
            .ToArray();
        var pool = desiredCandidates.Length == 0 ? candidates : desiredCandidates;
        return declaringSide
            ? pool.OrderBy(candidate => ClubsPoints(candidate.Card)).First().Card
            : pool.OrderByDescending(candidate => ClubsPoints(candidate.Card)).First().Card;
    }

    private static PlayerPosition WinningSeat(BelotSeatView view, PlayerPosition seat, Card card)
    {
        if (view.CurrentTrick.Count == 0)
        {
            return seat;
        }

        var leadSuit = view.CurrentTrick[0].Card.Suit;
        var best = view.CurrentTrick[0];
        foreach (var played in view.CurrentTrick.Skip(1))
        {
            if (Beats(played.Card, best.Card, leadSuit))
            {
                best = played;
            }
        }

        return Beats(card, best.Card, leadSuit) ? seat : best.Player;
    }

    private static bool Beats(Card candidate, Card incumbent, CardSuit leadSuit)
    {
        if (candidate.Suit == CardSuit.Club)
        {
            return incumbent.Suit != CardSuit.Club || candidate.TrumpOrder > incumbent.TrumpOrder;
        }

        return incumbent.Suit != CardSuit.Club
            && candidate.Suit == leadSuit
            && incumbent.Suit == leadSuit
            && candidate.NoTrumpOrder > incumbent.NoTrumpOrder;
    }

    private static int ClubsPoints(Card card)
    {
        if (card.Suit == CardSuit.Club)
        {
            return card.Type switch
            {
                CardType.Jack => 20,
                CardType.Nine => 14,
                CardType.Ace => 11,
                CardType.Ten => 10,
                CardType.King => 4,
                CardType.Queen => 3,
                _ => 0,
            };
        }

        return card.Type switch
        {
            CardType.Ace => 11,
            CardType.Ten => 10,
            CardType.King => 4,
            CardType.Queen => 3,
            _ => 0,
        };
    }
}
