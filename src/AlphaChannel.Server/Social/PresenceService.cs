using System.Collections.Concurrent;
using AlphaChannel.Contracts;
using AlphaChannel.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AlphaChannel.Server.Social;

// Called from ConnectionHandler (a singleton) at the points where an account's connection or
// watch-along state changes - IDbContextFactory for the same reason as every other service here.
// Presence itself is never stored: this only ever pushes a freshly-computed PresenceLabels result,
// the same live query GET /friends already uses for the pull case.
internal sealed class PresenceService(
    IDbContextFactory<AlphaChannelDbContext> dbFactory, UserDirectory directory, RoomManager rooms, Live.LiveDirectory liveDirectory)
{
    // Cheap in-memory dedup, checked before anything DB-touching runs - stream.state publishes
    // every tick with no diff-check (see ConnectionHandler's own comment on why), so NotifyAsync
    // needs to be safe to call that often without hammering the database or spamming friends with
    // identical pushes on every tick.
    private readonly ConcurrentDictionary<
    string,
    (
        bool Online,
        string? WatchingLabel,
        bool HostingJoinableWatchParty
    )> lastPushed = new();

    public async Task NotifyAsync(string accountIdString, bool online, CancellationToken cancellationToken)
    {
        var watchingLabel =
            online
                ? PresenceLabels.WatchingLabel(
                    accountIdString,
                    rooms,
                    directory,
                    liveDirectory)
                : null;

        var hostingJoinableWatchParty =
            online &&
            PresenceLabels.HostingJoinableWatchParty(
                accountIdString,
                rooms);

        var current =
            (
                Online: online,
                WatchingLabel: watchingLabel,
                HostingJoinableWatchParty:
                    hostingJoinableWatchParty
            );
        if (lastPushed.TryGetValue(accountIdString, out var previous) && previous == current)
        {
            return;
        }

        lastPushed[accountIdString] = current;

        if (!Guid.TryParse(accountIdString, out var accountId))
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var self = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (self is null)
        {
            return;
        }

        var friendIds = await db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterAccountId == accountId || f.AddresseeAccountId == accountId))
            .Select(f => f.RequesterAccountId == accountId ? f.AddresseeAccountId : f.RequesterAccountId)
            .ToListAsync(cancellationToken);

        if (friendIds.Count == 0)
        {
            return;
        }

        var settings = await db.Settings.FirstOrDefaultAsync(s => s.Id == ServerSettings.SingletonId, cancellationToken) ?? new ServerSettings();

        var friends = await db.Accounts.Where(a => friendIds.Contains(a.Id)).ToListAsync(cancellationToken);
        foreach (var friend in friends)
        {
            if (LalafellVisibility.IsHiddenFrom(friend, self, settings))
            {
                continue;
            }

            if (!directory.TryGetSocket(friend.Id.ToString(), out var socket) || socket is null)
            {
                continue;
            }

            await SocketSend.SendAsync(socket, new SocialControl
            {
                Type = SocialSignalType.PresenceUpdate,
                AccountId = accountIdString,
                Online = online,
                WatchingLabel = watchingLabel,
                HostingJoinableWatchParty =
                    hostingJoinableWatchParty,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    // Pushes the current UserDirectory.OnlineCount to every connected client so the plugin UI can
    // show a global "users online" figure next to friends-online without polling.
    public async Task BroadcastOnlineCountAsync(CancellationToken cancellationToken)
    {
        var message = new SocialControl
        {
            Type = SocialSignalType.OnlineCount,
            OnlineCount = directory.OnlineCount,
        };

        foreach (var socket in directory.ConnectedSockets())
        {
            await SocketSend.SendAsync(socket, message, cancellationToken).ConfigureAwait(false);
        }
    }
}
