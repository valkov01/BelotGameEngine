namespace Belot.WebApi.Tests;

public static class TeamMatchScoringFixtures
{
    public static IEnumerable<object[]> Independent()
    {
        return new[]
        {
            new TeamMatchScoringFixture(
                "all-pass",
                new[]
                {
                    new FixtureDeal((0, 0), (0, 0)),
                    new FixtureDeal((0, 0), (0, 0)),
                },
                new[] { 0, 0 },
                0),
            new TeamMatchScoringFixture(
                "changing-team-lead",
                new[]
                {
                    new FixtureDeal((20, 6), (14, 12)),
                    new FixtureDeal((0, 0), (8, 18)),
                    new FixtureDeal((7, 19), (21, 5)),
                },
                new[] { 12, 10, -28 },
                -6),
            new TeamMatchScoringFixture(
                "hanging-points-awarded-to-table-two-team-two",
                new[]
                {
                    new FixtureDeal((13, 13), (8, 18)),
                    new FixtureDeal((29, 0), (0, 29)),
                },
                new[] { 10, 58 },
                68),
            new TeamMatchScoringFixture(
                "scheduled-final-plus-extension",
                new[]
                {
                    new FixtureDeal((16, 10), (12, 14)),
                    new FixtureDeal((4, 22), (20, 6)),
                    new FixtureDeal((9, 17), (18, 8)),
                    new FixtureDeal((15, 11), (10, 16)),
                    new FixtureDeal((27, 0), (0, 27)),
                },
                new[] { 8, -32, -18, 10, 54 },
                22),
        }.Select(fixture => new object[] { fixture });
    }

    public sealed record TeamMatchScoringFixture(
        string Name,
        IReadOnlyList<FixtureDeal> Deals,
        IReadOnlyList<int> ExpectedSignedDeals,
        int ExpectedFinalMargin);

    public sealed record FixtureDeal(
        (int SouthNorth, int EastWest) Table1,
        (int SouthNorth, int EastWest) Table2);
}
