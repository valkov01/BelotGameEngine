namespace Belot.WebApi;

using Belot.Engine.Game;
using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

internal static class GameEventProjector
{
    public static IReadOnlyList<PublicGameEventDto> Project(
        BelotSeatView before,
        BelotSeatView after,
        PlayerPosition actor,
        BelotAction action,
        int firstSequence)
    {
        var events = new List<PublicGameEventDto>();
        var roundEnded = after.PreviousRounds.Count > before.PreviousRounds.Count;
        var ended = roundEnded ? after.PreviousRounds[^1] : null;
        var roundNumber = before.RoundNumber;

        var bids = ended?.Bids ?? after.Bids;
        var contractSeat = before.Contract.Player;
        var contract = before.Contract.Type;
        for (var index = before.Bids.Count; index < bids.Count; index++)
        {
            var bid = bids[index];
            if (bid.Type is BidType.Double or BidType.ReDouble)
            {
                contract = (contract & ~(BidType.Double | BidType.ReDouble)) | bid.Type;
            }
            else if (bid.Type != BidType.Pass)
            {
                contractSeat = bid.Player;
                contract = bid.Type;
            }

            events.Add(new PublicGameEventDto(
                firstSequence + events.Count,
                "bid",
                roundNumber,
                bid.Player.ToString(),
                bid.Type.ToString(),
                contractSeat == PlayerPosition.Unknown ? null : contractSeat.ToString(),
                contract.ToString(),
                actor != PlayerPosition.South || index != before.Bids.Count || action.Type != BelotActionType.Bid));
        }

        var resultingContract = ended?.Contract ?? after.Contract;
        if (before.Decision == BelotDecision.Bid && after.Decision != BelotDecision.Bid && resultingContract.Type != BidType.Pass)
        {
            events.Add(new PublicGameEventDto(
                firstSequence + events.Count,
                "contract",
                roundNumber,
                ContractSeat: resultingContract.Player.ToString(),
                Contract: resultingContract.Type.ToString()));
        }

        if (action.Type == BelotActionType.Announce)
        {
            var announces = ended?.Announces ?? after.Announces;
            var declared = announces
                .Skip(before.Announces.Count)
                .Where(announce => announce.Type != AnnounceType.Belot && announce.Player == actor)
                .Select(ApiMapper.ToAnnounce)
                .ToArray();
            events.Add(new PublicGameEventDto(
                firstSequence + events.Count,
                "declarations",
                roundNumber,
                actor.ToString(),
                Automatic: actor != PlayerPosition.South,
                Announces: declared));
        }

        var tricks = ended?.Tricks ?? after.Tricks;
        var cardsPlayed = before.Tricks.Sum(trick => trick.Cards.Count) + before.CurrentTrick.Count;
        var newCards = tricks.SelectMany(trick => trick.Cards)
            .Concat(ended is null ? after.CurrentTrick : Array.Empty<BelotPlayedCard>())
            .ToArray();
        for (var index = cardsPlayed; index < newCards.Length; index++)
        {
            var card = newCards[index];
            var automatic = actor != PlayerPosition.South || index != cardsPlayed || action.Type != BelotActionType.PlayCard;
            events.Add(new PublicGameEventDto(
                firstSequence + events.Count,
                "card",
                roundNumber,
                card.Player.ToString(),
                Automatic: automatic,
                Card: CardDto.From(card.Card),
                Belote: card.Belote,
                TrickNumber: (index / 4) + 1));

            if (index % 4 == 3)
            {
                var trick = tricks[index / 4];
                events.Add(new PublicGameEventDto(
                    firstSequence + events.Count,
                    "trick",
                    roundNumber,
                    TrickNumber: (index / 4) + 1,
                    Cards: trick.Cards.Select(ApiMapper.ToPlayedCard).ToArray(),
                    Winner: trick.Winner.ToString()));
            }
        }

        if (ended is not null)
        {
            events.Add(new PublicGameEventDto(
                firstSequence + events.Count,
                "deal-ended",
                roundNumber,
                ContractSeat: ended.Contract.Player.ToString(),
                Contract: ended.Contract.Type.ToString(),
                SouthNorthPoints: ended.SouthNorthPoints,
                EastWestPoints: ended.EastWestPoints,
                SouthNorthTotalInRoundPoints: ended.SouthNorthTotalInRoundPoints,
                EastWestTotalInRoundPoints: ended.EastWestTotalInRoundPoints,
                HangingPoints: ended.HangingPoints,
                MatchFinished: after.IsMatchFinished));
        }

        if (after.RoundNumber > before.RoundNumber && !after.IsMatchFinished)
        {
            events.Add(new PublicGameEventDto(
                firstSequence + events.Count,
                "deal-started",
                after.RoundNumber,
                Seat: after.FirstToPlayInTheRound.ToString()));
        }

        return events;
    }
}