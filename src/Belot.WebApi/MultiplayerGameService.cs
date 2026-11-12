namespace Belot.WebApi;

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;

using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

/// <summary>A private multiplayer game snapshot scoped to one authenticated seat.</summary>
public sealed record MultiplayerGameSnapshotDto(
    string GameId,
    long Revision,
    PlayerPosition YourSeat,
    GameStateDto State);

/// <summary>A successful multiplayer action and the public events it produced.</summary>
public sealed record MultiplayerGameActionResponse(
    long Revision,
    PlayerPosition YourSeat,
    GameStateDto State,
    IReadOnlyList<PublicGameEventDto> Events);

/// <summary>Explains a multiplayer action failure and the current revision when one is known.</summary>
public sealed record MultiplayerGameErrorDto(string Error, long? CurrentRevision);

/// <summary>Revision-checked action submitted by an authenticated room member.</summary>
public sealed record MultiplayerGameActionRequest
{
    /// <summary>The revision returned by the snapshot on which this action is based.</summary>
    public long ExpectedRevision { get; init; }

    /// <summary>A client-generated key reused verbatim when retrying this action.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>The bid for a bidding decision.</summary>
    public string? Bid { get; init; }

    /// <summary>The offered declarations selected for an announcement decision.</summary>
    public IReadOnlyList<AnnounceRequest>? Announces { get; init; }

    /// <summary>The card for a card-play decision.</summary>
    public CardDto? Card { get; init; }

    /// <summary>Whether this card play claims a belote.</summary>
    public bool Belote { get; init; }

    internal GameActionRequest ToGameActionRequest() => new()
    {
        Bid = this.Bid,
        Announces = this.Announces,
        Card = this.Card,
        Belote = this.Belote,
    };
}

internal enum MultiplayerGameServiceStatus
{
    Success,
    Unauthorized,
    Forbidden,
    NotFound,
    InvalidRequest,
    Conflict,
    Gone,
}

internal sealed record MultiplayerGameServiceResult(
    MultiplayerGameServiceStatus Status,
    MultiplayerRoom? Room = null,
    MultiplayerGameSnapshotDto? Snapshot = null,
    MultiplayerGameActionResponse? ActionResponse = null,
    long? CurrentRevision = null,
    string? Error = null);

internal interface IMultiplayerGameService
{
    bool IsGameAvailable(string roomId);

    Task<MultiplayerGameServiceResult> StartAsync(
        ClaimsPrincipal caller,
        string roomId,
        CancellationToken cancellationToken);

    Task<MultiplayerGameServiceResult> GetSnapshotAsync(
        ClaimsPrincipal caller,
        string gameId,
        CancellationToken cancellationToken);

    Task<MultiplayerGameServiceResult> SubmitActionAsync(
        ClaimsPrincipal caller,
        string gameId,
        MultiplayerGameActionRequest? request,
        CancellationToken cancellationToken);
}

internal sealed class MultiplayerGameStore
{
    private readonly ConcurrentDictionary<string, ActiveMultiplayerGame> games = new(StringComparer.Ordinal);

    public ActiveMultiplayerGame? Find(string gameId) =>
        this.games.TryGetValue(gameId, out var game) ? game : null;

    public bool TryAdd(ActiveMultiplayerGame game) => this.games.TryAdd(game.GameId, game);

    public bool Remove(string gameId, ActiveMultiplayerGame game) =>
        ((ICollection<KeyValuePair<string, ActiveMultiplayerGame>>)this.games)
            .Remove(new KeyValuePair<string, ActiveMultiplayerGame>(gameId, game));
}

internal sealed class ActiveMultiplayerGame
{
    public ActiveMultiplayerGame(
        string gameId,
        MultiplayerRoom room,
        BelotMatch match,
        IReadOnlyDictionary<string, string> bots,
        IPlayer?[] players)
    {
        this.GameId = gameId;
        this.Room = room;
        this.Match = match;
        this.Bots = bots;
        this.Players = players;
    }

    public string GameId { get; }

    public MultiplayerRoom Room { get; set; }

    public BelotMatch Match { get; }

    public IReadOnlyDictionary<string, string> Bots { get; }

    public IPlayer?[] Players { get; }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public long Revision { get; set; }

    public Dictionary<string, IdempotencyRecord> IdempotencyRecords { get; } = new(StringComparer.Ordinal);
}

internal sealed record IdempotencyRecord(
    string UserId,
    string Fingerprint,
    MultiplayerGameActionResponse Response);

internal sealed class MultiplayerGameService : IMultiplayerGameService
{
    private readonly IMultiplayerRoomRepository rooms;

    private readonly MultiplayerGameStore games;

    private readonly SemaphoreSlim startGate = new(1, 1);

    public MultiplayerGameService(IMultiplayerRoomRepository rooms, MultiplayerGameStore games)
    {
        this.rooms = rooms;
        this.games = games;
    }

    public bool IsGameAvailable(string roomId) => this.games.Find(roomId) is not null;

    public async Task<MultiplayerGameServiceResult> StartAsync(
        ClaimsPrincipal caller,
        string roomId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(caller, out var userId))
        {
            return new MultiplayerGameServiceResult(MultiplayerGameServiceStatus.Unauthorized);
        }

        await this.startGate.WaitAsync(cancellationToken);
        try
        {
            var room = await this.rooms.FindAsync(roomId, cancellationToken);
            if (room is null)
            {
                return new MultiplayerGameServiceResult(MultiplayerGameServiceStatus.NotFound);
            }

            if (!string.Equals(room.HostUserId, userId, StringComparison.Ordinal))
            {
                return new MultiplayerGameServiceResult(MultiplayerGameServiceStatus.Forbidden);
            }

            if (room.Status == MultiplayerRoomStatus.Started)
            {
                return this.games.Find(roomId) is not null
                    ? new MultiplayerGameServiceResult(MultiplayerGameServiceStatus.Success, room)
                    : new MultiplayerGameServiceResult(
                        MultiplayerGameServiceStatus.Gone,
                        Error: "The lobby started in an earlier process, but its in-memory match is unavailable.");
            }

            var begin = await this.rooms.BeginStartAsync(roomId, userId, cancellationToken);
            if (begin.Status != MultiplayerRoomRepositoryStatus.Success || begin.Room is null)
            {
                return MapRepositoryResult(begin);
            }

            var activeGame = this.games.Find(roomId);
            var createdHere = false;
            if (activeGame is null)
            {
                activeGame = CreateGame(begin.Room);
                createdHere = this.games.TryAdd(activeGame);
                if (!createdHere)
                {
                    activeGame = this.games.Find(roomId);
                }
            }

            if (activeGame is null)
            {
                return new MultiplayerGameServiceResult(
                    MultiplayerGameServiceStatus.Conflict,
                    Error: "Another start attempt is initializing this match.");
            }

            var complete = await this.rooms.CompleteStartAsync(roomId, userId, cancellationToken);
            if (complete.Status != MultiplayerRoomRepositoryStatus.Success || complete.Room is null)
            {
                if (createdHere)
                {
                    this.games.Remove(roomId, activeGame);
                }

                return MapRepositoryResult(complete);
            }

            activeGame.Room = complete.Room;
            return new MultiplayerGameServiceResult(MultiplayerGameServiceStatus.Success, complete.Room);
        }
        finally
        {
            this.startGate.Release();
        }
    }

    public async Task<MultiplayerGameServiceResult> GetSnapshotAsync(
        ClaimsPrincipal caller,
        string gameId,
        CancellationToken cancellationToken)
    {
        var access = await this.GetMemberRoomAsync(caller, gameId, cancellationToken);
        if (access.Result is not null)
        {
            return access.Result;
        }

        if (access.Room!.Status != MultiplayerRoomStatus.Started)
        {
            return new MultiplayerGameServiceResult(
                MultiplayerGameServiceStatus.Conflict,
                Error: "The lobby has not completed match initialization.");
        }

        var game = this.games.Find(gameId);
        if (game is null)
        {
            return new MultiplayerGameServiceResult(
                MultiplayerGameServiceStatus.Gone,
                Error: "The in-memory match is unavailable after a process restart.");
        }

        await game.Gate.WaitAsync(cancellationToken);
        try
        {
            var membership = access.Membership!;
            var state = ApiMapper.ToState(game.GameId, game.Bots, game.Match.GetView(membership.Seat));
            return new MultiplayerGameServiceResult(
                MultiplayerGameServiceStatus.Success,
                Snapshot: new MultiplayerGameSnapshotDto(game.GameId, game.Revision, membership.Seat, state));
        }
        finally
        {
            game.Gate.Release();
        }
    }

    public async Task<MultiplayerGameServiceResult> SubmitActionAsync(
        ClaimsPrincipal caller,
        string gameId,
        MultiplayerGameActionRequest? request,
        CancellationToken cancellationToken)
    {
        var access = await this.GetMemberRoomAsync(caller, gameId, cancellationToken);
        if (access.Result is not null)
        {
            return access.Result;
        }

        if (access.Room!.Status != MultiplayerRoomStatus.Started)
        {
            return new MultiplayerGameServiceResult(
                MultiplayerGameServiceStatus.Conflict,
                Error: "The lobby has not completed match initialization.");
        }

        var game = this.games.Find(gameId);
        if (game is null)
        {
            return new MultiplayerGameServiceResult(
                MultiplayerGameServiceStatus.Gone,
                Error: "The in-memory match is unavailable after a process restart.");
        }

        if (request is null || request.ExpectedRevision < 0
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.IdempotencyKey.Length > 128)
        {
            return new MultiplayerGameServiceResult(
                MultiplayerGameServiceStatus.InvalidRequest,
                Error: "Provide a non-negative expectedRevision and an idempotencyKey of 1 to 128 characters.");
        }

        await game.Gate.WaitAsync(cancellationToken);
        try
        {
            var userId = access.UserId
                ?? throw new InvalidOperationException("An authorized multiplayer member has no authenticated user ID.");
            var membership = access.Membership!;
            var fingerprint = JsonSerializer.Serialize(request);
            if (game.IdempotencyRecords.TryGetValue(request.IdempotencyKey, out var previous))
            {
                return string.Equals(previous.UserId, userId, StringComparison.Ordinal)
                    && string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? new MultiplayerGameServiceResult(
                        MultiplayerGameServiceStatus.Success,
                        ActionResponse: previous.Response)
                    : new MultiplayerGameServiceResult(
                        MultiplayerGameServiceStatus.Conflict,
                        CurrentRevision: game.Revision,
                        Error: "The idempotency key was already used for a different action.");
            }

            if (request.ExpectedRevision != game.Revision)
            {
                return new MultiplayerGameServiceResult(
                    MultiplayerGameServiceStatus.Conflict,
                    CurrentRevision: game.Revision,
                    Error: "The game revision is stale; read the current seat snapshot before acting.");
            }

            var match = game.Match;
            if (match.IsFinished)
            {
                return new MultiplayerGameServiceResult(
                    MultiplayerGameServiceStatus.Conflict,
                    CurrentRevision: game.Revision,
                    Error: "The match is finished.");
            }

            if (match.ToMove != membership.Seat)
            {
                return new MultiplayerGameServiceResult(
                    MultiplayerGameServiceStatus.Conflict,
                    CurrentRevision: game.Revision,
                    Error: "This authenticated seat is not currently to move.");
            }

            var before = match.GetView(membership.Seat);
            if (!GameService.TryBuildAction(before, request.ToGameActionRequest(), out var action, out var error))
            {
                return new MultiplayerGameServiceResult(
                    MultiplayerGameServiceStatus.InvalidRequest,
                    CurrentRevision: game.Revision,
                    Error: error);
            }

            var validation = match.Validate(membership.Seat, action);
            if (validation != BelotActResult.Ok)
            {
                return new MultiplayerGameServiceResult(
                    MultiplayerGameServiceStatus.InvalidRequest,
                    CurrentRevision: game.Revision,
                    Error: validation.ToString());
            }

            var events = new List<PublicGameEventDto>();
            var actResult = match.Act(membership.Seat, action);
            if (actResult != BelotActResult.Ok)
            {
                return new MultiplayerGameServiceResult(
                    MultiplayerGameServiceStatus.Conflict,
                    CurrentRevision: game.Revision,
                    Error: actResult.ToString());
            }

            var after = match.GetView(membership.Seat);
            events.AddRange(GameEventProjector.Project(before, after, membership.Seat, action, events.Count));

            while (!match.IsFinished && game.Players[match.ToMove.Index()] is { } bot)
            {
                var botSeat = match.ToMove;
                var botBefore = match.GetView(botSeat);
                var botAction = bot.Decide(botBefore);
                var botResult = match.Act(botSeat, botAction);
                if (botResult != BelotActResult.Ok)
                {
                    throw new InvalidOperationException($"Bot action was rejected: {botResult}.");
                }

                var botAfter = match.GetView(botSeat);
                events.AddRange(GameEventProjector.Project(
                    botBefore,
                    botAfter,
                    botSeat,
                    botAction,
                    events.Count,
                    actorIsAutomatic: true));
            }

            game.Revision++;
            var response = new MultiplayerGameActionResponse(
                game.Revision,
                membership.Seat,
                ApiMapper.ToState(game.GameId, game.Bots, match.GetView(membership.Seat)),
                events);
            game.IdempotencyRecords.Add(
                request.IdempotencyKey,
                new IdempotencyRecord(userId, fingerprint, response));
            return new MultiplayerGameServiceResult(
                MultiplayerGameServiceStatus.Success,
                ActionResponse: response);
        }
        finally
        {
            game.Gate.Release();
        }
    }

    private async Task<MemberRoomAccess> GetMemberRoomAsync(
        ClaimsPrincipal caller,
        string gameId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(caller, out var userId))
        {
            return new MemberRoomAccess(null, null, null, new MultiplayerGameServiceResult(MultiplayerGameServiceStatus.Unauthorized));
        }

        var room = await this.rooms.FindAsync(gameId, cancellationToken);
        if (room is null)
        {
            return new MemberRoomAccess(userId, null, null, new MultiplayerGameServiceResult(MultiplayerGameServiceStatus.NotFound));
        }

        var membership = await this.rooms.FindMembershipAsync(gameId, userId, cancellationToken);
        return membership is null
            ? new MemberRoomAccess(userId, room, null, new MultiplayerGameServiceResult(MultiplayerGameServiceStatus.Forbidden))
            : new MemberRoomAccess(userId, room, membership, null);
    }

    private static ActiveMultiplayerGame CreateGame(MultiplayerRoom room)
    {
        var players = new IPlayer?[4];
        var bots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var seat in room.Seats)
        {
            if (seat.OwnerUserId is not null)
            {
                if (seat.BotProfileId is not null)
                {
                    throw new InvalidOperationException($"Human seat {seat.Position} also has a bot profile.");
                }

                continue;
            }

            var botProfile = seat.BotProfileId
                ?? throw new InvalidOperationException($"Seat {seat.Position} is neither human-owned nor assigned a bot.");
            bots.Add(seat.Position.ToString(), botProfile);
            players[seat.Position.Index()] = BotProfiles.Create(botProfile);
        }

        var match = new BelotMatch(options: new BelotMatchOptions
        {
            FirstToPlay = PlayerPosition.South,
            RecordHistory = true,
        });
        match.Start();
        return new ActiveMultiplayerGame(room.RoomId, room, match, bots, players);
    }

    private static MultiplayerGameServiceResult MapRepositoryResult(MultiplayerRoomRepositoryResult result) =>
        result.Status switch
        {
            MultiplayerRoomRepositoryStatus.Success => new MultiplayerGameServiceResult(
                MultiplayerGameServiceStatus.Success,
                result.Room),
            MultiplayerRoomRepositoryStatus.NotFound => new MultiplayerGameServiceResult(MultiplayerGameServiceStatus.NotFound),
            MultiplayerRoomRepositoryStatus.NotMember or MultiplayerRoomRepositoryStatus.NotHost =>
                new MultiplayerGameServiceResult(MultiplayerGameServiceStatus.Forbidden),
            MultiplayerRoomRepositoryStatus.NotEnoughPlayers => new MultiplayerGameServiceResult(
                MultiplayerGameServiceStatus.Conflict,
                Error: "At least two human players must join before starting."),
            MultiplayerRoomRepositoryStatus.MembersNotReady => new MultiplayerGameServiceResult(
                MultiplayerGameServiceStatus.Conflict,
                Error: "Every joined human player must be ready before starting."),
            MultiplayerRoomRepositoryStatus.LobbyClosed => new MultiplayerGameServiceResult(
                MultiplayerGameServiceStatus.Conflict,
                Error: "The lobby is no longer waiting for a start."),
            _ => new MultiplayerGameServiceResult(
                MultiplayerGameServiceStatus.Conflict,
                Error: $"The lobby cannot start: {result.Status}."),
        };

    private static bool TryGetUserId(ClaimsPrincipal caller, out string userId)
    {
        userId = caller.FindFirst("sub")?.Value ?? string.Empty;
        return caller.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(userId);
    }

    private sealed record MemberRoomAccess(
        string? UserId,
        MultiplayerRoom? Room,
        RoomMembership? Membership,
        MultiplayerGameServiceResult? Result);
}
