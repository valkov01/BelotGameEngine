namespace Belot.WebApi.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Belot.Engine.Players;

using Xunit;

/// <summary>First-release named seat invitation inbox, no general join-code required.</summary>
public sealed class TeamMatchPreStartInvitationTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ApiFactory factory = new();
    private readonly HttpClient client;

    public TeamMatchPreStartInvitationTests() => this.client = this.factory.CreateClient();

    [Fact]
    public async Task NamedReservedPlayerCanAcceptTheirExactPrestartSeatFromPrivateInbox()
    {
        var host = Guid.NewGuid().ToString();
        var invited = Guid.NewGuid().ToString();
        var outsider = Guid.NewGuid().ToString();
        using var created = await this.Send(HttpMethod.Post, "/api/team-matches", host,
            new { scheduledDeals = 4 });
        var setup = (await created.Content.ReadFromJsonAsync<CreateTeamMatchResponse>(JsonOptions))!;
        var url = "/api/team-matches/" + setup.Lobby.MatchId;

        using var reserved = await this.Send(HttpMethod.Post, url + "/reservations", host,
            new { table = 2, seat = PlayerPosition.East, reservedUserId = invited });
        Assert.Equal(HttpStatusCode.OK, reserved.StatusCode);

        using var inbox = await this.Send(HttpMethod.Get, "/api/team-matches/invitations", invited);
        Assert.Equal(HttpStatusCode.OK, inbox.StatusCode);
        var invitation = Assert.Single((await inbox.Content.ReadFromJsonAsync<TeamMatchReplacementInvitationDto[]>(JsonOptions))!);
        Assert.Equal(2, invitation.Table);
        Assert.Equal(PlayerPosition.East, invitation.Seat);
        Assert.Equal(setup.Lobby.MatchId, invitation.MatchId);

        using var outsiderInbox = await this.Send(HttpMethod.Get, "/api/team-matches/invitations", outsider);
        Assert.Empty((await outsiderInbox.Content.ReadFromJsonAsync<TeamMatchReplacementInvitationDto[]>(JsonOptions))!);

        using var outsiderClaim = await this.Send(HttpMethod.Post,
            url + "/invitations/" + invitation.InvitationId + "/accept", outsider);
        Assert.Equal(HttpStatusCode.Forbidden, outsiderClaim.StatusCode);

        using var joined = await this.Send(HttpMethod.Post,
            url + "/invitations/" + invitation.InvitationId + "/accept", invited);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var lobby = (await joined.Content.ReadFromJsonAsync<TeamMatchLobbyDto>(JsonOptions))!;
        Assert.False(lobby.IsStarted);
        Assert.Equal(2, lobby.YourSeat.Table);
        Assert.Equal(PlayerPosition.East, lobby.YourSeat.Seat);
        Assert.Equal(2, lobby.Seats.Count(s => s.IsOccupied));
        Assert.False(lobby.Seats.Single(s => s.Table == 2 && s.Seat == PlayerPosition.East).IsReserved);

        using var authorizedGet = await this.Send(HttpMethod.Get, url, invited);
        Assert.Equal(HttpStatusCode.OK, authorizedGet.StatusCode);

        using var replay = await this.Send(HttpMethod.Post,
            url + "/invitations/" + invitation.InvitationId + "/accept", invited);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        using var emptyInbox = await this.Send(HttpMethod.Get, "/api/team-matches/invitations", invited);
        Assert.Empty((await emptyInbox.Content.ReadFromJsonAsync<TeamMatchReplacementInvitationDto[]>(JsonOptions))!);
    }

    [Fact]
    public async Task CancellingOrReassigningReservationRevokesOldRecipientInvite()
    {
        var host = Guid.NewGuid().ToString();
        var old = Guid.NewGuid().ToString();
        var newer = Guid.NewGuid().ToString();
        using var created = await this.Send(HttpMethod.Post, "/api/team-matches", host,
            new { scheduledDeals = 4 });
        var setup = (await created.Content.ReadFromJsonAsync<CreateTeamMatchResponse>(JsonOptions))!;
        var url = "/api/team-matches/" + setup.Lobby.MatchId;

        using var first = await this.Send(HttpMethod.Post, url + "/reservations", host,
            new { table = 1, seat = PlayerPosition.North, reservedUserId = old });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var oldInbox = await this.Send(HttpMethod.Get, "/api/team-matches/invitations", old);
        var oldInvite = Assert.Single((await oldInbox.Content.ReadFromJsonAsync<TeamMatchReplacementInvitationDto[]>(JsonOptions))!);

        using var changed = await this.Send(HttpMethod.Post, url + "/reservations", host,
            new { table = 1, seat = PlayerPosition.North, reservedUserId = newer });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var cannotAccept = await this.Send(HttpMethod.Post,
            url + "/invitations/" + oldInvite.InvitationId + "/accept", old);
        Assert.Equal(HttpStatusCode.Forbidden, cannotAccept.StatusCode);

        using var newInbox = await this.Send(HttpMethod.Get, "/api/team-matches/invitations", newer);
        var newInvite = Assert.Single((await newInbox.Content.ReadFromJsonAsync<TeamMatchReplacementInvitationDto[]>(JsonOptions))!);
        using var cancelled = await this.Send(HttpMethod.Post, url + "/reservations", host,
            new { table = 1, seat = PlayerPosition.North, reservedUserId = (string?)null });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        using var nowRevoked = await this.Send(HttpMethod.Post,
            url + "/invitations/" + newInvite.InvitationId + "/accept", newer);
        Assert.Equal(HttpStatusCode.Forbidden, nowRevoked.StatusCode);
    }

    public void Dispose() { this.client.Dispose(); this.factory.Dispose(); }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string user, object? body = null)
    {
        using var req = ApiFactory.CreateAuthenticatedRequest(method, path, ApiFactory.CreateAccessToken(user));
        if (body != null) req.Content = JsonContent.Create(body, options: JsonOptions);
        return await this.client.SendAsync(req);
    }
}
