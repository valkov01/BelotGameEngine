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

        using var continuation = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game?afterSequence=" + accepted.LastEventSequence,
            fixture.Guest);
        var continued = (await continuation.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Empty(continued.Events);
        Assert.False(continued.SnapshotRequired);
        Assert.Equal(accepted.LastEventSequence, continued.LastEventSequence);

        using var futureCursor = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game?afterSequence=" + (accepted.LastEventSequence + 1),
            fixture.Guest);
        var recovered = (await futureCursor.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.True(recovered.SnapshotRequired);
        Assert.Empty(recovered.Events);
        Assert.Equal(accepted.State.RoundNumber, recovered.State.RoundNumber);

        using var duplicate = await this.SendAsync(
            HttpMethod.Post,
            fixture.Root + "/game/actions",
            fixture.Guest,
            request);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var afterDuplicate = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game?afterSequence=" + accepted.LastEventSequence,
            fixture.Guest);
        var unchanged = (await afterDuplicate.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Equal(accepted.LastEventSequence, unchanged.LastEventSequence);
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
        var recovery = (await actionResponse.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.True(recovery.LastEventSequence > 1);
        Assert.True(recovery.SnapshotRequired);
        Assert.Empty(recovery.Events);
        Assert.True(recovery.State.Hand.Count == 5);

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

        using var snapshotResponse = await this.SendAsync(
            HttpMethod.Get,
            fixture.Root + "/game",
            fixture.Guest);
        var snapshot = (await snapshotResponse.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Equal(recovery.LastEventSequence, snapshot.LastEventSequence);
        Assert.Equal(recovery.State.RoundNumber, snapshot.State.RoundNumber);
        Assert.Equal(recovery.State.Hand, snapshot.State.Hand);
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
        HttpClient? client = null)
    {
        using var request = ApiFactory.CreateAuthenticatedRequest(
            method,
            path,
            ApiFactory.CreateAccessToken(user));
        if (body != null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return await (client ?? this.client).SendAsync(request);
    }
}
