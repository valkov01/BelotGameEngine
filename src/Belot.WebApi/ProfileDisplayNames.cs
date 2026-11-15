namespace Belot.WebApi;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public interface IProfileDisplayNameProvider
{
    Task<IReadOnlyDictionary<string, string?>> ResolveAsync(
        IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken);
}

internal sealed class SupabaseProfileDisplayNameProvider : IProfileDisplayNameProvider
{
    private const int MaximumCachedProfiles = 4096;

    private const int MaximumDisplayNameLength = 40;

    private static readonly TimeSpan ProfileCacheDuration = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan MissingProfileCacheDuration = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory httpClientFactory;

    private readonly ILogger<SupabaseProfileDisplayNameProvider> logger;

    private readonly Uri? projectUri;

    private readonly string? serviceRoleKey;

    private readonly SemaphoreSlim lookupGate = new(8, 8);

    private readonly object cacheLock = new();

    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);

    public SupabaseProfileDisplayNameProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<SupabaseProfileDisplayNameProvider> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;

        var projectUrl = configuration["Supabase:Url"];
        this.serviceRoleKey = configuration["Supabase:ServiceRoleKey"];
        if (string.IsNullOrWhiteSpace(projectUrl) || string.IsNullOrWhiteSpace(this.serviceRoleKey))
        {
            this.logger.LogInformation(
                "Supabase profile lookup is not configured; multiplayer seat display names will use the role-based fallback.");
            return;
        }

        if (this.serviceRoleKey.Length > 4096
            || this.serviceRoleKey.Any(character => character <= ' ' || character > '~'))
        {
            this.logger.LogWarning(
                "Supabase profile lookup has an invalid server credential; multiplayer seat display names will use the role-based fallback.");
            this.serviceRoleKey = null;
            return;
        }

        if (!Uri.TryCreate(projectUrl, UriKind.Absolute, out var projectUri)
            || projectUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(projectUri.Query)
            || !string.IsNullOrEmpty(projectUri.Fragment))
        {
            this.logger.LogWarning(
                "Supabase profile lookup requires a valid HTTPS Supabase project URL; multiplayer seat display names will use the role-based fallback.");
            this.serviceRoleKey = null;
            return;
        }

        this.projectUri = projectUri;
    }

    public async Task<IReadOnlyDictionary<string, string?>> ResolveAsync(
        IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (this.projectUri is null || this.serviceRoleKey is null)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        var distinctUserIds = userIds
            .Where(userId => !string.IsNullOrWhiteSpace(userId) && userId.Length <= 256)
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        var results = await Task.WhenAll(distinctUserIds.Select(userId => this.ResolveOneAsync(userId, cancellationToken)));
        return results.ToDictionary(result => result.UserId, result => result.DisplayName, StringComparer.Ordinal);
    }

    private async Task<(string UserId, string? DisplayName)> ResolveOneAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (this.TryGetCached(userId, out var cachedName))
        {
            return (userId, cachedName);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LookupTimeout);
        var gateAcquired = false;
        try
        {
            await this.lookupGate.WaitAsync(timeout.Token);
            gateAcquired = true;
            if (this.TryGetCached(userId, out cachedName))
            {
                return (userId, cachedName);
            }

            var lookup = await this.LookupAsync(userId, timeout.Token);
            this.SetCached(userId, lookup.DisplayName, lookup.CacheDuration);
            return (userId, lookup.DisplayName);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            this.logger.LogWarning(
                "Supabase profile lookup timed out; multiplayer seat display names will use the role-based fallback.");
            this.SetCached(userId, null, FailureCacheDuration);
            return (userId, null);
        }
        finally
        {
            if (gateAcquired)
            {
                this.lookupGate.Release();
            }
        }
    }

    private async Task<LookupResult> LookupAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            var requestUri = new Uri(
                this.projectUri!,
                $"/rest/v1/profiles?id=eq.{Uri.EscapeDataString(userId)}&select=id,display_name");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation("apikey", this.serviceRoleKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.serviceRoleKey);

            using var response = await this.httpClientFactory
                .CreateClient("SupabaseProfiles")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new LookupResult(null, MissingProfileCacheDuration);
            }

            if (!response.IsSuccessStatusCode)
            {
                this.logger.LogWarning(
                    "Supabase profile lookup returned HTTP {StatusCode}; multiplayer seat display names will use the role-based fallback.",
                    (int)response.StatusCode);
                return new LookupResult(null, FailureCacheDuration);
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() == 0)
            {
                return new LookupResult(null, MissingProfileCacheDuration);
            }

            var profile = document.RootElement[0];
            if (profile.ValueKind != JsonValueKind.Object)
            {
                this.logger.LogWarning(
                    "Supabase profile lookup returned an unexpected row; multiplayer seat display names will use the role-based fallback.");
                return new LookupResult(null, FailureCacheDuration);
            }

            var returnedId = ReadString(profile, "id");
            if (!string.Equals(returnedId, userId, StringComparison.Ordinal))
            {
                this.logger.LogWarning(
                    "Supabase profile lookup returned an unexpected row; multiplayer seat display names will use the role-based fallback.");
                return new LookupResult(null, FailureCacheDuration);
            }

            var displayName = Normalize(ReadString(profile, "display_name"));
            return new LookupResult(displayName, displayName is null ? MissingProfileCacheDuration : ProfileCacheDuration);
        }
        catch (HttpRequestException)
        {
            this.logger.LogWarning(
                "Supabase profile lookup failed; multiplayer seat display names will use the role-based fallback.");
            return new LookupResult(null, FailureCacheDuration);
        }
        catch (IOException)
        {
            this.logger.LogWarning(
                "Supabase profile lookup failed while reading its response; multiplayer seat display names will use the role-based fallback.");
            return new LookupResult(null, FailureCacheDuration);
        }
        catch (JsonException)
        {
            this.logger.LogWarning(
                "Supabase profile lookup returned invalid JSON; multiplayer seat display names will use the role-based fallback.");
            return new LookupResult(null, FailureCacheDuration);
        }
        catch (UriFormatException)
        {
            this.logger.LogWarning(
                "Supabase profile lookup could not form a safe request; multiplayer seat display names will use the role-based fallback.");
            return new LookupResult(null, FailureCacheDuration);
        }
    }

    private static string? ReadString(JsonElement profile, string propertyName) =>
        profile.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(MaximumDisplayNameLength);
        var pendingSpace = false;
        var runeCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (runeCount >= MaximumDisplayNameLength)
                {
                    break;
                }

                builder.Append(' ');
                runeCount++;
                pendingSpace = false;
            }

            if (runeCount >= MaximumDisplayNameLength)
            {
                break;
            }

            builder.Append(rune.ToString());
            runeCount++;
        }

        var normalized = builder.ToString().Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private bool TryGetCached(string userId, out string? displayName)
    {
        lock (this.cacheLock)
        {
            if (this.cache.TryGetValue(userId, out var entry))
            {
                if (entry.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    displayName = entry.DisplayName;
                    return true;
                }

                this.cache.Remove(userId);
            }
        }

        displayName = null;
        return false;
    }

    private void SetCached(string userId, string? displayName, TimeSpan duration)
    {
        lock (this.cacheLock)
        {
            if (this.cache.Count >= MaximumCachedProfiles && !this.cache.ContainsKey(userId))
            {
                var expiredKeys = this.cache
                    .Where(entry => entry.Value.ExpiresAt <= DateTimeOffset.UtcNow)
                    .Select(entry => entry.Key)
                    .ToArray();
                foreach (var expiredKey in expiredKeys)
                {
                    this.cache.Remove(expiredKey);
                }
            }

            if (this.cache.Count >= MaximumCachedProfiles && !this.cache.ContainsKey(userId))
            {
                var oldest = this.cache.MinBy(entry => entry.Value.ExpiresAt);
                if (oldest.Key is not null)
                {
                    this.cache.Remove(oldest.Key);
                }
            }

            this.cache[userId] = new CacheEntry(displayName, DateTimeOffset.UtcNow.Add(duration));
        }
    }

    private sealed record CacheEntry(string? DisplayName, DateTimeOffset ExpiresAt);

    private sealed record LookupResult(string? DisplayName, TimeSpan CacheDuration);
}
