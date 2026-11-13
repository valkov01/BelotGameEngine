namespace Belot.WebApi.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Belot.Engine;
using Belot.Engine.Game;
using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
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
            multiplayerConnectionString: Environment.GetEnvironmentVariable("BELOT_TEST_POSTGRES"),
            realtimeEventsPerGame: 32,
            realtimeEventsPerLobby: 16);
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
        var resume = paths.GetProperty("/api/multiplayer/games/{gameId}/automation/resume").GetProperty("post");
        Assert.Equal("#/components/schemas/MultiplayerGameSnapshot", snapshot
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/MultiplayerGameActionResponse", action
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/MultiplayerGameActionRequest", action
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/MultiplayerAutomationResponse", resume
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
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
    public async Task HubSubscriptionsRequireRoomMembershipAndValidatedIdentity()
    {
        var room = await this.CreateStartedRoomAsync(PlayerPosition.North);
        await using var outsider = this.CreateHubConnection(Guid.NewGuid().ToString());
        await outsider.StartAsync();

        await Assert.ThrowsAsync<HubException>(() =>
            outsider.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
                "JoinRoom",
                room.Room.RoomId,
                0L,
                -1));
        await Assert.ThrowsAsync<HubException>(() =>
            outsider.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
                "JoinGame",
                room.Room.RoomId,
                0L,
                -1));

        await using var unauthenticated = this.CreateHubConnection(null);
        await Assert.ThrowsAnyAsync<Exception>(() => unauthenticated.StartAsync());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task EveryHumanGameSubscriberReceivesOnlyOrderedPublicEvents(int humanCount)
    {
        var joiningSeats = new[] { PlayerPosition.North, PlayerPosition.East, PlayerPosition.West }
            .Take(humanCount - 1)
            .ToArray();
        var room = await this.CreateStartedRoomAsync(joiningSeats);
        var channels = new List<Channel<MultiplayerGameEventEnvelope>>();
        var connections = new List<HubConnection>();
        try
        {
            foreach (var userId in room.Members.Values)
            {
                var connection = this.CreateHubConnection(userId);
                var channel = Channel.CreateUnbounded<MultiplayerGameEventEnvelope>();
                connection.On<MultiplayerGameEventEnvelope>(
                    "GameEvent",
                    gameEvent => channel.Writer.TryWrite(gameEvent));
                await connection.StartAsync();
                var snapshot = await this.GetSnapshotAsync(room.Room.RoomId, userId);
                var subscribed = await connection.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
                    "JoinGame",
                    room.Room.RoomId,
                    snapshot.Revision,
                    snapshot.LastEventSequence);
                Assert.False(subscribed.SnapshotRequired);
                connections.Add(connection);
                channels.Add(channel);
            }

            var southId = room.Members[PlayerPosition.South];
            var southSnapshot = await this.GetSnapshotAsync(room.Room.RoomId, southId);
            var action = this.CreateAction(southSnapshot, "realtime-first-action");
            using var acceptedResponse = await this.SendActionAsync(room.Room.RoomId, southId, action);
            Assert.Equal(HttpStatusCode.OK, acceptedResponse.StatusCode);
            var accepted = (await acceptedResponse.Content.ReadFromJsonAsync<MultiplayerGameActionResponse>(ApiJsonOptions))!;
            Assert.NotEmpty(accepted.Events);

            var deliveredByClient = new List<MultiplayerGameEventEnvelope[]>();
            foreach (var channel in channels)
            {
                var delivered = new MultiplayerGameEventEnvelope[accepted.Events.Count];
                for (var eventIndex = 0; eventIndex < delivered.Length; eventIndex++)
                {
                    delivered[eventIndex] = await channel.Reader.ReadAsync()
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(10));
                }

                Assert.Equal(
                    Enumerable.Range(0, accepted.Events.Count),
                    delivered.Select(gameEvent => gameEvent.Sequence));
                Assert.All(delivered, gameEvent => Assert.Equal(accepted.Revision, gameEvent.Revision));
                Assert.Equal(accepted.Events.Select(gameEvent => gameEvent.Type), delivered.Select(gameEvent => gameEvent.Event.Type));
                Assert.Contains(delivered, gameEvent =>
                    gameEvent.Event.Seat == PlayerPosition.South.ToString()
                    && gameEvent.Event.Automatic != true);
                Assert.All(delivered, gameEvent =>
                {
                    if (gameEvent.Event.Seat is { } seat)
                    {
                        Assert.True(Enum.TryParse<PlayerPosition>(seat, out var parsedSeat));
                        Assert.True(Enum.IsDefined(parsedSeat));
                    }
                });
                Assert.All(delivered, gameEvent =>
                {
                    var json = JsonSerializer.Serialize(gameEvent);
                    Assert.DoesNotContain("\"hand\"", json, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("\"playableCards\"", json, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("\"availableBids\"", json, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("\"availableAnnounces\"", json, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("\"beloteCards\"", json, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("\"record\"", json, StringComparison.OrdinalIgnoreCase);
                });

                deliveredByClient.Add(delivered);
            }

            Assert.All(deliveredByClient.Skip(1), delivered =>
                Assert.Equal(deliveredByClient[0].Select(gameEvent => gameEvent.Sequence), delivered.Select(gameEvent => gameEvent.Sequence)));

            var nextSeat = Enum.Parse<PlayerPosition>(accepted.State.ToMove);
            Assert.True(room.Members.TryGetValue(nextSeat, out var nextUserId));
            var nextSnapshot = await this.GetSnapshotAsync(room.Room.RoomId, nextUserId!);
            var nextAction = this.CreateAction(nextSnapshot, "realtime-second-action");
            using (var nextResponse = await this.SendActionAsync(room.Room.RoomId, nextUserId!, nextAction))
            {
                Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
                var nextAccepted = (await nextResponse.Content.ReadFromJsonAsync<MultiplayerGameActionResponse>(ApiJsonOptions))!;
                Assert.Equal(accepted.Revision + 1, nextAccepted.Revision);
                Assert.NotEmpty(nextAccepted.Events);
                foreach (var channel in channels)
                {
                    var revisionEvents = new MultiplayerGameEventEnvelope[nextAccepted.Events.Count];
                    for (var eventIndex = 0; eventIndex < revisionEvents.Length; eventIndex++)
                    {
                        revisionEvents[eventIndex] = await channel.Reader.ReadAsync()
                            .AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(10));
                    }

                    Assert.All(revisionEvents, gameEvent => Assert.Equal(nextAccepted.Revision, gameEvent.Revision));
                    Assert.Equal(
                        Enumerable.Range(0, nextAccepted.Events.Count),
                        revisionEvents.Select(gameEvent => gameEvent.Sequence));
                }
            }

            using var duplicate = await this.SendActionAsync(room.Room.RoomId, southId, action);
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            var duplicateResult = (await duplicate.Content.ReadFromJsonAsync<MultiplayerGameActionResponse>(ApiJsonOptions))!;
            Assert.Equal(accepted.Revision, duplicateResult.Revision);
            await Task.Delay(100);
            Assert.All(channels, channel => Assert.False(channel.Reader.TryRead(out _)));
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task LobbyJoinReadyAndStartChangesAreBroadcastAsPublicEvents()
    {
        var hostId = Guid.NewGuid().ToString();
        var created = await this.CreateLobbyAsync(hostId, new CreateMultiplayerRoomRequest
        {
            MaximumHumanPlayers = 2,
            EastBot = "random",
            NorthBot = "random",
            WestBot = "random",
        });
        var hostChannel = Channel.CreateUnbounded<MultiplayerLobbyEventEnvelope>();
        await using var hostConnection = this.CreateHubConnection(hostId);
        hostConnection.On<MultiplayerLobbyEventEnvelope>(
            "LobbyEvent",
            lobbyEvent => hostChannel.Writer.TryWrite(lobbyEvent));
        await hostConnection.StartAsync();
        var hostSubscription = await hostConnection.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
            "JoinRoom",
            created.Room.RoomId,
            created.Room.EventRevision,
            created.Room.EventSequence);
        Assert.False(hostSubscription.SnapshotRequired);

        var joiningUserId = Guid.NewGuid().ToString();
        using var joinRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{created.Room.RoomId}/join",
            ApiFactory.CreateAccessToken(joiningUserId));
        joinRequest.Content = JsonContent.Create(new { invitationCode = created.InvitationCode, seat = PlayerPosition.East });
        using var joined = await this.client.SendAsync(joinRequest);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var joinedRoom = (await joined.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;
        var joinedEvent = await hostChannel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("player-joined", joinedEvent.Type);
        Assert.Equal(joinedRoom.YourSeat.ToString(), joinedEvent.Seat);
        Assert.Equal(2, joinedEvent.HumanPlayerCount);

        var joiningChannel = Channel.CreateUnbounded<MultiplayerLobbyEventEnvelope>();
        await using var joiningConnection = this.CreateHubConnection(joiningUserId);
        joiningConnection.On<MultiplayerLobbyEventEnvelope>(
            "LobbyEvent",
            lobbyEvent => joiningChannel.Writer.TryWrite(lobbyEvent));
        await joiningConnection.StartAsync();
        var joiningSubscription = await joiningConnection.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
            "JoinRoom",
            created.Room.RoomId,
            joinedRoom.EventRevision,
            joinedRoom.EventSequence);
        Assert.False(joiningSubscription.SnapshotRequired);

        await this.SetReadyAsync(created.Room.RoomId, hostId, true);
        var hostReadyEvent = await hostChannel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("player-ready", hostReadyEvent.Type);
        Assert.Equal(PlayerPosition.South.ToString(), hostReadyEvent.Seat);
        var joiningObservedHostReady = await joiningChannel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PlayerPosition.South.ToString(), joiningObservedHostReady.Seat);
        Assert.Equal(hostReadyEvent.Revision, joiningObservedHostReady.Revision);

        await this.SetReadyAsync(created.Room.RoomId, joiningUserId, true);
        var joiningReadyEvent = await joiningChannel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("player-ready", joiningReadyEvent.Type);
        Assert.Equal(joinedRoom.YourSeat.ToString(), joiningReadyEvent.Seat);
        var hostObservedJoiningReady = await hostChannel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PlayerPosition.East.ToString(), hostObservedJoiningReady.Seat);
        Assert.Equal(joiningReadyEvent.Revision, hostObservedJoiningReady.Revision);

        using var startRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{created.Room.RoomId}/start",
            ApiFactory.CreateAccessToken(hostId));
        using var started = await this.client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var startedRoom = (await started.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;
        var startedForHost = await hostChannel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var startedForJoiner = await joiningChannel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("game-started", startedForHost.Type);
        Assert.Equal(startedForHost.Revision, startedForJoiner.Revision);
        Assert.Equal(MultiplayerRoomStatus.Started, startedRoom.Status);
        Assert.True(joinedEvent.Revision < hostReadyEvent.Revision);
        Assert.True(hostReadyEvent.Revision < joiningReadyEvent.Revision);
        Assert.True(joiningReadyEvent.Revision < startedForHost.Revision);
        Assert.All(
            new[] { joinedEvent, hostReadyEvent, joiningReadyEvent, startedForHost },
            lobbyEvent => Assert.Equal(0, lobbyEvent.Sequence));

        var publicJson = JsonSerializer.Serialize(startedForJoiner);
        Assert.DoesNotContain(hostId, publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain(joiningUserId, publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"hand\"", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"invitationCode\"", publicJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LobbyReadAndMutationCannotRacePastEachOthersCursors()
    {
        var hostId = Guid.NewGuid().ToString();
        var created = await this.CreateLobbyAsync(hostId, new CreateMultiplayerRoomRequest
        {
            MaximumHumanPlayers = 2,
            EastBot = "random",
            NorthBot = "random",
            WestBot = "random",
        });
        var joiningUserId = Guid.NewGuid().ToString();
        await this.JoinLobbyAsync(
            this.client,
            created.Room.RoomId,
            created.InvitationCode,
            joiningUserId,
            PlayerPosition.East);

        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockNextRead = 1;
        this.factory.LobbyLockEntered = async roomId =>
        {
            if (roomId == created.Room.RoomId && Interlocked.Exchange(ref blockNextRead, 0) == 1)
            {
                readEntered.SetResult();
                await releaseRead.Task;
            }
        };
        this.factory.LobbyLockWaiting = roomId =>
        {
            if (roomId == created.Room.RoomId && readEntered.Task.IsCompleted)
            {
                mutationWaiting.TrySetResult();
            }
        };

        using var readRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/multiplayer/lobbies/{created.Room.RoomId}",
            ApiFactory.CreateAccessToken(hostId));
        var readTask = this.client.SendAsync(readRequest);
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var readyRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{created.Room.RoomId}/ready",
            ApiFactory.CreateAccessToken(hostId));
        readyRequest.Content = JsonContent.Create(new { isReady = true });
        var readyTask = this.client.SendAsync(readyRequest);
        try
        {
            await mutationWaiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(readyTask.IsCompleted);
        }
        finally
        {
            releaseRead.TrySetResult();
        }

        using var readResponse = await readTask;
        using var readyResponse = await readyTask;
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
        var staleButConsistent = (await readResponse.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;
        var acceptedState = (await readyResponse.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;
        Assert.False(staleButConsistent.Seats.Single(seat => seat.Seat == PlayerPosition.South).IsReady);
        Assert.True(acceptedState.Seats.Single(seat => seat.Seat == PlayerPosition.South).IsReady);
        Assert.True(acceptedState.EventRevision > staleButConsistent.EventRevision);
        Assert.Equal(0, staleButConsistent.EventSequence);
        Assert.Equal(0, acceptedState.EventSequence);

        this.factory.LobbyLockEntered = null;
        this.factory.LobbyLockWaiting = null;
    }

    [Fact]
    public async Task ConcurrentReadinessChangesPublishOrderedMatchingSnapshots()
    {
        var hostId = Guid.NewGuid().ToString();
        var created = await this.CreateLobbyAsync(hostId, new CreateMultiplayerRoomRequest
        {
            MaximumHumanPlayers = 2,
            EastBot = "random",
            NorthBot = "random",
            WestBot = "random",
        });
        var joiningUserId = Guid.NewGuid().ToString();
        await this.JoinLobbyAsync(
            this.client,
            created.Room.RoomId,
            created.InvitationCode,
            joiningUserId,
            PlayerPosition.East);
        using var joinedLobbyResponse = await this.GetLobbyAsync(created.Room.RoomId, hostId);
        var joinedLobby = (await joinedLobbyResponse.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;

        var events = Channel.CreateUnbounded<MultiplayerLobbyEventEnvelope>();
        await using var connection = this.CreateHubConnection(hostId);
        connection.On<MultiplayerLobbyEventEnvelope>(
            "LobbyEvent",
            lobbyEvent => events.Writer.TryWrite(lobbyEvent));
        await connection.StartAsync();
        await connection.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
            "JoinRoom",
            created.Room.RoomId,
            joinedLobby.EventRevision,
            joinedLobby.EventSequence);

        var bothRequestsWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingRequests = 0;
        Task<HttpResponseMessage>[] responses = Array.Empty<Task<HttpResponseMessage>>();
        await this.factory.Services.GetRequiredService<IMultiplayerRealtimePublisher>()
            .WithLobbyLockAsync(
                created.Room.RoomId,
                async () =>
                {
                    this.factory.LobbyLockWaiting = roomId =>
                    {
                        if (roomId == created.Room.RoomId
                            && Interlocked.Increment(ref waitingRequests) == 2)
                        {
                            bothRequestsWaiting.TrySetResult();
                        }
                    };
                    responses = new[]
                    {
                        this.SendReadyResponseAsync(created.Room.RoomId, hostId, true),
                        this.SendReadyResponseAsync(created.Room.RoomId, joiningUserId, true),
                    };
                    await bothRequestsWaiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    return true;
                },
                CancellationToken.None);
        this.factory.LobbyLockWaiting = null;
        var completedResponses = await Task.WhenAll(responses);
        try
        {
            Assert.All(completedResponses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            var snapshots = await Task.WhenAll(completedResponses.Select(response =>
                response.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions)));
            var receivedEvents = new[]
            {
                await events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)),
                await events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)),
            };
            Assert.True(receivedEvents[0].Revision < receivedEvents[1].Revision);
            Assert.Equal(
                receivedEvents.Select(lobbyEvent => lobbyEvent.Revision).Order(),
                snapshots.Select(snapshot => snapshot!.EventRevision).Order());
            foreach (var lobbyEvent in receivedEvents)
            {
                var matchingSnapshot = snapshots.Single(snapshot => snapshot!.EventRevision == lobbyEvent.Revision)!;
                Assert.Equal(
                    lobbyEvent.Seats.Select(seat => (seat.Seat, seat.IsReady)).OrderBy(seat => seat.Seat),
                    matchingSnapshot.Seats.Select(seat => (seat.Seat.ToString(), seat.IsReady)).OrderBy(seat => seat.Item1));
            }

            var latestLobby = await this.GetLobbyAsync(created.Room.RoomId, hostId);
            var latest = (await latestLobby.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;
            Assert.Equal(receivedEvents[1].Revision, latest.EventRevision);
            Assert.All(latest.Seats.Where(seat => seat.IsOccupied), seat => Assert.True(seat.IsReady));
        }
        finally
        {
            foreach (var response in completedResponses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task ReconnectAfterLobbyHistoryEvictionRequiresOneSnapshotThenResumes()
    {
        var hostId = Guid.NewGuid().ToString();
        var created = await this.CreateLobbyAsync(hostId, new CreateMultiplayerRoomRequest
        {
            MaximumHumanPlayers = 2,
            EastBot = "random",
            NorthBot = "random",
            WestBot = "random",
        });
        var staleCursor = new MultiplayerEventCursor(created.Room.EventRevision, created.Room.EventSequence);
        for (var eventIndex = 0; eventIndex < 17; eventIndex++)
        {
            using var response = await this.SendReadyResponseAsync(
                created.Room.RoomId,
                hostId,
                isReady: eventIndex % 2 == 0);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var notices = Channel.CreateUnbounded<JsonElement>();
        await using var connection = this.CreateHubConnection(hostId);
        connection.On<JsonElement>("SnapshotRequired", notice => notices.Writer.TryWrite(notice));
        await connection.StartAsync();
        var staleReplay = await connection.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
            "JoinRoom",
            created.Room.RoomId,
            staleCursor.Revision,
            staleCursor.Sequence);
        Assert.True(staleReplay.SnapshotRequired);
        var notice = await notices.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("room", notice.GetProperty("resourceType").GetString());

        using var snapshotResponse = await this.GetLobbyAsync(created.Room.RoomId, hostId);
        Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);
        var snapshot = (await snapshotResponse.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;
        var recoveredReplay = await connection.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
            "JoinRoom",
            created.Room.RoomId,
            snapshot.EventRevision,
            snapshot.EventSequence);
        Assert.False(recoveredReplay.SnapshotRequired);
        Assert.Equal(snapshot.EventRevision, recoveredReplay.LatestRevision);
    }

    [Fact]
    public async Task OtherLobbiesRevisionGapsDoNotCauseAFalseReplayGap()
    {
        var firstHost = Guid.NewGuid().ToString();
        var firstLobby = await this.CreateLobbyAsync(firstHost, new CreateMultiplayerRoomRequest());
        var secondHost = Guid.NewGuid().ToString();
        var secondLobby = await this.CreateLobbyAsync(secondHost, new CreateMultiplayerRoomRequest());
        var firstCursor = new MultiplayerEventCursor(firstLobby.Room.EventRevision, firstLobby.Room.EventSequence);
        await this.SetReadyAsync(secondLobby.Room.RoomId, secondHost, true);
        await this.SetReadyAsync(firstLobby.Room.RoomId, firstHost, true);

        var received = Channel.CreateUnbounded<MultiplayerLobbyEventEnvelope>();
        await using var connection = this.CreateHubConnection(firstHost);
        connection.On<MultiplayerLobbyEventEnvelope>(
            "LobbyEvent",
            lobbyEvent => received.Writer.TryWrite(lobbyEvent));
        await connection.StartAsync();
        var replay = await connection.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
            "JoinRoom",
            firstLobby.Room.RoomId,
            firstCursor.Revision,
            firstCursor.Sequence);

        Assert.False(replay.SnapshotRequired);
        var replayedEvent = await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(firstLobby.Room.RoomId, replayedEvent.RoomId);
        Assert.Equal("player-ready", replayedEvent.Type);
    }

    [Fact]
    public async Task ReconnectUsesFreshSnapshotAndSignalsWhenRequestedHistoryIsUnavailable()
    {
        var room = await this.CreateStartedRoomAsync(PlayerPosition.North);
        var userId = room.Members[PlayerPosition.South];
        var gameId = room.Room.RoomId;
        var snapshot = await this.GetSnapshotAsync(gameId, userId);

        await using (var firstConnection = this.CreateHubConnection(userId))
        {
            await firstConnection.StartAsync();
            var firstSubscription = await firstConnection.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
                "JoinGame",
                gameId,
                snapshot.Revision,
                snapshot.LastEventSequence);
            Assert.False(firstSubscription.SnapshotRequired);
        }

        var action = this.CreateAction(snapshot, "action-during-disconnect");
        using var accepted = await this.SendActionAsync(gameId, userId, action);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var actionResult = (await accepted.Content.ReadFromJsonAsync<MultiplayerGameActionResponse>(ApiJsonOptions))!;

        var replayChannel = Channel.CreateUnbounded<MultiplayerGameEventEnvelope>();
        var latestSnapshot = await this.GetSnapshotAsync(gameId, userId);
        var reconnectChannel = Channel.CreateUnbounded<JsonElement>();
        await using var reconnected = this.CreateHubConnection(userId);
        reconnected.On<MultiplayerGameEventEnvelope>(
            "GameEvent",
            gameEvent => replayChannel.Writer.TryWrite(gameEvent));
        reconnected.On<JsonElement>("SnapshotRequired", notice => reconnectChannel.Writer.TryWrite(notice));
        await reconnected.StartAsync();
        var replayedSubscription = await reconnected.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
            "JoinGame",
            gameId,
            snapshot.Revision,
            snapshot.LastEventSequence);
        Assert.False(replayedSubscription.SnapshotRequired);
        var replayedEvents = new MultiplayerGameEventEnvelope[actionResult.Events.Count];
        for (var eventIndex = 0; eventIndex < replayedEvents.Length; eventIndex++)
        {
            replayedEvents[eventIndex] = await replayChannel.Reader.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(actionResult.Events.Select(gameEvent => gameEvent.Sequence), replayedEvents.Select(gameEvent => gameEvent.Sequence));

        var staleSubscription = await reconnected.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
            "JoinGame",
            gameId,
            latestSnapshot.Revision + 10,
            0);
        Assert.True(staleSubscription.SnapshotRequired);
        var notice = await reconnectChannel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("game", notice.GetProperty("resourceType").GetString());
        var refreshedSnapshot = await this.GetSnapshotAsync(gameId, userId);

        var currentSubscription = await reconnected.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
            "JoinGame",
            gameId,
            refreshedSnapshot.Revision,
            refreshedSnapshot.LastEventSequence);
        Assert.False(currentSubscription.SnapshotRequired);
    }

    [Fact]
    public async Task HubSignalsSnapshotRequiredWhenRetainedGameHistoryHasBeenEvicted()
    {
        var room = await this.CreateStartedRoomAsync(PlayerPosition.North);
        var gameId = room.Room.RoomId;
        var userId = room.Members[PlayerPosition.South];
        var initialSnapshot = await this.GetSnapshotAsync(gameId, userId);
        var action = this.CreateAction(initialSnapshot, "overflow-retained-events");
        using var acceptedResponse = await this.SendActionAsync(gameId, userId, action);
        Assert.Equal(HttpStatusCode.OK, acceptedResponse.StatusCode);
        var accepted = (await acceptedResponse.Content.ReadFromJsonAsync<MultiplayerGameActionResponse>(ApiJsonOptions))!;
        Assert.True(accepted.Events.Count < 33);

        var additionalEvents = Enumerable.Range(accepted.Events.Count, 33 - accepted.Events.Count)
            .Select(sequence => new PublicGameEventDto(
                sequence,
                "bid",
                1,
                PlayerPosition.South.ToString()))
            .ToArray();
        await this.factory.Services.GetRequiredService<IMultiplayerRealtimePublisher>()
            .PublishGameEventsAsync(gameId, accepted.Revision, additionalEvents, CancellationToken.None);

        var notices = Channel.CreateUnbounded<JsonElement>();
        await using var connection = this.CreateHubConnection(userId);
        connection.On<JsonElement>("SnapshotRequired", notice => notices.Writer.TryWrite(notice));
        await connection.StartAsync();
        var subscription = await connection.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
            "JoinGame",
            gameId,
            initialSnapshot.Revision,
            initialSnapshot.LastEventSequence);

        Assert.True(subscription.SnapshotRequired);
        var notice = await notices.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("game", notice.GetProperty("resourceType").GetString());
        Assert.Equal(accepted.Revision, notice.GetProperty("revision").GetInt64());
        Assert.Equal(32, notice.GetProperty("sequence").GetInt32());
    }

    [Fact]
    public async Task AnotherRoomMemberCanResumePendingBotsWithoutReceivingOriginalPrivateResponse()
    {
        var room = await this.CreateStartedRoomAsync(PlayerPosition.North);
        var gameId = room.Room.RoomId;
        var game = this.factory.Services.GetRequiredService<MultiplayerGameStore>().Find(gameId);
        Assert.NotNull(game);
        game!.Players[PlayerPosition.East.Index()] = new FailOncePlayer();

        var northId = room.Members[PlayerPosition.North];
        var northEvents = Channel.CreateUnbounded<MultiplayerGameEventEnvelope>();
        await using var northConnection = this.CreateHubConnection(northId);
        northConnection.On<MultiplayerGameEventEnvelope>(
            "GameEvent",
            gameEvent => northEvents.Writer.TryWrite(gameEvent));
        await northConnection.StartAsync();
        var northSnapshot = await this.GetSnapshotAsync(gameId, northId);
        await northConnection.InvokeAsync<MultiplayerRealtimeSubscriptionResult>(
            "JoinGame",
            gameId,
            northSnapshot.Revision,
            northSnapshot.LastEventSequence);

        var southId = room.Members[PlayerPosition.South];
        var action = this.CreateAction(await this.GetSnapshotAsync(gameId, southId), "resume-by-partner");
        using (var failed = await this.SendActionAsync(gameId, southId, action))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        }

        var humanActionEvent = await northEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PlayerPosition.South.ToString(), humanActionEvent.Event.Seat);

        using var resumeRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/games/{gameId}/automation/resume",
            ApiFactory.CreateAccessToken(northId));
        using var resumed = await this.client.SendAsync(resumeRequest);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        var responseBody = await resumed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"state\"", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"hand\"", responseBody, StringComparison.OrdinalIgnoreCase);
        var automation = JsonSerializer.Deserialize<MultiplayerAutomationResponse>(responseBody, ApiJsonOptions)!;
        Assert.Equal(1, automation.Revision);
        Assert.False(automation.AutomationPending);

        var botEvent = await northEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PlayerPosition.East.ToString(), botEvent.Event.Seat);
        Assert.True(botEvent.Event.Automatic);

        using var originalRetry = await this.SendActionAsync(gameId, southId, action);
        Assert.Equal(HttpStatusCode.OK, originalRetry.StatusCode);
        var retried = (await originalRetry.Content.ReadFromJsonAsync<MultiplayerGameActionResponse>(ApiJsonOptions))!;
        Assert.Single(retried.Events.Where(gameEvent => gameEvent.Seat == PlayerPosition.South.ToString()));
        Assert.Equal(1, (await this.GetSnapshotAsync(gameId, southId)).Revision);
    }

    [Fact]
    public async Task SignalRDeliveryFailureDoesNotRollBackOrRepeatAnAcceptedAction()
    {
        using var failingFactory = new ApiFactory(failRealtimeGameDelivery: true);
        using var failingClient = failingFactory.CreateClient();
        var southId = Guid.NewGuid().ToString();
        var creation = await this.CreateLobbyAsync(failingClient, southId, new CreateMultiplayerRoomRequest
        {
            MaximumHumanPlayers = 2,
            EastBot = "random",
            NorthBot = "random",
            WestBot = "random",
        });
        var northId = Guid.NewGuid().ToString();
        await this.JoinLobbyAsync(failingClient, creation.Room.RoomId, creation.InvitationCode, northId, PlayerPosition.North);
        await this.SetReadyAsync(failingClient, creation.Room.RoomId, southId, true);
        await this.SetReadyAsync(failingClient, creation.Room.RoomId, northId, true);
        await this.StartLobbyAsync(failingClient, creation.Room.RoomId, southId);
        var snapshot = await this.GetSnapshotAsync(failingClient, creation.Room.RoomId, southId);
        var action = this.CreateAction(snapshot, "publish-failure-does-not-repeat");

        using var accepted = await this.SendActionAsync(failingClient, creation.Room.RoomId, southId, action);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var retry = await this.SendActionAsync(failingClient, creation.Room.RoomId, southId, action);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var result = (await retry.Content.ReadFromJsonAsync<MultiplayerGameActionResponse>(ApiJsonOptions))!;
        Assert.Equal(1, result.Revision);
        var current = await this.GetSnapshotAsync(failingClient, creation.Room.RoomId, southId);
        Assert.Equal(1, current.Revision);
        Assert.Single(current.State.Bids.Where(bid => bid.Seat == PlayerPosition.South.ToString()));
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

    private HubConnection CreateHubConnection(string? userId)
    {
        var accessToken = userId is null ? null : ApiFactory.CreateAccessToken(userId);
        return new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/multiplayer", options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.AccessTokenProvider = () => Task.FromResult(accessToken);
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var webSocketClient = this.factory.Server.CreateWebSocketClient();
                    if (accessToken is not null)
                    {
                        webSocketClient.ConfigureRequest = request =>
                            request.Headers["Authorization"] = $"Bearer {accessToken}";
                    }

                    return await webSocketClient.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .AddJsonProtocol(options =>
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
    }

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
        => await this.CreateLobbyAsync(this.client, userId, request);

    private async Task SetReadyAsync(string roomId, string userId, bool isReady)
    {
        using var response = await this.SendReadyResponseAsync(roomId, userId, isReady);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<HttpResponseMessage> SendReadyResponseAsync(string roomId, string userId, bool isReady) =>
        this.SendReadyResponseAsync(this.client, roomId, userId, isReady);

    private Task<HttpResponseMessage> SendReadyResponseAsync(
        HttpClient client,
        string roomId,
        string userId,
        bool isReady)
    {
        var request = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{roomId}/ready",
            ApiFactory.CreateAccessToken(userId));
        request.Content = JsonContent.Create(new { isReady });
        return client.SendAsync(request);
    }

    private async Task<CreateMultiplayerRoomResponse> CreateLobbyAsync(
        HttpClient client,
        string userId,
        CreateMultiplayerRoomRequest request)
    {
        using var createRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/multiplayer/lobbies",
            ApiFactory.CreateAccessToken(userId));
        createRequest.Content = JsonContent.Create(request);
        using var response = await client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateMultiplayerRoomResponse>(ApiJsonOptions))!;
    }

    private async Task<MultiplayerGameSnapshotDto> GetSnapshotAsync(string gameId, string userId)
        => await this.GetSnapshotAsync(this.client, gameId, userId);

    private async Task<MultiplayerGameSnapshotDto> GetSnapshotAsync(
        HttpClient client,
        string gameId,
        string userId)
    {
        using var request = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/multiplayer/games/{gameId}",
            ApiFactory.CreateAccessToken(userId));
        using var response = await client.SendAsync(request);
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
        => this.SendActionAsync(this.client, gameId, userId, action);

    private Task<HttpResponseMessage> SendActionAsync(
        HttpClient client,
        string gameId,
        string userId,
        MultiplayerGameActionRequest action)
    {
        var request = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/games/{gameId}/actions",
            ApiFactory.CreateAccessToken(userId));
        request.Content = JsonContent.Create(action, options: ApiJsonOptions);
        return client.SendAsync(request);
    }

    private async Task JoinLobbyAsync(
        HttpClient client,
        string roomId,
        string invitationCode,
        string userId,
        PlayerPosition seat)
    {
        using var request = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{roomId}/join",
            ApiFactory.CreateAccessToken(userId));
        request.Content = JsonContent.Create(new { invitationCode, seat });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task SetReadyAsync(HttpClient client, string roomId, string userId, bool isReady)
    {
        using var request = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{roomId}/ready",
            ApiFactory.CreateAccessToken(userId));
        request.Content = JsonContent.Create(new { isReady });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task StartLobbyAsync(HttpClient client, string roomId, string userId)
    {
        using var request = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{roomId}/start",
            ApiFactory.CreateAccessToken(userId));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
