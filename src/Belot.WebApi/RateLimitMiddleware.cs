namespace Belot.WebApi;

using Microsoft.AspNetCore.Http;

internal sealed class HybridIpRateLimitMiddleware
{
    private readonly RequestDelegate next;

    public HybridIpRateLimitMiddleware(RequestDelegate next) => this.next = next;

    public async Task InvokeAsync(HttpContext context, HybridRateLimiter limiter)
    {
        using var attempt = limiter.AcquireAggregate(
            context.Connection.RemoteIpAddress?.ToString());
        if (!attempt.Accepted)
        {
            await RateLimitDiagnostics.RejectAsync(
                context,
                attempt.Policy!,
                attempt.PartitionHash!,
                attempt.RetryAfter);
            return;
        }

        await this.next(context);
    }
}

internal sealed class TeamMatchRateLimitMiddleware
{
    private readonly RequestDelegate next;

    public TeamMatchRateLimitMiddleware(RequestDelegate next) => this.next = next;

    public async Task InvokeAsync(HttpContext context, HybridRateLimiter limiter)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<TeamMatchRateLimitMetadata>();
        if (metadata is null)
        {
            await this.next(context);
            return;
        }

        var userId = context.User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userId) || context.User.Identity?.IsAuthenticated != true)
        {
            await this.next(context);
            return;
        }

        using var attempt = limiter.AcquireAuthenticated(userId, metadata.Kind);
        if (!attempt.Accepted)
        {
            await RateLimitDiagnostics.RejectAsync(
                context,
                attempt.Policy!,
                attempt.PartitionHash!,
                attempt.RetryAfter);
            return;
        }

        await this.next(context);
    }
}
