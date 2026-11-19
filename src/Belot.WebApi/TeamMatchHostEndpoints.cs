namespace Belot.WebApi;

using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Authenticated Team Matches host controls, player reports and private seat
/// game entry. Every access check uses server-trusted JWT sub, never client UID.
/// </summary>
internal static class TeamMatchHostEndpoints
{
    internal static RouteGroupBuilder MapTeamMatchHostEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/team-matches").RequireAuthorization("Multiplayer");
        group.MapGet("/invitations", ListReplacementInvites)
            .WithMetadata(new TeamMatchRateLimitMetadata(TeamMatchRateLimitKind.Read));
        group.MapGet("/{matchId}/notices", GetRemovalNotices)
            .WithMetadata(new TeamMatchRateLimitMetadata(TeamMatchRateLimitKind.Read));
        group.MapPost("/{matchId}/invitations/{invitationId}/accept", AcceptReplacement)
            .WithMetadata(new TeamMatchRateLimitMetadata(TeamMatchRateLimitKind.Mutation));
        group.MapGet("/{matchId}/organizer", GetOrganizer)
            .WithMetadata(new TeamMatchRateLimitMetadata(TeamMatchRateLimitKind.Read));
        group.MapGet("/{matchId}/results", GetResults)
            .WithMetadata(new TeamMatchRateLimitMetadata(TeamMatchRateLimitKind.Read));
        group.MapPost("/{matchId}/reports", ReportInactive)
            .WithMetadata(new TeamMatchRateLimitMetadata(TeamMatchRateLimitKind.Mutation));
        group.MapPost("/{matchId}/organizer/kick", Kick)
            .WithMetadata(new TeamMatchRateLimitMetadata(TeamMatchRateLimitKind.Mutation));
        group.MapPost("/{matchId}/organizer/invite-replacement", InviteReplacement)
            .WithMetadata(new TeamMatchRateLimitMetadata(TeamMatchRateLimitKind.Mutation));
        group.MapPost("/{matchId}/organizer/assign-replacement-bot", AssignReplacementBot)
            .WithMetadata(new TeamMatchRateLimitMetadata(TeamMatchRateLimitKind.Mutation));
        group.MapGet("/{matchId}/game", GetGame)
            .WithMetadata(new TeamMatchRateLimitMetadata(TeamMatchRateLimitKind.Read))
            .WithName("GetTeamMatchGame")
            .WithSummary("Get a private Team Match seat snapshot and public events after a cursor")
            .WithDescription("If event history for afterSequence has expired, returns an authoritative snapshot with an empty event list and snapshotRequired=true.");
        group.MapPost("/{matchId}/game/actions", SubmitAction)
            .WithMetadata(new TeamMatchRateLimitMetadata(TeamMatchRateLimitKind.Mutation))
            .WithName("SubmitTeamMatchGameAction")
            .WithSummary("Submit a revision-checked Team Match action")
            .WithDescription("Accepts an action only from the authenticated current seat. Optional afterSequence returns public events since that table cursor; expired history returns an authoritative seat snapshot instead of a partial event list.");
        group.MapPost("/{matchId}/game/bots/resume", ResumeBots)
            .WithMetadata(new TeamMatchRateLimitMetadata(TeamMatchRateLimitKind.Mutation));
        return group;
    }

    private static IResult GetRemovalNotices(
        string matchId, ClaimsPrincipal caller, TeamMatchLobbyService rooms) =>
        Respond(rooms.GetRemovalNotices(matchId, UserId(caller)),
            result => Results.Ok(result.Notices));

    private static IResult ListReplacementInvites(
        ClaimsPrincipal caller, TeamMatchLobbyService rooms) =>
        Respond(rooms.ListInvitations(UserId(caller)), result => Results.Ok(result.Invitations));

    private static IResult AcceptReplacement(
        string matchId, string invitationId, ClaimsPrincipal caller,
        TeamMatchLobbyService rooms) =>
        Respond(
            rooms.AcceptReplacement(matchId, UserId(caller), invitationId),
            result => Results.Ok(result.Lobby));

    private static IResult GetResults(
        string matchId, ClaimsPrincipal caller, TeamMatchLobbyService rooms) =>
        Respond(rooms.GetMemberResults(matchId, UserId(caller)),
            result => Results.Ok(result.Results));

    private static async Task<IResult> GetOrganizer(
        string matchId, ClaimsPrincipal caller, TeamMatchLobbyService rooms,
        IProfileDisplayNameProvider profiles, CancellationToken cancellationToken)
    {
        var result = rooms.GetOrganizerOverview(matchId, UserId(caller));
        if (result.Status != TeamMatchLobbyStatus.Success)
        {
            return Error(result);
        }

        var overview = result.Overview!;
        // Only known member display names, not raw Supabase IDs, appear in
        // the organizer progress response. "Unknown" means presence is not
        // implemented; never invent an "online" indicator.
        var owners = rooms.GetMemberOwnerIds(matchId, UserId(caller));
        var displayNames = owners == null
            ? new Dictionary<string, string?>()
            : await profiles.ResolveAsync(
                owners.Values.Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);
        var tables = overview.Tables.Select(table => table with
        {
            Seats = table.Seats.Select(seat =>
                owners != null
                && owners.TryGetValue((seat.Table, seat.Seat), out var id)
                && displayNames.TryGetValue(id, out var display)
                    ? seat with { DisplayName = display }
                    : seat).ToArray(),
        }).ToArray();
        return Results.Ok(overview with { Tables = tables });
    }

    private static IResult ReportInactive(
        string matchId, TeamMatchInactivityReportRequest? request,
        ClaimsPrincipal caller, TeamMatchLobbyService rooms) =>
        Respond(
            rooms.ReportInactive(matchId, UserId(caller), request),
            result => Results.Accepted(
                $"/api/team-matches/{matchId}/organizer", result.Report));

    private static IResult Kick(
        string matchId, KickTeamMatchPlayerRequest? request,
        ClaimsPrincipal caller, TeamMatchLobbyService rooms) =>
        Respond(rooms.Kick(matchId, UserId(caller), request),
            result => Results.Ok(result.Lobby));

    private static IResult InviteReplacement(
        string matchId, InviteTeamMatchReplacementRequest? request,
        ClaimsPrincipal caller, TeamMatchLobbyService rooms) =>
        Respond(rooms.InviteReplacement(matchId, UserId(caller), request),
            result => Results.Ok(result.Invitation));

    private static IResult AssignReplacementBot(
        string matchId, AssignTeamMatchReplacementBotRequest? request,
        ClaimsPrincipal caller, TeamMatchLobbyService rooms) =>
        Respond(rooms.AssignReplacementBot(matchId, UserId(caller), request),
            result => Results.Ok(result.Lobby));

    private static IResult GetGame(
        string matchId, int? afterSequence, ClaimsPrincipal caller, TeamMatchLobbyService rooms) =>
        Respond(rooms.GetPrivateGame(matchId, UserId(caller), afterSequence),
            result => Results.Ok(result.Game));

    private static IResult SubmitAction(
        string matchId, TeamMatchHumanActionRequest? request,
        ClaimsPrincipal caller, TeamMatchLobbyService rooms) =>
        Respond(rooms.SubmitHumanAction(matchId, UserId(caller), request),
            result => Results.Ok(result.Game));

    private static IResult ResumeBots(
        string matchId, ResumeTeamMatchBotsRequest? request,
        ClaimsPrincipal caller, TeamMatchLobbyService rooms) =>
        Respond(rooms.ResumeAssignedBots(matchId, UserId(caller), request?.Table ?? 0),
            _ => Results.Ok(new { Resumed = true }));

    private static IResult Respond(
        TeamMatchHostResult result, Func<TeamMatchHostResult, IResult> success) =>
        result.Status == TeamMatchLobbyStatus.Success ? success(result) : Error(result);

    private static IResult Error(TeamMatchHostResult result) => result.Status switch
    {
        TeamMatchLobbyStatus.NotFound => Results.NotFound(new ApiError("Team Match not found.")),
        TeamMatchLobbyStatus.Forbidden => Results.Forbid(),
        TeamMatchLobbyStatus.Invalid => Results.BadRequest(new ApiError(result.Error ?? "Invalid request.")),
        TeamMatchLobbyStatus.Conflict => Results.Conflict(new ApiError(result.Error ?? "Match state changed.")),
        _ => throw new InvalidOperationException("Unexpected Team Match host result."),
    };

    private static string UserId(ClaimsPrincipal caller) =>
        caller.FindFirst("sub")?.Value ?? string.Empty;
}
