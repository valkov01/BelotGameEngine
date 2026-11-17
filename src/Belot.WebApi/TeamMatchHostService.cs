namespace Belot.WebApi;

using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

/// <summary>Seat substitution does not relocate a compass seat or redeal cards.</summary>
public sealed record KickTeamMatchPlayerRequest(int Table, PlayerPosition Seat);

public sealed record InviteTeamMatchReplacementRequest(int Table, PlayerPosition Seat, string? UserId);

public sealed record AssignTeamMatchReplacementBotRequest(int Table, PlayerPosition Seat, string? BotProfileId);

public sealed record TeamMatchInactivityReportRequest(int Table, PlayerPosition Seat, string? Reason);

public sealed record TeamMatchHumanActionRequest(
    long ExpectedTableRevision, string? CommandId, GameActionRequest? Action);

public sealed record ResumeTeamMatchBotsRequest(int Table);

public sealed record TeamMatchReportDto(
    string ReportId, int Table, PlayerPosition Seat, PlayerPosition ReporterSeat,
    string? Reason, DateTimeOffset CreatedAt);

public sealed record TeamMatchReplacementInvitationDto(
    string InvitationId, string MatchId, string MatchName,
    int Table, PlayerPosition Seat, DateTimeOffset CreatedAt);

public sealed record TeamMatchOrganizerTableDto(
    int Table, int RoundNumber, string ToMove, string Decision, bool IsPaused,
    bool AwaitingSharedBoundary, bool IsFinished, string ConnectivityStatus,
    IReadOnlyList<TeamMatchSeatDto> Seats, IReadOnlyList<BidDto> Bids,
    IReadOnlyList<PlayedCardDto> CurrentTrick, IReadOnlyList<TrickDto> CompletedTricks,
    IReadOnlyList<int> CardCounts);

internal sealed record TeamMatchOrganizerOverviewDto(
    string MatchId, bool BarometerEnabled, int ScheduledDeals,
    IReadOnlyList<TeamMatchOrganizerTableDto> Tables,
    TeamMatchResultsView Results,
    IReadOnlyList<TeamMatchReportDto> Reports);

public sealed record TeamMatchGameSnapshotDto(
    string MatchId, int Table, PlayerPosition YourSeat, long Revision,
    bool IsPaused, bool AutomationPending, GameStateDto State);

internal sealed record TeamMatchHostResult(
    TeamMatchLobbyStatus Status,
    string? Error = null,
    TeamMatchLobbyDto? Lobby = null,
    TeamMatchOrganizerOverviewDto? Overview = null,
    TeamMatchGameSnapshotDto? Game = null,
    TeamMatchReplacementInvitationDto? Invitation = null,
    IReadOnlyList<TeamMatchReplacementInvitationDto>? Invitations = null,
    TeamMatchReportDto? Report = null);

/// <summary>
/// Host operations are synchronized under the same TeamMatchLobbyService gate
/// that protects admissions and human game actions. Only the fixed-seat
/// occupant changes; neither a kick nor an invite creates an automatic bot.
/// </summary>
internal sealed partial class TeamMatchLobbyService
{
    private sealed record TeamMatchReportInternal(
        string Id, string ReporterUserId, PlayerPosition ReporterSeat,
        int Table, PlayerPosition Seat, string? Reason, DateTimeOffset At);

    private sealed record TeamMatchReplacementInvitationInternal(
        string Id, string UserId, int Table, PlayerPosition Seat, DateTimeOffset At);

    private sealed partial class RoomState
    {
        public List<TeamMatchReportInternal> InactivityReports { get; } = new();

        public Dictionary<(int Table, PlayerPosition Seat), TeamMatchReplacementInvitationInternal>
            ReplacementInvitations { get; } = new();

        public Dictionary<(int Table, PlayerPosition Seat), IPlayer> BotPlayers { get; } = new();

        public bool[] BotAutomationPending { get; } = new bool[3];

        public bool[] BotAutomationFailed { get; } = new bool[3];
    }

    public TeamMatchHostResult ReportInactive(
        string matchId, string userId, TeamMatchInactivityReportRequest? request)
    {
        if (request == null || !ValidSeat(request.Table, request.Seat)
            || request.Reason?.Length > 300)
        {
            return InvalidHost("Specify a target seat and optionally up to 300 characters.");
        }

        lock (this.gate)
        {
            var member = this.LookupMember(matchId, userId);
            if (member.Status != TeamMatchLobbyStatus.Success)
            {
                return HostProblem(member);
            }

            var room = this.rooms[matchId];
            if (room.Game == null)
            {
                return ConflictHost("Reports are available only after match start.");
            }

            var target = FindSeat(room, request.Table, request.Seat)!;
            var source = room.Seats.Single(seat => seat.UserId == userId);
            if (target.UserId == null || target.UserId == userId)
            {
                return ConflictHost("Report another active human participant.");
            }

            var now = DateTimeOffset.UtcNow;
            if (room.InactivityReports.Any(report =>
                report.ReporterUserId == userId && report.Table == request.Table
                && report.Seat == request.Seat && now - report.At < TimeSpan.FromMinutes(1)))
            {
                return ConflictHost("An inactivity report for this player was submitted recently.");
            }

            var record = new TeamMatchReportInternal(
                NewSecret(12), userId, source.Position, request.Table, request.Seat,
                string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(), now);
            room.InactivityReports.Add(record);
            return new TeamMatchHostResult(
                TeamMatchLobbyStatus.Success, Report: PublicReport(record));
        }
    }

    public TeamMatchHostResult GetOrganizerOverview(string matchId, string userId)
    {
        lock (this.gate)
        {
            var host = this.LookupHost(matchId, userId);
            if (host.Status != TeamMatchLobbyStatus.Success)
            {
                return HostProblem(host);
            }

            var room = this.rooms[matchId];
            if (room.Game == null)
            {
                return ConflictHost("Match has not started.");
            }

            // Extract only shared/public auction, played cards, counts and turn.
            // The host cannot query other players' Hand, PlayableCards or Record.
            var lobby = View(room, userId);
            var tables = new List<TeamMatchOrganizerTableDto>(2);
            for (var table = 1; table <= 2; table++)
            {
                var view = room.Game.GetSeatView(table, PlayerPosition.South);
                tables.Add(new TeamMatchOrganizerTableDto(
                    table, view.RoundNumber, view.ToMove.ToString(), view.Decision.ToString(),
                    TablePaused(room, table),
                    room.Game.IsAwaitingSharedBoundary(table), view.IsMatchFinished,
                    "unknown", // no reliable live-presence integration yet
                    lobby.Seats.Where(seat => seat.Table == table).ToArray(),
                    view.Bids.Select(ApiMapper.ToBid).ToArray(),
                    view.CurrentTrick.Select(ApiMapper.ToPlayedCard).ToArray(),
                    view.Tricks.Select(ApiMapper.ToTrick).ToArray(),
                    view.CardCounts.ToArray()));
            }

            return new TeamMatchHostResult(
                TeamMatchLobbyStatus.Success,
                Overview: new TeamMatchOrganizerOverviewDto(
                    room.Id, room.BarometerEnabled, room.ScheduledDeals,
                    tables.ToArray(), room.Game.GetComparedResults(),
                    room.InactivityReports.Select(PublicReport).ToArray()));
        }
    }

    public TeamMatchHostResult Kick(
        string matchId, string userId, KickTeamMatchPlayerRequest? request)
    {
        if (request == null || !ValidSeat(request.Table, request.Seat))
        {
            return InvalidHost("Choose a fixed table and seat.");
        }

        lock (this.gate)
        {
            var host = this.LookupHost(matchId, userId);
            if (host.Status != TeamMatchLobbyStatus.Success)
            {
                return HostProblem(host);
            }

            var room = this.rooms[matchId];
            if (room.Game == null)
            {
                return ConflictHost("A participant can be substituted only after the match begins.");
            }

            var target = FindSeat(room, request.Table, request.Seat)!;
            if (target.UserId == null || target.UserId == room.HostId)
            {
                return ConflictHost("Only a non-organizer human player can be removed.");
            }

            // Kicking revokes private access under the SAME lock used by every
            // Team Match action. Never silently create a bot.
            room.Actions!.RevokeForTrustedCoordinator(request.Table, request.Seat);
            target.UserId = null;
            target.ReservedUserId = null;
            target.BotProfileId = null;
            room.BotPlayers.Remove((request.Table, request.Seat));
            room.ReplacementInvitations.Remove((request.Table, request.Seat));
            room.BotAutomationPending[request.Table] = false;
            return new TeamMatchHostResult(
                TeamMatchLobbyStatus.Success, Lobby: View(room, userId));
        }
    }

    public TeamMatchHostResult InviteReplacement(
        string matchId, string userId, InviteTeamMatchReplacementRequest? request)
    {
        if (request == null || !ValidSeat(request.Table, request.Seat)
            || string.IsNullOrWhiteSpace(request.UserId) || request.UserId.Length > 128)
        {
            return InvalidHost("Choose a seat and a valid authenticated replacement user ID.");
        }

        lock (this.gate)
        {
            var host = this.LookupHost(matchId, userId);
            if (host.Status != TeamMatchLobbyStatus.Success)
            {
                return HostProblem(host);
            }

            var room = this.rooms[matchId];
            if (room.Game == null)
            {
                return ConflictHost("Use prestart reservations in the lobby.");
            }

            var target = FindSeat(room, request.Table, request.Seat)!;
            if (target.UserId != null || IsMember(room, request.UserId)
                || room.ReplacementInvitations.Values.Any(inv => inv.UserId == request.UserId
                    && (inv.Table != request.Table || inv.Seat != request.Seat)))
            {
                return ConflictHost("The seat is human-occupied or the replacement already belongs to this match.");
            }

            var invite = new TeamMatchReplacementInvitationInternal(
                NewSecret(24), request.UserId, request.Table, request.Seat, DateTimeOffset.UtcNow);
            room.ReplacementInvitations[(request.Table, request.Seat)] = invite;
            target.ReservedUserId = request.UserId;
            // A vacant seat remains paused; a bot seat keeps its bot until acceptance.
            return new TeamMatchHostResult(
                TeamMatchLobbyStatus.Success,
                Invitation: PublicInvite(room, invite));
        }
    }

    /// <summary>Private invitation inbox. The user does not need lobby membership yet.</summary>
    public TeamMatchHostResult ListInvitations(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new TeamMatchHostResult(TeamMatchLobbyStatus.Forbidden);
        }

        lock (this.gate)
        {
            var list = this.rooms.Values
                .SelectMany(room => room.ReplacementInvitations.Values
                    .Where(invite => invite.UserId == userId)
                    .Select(invite => PublicInvite(room, invite))).ToArray();
            return new TeamMatchHostResult(
                TeamMatchLobbyStatus.Success, Invitations: list);
        }
    }

    public TeamMatchHostResult AcceptReplacement(
        string matchId, string userId, string invitationId)
    {
        lock (this.gate)
        {
            var room = this.Find(matchId);
            if (room == null)
            {
                return new TeamMatchHostResult(TeamMatchLobbyStatus.NotFound);
            }

            if (room.Game == null || string.IsNullOrWhiteSpace(invitationId)
                || IsMember(room, userId))
            {
                return ConflictHost("This replacement invitation cannot be accepted.");
            }

            var match = room.ReplacementInvitations.FirstOrDefault(pair =>
                pair.Value.Id == invitationId && pair.Value.UserId == userId);
            if (match.Value == null)
            {
                return new TeamMatchHostResult(TeamMatchLobbyStatus.Forbidden);
            }

            var seat = FindSeat(room, match.Value.Table, match.Value.Seat)!;
            if (seat.UserId != null || seat.ReservedUserId != userId)
            {
                return ConflictHost("This seat has already been claimed or reassigned.");
            }

            // Authorized private hand changes controller atomically; the new
            // user gets only this fixed seat's remaining cards.
            room.Actions!.AssignForTrustedCoordinator(match.Value.Table, match.Value.Seat, userId);
            seat.UserId = userId;
            seat.BotProfileId = null;
            seat.ReservedUserId = null;
            room.BotPlayers.Remove((match.Value.Table, match.Value.Seat));
            room.ReplacementInvitations.Remove(match.Key);
            this.ContinueAssignedBots(room, match.Value.Table);
            return new TeamMatchHostResult(
                TeamMatchLobbyStatus.Success, Lobby: View(room, userId));
        }
    }

    public TeamMatchHostResult AssignReplacementBot(
        string matchId, string userId, AssignTeamMatchReplacementBotRequest? request)
    {
        if (request == null || !ValidSeat(request.Table, request.Seat)
            || string.IsNullOrWhiteSpace(request.BotProfileId)
            || !BotProfiles.IsValid(request.BotProfileId))
        {
            return InvalidHost("Select a fixed empty/bot seat and a supported AI difficulty.");
        }

        lock (this.gate)
        {
            var host = this.LookupHost(matchId, userId);
            if (host.Status != TeamMatchLobbyStatus.Success)
            {
                return HostProblem(host);
            }

            var room = this.rooms[matchId];
            if (room.Game == null)
            {
                return ConflictHost("Use lobby bot assignments before match start.");
            }

            var seat = FindSeat(room, request.Table, request.Seat)!;
            if (seat.UserId != null)
            {
                return ConflictHost("Kick the human occupant first; never auto-replace a human.");
            }

            room.ReplacementInvitations.Remove((request.Table, request.Seat));
            seat.ReservedUserId = null;
            seat.BotProfileId = request.BotProfileId.ToLowerInvariant();
            room.BotPlayers[(request.Table, request.Seat)] = BotProfiles.Create(seat.BotProfileId);
            // Resume only because the host EXPLICITLY requested a bot.
            this.ContinueAssignedBots(room, request.Table);
            return new TeamMatchHostResult(
                TeamMatchLobbyStatus.Success, Lobby: View(room, userId));
        }
    }

    public TeamMatchHostResult GetPrivateGame(string matchId, string userId)
    {
        lock (this.gate)
        {
            var member = this.LookupMember(matchId, userId);
            if (member.Status != TeamMatchLobbyStatus.Success)
            {
                return HostProblem(member);
            }

            var room = this.rooms[matchId];
            if (room.Game == null || room.Actions == null)
            {
                return ConflictHost("Game has not started.");
            }

            var seat = room.Seats.Single(seat => seat.UserId == userId);
            var view = room.Actions.GetSeatView(userId, seat.Table, seat.Position);
            var revision = room.Actions.GetTableRevision(userId, seat.Table, seat.Position);
            if (view == null || !revision.HasValue)
            {
                return new TeamMatchHostResult(TeamMatchLobbyStatus.Forbidden);
            }

            return new TeamMatchHostResult(
                TeamMatchLobbyStatus.Success,
                Game: new TeamMatchGameSnapshotDto(
                    room.Id, seat.Table, seat.Position, revision.Value,
                    TablePaused(room, seat.Table), room.BotAutomationPending[seat.Table],
                    ApiMapper.ToState(room.Id, BotMap(room, seat.Table), view)));
        }
    }

    public TeamMatchHostResult SubmitHumanAction(
        string matchId, string userId, TeamMatchHumanActionRequest? request)
    {
        if (request == null || request.ExpectedTableRevision < 0
            || string.IsNullOrWhiteSpace(request.CommandId) || request.CommandId.Length > 128
            || request.Action == null)
        {
            return InvalidHost("Supply an action, expected table revision and unique command ID.");
        }

        lock (this.gate)
        {
            var member = this.LookupMember(matchId, userId);
            if (member.Status != TeamMatchLobbyStatus.Success)
            {
                return HostProblem(member);
            }

            var room = this.rooms[matchId];
            if (room.Game == null || room.Actions == null)
            {
                return ConflictHost("Game has not started.");
            }

            var seat = room.Seats.Single(seat => seat.UserId == userId);
            if (TablePaused(room, seat.Table))
            {
                return ConflictHost("This table is paused until its vacant seat receives a bot or invited human.");
            }

            var view = room.Actions.GetSeatView(userId, seat.Table, seat.Position);
            if (view == null)
            {
                return new TeamMatchHostResult(TeamMatchLobbyStatus.Forbidden);
            }

            if (!GameService.TryBuildAction(view, request.Action, out var action, out var error))
            {
                return InvalidHost(error);
            }

            var submitted = room.Actions.Submit(
                userId, seat.Table, seat.Position, request.ExpectedTableRevision,
                request.CommandId, action);
            if (submitted.Status != TeamMatchSeatActionStatus.Accepted)
            {
                return submitted.Status == TeamMatchSeatActionStatus.Forbidden
                    ? new TeamMatchHostResult(TeamMatchLobbyStatus.Forbidden)
                    : ConflictHost($"Action not accepted: {submitted.Status}.");
            }

            this.ContinueAssignedBots(room, seat.Table);
            this.ContinueAssignedBots(room, seat.Table == 1 ? 2 : 1);
            return this.GetPrivateGame(matchId, userId);
        }
    }

    public TeamMatchHostResult ResumeAssignedBots(string matchId, string userId, int table)
    {
        if (table is < 1 or > 2)
        {
            return InvalidHost("Choose table 1 or 2.");
        }

        lock (this.gate)
        {
            var member = this.LookupMember(matchId, userId);
            if (member.Status != TeamMatchLobbyStatus.Success)
            {
                return HostProblem(member);
            }

            var room = this.rooms[matchId];
            if (room.Game == null)
            {
                return ConflictHost("Game has not started.");
            }

            if (TablePaused(room, table))
            {
                return ConflictHost("This table is paused for a replacement, not pending bot automation.");
            }

            this.ContinueAssignedBots(room, table);
            return new TeamMatchHostResult(TeamMatchLobbyStatus.Success);
        }
    }

    private void ContinueAssignedBots(RoomState room, int table)
    {
        if (room.Game == null || room.Actions == null || TablePaused(room, table))
        {
            return;
        }

        // Bounded continuation, following the existing multiplayer engine's
        // human-action-triggered automation pattern. A completely human-free
        // table requiring persistent independent scheduling is LAST A10.7 #37.
        const int maximumActions = 96;
        room.BotAutomationFailed[table] = false;
        var actions = 0;
        while (!room.Game.IsFinished && !TablePaused(room, table)
            && actions++ < maximumActions)
        {
            var view = room.Game.GetSeatView(table, PlayerPosition.South);
            if (view.ToMove is not (PlayerPosition.South or PlayerPosition.East
                or PlayerPosition.North or PlayerPosition.West))
            {
                room.BotAutomationPending[table] = false;
                return;
            }

            var botSeat = FindSeat(room, table, view.ToMove)!;
            if (botSeat.BotProfileId == null || botSeat.UserId != null)
            {
                room.BotAutomationPending[table] = false;
                return;
            }

            try
            {
                var key = (table, view.ToMove);
                if (!room.BotPlayers.TryGetValue(key, out var player))
                {
                    player = BotProfiles.Create(botSeat.BotProfileId);
                    room.BotPlayers.Add(key, player);
                }

                var move = player.Decide(room.Game.GetSeatView(table, view.ToMove));
                if (room.Actions.SubmitBotForTrustedCoordinator(table, view.ToMove, move)
                    != BelotActResult.Ok)
                {
                    room.BotAutomationFailed[table] = true;
                    room.BotAutomationPending[table] = true;
                    return;
                }
            }
            catch (Exception)
            {
                // Never roll back an already accepted human action or fake a bot
                // move on failure. Leave a resumable pending state to retry safely.
                room.BotAutomationFailed[table] = true;
                room.BotAutomationPending[table] = true;
                return;
            }
        }

        room.BotAutomationPending[table] = !room.Game.IsFinished;
    }

    private static bool TablePaused(RoomState room, int table) =>
        room.Seats.Any(seat => seat.Table == table
            && seat.UserId == null && seat.BotProfileId == null);

    private static IReadOnlyDictionary<string, string> BotMap(RoomState room, int table) =>
        room.Seats.Where(seat => seat.Table == table && seat.BotProfileId != null)
            .ToDictionary(seat => seat.Position.ToString(), seat => seat.BotProfileId!);

    private static TeamMatchReportDto PublicReport(TeamMatchReportInternal report) =>
        new(report.Id, report.Table, report.Seat, report.ReporterSeat, report.Reason, report.At);

    private static TeamMatchReplacementInvitationDto PublicInvite(
        RoomState room, TeamMatchReplacementInvitationInternal invite) =>
        new(invite.Id, room.Id, room.Name, invite.Table, invite.Seat, invite.At);

    private static TeamMatchHostResult HostProblem(TeamMatchLobbyResult result) =>
        new(result.Status, result.Error);

    private static TeamMatchHostResult InvalidHost(string error) =>
        new(TeamMatchLobbyStatus.Invalid, error);

    private static TeamMatchHostResult ConflictHost(string error) =>
        new(TeamMatchLobbyStatus.Conflict, error);
}
