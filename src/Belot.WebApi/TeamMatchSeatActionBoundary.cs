namespace Belot.WebApi;

using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

/// <summary>
/// Immutable, initially human-controlled Team Match seat. The user ID must come from
/// authenticated server claims, never from a user-submitted request field.
/// </summary>
internal sealed record TeamMatchHumanSeat(int Table, PlayerPosition Seat, string UserId);

internal enum TeamMatchSeatActionStatus
{
    Accepted,
    Forbidden,
    StaleRevision,
    DuplicateCommand,
    RejectedByGameRules,
}

internal sealed record TeamMatchSeatActionResult(
    TeamMatchSeatActionStatus Status,
    long TableRevision,
    BelotActResult? EngineResult = null);

/// <summary>
/// An internal security boundary for human actions and private snapshots.
/// Resolves current seat ownership INSIDE the same monitor that serializes the
/// actual game action. Later A10.3/A10.4 can use this same monitor for approved
/// organizer seat substitutions. This class DOES NOT expose HTTP routes, validate
/// JWTs, implement organizer rights, or authorize a substitution policy.
/// </summary>
internal sealed class TeamMatchSeatActionBoundary
{
    private readonly object gate = new();

    private readonly TeamMatchCoreCoordinator game;

    private readonly Dictionary<(int Table, PlayerPosition Seat), string> humanSeats = new();

    private readonly Dictionary<int, long> tableRevisions = new()
    {
        [1] = 0,
        [2] = 0,
    };

    private readonly HashSet<(int Table, PlayerPosition Seat, string UserId, string CommandId)> appliedCommands = new();

    public TeamMatchSeatActionBoundary(TeamMatchCoreCoordinator game, IEnumerable<TeamMatchHumanSeat> humans)
    {
        this.game = game ?? throw new ArgumentNullException(nameof(game));
        if (humans == null)
        {
            throw new ArgumentNullException(nameof(humans));
        }

        var occupiedUserIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var human in humans)
        {
            if (human == null || !ValidSeat(human.Table, human.Seat)
                || string.IsNullOrWhiteSpace(human.UserId))
            {
                throw new ArgumentException("Human seat assignments must include an absolute table/seat and authenticated user ID.", nameof(humans));
            }

            var key = (human.Table, human.Seat);
            if (!this.humanSeats.TryAdd(key, human.UserId)
                || !occupiedUserIds.Add(human.UserId))
            {
                throw new ArgumentException("One human account may occupy exactly one unique Team Match seat.", nameof(humans));
            }
        }
    }

    /// <summary>Returns a private view only to that seat's current authenticated human controller.</summary>
    public BelotSeatView? GetSeatView(string authenticatedUserId, int table, PlayerPosition seat)
    {
        lock (this.gate)
        {
            return this.IsAuthorized(authenticatedUserId, table, seat)
                ? this.game.GetSeatView(table, seat)
                : null;
        }
    }

    public long? GetTableRevision(string authenticatedUserId, int table, PlayerPosition seat)
    {
        lock (this.gate)
        {
            return this.IsAuthorized(authenticatedUserId, table, seat)
                ? this.tableRevisions[table]
                : null;
        }
    }

    /// <summary>
    /// The server's trusted subject ID and seat binding are rechecked atomically
    /// when an action is actually applied. Accepted command IDs are one-shot,
    /// so retries never apply a second card/bid. Caller refetches after a duplicate.
    /// </summary>
    public TeamMatchSeatActionResult Submit(
        string authenticatedUserId,
        int table,
        PlayerPosition seat,
        long expectedTableRevision,
        string commandId,
        BelotAction action)
    {
        lock (this.gate)
        {
            if (!this.IsAuthorized(authenticatedUserId, table, seat))
            {
                return new TeamMatchSeatActionResult(TeamMatchSeatActionStatus.Forbidden, -1);
            }

            if (string.IsNullOrWhiteSpace(commandId) || commandId.Length > 128)
            {
                throw new ArgumentException("A bounded unique command ID is required.", nameof(commandId));
            }

            var command = (table, seat, authenticatedUserId, commandId);
            if (this.appliedCommands.Contains(command))
            {
                return new TeamMatchSeatActionResult(
                    TeamMatchSeatActionStatus.DuplicateCommand, this.tableRevisions[table]);
            }

            if (this.tableRevisions[table] != expectedTableRevision)
            {
                return new TeamMatchSeatActionResult(
                    TeamMatchSeatActionStatus.StaleRevision, this.tableRevisions[table]);
            }

            var other = table == 1 ? 2 : 1;
            var beforeOther = this.game.GetSeatView(other, PlayerPosition.South);
            var result = this.game.Act(table, seat, action);
            if (result != BelotActResult.Ok)
            {
                return new TeamMatchSeatActionResult(
                    TeamMatchSeatActionStatus.RejectedByGameRules, this.tableRevisions[table], result);
            }

            this.appliedCommands.Add(command);
            this.tableRevisions[table]++;

            // When the last paired deal completes, the coordinator can start
            // an extra board (or end the match) at BOTH tables. Invalidate any
            // already observed action revision from the other table too.
            var afterOther = this.game.GetSeatView(other, PlayerPosition.South);
            if (beforeOther.RoundNumber != afterOther.RoundNumber
                || beforeOther.IsMatchFinished != afterOther.IsMatchFinished)
            {
                this.tableRevisions[other]++;
            }

            return new TeamMatchSeatActionResult(
                TeamMatchSeatActionStatus.Accepted, this.tableRevisions[table], result);
        }
    }

    /// <summary>
    /// Internal revocation primitive. A future organizer command must validate
    /// organizer rights and settle the D2 handoff policy BEFORE calling this.
    /// Removing a human lease does not assign another actor or play a bot move.
    /// </summary>
    internal void RevokeForTrustedCoordinator(int table, PlayerPosition seat)
    {
        if (!ValidSeat(table, seat))
        {
            throw new ArgumentOutOfRangeException(nameof(seat));
        }

        lock (this.gate)
        {
            this.humanSeats.Remove((table, seat));
            this.tableRevisions[table]++;
        }
    }

    private bool IsAuthorized(string userId, int table, PlayerPosition seat) =>
        ValidSeat(table, seat)
        && !string.IsNullOrWhiteSpace(userId)
        && this.humanSeats.TryGetValue((table, seat), out var owner)
        && string.Equals(userId, owner, StringComparison.Ordinal);

    private static bool ValidSeat(int table, PlayerPosition seat) =>
        (table == 1 || table == 2)
        && (seat == PlayerPosition.South || seat == PlayerPosition.East
            || seat == PlayerPosition.North || seat == PlayerPosition.West);
}
