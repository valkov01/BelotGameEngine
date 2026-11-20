namespace Belot.WebApi.Tests;

using Belot.Engine.Game;
using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

using Microsoft.Extensions.DependencyInjection;

using Xunit;
using Xunit.Abstractions;

[Collection(TeamMatchSimulationCollection.Name)]
public sealed class TeamMatchAutomatedSimulationTests
{
    private readonly ITestOutputHelper output;

    private readonly TeamMatchHttpSimulator simulator;

    public TeamMatchAutomatedSimulationTests(
        TeamMatchSimulationFixture fixture,
        ITestOutputHelper output)
    {
        this.output = output;
        this.simulator = new TeamMatchHttpSimulator(
            fixture.Client,
            output,
            fixture.GetCoordinator);
    }

    [Theory]
    [Trait("Category", "TeamMatchSmoke")]
    [MemberData(
        nameof(TeamMatchSimulationScenario.Smoke),
        MemberType = typeof(TeamMatchSimulationScenario))]
    public async Task SmokeCoversEverySupportedHumanBotDistribution(
        TeamMatchSimulationScenario scenario)
    {
        Assert.Equal(4, scenario.ScheduledDeals);
        var audit = await this.simulator.RunAsync(scenario);
        Assert.True(audit.ActualDeals >= scenario.ScheduledDeals);
    }

    [Theory]
    [Trait("Category", "TeamMatchBoundary")]
    [MemberData(
        nameof(TeamMatchSimulationScenario.Boundary),
        MemberType = typeof(TeamMatchSimulationScenario))]
    public async Task BoundaryCoversTwentyDealMatchesForEveryDistribution(
        TeamMatchSimulationScenario scenario)
    {
        Assert.Equal(20, scenario.ScheduledDeals);
        var audit = await this.simulator.RunAsync(scenario);
        Assert.True(audit.ActualDeals >= scenario.ScheduledDeals);
    }

    [Theory]
    [Trait("Category", "TeamMatchMatrix")]
    [MemberData(
        nameof(TeamMatchSimulationScenario.Extended),
        MemberType = typeof(TeamMatchSimulationScenario))]
    public async Task MatrixCoversEveryRemainingSupportedDealCount(
        TeamMatchSimulationScenario scenario)
    {
        var audit = await this.simulator.RunAsync(scenario);
        Assert.True(audit.ActualDeals >= scenario.ScheduledDeals);
    }

    [Theory]
    [Trait("Category", "TeamMatchAutonomous")]
    [MemberData(
        nameof(TeamMatchSimulationScenario.AutonomousAllBotTables),
        MemberType = typeof(TeamMatchSimulationScenario))]
    public async Task OneHumanCompletesAutonomousFourBotTableAtEveryDealCount(
        TeamMatchSimulationScenario scenario)
    {
        var audit = await this.simulator.RunAsync(scenario);
        Assert.True(audit.ActualDeals >= scenario.ScheduledDeals);
    }

    [Fact]
    [Trait("Category", "TeamMatchFast")]
    public async Task OneHumanFourDealAutonomousMatchIsFastSmokeCandidate()
    {
        var scenario = TeamMatchSimulationScenario.AutonomousAllBotTables
            .Select(row => Assert.IsType<TeamMatchSimulationScenario>(Assert.Single(row)))
            .Single(candidate => candidate.ScheduledDeals == 4);
        var audit = await this.simulator.RunAsync(scenario);
        Assert.True(audit.ActualDeals >= scenario.ScheduledDeals);
    }

    [Fact]
    [Trait("Category", "TeamMatchHanging")]
    public async Task BarometerModesAuditRealHangingExtensionAndDeclarations()
    {
        var seed = FindHangingAndDeclarationSeed();
        foreach (var barometer in new[] { false, true })
        {
            using var factory = new ApiFactory(teamMatchDeckSeed: seed);
            using var client = factory.CreateClient();
            var simulator = new TeamMatchHttpSimulator(
                client,
                this.output,
                matchId => factory.Services
                    .GetRequiredService<TeamMatchLobbyService>()
                    .GetCoordinatorForAudit(matchId));
            var audit = await simulator.RunAsync(
                new TeamMatchSimulationScenario(8, 4, barometer ? 1 : 0, barometer, seed));
            Assert.True(audit.ExtendedForHanging);
            Assert.True(audit.Deals[3].Table1Hanging > 0
                || audit.Deals[3].Table2Hanging > 0);
            Assert.True(audit.Counters.Declarations > 0);
            Assert.True(audit.Deals[^1].Table1Hanging == 0
                && audit.Deals[^1].Table2Hanging == 0);
        }
    }

    [Fact]
    public void CoverageCatalogContainsOneHundredThirtySixSupportedCases()
    {
        var supported = TeamMatchSimulationScenario.All;
        Assert.Equal(119, supported.Count);
        Assert.Equal(119, supported.Select(
            scenario => (scenario.HumanCount, scenario.ScheduledDeals)).Distinct().Count());
        Assert.Equal(7, supported.Select(scenario => scenario.HumanCount).Distinct().Count());
        Assert.Equal(17, supported.Select(scenario => scenario.ScheduledDeals).Distinct().Count());
        Assert.All(supported, scenario =>
        {
            Assert.InRange(scenario.HumanCount, 2, 8);
            Assert.InRange(scenario.ScheduledDeals, 4, 20);
            Assert.All(new[] { 1, 2 }, table =>
                Assert.Contains(
                    scenario.HumanSeats(),
                    seat => seat.Table == table));
            Assert.Equal(
                scenario.HumanCount,
                scenario.HumanSeats().Distinct().Count());
        });

        var autonomous = TeamMatchSimulationScenario.AutonomousAllBotTables
            .Select(row => Assert.IsType<TeamMatchSimulationScenario>(Assert.Single(row)))
            .ToArray();
        Assert.Equal(17, autonomous.Length);
        Assert.All(autonomous, scenario => Assert.Equal(1, scenario.HumanCount));
        Assert.Equal(136, supported.Count + autonomous.Length);
    }

    [Theory]
    [MemberData(
        nameof(TeamMatchScoringFixtures.Independent),
        MemberType = typeof(TeamMatchScoringFixtures))]
    public void IndependentFixturesReviewTeamMappingAndSignedCumulativeTt(
        TeamMatchScoringFixtures.TeamMatchScoringFixture fixture)
    {
        var deals = fixture.Deals.Select((scores, index) =>
        {
            var table1 = scores.Table1;
            var table2 = scores.Table2;
            var team1 = table1.SouthNorth + table2.EastWest;
            var team2 = table1.EastWest + table2.SouthNorth;
            return new IndependentScoringResult(
                index + 1,
                team1,
                team2,
                team1 - team2);
        }).ToArray();
        Assert.Equal(fixture.ExpectedSignedDeals, deals.Select(deal => deal.Signed));
        Assert.Equal(fixture.ExpectedFinalMargin, deals.Sum(deal => deal.Signed));
    }

    private static int FindHangingAndDeclarationSeed()
    {
        for (var seed = 0; seed < 10_000; seed++)
        {
            var game = new TeamMatchCoreCoordinator(4, seed, barometerEnabled: false);
            var declarations = 0;
            for (var deal = 1; deal <= 4; deal++)
            {
                PlayDeal(game, 1, deal, ref declarations);
                PlayDeal(game, 2, deal, ref declarations);
            }

            if (game.AllowedDeals > 4 && declarations > 0)
            {
                return seed;
            }
        }

        throw new InvalidOperationException("No deterministic hanging/declaration seed found.");
    }

    private static void PlayDeal(
        TeamMatchCoreCoordinator game,
        int table,
        int dealNumber,
        ref int declarations)
    {
        var guard = 0;
        while (!game.IsFinished && game.GetRoundsPlayed(table) < dealNumber)
        {
            Assert.True(++guard < 200);
            var publicView = game.GetSeatView(table, PlayerPosition.South);
            var view = game.GetSeatView(table, publicView.ToMove);
            BelotAction action = view.Decision switch
            {
                BelotDecision.Bid => BelotAction.Bid(
                    view.Contract.Type == BidType.Pass ? BidType.AllTrumps : BidType.Pass),
                BelotDecision.Announce => DeclareAll(view, ref declarations),
                BelotDecision.PlayCard => BelotAction.PlayCard(view.PlayableCards[0]),
                _ => throw new InvalidOperationException(),
            };
            Assert.Equal(BelotActResult.Ok, game.Act(table, publicView.ToMove, action));
        }
    }

    private static BelotAction DeclareAll(BelotSeatView view, ref int declarations)
    {
        var announces = view.AvailableAnnounces
            .Select(announce => new Announce(announce.Type, announce.Card))
            .ToArray();
        declarations += announces.Length;
        return BelotAction.Declare(announces);
    }

    private sealed record IndependentScoringResult(
        int DealNumber,
        int Team1Points,
        int Team2Points,
        int Signed);
}
