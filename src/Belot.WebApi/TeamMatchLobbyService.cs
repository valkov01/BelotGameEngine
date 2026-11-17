namespace Belot.WebApi;

using System.Security.Cryptography;
using System.Text;

using Belot.Engine.Players;

/// <summary>Only a private, unlisted two-table match is supported in A10.3.</summary>
public sealed class CreateTeamMatchRequest
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Team1Name { get; init; }

    public string? Team2Name { get; init; }

    public int ScheduledDeals { get; init; } = 4;

    public bool BarometerEnabled { get; init; } = true;

    /// <summary>The organizer must be seated, but can choose ANY absolute seat.</summary>
    public int OrganizerTable { get; init; } = 1;

    public PlayerPosition OrganizerSeat { get; init; } = PlayerPosition.South;
}

public sealed record TeamMatchSeatAddress(int Table, PlayerPosition Seat);

public sealed record JoinTeamMatchRequest(string? InvitationCode, int Table, PlayerPosition Seat);

public sealed record MoveTeamMatchSeatRequest(int Table, PlayerPosition Seat);

public sealed record SetTeamMatchBotRequest(int Table, PlayerPosition Seat, string? BotProfileId);

public sealed record ReserveTeamMatchSeatRequest(int Table, PlayerPosition Seat, string? ReservedUserId);

/// <summary>No stable user IDs, invitation secrets or hands in lobby responses.</summary>
public sealed record TeamMatchSeatDto(
    int Table,
    PlayerPosition Seat,
    int TeamNumber,
    bool IsOccupied,
    bool IsHost,
    bool IsYou,
    bool IsReserved,
    bool ReservedForYou,
    string? BotProfileId)
{
    public string? DisplayName { get; init; }
}

public sealed record TeamMatchLobbyDto(
    string MatchId,
    string Name,
    string? Description,
    string Team1Name,
    string Team2Name,
    int ScheduledDeals,
    bool BarometerEnabled,
    bool IsStarted,
    TeamMatchSeatAddress YourSeat,
    IReadOnlyList<TeamMatchSeatDto> Seats);

public sealed record CreateTeamMatchResponse(TeamMatchLobbyDto Lobby, string InvitationCode);

public sealed record RotateTeamMatchInviteResponse(string InvitationCode);

internal enum TeamMatchLobbyStatus
{
    Success,
    Invalid,
    Forbidden,
    NotFound,
    Conflict,
}

internal sealed record TeamMatchLobbyResult(
    TeamMatchLobbyStatus Status,
    TeamMatchLobbyDto? Lobby = null,
    string? InvitationCode = null,
    string? Error = null);

/// <summary>
/// A10.3 private lobby state. Process-local pending #12 durable persistence.
/// All admission, host, seat, reserve and start mutations are serialized together.
/// The private code is stored only as a hash; it never appears in a GET/seat DTO.
/// </summary>
internal sealed partial class TeamMatchLobbyService
{
    private static readonly PlayerPosition[] CompassSeats =
    {
        PlayerPosition.South, PlayerPosition.East,
        PlayerPosition.North, PlayerPosition.West,
    };

    private readonly object gate = new();

    private readonly Dictionary<string, RoomState> rooms = new(StringComparer.Ordinal);

    private sealed class SeatState
    {
        public required int Table { get; init; }

        public required PlayerPosition Position { get; init; }

        public string? UserId { get; set; }

        public string? ReservedUserId { get; set; }

        public string? BotProfileId { get; set; }
    }

    private sealed partial class RoomState
    {
        public required string Id { get; init; }

        public required string HostId { get; init; }

        public required string Name { get; init; }

        public string? Description { get; init; }

        public required string Team1Name { get; init; }

        public required string Team2Name { get; init; }

        public required int ScheduledDeals { get; init; }

        public required bool BarometerEnabled { get; init; }

        public required byte[] InviteCodeHash { get; set; }

        public required SeatState[] Seats { get; init; }

        public TeamMatchCoreCoordinator? Game { get; set; }

        public TeamMatchSeatActionBoundary? Actions { get; set; }
    }

    public TeamMatchLobbyResult Create(string callerUserId, CreateTeamMatchRequest? request)
    {
        if (string.IsNullOrWhiteSpace(callerUserId)
            || request == null || request.ScheduledDeals is < 4 or > 20
            || !ValidSeat(request.OrganizerTable, request.OrganizerSeat)
            || !ValidOptionalText(request.Name, 80)
            || !ValidOptionalText(request.Description, 300)
            || !ValidOptionalText(request.Team1Name, 40)
            || !ValidOptionalText(request.Team2Name, 40))
        {
            return Invalid("Invalid match configuration (4–20 deals, valid initial seat and bounded names).");
        }

        lock (this.gate)
        {
            var id = NewSecret(16);
            var invitationCode = NewSecret(32);
            var seats = (from table in new[] { 1, 2 }
                         from seat in CompassSeats
                         select new SeatState { Table = table, Position = seat }).ToArray();
            var hostSeat = seats.Single(seat =>
                seat.Table == request.OrganizerTable && seat.Position == request.OrganizerSeat);
            hostSeat.UserId = callerUserId;
            var match = new RoomState
            {
                Id = id,
                HostId = callerUserId,
                Name = TextOrDefault(request.Name, $"Отборен мач #{id[..8]}"),
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                Team1Name = TextOrDefault(request.Team1Name, "Отбор 1"),
                Team2Name = TextOrDefault(request.Team2Name, "Отбор 2"),
                ScheduledDeals = request.ScheduledDeals,
                BarometerEnabled = request.BarometerEnabled,
                InviteCodeHash = HashCode(invitationCode),
                Seats = seats,
            };
            this.rooms.Add(id, match);
            return new TeamMatchLobbyResult(
                TeamMatchLobbyStatus.Success, View(match, callerUserId), invitationCode);
        }
    }

    public TeamMatchLobbyResult Get(string matchId, string callerUserId)
    {
        lock (this.gate)
        {
            var match = this.Find(matchId);
            if (match == null)
            {
                return new TeamMatchLobbyResult(TeamMatchLobbyStatus.NotFound);
            }

            return IsMember(match, callerUserId)
                ? new TeamMatchLobbyResult(TeamMatchLobbyStatus.Success, View(match, callerUserId))
                : new TeamMatchLobbyResult(TeamMatchLobbyStatus.Forbidden);
        }
    }

    public TeamMatchLobbyResult Join(string matchId, string callerUserId, JoinTeamMatchRequest? request)
    {
        if (request == null || !ValidSeat(request.Table, request.Seat)
            || string.IsNullOrWhiteSpace(request.InvitationCode)
            || request.InvitationCode.Length > 128)
        {
            return Invalid("A valid invitation and a table/seat are required.");
        }

        lock (this.gate)
        {
            var match = this.Find(matchId);
            if (match == null)
            {
                return new TeamMatchLobbyResult(TeamMatchLobbyStatus.NotFound);
            }

            if (!CodeMatches(match.InviteCodeHash, request.InvitationCode))
            {
                return new TeamMatchLobbyResult(TeamMatchLobbyStatus.Forbidden);
            }

            if (match.Game != null)
            {
                return Conflict("The lobby has already started.");
            }

            if (IsMember(match, callerUserId))
            {
                return Conflict("This user already occupies a match seat.");
            }

            var target = FindSeat(match, request.Table, request.Seat)!;
            if (target.UserId != null || target.BotProfileId != null
                || (target.ReservedUserId != null
                    && !string.Equals(target.ReservedUserId, callerUserId, StringComparison.Ordinal))
                || match.Seats.Any(seat =>
                    string.Equals(seat.ReservedUserId, callerUserId, StringComparison.Ordinal)
                    && !ReferenceEquals(seat, target)))
            {
                return Conflict("That seat is unavailable or reserved for a different account.");
            }

            target.UserId = callerUserId;
            target.ReservedUserId = null;
            return new TeamMatchLobbyResult(TeamMatchLobbyStatus.Success, View(match, callerUserId));
        }
    }

    public TeamMatchLobbyResult Move(string matchId, string callerUserId, MoveTeamMatchSeatRequest? request)
    {
        if (request == null || !ValidSeat(request.Table, request.Seat))
        {
            return Invalid("Choose a valid table and compass seat.");
        }

        lock (this.gate)
        {
            var result = this.LookupMember(matchId, callerUserId);
            if (result.Status != TeamMatchLobbyStatus.Success)
            {
                return result;
            }

            var match = this.rooms[matchId];
            if (match.Game != null)
            {
                return Conflict("Seats are fixed after match start.");
            }

            var source = match.Seats.Single(seat => seat.UserId == callerUserId);
            var target = FindSeat(match, request.Table, request.Seat)!;
            if (ReferenceEquals(source, target))
            {
                return new TeamMatchLobbyResult(TeamMatchLobbyStatus.Success, View(match, callerUserId));
            }

            if (target.UserId != null || target.BotProfileId != null
                || (target.ReservedUserId != null && target.ReservedUserId != callerUserId))
            {
                return Conflict("The selected seat is occupied or reserved.");
            }

            source.UserId = null;
            target.UserId = callerUserId;
            target.ReservedUserId = null;
            return new TeamMatchLobbyResult(TeamMatchLobbyStatus.Success, View(match, callerUserId));
        }
    }

    public TeamMatchLobbyResult SetBot(string matchId, string callerUserId, SetTeamMatchBotRequest? request)
    {
        if (request == null || !ValidSeat(request.Table, request.Seat)
            || (request.BotProfileId != null && !BotProfiles.IsValid(request.BotProfileId)))
        {
            return Invalid("Choose a valid seat and supported AI difficulty.");
        }

        lock (this.gate)
        {
            var result = this.LookupHost(matchId, callerUserId);
            if (result.Status != TeamMatchLobbyStatus.Success)
            {
                return result;
            }

            var match = this.rooms[matchId];
            if (match.Game != null)
            {
                return Conflict("Bot assignment is only allowed before match start.");
            }

            var seat = FindSeat(match, request.Table, request.Seat)!;
            if (seat.UserId != null || seat.ReservedUserId != null)
            {
                return Conflict("Occupied or reserved seats cannot be assigned bots.");
            }

            seat.BotProfileId = request.BotProfileId?.ToLowerInvariant();
            return new TeamMatchLobbyResult(TeamMatchLobbyStatus.Success, View(match, callerUserId));
        }
    }

    public TeamMatchLobbyResult Reserve(string matchId, string callerUserId, ReserveTeamMatchSeatRequest? request)
    {
        if (request == null || !ValidSeat(request.Table, request.Seat)
            || (request.ReservedUserId != null
                && (string.IsNullOrWhiteSpace(request.ReservedUserId) || request.ReservedUserId.Length > 128)))
        {
            return Invalid("Provide a valid seat and account ID, or null to remove a reservation.");
        }

        lock (this.gate)
        {
            var result = this.LookupHost(matchId, callerUserId);
            if (result.Status != TeamMatchLobbyStatus.Success)
            {
                return result;
            }

            var match = this.rooms[matchId];
            if (match.Game != null)
            {
                return Conflict("Reservations are fixed when the match starts.");
            }

            var target = FindSeat(match, request.Table, request.Seat)!;
            if (target.UserId != null || target.BotProfileId != null)
            {
                return Conflict("An occupied seat cannot be reserved.");
            }

            if (request.ReservedUserId != null
                && (IsMember(match, request.ReservedUserId)
                    || match.Seats.Any(seat =>
                        !ReferenceEquals(seat, target)
                        && string.Equals(seat.ReservedUserId, request.ReservedUserId, StringComparison.Ordinal))))
            {
                return Conflict("That account already occupies or is reserved for another seat.");
            }

            target.ReservedUserId = request.ReservedUserId;
            return new TeamMatchLobbyResult(TeamMatchLobbyStatus.Success, View(match, callerUserId));
        }
    }

    public TeamMatchLobbyResult RotateInvite(string matchId, string callerUserId)
    {
        lock (this.gate)
        {
            var result = this.LookupHost(matchId, callerUserId);
            if (result.Status != TeamMatchLobbyStatus.Success)
            {
                return result;
            }

            var match = this.rooms[matchId];
            if (match.Game != null)
            {
                return Conflict("Invitations cannot rotate after the match starts.");
            }

            var code = NewSecret(32);
            match.InviteCodeHash = HashCode(code);
            return new TeamMatchLobbyResult(
                TeamMatchLobbyStatus.Success, View(match, callerUserId), code);
        }
    }

    public TeamMatchLobbyResult Start(string matchId, string callerUserId)
    {
        lock (this.gate)
        {
            var result = this.LookupHost(matchId, callerUserId);
            if (result.Status != TeamMatchLobbyStatus.Success)
            {
                return result;
            }

            var match = this.rooms[matchId];
            if (match.Game != null)
            {
                return Conflict("The match has already started.");
            }

            if (match.Seats.Any(seat => seat.UserId == null && seat.BotProfileId == null))
            {
                return Conflict("All eight seats need a human or a bot before starting.");
            }

            // Explicitly temporary, until autonomous 4-bot-table A10.7 (#37).
            if (new[] { 1, 2 }.Any(table => !match.Seats.Any(
                    seat => seat.Table == table && seat.UserId != null)))
            {
                return Conflict("A human is required at each table until autonomous all-bot tables are implemented.");
            }

            // No public game routes are exposed in A10.3. This prepares the
            // authoritative match and locks the lobby roster for A10.4/A10.5.
            var game = new TeamMatchCoreCoordinator(
                match.ScheduledDeals, barometerEnabled: match.BarometerEnabled);
            var humanSeats = match.Seats
                .Where(seat => seat.UserId != null)
                .Select(seat => new TeamMatchHumanSeat(
                    seat.Table, seat.Position, seat.UserId!))
                .ToArray();
            match.Actions = new TeamMatchSeatActionBoundary(game, humanSeats);
            match.Game = game;
            this.ContinueAssignedBots(match, 1);
            this.ContinueAssignedBots(match, 2);
            return new TeamMatchLobbyResult(TeamMatchLobbyStatus.Success, View(match, callerUserId));
        }
    }

    /// <summary>Returns human seat IDs only to current authorized lobby members.</summary>
    internal IReadOnlyDictionary<(int Table, PlayerPosition Seat), string>? GetMemberOwnerIds(
        string matchId,
        string callerUserId)
    {
        lock (this.gate)
        {
            var match = this.Find(matchId);
            return match == null || !IsMember(match, callerUserId)
                ? null
                : match.Seats
                    .Where(seat => seat.UserId != null)
                    .ToDictionary(seat => (seat.Table, seat.Position), seat => seat.UserId!);
        }
    }

    private TeamMatchLobbyResult LookupHost(string matchId, string callerUserId)
    {
        var member = this.LookupMember(matchId, callerUserId);
        if (member.Status != TeamMatchLobbyStatus.Success)
        {
            return member;
        }

        return string.Equals(this.rooms[matchId].HostId, callerUserId, StringComparison.Ordinal)
            ? member
            : new TeamMatchLobbyResult(TeamMatchLobbyStatus.Forbidden);
    }

    private TeamMatchLobbyResult LookupMember(string matchId, string callerUserId)
    {
        var match = this.Find(matchId);
        if (match == null)
        {
            return new TeamMatchLobbyResult(TeamMatchLobbyStatus.NotFound);
        }

        return IsMember(match, callerUserId)
            ? new TeamMatchLobbyResult(TeamMatchLobbyStatus.Success, View(match, callerUserId))
            : new TeamMatchLobbyResult(TeamMatchLobbyStatus.Forbidden);
    }

    private RoomState? Find(string matchId) =>
        !string.IsNullOrWhiteSpace(matchId) && this.rooms.TryGetValue(matchId, out var match)
            ? match
            : null;

    private static bool IsMember(RoomState room, string userId) =>
        !string.IsNullOrWhiteSpace(userId)
        && room.Seats.Any(seat => string.Equals(seat.UserId, userId, StringComparison.Ordinal));

    private static SeatState? FindSeat(RoomState room, int table, PlayerPosition compass) =>
        room.Seats.FirstOrDefault(seat => seat.Table == table && seat.Position == compass);

    private static TeamMatchLobbyDto View(RoomState room, string viewerId)
    {
        var yours = room.Seats.Single(seat => seat.UserId == viewerId);
        var seats = room.Seats.Select(seat =>
            new TeamMatchSeatDto(
                seat.Table,
                seat.Position,
                seat.Table == 1
                    ? (seat.Position is PlayerPosition.South or PlayerPosition.North ? 1 : 2)
                    : (seat.Position is PlayerPosition.South or PlayerPosition.North ? 2 : 1),
                seat.UserId != null || seat.BotProfileId != null,
                seat.UserId == room.HostId,
                seat.UserId == viewerId,
                seat.ReservedUserId != null,
                seat.ReservedUserId == viewerId,
                seat.BotProfileId)).ToArray();
        return new TeamMatchLobbyDto(
            room.Id,
            room.Name,
            room.Description,
            room.Team1Name,
            room.Team2Name,
            room.ScheduledDeals,
            room.BarometerEnabled,
            room.Game != null,
            new TeamMatchSeatAddress(yours.Table, yours.Position),
            seats);
    }

    private static bool ValidSeat(int table, PlayerPosition compass) =>
        (table == 1 || table == 2)
        && compass is PlayerPosition.South or PlayerPosition.East or PlayerPosition.North or PlayerPosition.West;

    private static bool ValidOptionalText(string? value, int maxLength) =>
        value == null || value.Length <= maxLength;

    private static string TextOrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NewSecret(int count)
    {
        var data = RandomNumberGenerator.GetBytes(count);
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] HashCode(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

    private static bool CodeMatches(byte[] storedHash, string code) =>
        CryptographicOperations.FixedTimeEquals(storedHash, HashCode(code));

    private static TeamMatchLobbyResult Invalid(string error) =>
        new(TeamMatchLobbyStatus.Invalid, Error: error);

    private static TeamMatchLobbyResult Conflict(string error) =>
        new(TeamMatchLobbyStatus.Conflict, Error: error);
}
