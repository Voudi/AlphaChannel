namespace AlphaChannel.Contracts;

// Ported from Aetherphone's Core/Telephony/Contracts/Signals.cs - the stream.* slice only. That
// file multiplexes call.*/chat.*/velvet.*/etc signals onto the same socket because Aetherphone is
// a whole phone; AlphaChannel only ever had streaming, so there is nothing else to multiplex.
public static class SignalType
{
    public const string StreamState = "stream.state";
    public const string StreamJoin = "stream.join";
    public const string StreamLeave = "stream.leave";
    public const string StreamJoined = "stream.joined";
    public const string StreamDeclined = "stream.declined";
    public const string StreamRoster = "stream.roster";
    public const string StreamLeft = "stream.left";
    public const string StreamEnded = "stream.ended";

    // Sent by the client right after connecting, carrying its current DisplayName so the server can
    // show real names in rosters instead of raw UserId GUIDs.
    public const string StreamHello = "stream.hello";

    // Server -> client push telling this client its name was cleared by an admin reset and it needs
    // to prompt the player for a new one - see AlphaChannel.Server's /admin/reset-username.
    public const string StreamRenameRequired = "stream.renameRequired";

    // Host -> server, naming (via HostId, carrying the target's real UserId here - the host already
    // has real UserIds from their own roster) a current viewer to hand hosting duty to.
    public const string StreamTransferHost = "stream.transferHost";

    // Server -> everyone in the room (old host, new host, remaining viewers) once a transfer
    // completes - HostId carries the new host's UserId. Each client compares that against its own
    // UserId to work out whether it just became the host, was just demoted to viewer, or is
    // unaffected but should update who it recognizes as host.
    public const string StreamHostTransferred = "stream.hostTransferred";

    // Viewer -> server -> broadcast to the whole room (including the sender, for simplicity - see
    // the client-side note on why that's fine here). Reaction carries a short glyph/emoji, not a
    // free-text field - this is a fun reaction burst, not a chat feature.
    public const string StreamReaction = "stream.reaction";

    // Watch-party chat: short free-text lines broadcast to everyone in the room (host + viewers).
    // Ephemeral to the room lifetime — not Alpha Chat / E2E DMs.
    public const string StreamChat = "stream.chat";
}

// Same flat envelope shape as Aetherphone's CallControl, trimmed to only the fields the stream.*
// signals actually use - nulls are omitted on the wire, unknown fields are ignored, so this stays
// wire-compatible with a server that happens to also speak the fuller Aetherphone dialect.
public sealed record StreamControl
{
    public string Type { get; init; } = string.Empty;
    public string? HostId { get; init; }
    public string? UserId { get; init; }
    public string? DisplayName { get; init; }
    public string? Reason { get; init; }
    public ParticipantInfo[]? Participants { get; init; }
    public string? Reaction { get; init; }

    // stream.chat body — capped client-side; server relays blindly like reactions.
    public string? ChatText { get; init; }

    public string? Url { get; init; }
    public double? PositionSeconds { get; init; }
    public bool? Paused { get; init; }

    // Host-set, carried on stream.state and persisted on the Room server-side until changed again
    // (an omitted/null value does NOT reset it to false - see ConnectionHandler's handling). When
    // true, PresenceLabels hides what/who this room's participants are watching from friends -
    // still shows a neutral "watching privately" rather than nothing, so presence doesn't look like
    // they've gone offline.
    public bool? IsPrivate { get; init; }

    // Host-set on stream.state (null = unchanged). Description is capped server-side (~280).
    public string? Description { get; init; }
    public string? Location { get; init; }
    public RoomKind? Kind { get; init; }

    // Host sets on stream.state; viewers send on stream.join. Never stored on LastState / never
    // echoed to other clients. Empty string on state clears the lock.
    public string? Password { get; init; }

    // The host's world-anchored screen transform (VideoEngine.ScreenPosition/ScreenYaw/ScreenScale).
    // Omitted entirely if the host's screen isn't active. Every viewer's client applies this to its
    // own local ScreenPainter - there is no shared/networked 3D object.
    public float? ScreenX { get; init; }
    public float? ScreenY { get; init; }
    public float? ScreenZ { get; init; }
    public float? ScreenYaw { get; init; }
    public float? ScreenScale { get; init; }
}

public sealed record ParticipantInfo(string UserId, string DisplayName);

public enum RoomKind
{
    Public = 0,
    Locked = 1,
    Venue = 2,
}

public sealed record RoomDirectoryDto(
    string HostAccountId,
    string HostDisplayName,
    string? Description,
    string? Location,
    RoomKind Kind,
    int ViewerCount,
    bool Paused,
    bool HasMedia,
    string? Url);
