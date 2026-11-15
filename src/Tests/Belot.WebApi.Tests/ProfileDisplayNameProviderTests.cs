namespace Belot.WebApi.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Belot.WebApi;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

public sealed class ProfileDisplayNameProviderTests
{
    [Fact]
    public async Task SupabaseLookupUsesOnlyTheRequestedProfileFieldsAndCachesNormalizedName()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            requestCount++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/rest/v1/profiles", request.RequestUri!.AbsolutePath);
            Assert.Contains("id=eq.authenticated-user", Uri.UnescapeDataString(request.RequestUri.Query), StringComparison.Ordinal);
            Assert.Contains("select=id,username,display_name", Uri.UnescapeDataString(request.RequestUri.Query), StringComparison.Ordinal);
            Assert.Equal("server-only-key", request.Headers.GetValues("apikey").Single());
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "server-only-key"), request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":"authenticated-user","username":"  username\n here\u0001 ","display_name":"ignored","email":"private@example.test"}]""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }));
        var provider = this.CreateProvider(client, configured: true);

        var first = await provider.ResolveAsync(new[] { "authenticated-user" }, CancellationToken.None);
        var second = await provider.ResolveAsync(new[] { "authenticated-user" }, CancellationToken.None);

        Assert.Equal("username here", first["authenticated-user"]);
        Assert.Equal(first["authenticated-user"], second["authenticated-user"]);
        Assert.Equal(1, requestCount);
        Assert.DoesNotContain("private@example.test", first.Values.OfType<string>());
    }

    [Fact]
    public async Task EmptyUsernameFallsBackToBoundedDisplayName()
    {
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":"authenticated-user","username":"  \n ","display_name":"01234567890123456789012345678901234567890123456789"}]""",
                    Encoding.UTF8,
                    "application/json"),
            })));
        var provider = this.CreateProvider(client, configured: true);

        var names = await provider.ResolveAsync(new[] { "authenticated-user" }, CancellationToken.None);

        Assert.Equal("0123456789012345678901234567890123456789", names["authenticated-user"]);
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
