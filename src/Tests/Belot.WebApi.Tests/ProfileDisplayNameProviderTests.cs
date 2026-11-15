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
    [Fact]
    public async Task SupabaseLookupUsesOnlyDisplayNameAndCachesNormalizedPublicName()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            requestCount++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/rest/v1/profiles", request.RequestUri!.AbsolutePath);
            Assert.Equal(
                "id=eq.authenticated-user&select=id,display_name",
                Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?')));
            Assert.Equal("server-only-key", request.Headers.GetValues("apikey").Single());
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "server-only-key"), request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":"authenticated-user","username":"login-username","display_name":"  Public\n Name\u0001 ","email":"private@example.test"}]""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }));
        var provider = this.CreateProvider(client, configured: true);

        var first = await provider.ResolveAsync(new[] { "authenticated-user" }, CancellationToken.None);
        var second = await provider.ResolveAsync(new[] { "authenticated-user" }, CancellationToken.None);

        Assert.Equal("Public Name", first["authenticated-user"]);
        Assert.Equal(first["authenticated-user"], second["authenticated-user"]);
        Assert.Equal(1, requestCount);
        Assert.DoesNotContain("private@example.test", first.Values.OfType<string>());
    }

    [Fact]
    public async Task DisplayNameIsBounded()
    {
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":"authenticated-user","username":"login-username","display_name":"01234567890123456789012345678901234567890123456789"}]""",
                    Encoding.UTF8,
                    "application/json"),
            })));
        var provider = this.CreateProvider(client, configured: true);

        var names = await provider.ResolveAsync(new[] { "authenticated-user" }, CancellationToken.None);

        Assert.Equal("0123456789012345678901234567890123456789", names["authenticated-user"]);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" \t\r\n ", true)]
    [InlineData(null, false)]
    public async Task BlankOrMissingDisplayNameDoesNotFallBackToLoginUsername(
        string? displayName,
        bool includeDisplayName)
    {
        const string userId = "authenticated-user";
        var profileJson = includeDisplayName
            ? JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = userId,
                    username = "login-username",
                    display_name = displayName,
                },
            })
            : JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = userId,
                    username = "login-username",
                },
            });
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    profileJson,
                    Encoding.UTF8,
                    "application/json"),
            })));
        var provider = this.CreateProvider(client, configured: true);

        var names = await provider.ResolveAsync(new[] { userId }, CancellationToken.None);

        Assert.Null(names[userId]);
    }

    [Fact]
    public async Task SupabaseFailuresAndMissingConfigurationReturnNullNames()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));
        var configuredProvider = this.CreateProvider(client, configured: true);
        var failed = await configuredProvider.ResolveAsync(new[] { "authenticated-user" }, CancellationToken.None);
        Assert.Null(failed["authenticated-user"]);
        Assert.Equal(1, requestCount);

        var unconfiguredProvider = this.CreateProvider(client, configured: false);
        var unconfigured = await unconfiguredProvider.ResolveAsync(new[] { "authenticated-user" }, CancellationToken.None);
        Assert.Empty(unconfigured);
        Assert.Equal(1, requestCount);
    }

    private SupabaseProfileDisplayNameProvider CreateProvider(HttpClient client, bool configured)
    {
        var settings = new Dictionary<string, string?>();
        if (configured)
        {
            settings["Supabase:Url"] = "https://project.supabase.co";
            settings["Supabase:ServiceRoleKey"] = "server-only-key";
        }

        return new SupabaseProfileDisplayNameProvider(
            new StubHttpClientFactory(client),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<SupabaseProfileDisplayNameProvider>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            sendAsync(request, cancellationToken);
    }
}
