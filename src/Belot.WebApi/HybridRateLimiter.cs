namespace Belot.WebApi;

using System.Collections.Concurrent;

using Microsoft.Extensions.Configuration;

internal sealed class HybridRateLimiter
{
    private readonly FixedWindowQuota ipQuota;

    private readonly FixedWindowQuota readQuota;

    private readonly FixedWindowQuota mutationQuota;

    private readonly ConcurrentQuota userConcurrency;

    private readonly SemaphoreSlim aggregateConcurrency;

    public HybridRateLimiter(IConfiguration configuration)
    {
        var options = configuration.GetSection(HybridRateLimitOptions.SectionName)
            .Get<HybridRateLimitOptions>() ?? new HybridRateLimitOptions();
        var window = TimeSpan.FromSeconds(Math.Clamp(options.WindowSeconds, 1, 3600));
        this.ipQuota = new FixedWindowQuota(
            Math.Clamp(options.IpPermitLimit, 1, 100000),
            window);
        this.readQuota = new FixedWindowQuota(
            Math.Clamp(options.TeamMatchReadPermitLimit, 1, 10000),
            window);
        this.mutationQuota = new FixedWindowQuota(
            Math.Clamp(options.TeamMatchMutationPermitLimit, 1, 10000),
            window);
        this.userConcurrency = new ConcurrentQuota(
            Math.Clamp(options.PerUserConcurrencyLimit, 1, 100),
            window);
        this.aggregateConcurrency = new SemaphoreSlim(
            Math.Clamp(options.AggregateConcurrencyLimit, 1, 10000),
            Math.Clamp(options.AggregateConcurrencyLimit, 1, 10000));
    }

    public RateLimitAttempt AcquireAggregate(string? ipAddress)
    {
        var partition = RateLimitDiagnostics.HashPartition(ipAddress ?? "unknown-ip");
        if (!this.ipQuota.TryAcquire(partition, out var retryAfter))
        {
            return RateLimitAttempt.Rejected("hybrid-ip", partition, retryAfter);
        }

        return this.aggregateConcurrency.Wait(0)
            ? RateLimitAttempt.CreateAccepted(this.aggregateConcurrency, 1)
            : RateLimitAttempt.Rejected("hybrid-aggregate-concurrency", "aggregate", TimeSpan.FromSeconds(1));
    }

    public RateLimitAttempt AcquireAuthenticated(
        string userId,
        TeamMatchRateLimitKind kind)
    {
        var partition = RateLimitDiagnostics.HashPartition(userId);
        var quota = kind == TeamMatchRateLimitKind.Read ? this.readQuota : this.mutationQuota;
        var policy = kind == TeamMatchRateLimitKind.Read
            ? "team-match-read-user"
            : "team-match-mutation-user";
        if (!quota.TryAcquire(partition, out var retryAfter))
        {
            return RateLimitAttempt.Rejected(policy, partition, retryAfter);
        }

        if (!this.userConcurrency.TryAcquire(partition, out var release))
        {
            return RateLimitAttempt.Rejected(
                "team-match-user-concurrency",
                partition,
                TimeSpan.FromSeconds(1));
        }

        return RateLimitAttempt.CreateAccepted(release!);
    }

    private sealed class FixedWindowQuota
    {
        private readonly ConcurrentDictionary<string, Window> windows = new(StringComparer.Ordinal);

        private readonly int permitLimit;

        private readonly TimeSpan window;

        private int cleanupCountdown = 1024;

        public FixedWindowQuota(int permitLimit, TimeSpan window)
        {
            this.permitLimit = permitLimit;
            this.window = window;
        }

        public bool TryAcquire(string partition, out TimeSpan retryAfter)
        {
            this.CleanUpOccasionally();
            var state = this.windows.GetOrAdd(partition, static _ => new Window());
            return state.TryAcquire(this.permitLimit, this.window, out retryAfter);
        }

        private void CleanUpOccasionally()
        {
            if (Interlocked.Decrement(ref this.cleanupCountdown) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref this.cleanupCountdown, 1024);
            var cutoff = DateTimeOffset.UtcNow - this.window - this.window;
            foreach (var entry in this.windows)
            {
                if (entry.Value.LastSeenUtc < cutoff)
                {
                    this.windows.TryRemove(entry.Key, out _);
                }
            }
        }

        private sealed class Window
        {
            private long startedTicks;

            private int permitsUsed;

            private long lastSeenUtcTicks = DateTimeOffset.UtcNow.UtcTicks;

            public DateTimeOffset LastSeenUtc =>
                new(Interlocked.Read(ref this.lastSeenUtcTicks), TimeSpan.Zero);

            public bool TryAcquire(int permitLimit, TimeSpan window, out TimeSpan retryAfter)
            {
                var now = DateTimeOffset.UtcNow;
                lock (this)
                {
                    Interlocked.Exchange(ref this.lastSeenUtcTicks, now.UtcTicks);
                    var started = new DateTimeOffset(Interlocked.Read(ref this.startedTicks), TimeSpan.Zero);
                    if (started == DateTimeOffset.MinValue || now - started >= window)
                    {
                        Interlocked.Exchange(ref this.startedTicks, now.UtcTicks);
                        this.permitsUsed = 1;
                        retryAfter = window;
                        return true;
                    }

                    if (this.permitsUsed < permitLimit)
                    {
                        this.permitsUsed++;
                        retryAfter = TimeSpan.Zero;
                        return true;
                    }

                    retryAfter = window - (now - started);
                    return false;
                }
            }
        }
    }

    private sealed class ConcurrentQuota
    {
        private readonly ConcurrentDictionary<string, ConcurrentWindow> quotas = new(StringComparer.Ordinal);

        private readonly int permitLimit;

        private readonly TimeSpan retention;

        private int cleanupCountdown = 1024;

        public ConcurrentQuota(int permitLimit, TimeSpan retention)
        {
            this.permitLimit = permitLimit;
            this.retention = retention;
        }

        public bool TryAcquire(string partition, out IDisposable? release)
        {
            this.CleanUpOccasionally();
            var state = this.quotas.GetOrAdd(
                partition,
                _ => new ConcurrentWindow(this.permitLimit));
            if (state.TryAcquire(out var lease))
            {
                release = lease;
                return true;
            }

            release = null;
            return false;
        }

        private void CleanUpOccasionally()
        {
            if (Interlocked.Decrement(ref this.cleanupCountdown) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref this.cleanupCountdown, 1024);
            var cutoff = DateTimeOffset.UtcNow - this.retention - this.retention;
            foreach (var entry in this.quotas)
            {
                if (entry.Value.IsIdleBefore(cutoff))
                {
                    this.quotas.TryRemove(entry.Key, out _);
                }
            }
        }

        private sealed class ConcurrentWindow
        {
            private readonly SemaphoreSlim semaphore;

            private long lastSeenUtcTicks = DateTimeOffset.UtcNow.UtcTicks;

            public ConcurrentWindow(int permitLimit)
            {
                this.semaphore = new SemaphoreSlim(permitLimit, permitLimit);
                this.PermitLimit = permitLimit;
            }

            private int PermitLimit { get; }

            public bool TryAcquire(out IDisposable? release)
            {
                Interlocked.Exchange(ref this.lastSeenUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                if (this.semaphore.Wait(0))
                {
                    release = new SemaphoreRelease(this.semaphore);
                    return true;
                }

                release = null;
                return false;
            }

            public bool IsIdleBefore(DateTimeOffset cutoff) =>
                this.semaphore.CurrentCount == this.PermitLimit
                && new DateTimeOffset(
                    Interlocked.Read(ref this.lastSeenUtcTicks),
                    TimeSpan.Zero) < cutoff;
        }
    }

    internal sealed class RateLimitAttempt : IDisposable
    {
        private IDisposable? release;

        private RateLimitAttempt(
            bool accepted,
            string? policy,
            string? partitionHash,
            TimeSpan retryAfter,
            IDisposable? release)
        {
            this.Accepted = accepted;
            this.Policy = policy;
            this.PartitionHash = partitionHash;
            this.RetryAfter = retryAfter;
            this.release = release;
        }

        public bool Accepted { get; }

        public string? Policy { get; }

        public string? PartitionHash { get; }

        public TimeSpan RetryAfter { get; }

        public static RateLimitAttempt CreateAccepted(IDisposable release) =>
            new(true, null, null, TimeSpan.Zero, release);

        public static RateLimitAttempt CreateAccepted(SemaphoreSlim semaphore, int count) =>
            new(true, null, null, TimeSpan.Zero, new SemaphoreRelease(semaphore, count));

        public static RateLimitAttempt Rejected(
            string policy,
            string partitionHash,
            TimeSpan retryAfter) =>
            new(false, policy, partitionHash, retryAfter, null);

        public void Dispose()
        {
            Interlocked.Exchange(ref this.release, null)?.Dispose();
        }
    }

    private sealed class SemaphoreRelease : IDisposable
    {
        private readonly SemaphoreSlim semaphore;

        private readonly int count;

        private int released;

        public SemaphoreRelease(SemaphoreSlim semaphore, int count = 1)
        {
            this.semaphore = semaphore;
            this.count = count;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.released, 1) == 0)
            {
                this.semaphore.Release(this.count);
            }
        }
    }
}
