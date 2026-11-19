namespace Belot.WebApi;

using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

internal static class RateLimitDiagnostics
{
    internal const string SourceHeader = "X-Belot-RateLimit-Source";

    internal const string PolicyHeader = "X-Belot-RateLimit-Policy";

    public static Task RejectAsync(
        HttpContext context,
        string policy,
        string partitionHash,
        TimeSpan retryAfter)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers[SourceHeader] = "application";
        response.Headers[PolicyHeader] = policy;
        response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

        var route = context.GetEndpoint() is RouteEndpoint routeEndpoint
            ? routeEndpoint.RoutePattern.RawText
            : "unresolved";
        context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Belot.WebApi.RateLimit")
            .LogWarning(
                "Application rate limit rejected request. Policy={Policy} Route={Route} TraceId={TraceId} PartitionHash={PartitionHash}",
                policy,
                route,
                context.TraceIdentifier,
                partitionHash);
        return Task.CompletedTask;
    }

    public static string HashPartition(string? value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "unknown"));
        return Convert.ToHexString(digest.AsSpan(0, 8));
    }
}
