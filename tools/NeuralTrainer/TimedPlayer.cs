namespace Belot.NeuralTrainer
{
    using System.Collections.Generic;
    using System.Diagnostics;

    using Belot.Engine;
    using Belot.Engine.Game;
    using Belot.Engine.GameMechanics;
    using Belot.Engine.Players;

    /// <summary>Times another player's decision callbacks.</summary>
    internal sealed class TimedPlayer : IPlayer
    {
        private readonly IPlayer player;

        public TimedPlayer(IPlayer player)
        {
            this.player = player;
        }

        public long Decisions { get; private set; }

        public long Ticks { get; private set; }

        public List<long> DecisionTicks { get; } = new List<long>();

        public List<long> BidDecisionTicks { get; } = new List<long>();

        public List<long> AnnounceDecisionTicks { get; } = new List<long>();

        public List<long> AllDecisionTicks { get; } = new List<long>();

        public BidType GetBid(PlayerGetBidContext context)
        {
            var start = Stopwatch.GetTimestamp();
            var bid = this.player.GetBid(context);
            this.Record(start, this.BidDecisionTicks);
            return bid;
        }

        public IList<Announce> GetAnnounces(PlayerGetAnnouncesContext context)
        {
            var start = Stopwatch.GetTimestamp();
            var announces = this.player.GetAnnounces(context);
            this.Record(start, this.AnnounceDecisionTicks);
            return announces;
        }

        public PlayCardAction PlayCard(PlayerPlayCardContext context)
        {
            var start = Stopwatch.GetTimestamp();
            var action = this.player.PlayCard(context);
            this.Ticks += this.Record(start, this.DecisionTicks);
            this.Decisions++;
            return action;
        }

        public void EndOfTrick(IEnumerable<PlayCardAction> trickActions) => this.player.EndOfTrick(trickActions);

        public void EndOfRound(RoundResult roundResult) => this.player.EndOfRound(roundResult);

        public void EndOfGame(GameResult gameResult) => this.player.EndOfGame(gameResult);

        private long Record(long start, List<long> category)
        {
            var elapsed = Stopwatch.GetTimestamp() - start;
            category.Add(elapsed);
            this.AllDecisionTicks.Add(elapsed);
            return elapsed;
        }
    }
}
