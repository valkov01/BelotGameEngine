namespace Belot.WebApi;

using System.Security.Cryptography;
using System.Text.Json;

using Belot.Engine.Players;

using Npgsql;
using NpgsqlTypes;

internal sealed class PostgresMultiplayerRoomRepository : IMultiplayerRoomRepository
{
    private readonly NpgsqlDataSource dataSource;

    public PostgresMultiplayerRoomRepository(NpgsqlDataSource dataSource) => this.dataSource = dataSource;

    public async ValueTask<MultiplayerRoom> CreateAsync(
        string hostUserId,
        int maximumHumanPlayers,
        IReadOnlyDictionary<string, string> botProfiles,
        byte[] invitationCodeHash,
        CancellationToken cancellationToken)
    {
        var roomId = CreateRoomId();
        var createdAt = DateTimeOffset.UtcNow;
        await using var connection = await this.dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var roomCommand = new NpgsqlCommand(
            """
            INSERT INTO multiplayer_rooms
                (room_id, host_user_id, created_at, status, maximum_human_players, bot_profiles, invitation_code_hash)
            VALUES
                (@room_id, @host_user_id, @created_at, 'WaitingForPlayers', @maximum_human_players, @bot_profiles, @invitation_code_hash);
            """,
            connection,
            transaction))
        {
            roomCommand.Parameters.AddWithValue("room_id", roomId);
            roomCommand.Parameters.AddWithValue("host_user_id", hostUserId);
            roomCommand.Parameters.AddWithValue("created_at", createdAt);
            roomCommand.Parameters.AddWithValue("maximum_human_players", NpgsqlDbType.Smallint, (short)maximumHumanPlayers);
            roomCommand.Parameters.AddWithValue("bot_profiles", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(botProfiles));
            roomCommand.Parameters.AddWithValue("invitation_code_hash", NpgsqlDbType.Bytea, invitationCodeHash);
            await roomCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var seat in InMemoryMultiplayerRoomRepository.SeatsInEngineOrder)
        {
            await using var seatCommand = new NpgsqlCommand(
                """
                INSERT INTO multiplayer_room_seats
                    (room_id, seat, owner_user_id, is_host, is_ready, bot_profile_id, joined_at)
                VALUES
                    (@room_id, @seat, @owner_user_id, @is_host, FALSE, NULL, @joined_at);
                """,
                connection,
                transaction);
            seatCommand.Parameters.AddWithValue("room_id", roomId);
            seatCommand.Parameters.AddWithValue("seat", seat.ToString());
            seatCommand.Parameters.AddWithValue(
                "owner_user_id",
                NpgsqlDbType.Text,
                seat == PlayerPosition.South ? hostUserId : DBNull.Value);
            seatCommand.Parameters.AddWithValue("is_host", seat == PlayerPosition.South);
            seatCommand.Parameters.AddWithValue(
                "joined_at",
                NpgsqlDbType.TimestampTz,
                seat == PlayerPosition.South ? createdAt : DBNull.Value);
            await seatCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var room = await this.FindAsync(roomId, cancellationToken);
        return room ?? throw new InvalidOperationException("The newly created multiplayer room could not be read.");
    }

    public async ValueTask<MultiplayerRoom?> FindAsync(string roomId, CancellationToken cancellationToken)
    {
        await using var connection = await this.dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadRoomAsync(connection, transaction: null, roomId, cancellationToken);
    }

    public async ValueTask<RoomMembership?> FindMembershipAsync(
        string roomId,
        string userId,
        CancellationToken cancellationToken)
    {
        var room = await this.FindAsync(roomId, cancellationToken);
        return room?.Members.FirstOrDefault(member => string.Equals(member.UserId, userId, StringComparison.Ordinal));
    }

    public async ValueTask<MultiplayerRoomRepositoryResult> JoinAsync(
        string roomId,
        string userId,
        PlayerPosition seat,
        byte[] invitationCodeHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await this.dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var roomRow = await LockRoomAsync(connection, transaction, roomId, cancellationToken);
        if (roomRow is null)
        {
            return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotFound);
        }

        if (roomRow.Value.Status != MultiplayerRoomStatus.WaitingForPlayers)
        {
            return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.LobbyClosed);
        }

        if (!CryptographicOperations.FixedTimeEquals(roomRow.Value.InvitationCodeHash, invitationCodeHash))
        {
            return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.InvalidInvitation);
        }

        await using (var memberCommand = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM multiplayer_room_seats WHERE room_id = @room_id AND owner_user_id = @user_id);",
            connection,
            transaction))
        {
            memberCommand.Parameters.AddWithValue("room_id", roomId);
            memberCommand.Parameters.AddWithValue("user_id", userId);
            if ((bool)(await memberCommand.ExecuteScalarAsync(cancellationToken))!)
            {
                return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.UserAlreadyMember);
            }
        }

        await using (var countCommand = new NpgsqlCommand(
            "SELECT COUNT(*) FROM multiplayer_room_seats WHERE room_id = @room_id AND owner_user_id IS NOT NULL;",
            connection,
            transaction))
        {
            countCommand.Parameters.AddWithValue("room_id", roomId);
            if (Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture)
                >= roomRow.Value.MaximumHumanPlayers)
            {
                return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.RoomFull);
            }
        }

        await using (var seatCommand = new NpgsqlCommand(
            """
            UPDATE multiplayer_room_seats
            SET owner_user_id = @user_id, joined_at = @joined_at
            WHERE room_id = @room_id AND seat = @seat
                AND owner_user_id IS NULL AND bot_profile_id IS NULL;
            """,
            connection,
            transaction))
        {
            seatCommand.Parameters.AddWithValue("user_id", userId);
            seatCommand.Parameters.AddWithValue("joined_at", DateTimeOffset.UtcNow);
            seatCommand.Parameters.AddWithValue("room_id", roomId);
            seatCommand.Parameters.AddWithValue("seat", seat.ToString());
            if (await seatCommand.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.SeatOccupied);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        var room = await this.FindAsync(roomId, cancellationToken);
        return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.Success, room);
    }

    public async ValueTask<MultiplayerRoomRepositoryResult> SetReadyAsync(
        string roomId,
        string userId,
        bool isReady,
        CancellationToken cancellationToken)
    {
        await using var connection = await this.dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var roomRow = await LockRoomAsync(connection, transaction, roomId, cancellationToken);
        if (roomRow is null)
        {
            return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotFound);
        }

        if (roomRow.Value.Status != MultiplayerRoomStatus.WaitingForPlayers)
        {
            return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.LobbyClosed);
        }

        await using (var command = new NpgsqlCommand(
            """
            UPDATE multiplayer_room_seats SET is_ready = @is_ready
            WHERE room_id = @room_id AND owner_user_id = @user_id;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("is_ready", isReady);
            command.Parameters.AddWithValue("room_id", roomId);
            command.Parameters.AddWithValue("user_id", userId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotMember);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new MultiplayerRoomRepositoryResult(
            MultiplayerRoomRepositoryStatus.Success,
            await this.FindAsync(roomId, cancellationToken));
    }

    public async ValueTask<MultiplayerRoomRepositoryResult> BeginStartAsync(
        string roomId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await this.dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var roomRow = await LockRoomAsync(connection, transaction, roomId, cancellationToken);
        if (roomRow is null)
        {
            return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotFound);
        }

        if (!string.Equals(roomRow.Value.HostUserId, userId, StringComparison.Ordinal))
        {
            return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotHost);
        }

        if (roomRow.Value.Status == MultiplayerRoomStatus.Started)
        {
            return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.LobbyClosed);
        }

        if (roomRow.Value.Status == MultiplayerRoomStatus.Starting)
        {
            await transaction.CommitAsync(cancellationToken);
            return new MultiplayerRoomRepositoryResult(
                MultiplayerRoomRepositoryStatus.Success,
                await this.FindAsync(roomId, cancellationToken));
        }

        await using (var membersCommand = new NpgsqlCommand(
            """
            SELECT COUNT(*), COALESCE(bool_and(is_ready), FALSE)
            FROM multiplayer_room_seats
            WHERE room_id = @room_id AND owner_user_id IS NOT NULL;
            """,
            connection,
            transaction))
        {
            membersCommand.Parameters.AddWithValue("room_id", roomId);
            await using var reader = await membersCommand.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt64(0) < 2)
            {
                return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotEnoughPlayers);
            }

            if (!reader.GetBoolean(1))
            {
                return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.MembersNotReady);
            }
        }

        await using (var fillBotsCommand = new NpgsqlCommand(
            """
            UPDATE multiplayer_room_seats AS seat
            SET bot_profile_id = room.bot_profiles ->> seat.seat
            FROM multiplayer_rooms AS room
            WHERE room.room_id = @room_id
                AND seat.room_id = room.room_id
                AND seat.owner_user_id IS NULL;
            """,
            connection,
            transaction))
        {
            fillBotsCommand.Parameters.AddWithValue("room_id", roomId);
            await fillBotsCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateStatusCommand = new NpgsqlCommand(
            "UPDATE multiplayer_rooms SET status = 'Starting' WHERE room_id = @room_id;",
            connection,
            transaction))
        {
            updateStatusCommand.Parameters.AddWithValue("room_id", roomId);
            await updateStatusCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new MultiplayerRoomRepositoryResult(
            MultiplayerRoomRepositoryStatus.Success,
            await this.FindAsync(roomId, cancellationToken));
    }

    public async ValueTask<MultiplayerRoomRepositoryResult> CompleteStartAsync(
        string roomId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await this.dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var roomRow = await LockRoomAsync(connection, transaction, roomId, cancellationToken);
        if (roomRow is null)
        {
            return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotFound);
        }

        if (!string.Equals(roomRow.Value.HostUserId, userId, StringComparison.Ordinal))
        {
            return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.NotHost);
        }

        if (roomRow.Value.Status != MultiplayerRoomStatus.Starting)
        {
            return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.LobbyClosed);
        }

        await using (var updateStatusCommand = new NpgsqlCommand(
            "UPDATE multiplayer_rooms SET status = 'Started' WHERE room_id = @room_id AND status = 'Starting';",
            connection,
            transaction))
        {
            updateStatusCommand.Parameters.AddWithValue("room_id", roomId);
            if (await updateStatusCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return new MultiplayerRoomRepositoryResult(MultiplayerRoomRepositoryStatus.LobbyClosed);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new MultiplayerRoomRepositoryResult(
            MultiplayerRoomRepositoryStatus.Success,
            await this.FindAsync(roomId, cancellationToken));
    }

    private static async ValueTask<(string HostUserId, MultiplayerRoomStatus Status, int MaximumHumanPlayers, byte[] InvitationCodeHash)?> LockRoomAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string roomId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT host_user_id, status, maximum_human_players, invitation_code_hash
            FROM multiplayer_rooms WHERE room_id = @room_id FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("room_id", roomId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.GetString(0),
            Enum.Parse<MultiplayerRoomStatus>(reader.GetString(1), ignoreCase: false),
            reader.GetInt16(2),
            reader.GetFieldValue<byte[]>(3));
    }

    private static async ValueTask<MultiplayerRoom?> ReadRoomAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string roomId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT room.room_id, room.host_user_id, room.created_at, room.status, room.maximum_human_players,
                room.bot_profiles::text, seat.seat, seat.owner_user_id, seat.is_host, seat.is_ready,
                seat.bot_profile_id, seat.joined_at
            FROM multiplayer_rooms AS room
            JOIN multiplayer_room_seats AS seat ON seat.room_id = room.room_id
            WHERE room.room_id = @room_id
            ORDER BY array_position(ARRAY['South', 'East', 'North', 'West']::text[], seat.seat);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("room_id", roomId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var id = reader.GetString(0);
        var hostUserId = reader.GetString(1);
        var createdAt = reader.GetFieldValue<DateTimeOffset>(2);
        var status = Enum.Parse<MultiplayerRoomStatus>(reader.GetString(3), ignoreCase: false);
        var maximumHumanPlayers = reader.GetInt16(4);
        var botProfiles = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5))
            ?? throw new InvalidOperationException("Stored bot profile configuration is invalid.");
        var seats = new List<MultiplayerSeat>(4);
        var members = new List<RoomMembership>(4);
        do
        {
            var seat = Enum.Parse<PlayerPosition>(reader.GetString(6), ignoreCase: false);
            var owner = reader.IsDBNull(7) ? null : reader.GetString(7);
            var isHost = reader.GetBoolean(8);
            var isReady = reader.GetBoolean(9);
            var botProfileId = reader.IsDBNull(10) ? null : reader.GetString(10);
            var joinedAt = reader.IsDBNull(11) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(11);
            seats.Add(new MultiplayerSeat(seat, owner, isHost, isReady, botProfileId));
            if (owner is not null && joinedAt.HasValue)
            {
                members.Add(new RoomMembership(id, owner, seat, isHost, joinedAt.Value, isReady));
            }
        }
        while (await reader.ReadAsync(cancellationToken));

        return new MultiplayerRoom(
            id,
            hostUserId,
            createdAt,
            status,
            maximumHumanPlayers,
            botProfiles,
            seats.AsReadOnly(),
            members.AsReadOnly());
    }

    private static string CreateRoomId()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
