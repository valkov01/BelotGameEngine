namespace Belot.WebApi
{
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
    }

    public sealed record MultiplayerSeat(PlayerPosition Position, string? OwnerUserId, bool IsHost)
    {
        public MultiplayerTeam Team => Position switch
        {
            PlayerPosition.South or PlayerPosition.North => MultiplayerTeam.SouthNorth,
            PlayerPosition.East or PlayerPosition.West => MultiplayerTeam.EastWest,
            _ => throw new InvalidOperationException($"Unknown engine seat: {Position}."),
        };
    }

    public sealed record RoomMembership(
        string RoomId,
        string UserId,
        PlayerPosition Seat,
        bool IsHost,
        DateTimeOffset JoinedAt,
        bool IsReady = false);

    public sealed record MultiplayerRoom(
        string RoomId,
        string HostUserId,
        DateTimeOffset CreatedAt,
        MultiplayerRoomStatus Status,
        IReadOnlyList<MultiplayerSeat> Seats,
        IReadOnlyList<RoomMembership> Members);

    public sealed record MultiplayerSeatDto(PlayerPosition Seat, MultiplayerTeam Team, bool IsOccupied, bool IsHost);

    public sealed record MultiplayerRoomDto(
        string RoomId,
        MultiplayerRoomStatus Status,
        DateTimeOffset CreatedAt,
        PlayerPosition YourSeat,
        IReadOnlyList<MultiplayerSeatDto> Seats);

    public sealed record JoinMultiplayerRoomRequest(PlayerPosition? Seat);

    public enum MultiplayerRoomServiceStatus
    {
        Success,
        Unauthorized,
        Forbidden,
        NotFound,
        Conflict,
        InvalidSeat,
    }

    public sealed record MultiplayerRoomServiceResult(
        MultiplayerRoomServiceStatus Status,
        MultiplayerRoom? Room = null,
        string? Error = null);

    public enum SeatAssignmentStatus
    {
        Assigned,
        RoomNotFound,
        UserAlreadyMember,
        SeatOccupied,
        InvalidSeat,
    }

    public sealed record SeatAssignmentResult(SeatAssignmentStatus Status, MultiplayerRoom? Room = null);

    public interface IMultiplayerRoomRepository
    {
        ValueTask<MultiplayerRoom> CreateAsync(string hostUserId, CancellationToken cancellationToken);

        ValueTask<MultiplayerRoom?> FindAsync(string roomId, CancellationToken cancellationToken);

        ValueTask<RoomMembership?> FindMembershipAsync(string roomId, string userId, CancellationToken cancellationToken);

        ValueTask<SeatAssignmentResult> AssignSeatAsync(
            string roomId,
            string userId,
            PlayerPosition seat,
            CancellationToken cancellationToken);
    }

    public interface IRoomMembershipService
    {
        ValueTask<MultiplayerRoomServiceResult> CreateRoomAsync(ClaimsPrincipal caller, CancellationToken cancellationToken);

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
            PlayerPosition? seat,
            CancellationToken cancellationToken);
    }

    internal sealed class InMemoryMultiplayerRoomRepository : IMultiplayerRoomRepository
    {
        private static readonly PlayerPosition[] SeatsInEngineOrder =
        {
            PlayerPosition.South,
            PlayerPosition.East,
            PlayerPosition.North,
            PlayerPosition.West,
        };

        private readonly object sync = new();

        private readonly Dictionary<string, MultiplayerRoom> rooms = new(StringComparer.Ordinal);

        public ValueTask<MultiplayerRoom> CreateAsync(string hostUserId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.sync)
            {
                var roomId = CreateRoomId();
                var createdAt = DateTimeOffset.UtcNow;
                var seats = SeatsInEngineOrder
                    .Select(position => new MultiplayerSeat(position, position == PlayerPosition.South ? hostUserId : null, position == PlayerPosition.South))
                    .ToArray();
                var room = new MultiplayerRoom(
                    roomId,
                    hostUserId,
                    createdAt,
                    MultiplayerRoomStatus.WaitingForPlayers,
                    Array.AsReadOnly(seats),
                    Array.AsReadOnly(new[] { new RoomMembership(roomId, hostUserId, PlayerPosition.South, true, createdAt) }));
                this.rooms.Add(roomId, room);
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

        public ValueTask<SeatAssignmentResult> AssignSeatAsync(
            string roomId,
            string userId,
            PlayerPosition seat,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.sync)
            {
                if (!this.rooms.TryGetValue(roomId, out var room))
                {
                    return ValueTask.FromResult(new SeatAssignmentResult(SeatAssignmentStatus.RoomNotFound));
                }

                if (room.Members.Any(member => string.Equals(member.UserId, userId, StringComparison.Ordinal)))
                {
                    return ValueTask.FromResult(new SeatAssignmentResult(SeatAssignmentStatus.UserAlreadyMember));
                }

                var seatIndex = -1;
                for (var index = 0; index < room.Seats.Count; index++)
                {
                    if (room.Seats[index].Position == seat)
                    {
                        seatIndex = index;
                        break;
                    }
                }

                if (seatIndex < 0 || seat == PlayerPosition.South)
                {
                    return ValueTask.FromResult(new SeatAssignmentResult(SeatAssignmentStatus.InvalidSeat));
                }

                if (room.Seats[seatIndex].OwnerUserId is not null)
                {
                    return ValueTask.FromResult(new SeatAssignmentResult(SeatAssignmentStatus.SeatOccupied));
                }

                var joinedAt = DateTimeOffset.UtcNow;
                var seats = room.Seats.ToArray();
                seats[seatIndex] = new MultiplayerSeat(seat, userId, false);
                var members = room.Members
                    .Append(new RoomMembership(roomId, userId, seat, false, joinedAt))
                    .ToArray();
                var updatedRoom = room with
                {
                    Seats = Array.AsReadOnly(seats),
                    Members = Array.AsReadOnly(members),
                };
                this.rooms[roomId] = updatedRoom;
                return ValueTask.FromResult(new SeatAssignmentResult(SeatAssignmentStatus.Assigned, updatedRoom));
            }
        }

        private static string CreateRoomId()
        {
            Span<byte> bytes = stackalloc byte[32];
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
            CancellationToken cancellationToken)
        {
            if (!TryGetUserId(caller, out var userId))
            {
                return new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.Unauthorized);
            }

            var room = await this.repository.CreateAsync(userId, cancellationToken);
            return new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.Success, room);
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
            PlayerPosition? seat,
            CancellationToken cancellationToken)
        {
            if (!TryGetUserId(caller, out var userId))
            {
                return new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.Unauthorized);
            }

            if (seat is not (PlayerPosition.East or PlayerPosition.North or PlayerPosition.West))
            {
                return new MultiplayerRoomServiceResult(
                    MultiplayerRoomServiceStatus.InvalidSeat,
                    Error: "Choose an available joining seat: East, North, or West.");
            }

            var assignment = await this.repository.AssignSeatAsync(roomId, userId, seat.Value, cancellationToken);
            return assignment.Status switch
            {
                SeatAssignmentStatus.Assigned => new MultiplayerRoomServiceResult(
                    MultiplayerRoomServiceStatus.Success,
                    assignment.Room),
                SeatAssignmentStatus.RoomNotFound => new MultiplayerRoomServiceResult(MultiplayerRoomServiceStatus.NotFound),
                SeatAssignmentStatus.UserAlreadyMember => new MultiplayerRoomServiceResult(
                    MultiplayerRoomServiceStatus.Conflict,
                    Error: "This user already owns a seat in the room."),
                SeatAssignmentStatus.SeatOccupied => new MultiplayerRoomServiceResult(
                    MultiplayerRoomServiceStatus.Conflict,
                    Error: "That seat is already occupied."),
                SeatAssignmentStatus.InvalidSeat => new MultiplayerRoomServiceResult(
                    MultiplayerRoomServiceStatus.InvalidSeat,
                    Error: "Choose an available joining seat: East, North, or West."),
                _ => throw new InvalidOperationException($"Unexpected seat assignment result: {assignment.Status}."),
            };
        }

        private static bool TryGetUserId(ClaimsPrincipal caller, out string userId)
        {
            userId = caller.FindFirst("sub")?.Value ?? string.Empty;
            return caller.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(userId);
        }
    }

    internal static class MultiplayerRoomMapper
    {
        public static MultiplayerRoomDto ToDto(MultiplayerRoom room, string userId)
        {
            var membership = room.Members.Single(member => string.Equals(member.UserId, userId, StringComparison.Ordinal));
            var seats = room.Seats
                .Select(seat => new MultiplayerSeatDto(
                    seat.Position,
                    seat.Team,
                    seat.OwnerUserId is not null,
                    seat.IsHost))
                .ToArray();
            return new MultiplayerRoomDto(
                room.RoomId,
                room.Status,
                room.CreatedAt,
                membership.Seat,
                seats);
        }
    }
}
