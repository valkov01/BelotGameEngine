namespace Belot.WebApi;

using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Authenticated Team Match lobby API. Separate from existing single-table multiplayer.
/// Do not add player action/private hand routes here until the A10 game adapter is ready.
/// </summary>
internal static class TeamMatchLobbyEndpoints
{
    internal static RouteGroupBuilder MapTeamMatchLobbyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/team-matches").RequireAuthorization("Multiplayer");
        group.MapPost("/", CreateAsync);
        group.MapGet("/{matchId}", GetAsync);
        group.MapPost("/{matchId}/join", JoinAsync);
        group.MapPost("/{matchId}/seat", MoveAsync);
        group.MapPost("/{matchId}/bots", SetBotAsync);
        group.MapPost("/{matchId}/reservations", ReserveAsync);
        group.MapPost("/{matchId}/invitations/rotate", RotateInviteAsync);
        group.MapPost("/{matchId}/start", StartAsync);
        return group;
    }

    private static async Task<IResult> CreateAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateTeamMatchRequest? request,
        ClaimsPrincipal caller,
        TeamMatchLobbyService rooms,
        IProfileDisplayNameProvider names,
        CancellationToken cancellationToken)
    {
        var result = rooms.Create(UserId(caller), request ?? new CreateTeamMatchRequest());
        if (result.Status != TeamMatchLobbyStatus.Success)
        {
            return ErrorResult(result);
        }

        var lobby = await WithNames(result.Lobby!, caller, rooms, names, cancellationToken);
        return Results.Created($"/api/team-matches/{lobby.MatchId}",
            new CreateTeamMatchResponse(lobby, result.InvitationCode!));
    }

    private static async Task<IResult> GetAsync(
        string matchId,
        ClaimsPrincipal caller,
        TeamMatchLobbyService rooms,
        IProfileDisplayNameProvider names,
        CancellationToken cancellationToken) =>
        await Render(rooms.Get(matchId, UserId(caller)), caller, rooms, names, cancellationToken);

    private static async Task<IResult> JoinAsync(
        string matchId,
        JoinTeamMatchRequest? request,
        ClaimsPrincipal caller,
        TeamMatchLobbyService rooms,
        IProfileDisplayNameProvider names,
        CancellationToken cancellationToken) =>
        await Render(rooms.Join(matchId, UserId(caller), request), caller, rooms, names, cancellationToken);

    private static async Task<IResult> MoveAsync(
        string matchId,
        MoveTeamMatchSeatRequest? request,
        ClaimsPrincipal caller,
        TeamMatchLobbyService rooms,
        IProfileDisplayNameProvider names,
        CancellationToken cancellationToken) =>
        await Render(rooms.Move(matchId, UserId(caller), request), caller, rooms, names, cancellationToken);

    private static async Task<IResult> SetBotAsync(
        string matchId,
        SetTeamMatchBotRequest? request,
        ClaimsPrincipal caller,
        TeamMatchLobbyService rooms,
        IProfileDisplayNameProvider names,
        CancellationToken cancellationToken) =>
        await Render(rooms.SetBot(matchId, UserId(caller), request), caller, rooms, names, cancellationToken);

    private static async Task<IResult> ReserveAsync(
        string matchId,
        ReserveTeamMatchSeatRequest? request,
        ClaimsPrincipal caller,
        TeamMatchLobbyService rooms,
        IProfileDisplayNameProvider names,
        CancellationToken cancellationToken) =>
        await Render(rooms.Reserve(matchId, UserId(caller), request), caller, rooms, names, cancellationToken);

    private static async Task<IResult> RotateInviteAsync(
        string matchId,
        ClaimsPrincipal caller,
        TeamMatchLobbyService rooms,
        IProfileDisplayNameProvider names,
        CancellationToken cancellationToken)
    {
        var result = rooms.RotateInvite(matchId, UserId(caller));
        if (result.Status != TeamMatchLobbyStatus.Success)
        {
            return ErrorResult(result);
        }

        // Only the host gets the freshly rotated secret. GET/other lobby snapshots
        // never contain any invitation token or user IDs.
        return Results.Ok(new RotateTeamMatchInviteResponse(result.InvitationCode!));
    }

    private static async Task<IResult> StartAsync(
        string matchId,
        ClaimsPrincipal caller,
        TeamMatchLobbyService rooms,
        IProfileDisplayNameProvider names,
        CancellationToken cancellationToken) =>
        await Render(rooms.Start(matchId, UserId(caller)), caller, rooms, names, cancellationToken);

    private static async Task<IResult> Render(
        TeamMatchLobbyResult result,
        ClaimsPrincipal caller,
        TeamMatchLobbyService rooms,
        IProfileDisplayNameProvider names,
        CancellationToken cancellationToken)
    {
        if (result.Status != TeamMatchLobbyStatus.Success)
        {
            return ErrorResult(result);
        }

        return Results.Ok(await WithNames(result.Lobby!, caller, rooms, names, cancellationToken));
    }

    private static async Task<TeamMatchLobbyDto> WithNames(
        TeamMatchLobbyDto snapshot,
        ClaimsPrincipal caller,
        TeamMatchLobbyService rooms,
        IProfileDisplayNameProvider profiles,
        CancellationToken cancellationToken)
    {
        var owners = rooms.GetMemberOwnerIds(snapshot.MatchId, UserId(caller));
        if (owners == null || owners.Count == 0)
        {
            return snapshot;
        }

        // Resolve names through the existing project provider; the raw IDs are
        // never written to public lobby JSON or to SignalR. The provider itself
        // handles missing/unavailable profile data.
        var resolved = await profiles.ResolveAsync(owners.Values.Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);
        var seats = snapshot.Seats.Select(seat =>
        {
            var key = (seat.Table, seat.Seat);
            return owners.TryGetValue(key, out var id)
                && resolved.TryGetValue(id, out var displayName)
                ? seat with { DisplayName = displayName }
                : seat;
        }).ToArray();
        return snapshot with { Seats = seats };
    }

    private static string UserId(ClaimsPrincipal caller) => caller.FindFirst("sub")?.Value ?? string.Empty;

    private static IResult ErrorResult(TeamMatchLobbyResult result) => result.Status switch
    {
        TeamMatchLobbyStatus.Forbidden => Results.Forbid(),
        TeamMatchLobbyStatus.NotFound => Results.NotFound(new ApiError("Team Match not found.")),
        TeamMatchLobbyStatus.Invalid => Results.BadRequest(new ApiError(result.Error ?? "Invalid request.")),
        TeamMatchLobbyStatus.Conflict => Results.Conflict(new ApiError(result.Error ?? "The lobby state has changed.")),
        _ => throw new InvalidOperationException("Unexpected Team Match service response."),
    };
}
