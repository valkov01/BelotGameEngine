namespace Belot.WebApi.Tests;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Belot.Engine.Cards;
using Belot.Engine.Players;

using Xunit;
using Xunit.Abstractions;

internal sealed class TeamMatchHttpSimulator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly IReadOnlyList<(int Table, PlayerPosition Seat)> AllSeats =
        (from table in new[] { 1, 2 }
         from seat in new[]
         {
             PlayerPosition.South, PlayerPosition.East,
             PlayerPosition.North, PlayerPosition.West,
         }
         select (table, seat)).ToArray();

    private readonly HttpClient client;

    private readonly ITestOutputHelper output;

    public TeamMatchHttpSimulator(HttpClient client, ITestOutputHelper output)
    {
        this.client = client;
        this.output = output;
    }

    public async Task<TeamMatchSimulationAudit> RunAsync(TeamMatchSimulationScenario scenario)
    {
        var stopwatch = Stopwatch.StartNew();
        var setup = await this.CreateAndStartAsync(scenario);
        var counters = new TeamMatchActionCounters();
        var expectedRows = new List<ExpectedComparedDeal>();
        var releaseRows = new List<TeamMatchDealAudit>();
        var completedAtLeastOnce = false;
        var safety = 0;

        while (!setup.Snapshots.Values.All(snapshot => snapshot.State.IsFinished))
        {
            var dealNumber = expectedRows.Count + 1;
            Assert.True(++safety <= 64, $"Scenario {scenario.Name} exceeded its deal bound.");

            var firstTable = scenario.TableFinishesFirst(1, dealNumber) ? 1 : 2;
            var secondTable = firstTable == 1 ? 2 : 1;
            var firstToPlay = await this.RefreshFirstToPlayAsync(setup, dealNumber);
            Assert.Equal(firstToPlay[1], firstToPlay[2]);

            await this.DriveTableOneDealAsync(setup, firstTable, dealNumber, counters);
            await this.AssertOneTableAheadAsync(
                setup, firstTable, secondTable, dealNumber, expectedRows);

            await this.DriveTableOneDealAsync(setup, secondTable, dealNumber, counters);
            setup.Snapshots = await this.RefreshSnapshotsAsync(setup);
            var table1 = this.Round(setup, 1, dealNumber);
            var table2 = this.Round(setup, 2, dealNumber);
            Assert.Equal(table1.RoundNumber, table2.RoundNumber);
            Assert.Equal(dealNumber, table1.RoundNumber);

            this.AssertIdenticalBoard(table1, table2, dealNumber);
            var expected = ExpectedComparedDeal.Create(
                dealNumber,
                table1,
                table2,
                expectedRows.Count == 0 ? null : expectedRows[^1]);
            expectedRows.Add(expected);

            var priorRows = expectedRows.Take(expectedRows.Count - 1).ToArray();
            using var paired = await this.SendAsync(
                HttpMethod.Get,
                setup.Root + "/results",
                setup.Host);
            var results = await this.ReadRequiredAsync<TeamMatchResultsView>(paired);
            var matchComplete = setup.Snapshots.Values.All(snapshot => snapshot.State.IsFinished);
            var released = this.AssertResultVisibility(
                scenario, results, priorRows, expected, matchComplete);
            releaseRows.Add(new TeamMatchDealAudit(
                dealNumber,
                table1.SouthNorthPoints,
                table1.EastWestPoints,
                table2.SouthNorthPoints,
                table2.EastWestPoints,
                expected.Team1SignedDeal,
                expected.Team1CumulativeMargin,
                released,
                table1.HangingPoints,
                table2.HangingPoints));

            completedAtLeastOnce = completedAtLeastOnce || matchComplete;
        }

        Assert.True(completedAtLeastOnce);
        await this.AssertFinalStateAsync(setup, expectedRows, counters);
        stopwatch.Stop();
        var audit = new TeamMatchSimulationAudit(
            scenario,
            stopwatch.Elapsed,
            expectedRows.Count,
            expectedRows.Count > scenario.ScheduledDeals,
            counters,
            releaseRows);
        this.output.WriteLine(audit.Format());
        return audit;
    }

    public async Task AssertUnsupportedAllBotTableAsync(TeamMatchSimulationScenario scenario)
    {
        Assert.Equal(1, scenario.HumanCount);
        var setup = await this.CreateLobbyAsync(scenario);
        foreach (var seat in AllSeats.Except(
                     new[] { (scenario.HumanSeats()[0].Table, scenario.HumanSeats()[0].Seat) }))
        {
            using var bot = await this.SendAsync(
                HttpMethod.Post,
                setup.Root + "/bots",
                setup.Host,
                new { table = seat.Table, seat = seat.Seat, botProfileId = "skilled" });
            this.AssertSuccess(bot);
        }

        using var start = await this.SendAsync(HttpMethod.Post, setup.Root + "/start", setup.Host);
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        var message = await start.Content.ReadAsStringAsync();
        Assert.Contains("human is required at each table", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("autonomous all-bot tables", message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<SimulationSetup> CreateAndStartAsync(
        TeamMatchSimulationScenario scenario)
    {
        var setup = await this.CreateLobbyAsync(scenario);
        var humanSeats = scenario.HumanSeats();
        Assert.Equal(scenario.HumanCount, humanSeats.Count);
        Assert.All(new[] { 1, 2 }, table => Assert.Contains(humanSeats, human => human.Table == table));

        var participants = new List<TeamMatchParticipant>
        {
            new(setup.Host, humanSeats[0].Table, humanSeats[0].Seat),
        };
        for (var index = 1; index < humanSeats.Count; index++)
        {
            var seat = humanSeats[index];
            var user = $"{setup.Host}-human-{index}";
            using var join = await this.SendAsync(
                HttpMethod.Post,
                setup.Root + "/join",
                user,
                new
                {
                    invitationCode = setup.InvitationCode,
                    table = seat.Table,
                    seat = seat.Seat,
                });
            this.AssertSuccess(join);
            var joined = await this.ReadRequiredAsync<TeamMatchLobbyDto>(join);
            Assert.Equal(seat.Table, joined.YourSeat.Table);
            Assert.Equal(seat.Seat, joined.YourSeat.Seat);
            participants.Add(new TeamMatchParticipant(user, seat.Table, seat.Seat));
        }

        foreach (var seat in AllSeats.Except(
                     humanSeats.Select(human => (human.Table, human.Seat))))
        {
            using var bot = await this.SendAsync(
                HttpMethod.Post,
                setup.Root + "/bots",
                setup.Host,
                new { table = seat.Table, seat = seat.Seat, botProfileId = "skilled" });
            this.AssertSuccess(bot);
        }

        using var start = await this.SendAsync(HttpMethod.Post, setup.Root + "/start", setup.Host);
        this.AssertSuccess(start);
        var lobby = await this.ReadRequiredAsync<TeamMatchLobbyDto>(start);
        Assert.True(lobby.IsStarted);
        Assert.Equal(8, lobby.Seats.Count(seat => seat.IsOccupied));
        Assert.Equal(8 - scenario.HumanCount, lobby.Seats.Count(seat => seat.BotProfileId != null));
        Assert.Equal(scenario.HumanCount, lobby.Seats.Count(seat => seat.BotProfileId == null));
        setup.Participants = participants;
        setup.Scenario = scenario;
        setup.Snapshots = await this.RefreshSnapshotsAsync(setup);
        await this.AssertPrivateLobbySurfaceAsync(setup);
        return setup;
    }

    private async Task<SimulationSetup> CreateLobbyAsync(TeamMatchSimulationScenario scenario)
    {
        var host = $"a10-6-{scenario.Name}-{Guid.NewGuid():N}";
        using var created = await this.SendAsync(
            HttpMethod.Post,
            "/api/team-matches",
            host,
            new
            {
                name = "A10.6 " + scenario.Name,
                scheduledDeals = scenario.ScheduledDeals,
                barometerEnabled = scenario.BarometerEnabled,
                organizerTable = 1,
                organizerSeat = PlayerPosition.South,
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var response = await this.ReadRequiredAsync<CreateTeamMatchResponse>(created);
        Assert.Equal(scenario.ScheduledDeals, response.Lobby.ScheduledDeals);
        Assert.Equal(scenario.BarometerEnabled, response.Lobby.BarometerEnabled);
        Assert.False(string.IsNullOrWhiteSpace(response.InvitationCode));
        Assert.Equal(1, response.Lobby.YourSeat.Table);
        Assert.Equal(PlayerPosition.South, response.Lobby.YourSeat.Seat);
        return new SimulationSetup(
            host,
            response.InvitationCode,
            "/api/team-matches/" + response.Lobby.MatchId);
    }

    private async Task DriveTableOneDealAsync(
        SimulationSetup setup,
        int table,
        int dealNumber,
        TeamMatchActionCounters counters)
    {
        var guard = 0;
        while (true)
        {
            Assert.True(++guard <= 500, $"Table {table} did not finish deal {dealNumber}.");
            var snapshots = await this.RefreshSnapshotsAsync(setup, table);
            var current = snapshots.SingleOrDefault(
                participant => participant.Value.Table == table
                    && !participant.Value.State.IsFinished
                    && participant.Value.State.ToMove == participant.Value.YourSeat.ToString());
            if (current.Value?.State.PreviousRounds.Count >= dealNumber
                || snapshots.All(participant => participant.Value.State.IsFinished
                    || participant.Value.State.PreviousRounds.Count >= dealNumber))
            {
                return;
            }

            if (current.Key == null || current.Value == null)
            {
                throw new InvalidOperationException(
                    $"No authenticated human can advance table {table}.");
            }

            var action = this.ChooseAction(current.Value, dealNumber, counters);
            var request = new TeamMatchHumanActionRequest(
                current.Value.Revision,
                $"a10-6-{setup.MatchKey}-d{dealNumber}-t{table}-m{guard}",
                action.Request);
            using var response = await this.SendAsync(
                HttpMethod.Post,
                setup.Root + "/game/actions",
                current.Key.UserId,
                request);
            this.AssertSuccess(response);
            var after = await this.ReadRequiredAsync<TeamMatchGameSnapshotDto>(response);
            Assert.Equal(current.Value.Table, after.Table);
            Assert.False(after.IsPaused);
            Assert.True(after.Revision >= current.Value.Revision);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }

    private ChosenAction ChooseAction(
        TeamMatchGameSnapshotDto snapshot,
        int dealNumber,
        TeamMatchActionCounters counters)
    {
        var state = snapshot.State;
        switch (state.Decision)
        {
            case "Bid":
                var bid = state.Contract.Type == "Pass" ? "AllTrumps" : "Pass";
                Assert.Contains(bid, bid == "Pass" ? new[] { "Pass" } : state.AvailableBids);
                counters.Bids++;
                return new ChosenAction(new GameActionRequest { Bid = bid });
            case "Announce":
                var announcements = state.AvailableAnnounces
                    .Select(announce => new AnnounceRequest
                    {
                        Type = announce.Type,
                        Card = announce.Card,
                    })
                    .ToArray();
                counters.Declarations += announcements.Length;
                counters.AnnouncementActions++;
                return new ChosenAction(new GameActionRequest { Announces = announcements });
            case "PlayCard":
                var card = Assert.Single(state.PlayableCards.Take(1));
                var belote = state.BeloteCards.Contains(card);
                counters.Cards++;
                counters.Belotes += belote ? 1 : 0;
                return new ChosenAction(
                    new GameActionRequest { Card = card, Belote = belote });
            default:
                throw new InvalidOperationException(
                    $"Unexpected {state.Decision} at table {snapshot.Table}, deal {dealNumber}.");
        }
    }

    private async Task AssertOneTableAheadAsync(
        SimulationSetup setup,
        int firstTable,
        int secondTable,
        int dealNumber,
        IReadOnlyList<ExpectedComparedDeal> existing)
    {
        setup.Snapshots = await this.RefreshSnapshotsAsync(setup);
        Assert.True(setup.Snapshots.Values
            .Where(snapshot => snapshot.Table == firstTable)
            .All(snapshot => snapshot.State.PreviousRounds.Count >= dealNumber));
        Assert.True(setup.Snapshots.Values
            .Where(snapshot => snapshot.Table == secondTable)
            .All(snapshot => snapshot.State.PreviousRounds.Count == dealNumber - 1));

        using var response = await this.SendAsync(
            HttpMethod.Get,
            setup.Root + "/results",
            setup.Host);
        var results = await this.ReadRequiredAsync<TeamMatchResultsView>(response);
        if (setup.Scenario.BarometerEnabled)
        {
            this.AssertRows(results.ComparedDeals, existing);
            Assert.False(results.IsMatchComplete);
        }
        else
        {
            Assert.Null(results.Team1TotalPoints);
            Assert.Empty(results.ComparedDeals);
        }
    }

    private async Task AssertFinalStateAsync(
        SimulationSetup setup,
        IReadOnlyList<ExpectedComparedDeal> expected,
        TeamMatchActionCounters counters)
    {
        setup.Snapshots = await this.RefreshSnapshotsAsync(setup);
        Assert.All(setup.Snapshots.Values, snapshot =>
            Assert.True(snapshot.State.IsFinished));
        Assert.All(setup.Snapshots.Values, snapshot =>
            Assert.Equal(expected.Count, snapshot.State.PreviousRounds.Count));
        Assert.True(counters.Bids > 0);
        Assert.True(counters.Cards > 0);

        using var response = await this.SendAsync(
            HttpMethod.Get,
            setup.Root + "/results",
            setup.Host);
        var final = await this.ReadRequiredAsync<TeamMatchResultsView>(response);
        Assert.True(final.IsMatchComplete);
        this.AssertRows(final.ComparedDeals, expected);
        Assert.Equal(expected.Sum(row => row.Team1DealPoints), final.Team1TotalPoints);
        Assert.Equal(expected.Sum(row => row.Team2DealPoints), final.Team2TotalPoints);
        Assert.Equal(
            expected.Sum(row => row.Team1SignedDeal),
            final.Team1Margin);

        using var organizer = await this.SendAsync(
            HttpMethod.Get,
            setup.Root + "/organizer",
            setup.Host);
        var organizerJson = await organizer.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"hand\"", organizerJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"playableCards\"", organizerJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"availableBids\"", organizerJson, StringComparison.OrdinalIgnoreCase);
        var overview = this.ReadRequiredJson<JsonElement>(organizerJson);
        Assert.True(overview.GetProperty("tables").EnumerateArray().All(
            table => table.GetProperty("isFinished").GetBoolean()));
        Assert.Equal(0, overview.GetProperty("tables").EnumerateArray()
            .Count(table => table.GetProperty("isPaused").GetBoolean()));
    }

    private async Task AssertPrivateLobbySurfaceAsync(SimulationSetup setup)
    {
        foreach (var participant in setup.Participants)
        {
            using var response = await this.SendAsync(
                HttpMethod.Get,
                setup.Root,
                participant.UserId);
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("\"userId\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(setup.InvitationCode, json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"hand\"", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    private bool AssertResultVisibility(
        TeamMatchSimulationScenario scenario,
        TeamMatchResultsView actual,
        IReadOnlyList<ExpectedComparedDeal> previous,
        ExpectedComparedDeal expected,
        bool matchComplete)
    {
        Assert.Equal(scenario.BarometerEnabled, actual.BarometerEnabled);
        Assert.Equal(scenario.ScheduledDeals, actual.ScheduledDealCount);
        Assert.Equal(matchComplete, actual.IsMatchComplete);
        if (!scenario.BarometerEnabled && !matchComplete)
        {
            Assert.Null(actual.Team1TotalPoints);
            Assert.Null(actual.Team2TotalPoints);
            Assert.Null(actual.Team1Margin);
            Assert.Empty(actual.ComparedDeals);
            return false;
        }

        this.AssertRows(actual.ComparedDeals, previous.Append(expected).ToArray());
        Assert.Equal(expected.Team1CumulativePoints, actual.Team1TotalPoints);
        Assert.Equal(expected.Team2CumulativePoints, actual.Team2TotalPoints);
        Assert.Equal(expected.Team1CumulativeMargin, actual.Team1Margin);
        return true;
    }

    private async Task<Dictionary<TeamMatchParticipant, TeamMatchGameSnapshotDto>>
        RefreshSnapshotsAsync(SimulationSetup setup, int? table = null)
    {
        var snapshots = new Dictionary<TeamMatchParticipant, TeamMatchGameSnapshotDto>();
        foreach (var participant in setup.Participants.Where(
                     participant => !table.HasValue || participant.Table == table))
        {
            using var response = await this.SendAsync(
                HttpMethod.Get,
                setup.Root + "/game",
                participant.UserId);
            var snapshot = await this.ReadRequiredAsync<TeamMatchGameSnapshotDto>(response);
            Assert.Equal(participant.Table, snapshot.Table);
            Assert.Equal(participant.Seat, snapshot.YourSeat);
            Assert.Equal(setup.Root["/api/team-matches/".Length..], snapshot.MatchId);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.True(snapshot.State.Hand.Count <= 8);
            Assert.All(snapshot.State.Hand, card =>
            {
                Assert.True(CardDto.TryParse(card, out var parsed));
                Assert.NotNull(parsed);
            });
            snapshots.Add(participant, snapshot);
        }

        return snapshots;
    }

    private async Task<Dictionary<int, string>> RefreshFirstToPlayAsync(
        SimulationSetup setup,
        int dealNumber)
    {
        setup.Snapshots = await this.RefreshSnapshotsAsync(setup);
        var result = new Dictionary<int, string>();
        foreach (var table in new[] { 1, 2 })
        {
            var snapshots = setup.Snapshots.Values.Where(snapshot => snapshot.Table == table).ToArray();
            Assert.All(snapshots, snapshot => Assert.Equal(dealNumber, snapshot.State.RoundNumber));
            result[table] = Assert.Single(snapshots.Select(snapshot => snapshot.State.FirstToPlay).Distinct());
        }

        return result;
    }

    private RoundSummaryDto Round(SimulationSetup setup, int table, int dealNumber)
    {
        var rounds = setup.Snapshots.Values
            .Where(snapshot => snapshot.Table == table)
            .Select(snapshot => snapshot.State.PreviousRounds[dealNumber - 1])
            .ToArray();
        var expected = JsonSerializer.Serialize(rounds[0], JsonOptions);
        Assert.All(rounds, round =>
            Assert.Equal(expected, JsonSerializer.Serialize(round, JsonOptions)));
        return rounds[0];
    }

    private void AssertIdenticalBoard(
        RoundSummaryDto table1,
        RoundSummaryDto table2,
        int dealNumber)
    {
        Assert.Equal(8, table1.Tricks.Count);
        Assert.Equal(8, table2.Tricks.Count);
        var hands1 = this.HandsBySeat(table1);
        var hands2 = this.HandsBySeat(table2);
        foreach (var seat in Enum.GetValues<PlayerPosition>().Where(position =>
                     position is PlayerPosition.South or PlayerPosition.East
                         or PlayerPosition.North or PlayerPosition.West))
        {
            Assert.True(
                hands1[seat].SetEquals(hands2[seat]),
                $"Deal {dealNumber} has different {seat} cards at the two tables.");
            Assert.Equal(8, hands1[seat].Count);
        }

        Assert.Equal(32, hands1.Values.Sum(cards => cards.Count));
    }

    private Dictionary<PlayerPosition, HashSet<string>> HandsBySeat(
        RoundSummaryDto round)
    {
        var result = new Dictionary<PlayerPosition, HashSet<string>>();
        foreach (var seat in Enum.GetValues<PlayerPosition>().Where(position =>
                     position is PlayerPosition.South or PlayerPosition.East
                         or PlayerPosition.North or PlayerPosition.West))
        {
            result[seat] = round.Tricks
                .SelectMany(trick => trick.Cards)
                .Where(card => Enum.Parse<PlayerPosition>(card.Seat) == seat)
                .Select(card => card.Card.Suit + ":" + card.Card.Type)
                .ToHashSet(StringComparer.Ordinal);
        }

        return result;
    }

    private void AssertRows(
        IReadOnlyList<TeamMatchComparedDeal> actual,
        IReadOnlyList<ExpectedComparedDeal> expected)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var oracle = expected[index];
            var row = actual[index];
            Assert.Equal(oracle.DealNumber, row.DealNumber);
            Assert.Equal(oracle.Table1SouthNorth, row.Table1SouthNorth);
            Assert.Equal(oracle.Table1EastWest, row.Table1EastWest);
            Assert.Equal(oracle.Table2SouthNorth, row.Table2SouthNorth);
            Assert.Equal(oracle.Table2EastWest, row.Table2EastWest);
            Assert.Equal(oracle.Team1DealPoints, row.Team1DealPoints);
            Assert.Equal(oracle.Team2DealPoints, row.Team2DealPoints);
            Assert.Equal(oracle.Team1CumulativePoints, row.Team1CumulativePoints);
            Assert.Equal(oracle.Team2CumulativePoints, row.Team2CumulativePoints);
            Assert.Equal(oracle.Team1CumulativeMargin, row.Team1CumulativeMargin);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string userId,
        object? body = null)
    {
        using var request = ApiFactory.CreateAuthenticatedRequest(
            method,
            path,
            ApiFactory.CreateAccessToken(userId));
        if (body != null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        var response = await this.client.SendAsync(request);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        return response;
    }

    private async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response)
    {
        this.AssertSuccess(response);
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private T ReadRequiredJson<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)!;

    private void AssertSuccess(HttpResponseMessage response)
    {
        var details = $"{(int)response.StatusCode} {response.ReasonPhrase}: "
            + response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.True(response.IsSuccessStatusCode, details);
    }

    private sealed record SimulationSetup(
        string Host,
        string InvitationCode,
        string Root)
    {
        public string MatchKey { get; } = Root[(Root.LastIndexOf('/') + 1)..];

        public IReadOnlyList<TeamMatchParticipant> Participants { get; set; } =
            Array.Empty<TeamMatchParticipant>();

        public Dictionary<TeamMatchParticipant, TeamMatchGameSnapshotDto> Snapshots { get; set; } =
            new();

        public TeamMatchSimulationScenario Scenario { get; set; } = null!;
    }

    private sealed record TeamMatchParticipant(
        string UserId,
        int Table,
        PlayerPosition Seat);

    private sealed record ChosenAction(GameActionRequest Request);
}
