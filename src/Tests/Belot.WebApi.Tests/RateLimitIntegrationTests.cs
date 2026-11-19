namespace Belot.WebApi.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Belot.Engine.Players;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

public sealed class RateLimitIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task HybridPolicySupportsPollingPlayersSharingOneIp(int players)
    {
        using var factory = CreateHybridFactory(ipLimit: 600, readLimit: 90);
        using var client = factory.CreateClient();

        var requests = Enumerable.Range(0, players * 37);
        foreach (var requestNumber in requests)
        {
            var user = "shared-ip-user-" + (requestNumber % players);
            using var response = await Send(
                client,
                HttpMethod.Get,
                "/api/team-matches/not-a-member",
                user,
                ipAddress: "198.51.100.20");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task EightAccountsHaveIndependentReadBudgetsOnTheSameIp()
    {
        using var factory = CreateHybridFactory(ipLimit: 1000, readLimit: 2);
        using var client = factory.CreateClient();

        for (var userNumber = 0; userNumber < 8; userNumber++)
        {
            var user = "independent-user-" + userNumber;
            for (var requestNumber = 0; requestNumber < 2; requestNumber++)
            {
                using var accepted = await Send(
                    client,
                    HttpMethod.Get,
                    "/api/team-matches/not-a-member",
                    user,
                    ipAddress: "198.51.100.21");
                Assert.Equal(HttpStatusCode.NotFound, accepted.StatusCode);
            }

            using var rejected = await Send(
                client,
                HttpMethod.Get,
                "/api/team-matches/not-a-member",
                user,
                ipAddress: "198.51.100.21");
            AssertRateLimited(rejected, "team-match-read-user");
        }
    }

    [Fact]
    public async Task EightPlayersOnSeparateIpsReceiveIndependentIpBudgets()
    {
        using var factory = CreateHybridFactory(ipLimit: 1, readLimit: 10);
        using var client = factory.CreateClient();

        for (var userNumber = 0; userNumber < 8; userNumber++)
        {
            var address = "198.51.100." + (30 + userNumber);
            using var accepted = await Send(
                client,
                HttpMethod.Get,
                "/api/team-matches/not-a-member",
                "separate-ip-user-" + userNumber,
                ipAddress: address);
            Assert.Equal(HttpStatusCode.NotFound, accepted.StatusCode);

            using var rejected = await Send(
                client,
                HttpMethod.Get,
                "/api/team-matches/not-a-member",
                "separate-ip-user-" + userNumber,
                ipAddress: address);
            AssertRateLimited(rejected, "hybrid-ip");
        }
    }

    [Fact]
    public async Task BrowserTabsShareOneAuthenticatedUserBudget()
    {
        using var factory = CreateHybridFactory(ipLimit: 10, readLimit: 2);
        using var client = factory.CreateClient();
        var user = Guid.NewGuid().ToString();

        for (var tab = 0; tab < 2; tab++)
        {
            using var accepted = await Send(
                client,
                HttpMethod.Get,
                "/api/team-matches/not-a-member",
                user,
                ipAddress: "198.51.100.40");
            Assert.Equal(HttpStatusCode.NotFound, accepted.StatusCode);
        }

        using var rejected = await Send(
            client,
            HttpMethod.Get,
            "/api/team-matches/not-a-member",
            user,
            ipAddress: "198.51.100.40");
        AssertRateLimited(rejected, "team-match-read-user");
    }

    [Fact]
    public async Task AnonymousAndInvalidJwtTrafficHitsTheEarlyIpGuard()
    {
        using var factory = CreateHybridFactory(ipLimit: 3);
        using var client = factory.CreateClient();

        using var missingToken = await Send(
            client,
            HttpMethod.Get,
            "/api/team-matches/not-a-member",
            user: null,
            ipAddress: "198.51.100.50");
        Assert.Equal(HttpStatusCode.Unauthorized, missingToken.StatusCode);

        using var malformed = await Send(
            client,
            HttpMethod.Get,
            "/api/team-matches/not-a-member",
            user: null,
            accessToken: "not-a-jwt",
            ipAddress: "198.51.100.50");
        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);

        using var validAuthentication = await Send(
            client,
            HttpMethod.Get,
            "/api/team-matches/not-a-member",
            "valid-user",
            ipAddress: "198.51.100.50");
        Assert.Equal(HttpStatusCode.NotFound, validAuthentication.StatusCode);

        using var rejected = await Send(
            client,
            HttpMethod.Get,
            "/api/team-matches/not-a-member",
            user: null,
            ipAddress: "198.51.100.50");
        AssertRateLimited(rejected, "hybrid-ip");
    }

    [Fact]
    public async Task BurstHasSeparateReadAndMutationBudgets()
    {
        using var factory = CreateHybridFactory(
            ipLimit: 50,
            readLimit: 2,
            mutationLimit: 1);
        using var client = factory.CreateClient();
        var user = Guid.NewGuid().ToString();

        for (var request = 0; request < 2; request++)
        {
            using var read = await Send(
                client,
                HttpMethod.Get,
                "/api/team-matches/not-a-member",
                user,
                ipAddress: "198.51.100.60");
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        }

        using var rejectedRead = await Send(
            client,
            HttpMethod.Get,
            "/api/team-matches/not-a-member",
            user,
            ipAddress: "198.51.100.60");
        AssertRateLimited(rejectedRead, "team-match-read-user");

        using var mutation = await Send(
            client,
            HttpMethod.Post,
            "/api/team-matches/not-a-member/start",
            user,
            ipAddress: "198.51.100.60");
        Assert.Equal(HttpStatusCode.NotFound, mutation.StatusCode);

        using var rejectedMutation = await Send(
            client,
            HttpMethod.Post,
            "/api/team-matches/not-a-member/start",
            user,
            ipAddress: "198.51.100.60");
        AssertRateLimited(rejectedMutation, "team-match-mutation-user");
    }

    [Fact]
    public async Task AuthorizationFailuresStillReturn401And403WhenUnderBudget()
    {
        using var factory = CreateHybridFactory(ipLimit: 100, mutationLimit: 20);
        using var client = factory.CreateClient();
        var host = Guid.NewGuid().ToString();
        var stranger = Guid.NewGuid().ToString();

        using var creation = await Send(
            client,
            HttpMethod.Post,
            "/api/team-matches",
            host,
            new { scheduledDeals = 4 },
            "198.51.100.70");
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var setup = (await creation.Content.ReadFromJsonAsync<CreateTeamMatchResponse>(JsonOptions))!;
        var root = "/api/team-matches/" + setup.Lobby.MatchId;

        using var anonymous = await Send(
            client,
            HttpMethod.Get,
            root,
            user: null,
            ipAddress: "198.51.100.70");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var forbidden = await Send(
            client,
            HttpMethod.Get,
            root,
            stranger,
            ipAddress: "198.51.100.70");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task RejectedActionDoesNotChangeGameOrLeakPrivateData()
    {
        using var factory = CreateHybridFactory(
            ipLimit: 1000,
            readLimit: 100,
            mutationLimit: 8);
        using var client = factory.CreateClient();
        var host = Guid.NewGuid().ToString();
        var guest = Guid.NewGuid().ToString();

        using var creation = await Send(
            client,
            HttpMethod.Post,
            "/api/team-matches",
            host,
            new { scheduledDeals = 4 },
            "198.51.100.80");
        var setup = (await creation.Content.ReadFromJsonAsync<CreateTeamMatchResponse>(JsonOptions))!;
        var root = "/api/team-matches/" + setup.Lobby.MatchId;
        using var join = await Send(
            client,
            HttpMethod.Post,
            root + "/join",
            guest,
            new { invitationCode = setup.InvitationCode, table = 2, seat = PlayerPosition.South },
            "198.51.100.80");
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);

        for (var table = 1; table <= 2; table++)
        {
            foreach (var seat in new[] { PlayerPosition.East, PlayerPosition.North, PlayerPosition.West })
            {
                using var assign = await Send(
                    client,
                    HttpMethod.Post,
                    root + "/bots",
                    host,
                    new { table, seat, botProfileId = "skilled" },
                    "198.51.100.80");
                Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
            }
        }

        using var start = await Send(
            client,
            HttpMethod.Post,
            root + "/start",
            host,
            ipAddress: "198.51.100.80");
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        using var current = await Send(
            client,
            HttpMethod.Get,
            root + "/game",
            host,
            ipAddress: "198.51.100.80");
        var currentState = (await current.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        using var rejected = await Send(
            client,
            HttpMethod.Post,
            root + "/game/actions",
            host,
            new
            {
                expectedTableRevision = currentState.Revision,
                commandId = "must-not-commit",
                action = new { bid = "Pass" },
            },
            "198.51.100.80");
        AssertRateLimited(rejected, "team-match-mutation-user");

        using var after = await Send(
            client,
            HttpMethod.Get,
            root + "/game",
            host,
            ipAddress: "198.51.100.80");
        var afterState = (await after.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Equal(currentState.Revision, afterState.Revision);
        Assert.Equal(currentState.State.Hand, afterState.State.Hand);
        var rejectedBody = await rejected.Content.ReadAsStringAsync();
        Assert.DoesNotContain(host, rejectedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(guest, rejectedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(setup.Lobby.MatchId, rejected.Headers.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HybridPolicyDoesNotChangeSoloOrNormalMultiplayer()
    {
        using var factory = CreateHybridFactory(ipLimit: 100, mutationLimit: 1);
        using var client = factory.CreateClient();
        var user = Guid.NewGuid().ToString();

        using var solo = await client.PostAsJsonAsync("/api/games", new CreateGameRequest());
        Assert.Equal(HttpStatusCode.Created, solo.StatusCode);

        using var multiplayer = await Send(
            client,
            HttpMethod.Post,
            "/api/multiplayer/rooms",
            user,
            new CreateMultiplayerRoomRequest(),
            "198.51.100.90");
        Assert.Equal(HttpStatusCode.Created, multiplayer.StatusCode);
    }

    [Fact]
    public async Task LegacyDefaultRemains120RequestsPerMinuteWhenHybridIsDisabled()
    {
        using var factory = new ApiFactory(useProductionRateLimit: true);
        using var client = factory.CreateClient();

        for (var request = 0; request < 120; request++)
        {
            using var accepted = await client.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var rejected = await client.GetAsync("/api/health");
        AssertRateLimited(rejected, "legacy-ip");
    }

    [Fact]
    public async Task ConcurrencyBudgetsRejectWithoutQueueing()
    {
        using var factory = CreateHybridFactory(
            aggregateConcurrencyLimit: 2,
            perUserConcurrencyLimit: 1);
        var limiter = factory.Services.GetRequiredService<HybridRateLimiter>();
        using var first = limiter.AcquireAggregate("198.51.100.100");
        using var second = limiter.AcquireAggregate("198.51.100.101");
        using var third = limiter.AcquireAggregate("198.51.100.102");
        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.False(third.Accepted);
        Assert.Equal("hybrid-aggregate-concurrency", third.Policy);

        using var userFirst = limiter.AcquireAuthenticated(
            "same-user",
            TeamMatchRateLimitKind.Read);
        using var userSecond = limiter.AcquireAuthenticated(
            "same-user",
            TeamMatchRateLimitKind.Read);
        Assert.True(userFirst.Accepted);
        Assert.False(userSecond.Accepted);
        Assert.Equal("team-match-user-concurrency", userSecond.Policy);
    }

    private static ApiFactory CreateHybridFactory(
        int ipLimit = 600,
        int readLimit = 90,
        int mutationLimit = 20,
        int perUserConcurrencyLimit = 100,
        int aggregateConcurrencyLimit = 1000) =>
        new(
            useProductionRateLimit: true,
            hybridRateLimitEnabled: true,
            hybridIpPermitLimit: ipLimit,
            hybridReadPermitLimit: readLimit,
            hybridMutationPermitLimit: mutationLimit,
            hybridPerUserConcurrencyLimit: perUserConcurrencyLimit,
            hybridAggregateConcurrencyLimit: aggregateConcurrencyLimit);

    private static async Task<HttpResponseMessage> Send(
        HttpClient client,
        HttpMethod method,
        string path,
        string? user,
        object? body = null,
        string? ipAddress = null,
        string? accessToken = null)
    {
        var request = user is null && accessToken is null
            ? new HttpRequestMessage(method, path)
            : ApiFactory.CreateAuthenticatedRequest(
                method,
                path,
                accessToken ?? ApiFactory.CreateAccessToken(user!));
        if (body != null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        if (ipAddress != null)
        {
            request.Headers.Add("X-Test-Client-Ip", ipAddress);
        }

        return await client.SendAsync(request);
    }

    private static void AssertRateLimited(HttpResponseMessage response, string policy)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application", response.Headers.GetValues("X-Belot-RateLimit-Source").Single());
        Assert.Equal(policy, response.Headers.GetValues("X-Belot-RateLimit-Policy").Single());
        Assert.True(response.Headers.RetryAfter?.Delta >= TimeSpan.Zero
            || response.Headers.RetryAfter is not null);
    }
}
