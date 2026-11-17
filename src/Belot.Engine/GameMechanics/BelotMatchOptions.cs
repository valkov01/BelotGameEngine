namespace Belot.Engine.GameMechanics
{
    using System;
    using System.Collections.Generic;

    using Belot.Engine.Cards;
    using Belot.Engine.Players;

    /// <summary>Settings for a <see cref="BelotMatch"/>. Every property has a usable default.</summary>
    public sealed class BelotMatchOptions
    {
        /// <summary>
        /// Gets or sets who bids and leads first in the first deal; the next deals go round
        /// (South, East, North, West).
        /// </summary>
        public PlayerPosition FirstToPlay { get; set; } = PlayerPosition.South;

        /// <summary>
        /// Gets or sets the source of the deals' shuffles; null for a shared per-thread one. Every
        /// deal shuffles the whole deck once, so with a seeded source the n-th deal depends only
        /// on the seed and n.
        /// </summary>
        public Random Random { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the match keeps its history (deals, auctions,
        /// tricks, results), which the views and the record need. On by default.
        /// </summary>
        public bool RecordHistory { get; set; } = true;

        /// <summary>
        /// When true, a finished deal pauses before the next deal. The server must explicitly
        /// call AdvanceToNextRound or CompleteAfterRound. Ignores the standard 151-point finish.
        /// Defaults to false to preserve classic Belot behavior.
        /// </summary>
        public bool ManualRoundProgression { get; set; }

        /// <summary>
        /// Optional server-supplied canonical 32-card order for a numbered deal.
        /// The deck validates all 32 unique cards and takes a defensive copy.
        /// Never expose this delegate or its returned cards in an HTTP response.
        /// </summary>
        public Func<int, IReadOnlyList<Card>> DealOrderProvider { get; set; }

        /// <summary>Gets or sets the UI seats that must explicitly play even a single legal card.</summary>
        internal PlayerPosition ManualCardPlaySeats { get; set; }
    }
}
