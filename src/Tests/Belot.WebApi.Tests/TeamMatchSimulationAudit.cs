namespace Belot.WebApi.Tests;

using System.Text;

internal sealed record TeamMatchSimulationAudit(
    TeamMatchSimulationScenario Scenario,
    TimeSpan Duration,
    int ActualDeals,
    bool ExtendedForHanging,
    TeamMatchActionCounters Counters,
    IReadOnlyList<TeamMatchDealAudit> Deals)
{
    public string Format()
    {
        var text = new StringBuilder()
            .AppendLine(
                $"A10.6 {this.Scenario.Name}: PASS; deals={this.ActualDeals}; "
                + $"scheduled={this.Scenario.ScheduledDeals}; "
                + $"extended={this.ExtendedForHanging}; "
                + $"duration={this.Duration.TotalMilliseconds:F1}ms; "
                + $"bids={this.Counters.Bids}; announcements={this.Counters.AnnouncementActions}; "
                + $"declared={this.Counters.Declarations}; cards={this.Counters.Cards}; "
                + $"belotes={this.Counters.Belotes}");
        text.AppendLine(
            "deal | T1 SN/EW | T2 SN/EW | TT | cumulative TT | released | hanging | status");
        foreach (var deal in this.Deals)
        {
            text.AppendLine(
                $"{deal.DealNumber,4} | {deal.Table1SouthNorth,3}/{deal.Table1EastWest,3} "
                + $"| {deal.Table2SouthNorth,3}/{deal.Table2EastWest,3} "
                + $"| {deal.ExpectedSignedTt,4} | {deal.CumulativeSignedTt,4} "
                + $"| {(deal.ComparisonReleased ? "yes" : "no"),8} "
                + $"| {deal.Table1Hanging,2}/{deal.Table2Hanging,2} | PASS");
        }

        return text.ToString();
    }
}
