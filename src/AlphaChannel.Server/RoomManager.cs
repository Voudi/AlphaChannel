using System.Collections.Concurrent;
using System.Net.WebSockets;
using AlphaChannel.Contracts;

namespace AlphaChannel.Server;

// A room is keyed by RoomKey - originally always its creator's UserId, but that key stays fixed
// for the room's whole lifetime even after a stream.transferHost changes who HostUserId actually
// is. Torn down when whoever currently holds HostUserId disconnects. No persistence: this is the
// in-memory-only v1 the plan calls for, no database.
internal sealed class Room
{
    public required string RoomKey { get; init; }
    public required string HostUserId { get; set; }
    public StreamControl? LastState { get; set; }
    public ConcurrentDictionary<string, WebSocket> Viewers { get; } = new();

    // Set from StreamControl.IsPrivate on stream.state - see that field's doc comment.
    public bool IsPrivate { get; set; }

    public string? Description { get; set; }
    public string? Location { get; set; }
    public RoomKind Kind { get; set; } = RoomKind.Public;
    public string? PasswordHash { get; set; }
}

internal sealed class RoomManager
{
    private readonly ConcurrentDictionary<string, Room> rooms = new();

    public Room GetOrCreateRoom(string hostUserId) =>
        rooms.GetOrAdd(hostUserId, id => new Room { RoomKey = id, HostUserId = id });

    public Room? GetRoom(string roomKey) =>
        rooms.GetValueOrDefault(roomKey);

    // Finds the room this user currently hosts, if any - independent of what the room's original
    // RoomKey is, since a transfer can make HostUserId diverge from it. O(n) over live rooms is
    // fine at this scale, same reasoning as UserDirectory.TryResolveUserId's own scan.
    public Room? FindRoomHostedBy(string userId) =>
        rooms.Values.FirstOrDefault(room => room.HostUserId == userId);

    // Same O(n)-is-fine reasoning as FindRoomHostedBy - used by presence to say "watching with
    // {host}" for a viewer, as opposed to FindRoomHostedBy's "hosting" case.
    public Room? FindRoomViewedBy(string userId) =>
        rooms.Values.FirstOrDefault(room => room.Viewers.ContainsKey(userId));

    public void RemoveRoom(string roomKey) => rooms.TryRemove(roomKey, out _);

    public IReadOnlyList<Room> ListListable(RoomKind? kind = null)
    {
        return rooms.Values
            .Where(room => !room.IsPrivate)
            .Where(room => kind is null || room.Kind == kind)
            .ToList();
    }
}
