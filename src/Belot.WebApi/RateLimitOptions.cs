namespace Belot.WebApi;

internal sealed class HybridRateLimitOptions
{
    public const string SectionName = "RateLimit:Hybrid";

    public bool Enabled { get; init; }

    public int IpPermitLimit { get; init; } = 600;

    public int WindowSeconds { get; init; } = 60;

    public int TeamMatchReadPermitLimit { get; init; } = 90;

    public int TeamMatchMutationPermitLimit { get; init; } = 20;

    public int PerUserConcurrencyLimit { get; init; } = 4;

    public int AggregateConcurrencyLimit { get; init; } = 32;
}

internal enum TeamMatchRateLimitKind
{
    Read,
    Mutation,
}

internal sealed record TeamMatchRateLimitMetadata(TeamMatchRateLimitKind Kind);
