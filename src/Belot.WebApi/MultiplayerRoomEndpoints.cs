namespace Belot.WebApi
{
    using System.Security.Claims;

    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.AspNetCore.Mvc.ModelBinding;
    using Microsoft.AspNetCore.Routing;

    internal static class MultiplayerRoomEndpoints
    {
        public static RouteGroupBuilder MapMultiplayerRoomEndpoints(this IEndpointRouteBuilder endpoints)
        {
            var group = endpoints.MapGroup("/api/multiplayer").RequireAuthorization("Multiplayer");

            group.MapPost("/lobbies", CreateAsync);
            group.MapPost("/rooms", CreateAsync);
            group.MapGet("/lobbies/{roomId}", GetAsync);
            group.MapGet("/rooms/{roomId}", GetAsync);
            group.MapPost("/lobbies/{roomId}/join", JoinAsync);
            group.MapPost("/rooms/{roomId}/seats", JoinAsync);
            group.MapPost("/lobbies/{roomId}/ready", SetReadyAsync);
            group.MapPost("/lobbies/{roomId}/start", StartAsync);
            group.MapGet("/games/{gameId}", GetGameAsync)
                .WithName("GetMultiplayerGame")
                .WithSummary("Get a private multiplayer game snapshot")
                .WithDescription("Returns public match information and only the authenticated member's seat view.")
                .Produces<MultiplayerGameSnapshotDto>(StatusCodes.Status200OK)
                .Produces<MultiplayerGameErrorDto>(StatusCodes.Status403Forbidden)
                .Produces<MultiplayerGameErrorDto>(StatusCodes.Status410Gone);
            group.MapPost("/games/{gameId}/actions", SubmitActionAsync)
                .WithName("SubmitMultiplayerGameAction")
                .WithSummary("Submit a revision-checked multiplayer action")
                .WithDescription("Accepts an action only from the authenticated current human seat; duplicate idempotency keys return the original result.")
                .Produces<MultiplayerGameActionResponse>(StatusCodes.Status200OK)
                .Produces<MultiplayerGameErrorDto>(StatusCodes.Status409Conflict)
                .Produces<MultiplayerGameErrorDto>(StatusCodes.Status410Gone);

            return group;
        }

        private static async Task<IResult> CreateAsync(
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateMultiplayerRoomRequest? request,
            ClaimsPrincipal caller,
            IRoomMembershipService rooms,
            CancellationToken cancellationToken)
        {
            var result = await rooms.CreateRoomAsync(caller, request ?? new CreateMultiplayerRoomRequest(), cancellationToken);
            return result.Status == MultiplayerRoomServiceStatus.Success
                ? Results.Created(
                    $"/api/multiplayer/lobbies/{result.Room!.RoomId}",
                    new CreateMultiplayerRoomResponse(
                        MultiplayerRoomMapper.ToDto(result.Room, caller.FindFirst("sub")!.Value),
                        result.InvitationCode!))
                : ToProblem(result);
        }

        private static async Task<IResult> GetAsync(
            string roomId,
            ClaimsPrincipal caller,
            IRoomMembershipService rooms,
            IMultiplayerGameService games,
            CancellationToken cancellationToken)
        {
            var result = await rooms.GetRoomAsync(caller, roomId, cancellationToken);
            return result.Status == MultiplayerRoomServiceStatus.Success
                ? Results.Ok(MultiplayerRoomMapper.ToDto(
                    result.Room!,
                    caller.FindFirst("sub")!.Value,
                    result.Room!.Status == MultiplayerRoomStatus.Started && games.IsGameAvailable(roomId)))
                : ToProblem(result);
        }

        private static async Task<IResult> JoinAsync(
            string roomId,
            JoinMultiplayerRoomRequest? request,
            ClaimsPrincipal caller,
            ISeatAssignmentService assignments,
            CancellationToken cancellationToken)
        {
            var result = await assignments.AssignSeatAsync(caller, roomId, request, cancellationToken);
            return result.Status == MultiplayerRoomServiceStatus.Success
                ? Results.Ok(MultiplayerRoomMapper.ToDto(result.Room!, caller.FindFirst("sub")!.Value))
                : ToProblem(result);
        }

        private static async Task<IResult> SetReadyAsync(
            string roomId,
            SetMultiplayerReadyRequest? request,
            ClaimsPrincipal caller,
            ISeatAssignmentService assignments,
            CancellationToken cancellationToken)
        {
            if (request is null)
            {
                return Results.BadRequest(new ApiError("Provide the desired ready state."));
            }

            var result = await assignments.SetReadyAsync(caller, roomId, request.IsReady, cancellationToken);
            return result.Status == MultiplayerRoomServiceStatus.Success
                ? Results.Ok(MultiplayerRoomMapper.ToDto(result.Room!, caller.FindFirst("sub")!.Value))
                : ToProblem(result);
        }

        private static async Task<IResult> StartAsync(
            string roomId,
            ClaimsPrincipal caller,
            IMultiplayerGameService games,
            CancellationToken cancellationToken)
        {
            var result = await games.StartAsync(caller, roomId, cancellationToken);
            return result.Status == MultiplayerGameServiceStatus.Success
                ? Results.Ok(MultiplayerRoomMapper.ToDto(result.Room!, caller.FindFirst("sub")!.Value, isGameAvailable: true))
                : ToGameProblem(result);
        }

        private static async Task<IResult> GetGameAsync(
            string gameId,
            ClaimsPrincipal caller,
            IMultiplayerGameService games,
            CancellationToken cancellationToken)
        {
            var result = await games.GetSnapshotAsync(caller, gameId, cancellationToken);
            return result.Status == MultiplayerGameServiceStatus.Success
                ? Results.Ok(result.Snapshot)
                : ToGameProblem(result);
        }

        private static async Task<IResult> SubmitActionAsync(
            string gameId,
            MultiplayerGameActionRequest? request,
            ClaimsPrincipal caller,
            IMultiplayerGameService games,
            CancellationToken cancellationToken)
        {
            var result = await games.SubmitActionAsync(caller, gameId, request, cancellationToken);
            return result.Status == MultiplayerGameServiceStatus.Success
                ? Results.Ok(result.ActionResponse)
                : ToGameProblem(result);
        }

        private static IResult ToProblem(MultiplayerRoomServiceResult result) => result.Status switch
        {
            MultiplayerRoomServiceStatus.Unauthorized => Results.Unauthorized(),
            MultiplayerRoomServiceStatus.Forbidden => Results.Forbid(),
            MultiplayerRoomServiceStatus.NotFound => Results.NotFound(new ApiError("Room not found.")),
            MultiplayerRoomServiceStatus.Conflict => Results.Conflict(new ApiError(result.Error!)),
            MultiplayerRoomServiceStatus.InvalidRequest => Results.BadRequest(new ApiError(result.Error!)),
            _ => throw new InvalidOperationException($"Unexpected room service result: {result.Status}."),
        };

        private static IResult ToGameProblem(MultiplayerGameServiceResult result) => result.Status switch
        {
            MultiplayerGameServiceStatus.Unauthorized => Results.Unauthorized(),
            MultiplayerGameServiceStatus.Forbidden => Results.Forbid(),
            MultiplayerGameServiceStatus.NotFound => Results.NotFound(new ApiError("Game not found.")),
            MultiplayerGameServiceStatus.InvalidRequest => Results.BadRequest(
                new MultiplayerGameErrorDto(result.Error!, result.CurrentRevision)),
            MultiplayerGameServiceStatus.Conflict => Results.Conflict(
                new MultiplayerGameErrorDto(result.Error ?? "The multiplayer action conflicts with the current game state.", result.CurrentRevision)),
            MultiplayerGameServiceStatus.Gone => Results.Json(
                new MultiplayerGameErrorDto(result.Error ?? "The in-memory match is unavailable.", null),
                statusCode: StatusCodes.Status410Gone),
            _ => throw new InvalidOperationException($"Unexpected game service result: {result.Status}."),
        };
    }
}
