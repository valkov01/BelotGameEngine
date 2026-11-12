using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

using Belot.WebApi;

var builder = WebApplication.CreateBuilder(args);
var rateLimit = Math.Clamp(builder.Configuration.GetValue("RateLimit:PermitLimit", 120), 1, 10000);
var rateWindowSeconds = Math.Clamp(builder.Configuration.GetValue("RateLimit:WindowSeconds", 60), 1, 3600);
var supabaseAuthority = builder.Configuration["Authentication:Supabase:Authority"];
var supabaseIssuer = builder.Configuration["Authentication:Supabase:Issuer"] ?? supabaseAuthority;
var supabaseAudience = builder.Configuration["Authentication:Supabase:Audience"] ?? "authenticated";
var supabaseJwtSecret = builder.Configuration["Authentication:Supabase:JwtSecret"];
var postgresConnectionString = builder.Configuration.GetConnectionString("Multiplayer")
    ?? builder.Configuration["Multiplayer:PostgresConnectionString"];
var validSupabaseAlgorithms = string.IsNullOrEmpty(supabaseJwtSecret)
    ? new[] { SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256 }
    : new[] { SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256, SecurityAlgorithms.HmacSha256 };

if (!string.IsNullOrEmpty(supabaseJwtSecret) && Encoding.UTF8.GetByteCount(supabaseJwtSecret) < 32)
{
    throw new InvalidOperationException("Authentication:Supabase:JwtSecret must contain at least 32 UTF-8 bytes.");
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = string.IsNullOrWhiteSpace(supabaseAuthority) ? null : supabaseAuthority;
        options.Audience = supabaseAudience;
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = supabaseIssuer,
            ValidateAudience = true,
            ValidAudience = supabaseAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = validSupabaseAlgorithms,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        if (!string.IsNullOrEmpty(supabaseJwtSecret))
        {
            options.TokenValidationParameters.IssuerSigningKey =
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(supabaseJwtSecret));
        }
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Multiplayer", policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim("sub"));
});

builder.Services.AddCors(options =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    options.AddDefaultPolicy(policy =>
    {
        if (origins.Length > 0)
        {
            policy
                .WithOrigins(origins)
                .WithMethods("GET", "POST", "OPTIONS")
                .WithHeaders("Authorization", "Content-Type", "Accept", "X-Belot-Debug-Key");
        }
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimit,
                Window = TimeSpan.FromSeconds(rateWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});

builder.Services.AddSingleton<GameStore>();
builder.Services.AddSingleton<GameService>();
builder.Services.AddSingleton<DebugHandsAccess>();
if (string.IsNullOrWhiteSpace(postgresConnectionString))
{
    builder.Services.AddSingleton<IMultiplayerRoomRepository, InMemoryMultiplayerRoomRepository>();
}
else
{
    builder.Services.AddSingleton(Npgsql.NpgsqlDataSource.Create(postgresConnectionString));
    builder.Services.AddSingleton<IMultiplayerRoomRepository, PostgresMultiplayerRoomRepository>();
}

builder.Services.AddSingleton<MultiplayerRoomService>();
builder.Services.AddSingleton<IRoomMembershipService>(services => services.GetRequiredService<MultiplayerRoomService>());
builder.Services.AddSingleton<ISeatAssignmentService>(services => services.GetRequiredService<MultiplayerRoomService>());

var app = builder.Build();

if (string.IsNullOrWhiteSpace(postgresConnectionString))
{
    app.Logger.LogWarning("Multiplayer lobbies are using process-local storage. Configure Multiplayer:PostgresConnectionString for durable lobby persistence.");
}

app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new HealthResponse("ok")));
app.MapGet("/api/bots", () => Results.Ok(BotProfiles.All));
app.MapMultiplayerRoomEndpoints();

app.MapPost("/api/games", (CreateGameRequest request, GameService games) =>
{
    var result = games.Create(request);
    return result.Error is not null
        ? Results.BadRequest(new ApiError(result.Error))
        : result.Unavailable
            ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
            : Results.Created($"/api/games/{result.Game!.GameId}", result.Game);
});

app.MapGet("/api/games/{gameId}", async (string gameId, GameService games, CancellationToken cancellationToken) =>
{
    var result = await games.GetAsync(gameId, cancellationToken);
    return result is null ? Results.NotFound(new ApiError("Game not found.")) : Results.Ok(result);
});

app.MapGet("/api/games/{gameId}/debug/hands", async (
    string gameId,
    HttpContext context,
    GameService games,
    DebugHandsAccess debugAccess,
    CancellationToken cancellationToken) =>
{
    if (!debugAccess.IsEnabled)
    {
        return Results.NotFound(new ApiError("Not found."));
    }

    if (!debugAccess.IsAuthorized(context.Request.Headers["X-Belot-Debug-Key"].ToString()))
    {
        return Results.Unauthorized();
    }

    var result = await games.GetDebugHandsAsync(gameId, cancellationToken);
    return result is null ? Results.NotFound(new ApiError("Game not found.")) : Results.Ok(result);
});

app.MapPost("/api/games/{gameId}/actions", async (string gameId, GameActionRequest request, GameService games, CancellationToken cancellationToken) =>
{
    var result = await games.ActAsync(gameId, request, cancellationToken);
    return result.Status switch
    {
        GameActionStatus.NotFound => Results.NotFound(new ApiError("Game not found.")),
        GameActionStatus.Invalid => Results.BadRequest(new ApiError(result.Error!)),
        GameActionStatus.Conflict => Results.Conflict(new ApiError(result.Error!)),
        _ => Results.Ok(result.Response),
    };
});

app.Run();

public partial class Program
{
}

internal sealed class DebugHandsAccess
{
    private readonly bool enabled;

    private readonly byte[]? accessKeyHash;

    public DebugHandsAccess(IConfiguration configuration)
    {
        this.enabled = configuration.GetValue("Debug:RevealHandsEnabled", false);
        var accessKey = configuration["Debug:AccessKey"];
        this.accessKeyHash = string.IsNullOrEmpty(accessKey)
            ? null
            : SHA256.HashData(Encoding.UTF8.GetBytes(accessKey));
    }

    public bool IsEnabled => this.enabled && this.accessKeyHash is not null;

    public bool IsAuthorized(string? providedKey)
    {
        if (!this.IsEnabled || string.IsNullOrEmpty(providedKey))
        {
            return false;
        }

        var configuredHash = this.accessKeyHash;
        if (configuredHash is null)
        {
            return false;
        }

        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedKey));
        return CryptographicOperations.FixedTimeEquals(configuredHash, providedHash);
    }
}
