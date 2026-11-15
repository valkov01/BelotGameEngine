namespace Belot.WebApi.Tests;

using Belot.WebApi;

internal sealed class TestProfileDisplayNameProvider : IProfileDisplayNameProvider
{
    private readonly object sync = new();

    private readonly Dictionary<string, string?> displayNames = new(StringComparer.Ordinal);

    public bool IsAvailable { get; set; } = true;

    public void SetDisplayName(string userId, string? displayName)
    {
        lock (this.sync)
        {
            this.displayNames[userId] = displayName;
        }
    }

    public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(
        IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this.sync)
        {
            IReadOnlyDictionary<string, string?> result = this.IsAvailable
                ? userIds
                    .Where(this.displayNames.ContainsKey)
                    .Distinct(StringComparer.Ordinal)
                    .ToDictionary(userId => userId, userId => this.displayNames[userId], StringComparer.Ordinal)
                : new Dictionary<string, string?>(StringComparer.Ordinal);
            return Task.FromResult(result);
        }
    }
}
