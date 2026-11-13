namespace Belot.WebApi;

using System.Security.Claims;
using System.Security.Cryptography;

using Belot.Engine.Players;

public enum MultiplayerTeam
{
    SouthNorth,
    EastWest,
}

public enum MultiplayerRoomStatus
{
    WaitingForPlayers,
    Starting,
    Started,
}

public sealed record MultiplayerSeat(
    PlayerPosition Position,
    string? OwnerUserId,
    bool IsHost,
    bool IsReady,
    string? BotProfileId)
{
    public MultiplayerTeam Team => this.Position switch
    {
        PlayerPosition.South or PlayerPosition.North => MultiplayerTeam.SouthNorth,
        PlayerPosition.East or PlayerPosition.West => MultiplayerTeam.EastWest,
        _ => throw new InvalidOperationException($"Unknown engine seat: {this.Position}."),
    };
}

public sealed record RoomMembership(
    string RoomId,
    string UserId,
    PlayerPosition Seat,
    bool IsHost,
    DateTimeOffset JoinedAt,
    bool IsReady);

public sealed record MultiplayerRoom(
    string RoomId,
    string HostUserId,
    DateTimeOffset CreatedAt,
    MultiplayerRoomStatus Status,
    int MaximumHumanPlayers,
    IReadOnlyDictionary<string, string> BotProfiles,
    IReadOnlyList<MultiplayerSeat> Seats,
    IReadOnlyList<RoomMembership> Members);

/// <summary>Public occupancy and readiness information for one absolute seat.</summary>
public sealed record MultiplayerSeatDto(
    PlayerPosition Seat,
    MultiplayerTeam Team,
    bool IsOccupied,
    bool IsHost,
    bool IsReady,
    string? BotProfileId);

/// <summary>A lobby projection scoped to the authenticated member's own seat.</summary>
public sealed record MultiplayerRoomDto(
    string RoomId,
    MultiplayerRoomStatus Status,
    DateTimeOffset CreatedAt,
    PlayerPosition YourSeat,
    int HumanPlayerCount,
    int MaximumHumanPlayers,
    bool IsGameAvailable,
    IReadOnlyList<MultiplayerSeatDto> Seats,
    long EventRevision = 0,
    int EventSequence = -1);

/// <summary>Configuration for a private multiplayer lobby.</summary>
public sealed record CreateMultiplayerRoomRequest
{
    /// <summary>Maximum number of human players allowed to join, including the host.</summary>
    public int MaximumHumanPlayers { get; init; } = 4;

    /// <summary>Bot profile assigned to East if that seat is vacant when the host starts.</summary>
    public string EastBot { get; init; } = "skilled";

    /// <summary>Bot profile assigned to North if that seat is vacant when the host starts.</summary>
    public string NorthBot { get; init; } = "skilled";

    /// <summary>Bot profile assigned to West if that seat is vacant when the host starts.</summary>
    public string WestBot { get; init; } = "skilled";
}

/// <summary>The created lobby and its invitation secret, disclosed only at creation.</summary>
public sealed record CreateMultiplayerRoomResponse(MultiplayerRoomDto Room, string InvitationCode);

/// <summary>Invitation secret and absolute seat requested by a joining player.</summary>
public sealed record JoinMultiplayerRoomRequest(string? InvitationCode, PlayerPosition? Seat);

/// <summary>The authenticated member's desired ready state.</summary>
public sealed record SetMultiplayerReadyRequest(bool IsReady);

public enum MultiplayerRoomServiceStatus
{
    Success,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    InvalidRequest,
}

public sealed record MultiplayerRoomServiceResult(
    MultiplayerRoomServiceStatus Status,
    MultiplayerRoom? Room = null,
    string? InvitationCode = null,
    string? Error = null);

public enum MultiplayerRoomRepositoryStatus
{
    Success,
    NotFound,
    InvalidInvitation,
    UserAlreadyMember,
    SeatOccupied,
    RoomFull,
    LobbyClosed,
    NotMember,
    NotHost,
    NotEnoughPlayers,
    MembersNotReady,
}

public sealed record MultiplayerRoomRepositoryResult(
    MultiplayerRoomRepositoryStatus Status,
    MultiplayerRoom? Room = null);

public interface IMultiplayerRoomRepository
{
    ValueTask<MultiplayerRoom> CreateAsync(
        string hostUserId,
        int maximumHumanPlayers,
        IReadOnlyDictionary<string, string> botProfiles,
        byte[] invitationCodeHash,
        CancellationToken cancellationToken);

    ValueTask<MultiplayerRoom?> FindAsync(string roomId, CancellationToken cancellationToken);

    ValueTask<RoomMembership?> FindMembershipAsync(string roomId, string userId, CancellationToken cancellationToken);

    ValueTask<MultiplayerRoomRepositoryResult> JoinAsync(
        string roomId,
        string userId,
        PlayerPosition seat,
        byte[] invitationCodeHash,
        CancellationToken cancellationToken);

    ValueTask<MultiplayerRoomRepositoryResult> SetReadyAsync(
        string roomId,
        string userId,
        bool isReady,
        CancellationToken cancellationToken);

    ValueTask<MultiplayerRoomRepositoryResult> BeginStartAsync(
        string roomId,
        string userId,
        CancellationToken cancellationToken);

    ValueTask<MultiplayerRoomRepositoryResult> CompleteStartAsync(
        string roomId,
        string userId,
        CancellationToken cancellationToken);
}

public interface IRoomMembershipService
{
    ValueTask<MultiplayerRoomServiceResult> CreateRoomAsync(
        ClaimsPrincipal caller,
        CreateMultiplayerRoomRequest? request,
        CancellationToken cancellationToken);

    ValueTask<MultiplayerRoomServiceResult> GetRoomAsync(
        ClaimsPrincipal caller,
        string roomId,
        CancellationToken cancellationToken);
}

public interface ISeatAssignmentService
{
    ValueTask<MultiplayerRoomServiceResult> AssignSeatAsync(
        ClaimsPrincipal caller,
        string roomId,
        JoinMultiplayerRoomRequest? request,
        CancellationToken cancellationToken);

    ValueTask<MultiplayerRoomServiceResult> SetReadyAsync(
        ClaimsPrincipal caller,
        string roomId,
        bool isReady,
        CancellationToken cancellationToken);

}

internal sealed class InMemoryMultiplayerRoomRepository : IMultiplayerRoomRepository
{
    internal static readonly PlayerPosition[] SeatsInEngineOrder =
    {
        PlayerPosition.South,
        PlayerPosition.East,
        PlayerPosition.North,
        PlayerPosition.West,
    };

    private readonly object sync = new();

    private readonly Dictionary<string, MultiplayerRoom> rooms = new(StringComparer.Ordinal);

    private readonly Dictionary<string, byte[]> invitationCodeHashes = new(StringComparer.Ordinal);

    public ValueTask<MultiplayerRoom> CreateAsync(
        string hostUserId,
        int maximumHumanPlayers,
        IReadOnlyDictionary<string, string> botProfiles,
        byte[] invitationCodeHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this.sync)
        {
            var roomId = CreateRoomId();
            var createdAt = DateTimeOffset.UtcNow;
            var seats = SeatsInEngineOrder
                .Select(position => new MultiplayerSeat(
                    position,
                    position == PlayerPosition.South ? hostUserId : null,
                    position == PlayerPosition.South,
                    false,
                    null))
                .ToArray();
            var room = new MultiplayerRoom(
                roomId,
                hostUserId,
                createdAt,
                MultiplayerRoomStatus.WaitingForPlayers,
                maximumHumanPlayers,
                new Dictionary<string, string>(botProfiles, StringComparer.Ordinal),
                Array.AsReadOnly(seats),
                Array.AsReadOnly(new[] { new RoomMembership(roomId, hostUserId, PlayerPosition.South, true, createdAt, false) }));
            this.rooms.Add(roomId, room);
            this.invitationCodeHashes.Add(roomId, invitationCodeHash.ToArray());
            return ValueTask.FromResult(room);
        }
    }

    public ValueTask<MultiplayerRoom?> FindAsync(string roomId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this.sync)
        {
            this.rooms.TryGetValue(roomId, out var room);
            return ValueTask.FromResult(room);
        }
    }

    public ValueTask<RoomMembership?> FindMembershipAsync(
        string roomId,
        string userId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this.sync)
        {
            var membership = this.rooms.TryGetValue(roomId, out var room)
                ? room.Members.FirstOrDefault(member => string.Equals(member.UserId, userId, StringComparison.Ordinal))
                : null;
            return ValueTask.FromResult(membership);
        }
    }

    public ValueTask<MultiplayerRoomRepositoryResult> JoinAsync(
        string roomId,
        string userId,
        PlayerPosition seat,
        byte[] invitationCodeHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this.sync)
        {
            if (!this.rooms.TryGetValue(roomId, out var room))
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotFound));
            }

            if (room.Status != MultiplayerRoomStatus.WaitingForPlayers)
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.LobbyClosed));
            }

            if (!CryptographicOperations.FixedTimeEquals(this.invitationCodeHashes[roomId], invitationCodeHash))
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.InvalidInvitation));
            }

            if (room.Members.Any(member => string.Equals(member.UserId, userId, StringComparison.Ordinal)))
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.UserAlreadyMember));
            }

            if (room.Members.Count >= room.MaximumHumanPlayers)
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.RoomFull));
            }

            var seatIndex = Array.IndexOf(SeatsInEngineOrder, seat);
            if (seatIndex < 0 || seat == PlayerPosition.South)
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.SeatOccupied));
            }

            if (room.Seats[seatIndex].OwnerUserId is not null)
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.SeatOccupied));
            }

            var joinedAt = DateTimeOffset.UtcNow;
            var seats = room.Seats.ToArray();
            seats[seatIndex] = new MultiplayerSeat(seat, userId, false, false, null);
            var members = room.Members
                .Append(new RoomMembership(roomId, userId, seat, false, joinedAt, false))
                .ToArray();
            var updatedRoom = room with
            {
                Seats = Array.AsReadOnly(seats),
                Members = Array.AsReadOnly(members),
            };
            this.rooms[roomId] = updatedRoom;
            return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.Success, updatedRoom));
        }
    }

    public ValueTask<MultiplayerRoomRepositoryResult> SetReadyAsync(
        string roomId,
        string userId,
        bool isReady,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this.sync)
        {
            if (!this.rooms.TryGetValue(roomId, out var room))
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotFound));
            }

            if (room.Status != MultiplayerRoomStatus.WaitingForPlayers)
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.LobbyClosed));
            }

            var memberIndex = Array.FindIndex(room.Members.ToArray(), member => string.Equals(member.UserId, userId, StringComparison.Ordinal));
            if (memberIndex < 0)
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotMember));
            }

            var membership = room.Members[memberIndex] with { IsReady = isReady };
            var members = room.Members.ToArray();
            members[memberIndex] = membership;
            var seatIndex = Array.IndexOf(SeatsInEngineOrder, membership.Seat);
            var seats = room.Seats.ToArray();
            seats[seatIndex] = seats[seatIndex] with { IsReady = isReady };
            var updatedRoom = room with
            {
                Members = Array.AsReadOnly(members),
                Seats = Array.AsReadOnly(seats),
            };
            this.rooms[roomId] = updatedRoom;
            return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.Success, updatedRoom));
        }
    }

    public ValueTask<MultiplayerRoomRepositoryResult> BeginStartAsync(
        string roomId,
        string userId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this.sync)
        {
            if (!this.rooms.TryGetValue(roomId, out var room))
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotFound));
            }

            if (!string.Equals(room.HostUserId, userId, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotHost));
            }

            if (room.Status == MultiplayerRoomStatus.Started)
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.LobbyClosed));
            }

            if (room.Status == MultiplayerRoomStatus.Starting)
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.Success, room));
            }

            if (room.Members.Count < 2)
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotEnoughPlayers));
            }

            if (room.Members.Any(member => !member.IsReady))
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.MembersNotReady));
            }

            var seats = room.Seats
                .Select(seat => seat.OwnerUserId is null
                    ? seat with { BotProfileId = room.BotProfiles[seat.Position.ToString()] }
                    : seat)
                .ToArray();
            var startingRoom = room with
            {
                Status = MultiplayerRoomStatus.Starting,
                Seats = Array.AsReadOnly(seats),
            };
            this.rooms[roomId] = startingRoom;
            return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.Success, startingRoom));
        }
    }

    public ValueTask<MultiplayerRoomRepositoryResult> CompleteStartAsync(
        string roomId,
        string userId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this.sync)
        {
            if (!this.rooms.TryGetValue(roomId, out var room))
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotFound));
            }

            if (!string.Equals(room.HostUserId, userId, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotHost));
            }

            if (room.Status != MultiplayerRoomStatus.Starting)
            {
                return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.LobbyClosed));
            }

            var startedRoom = room with { Status = MultiplayerRoomStatus.Started };
            this.rooms[roomId] = startedRoom;
            return ValueTask.FromResult(new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.Success, startedRoom));
        }
    }

    private static string CreateRoomId()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

internal sealed class MultiplayerRoomService : IRoomMembershipService, ISeatAssignmentService
{
    private readonly IMultiplayerRoomRepository repository;

    public MultiplayerRoomService(IMultiplayerRoomRepository repository) => this.repository = repository;

    public async ValueTask<MultiplayerRoomServiceResult> CreateRoomAsync(
        ClaimsPrincipal caller,
        CreateMultiplayerRoomRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(caller, out var userId))
        {
            return new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.Unauthorized);
        }

        if (request is null
            || request.MaximumHumanPlayers is < 2 or > 4
            || !BotProfiles.IsValid(request.EastBot)
            || !BotProfiles.IsValid(request.NorthBot)
            || !BotProfiles.IsValid(request.WestBot))
        {
            return new MultiplayerRoomServiceResult(
                MultiplayerRoomServiceStatus.InvalidRequest,
                Error: "Choose a maximum of 2 to 4 human players and valid bot profiles for East, North, and West.");
        }

        var profiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PlayerPosition.East.ToString()] = request.EastBot.ToLowerInvariant(),
            [PlayerPosition.North.ToString()] = request.NorthBot.ToLowerInvariant(),
            [PlayerPosition.West.ToString()] = request.WestBot.ToLowerInvariant(),
        };
        var invitationCode = CreateInvitationCode();
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(invitationCode));
        var room = await this.repository.CreateAsync(
            userId,
            request.MaximumHumanPlayers,
            profiles,
            hash,
            cancellationToken);
        return new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.Success, room, invitationCode);
    }

    public async ValueTask<MultiplayerRoomServiceResult> GetRoomAsync(
        ClaimsPrincipal caller,
        string roomId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(caller, out var userId))
        {
            return new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.Unauthorized);
        }

        var room = await this.repository.FindAsync(roomId, cancellationToken);
        if (room is null)
        {
            return new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.NotFound);
        }

        var membership = await this.repository.FindMembershipAsync(roomId, userId, cancellationToken);
        return membership is null
            ? new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.Forbidden)
            : new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.Success, room);
    }

    public async ValueTask<MultiplayerRoomServiceResult> AssignSeatAsync(
        ClaimsPrincipal caller,
        string roomId,
        JoinMultiplayerRoomRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(caller, out var userId))
        {
            return new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.Unauthorized);
        }

        if (request?.Seat is not (PlayerPosition.East or PlayerPosition.North or PlayerPosition.West)
            || string.IsNullOrWhiteSpace(request.InvitationCode)
            || request.InvitationCode.Length > 128)
        {
            return new MultiplayerRoomServiceResult(
                MultiplayerRoomServiceStatus.InvalidRequest,
                Error: "Provide a valid invitation code and an available absolute seat: East, North, or West.");
        }

        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.InvitationCode));
        var result = await this.repository.JoinAsync(roomId, userId, request.Seat.Value, hash, cancellationToken);
        return MapRepositoryResult(result);
    }

    public async ValueTask<MultiplayerRoomServiceResult> SetReadyAsync(
        ClaimsPrincipal caller,
        string roomId,
        bool isReady,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(caller, out var userId))
        {
            return new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.Unauthorized);
        }

        return MapRepositoryResult(await this.repository.SetReadyAsync(roomId, userId, isReady, cancellationToken));
    }

    private static MultiplayerRoomServiceResult MapRepositoryResult(MultiplayerRoomRepositoryResult result) =>
        result.Status switch
        {
            MultiplayerRoomRepositoryStatus.Success => new MultiplayerRoomServiceResult(
                MultiplayerRoomServiceStatus.Success,
                result.Room),
            MultiplayerRoomRepositoryStatus.NotFound => new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.NotFound),
            MultiplayerRoomRepositoryStatus.NotMember or MultiplayerRoomRepositoryStatus.NotHost =>
                new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.Forbidden),
            MultiplayerRoomRepositoryStatus.InvalidInvitation => new MultiplayerRoomServiceResult(
                MultiplayerRoomServiceStatus.Forbidden,
                Error: "Invitation code is invalid."),
            MultiplayerRoomRepositoryStatus.UserAlreadyMember => new MultiplayerRoomServiceResult(
                MultiplayerRoomServiceStatus.Conflict,
                Error: "This user already owns a seat in the room."),
            MultiplayerRoomRepositoryStatus.SeatOccupied => new MultiplayerRoomServiceResult(
                MultiplayerRoomServiceStatus.Conflict,
                Error: "That seat is already occupied or is not joinable."),
            MultiplayerRoomRepositoryStatus.RoomFull => new MultiplayerRoomServiceResult(
                MultiplayerRoomServiceStatus.Conflict,
                Error: "The room already has its maximum number of human players."),
            MultiplayerRoomRepositoryStatus.LobbyClosed => new MultiplayerRoomServiceResult(
                MultiplayerRoomServiceStatus.Conflict,
                Error: "The lobby has already started."),
            MultiplayerRoomRepositoryStatus.NotEnoughPlayers => new MultiplayerRoomServiceResult(
                MultiplayerRoomServiceStatus.Conflict,
                Error: "At least two human players must join before starting."),
            MultiplayerRoomRepositoryStatus.MembersNotReady => new MultiplayerRoomServiceResult(
                MultiplayerRoomServiceStatus.Conflict,
                Error: "Every joined human player must be ready before starting."),
            _ => throw new InvalidOperationException($"Unexpected room repository result: {result.Status}."),
        };

    private static bool TryGetUserId(ClaimsPrincipal caller, out string userId)
    {
        userId = caller.FindFirst("sub")?.Value ?? string.Empty;
        return caller.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(userId);
    }

    private static string CreateInvitationCode()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

internal static class MultiplayerRoomMapper
{
    public static MultiplayerRoomDto ToDto(
        MultiplayerRoom room,
        string userId,
        bool isGameAvailable = false,
        MultiplayerEventCursor? eventCursor = null)
    {
        var membership = room.Members.Single(member => string.Equals(member.UserId, userId, StringComparison.Ordinal));
        var seats = room.Seats
            .Select(seat => new MultiplayerSeatDto(
                seat.Position,
                seat.Team,
                seat.OwnerUserId is not null || seat.BotProfileId is not null,
                seat.IsHost,
                seat.IsReady,
                seat.BotProfileId ?? (seat.OwnerUserId is null && room.Status == MultiplayerRoomStatus.WaitingForPlayers
                    ? room.BotProfiles[seat.Position.ToString()]
                    : null)))
            .ToArray();
        return new MultiplayerRoomDto(
            room.RoomId,
            room.Status,
            room.CreatedAt,
            membership.Seat,
            room.Members.Count,
            room.MaximumHumanPlayers,
            isGameAvailable,
            seats,
            eventCursor?.Revision ?? 0,
            eventCursor?.Sequence ?? -1);
    }
}
