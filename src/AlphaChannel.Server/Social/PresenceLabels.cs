using AlphaChannel.Server.Live;

namespace AlphaChannel.Server.Social;

// Shared by FriendService (pull, GET /friends) and PresenceService (push, on connect/disconnect)
// so "what is this account doing right now" is computed exactly one way. Nothing here is stored -
// it's a live query over RoomManager/UserDirectory/LiveDirectory, the same in-memory state
// stream.* already uses.
internal static class PresenceLabels
{
    public static string? WatchingLabel(string accountId, RoomManager rooms, UserDirectory directory, LiveDirectory live)
    {
        // Checked first - if you're live via Go Live, that's more relevant than any watch-along
        // state (going live already implies you're not simultaneously hosting/viewing a room).
        if (live.IsLive(accountId))
        {
            return "Live now";
        }

        if (rooms.FindRoomHostedBy(accountId) is { } hostedRoom)
        {
            if (hostedRoom.IsPrivate)
            {
                return "Watching privately";
            }

            var count =
                hostedRoom.Viewers.Count;

            return count == 0
                ? "Hosting a Watch Party"
                : $"Hosting a Watch Party ({count} watching)";
        }

        if (rooms.FindRoomViewedBy(accountId) is { } viewedRoom)
        {
            // A viewer in someone else's private room shouldn't leak who's hosting either.
            return viewedRoom.IsPrivate ? "Watching privately" : $"Watching with {directory.DisplayNameOrFallback(viewedRoom.HostUserId)}";
        }

        return null;
    }
    public static bool HostingJoinableWatchParty(
    string accountId,
    RoomManager rooms)
    {
        return rooms.FindRoomHostedBy(
                   accountId) is
        {
            IsPrivate: false
        };
    }
}
