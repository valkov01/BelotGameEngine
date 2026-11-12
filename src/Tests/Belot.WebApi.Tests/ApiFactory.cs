namespace Belot.WebApi.Tests;

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string TestIssuer = "https://supabase.test/auth/v1";

    public const string TestAudience = "authenticated";

    private static readonly RSA TestRsa = RSA.Create(2048);

    private static readonly RsaSecurityKey TestSigningKey = new(TestRsa) { KeyId = "isolated-integration-test-key" };

    private readonly bool revealHandsEnabled;

    private readonly string? debugAccessKey;

    public ApiFactory(bool revealHandsEnabled = false, string? debugAccessKey = null)
    {
        this.revealHandsEnabled = revealHandsEnabled;
        this.debugAccessKey = debugAccessKey;
    }

    public static string CreateAccessToken(
        string userId,
        string? issuer = null,
        string? audience = null,
        DateTime? expires = null)
    {
        var now = DateTime.UtcNow;
        var notBefore = expires.HasValue && expires.Value <= now
            ? expires.Value.AddMinutes(-1)
            : now.AddMinutes(-1);
        var token = new JwtSecurityToken(
            issuer ?? TestIssuer,
            audience ?? TestAudience,
            new[] { new Claim("sub", userId) },
            notBefore,
            expires ?? now.AddMinutes(5),
            new SigningCredentials(TestSigningKey, SecurityAlgorithms.RsaSha256));
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
            }));

        builder.ConfigureTestServices(services =>
        {
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
                    IssuerSigningKey = TestSigningKey,
                    ClockSkew = TimeSpan.Zero,
                };
            });
        });
    }
}
