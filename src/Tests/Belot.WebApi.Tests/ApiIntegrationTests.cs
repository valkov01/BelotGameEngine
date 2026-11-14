namespace Belot.WebApi.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Belot.AI.ClaudePlayer;
using Belot.Engine.Players;

using Xunit;

public sealed class ApiIntegrationTests : IDisposable
{
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

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
    public async Task MultiplayerRoomsRequireValidJwtWhileSoloRoutesRemainUnauthenticated()
    {
        using var missingToken = await this.client.PostAsync("/api/multiplayer/rooms", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, missingToken.StatusCode);

        using var malformedTokenRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/multiplayer/rooms",
            "not-a-jwt");
        using var malformedToken = await this.client.SendAsync(malformedTokenRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, malformedToken.StatusCode);

        using var wrongIssuerRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/multiplayer/rooms",
            ApiFactory.CreateAccessToken(Guid.NewGuid().ToString(), issuer: "https://other.example/auth/v1"));
        using var wrongIssuer = await this.client.SendAsync(wrongIssuerRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongIssuer.StatusCode);

        using var wrongAudienceRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/multiplayer/rooms",
            ApiFactory.CreateAccessToken(Guid.NewGuid().ToString(), audience: "another-audience"));
        using var wrongAudience = await this.client.SendAsync(wrongAudienceRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);

        using var expiredTokenRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/multiplayer/rooms",
            ApiFactory.CreateAccessToken(Guid.NewGuid().ToString(), expires: DateTime.UtcNow.AddMinutes(-5)));
        using var expiredToken = await this.client.SendAsync(expiredTokenRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredToken.StatusCode);

        using var validTokenRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/multiplayer/rooms",
            ApiFactory.CreateAccessToken(Guid.NewGuid().ToString()));
        using var validToken = await this.client.SendAsync(validTokenRequest);
        Assert.Equal(HttpStatusCode.Created, validToken.StatusCode);

        using var soloResponse = await this.client.PostAsJsonAsync("/api/games", new CreateGameRequest());
        Assert.Equal(HttpStatusCode.Created, soloResponse.StatusCode);
    }

    [Fact]
    public async Task MultiplayerRoomAssignmentKeepsAbsoluteSeatsTeamsAndUniqueOwnership()
    {
        var hostUserId = Guid.NewGuid().ToString();
        var creation = await this.CreateLobbyAsync(hostUserId, new CreateMultiplayerRoomRequest());
        var room = creation.Room;

        Assert.Equal(PlayerPosition.South, room.YourSeat);
        Assert.Equal(
            new[] { PlayerPosition.South, PlayerPosition.East, PlayerPosition.North, PlayerPosition.West },
            room.Seats.Select(seat => seat.Seat));
        Assert.Equal(
            new[] { MultiplayerTeam.SouthNorth, MultiplayerTeam.EastWest, MultiplayerTeam.SouthNorth, MultiplayerTeam.EastWest },
            room.Seats.Select(seat => seat.Team));
        Assert.True(room.Seats[0].IsHost);
        Assert.False(room.Seats[0].IsReady);
        Assert.Equal(43, creation.InvitationCode.Length);

        using var duplicateMembershipRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/join",
            ApiFactory.CreateAccessToken(hostUserId));
        duplicateMembershipRequest.Content = JsonContent.Create(new
        {
            seat = "North",
            invitationCode = creation.InvitationCode,
        });
        using var duplicateMembership = await this.client.SendAsync(duplicateMembershipRequest);
        Assert.Equal(HttpStatusCode.Conflict, duplicateMembership.StatusCode);

        var joiningUserId = Guid.NewGuid().ToString();
        using var joinRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/join",
            ApiFactory.CreateAccessToken(joiningUserId));
        joinRequest.Content = JsonContent.Create(new
        {
            seat = "East",
            invitationCode = creation.InvitationCode,
            userId = hostUserId,
        });
        using var joinResponse = await this.client.SendAsync(joinRequest);
        Assert.Equal(HttpStatusCode.OK, joinResponse.StatusCode);
        var joinedRoom = (await joinResponse.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;
        Assert.Equal(PlayerPosition.East, joinedRoom.YourSeat);
        Assert.True(joinedRoom.Seats[1].IsOccupied);

        using var secondSeatRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/join",
            ApiFactory.CreateAccessToken(joiningUserId));
        secondSeatRequest.Content = JsonContent.Create(new
        {
            seat = "North",
            invitationCode = creation.InvitationCode,
        });
        using var secondSeatResponse = await this.client.SendAsync(secondSeatRequest);
        Assert.Equal(HttpStatusCode.Conflict, secondSeatResponse.StatusCode);

        using var occupiedSeatRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/join",
            ApiFactory.CreateAccessToken(Guid.NewGuid().ToString()));
        occupiedSeatRequest.Content = JsonContent.Create(new
        {
            seat = "East",
            invitationCode = creation.InvitationCode,
        });
        using var occupiedSeatResponse = await this.client.SendAsync(occupiedSeatRequest);
        Assert.Equal(HttpStatusCode.Conflict, occupiedSeatResponse.StatusCode);

        using var hostSeatRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/join",
            ApiFactory.CreateAccessToken(Guid.NewGuid().ToString()));
        hostSeatRequest.Content = JsonContent.Create(new
        {
            seat = "South",
            invitationCode = creation.InvitationCode,
        });
        using var hostSeatResponse = await this.client.SendAsync(hostSeatRequest);
        Assert.Equal(HttpStatusCode.BadRequest, hostSeatResponse.StatusCode);

        using var invalidInvitationRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/join",
            ApiFactory.CreateAccessToken(Guid.NewGuid().ToString()));
        invalidInvitationRequest.Content = JsonContent.Create(new { seat = "West", invitationCode = "incorrect" });
        using var invalidInvitation = await this.client.SendAsync(invalidInvitationRequest);
        Assert.Equal(HttpStatusCode.Forbidden, invalidInvitation.StatusCode);

        using var readRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/multiplayer/lobbies/{room.RoomId}",
            ApiFactory.CreateAccessToken(hostUserId));
        using var readResponse = await this.client.SendAsync(readRequest);
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        var readJson = await readResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(creation.InvitationCode, readJson, StringComparison.Ordinal);
        Assert.DoesNotContain(hostUserId, readJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiplayerRoomReadRequiresMembership()
    {
        var hostUserId = Guid.NewGuid().ToString();
        var creation = await this.CreateLobbyAsync(hostUserId, new CreateMultiplayerRoomRequest());
        var room = creation.Room;

        using var unauthenticated = await this.client.GetAsync($"/api/multiplayer/lobbies/{room.RoomId}");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var nonMemberRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/multiplayer/lobbies/{room.RoomId}",
            ApiFactory.CreateAccessToken(Guid.NewGuid().ToString()));
        using var nonMember = await this.client.SendAsync(nonMemberRequest);
        Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);

        using var memberRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/multiplayer/lobbies/{room.RoomId}",
            ApiFactory.CreateAccessToken(hostUserId));
        using var member = await this.client.SendAsync(memberRequest);
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ReadyHostCanStartTwoThreeOrFourHumanLobbyAndRemainingSeatsBecomeBots(int humanCount)
    {
        var hostId = Guid.NewGuid().ToString();
        var creation = await this.CreateLobbyAsync(hostId, new CreateMultiplayerRoomRequest
        {
            MaximumHumanPlayers = humanCount,
            EastBot = "random",
            NorthBot = "expert",
            WestBot = "master",
        });
        var room = creation.Room;

        using (var prematureStartRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/start",
            ApiFactory.CreateAccessToken(hostId)))
        using (var prematureStart = await this.client.SendAsync(prematureStartRequest))
        {
            Assert.Equal(HttpStatusCode.Conflict, prematureStart.StatusCode);
        }

        var humanIds = new List<string> { hostId };
        foreach (var seat in new[] { PlayerPosition.East, PlayerPosition.North, PlayerPosition.West }.Take(humanCount - 1))
        {
            var userId = Guid.NewGuid().ToString();
            humanIds.Add(userId);
            using var joinRequest = ApiFactory.CreateAuthenticatedRequest(
                HttpMethod.Post,
                $"/api/multiplayer/lobbies/{room.RoomId}/join",
                ApiFactory.CreateAccessToken(userId));
            joinRequest.Content = JsonContent.Create(new { seat, invitationCode = creation.InvitationCode });
            using var joined = await this.client.SendAsync(joinRequest);
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }

        if (humanCount == 2)
        {
            using var overCapacityRequest = ApiFactory.CreateAuthenticatedRequest(
                HttpMethod.Post,
                $"/api/multiplayer/lobbies/{room.RoomId}/join",
                ApiFactory.CreateAccessToken(Guid.NewGuid().ToString()));
            overCapacityRequest.Content = JsonContent.Create(new
            {
                seat = "West",
                invitationCode = creation.InvitationCode,
            });
            using var overCapacity = await this.client.SendAsync(overCapacityRequest);
            Assert.Equal(HttpStatusCode.Conflict, overCapacity.StatusCode);
        }

        using (var notHostStartRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/start",
            ApiFactory.CreateAccessToken(humanIds[1])))
        using (var notHostStart = await this.client.SendAsync(notHostStartRequest))
        {
            Assert.Equal(HttpStatusCode.Forbidden, notHostStart.StatusCode);
        }

        using (var notReadyStartRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/start",
            ApiFactory.CreateAccessToken(hostId)))
        using (var notReadyStart = await this.client.SendAsync(notReadyStartRequest))
        {
            Assert.Equal(HttpStatusCode.Conflict, notReadyStart.StatusCode);
        }

        foreach (var userId in humanIds)
        {
            using var readyRequest = ApiFactory.CreateAuthenticatedRequest(
                HttpMethod.Post,
                $"/api/multiplayer/lobbies/{room.RoomId}/ready",
                ApiFactory.CreateAccessToken(userId));
            readyRequest.Content = JsonContent.Create(new { isReady = true });
            using var readyResponse = await this.client.SendAsync(readyRequest);
            Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
        }

        using var startRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/start",
            ApiFactory.CreateAccessToken(hostId));
        using var startResponse = await this.client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        var started = (await startResponse.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;

        Assert.Equal(MultiplayerRoomStatus.Started, started.Status);
        Assert.Equal(humanCount, started.HumanPlayerCount);
        Assert.Equal(humanCount, started.Seats.Count(seat => seat.IsOccupied && seat.BotProfileId is null));
        Assert.Equal(4 - humanCount, started.Seats.Count(seat => seat.BotProfileId is not null));
        Assert.All(started.Seats.Where(seat => seat.IsOccupied && seat.BotProfileId is null), seat => Assert.True(seat.IsReady));
        var selectedBots = new Dictionary<PlayerPosition, string>
        {
            [PlayerPosition.East] = "random",
            [PlayerPosition.North] = "expert",
            [PlayerPosition.West] = "master",
        };
        foreach (var vacantSeat in started.Seats.Where(seat => seat.BotProfileId is not null))
        {
            Assert.Equal(selectedBots[vacantSeat.Seat], vacantSeat.BotProfileId);
        }

        using var joinAfterStartRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/join",
            ApiFactory.CreateAccessToken(Guid.NewGuid().ToString()));
        joinAfterStartRequest.Content = JsonContent.Create(new
        {
            seat = "West",
            invitationCode = creation.InvitationCode,
        });
        using var joinAfterStart = await this.client.SendAsync(joinAfterStartRequest);
        Assert.Equal(HttpStatusCode.Conflict, joinAfterStart.StatusCode);

        using var reconnectRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/multiplayer/lobbies/{room.RoomId}",
            ApiFactory.CreateAccessToken(humanIds[^1]));
        using var reconnectResponse = await this.client.SendAsync(reconnectRequest);
        Assert.Equal(HttpStatusCode.OK, reconnectResponse.StatusCode);
        var reconnected = (await reconnectResponse.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;
        Assert.Equal(started.RoomId, reconnected.RoomId);
        Assert.Equal(started.Status, reconnected.Status);
        Assert.Equal(started.HumanPlayerCount, reconnected.HumanPlayerCount);
        Assert.Equal(started.Seats, reconnected.Seats);
    }

    [Fact]
    public async Task ConcurrentSeatClaimsAllowOnlyOneOwnerAndReadyCanBeRevoked()
    {
        var hostId = Guid.NewGuid().ToString();
        var creation = await this.CreateLobbyAsync(hostId, new CreateMultiplayerRoomRequest());
        var userIds = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var responses = await Task.WhenAll(userIds.Select(async userId =>
        {
            using var request = ApiFactory.CreateAuthenticatedRequest(
                HttpMethod.Post,
                $"/api/multiplayer/lobbies/{creation.Room.RoomId}/join",
                ApiFactory.CreateAccessToken(userId));
            request.Content = JsonContent.Create(new { seat = "East", invitationCode = creation.InvitationCode });
            return await this.client.SendAsync(request);
        }));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(2, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        foreach (var response in responses)
        {
            response.Dispose();
        }

        var roomResponse = await this.GetLobbyAsync(creation.Room.RoomId, hostId);
        var room = (await roomResponse.Content.ReadFromJsonAsync<MultiplayerRoomDto>(ApiJsonOptions))!;
        roomResponse.Dispose();
        var winningIndex = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.True(winningIndex >= 0);
        var joinedId = userIds[winningIndex];

        using var hostReadyRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/ready",
            ApiFactory.CreateAccessToken(hostId));
        hostReadyRequest.Content = JsonContent.Create(new { isReady = true });
        using var hostReady = await this.client.SendAsync(hostReadyRequest);
        Assert.Equal(HttpStatusCode.OK, hostReady.StatusCode);

        using var joinerReadyRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/ready",
            ApiFactory.CreateAccessToken(joinedId));
        joinerReadyRequest.Content = JsonContent.Create(new { isReady = true });
        using var joinerReady = await this.client.SendAsync(joinerReadyRequest);
        Assert.Equal(HttpStatusCode.OK, joinerReady.StatusCode);

        using var notReadyRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/ready",
            ApiFactory.CreateAccessToken(joinedId));
        notReadyRequest.Content = JsonContent.Create(new { isReady = false });
        using var notReady = await this.client.SendAsync(notReadyRequest);
        Assert.Equal(HttpStatusCode.OK, notReady.StatusCode);

        using var startRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            $"/api/multiplayer/lobbies/{room.RoomId}/start",
            ApiFactory.CreateAccessToken(hostId));
        using var startResponse = await this.client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.Conflict, startResponse.StatusCode);

        var secondCreation = await this.CreateLobbyAsync(Guid.NewGuid().ToString(), new CreateMultiplayerRoomRequest());
        var sharedUserId = Guid.NewGuid().ToString();
        var competingSeats = await Task.WhenAll(new[] { "East", "North" }.Select(async seat =>
        {
            using var request = ApiFactory.CreateAuthenticatedRequest(
                HttpMethod.Post,
                $"/api/multiplayer/lobbies/{secondCreation.Room.RoomId}/join",
                ApiFactory.CreateAccessToken(sharedUserId));
            request.Content = JsonContent.Create(new { seat, invitationCode = secondCreation.InvitationCode });
            return await this.client.SendAsync(request);
        }));
        Assert.Single(competingSeats, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(competingSeats, response => response.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in competingSeats)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task SupabaseJwtValidationAcceptsAsymmetricJwksAlgorithmsAndConfiguredLegacySecret()
    {
        using (var ellipticCurveFactory = new ApiFactory(useEcdsaSigningKey: true))
        using (var ellipticCurveClient = ellipticCurveFactory.CreateClient())
        using (var ellipticCurveRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/multiplayer/lobbies",
            ApiFactory.CreateAccessToken(Guid.NewGuid().ToString(), algorithm: "ES256")))
        using (var ellipticCurveResponse = await ellipticCurveClient.SendAsync(ellipticCurveRequest))
        {
            Assert.Equal(HttpStatusCode.Created, ellipticCurveResponse.StatusCode);
        }

        using (var hmacFactory = new ApiFactory(jwtSecret: ApiFactory.TestJwtSecret))
        using (var hmacClient = hmacFactory.CreateClient())
        using (var hmacRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/multiplayer/lobbies",
            ApiFactory.CreateAccessToken(Guid.NewGuid().ToString(), algorithm: "HS256")))
        using (var hmacResponse = await hmacClient.SendAsync(hmacRequest))
        {
            Assert.Equal(HttpStatusCode.Created, hmacResponse.StatusCode);
        }

        using var unsupportedAlgorithmRequest = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Post,
            "/api/multiplayer/lobbies",
            ApiFactory.CreateAccessToken(Guid.NewGuid().ToString(), algorithm: "HS256"));
        using var unsupportedAlgorithm = await this.client.SendAsync(unsupportedAlgorithmRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unsupportedAlgorithm.StatusCode);
    }

    [Fact]
    public async Task CorsAllowsConfiguredOriginsAndExpectedPreflightHeadersOnly()
    {
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/games");
        preflight.Headers.Add("Origin", "https://preview--belote-bridge-battle.lovable.app");
        preflight.Headers.Add("Access-Control-Request-Method", "DELETE");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization, content-type, x-belot-debug-key");

        using var response = await this.client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            "https://preview--belote-bridge-battle.lovable.app",
            response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var allowedMethods = response.Headers.GetValues("Access-Control-Allow-Methods").Single();
        Assert.Contains("GET", allowedMethods, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("POST", allowedMethods, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DELETE", allowedMethods, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OPTIONS", allowedMethods, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        var allowedHeaders = response.Headers.GetValues("Access-Control-Allow-Headers").Single();
        Assert.Contains("content-type", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authorization", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-belot-debug-key", allowedHeaders, StringComparison.OrdinalIgnoreCase);

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        allowedRequest.Headers.Add("Origin", "https://preview--belote-bridge-battle.lovable.app");
        using var allowedResponse = await this.client.SendAsync(allowedRequest);
        Assert.Equal(
            "https://preview--belote-bridge-battle.lovable.app",
            allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        deniedRequest.Headers.Add("Origin", "https://untrusted.example");
        using var deniedResponse = await this.client.SendAsync(deniedRequest);
        Assert.False(deniedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task CorsAllowsConfiguredOriginForSignalRNegotiationPreflightOnly()
    {
        const string trustedOrigin = "https://preview--belote-bridge-battle.lovable.app";
        using var preflight = new HttpRequestMessage(
            HttpMethod.Options,
            "/hubs/multiplayer/negotiate?negotiateVersion=1");
        preflight.Headers.Add("Origin", trustedOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add(
            "Access-Control-Request-Headers",
            "authorization, content-type, x-requested-with");

        using var response = await this.client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(trustedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        var allowedHeaders = response.Headers.GetValues("Access-Control-Allow-Headers").Single();
        Assert.Contains("authorization", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-requested-with", allowedHeaders, StringComparison.OrdinalIgnoreCase);

        using var deniedPreflight = new HttpRequestMessage(
            HttpMethod.Options,
            "/hubs/multiplayer/negotiate?negotiateVersion=1");
        deniedPreflight.Headers.Add("Origin", "https://untrusted.example");
        deniedPreflight.Headers.Add("Access-Control-Request-Method", "POST");
        deniedPreflight.Headers.Add(
            "Access-Control-Request-Headers",
            "authorization, content-type, x-requested-with");

        using var deniedResponse = await this.client.SendAsync(deniedPreflight);
        Assert.False(deniedResponse.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(deniedResponse.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public void CorsRejectsWildcardAllowedOriginConfiguration()
    {
        using var wildcardFactory = new ApiFactory(corsAllowedOrigin: "*");
        var exception = Assert.ThrowsAny<InvalidOperationException>(() => wildcardFactory.CreateClient());
        Assert.Contains("Cors:AllowedOrigins", exception.ToString(), StringComparison.Ordinal);
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
        Assert.False(paths.TryGetProperty("/api/games/{gameId}/debug/hands", out _));
        Assert.True(paths.TryGetProperty("/api/games/{gameId}/actions", out var actions));
        Assert.True(actions.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty("/api/multiplayer/lobbies", out var createLobby));
        Assert.Equal("BearerAuth", createLobby.GetProperty("post").GetProperty("security")[0].EnumerateObject().Single().Name);
        Assert.True(paths.TryGetProperty("/api/multiplayer/lobbies/{roomId}", out _));
        Assert.True(paths.TryGetProperty("/api/multiplayer/lobbies/{roomId}/join", out _));
        Assert.True(paths.TryGetProperty("/api/multiplayer/lobbies/{roomId}/ready", out _));
        Assert.True(paths.TryGetProperty("/api/multiplayer/lobbies/{roomId}/start", out _));

        var schemas = root.GetProperty("components").GetProperty("schemas");
        Assert.Equal(
            new[] { "Club", "Diamond", "Heart", "Spade" },
            schemas.GetProperty("Card").GetProperty("properties").GetProperty("suit").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            new[] { "Seven", "Eight", "Nine", "Ten", "Jack", "Queen", "King", "Ace" },
            schemas.GetProperty("Card").GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.True(schemas.TryGetProperty("ApiError", out _));
        Assert.True(schemas.TryGetProperty("GameActionResponse", out _));
        Assert.True(schemas.TryGetProperty("CreateMultiplayerRoomResponse", out _));
        Assert.True(schemas.TryGetProperty("MultiplayerRoom", out _));
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
    public void MasterProfileCreatesNeuralMaster()
    {
        Assert.IsType<ClaudePlayerNeural>(BotProfiles.Create("master"));
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
    public async Task DebugHandsEndpointIsUnavailableWhenDisabled()
    {
        var state = await this.CreateGameAsync(new CreateGameRequest());

        using var response = await this.client.GetAsync($"/api/games/{state.GameId}/debug/hands");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DebugHandsRequireAKeyReturnLiveHandsAndDoNotChangeGameplay()
    {
        const string debugAccessKey = "integration-test-debug-key";
        using var enabledFactory = new ApiFactory(revealHandsEnabled: true, debugAccessKey: debugAccessKey);
        using var enabledClient = enabledFactory.CreateClient();
        using var createResponse = await enabledClient.PostAsJsonAsync("/api/games", new CreateGameRequest());
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var state = (await createResponse.Content.ReadFromJsonAsync<GameStateDto>())!;
        var gamePath = $"/api/games/{state.GameId}";
        var debugPath = $"{gamePath}/debug/hands";

        using var missingKeyResponse = await enabledClient.GetAsync(debugPath);
        Assert.Equal(HttpStatusCode.Unauthorized, missingKeyResponse.StatusCode);

        using var invalidKeyRequest = new HttpRequestMessage(HttpMethod.Get, debugPath);
        invalidKeyRequest.Headers.Add("X-Belot-Debug-Key", "incorrect-key");
        using var invalidKeyResponse = await enabledClient.SendAsync(invalidKeyRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidKeyResponse.StatusCode);

        using var beforeResponse = await enabledClient.GetAsync(gamePath);
        var beforeJson = await beforeResponse.Content.ReadAsStringAsync();
        using var debugRequest = new HttpRequestMessage(HttpMethod.Get, debugPath);
        debugRequest.Headers.Add("X-Belot-Debug-Key", debugAccessKey);
        using var debugResponse = await enabledClient.SendAsync(debugRequest);
        Assert.Equal(HttpStatusCode.OK, debugResponse.StatusCode);
        var hands = (await debugResponse.Content.ReadFromJsonAsync<DebugHandsDto>())!;

        Assert.Equal(state.GameId, hands.GameId);
        Assert.Equal(new[] { "South", "North", "East", "West" }, hands.Hands.Select(hand => hand.Seat));
        Assert.Equal(new[] { "Human", "Partner bot", "Opponent bot", "Opponent bot" }, hands.Hands.Select(hand => hand.Role));
        Assert.All(hands.Hands, hand => Assert.Equal(5, hand.Cards.Count));
        Assert.Equal(20, hands.Hands.SelectMany(hand => hand.Cards).Distinct().Count());
        Assert.Equal(state.Hand.OrderBy(card => card.Suit).ThenBy(card => card.Type), hands.Hands[0].Cards.OrderBy(card => card.Suit).ThenBy(card => card.Type));

        var stateOnlyJson = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var hiddenCard in hands.Hands.Skip(1).SelectMany(hand => hand.Cards))
        {
            Assert.DoesNotContain(JsonSerializer.Serialize(hiddenCard, new JsonSerializerOptions(JsonSerializerDefaults.Web)), stateOnlyJson, StringComparison.Ordinal);
            Assert.DoesNotContain(JsonSerializer.Serialize(hiddenCard, new JsonSerializerOptions(JsonSerializerDefaults.Web)), beforeJson, StringComparison.Ordinal);
        }

        using var repeatedDebugRequest = new HttpRequestMessage(HttpMethod.Get, debugPath);
        repeatedDebugRequest.Headers.Add("X-Belot-Debug-Key", debugAccessKey);
        using var repeatedDebugResponse = await enabledClient.SendAsync(repeatedDebugRequest);
        Assert.Equal(JsonSerializer.Serialize(hands, new JsonSerializerOptions(JsonSerializerDefaults.Web)), await repeatedDebugResponse.Content.ReadAsStringAsync());
        using var afterReadResponse = await enabledClient.GetAsync(gamePath);
        Assert.Equal(beforeJson, await afterReadResponse.Content.ReadAsStringAsync());

        state = await AdvanceToPlayCardAsync(enabledClient, state);
        var handsBeforePlay = await GetDebugHandsAsync(enabledClient, debugPath, debugAccessKey);
        var cardToPlay = state.PlayableCards[0];
        using var playResponse = await enabledClient.PostAsJsonAsync(
            $"{gamePath}/actions",
            new { card = cardToPlay, belote = state.BeloteCards.Contains(cardToPlay) });
        Assert.Equal(HttpStatusCode.OK, playResponse.StatusCode);
        var playResult = (await playResponse.Content.ReadFromJsonAsync<GameActionResponse>())!;
        var playedCards = playResult.Events
            .Where(gameEvent => gameEvent.Type == "card")
            .Select(gameEvent => (gameEvent.Seat, gameEvent.Card))
            .ToArray();
        Assert.NotEmpty(playedCards);

        var handsAfterPlay = await GetDebugHandsAsync(enabledClient, debugPath, debugAccessKey);
        foreach (var seatHand in handsBeforePlay.Hands)
        {
            var cardsPlayedBySeat = playedCards
                .Where(played => played.Seat == seatHand.Seat)
                .Select(played => played.Card)
                .ToHashSet();
            var expectedCards = seatHand.Cards.Where(card => !cardsPlayedBySeat.Contains(card));
            var actualCards = handsAfterPlay.Hands.Single(hand => hand.Seat == seatHand.Seat).Cards;
            Assert.Equal(expectedCards.OrderBy(card => card.Suit).ThenBy(card => card.Type), actualCards.OrderBy(card => card.Suit).ThenBy(card => card.Type));
        }

        var actionJson = JsonSerializer.Serialize(playResult, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var hiddenCard in handsAfterPlay.Hands.Skip(1).SelectMany(hand => hand.Cards))
        {
            Assert.DoesNotContain(JsonSerializer.Serialize(hiddenCard, new JsonSerializerOptions(JsonSerializerDefaults.Web)), actionJson, StringComparison.Ordinal);
        }
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
    public async Task ThreeNeuralMastersCompleteAFullMatchThroughTheApi()
    {
        var state = await this.CreateGameAsync(new CreateGameRequest { North = "master", East = "master", West = "master" });
        var bidResponse = await this.PostActionAsync(state.GameId, new { bid = "AllTrumps" });
        Assert.Equal(HttpStatusCode.OK, bidResponse.StatusCode);
        var firstAction = (await bidResponse.Content.ReadFromJsonAsync<GameActionResponse>())!;
        state = firstAction.State;

        var automaticBids = firstAction.Events
            .Where(gameEvent => gameEvent.Type == "bid" && gameEvent.Automatic == true)
            .Select(gameEvent => gameEvent.Seat)
            .ToArray();
        Assert.Equal(new[] { "East", "North", "West" }, automaticBids);

        var eventTypes = new HashSet<string>(firstAction.Events.Select(gameEvent => gameEvent.Type), StringComparer.Ordinal);
        for (var actionIndex = 0; actionIndex < 1000 && !state.IsFinished; actionIndex++)
        {
            var result = await this.SubmitFirstAvailableActionAsync(state);
            state = result.State;
            eventTypes.UnionWith(result.Events.Select(gameEvent => gameEvent.Type));
            Assert.True(state.IsFinished || state.ToMove == "South");
        }

        Assert.True(state.IsFinished, "The match did not finish within the action limit.");
        Assert.NotNull(state.Winner);
        Assert.Contains("declarations", eventTypes);
        Assert.Contains("card", eventTypes);
        Assert.Contains("trick", eventTypes);
        Assert.Contains("deal-ended", eventTypes);
        Assert.Equal(state.SouthNorthPoints, state.PreviousRounds.Sum(round => round.SouthNorthPoints));
        Assert.Equal(state.EastWestPoints, state.PreviousRounds.Sum(round => round.EastWestPoints));
        Assert.True(state.SouthNorthPoints >= 151 || state.EastWestPoints >= 151);
        var nextSeat = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["South"] = "East",
            ["East"] = "North",
            ["North"] = "West",
            ["West"] = "South",
        };
        Assert.All(
            state.PreviousRounds.Where(round => round.Contract.Type != "Pass"),
            round =>
            {
                Assert.Equal(8, round.Tricks.Count);
                Assert.Equal(32, round.Tricks.Sum(trick => trick.Cards.Count));
                foreach (var trick in round.Tricks)
                {
                    for (var cardIndex = 1; cardIndex < trick.Cards.Count; cardIndex++)
                    {
                        Assert.Equal(nextSeat[trick.Cards[cardIndex - 1].Seat], trick.Cards[cardIndex].Seat);
                    }
                }
            });
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

    private static async Task<GameStateDto> AdvanceToPlayCardAsync(HttpClient client, GameStateDto state)
    {
        for (var index = 0; index < 100 && state.Decision != "PlayCard"; index++)
        {
            object action = state.Decision switch
            {
                "Bid" => new { bid = state.AvailableBids[0] },
                "Announce" => new
                {
                    announces = state.AvailableAnnounces.Select(announce => new { type = announce.Type, card = announce.Card }).ToArray(),
                },
                _ => throw new InvalidOperationException($"Unexpected decision: {state.Decision}."),
            };

            using var response = await client.PostAsJsonAsync($"/api/games/{state.GameId}/actions", action);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            state = (await response.Content.ReadFromJsonAsync<GameActionResponse>())!.State;
        }

        Assert.Equal("PlayCard", state.Decision);
        return state;
    }

    private static async Task<DebugHandsDto> GetDebugHandsAsync(HttpClient client, string path, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Belot-Debug-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DebugHandsDto>())!;
    }

    private async Task<GameStateDto> CreateGameAsync(CreateGameRequest request)
    {
        using var response = await this.client.PostAsJsonAsync("/api/games", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GameStateDto>())!;
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
        using var createResponse = await this.client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        return (await createResponse.Content.ReadFromJsonAsync<CreateMultiplayerRoomResponse>(ApiJsonOptions))!;
    }

    private Task<HttpResponseMessage> GetLobbyAsync(string roomId, string userId)
    {
        var request = ApiFactory.CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"/api/multiplayer/lobbies/{roomId}",
            ApiFactory.CreateAccessToken(userId));
        return this.client.SendAsync(request);
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
