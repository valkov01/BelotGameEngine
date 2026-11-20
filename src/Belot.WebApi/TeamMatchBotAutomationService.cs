namespace Belot.WebApi;

using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Belot.Engine.Players;

internal sealed class TeamMatchBotAutomationService : BackgroundService
{
    private readonly TeamMatchLobbyService rooms;

    private readonly ILogger<TeamMatchBotAutomationService> logger;

    private readonly TeamMatchAutomationOptions options;

    private readonly Channel<bool> wakeups;

    private readonly TimeSpan idleScan;

    private int pendingWakeupWaits;

    public TeamMatchBotAutomationService(
        TeamMatchLobbyService rooms,
        IOptions<TeamMatchAutomationOptions> options,
        ILogger<TeamMatchBotAutomationService> logger)
    {
        this.rooms = rooms;
        this.logger = logger;
        this.options = options.Value;
        this.idleScan = TimeSpan.FromMilliseconds(this.options.IdleScanMilliseconds);
        this.wakeups = Channel.CreateBounded<bool>(new BoundedChannelOptions(
            this.options.WakeupQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.rooms.AutomationRequested += this.Signal;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await this.WaitForWakeupAsync(stoppingToken);
                await this.RunCycleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            this.rooms.AutomationRequested -= this.Signal;
        }
    }

    internal int PendingWakeupWaits => Volatile.Read(ref this.pendingWakeupWaits);

    internal async Task WaitForWakeupAsync(CancellationToken cancellationToken)
    {
        using var waitCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Interlocked.Increment(ref this.pendingWakeupWaits);
        var signaled = this.wakeups.Reader
            .WaitToReadAsync(waitCancellation.Token)
            .AsTask();
        var delayed = Task.Delay(this.idleScan, waitCancellation.Token);
        try
        {
            await Task.WhenAny(signaled, delayed);
            await waitCancellation.CancelAsync();
            await ObserveCancellationAsync(signaled);
            await ObserveCancellationAsync(delayed);
        }
        finally
        {
            Interlocked.Decrement(ref this.pendingWakeupWaits);
            while (this.wakeups.Reader.TryRead(out _))
            {
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        for (var action = 0;
             action < this.options.ActionsPerCycle
             && !cancellationToken.IsCancellationRequested;
             action++)
        {
            var lease = this.rooms.TryAcquireBotDecision();
            if (lease == null)
            {
                return;
            }

            try
            {
                var decision = lease.Player.Decide(lease.View);
                if (cancellationToken.IsCancellationRequested)
                {
                    this.rooms.CancelBotDecision(lease);
                    return;
                }

                var result = this.rooms.TryCommitBotDecision(
                    lease,
                    decision,
                    cancellationToken);
                if (result == TeamMatchBotCommitResult.Failed)
                {
                    this.logger.LogError(
                        "A Team Match bot action was rejected for match {MatchId}, table {Table}, seat {Seat}, revision {Revision}.",
                        lease.MatchId, lease.Table, lease.Seat, lease.ExpectedRevision);
                }
            }
            catch (Exception exception)
            {
                this.rooms.FailBotDecision(
                    lease,
                    this.options.MaximumDecisionRetries,
                    this.options.RetryDelayMilliseconds);
                this.logger.LogError(
                    exception,
                    "A Team Match bot decision failed for match {MatchId}, table {Table}, seat {Seat}, revision {Revision}.",
                    lease.MatchId, lease.Table, lease.Seat, lease.ExpectedRevision);
            }

            if (this.options.InterActionDelayMilliseconds > 0)
            {
                await Task.Delay(
                    this.options.InterActionDelayMilliseconds,
                    cancellationToken);
            }
            else
            {
                await Task.Yield();
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            this.Signal();
        }
    }

    internal void Signal() => this.wakeups.Writer.TryWrite(true);

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
