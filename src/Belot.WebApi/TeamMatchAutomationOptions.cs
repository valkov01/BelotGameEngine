namespace Belot.WebApi;

internal sealed class TeamMatchAutomationOptions
{
    public const string SectionName = "TeamMatch:Automation";

    public int MaximumActiveMatches { get; init; } = 20;

    public int WakeupQueueCapacity { get; init; } = 64;

    public int ActionsPerCycle { get; init; } = 8;

    public int IdleScanMilliseconds { get; init; } = 250;

    public int InterActionDelayMilliseconds { get; init; } = 1;

    public int MaximumDecisionRetries { get; init; } = 3;

    public int RetryDelayMilliseconds { get; init; } = 250;
}
