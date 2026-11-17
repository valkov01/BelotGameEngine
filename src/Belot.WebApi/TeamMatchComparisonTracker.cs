namespace Belot.WebApi;

/// <summary>
/// A completed table's scored deal. Uses the C# engine's rounded game-point units,
/// not unrounded trick/announce values. No card, bid or private-hand information.
/// </summary>
internal sealed record TeamMatchTableDealPoints(int SouthNorth, int EastWest);

/// <summary>
/// One fully paired numbered deal. Table 1: Team 1 = NS, Team 2 = EW.
/// Table 2: Team 1 = EW, Team 2 = NS.
/// </summary>
internal sealed record TeamMatchComparedDeal(
    int DealNumber,
    int Table1SouthNorth,
    int Table1EastWest,
    int Table2SouthNorth,
    int Table2EastWest,
    int Team1DealPoints,
    int Team2DealPoints,
    int Team1CumulativePoints,
    int Team2CumulativePoints,
    int Team1CumulativeMargin);

/// <summary>
/// Safe cross-table comparison projection for an authenticated match member.
/// The future HTTP/SignalR adapter MUST still authorize membership before returning it.
/// Barometer OFF reveals no comparative points or deal rows before TRUE completion.
/// No winner is inferred from equal scores until the Product Owner approves tie UX.
/// </summary>
internal sealed record TeamMatchResultsView(
    bool BarometerEnabled,
    bool IsMatchComplete,
    int ScheduledDealCount,
    int? Team1TotalPoints,
    int? Team2TotalPoints,
    int? Team1Margin,
    IReadOnlyList<TeamMatchComparedDeal> ComparedDeals);

/// <summary>
/// Authoritative, append-only paired result accumulator. Each board is added once
/// after BOTH independent C# tables finish it, never on an unpaired result.
/// </summary>
internal sealed class TeamMatchComparisonTracker
{
    private readonly List<TeamMatchComparedDeal> pairedDeals = new();

    public int PairedDealCount => this.pairedDeals.Count;

    public void RecordPair(
        int dealNumber,
        TeamMatchTableDealPoints table1,
        TeamMatchTableDealPoints table2)
    {
        if (dealNumber != this.pairedDeals.Count + 1)
        {
            throw new InvalidOperationException("Paired deal results must be added once, in numbered order.");
        }

        ArgumentNullException.ThrowIfNull(table1);
        ArgumentNullException.ThrowIfNull(table2);
        if (table1.SouthNorth < 0 || table1.EastWest < 0
            || table2.SouthNorth < 0 || table2.EastWest < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(table1), "Belot point totals cannot be negative.");
        }

        checked
        {
            var team1Deal = table1.SouthNorth + table2.EastWest;
            var team2Deal = table1.EastWest + table2.SouthNorth;
            var previous = this.pairedDeals.Count > 0 ? this.pairedDeals[^1] : null;
            var team1Total = (previous?.Team1CumulativePoints ?? 0) + team1Deal;
            var team2Total = (previous?.Team2CumulativePoints ?? 0) + team2Deal;
            this.pairedDeals.Add(new TeamMatchComparedDeal(
                dealNumber,
                table1.SouthNorth,
                table1.EastWest,
                table2.SouthNorth,
                table2.EastWest,
                team1Deal,
                team2Deal,
                team1Total,
                team2Total,
                team1Total - team2Total));
        }
    }

    public TeamMatchResultsView Project(
        bool barometerEnabled,
        bool matchComplete,
        int scheduledDealCount)
    {
        // Build the server response with no score fields or rows at all when
        // Barometer OFF and the true, potentially extended match is still active.
        if (!barometerEnabled && !matchComplete)
        {
            return new TeamMatchResultsView(
                false, false, scheduledDealCount,
                null, null, null, Array.Empty<TeamMatchComparedDeal>());
        }

        var latest = this.pairedDeals.Count > 0 ? this.pairedDeals[^1] : null;
        return new TeamMatchResultsView(
            barometerEnabled,
            matchComplete,
            scheduledDealCount,
            latest?.Team1CumulativePoints ?? 0,
            latest?.Team2CumulativePoints ?? 0,
            latest?.Team1CumulativeMargin ?? 0,
            this.pairedDeals.ToArray()); // Never return mutable internal storage.
    }
}
