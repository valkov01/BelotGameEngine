namespace Belot.WebApi.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Belot.Engine;
using Belot.Engine.Game;
using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

public sealed class MultiplayerGameIntegrationTests : IDisposable
{
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ApiFactory factory;

    private readonly HttpClient client;

    public MultiplayerGameIntegrationTests()
    {
        this.factory = new ApiFactory(
            multiplayerConnectionString: Environment.GetEnvironmentVariable("BELOT_TEST_POSTGRES"));
        this.client = this.factory.CreateClient();
    }

    public void Dispose()
    {
        this.client.Dispose();
        this.factory.Dispose();
    }

    [Fact]
    public async Task StaticOpenApiDocumentsTheAuthenticatedMultiplayerGameRoutes()
    {
        using var response = await this.client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var snapshot = paths.GetProperty("/api/multiplayer/games/{gameId}").GetProperty("get");
        var action = paths.GetProperty("/api/multiplayer/games/{gameId}/actions").GetProperty("post");
        Assert.Equal("#/components/schemas/MultiplayerGameSnapshot", snapshot
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/MultiplayerGameActionResponse", action
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/MultiplayerGameActionRequest", action
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString());
    }

    [Fact]
    public void StoreKeepsActiveGamesAndExpiresOnlyCompletedGamesAfterRetention()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Multiplayer:Games:MaximumActive"] = "1",
                ["Multiplayer:Games:CompletedRetentionMinutes"] = "1",
            })
            .Build();
        var store = new MultiplayerGameStore(configuration);
        var active = this.CreateStoreGame("active");
        var next = this.CreateStoreGame("next");

        Assert.True(store.TryAdd(active));
        Assert.False(store.HasCapacity);
        Assert.False(store.TryAdd(next));

        var future = DateTimeOffset.UtcNow.AddHours(1);
        store.RemoveExpiredCompletedGames(future);
        Assert.Same(active, store.Find("active"));

        active.MarkCompleted();
        store.RemoveExpiredCompletedGames(future);
        Assert.Null(store.Find("active"));
        Assert.True(store.HasCapacity);
    }

    [Theory]
    [InlineData("North")]
    [InlineData("East")]
    public async Task TwoHumanPartnersAndOpponentsReceiveOnlyTheirOwnSeatViews(string secondSeatName)
    {
        var secondSeat = Enum.Parse<PlayerPosition>(secondSeatName);
        var room = await this.CreateStartedRoomAsync(secondSeat);

        foreach (var (seat, userId) in room.Members)
        {
            var snapshot = await this.GetSnapshotAsync(room.Room.RoomId, userId);
            Assert.Equal(seat, snapshot.YourSeat);
            Assert.Equal(PlayerPosition.South.ToString(), snapshot.State.ToMove);
            Assert.Equal(5, snapshot.State.Hand.Count);
            Assert.Empty(snapshot.State.PreviousRounds);
        }

        var snapshots = await Task.WhenAll(room.Members.Select(async member =>
            await this.GetSnapshotAsync(room.Room.RoomId, member.Value)));
        var allPrivateHands = snapshots.SelectMany(snapshot => snapshot.State.Hand).ToArray();
        Assert.Equal(5 * room.Members.Count, allPrivateHands.Length);
        Assert.Equal(allPrivateHands.Length, allPrivateHands.Distinct().Count());
        foreach (var snapshot in snapshots)
        {
            var json = JsonSerializer.Serialize(snapshot, ApiJsonOptions);
            Assert.Equal(1, this.CountProperty(json, "hand"));
            Assert.DoesNotContain("\"record\"", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ThreeAndFourHumanRoomsKeepAbsoluteSeatsAndAssignOnlyRemainingBots(int humanCount)
    {
        var room = await this.CreateStartedRoomAsync(
            new[] { PlayerPosition.East, PlayerPosition.North, PlayerPosition.West }
                .Take(humanCount - 1)
                .ToArray());
        Assert.Equal(humanCount, room.Room.HumanPlayerCount);

        var read = await this.GetLobbyAsync(room.Room.RoomId, room.Members[PlayerPosition.South]);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var lobby = (await read.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;
        Assert.True(lobby.IsGameAvailable);
        Assert.Equal(humanCount, lobby.Seats.Count(seat => seat.BotProfileId is null));
        Assert.Equal(4 - humanCount, lobby.Seats.Count(seat => seat.BotProfileId is not null));

        var privateHands = new List<CardDto>();
        foreach (var (seat, userId) in room.Members)
        {
            var snapshot = await this.GetSnapshotAsync(room.Room.RoomId, userId);
            Assert.Equal(seat, snapshot.YourSeat);
            Assert.Equal(5, snapshot.State.Hand.Count);
            privateHands.AddRange(snapshot.State.Hand);
        }

        Assert.Equal(privateHands.Count, privateHands.Distinct().Count());
    }

    [Fact]
    public async Task ActionsRequireMembershipTurnLegalityAndCurrentRevision()
    {
        var room = await this.CreateStartedRoomAsync(PlayerPosition.East);
        var gameId = room.Room.RoomId;
        var southId = room.Members[PlayerPosition.South];
        var eastId = room.Members[PlayerPosition.East];
        var snapshot = await this.GetSnapshotAsync(gameId, southId);
        var action = this.CreateAction(snapshot, "action-1");

        using (var unauthenticated = await this.client.PostAsJsonAsync($"/api/multiplayer/games/{gameId}/actions", action))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        }

        using (var nonMember = await this.SendActionAsync(gameId, Guid.NewGuid().ToString(), action))
        {
            Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);
        }

        using (var wrongPlayer = await this.SendActionAsync(gameId, eastId, action))
        {
            Assert.Equal(HttpStatusCode.Conflict, wrongPlayer.StatusCode);
        }

        var illegal = action with { Bid = "NotABid", IdempotencyKey = "illegal" };
        using (var illegalAction = await this.SendActionAsync(gameId, southId, illegal))
        {
            Assert.Equal(HttpStatusCode.BadRequest, illegalAction.StatusCode);
        }

        var unchanged = await this.GetSnapshotAsync(gameId, southId);
        Assert.Equal(0, unchanged.Revision);

        using var accepted = await this.SendActionAsync(gameId, southId, action);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedResult = (await accepted.Content.ReadFromJsonAsync<MultiplayerGameActionResponse>(ApiJsonOptions))!;
        Assert.Equal(1, acceptedResult.Revision);

        using var retry = await this.SendActionAsync(gameId, southId, action);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(
            JsonSerializer.Serialize(acceptedResult, ApiJsonOptions),
            JsonSerializer.Serialize(await retry.Content.ReadFromJsonAsync<MultiplayerGameActionResponse>(ApiJsonOptions), ApiJsonOptions));

        using (var reusedKey = await this.SendActionAsync(
            gameId,
            southId,
            action with { Bid = action.Bid == "Pass" ? "Clubs" : "Pass" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, reusedKey.StatusCode);
        }

        using var stale = await this.SendActionAsync(
            gameId,
            southId,
            action with { IdempotencyKey = "stale", ExpectedRevision = 0 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleError = (await stale.Content.ReadFromJsonAsync<MultiplayerGameErrorDto>(ApiJsonOptions))!;
        Assert.Equal(1, staleError.CurrentRevision);
    }

    [Fact]
    public async Task ConcurrentDuplicateActionsAreAppliedOnceAndReturnTheSameResult()
    {
        var room = await this.CreateStartedRoomAsync(PlayerPosition.North);
        var gameId = room.Room.RoomId;
        var southId = room.Members[PlayerPosition.South];
        var action = this.CreateAction(await this.GetSnapshotAsync(gameId, southId), "concurrent-action");

        var results = await Task.WhenAll(
            this.SendActionAsync(gameId, southId, action),
            this.SendActionAsync(gameId, southId, action));
        try
        {
            Assert.All(results, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            var bodies = await Task.WhenAll(results.Select(response => response.Content.ReadAsStringAsync()));
            Assert.Equal(bodies[0], bodies[1]);
            var result = JsonSerializer.Deserialize<MultiplayerGameActionResponse>(bodies[0], ApiJsonOptions)!;
            Assert.Equal(1, result.Revision);
            Assert.Equal(1, (await this.GetSnapshotAsync(gameId, southId)).Revision);
        }
        finally
        {
            foreach (var response in results)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task FailedBotDecisionCommitsRevisionAndRetryResumesWithoutReapplyingHumanAction()
    {
        var room = await this.CreateStartedRoomAsync(PlayerPosition.North);
        var gameId = room.Room.RoomId;
        var game = this.factory.Services.GetRequiredService<MultiplayerGameStore>().Find(gameId);
        Assert.NotNull(game);
        game!.Players[PlayerPosition.East.Index()] = new FailOncePlayer();

        var userId = room.Members[PlayerPosition.South];
        var action = this.CreateAction(await this.GetSnapshotAsync(gameId, userId), "retry-after-bot-failure");
        using (var failed = await this.SendActionAsync(gameId, userId, action))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            var error = (await failed.Content.ReadFromJsonAsync<MultiplayerGameErrorDto>(ApiJsonOptions))!;
            Assert.Equal(1, error.CurrentRevision);
        }

        var afterFailure = await this.GetSnapshotAsync(gameId, userId);
        Assert.Equal(1, afterFailure.Revision);
        Assert.Equal(PlayerPosition.East.ToString(), afterFailure.State.ToMove);
        Assert.Single(afterFailure.State.Bids.Where(bid => bid.Seat == PlayerPosition.South.ToString()));

        using var retry = await this.SendActionAsync(gameId, userId, action);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var result = (await retry.Content.ReadFromJsonAsync<MultiplayerGameActionResponse>(ApiJsonOptions))!;
        Assert.Equal(1, result.Revision);
        Assert.Single(result.Events.Where(gameEvent =>
            gameEvent.Seat == PlayerPosition.South.ToString()
            && gameEvent.Automatic != true
            && gameEvent.Type == "bid"));
        Assert.Contains(result.Events, gameEvent =>
            gameEvent.Seat == PlayerPosition.East.ToString()
            && gameEvent.Automatic == true
            && gameEvent.Type == "bid");

        var afterRetry = await this.GetSnapshotAsync(gameId, userId);
        Assert.Equal(1, afterRetry.Revision);
        Assert.Single(afterRetry.State.Bids.Where(bid => bid.Seat == PlayerPosition.South.ToString()));
    }

    [Theory]
    [InlineData("North", null, null)]
    [InlineData("East", null, null)]
    [InlineData("East", "North", null)]
    [InlineData("East", "North", "West")]
    public async Task HumanAndBotSeatCombinationsCanPlayCompleteMatches(
        string firstJoiningSeatName,
        string? secondJoiningSeatName,
        string? thirdJoiningSeatName)
    {
        var joiningSeats = new[] { firstJoiningSeatName, secondJoiningSeatName, thirdJoiningSeatName }
            .OfType<string>()
            .Select(name => Enum.Parse<PlayerPosition>(name))
            .ToArray();
        var room = await this.CreateStartedRoomAsync(joiningSeats);
        var gameId = room.Room.RoomId;
        var botSeats = room.Room.Seats
            .Where(seat => seat.BotProfileId is not null)
            .Select(seat => seat.Seat.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var events = new HashSet<string>(StringComparer.Ordinal);
        var sawAutomaticBotAction = false;
        MultiplayerGameSnapshotDto snapshot = await this.GetSnapshotAsync(gameId, room.Members[PlayerPosition.South]);

        for (var index = 0; index < 1600 && !snapshot.State.IsFinished; index++)
        {
            var currentSeat = Enum.Parse<PlayerPosition>(snapshot.State.ToMove);
            Assert.True(room.Members.TryGetValue(currentSeat, out var currentUserId), $"Unexpected bot turn: {currentSeat}.");
            snapshot = await this.GetSnapshotAsync(gameId, currentUserId!);
            var action = this.CreateAction(snapshot, Guid.NewGuid().ToString("N"));
            using var response = await this.SendActionAsync(gameId, currentUserId!, action);
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"Action {index} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            var accepted = (await response.Content.ReadFromJsonAsync<MultiplayerGameActionResponse>(ApiJsonOptions))!;
            Assert.Equal(snapshot.Revision + 1, accepted.Revision);
            Assert.Equal(currentSeat, accepted.YourSeat);
            var expectedEventType = snapshot.State.Decision switch
            {
                "Bid" => "bid",
                "Announce" => "declarations",
                "PlayCard" => "card",
                _ => throw new InvalidOperationException($"Unexpected decision: {snapshot.State.Decision}."),
            };
            Assert.Contains(
                accepted.Events,
                gameEvent => gameEvent.Seat == currentSeat.ToString()
                    && gameEvent.Type == expectedEventType
                    && gameEvent.Automatic != true);
            events.UnionWith(accepted.Events.Select(gameEvent => gameEvent.Type));
            sawAutomaticBotAction |= accepted.Events.Any(gameEvent =>
                gameEvent.Automatic == true && botSeats.Contains(gameEvent.Seat ?? string.Empty));
            snapshot = new MultiplayerGameSnapshotDto(gameId, accepted.Revision, accepted.YourSeat, accepted.State);

            if (!snapshot.State.IsFinished)
            {
                var nextSeat = Enum.Parse<PlayerPosition>(snapshot.State.ToMove);
                Assert.True(room.Members.ContainsKey(nextSeat), $"Bot automation stopped at {nextSeat}.");
            }
        }

        Assert.True(snapshot.State.IsFinished, "The multiplayer match did not finish within the action limit.");
        Assert.NotNull(snapshot.State.Winner);
        Assert.Contains("bid", events);
        Assert.Contains("card", events);
        Assert.Contains("trick", events);
        Assert.Contains("deal-ended", events);
        Assert.Equal(botSeats.Count > 0, sawAutomaticBotAction);
        Assert.Equal(snapshot.State.SouthNorthPoints, snapshot.State.PreviousRounds.Sum(round => round.SouthNorthPoints));
        Assert.Equal(snapshot.State.EastWestPoints, snapshot.State.PreviousRounds.Sum(round => round.EastWestPoints));
        Assert.All(
            snapshot.State.PreviousRounds.Where(round => round.Contract.Type != "Pass"),
            round =>
            {
                Assert.Equal(8, round.Tricks.Count);
                Assert.Equal(32, round.Tricks.Sum(trick => trick.Cards.Count));
            });

        var nextSeatByPosition = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["South"] = "East",
            ["East"] = "North",
            ["North"] = "West",
            ["West"] = "South",
        };
        Assert.All(
            snapshot.State.PreviousRounds.SelectMany(round => round.Tricks),
            trick =>
            {
                for (var cardIndex = 1; cardIndex < trick.Cards.Count; cardIndex++)
                {
                    Assert.Equal(nextSeatByPosition[trick.Cards[cardIndex - 1].Seat], trick.Cards[cardIndex].Seat);
                }
            });
    }

    [Fact]
    public async Task StartedLobbyWithoutItsProcessLocalMatchReportsUnavailableAfterRestart()
    {
        var room = await this.CreateStartedRoomAsync(PlayerPosition.East);
        var store = this.factory.Services.GetRequiredService<MultiplayerGameStore>();
        var activeGame = store.Find(room.Room.RoomId);
        Assert.NotNull(activeGame);
        Assert.True(store.Remove(room.Room.RoomId, activeGame!));

        using var lobbyResponse = await this.GetLobbyAsync(room.Room.RoomId, room.Members[PlayerPosition.South]);
        Assert.Equal(HttpStatusCode.OK, lobbyResponse.StatusCode);
        var lobby = (await lobbyResponse.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;
        Assert.Equal(MultiplayerRoomStatus.Started, lobby.Status);
        Assert.False(lobby.IsGameAvailable);

        using var gameRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/multiplayer/games/{room.Room.RoomId}",
            ApiFactory.CreateAccessToken(room.Members[PlayerPosition.South]));
        using var unavailable = await this.client.SendAsync(gameRequest);
        Assert.Equal(HttpStatusCode.Gone, unavailable.StatusCode);

        using (var unavailableAction = await this.SendActionAsync(
            room.Room.RoomId,
            room.Members[PlayerPosition.South],
            new MultiplayerGameActionRequest
            {
                ExpectedRevision = 0,
                IdempotencyKey = "after-restart",
                Bid = "Pass",
            }))
        {
            Assert.Equal(HttpStatusCode.Gone, unavailableAction.StatusCode);
        }

        using var startRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.Room.RoomId}/start",
            ApiFactory.CreateAccessToken(room.Members[PlayerPosition.South]));
        using var restartAttempt = await this.client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.Gone, restartAttempt.StatusCode);
    }

    private MultiplayerGameActionRequest CreateAction(
        MultiplayerGameSnapshotDto snapshot,
        string idempotencyKey) =>
        snapshot.State.Decision switch
        {
            "Bid" => new MultiplayerGameActionRequest
            {
                ExpectedRevision = snapshot.Revision,
                IdempotencyKey = idempotencyKey,
                Bid = snapshot.State.AvailableBids[0],
            },
            "Announce" => new MultiplayerGameActionRequest
            {
                ExpectedRevision = snapshot.Revision,
                IdempotencyKey = idempotencyKey,
                Announces = snapshot.State.AvailableAnnounces
                    .Select(announce => new AnnounceRequest { Type = announce.Type, Card = announce.Card })
                    .ToArray(),
            },
            "PlayCard" => new MultiplayerGameActionRequest
            {
                ExpectedRevision = snapshot.Revision,
                IdempotencyKey = idempotencyKey,
                Card = snapshot.State.PlayableCards[0],
                Belote = snapshot.State.BeloteCards.Contains(snapshot.State.PlayableCards[0]),
            },
            _ => throw new InvalidOperationException($"Unexpected decision: {snapshot.State.Decision}."),
        };

    private int CountProperty(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return this.CountProperty(document.RootElement, propertyName);
    }

    private int CountProperty(JsonElement element, string propertyName)
    {
        var count = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }

                count += this.CountProperty(property.Value, propertyName);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                count += this.CountProperty(item, propertyName);
            }
        }

        return count;
    }

    private async Task<StartedRoom> CreateStartedRoomAsync(params PlayerPosition[] joiningSeats)
    {
        var hostId = Guid.NewGuid().ToString();
        var members = new Dictionary<PlayerPosition, string> { [PlayerPosition.South] = hostId };
        var creation = await this.CreateLobbyAsync(hostId, new CreateMultiplayerRoomRequest
        {
            MaximumHumanPlayers = joiningSeats.Length + 1,
            EastBot = "random",
            NorthBot = "random",
            WestBot = "random",
        });

        foreach (var seat in joiningSeats)
        {
            var userId = Guid.NewGuid().ToString();
            members.Add(seat, userId);
            using var joinRequest = ApiFactory.CreateAuthenticatedRequest(
                HttpMethod.Post,
                $"/api/multiplayer/lobbies/{creation.Room.RoomId}/join",
                ApiFactory.CreateAccessToken(userId));
            joinRequest.Content = JsonContent.Create(new { invitationCode = creation.InvitationCode, seat });
            using var joined = await this.client.SendAsync(joinRequest);
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }

        foreach (var userId in members.Values)
        {
            using var readyRequest = ApiFactory.CreateAuthenticatedRequest(
                HttpMethod.Post,
                $"/api/multiplayer/lobbies/{creation.Room.RoomId}/ready",
                ApiFactory.CreateAccessToken(userId));
            readyRequest.Content = JsonContent.Create(new { isReady = true });
            using var ready = await this.client.SendAsync(readyRequest);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        using var startRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{creation.Room.RoomId}/start",
            ApiFactory.CreateAccessToken(hostId));
        using var started = await this.client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var startedRoom = (await started.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;
        Assert.Equal(MultiplayerRoomStatus.Started, startedRoom.Status);
        Assert.True(startedRoom.IsGameAvailable);
        return new StartedRoom(startedRoom, members);
    }

    private async Task<CreateMultiplayerRoomResponse> CreateLobbyAsync(
        string userId,
        CreateMultiplayerRoomRequest request)
    {
        using var createRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/multiplayer/lobbies",
            ApiFactory.CreateAccessToken(userId));
        createRequest.Content = JsonContent.Create(request);
        using var response = await this.client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateMultiplayerRoomResponse>(ApiJsonOptions))!;
    }

    private async Task<MultiplayerGameSnapshotDto> GetSnapshotAsync(string gameId, string userId)
    {
        using var request = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/multiplayer/games/{gameId}",
            ApiFactory.CreateAccessToken(userId));
        using var response = await this.client.SendAsync(request);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Snapshot for {userId} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<MultiplayerGameSnapshotDto>(ApiJsonOptions))!;
    }

    private Task<HttpResponseMessage> GetLobbyAsync(string roomId, string userId) =>
        this.client.SendAsync(ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/multiplayer/lobbies/{roomId}",
            ApiFactory.CreateAccessToken(userId)));

    private Task<HttpResponseMessage> SendActionAsync(
        string gameId,
        string userId,
        MultiplayerGameActionRequest action)
    {
        var request = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/games/{gameId}/actions",
            ApiFactory.CreateAccessToken(userId));
        request.Content = JsonContent.Create(action, options: ApiJsonOptions);
        return this.client.SendAsync(request);
    }

    private ActiveMultiplayerGame CreateStoreGame(string gameId)
    {
        var room = new MultiplayerRoom(
            gameId,
            "host",
            DateTimeOffset.UtcNow,
            MultiplayerRoomStatus.Started,
            2,
            new Dictionary<string, string>(),
            Array.Empty<MultiplayerSeat>(),
            Array.Empty<RoomMembership>());
        var match = new BelotMatch();
        match.Start();
        return new ActiveMultiplayerGame(
            gameId,
            room,
            match,
            new Dictionary<string, string>(),
            new IPlayer?[4]);
    }

    private sealed class FailOncePlayer : IPlayer
    {
        private readonly IPlayer fallback = BotProfiles.Create("random");

        private bool hasFailed;

        public BidType GetBid(PlayerGetBidContext context)
        {
            this.ThrowOnFirstDecision();
            return this.fallback.GetBid(context);
        }

        public IList<Announce> GetAnnounces(PlayerGetAnnouncesContext context)
        {
            this.ThrowOnFirstDecision();
            return this.fallback.GetAnnounces(context);
        }

        public PlayCardAction PlayCard(PlayerPlayCardContext context)
        {
            this.ThrowOnFirstDecision();
            return this.fallback.PlayCard(context);
        }

        public void EndOfTrick(IEnumerable<PlayCardAction> trickActions) =>
            this.fallback.EndOfTrick(trickActions);

        public void EndOfRound(RoundResult roundResult) => this.fallback.EndOfRound(roundResult);

        public void EndOfGame(GameResult gameResult) => this.fallback.EndOfGame(gameResult);

        private void ThrowOnFirstDecision()
        {
            if (!this.hasFailed)
            {
                this.hasFailed = true;
                throw new InvalidOperationException("Simulated one-time bot failure.");
            }
        }
    }

    private sealed record StartedRoom(
        MultiplayerRoomDto Room,
        IReadOnlyDictionary<PlayerPosition, string> Members);
}
