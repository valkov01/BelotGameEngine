namespace Belot.WebApi.Tests;

using Belot.Engine.Game;
using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

using Xunit;

public sealed class TeamMatchSeatActionBoundaryTests
{
    [Fact]
    public void OnlyCurrentAuthenticatedControllerCanSeePrivateHandOrAct()
    {
        var game = new TeamMatchCoreCoordinator(4, 303);
        var boundary = new TeamMatchSeatActionBoundary(game, new[]
        {
            new TeamMatchHumanSeat(1, PlayerPosition.South, "user-host"),
            new TeamMatchHumanSeat(2, PlayerPosition.South, "user-guest"),
        });

        Assert.Null(boundary.GetSeatView("user-guest", 1, PlayerPosition.South));
        Assert.Null(boundary.GetSeatView("user-host", 2, PlayerPosition.South));
        Assert.Null(boundary.GetSeatView("user-host", 1, PlayerPosition.East));
        Assert.Null(boundary.GetSeatView("outsider", 1, PlayerPosition.South));
        Assert.Null(boundary.GetTableRevision("outsider", 1, PlayerPosition.South));
        Assert.Equal(5, boundary.GetSeatView("user-host", 1, PlayerPosition.South)!.Hand.Count);
        Assert.Equal(0, boundary.GetTableRevision("user-host", 1, PlayerPosition.South));

        var forbidden = boundary.Submit(
            "user-guest", 1, PlayerPosition.South, 0, "attack-1", BelotAction.Bid(BidType.Pass));
        Assert.Equal(TeamMatchSeatActionStatus.Forbidden, forbidden.Status);

        var accepted = boundary.Submit(
            "user-host", 1, PlayerPosition.South, 0, "bid-1", BelotAction.Bid(BidType.Pass));
        Assert.Equal(TeamMatchSeatActionStatus.Accepted, accepted.Status);
        Assert.Equal(1, accepted.TableRevision);
        Assert.Equal(BelotActResult.Ok, accepted.EngineResult);
        Assert.Equal(1, boundary.GetTableRevision("user-host", 1, PlayerPosition.South));

        Assert.Equal(
            TeamMatchSeatActionStatus.DuplicateCommand,
            boundary.Submit("user-host", 1, PlayerPosition.South, 0, "bid-1", BelotAction.Bid(BidType.Pass)).Status);
        Assert.Equal(
            TeamMatchSeatActionStatus.StaleRevision,
            boundary.Submit("user-host", 1, PlayerPosition.South, 0, "bid-2", BelotAction.Bid(BidType.Pass)).Status);
        Assert.Equal(1, boundary.GetTableRevision("user-host", 1, PlayerPosition.South));

        // Server-verified organizer authorization is intentionally not implemented yet.
        // The internal primitive revokes the old actor under the same action lock.
        boundary.RevokeForTrustedCoordinator(1, PlayerPosition.South);
        Assert.Null(boundary.GetSeatView("user-host", 1, PlayerPosition.South));
        Assert.Null(boundary.GetTableRevision("user-host", 1, PlayerPosition.South));
        Assert.Equal(
            TeamMatchSeatActionStatus.Forbidden,
            boundary.Submit("user-host", 1, PlayerPosition.South, 1, "bid-after-revocation", BelotAction.Bid(BidType.Pass)).Status);
    }

    [Fact]
    public void ConcurrentDuplicateSeatCommandsApplyAtMostOnce()
    {
        var game = new TeamMatchCoreCoordinator(4, 28);
        var boundary = new TeamMatchSeatActionBoundary(game, new[]
        {
            new TeamMatchHumanSeat(1, PlayerPosition.South, "same-user"),
        });

        var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            boundary.Submit("same-user", 1, PlayerPosition.South, 0, "retry-" + i, BelotAction.Bid(BidType.Pass)))).ToArray();
        Task.WaitAll(tasks);

        Assert.Equal(1, tasks.Count(t => t.Result.Status == TeamMatchSeatActionStatus.Accepted));
        Assert.Equal(11, tasks.Count(t => t.Result.Status == TeamMatchSeatActionStatus.StaleRevision));
        Assert.Equal(1, boundary.GetTableRevision("same-user", 1, PlayerPosition.South));
        Assert.Single(game.GetSeatView(1, PlayerPosition.South).Bids);
    }

    [Fact]
    public void FinishingTheOtherTableInvalidatesOldSeatRevisionAcrossSharedBoundary()
    {
        var game = new TeamMatchCoreCoordinator(4, 38);
        var users = (from table in new[] { 1, 2 }
                     from seat in new[]
                     {
                         PlayerPosition.South, PlayerPosition.East,
                         PlayerPosition.North, PlayerPosition.West,
                     }
                     select new TeamMatchHumanSeat(table, seat, $"t{table}-{seat}")).ToArray();
        var boundary = new TeamMatchSeatActionBoundary(game, users);

        void PassBoard(int table)
        {
            for (var turn = 0; turn < 4; turn++)
            {
                var state = game.GetSeatView(table, PlayerPosition.South);
                var userId = $"t{table}-{state.ToMove}";
                var revision = boundary.GetTableRevision(userId, table, state.ToMove);
                Assert.NotNull(revision);
                var accepted = boundary.Submit(
                    userId, table, state.ToMove, revision!.Value,
                    $"table-{table}-board-{state.RoundNumber}-turn-{turn}",
                    BelotAction.Bid(BidType.Pass));
                Assert.Equal(TeamMatchSeatActionStatus.Accepted, accepted.Status);
            }
        }

        for (var round = 0; round < 4; round++)
        {
            PassBoard(1);
        }

        var oldRevision = boundary.GetTableRevision("t1-South", 1, PlayerPosition.South);
        Assert.True(game.IsAwaitingSharedBoundary(1));
        Assert.False(game.IsFinished);

        for (var round = 0; round < 4; round++)
        {
            PassBoard(2);
        }

        Assert.True(game.IsFinished);
        Assert.True(boundary.GetTableRevision("t1-South", 1, PlayerPosition.South) > oldRevision);
        Assert.Equal(
            TeamMatchSeatActionStatus.StaleRevision,
            boundary.Submit("t1-South", 1, PlayerPosition.South, oldRevision!.Value,
                "stale-other-table", BelotAction.Bid(BidType.Pass)).Status);
    }

    [Fact]
    public void DuplicateUsersOrSeatsCannotBeRegisteredAndInvalidSeatCannotBeClaimed()
    {
        var game = new TeamMatchCoreCoordinator(4, 123);
        Assert.Throws<ArgumentException>(() => new TeamMatchSeatActionBoundary(game, new[]
        {
            new TeamMatchHumanSeat(1, PlayerPosition.South, "u1"),
            new TeamMatchHumanSeat(2, PlayerPosition.East, "u1"),
        }));
        Assert.Throws<ArgumentException>(() => new TeamMatchSeatActionBoundary(game, new[]
        {
            new TeamMatchHumanSeat(1, PlayerPosition.South, "u1"),
            new TeamMatchHumanSeat(1, PlayerPosition.South, "u2"),
        }));
        Assert.Throws<ArgumentException>(() => new TeamMatchSeatActionBoundary(game, new[]
        {
            new TeamMatchHumanSeat(1, PlayerPosition.Unknown, "u1"),
        }));
    }
}
