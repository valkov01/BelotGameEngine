namespace Belot.WebApi
{
    using System.Security.Claims;

    using Belot.Engine.Players;

    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Routing;

    internal static class MultiplayerRoomEndpoints
    {
        public static RouteGroupBuilder MapMultiplayerRoomEndpoints(this IEndpointRouteBuilder endpoints)
        {
            var group = endpoints.MapGroup("/api/multiplayer").RequireAuthorization("Multiplayer");

            group.MapPost("/rooms", async (
                ClaimsPrincipal caller,
                IRoomMembershipService rooms,
                CancellationToken cancellationToken) =>
            {
                var result = await rooms.CreateRoomAsync(caller, cancellationToken);
                return result.Status == MultiplayerRoomServiceStatus.Success
                    ? Results.Created(
                        $"/api/multiplayer/rooms/{result.Room!.RoomId}",
                        MultiplayerRoomMapper.ToDto(result.Room, caller.FindFirst("sub")!.Value))
                    : ToProblem(result);
            });

            group.MapGet("/rooms/{roomId}", async (
                string roomId,
                ClaimsPrincipal caller,
                IRoomMembershipService rooms,
                CancellationToken cancellationToken) =>
            {
                var result = await rooms.GetRoomAsync(caller, roomId, cancellationToken);
                return result.Status == MultiplayerRoomServiceStatus.Success
                    ? Results.Ok(MultiplayerRoomMapper.ToDto(result.Room!, caller.FindFirst("sub")!.Value))
                    : ToProblem(result);
            });

            group.MapPost("/rooms/{roomId}/seats", async (
                string roomId,
                JoinMultiplayerRoomRequest? request,
                ClaimsPrincipal caller,
                ISeatAssignmentService assignments,
                CancellationToken cancellationToken) =>
            {
                var result = await assignments.AssignSeatAsync(caller, roomId, request?.Seat, cancellationToken);
                return result.Status == MultiplayerRoomServiceStatus.Success
                    ? Results.Ok(MultiplayerRoomMapper.ToDto(result.Room!, caller.FindFirst("sub")!.Value))
                    : ToProblem(result);
            });

            return group;
        }

        private static IResult ToProblem(MultiplayerRoomServiceResult result) => result.Status switch
        {
            MultiplayerRoomServiceStatus.Unauthorized => Results.Unauthorized(),
            MultiplayerRoomServiceStatus.Forbidden => Results.Forbid(),
            MultiplayerRoomServiceStatus.NotFound => Results.NotFound(new ApiError("Room not found.")),
            MultiplayerRoomServiceStatus.Conflict => Results.Conflict(new ApiError(result.Error!)),
            MultiplayerRoomServiceStatus.InvalidSeat => Results.BadRequest(new ApiError(result.Error!)),
            _ => throw new InvalidOperationException($"Unexpected room service result: {result.Status}."),
        };
    }
}
