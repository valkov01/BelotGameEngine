namespace Belot.WebApi.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Xunit;

public sealed class ApiIntegrationTests : IDisposable
{
    private readonly ApiFactory factory;

    private readonly HttpClient client;

    public ApiIntegrationTests()
    {
        this.factory = new ApiFactory();
        this.client = this.factory.CreateClient();
    }

    public void Dispose()
    {
        this.client.Dispose();
        this.factory.Dispose();
    }

    [Fact]
    public async Task HealthBotsAndDifficultySelectionAreAvailable()
    {
        using var health = await this.client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ok", (await health.Content.ReadFromJsonAsync<HealthResponse>())?.Status);

        var bots = await this.client.GetFromJsonAsync<BotProfileDto[]>("/api/bots");
        Assert.Equal(new[] { "random", "beginner", "skilled", "expert", "master" }, bots!.Select(bot => bot.Id));

        using var invalidGame = await this.client.PostAsJsonAsync("/api/games", new { north = "unknown" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidGame.StatusCode);

        var state = await this.CreateGameAsync(new CreateGameRequest { North = "random", East = "expert", West = "master" });
        Assert.Equal("random", state.Bots["North"]);
        Assert.Equal("expert", state.Bots["East"]);
        Assert.Equal("master", state.Bots["West"]);
    }

    [Fact]
    public async Task CorsAllowsConfiguredOriginsAndExpectedPreflightHeadersOnly()
    {
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/games");
        preflight.Headers.Add("Origin", "https://lovable.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type,accept");

        using var response = await this.client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("https://lovable.example", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var allowedMethods = response.Headers.GetValues("Access-Control-Allow-Methods").Single();
        Assert.Contains("GET", allowedMethods, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("POST", allowedMethods, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OPTIONS", allowedMethods, StringComparison.OrdinalIgnoreCase);
        var allowedHeaders = response.Headers.GetValues("Access-Control-Allow-Headers").Single();
        Assert.Contains("content-type", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("accept", allowedHeaders, StringComparison.OrdinalIgnoreCase);

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        allowedRequest.Headers.Add("Origin", "https://lovable.example");
        using var allowedResponse = await this.client.SendAsync(allowedRequest);
        Assert.Equal("https://lovable.example", allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        deniedRequest.Headers.Add("Origin", "https://untrusted.example");
        using var deniedResponse = await this.client.SendAsync(deniedRequest);
        Assert.False(deniedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task OpenApiDocumentIsPublicAndDescribesTheGameplayContract()
    {
        using var response = await this.client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        var paths = root.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/health", out _));
        Assert.True(paths.TryGetProperty("/api/bots", out _));
        Assert.True(paths.TryGetProperty("/api/games", out var games));
        Assert.True(games.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty("/api/games/{gameId}", out var game));
        Assert.True(game.TryGetProperty("get", out _));
        Assert.True(paths.TryGetProperty("/api/games/{gameId}/actions", out var actions));
        Assert.True(actions.TryGetProperty("post", out _));

        var schemas = root.GetProperty("components").GetProperty("schemas");
        Assert.Equal(
            new[] { "Club", "Diamond", "Heart", "Spade" },
            schemas.GetProperty("Card").GetProperty("properties").GetProperty("suit").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            new[] { "Seven", "Eight", "Nine", "Ten", "Jack", "Queen", "King", "Ace" },
            schemas.GetProperty("Card").GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.True(schemas.TryGetProperty("ApiError", out _));
        Assert.True(schemas.TryGetProperty("GameActionResponse", out _));
    }

    [Fact]
    public async Task EveryOriginalDifficultyExecutesABotDecision()
    {
        foreach (var level in new[] { "random", "beginner", "skilled", "expert", "master" })
        {
            var state = await this.CreateGameAsync(new CreateGameRequest { North = level, East = level, West = level });
            var result = await this.SubmitFirstAvailableActionAsync(state);

            Assert.Contains(result.Events, gameEvent => gameEvent.Seat != "South" && gameEvent.Automatic == true);
            Assert.Equal("South", result.State.ToMove);
        }
    }

    [Fact]
    public async Task ActionsRejectInvalidMovesAutomateBotsAndReturnOnlySouthHand()
    {
        var state = await this.CreateGameAsync(new CreateGameRequest { North = "beginner", East = "skilled", West = "expert" });
        Assert.Equal(5, state.Hand.Count);
        Assert.Equal(20, state.CardCounts.Sum());

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("\"deal\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("opponentHands", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, json.Split("\"suit\"", StringSplitOptions.None).Length - 1);

        var invalidBid = await this.PostActionAsync(state.GameId, new { bid = "not-a-bid" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidBid.StatusCode);
        state = (await this.GetStateAsync(state.GameId))!;
        Assert.Equal("Bid", state.Decision);

        var response = await this.PostActionAsync(state.GameId, new { bid = state.AvailableBids[0] });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<GameActionResponse>())!;
        state = result.State;
        Assert.Equal("South", state.ToMove);

        for (var index = 0; index < 100 && state.Decision != "PlayCard"; index++)
        {
            state = (await this.SubmitFirstAvailableActionAsync(state)).State;
        }

        Assert.Equal("PlayCard", state.Decision);
        Assert.Contains(result.Events, gameEvent => gameEvent.Seat != "South" && gameEvent.Automatic == true);

        var invalidCard = await this.PostActionAsync(state.GameId, new { card = new { suit = "Hearts", type = "invalid" }, belote = false });
        Assert.Equal(HttpStatusCode.BadRequest, invalidCard.StatusCode);

        var played = await this.SubmitFirstAvailableActionAsync(state);
        Assert.Equal("South", played.State.ToMove);
        Assert.Contains(played.Events, gameEvent => gameEvent.Type == "card");
        Assert.Contains(played.Events, gameEvent => gameEvent.Type == "card" && gameEvent.Seat != "South" && gameEvent.Automatic == true);
        Assert.Contains(played.Events, gameEvent => gameEvent.Type == "trick");
        Assert.Equal(played.Events.Select(gameEvent => gameEvent.Sequence).Order(), played.Events.Select(gameEvent => gameEvent.Sequence));
    }

    [Fact]
    public async Task AWellFormedButUnplayableCardIsRejectedWithoutChangingTheGame()
    {
        for (var gameIndex = 0; gameIndex < 10; gameIndex++)
        {
            var state = await this.CreateGameAsync(new CreateGameRequest { North = "random", East = "random", West = "random" });
            for (var actionIndex = 0; actionIndex < 200 && !state.IsFinished; actionIndex++)
            {
                var illegalCard = state.Hand.FirstOrDefault(card => !state.PlayableCards.Contains(card));
                if (state.Decision == "PlayCard" && illegalCard is not null)
                {
                    var original = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    using var response = await this.PostActionAsync(state.GameId, new { card = illegalCard, belote = false });

                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    var unchanged = await this.GetStateAsync(state.GameId);
                    Assert.Equal(original, JsonSerializer.Serialize(unchanged, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    return;
                }

                state = (await this.SubmitFirstAvailableActionAsync(state)).State;
            }
        }

        Assert.Fail("No restricted South play was found in the generated matches.");
    }

    [Fact]
    public async Task OfferedDeclarationsCanBeSubmitted()
    {
        for (var gameIndex = 0; gameIndex < 20; gameIndex++)
        {
            var state = await this.CreateGameAsync(new CreateGameRequest { North = "random", East = "random", West = "random" });
            for (var actionIndex = 0; actionIndex < 250 && !state.IsFinished; actionIndex++)
            {
                if (state.Decision == "Announce" && state.AvailableAnnounces.Count > 0)
                {
                    var offered = state.AvailableAnnounces[0];
                    var response = await this.PostActionAsync(state.GameId, new
                    {
                        announces = new[] { new { type = offered.Type, card = offered.Card } },
                    });
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    var result = (await response.Content.ReadFromJsonAsync<GameActionResponse>())!;
                    Assert.Contains(result.Events, gameEvent => gameEvent.Type == "declarations");
                    return;
                }

                state = (await this.SubmitFirstAvailableActionAsync(state)).State;
            }
        }

        Assert.Fail("No South declaration opportunity was found in the generated matches.");
    }

    [Fact]
    public async Task HumanCanCompleteAFullMatchAgainstBots()
    {
        var state = await this.CreateGameAsync(new CreateGameRequest { North = "random", East = "beginner", West = "random" });
        for (var actionIndex = 0; actionIndex < 1000 && !state.IsFinished; actionIndex++)
        {
            state = (await this.SubmitFirstAvailableActionAsync(state)).State;
        }

        Assert.True(state.IsFinished, "The match did not finish within the action limit.");
        Assert.NotNull(state.Winner);
        Assert.NotEmpty(state.PreviousRounds);
        Assert.Equal(state.SouthNorthPoints, state.PreviousRounds.Sum(round => round.SouthNorthPoints));
        Assert.Equal(state.EastWestPoints, state.PreviousRounds.Sum(round => round.EastWestPoints));
        Assert.True(state.SouthNorthPoints >= 151 || state.EastWestPoints >= 151);
        Assert.All(
            state.PreviousRounds.Where(round => round.Contract.Type != "Pass"),
            round =>
            {
                Assert.Equal(8, round.Tricks.Count);
                Assert.Equal(32, round.Tricks.Sum(trick => trick.Cards.Count));
            });

        var finishedJson = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("\"record\"", finishedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"deal\"", finishedJson, StringComparison.OrdinalIgnoreCase);

        var finishedAction = await this.PostActionAsync(state.GameId, new { bid = "Pass" });
        Assert.Equal(HttpStatusCode.Conflict, finishedAction.StatusCode);
    }

    [Fact]
    public async Task IndependentGamesCanBeActedOnConcurrently()
    {
        var states = await Task.WhenAll(
            this.CreateGameAsync(new CreateGameRequest { North = "random", East = "random", West = "random" }),
            this.CreateGameAsync(new CreateGameRequest { North = "beginner", East = "beginner", West = "beginner" }));
        Assert.NotEqual(states[0].GameId, states[1].GameId);

        var responses = await Task.WhenAll(
            this.SubmitFirstAvailableActionAsync(states[0]),
            this.SubmitFirstAvailableActionAsync(states[1]));
        Assert.Equal(states[0].GameId, responses[0].State.GameId);
        Assert.Equal(states[1].GameId, responses[1].State.GameId);
        Assert.All(responses, response => Assert.Equal("South", response.State.ToMove));
    }

    private async Task<GameStateDto> CreateGameAsync(CreateGameRequest request)
    {
        using var response = await this.client.PostAsJsonAsync("/api/games", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GameStateDto>())!;
    }

    private async Task<GameStateDto?> GetStateAsync(string gameId)
    {
        return await this.client.GetFromJsonAsync<GameStateDto>($"/api/games/{gameId}");
    }

    private Task<HttpResponseMessage> PostActionAsync(string gameId, object action)
    {
        return this.client.PostAsJsonAsync($"/api/games/{gameId}/actions", action);
    }

    private async Task<GameActionResponse> SubmitFirstAvailableActionAsync(GameStateDto state)
    {
        object action = state.Decision switch
        {
            "Bid" => new { bid = state.AvailableBids[0] },
            "Announce" => new
            {
                announces = state.AvailableAnnounces.Select(announce => new { type = announce.Type, card = announce.Card }).ToArray(),
            },
            "PlayCard" => new
            {
                card = state.PlayableCards[0],
                belote = state.BeloteCards.Contains(state.PlayableCards[0]),
            },
            _ => throw new InvalidOperationException($"Unexpected decision: {state.Decision}."),
        };

        using var response = await this.PostActionAsync(state.GameId, action);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GameActionResponse>())!;
    }
}
