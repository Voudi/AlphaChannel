using AlphaChannel.Contracts;
using AlphaChannel.Server.Auth;
using AlphaChannel.Server.Social;

namespace AlphaChannel.Server;

internal static class RoomEndpoints
{
    public static void MapRoomEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/").AddEndpointFilter<AccountAuthFilter>().AddEndpointFilter<LalafellGateFilter>();

        group.MapGet("/rooms", (string? kind, RoomDirectoryService directory) =>
        {
            RoomKind? filter = null;
            if (!string.IsNullOrWhiteSpace(kind))
            {
                if (!Enum.TryParse<RoomKind>(kind, ignoreCase: true, out var parsed))
                {
                    return Results.BadRequest();
                }

                filter = parsed;
            }

            return Results.Ok(directory.List(filter));
        });
    }
}
