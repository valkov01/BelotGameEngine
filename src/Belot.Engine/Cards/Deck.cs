namespace Belot.Engine.Cards
{
    using System;
    using System.Linq;
    using System.Runtime.CompilerServices;

    public class Deck
    {
        private readonly Card[] listOfCards;

        private readonly Random random;

        private int currentCardIndex;

        public Deck()
            : this(null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Deck"/> class.
        /// </summary>
        /// <param name="random">The source of the shuffles; null for a shared per-thread one. Every
        /// shuffle takes the same number of draws, so a seeded source deals the same sequence of
        /// decks.</param>
        public Deck(Random random)
        {
            this.listOfCards = Card.AllCards.ToArray();
            this.random = random;
        }

        public void Shuffle()
        {
            this.listOfCards.Shuffle(this.random ?? ThreadSafeRandom.Current);
            this.currentCardIndex = 0;
        }

        /// <summary>
        /// Loads a predetermined 32-card deal in the normal dealing order, without shuffling.
        /// Used by authoritative multi-table game coordinators to replay the exact same deal.
        /// </summary>
        /// <param name="order">Exactly one of each of the 32 cards.</param>
        internal void LoadOrder(System.Collections.Generic.IReadOnlyList<Card> order)
        {
            if (order == null || order.Count != Card.AllCards.Length)
            {
                throw new ArgumentException("A predetermined deal must contain exactly 32 cards.", nameof(order));
            }

            var seen = new bool[Card.AllCards.Length];
            for (var i = 0; i < order.Count; i++)
            {
                var card = order[i];
                if (card == null || card.GetHashCode() < 0 || card.GetHashCode() >= seen.Length
                    || !ReferenceEquals(Card.AllCards[card.GetHashCode()], card)
                    || seen[card.GetHashCode()])
                {
                    throw new ArgumentException("A predetermined deal must contain each card exactly once.", nameof(order));
                }

                seen[card.GetHashCode()] = true;
            }

            for (var i = 0; i < order.Count; i++)
            {
                this.listOfCards[i] = order[i];
            }

            this.currentCardIndex = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Card GetNextCard() => this.listOfCards[this.currentCardIndex++];

        /// <summary>Returns a defensive copy of the full private deck order for server-side board preparation.</summary>
        public Card[] CopyOrder() => (Card[])this.listOfCards.Clone();
    }
}
