namespace Belot.WebApi.Tests;

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string TestIssuer = "https://supabase.test/auth/v1";

    public const string TestAudience = "authenticated";

    public const string TestJwtSecret = "testing-only-secret-key-with-at-least-thirty-two-bytes";

    private static readonly RSA TestRsa = RSA.Create(2048);

    private static readonly RsaSecurityKey TestSigningKey = new(TestRsa) { KeyId = "isolated-integration-test-key" };

    private static readonly ECDsa TestEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static readonly ECDsaSecurityKey TestEcSigningKey = new(TestEcdsa) { KeyId = "isolated-ec-test-key" };

    private static readonly SymmetricSecurityKey TestHmacSigningKey = new(Encoding.UTF8.GetBytes(TestJwtSecret));

    private readonly bool revealHandsEnabled;

    private readonly string? debugAccessKey;

    private readonly bool useEcdsaSigningKey;

    private readonly string? jwtSecret;

    private readonly string? multiplayerConnectionString;

    private readonly int? realtimeEventsPerGame;

    private readonly int? realtimeEventsPerLobby;

    private readonly bool failRealtimeGameDelivery;

    private readonly string? corsAllowedOrigin;

    private readonly IProfileDisplayNameProvider? profileDisplayNameProvider;

    private readonly bool useProductionRateLimit;

    private readonly bool hybridRateLimitEnabled;

    private readonly int hybridIpPermitLimit;

    private readonly int hybridReadPermitLimit;

    private readonly int hybridMutationPermitLimit;

    private readonly int hybridPerUserConcurrencyLimit;

    private readonly int hybridAggregateConcurrencyLimit;

    public ApiFactory(
        bool revealHandsEnabled = false,
        string? debugAccessKey = null,
        bool useEcdsaSigningKey = false,
        string? jwtSecret = null,
        string? multiplayerConnectionString = null,
        int? realtimeEventsPerGame = null,
        int? realtimeEventsPerLobby = null,
        bool failRealtimeGameDelivery = false,
        string? corsAllowedOrigin = null,
        IProfileDisplayNameProvider? profileDisplayNameProvider = null,
        bool useProductionRateLimit = false,
        bool hybridRateLimitEnabled = false,
        int hybridIpPermitLimit = 600,
        int hybridReadPermitLimit = 90,
        int hybridMutationPermitLimit = 20,
        int hybridPerUserConcurrencyLimit = 4,
        int hybridAggregateConcurrencyLimit = 32)
    {
        this.revealHandsEnabled = revealHandsEnabled;
        this.debugAccessKey = debugAccessKey;
        this.useEcdsaSigningKey = useEcdsaSigningKey;
        this.jwtSecret = jwtSecret;
        this.multiplayerConnectionString = multiplayerConnectionString;
        this.realtimeEventsPerGame = realtimeEventsPerGame;
        this.realtimeEventsPerLobby = realtimeEventsPerLobby;
        this.failRealtimeGameDelivery = failRealtimeGameDelivery;
        this.corsAllowedOrigin = corsAllowedOrigin;
        this.profileDisplayNameProvider = profileDisplayNameProvider;
        this.useProductionRateLimit = useProductionRateLimit;
        this.hybridRateLimitEnabled = hybridRateLimitEnabled;
        this.hybridIpPermitLimit = hybridIpPermitLimit;
        this.hybridReadPermitLimit = hybridReadPermitLimit;
        this.hybridMutationPermitLimit = hybridMutationPermitLimit;
        this.hybridPerUserConcurrencyLimit = hybridPerUserConcurrencyLimit;
        this.hybridAggregateConcurrencyLimit = hybridAggregateConcurrencyLimit;
    }

    public Action<string>? LobbyLockWaiting { get; set; }

    public Func<string, Task>? LobbyLockEntered { get; set; }

    public static string CreateAccessToken(
        string userId,
        string? issuer = null,
        string? audience = null,
        DateTime? expires = null,
        string algorithm = "RS256")
    {
        var now = DateTime.UtcNow;
        var notBefore = expires.HasValue && expires.Value <= now
            ? expires.Value.AddMinutes(-1)
            : now.AddMinutes(-1);
        var (key, signingAlgorithm) = algorithm switch
        {
            "RS256" => ((SecurityKey)TestSigningKey, SecurityAlgorithms.RsaSha256),
            "ES256" => ((SecurityKey)TestEcSigningKey, SecurityAlgorithms.EcdsaSha256),
            "HS256" => ((SecurityKey)TestHmacSigningKey, SecurityAlgorithms.HmacSha256),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
        var token = new JwtSecurityToken(
            issuer ?? TestIssuer,
            audience ?? TestAudience,
            new[] { new Claim("sub", userId) },
            notBefore,
            expires ?? now.AddMinutes(5),
            new SigningCredentials(key, signingAlgorithm));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string path, string accessToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Debug:RevealHandsEnabled"] = this.revealHandsEnabled.ToString(),
                ["Debug:AccessKey"] = this.debugAccessKey,
                ["Authentication:Supabase:Issuer"] = TestIssuer,
                ["Authentication:Supabase:Audience"] = TestAudience,
                ["Authentication:Supabase:JwtSecret"] = this.jwtSecret,
                ["Multiplayer:PostgresConnectionString"] = this.multiplayerConnectionString,
                ["Multiplayer:Realtime:EventsPerGame"] = this.realtimeEventsPerGame?.ToString(),
                ["Multiplayer:Realtime:EventsPerLobby"] = this.realtimeEventsPerLobby?.ToString(),
                ["RateLimit:Hybrid:Enabled"] = this.hybridRateLimitEnabled.ToString(),
                ["RateLimit:Hybrid:IpPermitLimit"] = this.hybridIpPermitLimit.ToString(),
                ["RateLimit:Hybrid:TeamMatchReadPermitLimit"] = this.hybridReadPermitLimit.ToString(),
                ["RateLimit:Hybrid:TeamMatchMutationPermitLimit"] = this.hybridMutationPermitLimit.ToString(),
                ["RateLimit:Hybrid:PerUserConcurrencyLimit"] = this.hybridPerUserConcurrencyLimit.ToString(),
                ["RateLimit:Hybrid:AggregateConcurrencyLimit"] = this.hybridAggregateConcurrencyLimit.ToString(),
            };
            if (!this.useProductionRateLimit)
            {
                settings["RateLimit:PermitLimit"] = "10000";
            }

            if (this.corsAllowedOrigin is not null)
            {
                settings["Cors:AllowedOrigins:0"] = this.corsAllowedOrigin;
            }

            configuration.AddInMemoryCollection(settings);
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, TestClientIpStartupFilter>();
            if (!this.useProductionRateLimit)
            {
                services.PostConfigure<RateLimiterOptions>(options =>
                {
                    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            context.Connection.RemoteIpAddress?.ToString() ?? "test",
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = 100000,
                                Window = TimeSpan.FromMinutes(1),
                                QueueLimit = 0,
                                AutoReplenishment = true,
                            }));
                });
            }

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = TestIssuer,
                    ValidateAudience = true,
                    ValidAudience = TestAudience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = this.jwtSecret is not null
                        ? TestHmacSigningKey
                        : this.useEcdsaSigningKey
                            ? TestEcSigningKey
                            : TestSigningKey,
                    ValidAlgorithms = this.jwtSecret is not null
                        ? new[] { SecurityAlgorithms.HmacSha256 }
                        : this.useEcdsaSigningKey
                            ? new[] { SecurityAlgorithms.EcdsaSha256 }
                            : new[] { SecurityAlgorithms.RsaSha256 },
                    ClockSkew = TimeSpan.Zero,
                };
            });

            services.AddSingleton<IMultiplayerRealtimePublisher>(provider =>
                new TestRealtimePublisher(
                    this,
                    provider.GetRequiredService<MultiplayerRealtimePublisher>(),
                    this.failRealtimeGameDelivery));
            if (this.profileDisplayNameProvider is not null)
            {
                services.AddSingleton(this.profileDisplayNameProvider);
            }
        });
    }

    private sealed class TestClientIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, continuation) =>
                {
                    if (context.Request.Headers.TryGetValue("X-Test-Client-Ip", out var address)
                        && System.Net.IPAddress.TryParse(address.ToString(), out var parsed))
                    {
                        context.Connection.RemoteIpAddress = parsed;
                    }

                    await continuation(context);
                });
                next(app);
            };
    }

    private sealed class TestRealtimePublisher : IMultiplayerRealtimePublisher
    {
        private readonly ApiFactory factory;

        private readonly IMultiplayerRealtimePublisher publisher;

        private readonly bool failRealtimeGameDelivery;

        public TestRealtimePublisher(
            ApiFactory factory,
            IMultiplayerRealtimePublisher publisher,
            bool failRealtimeGameDelivery)
        {
            this.factory = factory;
            this.publisher = publisher;
            this.failRealtimeGameDelivery = failRealtimeGameDelivery;
        }

        public async Task<T> WithLobbyLockAsync<T>(
            string roomId,
            Func<Task<T>> operation,
            CancellationToken cancellationToken)
        {
            this.factory.LobbyLockWaiting?.Invoke(roomId);
            return await this.publisher.WithLobbyLockAsync(
                roomId,
                async () =>
                {
                    if (this.factory.LobbyLockEntered is { } entered)
                    {
                        await entered(roomId);
                    }

                    return await operation();
                },
                cancellationToken);
        }

        public MultiplayerEventCursor GetGameCursor(string gameId, long revision) =>
            this.publisher.GetGameCursor(gameId, revision);

        public MultiplayerEventCursor GetLobbyCursor(string roomId) =>
            this.publisher.GetLobbyCursor(roomId);

        public Task PublishGameEventsAsync(
            string gameId,
            long revision,
            IReadOnlyList<PublicGameEventDto> events,
            CancellationToken cancellationToken) =>
            this.failRealtimeGameDelivery
                ? Task.FromException(new IOException("Simulated SignalR delivery failure."))
                : this.publisher.PublishGameEventsAsync(gameId, revision, events, cancellationToken);

        public Task PublishLobbyChangeAsync(
            MultiplayerRoom room,
            string type,
            Belot.Engine.Players.PlayerPosition? seat,
            CancellationToken cancellationToken) =>
            this.publisher.PublishLobbyChangeAsync(room, type, seat, cancellationToken);

        public Task<MultiplayerRealtimeSubscriptionResult> SubscribeGameAsync(
            string connectionId,
            string gameId,
            long afterRevision,
            int afterSequence,
            long currentRevision,
            CancellationToken cancellationToken) =>
            this.publisher.SubscribeGameAsync(
                connectionId,
                gameId,
                afterRevision,
                afterSequence,
                currentRevision,
                cancellationToken);

        public Task<MultiplayerRealtimeSubscriptionResult> SubscribeRoomAsync(
            string connectionId,
            string roomId,
            long afterRevision,
            int afterSequence,
            CancellationToken cancellationToken) =>
            this.publisher.SubscribeRoomAsync(
                connectionId,
                roomId,
                afterRevision,
                afterSequence,
                cancellationToken);
    }
}
