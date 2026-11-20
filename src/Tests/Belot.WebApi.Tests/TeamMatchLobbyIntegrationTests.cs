namespace Belot.WebApi.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Belot.Engine.Players;

using Xunit;

/// <summary>
/// Real authenticated HTTP integration tests: the same Supabase JWT validation
/// policy used by the existing single-table multiplayer backend is enforced here.
/// </summary>
public sealed class TeamMatchLobbyIntegrationTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ApiFactory factory = new();

    private readonly HttpClient client;

    public TeamMatchLobbyIntegrationTests() => this.client = this.factory.CreateClient();

    [Fact]
    public async Task OrganizerCanChooseAnySeatAndConfigurationIsPrivate()
    {
        var host = Guid.NewGuid().ToString();
        var stranger = Guid.NewGuid().ToString();
        var request = new CreateTeamMatchRequest
        {
            Name = "The Great Match",
            Description = "Friends match",
            Team1Name = "Blue",
            Team2Name = "Red",
            ScheduledDeals = 10,
            BarometerEnabled = false,
            OrganizerTable = 2,
            OrganizerSeat = PlayerPosition.West,
        };

        using var anonymousCreate = await this.client.PostAsJsonAsync("/api/team-matches", request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousCreate.StatusCode);

        var (room, invite) = await this.CreateAsync(host, request);
        Assert.Equal("The Great Match", room.Name);
        Assert.Equal("Friends match", room.Description);
        Assert.Equal("Blue", room.Team1Name);
        Assert.Equal("Red", room.Team2Name);
        Assert.Equal(10, room.ScheduledDeals);
        Assert.False(room.BarometerEnabled);
        Assert.False(room.IsStarted);
        Assert.Equal(2, room.YourSeat.Table);
        Assert.Equal(PlayerPosition.West, room.YourSeat.Seat);
        Assert.Equal(8, room.Seats.Count);
        Assert.Equal(4, room.Seats.Count(seat => seat.TeamNumber == 1));
        Assert.Equal(4, room.Seats.Count(seat => seat.TeamNumber == 2));
        Assert.True(room.Seats.Single(seat => seat.IsHost).IsYou);
        Assert.Equal(1, room.Seats.Count(seat => seat.IsOccupied));
        Assert.True(invite.Length >= 40);

        using var denied = await this.SendAsync(HttpMethod.Get, $"/api/team-matches/{room.MatchId}", stranger);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var anonymousGet = await this.client.GetAsync($"/api/team-matches/{room.MatchId}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousGet.StatusCode);

        using var memberGet = await this.SendAsync(HttpMethod.Get, $"/api/team-matches/{room.MatchId}", host);
        Assert.Equal(HttpStatusCode.OK, memberGet.StatusCode);
        var json = await memberGet.Content.ReadAsStringAsync();
        Assert.DoesNotContain(invite, json, StringComparison.Ordinal);
        Assert.DoesNotContain(host, json, StringComparison.Ordinal);
        Assert.DoesNotContain("reservedUserId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerUserId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deck", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hand", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(21)]
    public async Task InvalidDealCountIsRejected(int dealCount)
    {
        using var response = await this.SendAsync(HttpMethod.Post, "/api/team-matches",
            Guid.NewGuid().ToString(), new { scheduledDeals = dealCount });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InvitationAndReservedSeatAccessIsAccountScoped()
    {
        var host = Guid.NewGuid().ToString();
        var invited = Guid.NewGuid().ToString();
        var stranger = Guid.NewGuid().ToString();
        var (lobby, invite) = await this.CreateAsync(host);
        var basePath = $"/api/team-matches/{lobby.MatchId}";

        using (var reserved = await this.SendAsync(HttpMethod.Post, basePath + "/reservations", host,
            new { table = 1, seat = PlayerPosition.North, reservedUserId = invited }))
        {
            Assert.Equal(HttpStatusCode.OK, reserved.StatusCode);
        }

        using (var unauthorizedReserve = await this.SendAsync(HttpMethod.Post, basePath + "/reservations", stranger,
            new { table = 2, seat = PlayerPosition.East, reservedUserId = stranger }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unauthorizedReserve.StatusCode);
        }

        using (var missing = await this.SendAsync(HttpMethod.Post, basePath + "/join", stranger,
            new { invitationCode = "made-up-secret", table = 1, seat = PlayerPosition.East }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        }

        using (var protectedSeat = await this.SendAsync(HttpMethod.Post, basePath + "/join", stranger,
            new { invitationCode = invite, table = 1, seat = PlayerPosition.North }))
        {
            Assert.Equal(HttpStatusCode.Conflict, protectedSeat.StatusCode);
        }

        // A reserved recipient cannot bypass the reservation by claiming
        // an unrelated open seat; she must receive the exact nominated seat.
        using (var wrongSeat = await this.SendAsync(HttpMethod.Post, basePath + "/join", invited,
            new { invitationCode = invite, table = 2, seat = PlayerPosition.South }))
        {
            Assert.Equal(HttpStatusCode.Conflict, wrongSeat.StatusCode);
        }

        using (var accepted = await this.SendAsync(HttpMethod.Post, basePath + "/join", invited,
            new { invitationCode = invite, table = 1, seat = PlayerPosition.North }))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var view = (await accepted.Content.ReadFromJsonAsync<TeamMatchLobbyDto>(JsonOptions))!;
            Assert.Equal(PlayerPosition.North, view.YourSeat.Seat);
            Assert.False(view.Seats.Single(seat => seat.Table == 1 && seat.Seat == PlayerPosition.North).IsReserved);
        }

        using (var duplicate = await this.SendAsync(HttpMethod.Post, basePath + "/join", invited,
            new { invitationCode = invite, table = 1, seat = PlayerPosition.East }))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        using (var general = await this.SendAsync(HttpMethod.Post, basePath + "/join", stranger,
            new { invitationCode = invite, table = 2, seat = PlayerPosition.South }))
        {
            Assert.Equal(HttpStatusCode.OK, general.StatusCode);
        }
    }

    [Fact]
    public async Task OnlyHostCanAssignRemoveBotsAndRotateCode()
    {
        var host = Guid.NewGuid().ToString();
        var guest = Guid.NewGuid().ToString();
        var stranger = Guid.NewGuid().ToString();
        var (lobby, invite) = await this.CreateAsync(host);
        var url = $"/api/team-matches/{lobby.MatchId}";

        using (var joined = await this.SendAsync(HttpMethod.Post, url + "/join", guest,
            new { invitationCode = invite, table = 2, seat = PlayerPosition.North }))
        {
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }

        using (var forbidden = await this.SendAsync(HttpMethod.Post, url + "/bots", guest,
            new { table = 2, seat = PlayerPosition.East, botProfileId = "expert" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var invalidBot = await this.SendAsync(HttpMethod.Post, url + "/bots", host,
            new { table = 2, seat = PlayerPosition.East, botProfileId = "fantasy-level" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidBot.StatusCode);
        }

        using (var bot = await this.SendAsync(HttpMethod.Post, url + "/bots", host,
            new { table = 2, seat = PlayerPosition.East, botProfileId = "expert" }))
        {
            Assert.Equal(HttpStatusCode.OK, bot.StatusCode);
            var view = (await bot.Content.ReadFromJsonAsync<TeamMatchLobbyDto>(JsonOptions))!;
            Assert.Equal("expert", view.Seats.Single(seat => seat.Table == 2 && seat.Seat == PlayerPosition.East).BotProfileId);
        }

        using (var removed = await this.SendAsync(HttpMethod.Post, url + "/bots", host,
            new { table = 2, seat = PlayerPosition.East, botProfileId = (string?)null }))
        {
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            var view = (await removed.Content.ReadFromJsonAsync<TeamMatchLobbyDto>(JsonOptions))!;
            Assert.Null(view.Seats.Single(seat => seat.Table == 2 && seat.Seat == PlayerPosition.East).BotProfileId);
        }

        using (var outsiderRotate = await this.SendAsync(HttpMethod.Post, url + "/invitations/rotate", stranger))
        {
            Assert.Equal(HttpStatusCode.Forbidden, outsiderRotate.StatusCode);
        }

        string rotated;
        using (var hostRotate = await this.SendAsync(HttpMethod.Post, url + "/invitations/rotate", host))
        {
            Assert.Equal(HttpStatusCode.OK, hostRotate.StatusCode);
            var payload = (await hostRotate.Content.ReadFromJsonAsync<RotateTeamMatchInviteResponse>(JsonOptions))!;
            rotated = payload.InvitationCode;
        }

        Assert.NotEqual(invite, rotated);
        using (var deniedOld = await this.SendAsync(HttpMethod.Post, url + "/join", stranger,
            new { invitationCode = invite, table = 1, seat = PlayerPosition.East }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedOld.StatusCode);
        }

        using (var acceptedNew = await this.SendAsync(HttpMethod.Post, url + "/join", stranger,
            new { invitationCode = rotated, table = 1, seat = PlayerPosition.East }))
        {
            Assert.Equal(HttpStatusCode.OK, acceptedNew.StatusCode);
        }

        using var lobbyResponse = await this.SendAsync(HttpMethod.Get, url, guest);
        var json = await lobbyResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(rotated, json, StringComparison.Ordinal);
        Assert.DoesNotContain(invite, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LobbyAllowsMemberSeatMovesOnlyBeforeStartAndKeepsOrganizerPlaying()
    {
        var host = Guid.NewGuid().ToString();
        var guest = Guid.NewGuid().ToString();
        var (lobby, code) = await this.CreateAsync(host);
        var url = $"/api/team-matches/{lobby.MatchId}";

        using (var moveHost = await this.SendAsync(HttpMethod.Post, url + "/seat", host,
            new { table = 2, seat = PlayerPosition.West }))
        {
            Assert.Equal(HttpStatusCode.OK, moveHost.StatusCode);
            var view = (await moveHost.Content.ReadFromJsonAsync<TeamMatchLobbyDto>(JsonOptions))!;
            Assert.True(view.Seats.Single(seat => seat.Table == 2 && seat.Seat == PlayerPosition.West).IsHost);
        }

        using (var join = await this.SendAsync(HttpMethod.Post, url + "/join", guest,
            new { invitationCode = code, table = 1, seat = PlayerPosition.North }))
        {
            Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        }

        using (var moveGuest = await this.SendAsync(HttpMethod.Post, url + "/seat", guest,
            new { table = 1, seat = PlayerPosition.South }))
        {
            Assert.Equal(HttpStatusCode.OK, moveGuest.StatusCode);
        }

        // Fill remaining six seats, including all bots on otherwise free seats.
        var seats = (from table in new[] { 1, 2 }
                     from seat in new[] { PlayerPosition.South, PlayerPosition.East, PlayerPosition.North, PlayerPosition.West }
                     where !(table == 2 && seat == PlayerPosition.West)
                        && !(table == 1 && seat == PlayerPosition.South)
                     select (table, seat)).ToArray();
        foreach (var (table, seat) in seats)
        {
            using var bot = await this.SendAsync(HttpMethod.Post, url + "/bots", host,
                new { table, seat, botProfileId = "skilled" });
            Assert.Equal(HttpStatusCode.OK, bot.StatusCode);
        }

        using (var guestStart = await this.SendAsync(HttpMethod.Post, url + "/start", guest))
        {
            Assert.Equal(HttpStatusCode.Forbidden, guestStart.StatusCode);
        }

        using (var started = await this.SendAsync(HttpMethod.Post, url + "/start", host))
        {
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            var view = (await started.Content.ReadFromJsonAsync<TeamMatchLobbyDto>(JsonOptions))!;
            Assert.True(view.IsStarted);
            Assert.Equal(8, view.Seats.Count(seat => seat.IsOccupied));
        }

        using (var cannotMove = await this.SendAsync(HttpMethod.Post, url + "/seat", host,
            new { table = 1, seat = PlayerPosition.North }))
        {
            Assert.Equal(HttpStatusCode.Conflict, cannotMove.StatusCode);
        }

        using (var cannotRemoveBot = await this.SendAsync(HttpMethod.Post, url + "/bots", host,
            new { table = 1, seat = PlayerPosition.North, botProfileId = (string?)null }))
        {
            Assert.Equal(HttpStatusCode.Conflict, cannotRemoveBot.StatusCode);
        }

        using (var cannotJoin = await this.SendAsync(HttpMethod.Post, url + "/join", Guid.NewGuid().ToString(),
            new { invitationCode = code, table = 1, seat = PlayerPosition.North }))
        {
            Assert.Equal(HttpStatusCode.Conflict, cannotJoin.StatusCode);
        }
    }

    [Fact]
    public async Task OrganizerCanStartWithOneHumanAndAnEntireBotTable()
    {
        var host = Guid.NewGuid().ToString();
        var (lobby, _) = await this.CreateAsync(host);
        var url = $"/api/team-matches/{lobby.MatchId}";
        foreach (var table in new[] { 1, 2 })
        {
            foreach (var seat in new[] { PlayerPosition.South, PlayerPosition.East, PlayerPosition.North, PlayerPosition.West })
            {
                if (table == 1 && seat == PlayerPosition.South)
                {
                    continue;
                }

                using var bot = await this.SendAsync(HttpMethod.Post, url + "/bots", host,
                    new { table, seat, botProfileId = "skilled" });
                Assert.Equal(HttpStatusCode.OK, bot.StatusCode);
            }
        }

        using var start = await this.SendAsync(HttpMethod.Post, url + "/start", host);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var stateResponse = await this.SendAsync(HttpMethod.Get, url, host);
        var state = (await stateResponse.Content.ReadFromJsonAsync<TeamMatchLobbyDto>(JsonOptions))!;
        Assert.True(state.IsStarted);
        Assert.Equal(8, state.Seats.Count(seat => seat.IsOccupied));
    }

    [Fact]
    public async Task SimultaneousClaimsForLastFreeSeatCannotBothSucceed()
    {
        var host = Guid.NewGuid().ToString();
        var (lobby, code) = await this.CreateAsync(host);
        var url = $"/api/team-matches/{lobby.MatchId}/join";
        var one = Guid.NewGuid().ToString();
        var two = Guid.NewGuid().ToString();
        var replies = await Task.WhenAll(
            this.SendAsync(HttpMethod.Post, url, one,
                new { invitationCode = code, table = 2, seat = PlayerPosition.East }),
            this.SendAsync(HttpMethod.Post, url, two,
                new { invitationCode = code, table = 2, seat = PlayerPosition.East }));
        try
        {
            Assert.Equal(1, replies.Count(reply => reply.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, replies.Count(reply => reply.StatusCode == HttpStatusCode.Conflict));
        }
        finally
        {
            foreach (var response in replies)
            {
                response.Dispose();
            }
        }
    }

    public void Dispose()
    {
        this.client.Dispose();
        this.factory.Dispose();
    }

    private async Task<(TeamMatchLobbyDto Lobby, string InvitationCode)> CreateAsync(
        string host,
        CreateTeamMatchRequest? request = null)
    {
        using var response = await this.SendAsync(HttpMethod.Post, "/api/team-matches", host,
            request ?? new CreateTeamMatchRequest());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateTeamMatchResponse>(JsonOptions))!;
        Assert.NotNull(created.Lobby);
        Assert.NotNull(created.InvitationCode);
        return (created.Lobby, created.InvitationCode);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string user,
        object? body = null)
    {
        using var request = ApiFactory.CreateAuthenticatedRequest(
            method, path, ApiFactory.CreateAccessToken(user));
        if (body != null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return await this.client.SendAsync(request);
    }
}
