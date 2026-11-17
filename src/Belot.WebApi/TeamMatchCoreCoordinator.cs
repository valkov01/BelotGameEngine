namespace Belot.WebApi;

using Belot.Engine.Cards;
using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

/// <summary>
/// Internal, unauthenticated A10 domain core. An HTTP adapter MUST check user/seat rights
/// before calling GetSeatView or Act. No public routes are introduced by this component.
/// Two ordinary Belot engines share canonical deals but advance at separate speeds.
/// </summary>
internal sealed class TeamMatchCoreCoordinator
{
    private readonly object gate = new();

    private readonly Deck canonicalDeck;

    private readonly TeamMatchDealSchedule schedule;

    private readonly Dictionary<int, Card[]> canonicalBoards = new();

    private readonly BelotMatch[] tables;

    public TeamMatchCoreCoordinator(
        int scheduledDeals,
        int deckSeed,
        PlayerPosition firstBidder = PlayerPosition.South)
    {
        this.schedule = new TeamMatchDealSchedule(scheduledDeals);
        this.canonicalDeck = new Deck(new Random(deckSeed));

        // Each table has its own deck, hands and score state. The provider returns
        // the same immutable numbered distribution for every absolute compass seat.
        this.tables = new BelotMatch[2];
        for (var i = 0; i < this.tables.Length; i++)
        {
            var match = new BelotMatch(new BelotMatchOptions
            {
                ManualRoundProgression = true,
                RecordHistory = true,
                FirstToPlay = firstBidder,
                DealOrderProvider = this.GetCanonicalBoard,
            });
            match.Start();
            this.tables[i] = match;
        }
    }

    /// <summary>Organizer-selected deal count: the minimum before hanging can extend the match.</summary>
    public int ScheduledDeals => this.schedule.ScheduledDeals;

    /// <summary>Current shared last scheduled board, extended one at a time when hanging remains.</summary>
    public int AllowedDeals => this.schedule.AllowedDeals;

    public bool IsFinished => this.schedule.IsFinished;

    public int GetRoundsPlayed(int table)
    {
        lock (this.gate)
        {
            return this.GetTable(table).RoundsPlayed;
        }
    }

    public int GetHangingPoints(int table)
    {
        lock (this.gate)
        {
            return this.GetTable(table).HangingPoints;
        }
    }

    public bool IsAwaitingSharedBoundary(int table)
    {
        lock (this.gate)
        {
            return this.GetTable(table).IsAwaitingRoundAdvance;
        }
    }

    /// <summary>
    /// This returns a private seat view. Caller MUST enforce authenticated seat ownership;
    /// the coordinator deliberately remains internal until A10.3 introduces that policy.
    /// </summary>
    public BelotSeatView GetSeatView(int table, PlayerPosition seat)
    {
        lock (this.gate)
        {
            return this.GetTable(table).GetView(seat);
        }
    }

    /// <summary>
    /// Accept a validated, seat-authorized action in the surrounding API layer. The coordinator
    /// serializes all mutations and never lets a table advance beyond the shared final boundary.
    /// </summary>
    public BelotActResult Act(int table, PlayerPosition seat, BelotAction action)
    {
        lock (this.gate)
        {
            var match = this.GetTable(table);
            if (this.IsFinished)
            {
                return BelotActResult.MatchFinished;
            }

            if (match.IsAwaitingRoundAdvance)
            {
                return BelotActResult.NotYourTurn;
            }

            var result = match.Act(seat, action);
            if (result == BelotActResult.Ok)
            {
                this.ProcessCompletedRounds();
            }

            return result;
        }
    }

    /// <summary>Internal test/audit hook: a defensive copy of the secret canonical board.</summary>
    internal IReadOnlyList<Card> GetBoardForAudit(int roundNumber)
    {
        lock (this.gate)
        {
            return this.GetCanonicalBoard(roundNumber).ToArray();
        }
    }

    private BelotMatch GetTable(int table) => table switch
    {
        1 => this.tables[0],
        2 => this.tables[1],
        _ => throw new ArgumentOutOfRangeException(nameof(table), "A team match has exactly two tables."),
    };

    private IReadOnlyList<Card> GetCanonicalBoard(int roundNumber)
    {
        lock (this.gate)
        {
            if (roundNumber < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(roundNumber));
            }

            // Generate every missing intermediate board exactly once, even when
            // one table is many rounds ahead of the other. The full 32-card order
            // is kept server-side, never returned from a public HTTP endpoint.
            while (this.canonicalBoards.Count < roundNumber)
            {
                this.canonicalDeck.Shuffle();
                this.canonicalBoards.Add(this.canonicalBoards.Count + 1, this.canonicalDeck.CopyOrder());
            }

            return this.canonicalBoards[roundNumber];
        }
    }

    private void ProcessCompletedRounds()
    {
        foreach (var table in this.tables)
        {
            if (table.IsAwaitingRoundAdvance && table.RoundsPlayed < this.AllowedDeals)
            {
                table.AdvanceToNextRound();
            }
        }

        // Only both completed tables may settle the shared boundary.
        // The schedule handles arbitrarily many identical extension deals.
        if (!this.tables.All(table => table.IsAwaitingRoundAdvance))
        {
            return;
        }

        var decision = this.schedule.Resolve(
            this.tables[0].RoundsPlayed,
            this.tables[1].RoundsPlayed,
            this.tables[0].HangingPoints,
            this.tables[1].HangingPoints);
        if (decision == TeamMatchBoundaryDecision.Extend)
        {
            foreach (var table in this.tables)
            {
                table.AdvanceToNextRound();
            }

            return;
        }

        if (decision == TeamMatchBoundaryDecision.Complete)
        {
            foreach (var table in this.tables)
            {
                table.CompleteAfterRound();
            }
        }
    }
}
