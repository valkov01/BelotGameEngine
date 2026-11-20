namespace Belot.WebApi.Tests;

using Belot.Engine;
using Belot.Engine.Game;
using Belot.Engine.GameMechanics;
using Belot.Engine.Players;

using Microsoft.Extensions.Configuration;

using Xunit;

public sealed class TeamMatchBotAutomationTests
{
    private static readonly (int Table, PlayerPosition Seat)[] AllSeats =
        (from table in new[] { 1, 2 }
         from seat in new[]
         {
             PlayerPosition.South,
             PlayerPosition.East,
             PlayerPosition.North,
             PlayerPosition.West,
         }
         select (table, seat)).ToArray();

    [Fact]
    public void CancelledAndStaleBotLeasesDoNotCommitAnAction()
    {
        var rooms = CreateRooms();
        var (matchId, host) = StartOneHumanMatch(rooms);
        var lease = Assert.IsType<TeamMatchBotDecisionLease>(
            rooms.TryAcquireBotDecision());
        var game = rooms.GetCoordinatorForAudit(matchId)!;
        var action = lease.Player.Decide(lease.View);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(
            TeamMatchBotCommitResult.Cancelled,
            rooms.TryCommitBotDecision(lease, action, cancellation.Token));
        Assert.Empty(game.GetSeatView(2, PlayerPosition.South).Bids);

        var retry = Assert.IsType<TeamMatchBotDecisionLease>(
            rooms.TryAcquireBotDecision());
        Assert.Equal(
            TeamMatchBotCommitResult.Accepted,
            rooms.TryCommitBotDecision(retry, retry.Player.Decide(retry.View)));
        Assert.Equal(
            TeamMatchBotCommitResult.Stale,
            rooms.TryCommitBotDecision(lease, action));
        Assert.Single(game.GetSeatView(2, PlayerPosition.South).Bids);

        Assert.Equal(
            TeamMatchLobbyStatus.Success,
            rooms.ResumeAssignedBots(matchId, host, 2).Status);
    }

    [Fact]
    public void ConcurrentCaptureProducesOnlyOneInFlightDecisionPerTable()
    {
        var rooms = CreateRooms();
        StartOneHumanMatch(rooms);

        var leases = Enumerable.Range(0, 16)
            .AsParallel()
            .Select(_ => rooms.TryAcquireBotDecision())
            .ToArray();

        Assert.Single(leases.Where(lease => lease != null));
    }

    [Fact]
    public void HumanReplacementInvalidatesTheCapturedBotDecision()
    {
        var rooms = CreateRooms();
        var (matchId, host) = StartOneHumanMatch(rooms);
        var lease = Assert.IsType<TeamMatchBotDecisionLease>(
            rooms.TryAcquireBotDecision());
        var action = lease.Player.Decide(lease.View);
        var replacement = Guid.NewGuid().ToString();

        var invited = rooms.InviteReplacement(
            matchId,
            host,
            new InviteTeamMatchReplacementRequest(2, PlayerPosition.South, replacement));
        Assert.Equal(TeamMatchLobbyStatus.Success, invited.Status);
        var accepted = rooms.AcceptReplacement(
            matchId,
            replacement,
            invited.Invitation!.InvitationId);
        Assert.Equal(TeamMatchLobbyStatus.Success, accepted.Status);
        Assert.Equal(
            TeamMatchBotCommitResult.Stale,
            rooms.TryCommitBotDecision(lease, action));
        Assert.Empty(
            rooms.GetCoordinatorForAudit(matchId)!
                .GetSeatView(2, PlayerPosition.South)
                .Bids);
        Assert.NotNull(
            rooms.GetPrivateGame(matchId, replacement).Game);
    }

    [Fact]
    public async Task RepeatedBotFailuresBackOffThenRequireExplicitResume()
    {
        var rooms = CreateRooms(_ => new FailingPlayer());
        var (matchId, host) = StartOneHumanMatch(rooms);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var lease = Assert.IsType<TeamMatchBotDecisionLease>(
                rooms.TryAcquireBotDecision());
            Assert.Throws<InvalidOperationException>(
                () => lease.Player.Decide(lease.View));
            rooms.FailBotDecision(lease, maximumRetries: 2, retryDelayMilliseconds: 10);
            if (attempt < 2)
            {
                await Task.Delay(50);
            }
        }

        Assert.Null(rooms.TryAcquireBotDecision());
        Assert.Equal(
            TeamMatchLobbyStatus.Success,
            rooms.ResumeAssignedBots(matchId, host, 2).Status);
        Assert.NotNull(rooms.TryAcquireBotDecision());
    }

    [Fact]
    public void ActiveMatchCapacityRejectsAnotherAutonomousStart()
    {
        var rooms = CreateRooms(maximumActiveMatches: 1);
        var first = StartOneHumanMatch(rooms);
        var secondHost = Guid.NewGuid().ToString();
        var second = rooms.Create(
            secondHost,
            new CreateTeamMatchRequest { ScheduledDeals = 4 });
        Assert.Equal(TeamMatchLobbyStatus.Success, second.Status);
        foreach (var seat in AllSeats.Except(
                     new[] { (1, PlayerPosition.South) }))
        {
            var assigned = rooms.SetBot(
                second.Lobby!.MatchId,
                secondHost,
                new SetTeamMatchBotRequest(
                    seat.Item1,
                    seat.Item2,
                    "skilled"));
            Assert.Equal(
                TeamMatchLobbyStatus.Success,
                assigned.Status);
        }

        var started = rooms.Start(second.Lobby!.MatchId, secondHost);
        Assert.Equal(TeamMatchLobbyStatus.Conflict, started.Status);
        Assert.Contains(
            "capacity",
            started.Error,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(rooms.GetCoordinatorForAudit(first.MatchId));
    }

    [Fact]
    public void KickPauseAssignmentAndResumeCannotUseTheRevokedLease()
    {
        var rooms = CreateRooms();
        var (matchId, host) = StartOneHumanMatch(rooms);
        var invitee = Guid.NewGuid().ToString();
        var lease = Assert.IsType<TeamMatchBotDecisionLease>(
            rooms.TryAcquireBotDecision());
        var action = lease.Player.Decide(lease.View);

        var invitation = rooms.InviteReplacement(
            matchId,
            host,
            new InviteTeamMatchReplacementRequest(2, PlayerPosition.South, invitee));
        rooms.AcceptReplacement(
            matchId,
            invitee,
            invitation.Invitation!.InvitationId);
        var kicked = rooms.Kick(
            matchId,
            host,
            new KickTeamMatchPlayerRequest(2, PlayerPosition.South));
        Assert.Equal(TeamMatchLobbyStatus.Success, kicked.Status);
        Assert.Equal(
            TeamMatchBotCommitResult.Stale,
            rooms.TryCommitBotDecision(lease, action));
        var assigned = rooms.AssignReplacementBot(
            matchId,
            host,
            new AssignTeamMatchReplacementBotRequest(
                2,
                PlayerPosition.South,
                "skilled"));
        Assert.Equal(TeamMatchLobbyStatus.Success, assigned.Status);
        Assert.NotNull(rooms.TryAcquireBotDecision());
    }

    private static TeamMatchLobbyService CreateRooms(
        Func<string, IPlayer>? createBot = null,
        int maximumActiveMatches = 20)
    {
        var settings = new Dictionary<string, string?>
        {
            ["TeamMatch:Automation:MaximumActiveMatches"] =
                maximumActiveMatches.ToString(),
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        return new TeamMatchLobbyService(
            configuration,
            (scheduledDeals, barometerEnabled) =>
                new TeamMatchCoreCoordinator(
                    scheduledDeals,
                    10_620,
                    barometerEnabled: barometerEnabled),
            createBot);
    }

    private static (string MatchId, string Host) StartOneHumanMatch(
        TeamMatchLobbyService rooms)
    {
        var host = Guid.NewGuid().ToString();
        var created = rooms.Create(
            host,
            new CreateTeamMatchRequest { ScheduledDeals = 4 });
        Assert.Equal(TeamMatchLobbyStatus.Success, created.Status);
        foreach (var (table, seat) in AllSeats.Except(
                     new[] { (1, PlayerPosition.South) }))
        {
            var assigned = rooms.SetBot(
                created.Lobby!.MatchId,
                host,
                new SetTeamMatchBotRequest(table, seat, "skilled"));
            Assert.Equal(
                TeamMatchLobbyStatus.Success,
                assigned.Status);
        }

        var started = rooms.Start(created.Lobby!.MatchId, host);
        Assert.Equal(TeamMatchLobbyStatus.Success, started.Status);
        return (created.Lobby.MatchId, host);
    }

    private sealed class FailingPlayer : IPlayer
    {
        public BidType GetBid(PlayerGetBidContext context) =>
            throw new InvalidOperationException("Simulated bot failure.");

        public IList<Announce> GetAnnounces(PlayerGetAnnouncesContext context) =>
            throw new InvalidOperationException("Simulated bot failure.");

        public PlayCardAction PlayCard(PlayerPlayCardContext context) =>
            throw new InvalidOperationException("Simulated bot failure.");

        public void EndOfTrick(IEnumerable<PlayCardAction> trickActions)
        {
        }

        public void EndOfRound(RoundResult roundResult)
        {
        }

        public void EndOfGame(GameResult gameResult)
        {
        }
    }
}
