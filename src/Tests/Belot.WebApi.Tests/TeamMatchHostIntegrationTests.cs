namespace Belot.WebApi.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Belot.Engine.Players;

using Xunit;

/// <summary>Real JWT / HTTP host-control checks; no mocked seat or match permissions.</summary>
public sealed class TeamMatchHostIntegrationTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ApiFactory factory = new();

    private readonly HttpClient client;

    public TeamMatchHostIntegrationTests() => this.client = this.factory.CreateClient();

    [Fact]
    public async Task KickedSeatPausesWithoutAutoBotAndOnlyAcceptedHumanResumesSameCards()
    {
        var fixture = await this.StartAsync(barometerEnabled: false);
        var host = fixture.Host;
        var guest = fixture.Guest;
        var root = fixture.Root;
        using var original = await this.Send(HttpMethod.Get, root + "/game", guest);
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        var before = (await original.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Equal(2, before.Table);
        Assert.False(before.IsPaused);
        Assert.Equal(PlayerPosition.South, before.YourSeat);
        Assert.Equal(5, before.State.Hand.Count);
        Assert.Equal(1, before.State.RoundNumber);

        using var reported = await this.Send(HttpMethod.Post, root + "/reports", host,
            new { table = 2, seat = PlayerPosition.South, reason = "Not playing" });
        Assert.Equal(HttpStatusCode.Accepted, reported.StatusCode);
        using var duplicateReport = await this.Send(HttpMethod.Post, root + "/reports", host,
            new { table = 2, seat = PlayerPosition.South, reason = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, duplicateReport.StatusCode);

        using var kicked = await this.Send(HttpMethod.Post, root + "/organizer/kick", host,
            new { table = 2, seat = PlayerPosition.South });
        Assert.Equal(HttpStatusCode.OK, kicked.StatusCode);
        var kickedLobby = (await kicked.Content.ReadFromJsonAsync<TeamMatchLobbyDto>(JsonOptions))!;
        var vacant = kickedLobby.Seats.Single(seat => seat.Table == 2 && seat.Seat == PlayerPosition.South);
        Assert.False(vacant.IsOccupied);
        Assert.Null(vacant.BotProfileId);

        using (var privateNotice = await this.Send(HttpMethod.Get, root + "/notices", guest))
        {
            Assert.Equal(HttpStatusCode.OK, privateNotice.StatusCode);
            var notice = Assert.Single(
                (await privateNotice.Content.ReadFromJsonAsync<TeamMatchRemovalNoticeDto[]>(JsonOptions))!);
            Assert.Equal(2, notice.Table);
            Assert.Equal(PlayerPosition.South, notice.Seat);
            Assert.Equal("Вие бяхте сменен от организатора на мача.", notice.Message);
        }

        using (var deniedNotice = await this.Send(HttpMethod.Get, root + "/notices", Guid.NewGuid().ToString()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedNotice.StatusCode);
        }

        using var revoked = await this.Send(HttpMethod.Get, root + "/game", guest);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        using var revokedEvents = await this.Send(
            HttpMethod.Get, root + "/game?afterSequence=-1", guest);
        Assert.Equal(HttpStatusCode.Forbidden, revokedEvents.StatusCode);
        using var outsiderEvents = await this.Send(
            HttpMethod.Get, root + "/game?afterSequence=-1", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, outsiderEvents.StatusCode);
        using var oldAction = await this.Send(HttpMethod.Post, root + "/game/actions", guest,
            new { expectedTableRevision = before.Revision, commandId = "stale-kicked",
                  action = new { bid = "Pass" } });
        Assert.Equal(HttpStatusCode.Forbidden, oldAction.StatusCode);

        using var pausedView = await this.Send(HttpMethod.Get, root + "/organizer", host);
        Assert.Equal(HttpStatusCode.OK, pausedView.StatusCode);
        var paused = await pausedView.Content.ReadFromJsonAsync<JsonElement>();
        var table2 = paused.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("table").GetInt32() == 2);
        Assert.True(table2.GetProperty("isPaused").GetBoolean());
        Assert.Equal("unknown", table2.GetProperty("connectivityStatus").GetString());
        Assert.Single(paused.GetProperty("reports").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, paused.GetProperty("results").GetProperty("team1TotalPoints").ValueKind);
        var pausedJson = pausedView.Content.ReadAsStringAsync().Result;
        Assert.DoesNotContain("\"hand\"", pausedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"playableCards\"", pausedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"availableBids\"", pausedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Guest, pausedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Host, pausedJson, StringComparison.Ordinal);

        var replacement = Guid.NewGuid().ToString();
        string inviteId;
        using (var invite = await this.Send(HttpMethod.Post, root + "/organizer/invite-replacement", host,
                   new { table = 2, seat = PlayerPosition.South, userId = replacement }))
        {
            Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
            inviteId = (await invite.Content.ReadFromJsonAsync<TeamMatchReplacementInvitationDto>(JsonOptions))!.InvitationId;
        }

        using (var pending = await this.Send(HttpMethod.Get, root + "/organizer", host))
        {
            var state = await pending.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(state.GetProperty("tables").EnumerateArray()
                .Single(t => t.GetProperty("table").GetInt32() == 2)
                .GetProperty("isPaused").GetBoolean());
        }

        using var outsidersInbox = await this.Send(HttpMethod.Get, "/api/team-matches/invitations", guest);
        Assert.Empty((await outsidersInbox.Content.ReadFromJsonAsync<TeamMatchReplacementInvitationDto[]>(JsonOptions))!);
        using var inbox = await this.Send(HttpMethod.Get, "/api/team-matches/invitations", replacement);
        Assert.Single((await inbox.Content.ReadFromJsonAsync<TeamMatchReplacementInvitationDto[]>(JsonOptions))!);

        using var forbiddenAccept = await this.Send(HttpMethod.Post,
            root + "/invitations/" + inviteId + "/accept", guest);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenAccept.StatusCode);

        using var accepted = await this.Send(HttpMethod.Post,
            root + "/invitations/" + inviteId + "/accept", replacement);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var restored = await this.Send(HttpMethod.Get, root + "/game", replacement);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var after = (await restored.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Equal(before.State.RoundNumber, after.State.RoundNumber);
        Assert.Equal(before.State.Hand, after.State.Hand);
        Assert.Equal(before.State.ToMove, after.State.ToMove);
        Assert.False(after.IsPaused);
        Assert.True(after.Revision > before.Revision);
        Assert.Equal(0, after.State.PreviousRounds.Count);
        using var kickedStillForbidden = await this.Send(HttpMethod.Get, root + "/game", guest);
        Assert.Equal(HttpStatusCode.Forbidden, kickedStillForbidden.StatusCode);
        using var replayInvite = await this.Send(HttpMethod.Post,
            root + "/invitations/" + inviteId + "/accept", replacement);
        Assert.Equal(HttpStatusCode.Conflict, replayInvite.StatusCode);
    }

    [Fact]
    public async Task ExplicitBotAssignmentCancelsWaitingInviteAndActuallyAdvancesEngine()
    {
        var fx = await this.StartAsync();
        using var kick = await this.Send(HttpMethod.Post, fx.Root + "/organizer/kick", fx.Host,
            new { table = 2, seat = PlayerPosition.South });
        Assert.Equal(HttpStatusCode.OK, kick.StatusCode);

        var invited = Guid.NewGuid().ToString();
        string inviteId;
        using (var invite = await this.Send(HttpMethod.Post, fx.Root + "/organizer/invite-replacement", fx.Host,
            new { table = 2, seat = PlayerPosition.South, userId = invited }))
        {
            Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
            inviteId = (await invite.Content.ReadFromJsonAsync<TeamMatchReplacementInvitationDto>(JsonOptions))!.InvitationId;
        }

        using var assign = await this.Send(HttpMethod.Post, fx.Root + "/organizer/assign-replacement-bot",
            fx.Host, new { table = 2, seat = PlayerPosition.South, botProfileId = "skilled" });
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        var lobby = (await assign.Content.ReadFromJsonAsync<TeamMatchLobbyDto>(JsonOptions))!;
        Assert.Equal("skilled",
            lobby.Seats.Single(s => s.Table == 2 && s.Seat == PlayerPosition.South).BotProfileId);

        using var cannotAccept = await this.Send(HttpMethod.Post,
            fx.Root + "/invitations/" + inviteId + "/accept", invited);
        Assert.Equal(HttpStatusCode.Forbidden, cannotAccept.StatusCode);

        // No human remains at table 2, so the hosted worker advances the
        // explicitly assigned bot without a private table action.
        using var overview = await this.Send(HttpMethod.Get, fx.Root + "/organizer", fx.Host);
        var publicState = await overview.Content.ReadFromJsonAsync<JsonElement>();
        var second = publicState.GetProperty("tables").EnumerateArray()
            .Single(t => t.GetProperty("table").GetInt32() == 2);
        Assert.False(second.GetProperty("isPaused").GetBoolean());
        Assert.True(second.GetProperty("roundNumber").GetInt32() > 1
            || second.GetProperty("bids").GetArrayLength() > 0);
        using var retry = await this.Send(HttpMethod.Post, fx.Root + "/game/bots/resume", fx.Host,
            new { table = 2 });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task ReplacingPlayerMidDealPreservesRemainingCardsAndAllowsNewControllerToAct()
    {
        var fx = await this.StartAsync();
        using var first = await this.Send(HttpMethod.Get, fx.Root + "/game", fx.Guest);
        var state = (await first.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Equal(1, state.State.RoundNumber);
        var guard = 0;

        // Actually execute the auction and at least one PLAYED CARD; this is
        // not just a pre-auction handover test.
        while ((state.State.Tricks.Sum(trick => trick.Cards.Count)
            + state.State.CurrentTrick.Count == 0 || state.State.Hand.Count >= 8)
            && guard++ < 20)
        {
            GameActionRequest move = state.State.Decision switch
            {
                "Bid" => new GameActionRequest
                {
                    Bid = state.State.Bids.Count == 0 ? "NoTrumps" : "Pass",
                },
                "Announce" => new GameActionRequest { Announces = Array.Empty<AnnounceRequest>() },
                "PlayCard" => new GameActionRequest { Card = state.State.PlayableCards[0] },
                _ => throw new InvalidOperationException("Unexpected decision: " + state.State.Decision),
            };
            using var played = await this.Send(HttpMethod.Post, fx.Root + "/game/actions", fx.Guest,
                new TeamMatchHumanActionRequest(state.Revision, "mid-deal-" + guard, move));
            Assert.Equal(HttpStatusCode.OK, played.StatusCode);
            state = (await played.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
            for (var wait = 0;
                 wait < 100
                 && !state.State.IsFinished
                 && state.State.ToMove != PlayerPosition.South.ToString();
                 wait++)
            {
                await Task.Delay(10);
                using var refreshed = await this.Send(
                    HttpMethod.Get, fx.Root + "/game", fx.Guest);
                state = (await refreshed.Content
                    .ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
            }
        }

        Assert.True(state.State.Hand.Count < 8, "A human should have played at least one card.");
        Assert.Equal(1, state.State.RoundNumber);
        var hand = state.State.Hand.ToArray();
        var bids = state.State.Bids.ToArray();
        var cardsAlreadyPlayed = state.State.Tricks.Sum(trick => trick.Cards.Count)
            + state.State.CurrentTrick.Count;
        Assert.True(cardsAlreadyPlayed >= 1);

        using var kicked = await this.Send(HttpMethod.Post, fx.Root + "/organizer/kick", fx.Host,
            new { table = 2, seat = PlayerPosition.South });
        Assert.Equal(HttpStatusCode.OK, kicked.StatusCode);

        var newUser = Guid.NewGuid().ToString();
        using var invitation = await this.Send(HttpMethod.Post,
            fx.Root + "/organizer/invite-replacement", fx.Host,
            new { table = 2, seat = PlayerPosition.South, userId = newUser });
        Assert.Equal(HttpStatusCode.OK, invitation.StatusCode);
        var invite = (await invitation.Content.ReadFromJsonAsync<TeamMatchReplacementInvitationDto>(JsonOptions))!;
        using var accepted = await this.Send(HttpMethod.Post,
            fx.Root + "/invitations/" + invite.InvitationId + "/accept", newUser);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var resumed = await this.Send(HttpMethod.Get, fx.Root + "/game", newUser);
        var restored = (await resumed.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.False(restored.IsPaused);
        Assert.True(restored.Revision > state.Revision);
        Assert.Equal(hand, restored.State.Hand);
        Assert.Equal(bids, restored.State.Bids);
        Assert.Equal(cardsAlreadyPlayed,
            restored.State.Tricks.Sum(trick => trick.Cards.Count)
                + restored.State.CurrentTrick.Count);
        Assert.Equal(1, restored.State.RoundNumber);

        using var old = await this.Send(HttpMethod.Post, fx.Root + "/game/actions", fx.Guest,
            new TeamMatchHumanActionRequest(state.Revision, "former-actor", new GameActionRequest { Bid = "Pass" }));
        Assert.Equal(HttpStatusCode.Forbidden, old.StatusCode);

        // The replacement can continue the current deal without a new deal.
        var next = restored.State.Decision switch
        {
            "Bid" => new GameActionRequest { Bid = "Pass" },
            "Announce" => new GameActionRequest { Announces = Array.Empty<AnnounceRequest>() },
            "PlayCard" => new GameActionRequest { Card = restored.State.PlayableCards[0] },
            _ => throw new InvalidOperationException("Unexpected decision after replacement"),
        };
        using var newAction = await this.Send(HttpMethod.Post, fx.Root + "/game/actions", newUser,
            new TeamMatchHumanActionRequest(restored.Revision, "replacement-resume", next));
        Assert.Equal(HttpStatusCode.OK, newAction.StatusCode);
    }

    [Fact]
    public async Task BotSeatCanBeInvitedAndTransferredOnlyOnAcceptance()
    {
        var fx = await this.StartAsync();
        var invited = Guid.NewGuid().ToString();
        using var inviteResponse = await this.Send(HttpMethod.Post,
            fx.Root + "/organizer/invite-replacement", fx.Host,
            new { table = 2, seat = PlayerPosition.East, userId = invited });
        Assert.Equal(HttpStatusCode.OK, inviteResponse.StatusCode);
        var invite = (await inviteResponse.Content.ReadFromJsonAsync<TeamMatchReplacementInvitationDto>(JsonOptions))!;
        using var before = await this.Send(HttpMethod.Get, fx.Root, fx.Guest);
        var oldRoom = (await before.Content.ReadFromJsonAsync<TeamMatchLobbyDto>(JsonOptions))!;
        Assert.Equal("skilled", oldRoom.Seats.Single(
            s => s.Table == 2 && s.Seat == PlayerPosition.East).BotProfileId);

        using var accepted = await this.Send(HttpMethod.Post,
            fx.Root + "/invitations/" + invite.InvitationId + "/accept", invited);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var ownView = await this.Send(HttpMethod.Get, fx.Root + "/game", invited);
        Assert.Equal(HttpStatusCode.OK, ownView.StatusCode);
        var snapshot = (await ownView.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Equal(2, snapshot.Table);
        Assert.Equal(PlayerPosition.East, snapshot.YourSeat);
        Assert.Equal(5, snapshot.State.Hand.Count);
        Assert.Equal(1, snapshot.State.RoundNumber);
        using var after = await this.Send(HttpMethod.Get, fx.Root, fx.Guest);
        var newRoom = (await after.Content.ReadFromJsonAsync<TeamMatchLobbyDto>(JsonOptions))!;
        Assert.Null(newRoom.Seats.Single(
            s => s.Table == 2 && s.Seat == PlayerPosition.East).BotProfileId);
    }

    [Fact]
    public async Task NonHostCannotControlOrObserveAndReportsAreMemberOnly()
    {
        var fx = await this.StartAsync();
        var intruder = Guid.NewGuid().ToString();
        using var nonHostKick = await this.Send(HttpMethod.Post, fx.Root + "/organizer/kick", fx.Guest,
            new { table = 1, seat = PlayerPosition.South });
        Assert.Equal(HttpStatusCode.Forbidden, nonHostKick.StatusCode);
        using var outsiderReport = await this.Send(HttpMethod.Post, fx.Root + "/reports", intruder,
            new { table = 2, seat = PlayerPosition.South, reason = "False report" });
        Assert.Equal(HttpStatusCode.Forbidden, outsiderReport.StatusCode);
        using var guestOverview = await this.Send(HttpMethod.Get, fx.Root + "/organizer", fx.Guest);
        Assert.Equal(HttpStatusCode.Forbidden, guestOverview.StatusCode);
        using var strangerGame = await this.Send(HttpMethod.Get, fx.Root + "/game", intruder);
        Assert.Equal(HttpStatusCode.Forbidden, strangerGame.StatusCode);
        using var anon = await this.client.GetAsync(fx.Root + "/organizer");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
    }

    [Fact]
    public async Task AcceptedHumanActionStaysCommittedWhenHostKicksConcurrentController()
    {
        var fx = await this.StartAsync();
        using var current = await this.Send(HttpMethod.Get, fx.Root + "/game", fx.Guest);
        var snapshot = (await current.Content.ReadFromJsonAsync<TeamMatchGameSnapshotDto>(JsonOptions))!;
        Assert.Equal(PlayerPosition.South, snapshot.YourSeat);
        var submitted = this.Send(HttpMethod.Post, fx.Root + "/game/actions", fx.Guest,
            new { expectedTableRevision = snapshot.Revision, commandId = "racing-command",
                action = new { bid = "Pass" } });
        var kicked = this.Send(HttpMethod.Post, fx.Root + "/organizer/kick", fx.Host,
            new { table = 2, seat = PlayerPosition.South });
        var responses = await Task.WhenAll(submitted, kicked);
        try
        {
            Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);
            Assert.Contains(responses[0].StatusCode,
                new[] { HttpStatusCode.OK, HttpStatusCode.Forbidden });
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        using var denied = await this.Send(HttpMethod.Get, fx.Root + "/game", fx.Guest);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var duplicateAfterKick = await this.Send(HttpMethod.Post,
            fx.Root + "/game/actions", fx.Guest,
            new { expectedTableRevision = snapshot.Revision, commandId = "racing-command",
                action = new { bid = "Pass" } });
        Assert.Equal(HttpStatusCode.Forbidden, duplicateAfterKick.StatusCode);
    }

    public void Dispose()
    {
        this.client.Dispose();
        this.factory.Dispose();
    }

    private async Task<(string Host, string Guest, string Root)> StartAsync(bool barometerEnabled = true)
    {
        var host = Guid.NewGuid().ToString();
        var guest = Guid.NewGuid().ToString();
        using var creation = await this.Send(HttpMethod.Post, "/api/team-matches", host,
            new { scheduledDeals = 4, barometerEnabled });
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var setup = (await creation.Content.ReadFromJsonAsync<CreateTeamMatchResponse>(JsonOptions))!;
        var root = "/api/team-matches/" + setup.Lobby.MatchId;
        using var join = await this.Send(HttpMethod.Post, root + "/join", guest,
            new { invitationCode = setup.InvitationCode, table = 2, seat = PlayerPosition.South });
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        for (var table = 1; table <= 2; table++)
        {
            foreach (var seat in new[] { PlayerPosition.East, PlayerPosition.North, PlayerPosition.West })
            {
                using var assign = await this.Send(HttpMethod.Post, root + "/bots", host,
                    new { table, seat, botProfileId = "skilled" });
                Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
            }
        }

        using var start = await this.Send(HttpMethod.Post, root + "/start", host);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        return (host, guest, root);
    }

    private async Task<HttpResponseMessage> Send(
        HttpMethod method, string path, string user, object? body = null)
    {
        using var request = ApiFactory.CreateAuthenticatedRequest(
            method, path, ApiFactory.CreateAccessToken(user));
        if (body != null) request.Content = JsonContent.Create(body, options: JsonOptions);
        return await this.client.SendAsync(request);
    }
}
