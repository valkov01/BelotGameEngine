namespace Belot.WebApi.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Belot.Engine.Players;

using Xunit;

public sealed class TeamMatchMemberResultsTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly ApiFactory factory = new();
    private readonly HttpClient client;
    public TeamMatchMemberResultsTests() => this.client = this.factory.CreateClient();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothPlayersCanGetOnlyAuthorizedCrossTableComparison(bool enabled)
    {
        var host = Guid.NewGuid().ToString();
        var guest = Guid.NewGuid().ToString();
        var outsider = Guid.NewGuid().ToString();
        using var creation = await this.Send(HttpMethod.Post, "/api/team-matches", host,
            new { scheduledDeals = 4, barometerEnabled = enabled });
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var setup = (await creation.Content.ReadFromJsonAsync<CreateTeamMatchResponse>(JsonOptions))!;
        var root = "/api/team-matches/" + setup.Lobby.MatchId;
        using var join = await this.Send(HttpMethod.Post, root + "/join", guest,
            new { invitationCode = setup.InvitationCode, table = 2, seat = PlayerPosition.South });
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        foreach (var table in new[] { 1, 2 })
        {
            foreach (var seat in new[] { PlayerPosition.East, PlayerPosition.North, PlayerPosition.West })
            {
                using var bot = await this.Send(HttpMethod.Post, root + "/bots", host,
                    new { table, seat, botProfileId = "skilled" });
                Assert.Equal(HttpStatusCode.OK, bot.StatusCode);
            }
        }
        using var start = await this.Send(HttpMethod.Post, root + "/start", host);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        foreach (var member in new[] { host, guest })
        {
            using var response = await this.Send(HttpMethod.Get, root + "/results", member);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(enabled, parsed.RootElement.GetProperty("barometerEnabled").GetBoolean());
            Assert.False(parsed.RootElement.GetProperty("isMatchComplete").GetBoolean());
            Assert.Equal(0, parsed.RootElement.GetProperty("comparedDeals").GetArrayLength());
            Assert.Equal(enabled ? JsonValueKind.Number : JsonValueKind.Null,
                parsed.RootElement.GetProperty("team1TotalPoints").ValueKind);
            Assert.DoesNotContain("hand", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("playableCards", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("canonical", json, StringComparison.OrdinalIgnoreCase);
        }

        using var denied = await this.Send(HttpMethod.Get, root + "/results", outsider);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var anonymous = await this.client.GetAsync(root + "/results");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    public void Dispose() { this.client.Dispose(); this.factory.Dispose(); }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string user, object? body = null)
    {
        using var request = ApiFactory.CreateAuthenticatedRequest(
            method, path, ApiFactory.CreateAccessToken(user));
        if (body != null) request.Content = JsonContent.Create(body, options: JsonOptions);
        return await this.client.SendAsync(request);
    }
}
