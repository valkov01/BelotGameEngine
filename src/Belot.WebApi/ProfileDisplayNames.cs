namespace Belot.WebApi;

using System.Net.Http.Headers;
using System.Net.Http.Json;
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

internal sealed class LovableProfileDisplayNameProvider : IProfileDisplayNameProvider
{
    private const int MaximumCachedProfiles = 4096;

    private const int MaximumDisplayNameLength = 40;

    private const int MaximumResponseBytes = 16 * 1024;

    private static readonly TimeSpan ProfileCacheDuration = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan MissingProfileCacheDuration = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpClient httpClient;

    private readonly ILogger<LovableProfileDisplayNameProvider> logger;

    private readonly Uri? lookupUri;

    private readonly string? sharedSecret;

    private readonly SemaphoreSlim lookupGate = new(8, 8);

    private readonly object cacheLock = new();

    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);

    public LovableProfileDisplayNameProvider(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<LovableProfileDisplayNameProvider> logger)
    {
        this.httpClient = httpClient;
        this.logger = logger;

        var configuredUri = configuration["Multiplayer:ProfileLookup:Url"];
        this.sharedSecret = configuration["MULTIPLAYER_PROFILE_LOOKUP_SECRET"];
        if (string.IsNullOrWhiteSpace(configuredUri) || string.IsNullOrWhiteSpace(this.sharedSecret))
        {
            this.logger.LogInformation(
                "Multiplayer profile lookup is not configured; seat display names will use the role-based fallback.");
            return;
        }

        if (this.sharedSecret.Length < 32
            || this.sharedSecret.Length > 4096
            || this.sharedSecret.Any(character => character <= ' ' || character > '~'))
        {
            this.logger.LogWarning(
                "Multiplayer profile lookup has an invalid server credential; seat display names will use the role-based fallback.");
            this.sharedSecret = null;
            return;
        }

        if (!Uri.TryCreate(configuredUri, UriKind.Absolute, out var lookupUri)
            || lookupUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(lookupUri.UserInfo)
            || !string.IsNullOrEmpty(lookupUri.Query)
            || !string.IsNullOrEmpty(lookupUri.Fragment))
        {
            this.logger.LogWarning(
                "Multiplayer profile lookup requires a valid HTTPS endpoint URL; seat display names will use the role-based fallback.");
            this.sharedSecret = null;
            return;
        }

        this.lookupUri = lookupUri;
    }

    public async Task<IReadOnlyDictionary<string, string?>> ResolveAsync(
        IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var requestedUsers = userIds
            .Select(userId => Guid.TryParseExact(userId, "D", out var parsedId)
                ? (UserId: userId, CacheKey: parsedId.ToString("D"))
                : default)
            .Where(user => user.UserId is not null)
            .DistinctBy(user => user.CacheKey, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        if (requestedUsers.Length == 0)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string?>(requestedUsers.Length, StringComparer.Ordinal);
        var uncachedUsers = new List<(string UserId, string CacheKey)>(requestedUsers.Length);
        foreach (var user in requestedUsers)
        {
            if (this.TryGetCached(user.CacheKey, out var cachedName))
            {
                result[user.UserId] = cachedName;
            }
            else
            {
                uncachedUsers.Add(user);
            }
        }

        if (uncachedUsers.Count == 0)
        {
            return result;
        }

        if (this.lookupUri is null || this.sharedSecret is null)
        {
            foreach (var user in uncachedUsers)
            {
                result[user.UserId] = null;
            }

            return result;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LookupTimeout);
        var gateAcquired = false;
        try
        {
            await this.lookupGate.WaitAsync(timeout.Token);
            gateAcquired = true;

            var usersToLookup = new List<(string UserId, string CacheKey)>(uncachedUsers.Count);
            foreach (var user in uncachedUsers)
            {
                if (this.TryGetCached(user.CacheKey, out var cachedName))
                {
                    result[user.UserId] = cachedName;
                }
                else
                {
                    usersToLookup.Add(user);
                }
            }

            if (usersToLookup.Count > 0)
            {
                var lookup = await this.LookupAsync(usersToLookup, timeout.Token);
                foreach (var user in usersToLookup)
                {
                    lookup.Names.TryGetValue(user.CacheKey, out var displayName);
                    this.SetCached(
                        user.CacheKey,
                        displayName,
                        lookup.Success
                            ? displayName is null ? MissingProfileCacheDuration : ProfileCacheDuration
                            : FailureCacheDuration);
                    result[user.UserId] = displayName;
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            this.logger.LogWarning(
                "Multiplayer profile lookup timed out; seat display names will use the role-based fallback.");
            foreach (var user in uncachedUsers)
            {
                this.SetCached(user.CacheKey, null, FailureCacheDuration);
                result[user.UserId] = null;
            }

            return result;
        }
        finally
        {
            if (gateAcquired)
            {
                this.lookupGate.Release();
            }
        }
    }

    private async Task<ProfileLookupResult> LookupAsync(
        IReadOnlyCollection<(string UserId, string CacheKey)> users,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, this.lookupUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.sharedSecret);
            request.Content = JsonContent.Create(new ProfileLookupRequest(users.Select(user => user.UserId).ToArray()));

            using var response = await this.httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                this.logger.LogWarning(
                    "Multiplayer profile lookup returned HTTP {StatusCode}; seat display names will use the role-based fallback.",
                    (int)response.StatusCode);
                return FailedLookup();
            }

            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            {
                this.logger.LogWarning(
                    "Multiplayer profile lookup returned an oversized response; seat display names will use the role-based fallback.");
                return FailedLookup();
            }

            var responseBytes = await ReadBoundedResponseAsync(response.Content, cancellationToken);
            if (responseBytes is null)
            {
                this.logger.LogWarning(
                    "Multiplayer profile lookup returned an oversized response; seat display names will use the role-based fallback.");
                return FailedLookup();
            }

            using var document = JsonDocument.Parse(responseBytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("profiles", out var profiles)
                || profiles.ValueKind != JsonValueKind.Array
                || profiles.GetArrayLength() > 4)
            {
                this.logger.LogWarning(
                    "Multiplayer profile lookup returned an invalid response; seat display names will use the role-based fallback.");
                return FailedLookup();
            }

            var requestedIds = users
                .Select(user => user.CacheKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in profiles.EnumerateArray())
            {
                if (profile.ValueKind != JsonValueKind.Object
                    || !profile.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String
                    || !Guid.TryParseExact(idElement.GetString(), "D", out var parsedId))
                {
                    continue;
                }

                var cacheKey = parsedId.ToString("D");
                if (!requestedIds.Contains(cacheKey)
                    || !profile.TryGetProperty("displayName", out var displayNameElement)
                    || (displayNameElement.ValueKind != JsonValueKind.String
                        && displayNameElement.ValueKind != JsonValueKind.Null))
                {
                    continue;
                }

                names[cacheKey] = Normalize(
                    displayNameElement.ValueKind == JsonValueKind.String ? displayNameElement.GetString() : null);
            }

            return new ProfileLookupResult(true, names);
        }
        catch (HttpRequestException)
        {
            this.logger.LogWarning(
                "Multiplayer profile lookup failed; seat display names will use the role-based fallback.");
            return FailedLookup();
        }
        catch (IOException)
        {
            this.logger.LogWarning(
                "Multiplayer profile lookup failed while reading its response; seat display names will use the role-based fallback.");
            return FailedLookup();
        }
        catch (JsonException)
        {
            this.logger.LogWarning(
                "Multiplayer profile lookup returned invalid JSON; seat display names will use the role-based fallback.");
            return FailedLookup();
        }
    }

    private static ProfileLookupResult FailedLookup() =>
        new(false, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

    private static async Task<byte[]?> ReadBoundedResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var responseStream = await content.ReadAsStreamAsync(cancellationToken);
        using var responseBuffer = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var bytesRead = await responseStream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                return responseBuffer.ToArray();
            }

            if (responseBuffer.Length + bytesRead > MaximumResponseBytes)
            {
                return null;
            }

            responseBuffer.Write(buffer, 0, bytesRead);
        }
    }

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

    private sealed record ProfileLookupRequest(IReadOnlyCollection<string> UserIds);

    private sealed record ProfileLookupResult(bool Success, Dictionary<string, string?> Names);

    private sealed record CacheEntry(string? DisplayName, DateTimeOffset ExpiresAt);
}
