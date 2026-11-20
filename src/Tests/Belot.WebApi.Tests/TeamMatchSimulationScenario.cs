namespace Belot.WebApi.Tests;

using Belot.Engine.Players;

public sealed record TeamMatchSimulationScenario(
    int HumanCount,
    int ScheduledDeals,
    int SeatVariation,
    bool BarometerEnabled,
    int Seed)
{
    private static readonly int[] HumanCounts = { 2, 3, 4, 5, 6, 7, 8 };

    public string Name =>
        $"{this.HumanCount}h-{8 - this.HumanCount}b-{this.ScheduledDeals}d-"
        + $"{(this.BarometerEnabled ? "barometer" : "hidden")}-v{this.SeatVariation}";

    public static IEnumerable<object[]> Smoke => HumanCounts.Select(count =>
        Scenario(count, 4));

    public static IEnumerable<object[]> Boundary => HumanCounts.Select(count =>
        Scenario(count, 20));

    public static IEnumerable<object[]> Extended => HumanCounts
        .SelectMany(count => Enumerable.Range(5, 15).Select(deals => Scenario(count, deals)));

    public static IEnumerable<object[]> DeferredAllBotTables =>
        Enumerable.Range(4, 17).Select(deals => new object[]
        {
            new TeamMatchSimulationScenario(
                1,
                deals,
                deals % 4,
                deals % 2 == 0,
                SeedFor(1, deals)),
        });

    public static IReadOnlyList<TeamMatchSimulationScenario> All =>
        HumanCounts.Select(count => Create(count, 4))
            .Concat(HumanCounts.SelectMany(
                count => Enumerable.Range(5, 16).Select(deals => Create(count, deals))))
            .ToArray();

    public IReadOnlyList<(int Table, PlayerPosition Seat)> HumanSeats()
    {
        var seats = new[]
        {
            (1, PlayerPosition.South),
            (2, PlayerPosition.East),
            (1, PlayerPosition.North),
            (2, PlayerPosition.West),
            (1, PlayerPosition.East),
            (2, PlayerPosition.North),
            (1, PlayerPosition.West),
            (2, PlayerPosition.South),
        };
        var result = new List<(int Table, PlayerPosition Seat)> { seats[0] };
        if (this.HumanCount == 1)
        {
            return result;
        }

        var rotated = ((this.SeatVariation + this.HumanCount) % (seats.Length - 1)) + 1;
        var index = rotated;
        do
        {
            var candidate = seats[index];
            if (candidate.Item1 == 2)
            {
                result.Add(candidate);
                break;
            }

            index = (index % (seats.Length - 1)) + 1;
        }
        while (index != rotated);

        for (var offset = 1; result.Count < this.HumanCount; offset++)
        {
            var candidate = seats[((rotated - 1 + offset) % (seats.Length - 1)) + 1];
            if (!result.Contains(candidate))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    public bool TableFinishesFirst(int table, int dealNumber) =>
        table == ((this.HumanCount + this.ScheduledDeals + this.SeatVariation + dealNumber) % 2) + 1;

    private static object[] Scenario(int humans, int deals) =>
        new object[] { Create(humans, deals) };

    private static TeamMatchSimulationScenario Create(int humans, int deals)
    {
        var seatVariation = ((humans * 3) + deals) % 4;
        return new(
            humans,
            deals,
            seatVariation,
            (humans + deals) % 2 == 0,
            SeedFor(humans, deals));
    }

    private static int SeedFor(int humans, int deals) =>
        10_620;
}
