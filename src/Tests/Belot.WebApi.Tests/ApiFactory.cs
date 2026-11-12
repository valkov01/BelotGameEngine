namespace Belot.WebApi.Tests;

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication.JwtBearer;
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

    public ApiFactory(
        bool revealHandsEnabled = false,
        string? debugAccessKey = null,
        bool useEcdsaSigningKey = false,
        string? jwtSecret = null)
    {
        this.revealHandsEnabled = revealHandsEnabled;
        this.debugAccessKey = debugAccessKey;
        this.useEcdsaSigningKey = useEcdsaSigningKey;
        this.jwtSecret = jwtSecret;
    }

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
        builder.ConfigureAppConfiguration(
            (_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:PermitLimit"] = "10000",
                ["Debug:RevealHandsEnabled"] = this.revealHandsEnabled.ToString(),
                ["Debug:AccessKey"] = this.debugAccessKey,
                ["Authentication:Supabase:Issuer"] = TestIssuer,
                ["Authentication:Supabase:Audience"] = TestAudience,
                ["Authentication:Supabase:JwtSecret"] = this.jwtSecret,
            }));

        builder.ConfigureTestServices(services =>
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
        });
    }
}
