namespace Belot.WebApi.Tests;

internal sealed record ExpectedComparedDeal(
    int DealNumber,
    int Table1SouthNorth,
    int Table1EastWest,
    int Table2SouthNorth,
    int Table2EastWest,
    int Team1DealPoints,
    int Team2DealPoints,
    int Team1CumulativePoints,
    int Team2CumulativePoints,
    int Team1CumulativeMargin)
{
    public int Team1SignedDeal => this.Team1DealPoints - this.Team2DealPoints;

    public static ExpectedComparedDeal Create(
        int dealNumber,
        RoundSummaryDto table1,
        RoundSummaryDto table2,
        ExpectedComparedDeal? previous = null)
    {
        var team1 = checked(table1.SouthNorthPoints + table2.EastWestPoints);
        var team2 = checked(table1.EastWestPoints + table2.SouthNorthPoints);
        var cumulative1 = (previous?.Team1CumulativePoints ?? 0) + team1;
        var cumulative2 = (previous?.Team2CumulativePoints ?? 0) + team2;
        return new ExpectedComparedDeal(
            dealNumber,
            table1.SouthNorthPoints,
            table1.EastWestPoints,
            table2.SouthNorthPoints,
            table2.EastWestPoints,
            team1,
            team2,
            cumulative1,
            cumulative2,
            cumulative1 - cumulative2);
    }
}
