namespace Belot.WebApi;

using System.Collections.Concurrent;
using System.Security.Cryptography;

using Belot.AI.ClaudePlayer;
using Belot.AI.DummyPlayer;
using Belot.AI.SmartPlayer;
using Belot.Engine.Game;
using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

internal static class BotProfiles
{
    public static IReadOnlyList<BotProfileDto> All { get; } = new[]
    {
        new BotProfileDto("random", "Random"),
        new BotProfileDto("beginner", "Beginner"),
        new BotProfileDto("skilled", "Skilled"),
        new BotProfileDto("expert", "Expert"),
        new BotProfileDto("master", "Master"),
    };

    public static bool IsValid(string? id) => All.Any(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IPlayer Create(string id) => id.ToLowerInvariant() switch
    {
        "random" => new RandomPlayer(),
        "beginner" => new DummyPlayer(),
        "skilled" => new SmartPlayer(),
        "expert" => ClaudePlayerProfiles.CreateExpert(),
        "master" => ClaudePlayerProfiles.CreateMaster(),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
}

internal sealed class GameStore
{
    private readonly ConcurrentDictionary<string, ActiveGame> games = new(StringComparer.Ordinal);

    private readonly object sync = new();

    private readonly int maximumGames;

    public GameStore(IConfiguration configuration)
    {
        this.maximumGames = Math.Clamp(configuration.GetValue("Games:MaximumActive", 500), 1, 10000);
    }

    public ActiveGame? Find(string gameId) => this.games.TryGetValue(gameId, out var game) ? game : null;

    public ActiveGame? Add(BelotMatch match, IReadOnlyDictionary<string, string> bots, IPlayer[] players)
    {
        lock (this.sync)
        {
            if (this.games.Count >= this.maximumGames)
            {
                return null;
            }

            Span<byte> bytes = stackalloc byte[32];
            while (true)
            {
                RandomNumberGenerator.Fill(bytes);
                var id = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var game = new ActiveGame(id, match, bots, players);
                if (this.games.TryAdd(id, game))
                {
                    return game;
                }
            }
        }
    }
}

internal sealed class ActiveGame
{
    public ActiveGame(string gameId, BelotMatch match, IReadOnlyDictionary<string, string> bots, IPlayer[] players)
    {
        this.GameId = gameId;
        this.Match = match;
        this.Bots = bots;
        this.Players = players;
    }

    public string GameId { get; }

    public BelotMatch Match { get; }

    public IReadOnlyDictionary<string, string> Bots { get; }

    public IPlayer[] Players { get; }

    public SemaphoreSlim Gate { get; } = new(1, 1);
}

internal sealed record CreateResult(GameStateDto? Game, string? Error, bool Unavailable);

internal enum GameActionStatus
{
    Ok,
    NotFound,
    Invalid,
    Conflict,
}

internal sealed record GameActionResult(GameActionStatus Status, GameActionResponse? Response = null, string? Error = null);

internal sealed class GameService
{
    private readonly GameStore store;

    public GameService(GameStore store) => this.store = store;

    public CreateResult Create(CreateGameRequest request)
    {
        if (request is null || !BotProfiles.IsValid(request.North) || !BotProfiles.IsValid(request.East) || !BotProfiles.IsValid(request.West))
        {
            return new CreateResult(null, "north, east and west must be valid bot profile ids.", false);
        }

        var bots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["North"] = request.North.ToLowerInvariant(),
            ["East"] = request.East.ToLowerInvariant(),
            ["West"] = request.West.ToLowerInvariant(),
        };
        var players = new IPlayer[4];
        players[PlayerPosition.East.Index()] = BotProfiles.Create(bots["East"]);
        players[PlayerPosition.North.Index()] = BotProfiles.Create(bots["North"]);
        players[PlayerPosition.West.Index()] = BotProfiles.Create(bots["West"]);

        var match = new BelotMatch(options: new BelotMatchOptions { FirstToPlay = PlayerPosition.South, RecordHistory = true });
        match.Start();
        var game = this.store.Add(match, bots, players);
        return game is null
            ? new CreateResult(null, null, true)
            : new CreateResult(ApiMapper.ToState(game.GameId, game.Bots, match.GetView(PlayerPosition.South)), null, false);
    }

    public async Task<GameStateDto?> GetAsync(string gameId, CancellationToken cancellationToken)
    {
        var game = this.store.Find(gameId);
        if (game is null)
        {
            return null;
        }

        await game.Gate.WaitAsync(cancellationToken);
        try
        {
            return ApiMapper.ToState(game.GameId, game.Bots, game.Match.GetView(PlayerPosition.South));
        }
        finally
        {
            game.Gate.Release();
        }
    }

    public async Task<DebugHandsDto?> GetDebugHandsAsync(string gameId, CancellationToken cancellationToken)
    {
        var game = this.store.Find(gameId);
        if (game is null)
        {
            return null;
        }

        await game.Gate.WaitAsync(cancellationToken);
        try
        {
            var seats = new[]
            {
                (Position: PlayerPosition.South, Role: "Human"),
                (Position: PlayerPosition.North, Role: "Partner bot"),
                (Position: PlayerPosition.East, Role: "Opponent bot"),
                (Position: PlayerPosition.West, Role: "Opponent bot"),
            };
            var hands = seats
                .Select(seat => new DebugHandDto(
                    seat.Position.ToString(),
                    seat.Role,
                    game.Match.GetView(seat.Position).Hand.Select(CardDto.From).ToArray()))
                .ToArray();

            return new DebugHandsDto(game.GameId, hands);
        }
        finally
        {
            game.Gate.Release();
        }
    }

    public async Task<GameActionResult> ActAsync(string gameId, GameActionRequest request, CancellationToken cancellationToken)
    {
        var game = this.store.Find(gameId);
        if (game is null)
        {
            return new GameActionResult(GameActionStatus.NotFound);
        }

        await game.Gate.WaitAsync(cancellationToken);
        try
        {
            var match = game.Match;
            if (match.IsFinished)
            {
                return new GameActionResult(GameActionStatus.Conflict, Error: "The match is finished.");
            }

            if (match.ToMove != PlayerPosition.South)
            {
                return new GameActionResult(GameActionStatus.Conflict, Error: "The human seat is not currently to move.");
            }

            var before = match.GetView(PlayerPosition.South);
            if (!TryBuildAction(before, request, out var action, out var error))
            {
                return new GameActionResult(GameActionStatus.Invalid, Error: error);
            }

            var validation = match.Validate(PlayerPosition.South, action);
            if (validation != BelotActResult.Ok)
            {
                return new GameActionResult(GameActionStatus.Invalid, Error: validation.ToString());
            }

            var events = new List<PublicGameEventDto>();
            match.Act(PlayerPosition.South, action);
            var after = match.GetView(PlayerPosition.South);
            events.AddRange(GameEventProjector.Project(before, after, PlayerPosition.South, action, events.Count));

            while (!match.IsFinished && match.ToMove != PlayerPosition.South)
            {
                var seat = match.ToMove;
                var botBefore = match.GetView(PlayerPosition.South);
                var botAction = game.Players[seat.Index()].Decide(match.GetView(seat));
                var botResult = match.Act(seat, botAction);
                if (botResult != BelotActResult.Ok)
                {
                    throw new InvalidOperationException($"Bot action was rejected: {botResult}.");
                }

                var botAfter = match.GetView(PlayerPosition.South);
                events.AddRange(GameEventProjector.Project(botBefore, botAfter, seat, botAction, events.Count));
            }

            return new GameActionResult(
                GameActionStatus.Ok,
                new GameActionResponse(ApiMapper.ToState(game.GameId, game.Bots, match.GetView(PlayerPosition.South)), events));
        }
        finally
        {
            game.Gate.Release();
        }
    }

    private static bool TryBuildAction(BelotSeatView view, GameActionRequest request, out BelotAction action, out string error)
    {
        action = null!;
        error = "Action does not match the current decision.";
        if (request is null)
        {
            error = "An action body is required.";
            return false;
        }

        switch (view.Decision)
        {
            case BelotDecision.Bid:
                if (request.Bid is null
                    || request.Announces is not null
                    || request.Card is not null
                    || request.Belote
                    || !Enum.TryParse<BidType>(request.Bid, true, out var bid)
                    || !Enum.IsDefined(bid))
                {
                    error = "bid must be one available bid name.";
                    return false;
                }

                action = BelotAction.Bid(bid);
                return true;
            case BelotDecision.Announce:
                var declarations = new List<Announce>();
                var remaining = view.AvailableAnnounces.ToList();
                foreach (var selection in request.Announces ?? Array.Empty<AnnounceRequest>())
                {
                    if (selection is null
                        || selection.Type is null
                        || !Enum.TryParse<AnnounceType>(selection.Type, true, out var announceType)
                        || !Enum.IsDefined(announceType)
                        || !CardDto.TryParse(selection.Card, out var card))
                    {
                        error = "Each declaration requires a valid type and card.";
                        return false;
                    }

                    var offered = remaining.FindIndex(announce => announce.Type == announceType && announce.Card == card);
                    if (offered < 0)
                    {
                        error = "A declaration was not offered for this hand.";
                        return false;
                    }

                    remaining.RemoveAt(offered);
                    declarations.Add(new Announce(announceType, card!));
                }

                action = BelotAction.Declare(declarations);
                return true;
            case BelotDecision.PlayCard:
                if (request.Bid is not null
                    || request.Announces is not null
                    || !CardDto.TryParse(request.Card, out var playCard))
                {
                    error = "card must contain a valid suit and type.";
                    return false;
                }

                if (request.Belote && !view.BeloteCards.Contains(playCard))
                {
                    error = "That card cannot announce a belote.";
                    return false;
                }

                action = BelotAction.PlayCard(playCard!, request.Belote);
                return true;
            default:
                error = "The match has no pending decision.";
                return false;
        }
    }
}