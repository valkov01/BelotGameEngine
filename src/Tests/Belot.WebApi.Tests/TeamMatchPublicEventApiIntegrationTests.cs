namespace Belot.WebApi.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Belot.Engine.Players;

using Xunit;

public sealed class TeamMatchPublicEventApiIntegrationTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ApiFactory factory = new();

    private readonly HttpClient client;

    public TeamMatchPublicEventApiIntegrationTests()
    {
        this.client = this.factory.CreateClient();
    }

    [Fact]
    public async Task GameEndpointsReplayOrderedPublicEventsAndRecoverFromFutureCursors()
    {
        var fixture = await this.StartAsync();
        using var initialResponse = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game?afterSequence=-1",
            fixture.Guest);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var initial = (await initialResponse.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Equal(-1, initial.LastEventSequence);
        Assert.Empty(initial.Events);
        Assert.False(initial.SnapshotRequired);

        var request = new TeamMatchHumanActionRequest(
            initial.Revision,
            "cursor-action",
            new GameActionRequest { Bid = "Pass" },
            initial.LastEventSequence);
        using var actionResponse = await this.SendAsync(
            HttpMethod.Post,
            fixture.Root + "/game/actions",
            fixture.Guest,
            request);
        Assert.Equal(HttpStatusCode.OK, actionResponse.StatusCode);
        var accepted = (await actionResponse.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.NotEmpty(accepted.Events);
        Assert.False(accepted.SnapshotRequired);
        Assert.Equal(
            Enumerable.Range(accepted.Events[0].Sequence, accepted.Events.Count),
            accepted.Events.Select(gameEvent => gameEvent.Sequence));
        Assert.Contains(accepted.Events, gameEvent =>
            gameEvent.Type == "bid"
            && gameEvent.Seat == PlayerPosition.South.ToString()
            && gameEvent.Automatic != true);

        var eventsJson = JsonSerializer.Serialize(accepted.Events);
        Assert.DoesNotContain("hand", eventsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("availableBids", eventsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("playableCards", eventsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Host, eventsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Guest, eventsJson, StringComparison.Ordinal);

        var settled = await this.WaitForHumanTurnAsync(fixture, accepted);
        using var continuation = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game?afterSequence=" + accepted.LastEventSequence,
            fixture.Guest);
        var continued = (await continuation.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.False(continued.SnapshotRequired);
        Assert.NotEmpty(continued.Events);
        Assert.All(
            continued.Events,
            gameEvent => Assert.True(gameEvent.Sequence > accepted.LastEventSequence));
        Assert.Equal(
            Enumerable.Range(continued.Events[0].Sequence, continued.Events.Count),
            continued.Events.Select(gameEvent => gameEvent.Sequence));
        Assert.Equal(settled.LastEventSequence, continued.LastEventSequence);

        using var futureCursor = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game?afterSequence=" + (settled.LastEventSequence + 1),
            fixture.Guest);
        var recovered = (await futureCursor.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.True(recovered.SnapshotRequired);
        Assert.Empty(recovered.Events);
        Assert.Equal(settled.State.RoundNumber, recovered.State.RoundNumber);

        using var duplicate = await this.SendAsync(
            HttpMethod.Post,
            fixture.Root + "/game/actions",
            fixture.Guest,
            request);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var afterDuplicate = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game?afterSequence=" + settled.LastEventSequence,
            fixture.Guest);
        var unchanged = (await afterDuplicate.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Equal(settled.LastEventSequence, unchanged.LastEventSequence);
        Assert.Empty(unchanged.Events);
    }

    [Fact]
    public async Task ExpiredCursorReturnsAnAuthoritativeSnapshotWithoutPartialEvents()
    {
        using var limitedFactory = new ApiFactory(teamMatchPublicEventsPerTable: 2);
        using var limitedClient = limitedFactory.CreateClient();
        var fixture = await this.StartAsync(limitedClient);
        using var initialResponse = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game?afterSequence=-1",
            fixture.Guest,
            client: limitedClient);
        var initial = (await initialResponse.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;

        using var actionResponse = await this.SendAsync(
            HttpMethod.Post,
            fixture.Root + "/game/actions",
            fixture.Guest,
            new TeamMatchHumanActionRequest(
                initial.Revision,
                "expired-history",
                new GameActionRequest { Bid = "Pass" },
                -1),
            limitedClient);
        Assert.Equal(HttpStatusCode.OK, actionResponse.StatusCode);
        var accepted = (await actionResponse.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        await this.WaitForHumanTurnAsync(fixture, accepted, limitedClient);
        using var expired = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game?afterSequence=-1",
            fixture.Guest,
            client: limitedClient);
        var recovery = (await expired.Content
            .ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.True(recovery.LastEventSequence > 1);
        Assert.True(recovery.SnapshotRequired);
        Assert.Empty(recovery.Events);
        Assert.InRange(recovery.State.Hand.Count, 1, 8);

        using var snapshotResponse = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game",
            fixture.Guest,
            client: limitedClient);
        var snapshot = (await snapshotResponse.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Equal(recovery.LastEventSequence, snapshot.LastEventSequence);
        Assert.Equal(recovery.State.RoundNumber, snapshot.State.RoundNumber);
        Assert.Equal(recovery.State.Hand, snapshot.State.Hand);

        using var resumed = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game?afterSequence=" + recovery.LastEventSequence,
            fixture.Guest,
            client: limitedClient);
        var latest = (await resumed.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.False(latest.SnapshotRequired);
        Assert.Empty(latest.Events);
    }

    [Fact]
    public async Task FuturePostCursorStillRequiresSnapshotAfterTheAcceptedActionAdvancesIt()
    {
        var fixture = await this.StartAsync();
        using var initialResponse = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game?afterSequence=-1",
            fixture.Guest);
        var initial = (await initialResponse.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;

        using var actionResponse = await this.SendAsync(
            HttpMethod.Post,
            fixture.Root + "/game/actions",
            fixture.Guest,
            new TeamMatchHumanActionRequest(
                initial.Revision,
                "future-post-cursor",
                new GameActionRequest { Bid = "Pass" },
                0));
        Assert.Equal(HttpStatusCode.OK, actionResponse.StatusCode);
        var recovery = (await actionResponse.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.True(recovery.SnapshotRequired);
        Assert.Empty(recovery.Events);
        Assert.True(recovery.LastEventSequence >= 0);

        var settled = await this.WaitForHumanTurnAsync(fixture, recovery);
        using var snapshotResponse = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game",
            fixture.Guest);
        var snapshot = (await snapshotResponse.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Equal(settled.LastEventSequence, snapshot.LastEventSequence);
        Assert.Equal(settled.State.RoundNumber, snapshot.State.RoundNumber);
        Assert.Equal(settled.State.Hand, snapshot.State.Hand);
    }

    [Fact]
    public async Task HybridLimitsAndCursorReplayWorkForEightPlayersSharingOneIp()
    {
        const string ipAddress = "198.51.100.120";
        using var hybridFactory = new ApiFactory(
            useProductionRateLimit: true,
            hybridRateLimitEnabled: true,
            hybridIpPermitLimit: 300,
            hybridReadPermitLimit: 17,
            hybridMutationPermitLimit: 20,
            hybridPerUserConcurrencyLimit: 4,
            hybridAggregateConcurrencyLimit: 32);
        using var hybridClient = hybridFactory.CreateClient();
        var members = Enumerable.Range(0, 8)
            .Select(_ => Guid.NewGuid().ToString())
            .ToArray();
        var seats = new[]
        {
            (1, PlayerPosition.South),
            (1, PlayerPosition.East),
            (1, PlayerPosition.North),
            (1, PlayerPosition.West),
            (2, PlayerPosition.South),
            (2, PlayerPosition.East),
            (2, PlayerPosition.North),
            (2, PlayerPosition.West),
        };

        using var creation = await this.SendAsync(
            HttpMethod.Post,
            "/api/team-matches",
            members[0],
            new { scheduledDeals = 4, barometerEnabled = true },
            hybridClient,
            ipAddress);
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var setup = (await creation.Content.ReadFromJsonAsync<CreateTeamMatchResponse>(JsonOptions))!;
        var root = "/api/team-matches/" + setup.Lobby.MatchId;

        for (var index = 1; index < members.Length; index++)
        {
            using var joined = await this.SendAsync(
                HttpMethod.Post,
                root + "/join",
                members[index],
                new
                {
                    invitationCode = setup.InvitationCode,
                    table = seats[index].Item1,
                    seat = seats[index].Item2,
                },
                hybridClient,
                ipAddress);
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }

        using var started = await this.SendAsync(
            HttpMethod.Post,
            root + "/start",
            members[0],
            client: hybridClient,
            ipAddress: ipAddress);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        TeamMatchGameSnapshotDto? hostSnapshot = null;
        for (var memberIndex = 0; memberIndex < members.Length; memberIndex++)
        {
            for (var poll = 0; poll < 16; poll++)
            {
                using var response = await this.SendAsync(
                    HttpMethod.Get,
                    root + "/game?afterSequence=-1",
                    members[memberIndex],
                    client: hybridClient,
                    ipAddress: ipAddress);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var snapshot =
                    (await response.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
                Assert.Equal(seats[memberIndex].Item1, snapshot.Table);
                Assert.Equal(seats[memberIndex].Item2, snapshot.YourSeat);
                Assert.Equal(5, snapshot.State.Hand.Count);
                if (memberIndex == 0)
                {
                    hostSnapshot = snapshot;
                }
            }
        }

        Assert.NotNull(hostSnapshot);
        using var actionResponse = await this.SendAsync(
            HttpMethod.Post,
            root + "/game/actions",
            members[0],
            new TeamMatchHumanActionRequest(
                hostSnapshot.Revision,
                "hybrid-cursor-action",
                new GameActionRequest { Bid = "Pass" },
                hostSnapshot.LastEventSequence),
            hybridClient,
            ipAddress);
        Assert.Equal(HttpStatusCode.OK, actionResponse.StatusCode);
        var accepted =
            (await actionResponse.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.NotEmpty(accepted.Events);
        Assert.Equal(
            Enumerable.Range(accepted.Events[0].Sequence, accepted.Events.Count),
            accepted.Events.Select(gameEvent => gameEvent.Sequence));
        var eventsJson = JsonSerializer.Serialize(accepted.Events);
        Assert.DoesNotContain("hand", eventsJson, StringComparison.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            Assert.DoesNotContain(member, eventsJson, StringComparison.Ordinal);
        }

        using var replay = await this.SendAsync(
            HttpMethod.Get,
            root + "/game?afterSequence=" + hostSnapshot.LastEventSequence,
            members[0],
            client: hybridClient,
            ipAddress: ipAddress);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed =
            (await replay.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Equal(accepted.LastEventSequence, replayed.LastEventSequence);
        Assert.Equal(accepted.Events.Select(gameEvent => gameEvent.Sequence),
            replayed.Events.Select(gameEvent => gameEvent.Sequence));
    }

    [Fact]
    public async Task InitialBotActionsAreAvailableToTheCurrentTableMember()
    {
        var host = Guid.NewGuid().ToString();
        var guest = Guid.NewGuid().ToString();
        using var creation = await this.SendAsync(
            HttpMethod.Post,
            "/api/team-matches",
            host,
            new { scheduledDeals = 4, organizerTable = 1, organizerSeat = PlayerPosition.East });
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var setup = (await creation.Content.ReadFromJsonAsync<CreateTeamMatchResponse>(JsonOptions))!;
        var root = "/api/team-matches/" + setup.Lobby.MatchId;
        using var join = await this.SendAsync(
            HttpMethod.Post,
            root + "/join",
            guest,
            new { invitationCode = setup.InvitationCode, table = 2, seat = PlayerPosition.South });
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);

        foreach (var table in new[] { 1, 2 })
        {
            foreach (var seat in Enum.GetValues<PlayerPosition>()
                         .Where(seat => seat is PlayerPosition.South or PlayerPosition.East
                             or PlayerPosition.North or PlayerPosition.West))
            {
                if ((table == 1 && seat == PlayerPosition.East)
                    || (table == 2 && seat == PlayerPosition.South))
                {
                    continue;
                }

                using var assigned = await this.SendAsync(
                    HttpMethod.Post,
                    root + "/bots",
                    host,
                    new { table, seat, botProfileId = "skilled" });
                Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
            }
        }

        using var start = await this.SendAsync(HttpMethod.Post, root + "/start", host);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var snapshotResponse = await this.SendAsync(
            HttpMethod.Get,
            root + "/game?afterSequence=-1",
            host);
        Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);
        var snapshot = (await snapshotResponse.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Contains(snapshot.Events, gameEvent =>
            gameEvent.Type == "bid"
            && gameEvent.Seat == PlayerPosition.South.ToString()
            && gameEvent.Automatic == true);
        Assert.Equal(
            Enumerable.Range(snapshot.Events[0].Sequence, snapshot.Events.Count),
            snapshot.Events.Select(gameEvent => gameEvent.Sequence));
        Assert.DoesNotContain(host, JsonSerializer.Serialize(snapshot.Events), StringComparison.Ordinal);
        Assert.DoesNotContain(guest, JsonSerializer.Serialize(snapshot.Events), StringComparison.Ordinal);
        Assert.DoesNotContain(setup.InvitationCode, JsonSerializer.Serialize(snapshot.Events), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        this.client.Dispose();
        this.factory.Dispose();
    }

    private async Task<(string Host, string Guest, string Root)> StartAsync(HttpClient? client = null)
    {
        var host = Guid.NewGuid().ToString();
        var guest = Guid.NewGuid().ToString();
        using var creation = await this.SendAsync(
            HttpMethod.Post,
            "/api/team-matches",
            host,
            new { scheduledDeals = 4, barometerEnabled = true },
            client);
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var setup = (await creation.Content.ReadFromJsonAsync<CreateTeamMatchResponse>(JsonOptions))!;
        var root = "/api/team-matches/" + setup.Lobby.MatchId;
        using var join = await this.SendAsync(
            HttpMethod.Post,
            root + "/join",
            guest,
            new { invitationCode = setup.InvitationCode, table = 2, seat = PlayerPosition.South },
            client);
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);

        foreach (var table in new[] { 1, 2 })
        {
            foreach (var seat in Enum.GetValues<PlayerPosition>()
                         .Where(seat => seat is PlayerPosition.South or PlayerPosition.East
                             or PlayerPosition.North or PlayerPosition.West))
            {
                if ((table == 1 && seat == PlayerPosition.South)
                    || (table == 2 && seat == PlayerPosition.South))
                {
                    continue;
                }

                using var assigned = await this.SendAsync(
                    HttpMethod.Post,
                    root + "/bots",
                    host,
                    new { table, seat, botProfileId = "skilled" },
                    client);
                Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
            }
        }

        using var start = await this.SendAsync(HttpMethod.Post, root + "/start", host, client: client);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        return (host, guest, root);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string user,
        object? body = null,
        HttpClient? client = null,
        string? ipAddress = null)
    {
        using var request = ApiFactory.CreateAuthenticatedRequest(
            method,
            path,
            ApiFactory.CreateAccessToken(user));
        if (body != null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        if (ipAddress != null)
        {
            request.Headers.Add("X-Test-Client-Ip", ipAddress);
        }

        return await (client ?? this.client).SendAsync(request);
    }

    private async Task<TeamMatchGameSnapshotDto> WaitForHumanTurnAsync(
        (string Host, string Guest, string Root) fixture,
        TeamMatchGameSnapshotDto initial,
        HttpClient? client = null)
    {
        var state = initial;
        for (var wait = 0;
             wait < 500
             && !state.State.IsFinished
             && (state.State.ToMove != PlayerPosition.South.ToString()
                 || state.AutomationPending);
             wait++)
        {
            await Task.Delay(10);
            using var response = await this.SendAsync(
                HttpMethod.Get,
                fixture.Root + "/game",
                fixture.Guest,
                client: client);
            state = (await response.Content
                .ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        }

        Assert.False(state.AutomationPending);
        Assert.Equal(PlayerPosition.South.ToString(), state.State.ToMove);
        return state;
    }
}
