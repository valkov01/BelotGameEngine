namespace Belot.WebApi;

/// <summary>Result of checking whether the two-table shared end boundary is ready.</summary>
internal enum TeamMatchBoundaryDecision
{
    Waiting,
    Extend,
    Complete,
}

/// <summary>
/// Pure server-side A10 end-condition policy. The organizer selects 4..20 scheduled
/// deals, but either table's unresolved hanging points extend BOTH tables by the
/// same additional numbered board as many times as necessary.
/// </summary>
internal sealed class TeamMatchDealSchedule
{
    public TeamMatchDealSchedule(int scheduledDeals)
    {
        if (scheduledDeals is < 4 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(scheduledDeals), "Choose 4 to 20 scheduled deals.");
        }

        this.ScheduledDeals = scheduledDeals;
        this.AllowedDeals = scheduledDeals;
    }

    public int ScheduledDeals { get; }

    public int AllowedDeals { get; private set; }

    public bool IsFinished { get; private set; }

    /// <summary>
    /// All input scores are private server-side engine state, not client-supplied
    /// decisions. Ordinary deals can advance independently up to AllowedDeals.
    /// </summary>
    public TeamMatchBoundaryDecision Resolve(
        int table1RoundsPlayed,
        int table2RoundsPlayed,
        int table1Hanging,
        int table2Hanging)
    {
        if (table1RoundsPlayed < 0 || table2RoundsPlayed < 0
            || table1RoundsPlayed > this.AllowedDeals || table2RoundsPlayed > this.AllowedDeals
            || table1Hanging < 0 || table2Hanging < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(table1RoundsPlayed), "Invalid table progress or hanging points.");
        }

        if (this.IsFinished)
        {
            return TeamMatchBoundaryDecision.Complete;
        }

        if (table1RoundsPlayed != this.AllowedDeals || table2RoundsPlayed != this.AllowedDeals)
        {
            return TeamMatchBoundaryDecision.Waiting;
        }

        if (table1Hanging > 0 || table2Hanging > 0)
        {
            this.AllowedDeals++;
            return TeamMatchBoundaryDecision.Extend;
        }

        this.IsFinished = true;
        return TeamMatchBoundaryDecision.Complete;
    }
}
