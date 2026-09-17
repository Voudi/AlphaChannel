namespace AlphaChannel.Contracts;

// Push-only signal family for friends/presence/DMs/activity, sharing the /rt socket with
// stream.* traffic but never sent client -> server: every mutation (send a friend request, accept
// one, send a DM, etc.) goes over REST instead (see AlphaChannel.Server's Auth/Social/Moderation
// endpoint groups) - the socket only ever pushes the *result* to whoever needs to know about it.
// That split means ConnectionHandler's receive-loop switch on StreamControl.Type never needs to
// branch on these at all; only the client-side demux (StreamClient) has to tell the two families
// apart, since it's the one place that receives both.
public static class SocialSignalType
{
    public const string FriendRequestReceived = "friend.requestReceived";
    public const string FriendAccepted = "friend.accepted";
    public const string FriendRemoved = "friend.removed";
    public const string PresenceUpdate = "presence.update";
    // Global connected-client count (UserDirectory sockets) — pushed to everyone on connect/disconnect.
    public const string OnlineCount = "presence.onlineCount";
    public const string DmMessage = "dm.message";
    public const string ActivityNew = "activity.new";
}

// Deliberately no field-name overlap with StreamControl (AccountId vs UserId/HostId) so the two
// envelope shapes stay visually distinguishable even before the Type prefix is checked.
public sealed record SocialControl
{
    public string Type { get; init; } = string.Empty;

    // Who this push is about - the other party in a friend event, the sender of a DM, etc.
    public string? AccountId { get; init; }
    public string? DisplayName { get; init; }

    public string? RequestId { get; init; }

    public string? ConversationId { get; init; }
    public string? MessageId { get; init; }
    public string? Ciphertext { get; init; }
    public string? Nonce { get; init; }
    public string? Tag { get; init; }
    public string? CommitmentTag { get; init; }
    public long? TimestampUnix { get; init; }

    public bool? Online { get; init; }

    // Global connected AlphaChannel clients — set on presence.onlineCount pushes only.
    public int? OnlineCount { get; init; }

    // Already-resolved display text (e.g. "watching Alice's stream") - no client-side lookup needed.
    public string? WatchingLabel { get; init; }

    //
    // True only when this account currently hosts a non-private
    // Watch Party that one of their friends can attempt to join.
    //
    public bool? HostingJoinableWatchParty { get; init; }

    public string? ActivityType { get; init; }
}
