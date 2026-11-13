namespace Belot.WebApi;

using System.Security.Claims;

using Belot.Engine.Players;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

public sealed record MultiplayerEventCursor(long Revision, int Sequence);

public sealed record MultiplayerRealtimeSubscriptionResult(
    bool SnapshotRequired,
    long LatestRevision,
    int LatestSequence);

public sealed record MultiplayerGameEventEnvelope(
    string GameId,
    long Revision,
    int Sequence,
    PublicGameEventDto Event);

public sealed record MultiplayerLobbySeatEvent(
    string Seat,
    string Team,
    bool IsOccupied,
    bool IsHost,
    bool IsReady,
    string? BotProfileId);

public sealed record MultiplayerLobbyEventEnvelope(
    string RoomId,
    long Revision,
    int Sequence,
    string Type,
    string? Seat,
    MultiplayerRoomStatus Status,
    int HumanPlayerCount,
    int MaximumHumanPlayers,
    IReadOnlyList<MultiplayerLobbySeatEvent> Seats);

public interface IMultiplayerRealtimePublisher
{
    Task<T> WithLobbyLockAsync<T>(
        string roomId,
        Func<Task<T>> operation,
        CancellationToken cancellationToken);

    MultiplayerEventCursor GetGameCursor(string gameId, long revision);

    MultiplayerEventCursor GetLobbyCursor(string roomId);

    Task PublishGameEventsAsync(
        string gameId,
        long revision,
        IReadOnlyList<PublicGameEventDto> events,
        CancellationToken cancellationToken);

    Task PublishLobbyChangeAsync(
        MultiplayerRoom room,
        string type,
        PlayerPosition? seat,
        CancellationToken cancellationToken);

    Task<MultiplayerRealtimeSubscriptionResult> SubscribeGameAsync(
        string connectionId,
        string gameId,
        long afterRevision,
        int afterSequence,
        long currentRevision,
        CancellationToken cancellationToken);

    Task<MultiplayerRealtimeSubscriptionResult> SubscribeRoomAsync(
        string connectionId,
        string roomId,
        long afterRevision,
        int afterSequence,
        CancellationToken cancellationToken);
}

internal sealed class MultiplayerRealtimePublisher : IMultiplayerRealtimePublisher
{
    private const int DefaultEventsPerGame = 512;

    private const int DefaultEventsPerLobby = 128;

    private const int MaximumTopics = 512;

    private const int LobbyLockStripeCount = 64;

    private const string GameEventName = "GameEvent";

    private const string LobbyEventName = "LobbyEvent";

    private const string SnapshotRequiredEventName = "SnapshotRequired";

    private readonly IHubContext<MultiplayerRealtimeHub> hub;

    private readonly ILogger<MultiplayerRealtimePublisher> logger;

    private readonly SemaphoreSlim publishGate = new(1, 1);

    private readonly SemaphoreSlim[] lobbyLocks = Enumerable
        .Range(0, LobbyLockStripeCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    private readonly object sync = new();

    private readonly Dictionary<string, GameTopic> gameTopics = new(StringComparer.Ordinal);

    private readonly Dictionary<string, LobbyTopic> lobbyTopics = new(StringComparer.Ordinal);

    private readonly int eventsPerGame;

    private readonly int eventsPerLobby;

    private long lobbyRevision;

    public MultiplayerRealtimePublisher(
        IHubContext<MultiplayerRealtimeHub> hub,
        IConfiguration configuration,
        ILogger<MultiplayerRealtimePublisher> logger)
    {
        this.hub = hub;
        this.logger = logger;
        this.eventsPerGame = Math.Clamp(configuration.GetValue("Multiplayer:Realtime:EventsPerGame", DefaultEventsPerGame), 32, 4096);
        this.eventsPerLobby = Math.Clamp(configuration.GetValue("Multiplayer:Realtime:EventsPerLobby", DefaultEventsPerLobby), 16, 1024);
    }

    public async Task<T> WithLobbyLockAsync<T>(
        string roomId,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var lobbyLock = this.lobbyLocks[(StringComparer.Ordinal.GetHashCode(roomId) & int.MaxValue) % LobbyLockStripeCount];
        await lobbyLock.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            lobbyLock.Release();
        }
    }

    public MultiplayerEventCursor GetGameCursor(string gameId, long revision)
    {
        lock (this.sync)
        {
            return this.gameTopics.TryGetValue(gameId, out var topic)
                ? new MultiplayerEventCursor(topic.Revision, topic.Sequence)
                : new MultiplayerEventCursor(revision, -1);
        }
    }

    public MultiplayerEventCursor GetLobbyCursor(string roomId)
    {
        lock (this.sync)
        {
            if (!this.lobbyTopics.TryGetValue(roomId, out var topic))
            {
                topic = CreateLobbyTopic(Interlocked.Increment(ref this.lobbyRevision));
                this.lobbyTopics.Add(roomId, topic);
                this.TrimTopics();
            }

            topic.LastAccess = DateTimeOffset.UtcNow;
            return new MultiplayerEventCursor(topic.Revision, topic.Sequence);
        }
    }

    public async Task PublishGameEventsAsync(
        string gameId,
        long revision,
        IReadOnlyList<PublicGameEventDto> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        await this.publishGate.WaitAsync(cancellationToken);
        try
        {
            var group = GameGroup(gameId);
            foreach (var gameEvent in events)
            {
                var envelope = new MultiplayerGameEventEnvelope(gameId, revision, gameEvent.Sequence, gameEvent);
                if (!this.TryAppendGameEvent(envelope))
                {
                    continue;
                }

                await this.TryBroadcastAsync(group, GameEventName, envelope, cancellationToken);
            }
        }
        finally
        {
            this.publishGate.Release();
        }
    }

    public async Task PublishLobbyChangeAsync(
        MultiplayerRoom room,
        string type,
        PlayerPosition? seat,
        CancellationToken cancellationToken)
    {
        await this.publishGate.WaitAsync(cancellationToken);
        try
        {
            var envelope = this.AppendLobbyEvent(room, type, seat);
            await this.TryBroadcastAsync(LobbyGroup(room.RoomId), LobbyEventName, envelope, cancellationToken);
        }
        finally
        {
            this.publishGate.Release();
        }
    }

    public async Task<MultiplayerRealtimeSubscriptionResult> SubscribeGameAsync(
        string connectionId,
        string gameId,
        long afterRevision,
        int afterSequence,
        long currentRevision,
        CancellationToken cancellationToken)
    {
        await this.publishGate.WaitAsync(cancellationToken);
        try
        {
            var group = GameGroup(gameId);
            await this.hub.Groups.AddToGroupAsync(connectionId, group, cancellationToken);
            var (events, cursor, snapshotRequired) = this.GetGameReplay(gameId, afterRevision, afterSequence, currentRevision);
            if (snapshotRequired)
            {
                await this.SendSnapshotRequiredAsync(connectionId, "game", gameId, cursor, cancellationToken);
            }
            else
            {
                foreach (var gameEvent in events)
                {
                    await this.hub.Clients.Client(connectionId)
                        .SendAsync(GameEventName, gameEvent, cancellationToken);
                }
            }

            return new MultiplayerRealtimeSubscriptionResult(snapshotRequired, cursor.Revision, cursor.Sequence);
        }
        finally
        {
            this.publishGate.Release();
        }
    }

    public async Task<MultiplayerRealtimeSubscriptionResult> SubscribeRoomAsync(
        string connectionId,
        string roomId,
        long afterRevision,
        int afterSequence,
        CancellationToken cancellationToken)
    {
        await this.publishGate.WaitAsync(cancellationToken);
        try
        {
            var group = LobbyGroup(roomId);
            await this.hub.Groups.AddToGroupAsync(connectionId, group, cancellationToken);
            var (events, cursor, snapshotRequired) = this.GetLobbyReplay(roomId, afterRevision, afterSequence);
            if (snapshotRequired)
            {
                await this.SendSnapshotRequiredAsync(connectionId, "room", roomId, cursor, cancellationToken);
            }
            else
            {
                foreach (var lobbyEvent in events)
                {
                    await this.hub.Clients.Client(connectionId)
                        .SendAsync(LobbyEventName, lobbyEvent, cancellationToken);
                }
            }

            return new MultiplayerRealtimeSubscriptionResult(snapshotRequired, cursor.Revision, cursor.Sequence);
        }
        finally
        {
            this.publishGate.Release();
        }
    }

    internal static string GameGroup(string gameId) => $"multiplayer:game:{gameId}";

    internal static string LobbyGroup(string roomId) => $"multiplayer:room:{roomId}";

    private bool TryAppendGameEvent(MultiplayerGameEventEnvelope envelope)
    {
        lock (this.sync)
        {
            if (!this.gameTopics.TryGetValue(envelope.GameId, out var topic))
            {
                topic = new GameTopic(
                    envelope.Revision,
                    -1,
                    envelope.Revision > 1 || envelope.Sequence > 0);
                this.gameTopics.Add(envelope.GameId, topic);
            }

            if (envelope.Revision < topic.Revision
                || (envelope.Revision == topic.Revision && envelope.Sequence <= topic.Sequence))
            {
                return false;
            }

            topic.Events.Add(envelope);
            topic.Revision = envelope.Revision;
            topic.Sequence = envelope.Sequence;
            topic.LastAccess = DateTimeOffset.UtcNow;
            if (topic.Events.Count > this.eventsPerGame)
            {
                topic.Events.RemoveAt(0);
                topic.Truncated = true;
            }

            this.TrimTopics();
            return true;
        }
    }

    private MultiplayerLobbyEventEnvelope AppendLobbyEvent(
        MultiplayerRoom room,
        string type,
        PlayerPosition? seat)
    {
        lock (this.sync)
        {
            if (!this.lobbyTopics.TryGetValue(room.RoomId, out var topic))
            {
                topic = CreateLobbyTopic(Interlocked.Increment(ref this.lobbyRevision));
                this.lobbyTopics.Add(room.RoomId, topic);
            }

            var envelope = new MultiplayerLobbyEventEnvelope(
                room.RoomId,
                Interlocked.Increment(ref this.lobbyRevision),
                0,
                type,
                seat?.ToString(),
                room.Status,
                room.Members.Count,
                room.MaximumHumanPlayers,
                room.Seats.Select(ToPublicSeat).ToArray());
            topic.Events.Add(envelope);
            topic.Revision = envelope.Revision;
            topic.Sequence = envelope.Sequence;
            topic.LastAccess = DateTimeOffset.UtcNow;
            if (topic.Events.Count > this.eventsPerLobby)
            {
                topic.Events.RemoveAt(0);
                topic.Truncated = true;
            }

            this.TrimTopics();
            return envelope;
        }
    }

    private (IReadOnlyList<MultiplayerGameEventEnvelope> Events, MultiplayerEventCursor Cursor, bool SnapshotRequired)
        GetGameReplay(string gameId, long afterRevision, int afterSequence, long currentRevision)
    {
        lock (this.sync)
        {
            if (!this.gameTopics.TryGetValue(gameId, out var topic))
            {
                var cursor = new MultiplayerEventCursor(currentRevision, -1);
                var needsSnapshot = afterRevision != currentRevision || afterSequence != cursor.Sequence;
                return (Array.Empty<MultiplayerGameEventEnvelope>(), cursor, needsSnapshot);
            }

            topic.LastAccess = DateTimeOffset.UtcNow;
            var latest = new MultiplayerEventCursor(Math.Max(topic.Revision, currentRevision), topic.Revision >= currentRevision
                ? topic.Sequence
                : -1);
            var events = topic.Events
                .Where(gameEvent => IsAfter(gameEvent.Revision, gameEvent.Sequence, afterRevision, afterSequence))
                .ToArray();
            var snapshotRequired = IsReplayGap(
                topic.Events.Select(gameEvent => new MultiplayerEventCursor(gameEvent.Revision, gameEvent.Sequence)).ToArray(),
                topic.Truncated,
                afterRevision,
                afterSequence,
                latest);
            return (events, latest, snapshotRequired);
        }
    }

    private (IReadOnlyList<MultiplayerLobbyEventEnvelope> Events, MultiplayerEventCursor Cursor, bool SnapshotRequired)
        GetLobbyReplay(string roomId, long afterRevision, int afterSequence)
    {
        lock (this.sync)
        {
            var latestRevision = Interlocked.Read(ref this.lobbyRevision);
            if (!this.lobbyTopics.TryGetValue(roomId, out var topic))
            {
                var cursor = new MultiplayerEventCursor(latestRevision, -1);
                var needsSnapshot = afterRevision != cursor.Revision || afterSequence != cursor.Sequence;
                return (Array.Empty<MultiplayerLobbyEventEnvelope>(), cursor, needsSnapshot);
            }

            topic.LastAccess = DateTimeOffset.UtcNow;
            var latest = new MultiplayerEventCursor(topic.Revision, topic.Sequence);
            var events = topic.Events
                .Where(lobbyEvent => IsAfter(lobbyEvent.Revision, lobbyEvent.Sequence, afterRevision, afterSequence))
                .ToArray();
            var snapshotRequired = IsLobbyReplayGap(topic, afterRevision, afterSequence, latest);
            return (events, latest, snapshotRequired);
        }
    }

    private static LobbyTopic CreateLobbyTopic(long revision) => new(revision);

    private async Task TryBroadcastAsync<T>(
        string group,
        string eventName,
        T value,
        CancellationToken cancellationToken)
    {
        try
        {
            await this.hub.Clients.Group(group).SendAsync(eventName, value, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            this.logger.LogWarning(exception, "SignalR delivery failed for event {EventName} in group {Group}.", eventName, group);
        }
    }

    private async Task SendSnapshotRequiredAsync(
        string connectionId,
        string resourceType,
        string resourceId,
        MultiplayerEventCursor cursor,
        CancellationToken cancellationToken)
    {
        try
        {
            await this.hub.Clients.Client(connectionId).SendAsync(
                SnapshotRequiredEventName,
                new
                {
                    resourceType,
                    resourceId,
                    revision = cursor.Revision,
                    sequence = cursor.Sequence,
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            this.logger.LogWarning(exception, "SignalR snapshot-required notice failed for {ResourceType} {ResourceId}.", resourceType, resourceId);
        }
    }

    private void TrimTopics()
    {
        while (this.gameTopics.Count + this.lobbyTopics.Count > MaximumTopics)
        {
            var oldestGame = this.gameTopics
                .MinBy(entry => entry.Value.LastAccess);
            var oldestLobby = this.lobbyTopics.MinBy(entry => entry.Value.LastAccess);
            if (oldestGame.Value is null && oldestLobby.Value is null)
            {
                return;
            }

            if (oldestGame.Value is not null
                && (oldestLobby.Value is null || oldestGame.Value.LastAccess <= oldestLobby.Value.LastAccess))
            {
                this.gameTopics.Remove(oldestGame.Key);
            }
            else
            {
                this.lobbyTopics.Remove(oldestLobby.Key);
            }
        }
    }

    private static bool IsReplayGap(
        IReadOnlyList<MultiplayerEventCursor> retained,
        bool truncated,
        long afterRevision,
        int afterSequence,
        MultiplayerEventCursor latest)
    {
        if (afterRevision > latest.Revision
            || (afterRevision == latest.Revision && afterSequence > latest.Sequence))
        {
            return true;
        }

        if (afterRevision == latest.Revision && afterSequence == latest.Sequence)
        {
            return false;
        }

        if (retained.Count == 0)
        {
            return afterRevision < latest.Revision || afterSequence < latest.Sequence;
        }

        var cursorRetained = retained.Any(cursor =>
            cursor.Revision == afterRevision && cursor.Sequence == afterSequence);
        if (cursorRetained)
        {
            return false;
        }

        var cursorBeforeFirst = afterRevision == 0
            && afterSequence == -1
            && !truncated
            && retained[0].Revision == 1
            && retained[0].Sequence == 0;
        return !cursorBeforeFirst;
    }

    private static bool IsAfter(long revision, int sequence, long afterRevision, int afterSequence) =>
        revision > afterRevision || (revision == afterRevision && sequence > afterSequence);

    private static MultiplayerLobbySeatEvent ToPublicSeat(MultiplayerSeat seat) =>
        new(
            seat.Position.ToString(),
            seat.Team.ToString(),
            seat.OwnerUserId is not null || seat.BotProfileId is not null,
            seat.IsHost,
            seat.IsReady,
            seat.BotProfileId);

    private sealed class GameTopic(long revision, int sequence, bool truncated)
    {
        public List<MultiplayerGameEventEnvelope> Events { get; } = new();

        public long Revision { get; set; } = revision;

        public int Sequence { get; set; } = sequence;

        public DateTimeOffset LastAccess { get; set; } = DateTimeOffset.UtcNow;

        public bool Truncated { get; set; } = truncated;
    }

    private sealed class LobbyTopic(long revision)
    {
        public List<MultiplayerLobbyEventEnvelope> Events { get; } = new();

        public long Revision { get; set; } = revision;

        public int Sequence { get; set; } = -1;

        public long BaselineRevision { get; } = revision;

        public DateTimeOffset LastAccess { get; set; } = DateTimeOffset.UtcNow;

        public bool Truncated { get; set; }
    }

    private static bool IsLobbyReplayGap(
        LobbyTopic topic,
        long afterRevision,
        int afterSequence,
        MultiplayerEventCursor latest)
    {
        if (afterRevision > latest.Revision
            || (afterRevision == latest.Revision && afterSequence > latest.Sequence))
        {
            return true;
        }

        if (afterRevision == latest.Revision && afterSequence == latest.Sequence)
        {
            return false;
        }

        if (topic.Events.Any(lobbyEvent =>
            lobbyEvent.Revision == afterRevision && lobbyEvent.Sequence == afterSequence))
        {
            return false;
        }

        if (afterRevision == topic.BaselineRevision && afterSequence == -1)
        {
            return topic.Truncated;
        }

        return true;
    }
}

[Authorize(Policy = "Multiplayer")]
public sealed class MultiplayerRealtimeHub(
    IMultiplayerRoomRepository rooms,
    IMultiplayerGameRevisionProvider games,
    IMultiplayerRealtimePublisher realtime) : Hub
{
    public async Task<MultiplayerRealtimeSubscriptionResult> JoinRoom(
        string roomId,
        long afterRevision,
        int afterSequence)
    {
        var userId = GetAuthenticatedUserId(this.Context.User);
        var membership = await rooms.FindMembershipAsync(roomId, userId, this.Context.ConnectionAborted);
        if (membership is null)
        {
            throw new HubException("The authenticated user is not a member of this room.");
        }

        return await realtime.SubscribeRoomAsync(
            this.Context.ConnectionId,
            roomId,
            afterRevision,
            afterSequence,
            this.Context.ConnectionAborted);
    }

    public async Task<MultiplayerRealtimeSubscriptionResult> JoinGame(
        string gameId,
        long afterRevision,
        int afterSequence)
    {
        var userId = GetAuthenticatedUserId(this.Context.User);
        var room = await rooms.FindAsync(gameId, this.Context.ConnectionAborted);
        var membership = await rooms.FindMembershipAsync(gameId, userId, this.Context.ConnectionAborted);
        if (room?.Status != MultiplayerRoomStatus.Started || membership is null)
        {
            throw new HubException("The authenticated user is not a member of a started game.");
        }

        if (!games.TryGetRevision(gameId, out var currentRevision))
        {
            throw new HubException("The in-memory game is unavailable; fetch the lobby state and reconnect after a restart.");
        }

        return await realtime.SubscribeGameAsync(
            this.Context.ConnectionId,
            gameId,
            afterRevision,
            afterSequence,
            currentRevision,
            this.Context.ConnectionAborted);
    }

    private static string GetAuthenticatedUserId(ClaimsPrincipal? user)
    {
        var userId = user?.FindFirst("sub")?.Value;
        if (user?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId))
        {
            throw new HubException("A valid authenticated user is required.");
        }

        return userId;
    }
}
