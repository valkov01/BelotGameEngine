namespace Belot.WebApi;

using System.Security.Cryptography;

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

    private readonly Deck? deterministicDeck;

    private readonly TeamMatchDealSchedule schedule;

    private readonly TeamMatchComparisonTracker comparisons = new();

    private readonly bool barometerEnabled;

    private readonly Dictionary<int, Card[]> canonicalBoards = new();

    private readonly BelotMatch[] tables;

    /// <summary>
    /// Production default: secure, unpredictable canonical deal permutations.
    /// Neither the shuffle RNG nor the full deals may be exposed in public views.
    /// </summary>
    public TeamMatchCoreCoordinator(
        int scheduledDeals,
        PlayerPosition firstBidder = PlayerPosition.South,
        bool barometerEnabled = true)
        : this(scheduledDeals, null, firstBidder, barometerEnabled)
    {
    }

    /// <summary>
    /// Deterministic mode for reproducible unit tests only; do not accept this seed
    /// from a user or reuse this overload for public Team Matches.
    /// </summary>
    internal TeamMatchCoreCoordinator(
        int scheduledDeals,
        int deckSeed,
        PlayerPosition firstBidder = PlayerPosition.South,
        bool barometerEnabled = true)
        : this(scheduledDeals, (int?)deckSeed, firstBidder, barometerEnabled)
    {
    }

    private TeamMatchCoreCoordinator(
        int scheduledDeals,
        int? deterministicDeckSeed,
        PlayerPosition firstBidder,
        bool barometerEnabled)
    {
        this.schedule = new TeamMatchDealSchedule(scheduledDeals);
        this.barometerEnabled = barometerEnabled;
        if (deterministicDeckSeed.HasValue)
        {
            this.deterministicDeck = new Deck(new Random(deterministicDeckSeed.Value));
        }

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

    /// <summary>
    /// Public-data-only comparison projection. Future REST/SignalR handlers MUST
    /// authenticate match membership first; the coordinator itself is internal.
    /// Barometer OFF hides all comparative results until the actual match ends.
    /// </summary>
    internal TeamMatchResultsView GetComparedResults()
    {
        lock (this.gate)
        {
            return this.comparisons.Project(
                this.barometerEnabled, this.IsFinished, this.ScheduledDeals);
        }
    }

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
                this.CapturePairedCompletedDeals();
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
                Card[] board;
                if (this.deterministicDeck != null)
                {
                    this.deterministicDeck.Shuffle();
                    board = this.deterministicDeck.CopyOrder();
                }
                else
                {
                    board = Card.AllCards.ToArray();
                    // Unbiased Fisher-Yates, using a cryptographically secure source.
                    // Never derive public tournament deals from a 32-bit test seed.
                    for (var i = board.Length - 1; i > 0; i--)
                    {
                        var j = RandomNumberGenerator.GetInt32(i + 1);
                        (board[i], board[j]) = (board[j], board[i]);
                    }
                }

                this.canonicalBoards.Add(this.canonicalBoards.Count + 1, board);
            }

            return this.canonicalBoards[roundNumber];
        }
    }

    /// <summary>
    /// Pair only the same completed deal index at both tables, never by finish
    /// order or current active board. BelotSeatView.PreviousRounds contains the
    /// C# engine's scored PUBLIC summaries (not raw trick points or secret hands).
    /// Capture while holding the coordinator lock, once per numbered deal.
    /// </summary>
    private void CapturePairedCompletedDeals()
    {
        var pairedReady = Math.Min(this.tables[0].RoundsPlayed, this.tables[1].RoundsPlayed);
        if (pairedReady <= this.comparisons.PairedDealCount)
        {
            return;
        }

        var table1 = this.tables[0].GetView(PlayerPosition.South).PreviousRounds;
        var table2 = this.tables[1].GetView(PlayerPosition.South).PreviousRounds;
        for (var dealNumber = this.comparisons.PairedDealCount + 1;
             dealNumber <= pairedReady;
             dealNumber++)
        {
            var index = dealNumber - 1;
            if (table1.Count <= index || table2.Count <= index)
            {
                throw new InvalidOperationException("Cannot compare a deal without both authoritative engine scores.");
            }

            var one = table1[index];
            var two = table2[index];
            this.comparisons.RecordPair(
                dealNumber,
                new TeamMatchTableDealPoints(one.SouthNorthPoints, one.EastWestPoints),
                new TeamMatchTableDealPoints(two.SouthNorthPoints, two.EastWestPoints));
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
