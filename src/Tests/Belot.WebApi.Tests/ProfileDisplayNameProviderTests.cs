namespace Belot.WebApi.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Belot.WebApi;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

public sealed class ProfileDisplayNameProviderTests
{
    private const string LookupUrl = "https://lovable.example.test/api/public/multiplayer-profile-lookup";

    private const string SharedSecret = "test-only-shared-secret-with-at-least-32-characters";

    private static readonly string[] UserIds =
    {
        "11111111-1111-4111-8111-111111111111",
        "22222222-2222-4222-8222-222222222222",
        "33333333-3333-4333-8333-333333333333",
        "44444444-4444-4444-8444-444444444444",
    };

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LovableLookupPostsBoundedUuidBatchAndCachesOnlyPublicDisplayNames(int count)
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHttpMessageHandler(async (request, _) =>
        {
            requestCount++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(LookupUrl, request.RequestUri!.AbsoluteUri);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", SharedSecret), request.Headers.Authorization);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);

            using var requestJson = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
            var userIds = requestJson.RootElement.GetProperty("userIds")
                .EnumerateArray()
                .Select(userId => userId.GetString())
                .ToArray();
            Assert.Equal(UserIds.Take(count), userIds);
            Assert.All(userIds, userId => Assert.True(Guid.TryParse(userId, out var parsedId)));

            var profiles = userIds.Select((userId, index) => new
            {
                id = userId,
                displayName = index == 0 ? "  Public\n Name\u0001 " : $"Player {index}",
                username = "private-login",
                email = "private@example.test",
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { profiles }),
                    Encoding.UTF8,
                    "application/json"),
            };
        }));
        var provider = this.CreateProvider(client);

        var first = await provider.ResolveAsync(UserIds.Take(count).ToArray(), CancellationToken.None);
        var second = await provider.ResolveAsync(UserIds.Take(count).ToArray(), CancellationToken.None);

        Assert.Equal(count, first.Count);
        Assert.Equal("Public Name", first[UserIds[0]]);
        Assert.Equal(first[UserIds[0]], second[UserIds[0]]);
        Assert.Equal(1, requestCount);
        var serialized = JsonSerializer.Serialize(first);
        Assert.DoesNotContain("private-login", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private@example.test", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisplayNameIsBoundedToFortyUnicodeCharacters()
    {
        using var client = this.RespondWith(
            """{"profiles":[{"id":"11111111-1111-4111-8111-111111111111","displayName":"01234567890123456789012345678901234567890123456789"}]}""");
        var provider = this.CreateProvider(client);

        var names = await provider.ResolveAsync(new[] { UserIds[0] }, CancellationToken.None);

        Assert.Equal("0123456789012345678901234567890123456789", names[UserIds[0]]);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"  \\t\\r\\n \"")]
    [InlineData("\"\"")]
    public async Task NullOrBlankDisplayNamesRemainNull(string displayNameJson)
    {
        using var client = this.RespondWith(
            $$"""{"profiles":[{"id":"{{UserIds[0]}}","displayName":{{displayNameJson}},"username":"private-login"}]}""");
        var provider = this.CreateProvider(client);

        var names = await provider.ResolveAsync(new[] { UserIds[0] }, CancellationToken.None);

        Assert.Null(names[UserIds[0]]);
    }

    [Fact]
    public async Task MissingDisplayNameDoesNotFallBackToLoginUsername()
    {
        using var client = this.RespondWith(
            $$"""{"profiles":[{"id":"{{UserIds[0]}}","username":"private-login","email":"private@example.test"}]}""");
        var provider = this.CreateProvider(client);

        var names = await provider.ResolveAsync(new[] { UserIds[0] }, CancellationToken.None);

        Assert.Null(names[UserIds[0]]);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(names), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedProfileIdsAndMalformedProfileEntriesAreIgnored()
    {
        using var client = this.RespondWith(
            $$"""{"profiles":[{"id":"{{UserIds[1]}}","displayName":"Not requested"},{"id":"not-a-uuid","displayName":"Malformed"},{"id":"{{UserIds[0]}}","displayName":"Requested"}]}""");
        var provider = this.CreateProvider(client);

        var names = await provider.ResolveAsync(new[] { UserIds[0] }, CancellationToken.None);

        Assert.Equal("Requested", names[UserIds[0]]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HttpFailuresReturnNullDisplayNames(HttpStatusCode statusCode)
    {
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode))));
        var provider = this.CreateProvider(client);

        var names = await provider.ResolveAsync(new[] { UserIds[0] }, CancellationToken.None);

        Assert.Null(names[UserIds[0]]);
    }

    [Fact]
    public async Task NetworkFailuresReturnNullAndAreBrieflyCached()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            requestCount++;
            throw new HttpRequestException("Simulated network failure.");
        }));
        var provider = this.CreateProvider(client);

        var first = await provider.ResolveAsync(new[] { UserIds[0] }, CancellationToken.None);
        var second = await provider.ResolveAsync(new[] { UserIds[0] }, CancellationToken.None);

        Assert.Null(first[UserIds[0]]);
        Assert.Null(second[UserIds[0]]);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task LookupTimeoutReturnsNullAndDoesNotEscape()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var provider = this.CreateProvider(client);

        var names = await provider.ResolveAsync(new[] { UserIds[0] }, CancellationToken.None);

        Assert.Null(names[UserIds[0]]);
    }

    [Theory]
    [InlineData(null, SharedSecret)]
    [InlineData(LookupUrl, null)]
    [InlineData("http://lovable.example.test/api/public/multiplayer-profile-lookup", SharedSecret)]
    [InlineData("https://user:password@lovable.example.test/lookup", SharedSecret)]
    [InlineData("https://lovable.example.test/lookup?secret=value", SharedSecret)]
    [InlineData(LookupUrl, "short")]
    public async Task MissingOrInvalidConfigurationDisablesLookup(string? url, string? secret)
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var provider = this.CreateProvider(client, url, secret);

        var names = await provider.ResolveAsync(new[] { UserIds[0] }, CancellationToken.None);

        Assert.Null(names[UserIds[0]]);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task NonUuidIdsAreNeverSentToLookupEndpoint()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var provider = this.CreateProvider(client);

        var names = await provider.ResolveAsync(new[] { "client-supplied-user-id" }, CancellationToken.None);

        Assert.Empty(names);
        Assert.Equal(0, requestCount);
    }

    private IProfileDisplayNameProvider CreateProvider(
        HttpClient client,
        string? url = LookupUrl,
        string? secret = SharedSecret)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Multiplayer:ProfileLookup:Url"] = url,
            ["MULTIPLAYER_PROFILE_LOOKUP_SECRET"] = secret,
        };
        return new LovableProfileDisplayNameProvider(
            client,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<LovableProfileDisplayNameProvider>.Instance);
    }

    private HttpClient RespondWith(string content) =>
        new(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            })));

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            sendAsync(request, cancellationToken);
    }
}
