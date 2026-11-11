namespace Belot.WebApi;

using Belot.Engine.Cards;
using Belot.Engine.Game;
using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

public sealed record HealthResponse(string Status);

public sealed record ApiError(string Error);

public sealed record BotProfileDto(string Id, string Name);

public sealed class CreateGameRequest
{
    public string North { get; init; } = "skilled";

    public string East { get; init; } = "skilled";

    public string West { get; init; } = "skilled";
}

public sealed class GameActionRequest
{
    public string? Bid { get; init; }

    public IReadOnlyList<AnnounceRequest>? Announces { get; init; }

    public CardDto? Card { get; init; }

    public bool Belote { get; init; }
}

public sealed class AnnounceRequest
{
    public string? Type { get; init; }

    public CardDto? Card { get; init; }
}

public sealed record CardDto(string Suit, string Type)
{
    public static CardDto From(Card card) => new(card.Suit.ToString(), card.Type.ToString());

    public static bool TryParse(CardDto? value, out Card? card)
    {
        card = null;
        if (value is null
            || !Enum.TryParse<CardSuit>(value.Suit, true, out var suit)
            || !Enum.IsDefined(suit)
            || !Enum.TryParse<CardType>(value.Type, true, out var type)
            || !Enum.IsDefined(type))
        {
            return false;
        }

        card = Card.GetCard(suit, type);
        return true;
    }
}

public sealed record BidDto(string Seat, string Type);

public sealed record AnnounceDto(string Seat, string Type, CardDto? Card, bool? IsScored);

public sealed record PlayedCardDto(string Seat, CardDto Card, bool Belote);

public sealed record TrickDto(IReadOnlyList<PlayedCardDto> Cards, string Winner);

public sealed record RoundSummaryDto(
    int RoundNumber,
    BidDto Contract,
    int SouthNorthPoints,
    int EastWestPoints,
    int SouthNorthTotalInRoundPoints,
    int EastWestTotalInRoundPoints,
    int HangingPoints,
    IReadOnlyList<BidDto> Bids,
    IReadOnlyList<AnnounceDto> Announces,
    IReadOnlyList<TrickDto> Tricks);

public sealed record GameStateDto(
    string GameId,
    IReadOnlyDictionary<string, string> Bots,
    bool IsFinished,
    string? Winner,
    string ToMove,
    string Decision,
    int RoundNumber,
    string FirstToPlay,
    int SouthNorthPoints,
    int EastWestPoints,
    int HangingPoints,
    BidDto Contract,
    IReadOnlyList<BidDto> Bids,
    IReadOnlyList<AnnounceDto> Announces,
    IReadOnlyList<TrickDto> Tricks,
    IReadOnlyList<PlayedCardDto> CurrentTrick,
    IReadOnlyList<int> CardCounts,
    IReadOnlyList<CardDto> Hand,
    IReadOnlyList<string> AvailableBids,
    IReadOnlyList<AnnounceDto> AvailableAnnounces,
    IReadOnlyList<CardDto> PlayableCards,
    IReadOnlyList<CardDto> BeloteCards,
    IReadOnlyList<RoundSummaryDto> PreviousRounds);

public sealed record PublicGameEventDto(
    int Sequence,
    string Type,
    int RoundNumber,
    string? Seat = null,
    string? Bid = null,
    string? ContractSeat = null,
    string? Contract = null,
    bool? Automatic = null,
    CardDto? Card = null,
    bool? Belote = null,
    int? TrickNumber = null,
    IReadOnlyList<PlayedCardDto>? Cards = null,
    string? Winner = null,
    IReadOnlyList<AnnounceDto>? Announces = null,
    int? SouthNorthPoints = null,
    int? EastWestPoints = null,
    int? SouthNorthTotalInRoundPoints = null,
    int? EastWestTotalInRoundPoints = null,
    int? HangingPoints = null,
    bool? MatchFinished = null);

public sealed record GameActionResponse(GameStateDto State, IReadOnlyList<PublicGameEventDto> Events);

public sealed record DebugHandDto(string Seat, string Role, IReadOnlyList<CardDto> Cards);

public sealed record DebugHandsDto(string GameId, IReadOnlyList<DebugHandDto> Hands);

internal static class ApiMapper
{
    public static GameStateDto ToState(string gameId, IReadOnlyDictionary<string, string> bots, BelotSeatView view)
    {
        return new GameStateDto(
            gameId,
            bots,
            view.IsMatchFinished,
            view.IsMatchFinished && view.MatchWinner != PlayerPosition.Unknown ? view.MatchWinner.ToString() : null,
            view.ToMove.ToString(),
            view.Decision.ToString(),
            view.RoundNumber,
            view.FirstToPlayInTheRound.ToString(),
            view.SouthNorthPoints,
            view.EastWestPoints,
            view.HangingPoints,
            ToBid(view.Contract),
            view.Bids.Select(ToBid).ToArray(),
            view.Announces.Select(ToAnnounce).ToArray(),
            view.Tricks.Select(ToTrick).ToArray(),
            view.CurrentTrick.Select(ToPlayedCard).ToArray(),
            view.CardCounts.ToArray(),
            view.Hand.Select(CardDto.From).ToArray(),
            ExpandBids(view.AvailableBids),
            view.AvailableAnnounces.Select(ToAnnounce).ToArray(),
            view.PlayableCards.Select(CardDto.From).ToArray(),
            view.BeloteCards.Select(CardDto.From).ToArray(),
            view.PreviousRounds.Select((round, index) => ToRound(round, index + 1)).ToArray());
    }

    public static BidDto ToBid(Bid bid) => new(bid.Player.ToString(), bid.Type.ToString());

    public static AnnounceDto ToAnnounce(BelotAnnounce announce) =>
        new(announce.Player.ToString(), announce.Type.ToString(), announce.Card is null ? null : CardDto.From(announce.Card), announce.IsScored);

    public static PlayedCardDto ToPlayedCard(BelotPlayedCard card) => new(card.Player.ToString(), CardDto.From(card.Card), card.Belote);

    public static TrickDto ToTrick(BelotTrick trick) => new(trick.Cards.Select(ToPlayedCard).ToArray(), trick.Winner.ToString());

    private static RoundSummaryDto ToRound(BelotRoundSummary round, int roundNumber) =>
        new(
            roundNumber,
            ToBid(round.Contract),
            round.SouthNorthPoints,
            round.EastWestPoints,
            round.SouthNorthTotalInRoundPoints,
            round.EastWestTotalInRoundPoints,
            round.HangingPoints,
            round.Bids.Select(ToBid).ToArray(),
            round.Announces.Select(ToAnnounce).ToArray(),
            round.Tricks.Select(ToTrick).ToArray());

    private static IReadOnlyList<string> ExpandBids(BidType bids)
    {
        var values = new[]
        {
            BidType.Clubs, BidType.Diamonds, BidType.Hearts, BidType.Spades,
            BidType.NoTrumps, BidType.AllTrumps, BidType.Double, BidType.ReDouble,
        };
        return values.Where(bid => bids.HasFlag(bid)).Select(bid => bid.ToString()).ToArray();
    }
}