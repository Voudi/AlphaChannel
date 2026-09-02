using AlphaChannel.Contracts;

namespace AlphaChannel.Server;

internal sealed class RoomDirectoryService(RoomManager rooms, UserDirectory directory)
{
    public IReadOnlyList<RoomDirectoryDto> List(RoomKind? kind)
    {
        return rooms.ListListable(kind).Select(ToDto).ToList();
    }

    private RoomDirectoryDto ToDto(Room room)
    {
        var state = room.LastState;
        var hasMedia = !string.IsNullOrWhiteSpace(state?.Url);
        var url = room.Kind == RoomKind.Locked ? null : state?.Url;
        return new RoomDirectoryDto(
            room.HostUserId,
            directory.DisplayNameOrFallback(room.HostUserId),
            room.Description,
            room.Location,
            room.Kind,
            room.Viewers.Count,
            state?.Paused ?? true,
            hasMedia,
            url);
    }
}
