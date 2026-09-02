using AlphaChannel.Contracts;

namespace AlphaChannel.Server.Data;

// The durable identity behind a connection. Public-facing (Handle/DisplayName) - the verified
// FFXIV character that proved this account belongs to a real person lives on AccountCharacter,
// deliberately kept out of this type so nothing that touches Account by itself can leak it.
internal sealed class Account
{
    public Guid Id { get; set; }

    // Random, immutable, internal-only id (never derived from the real character name). No longer
    // used for lookup - see DisplayName below - kept as a stable identifier that survives a
    // DisplayName change/collision-retry, and shown in Settings mostly for support/debugging.
    public required string Handle { get; set; }

    // Chosen "gamer tag" - what's shown everywhere (Friends/Alpha Chat/Activity/Tweeter) and the
    // one thing other players search/add-a-friend by (exact match, case-insensitive, unique - see
    // AccountService.UpdateProfileAsync and FriendService.FindAccountByDisplayNameAsync). Defaults
    // to Handle at account creation, but onboarding requires picking a real one before it finishes.
    public required string DisplayName { get; set; }

    // A second, regenerable way to be added as a friend that doesn't require picking a public
    // handle at all - share it out of band (Discord, party chat) and it's spent/rotated after use.
    public required string InviteCode { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    // Profile - all client-editable via PATCH /me (icon/color/bio/status) plus POST/DELETE /me/avatar
    // for an optional uploaded picture. AvatarIcon is a key into a curated FontAwesome set used as
    // the fallback chip when AvatarImagePath is null (or while the image is still loading client-
    // side). Uploaded images are stored under data/avatars/ and served at GET /avatars/{file}.
    public string? AvatarIcon { get; set; }
    public string AvatarColorHex { get; set; } = "#9966FA";
    // Filename only (e.g. "{accountId}.png") inside the avatars storage root — never a client path.
    public string? AvatarImagePath { get; set; }
    public string? Bio { get; set; }
    public string? StatusMessage { get; set; }

    public bool IsBanned { get; set; }
    public string? BanReason { get; set; }
    public DateTime? BannedAtUtc { get; set; }
    public DateTime? BannedUntilUtc { get; set; } // null while IsBanned means permanent

    // X25519 public key uploaded once the plugin generates a local keypair (see DM design) - used
    // by other accounts to derive a shared secret for encrypting messages to this account.
    public byte[]? DmPublicKey { get; set; }

    // Read client-side from the live character model at sign-in (see Plugin.cs's ReadIsLalafell)
    // and OR'd in whenever a Lalafell character gets linked to this account later. Gates social
    // features via LalafellSocialStatus - see LalafellReviewService for the approve/deny flow and
    // ServerSettings.HideLalafellFromNonLalafell for the separate visibility toggle.
    public bool IsLalafell { get; set; }
    public LalafellSocialStatus LalafellSocialStatus { get; set; } = LalafellSocialStatus.NotApplicable;

    // Asked once at account creation (also editable later from Settings) - comma-separated race
    // names, purely a self-report, not itself used for any gating decision.
    public string? SelfReportedRaces { get; set; }

    // Best-effort corroboration: LodestoneRaceChecker looks the character up independently and
    // flags a contradiction with IsLalafell for admin attention. Never blocks anything by itself -
    // see LodestoneRaceChecker's own header comment for why.
    public bool LodestoneRaceMismatch { get; set; }
    public DateTime? LodestoneCheckedAtUtc { get; set; }

    // Per-account preference, asked at account creation and editable later from Settings - default
    // true (see it by default; this is an opt-out, not an opt-in, so a player who never answers the
    // question isn't silently cut off from anything). Filters Lalafell-flagged accounts out of only
    // the social surfaces (friends/activity/etc) for THIS viewer - never affects watch-along, which
    // isn't a "social app" in this sense. ServerSettings.HideLalafellFromNonLalafell is a separate
    // admin-wide override that forces the hidden behavior for everyone regardless of this value.
    public bool WantsToSeeLalafellContent { get; set; } = true;

    public PatreonTier PatreonTier { get; set; } = PatreonTier.None;
    public bool IsDeveloper { get; set; }
}

internal enum LalafellSocialStatus
{
    NotApplicable, // IsLalafell is false - this account was never gated in the first place
    Pending,
    Approved,
    Denied,
}

// Single-row table (Id is always fixed at 1) for server-wide toggles an admin can flip without a
// redeploy - see LalafellReviewService and the admin UI page.
internal sealed class ServerSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public bool HideLalafellFromNonLalafell { get; set; }
}

// The verified FFXIV character(s) behind an account. Kept in its own table specifically so no API
// response has to touch it to answer ordinary questions ("what's my friend's handle") - only the
// auth flow and ban-evasion checks ever query this table.
internal sealed class AccountCharacter
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public required string CharacterName { get; set; }
    public required string World { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime LinkedAtUtc { get; set; }
}

// One row per plugin the client's IDalamudPluginInterface.InstalledPlugins reported at last sync -
// see PluginHubService.SyncAsync, which replaces an account's whole set wholesale rather than
// diffing (installed-plugin lists change rarely and are small, so this is simpler than tracking
// adds/removes). Friends-only visibility (PluginHubService.GetFriendPluginsAsync), same posture as
// the rest of the social surface - this is still "what someone runs," not public information.
internal sealed class InstalledPlugin
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public required string InternalName { get; set; }
    public required string Name { get; set; }
    public required string Version { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

// Bearer tokens are never stored raw - only a SHA-256 hash, so a database dump doesn't hand out
// live credentials. /rt and every authenticated endpoint hash the incoming token and look it up here.
internal sealed class AuthToken
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public required string TokenHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}

internal enum FriendshipStatus
{
    Pending,
    Accepted,
    Declined,
}

internal sealed class Friendship
{
    public Guid Id { get; set; }
    public Guid RequesterAccountId { get; set; }
    public Guid AddresseeAccountId { get; set; }
    public FriendshipStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RespondedAtUtc { get; set; }
}

// Independent of Friendship - you can block someone you were never friends with. Blocking removes
// any existing friendship and prevents new friend requests and DMs in both directions.
internal sealed class Block
{
    public Guid Id { get; set; }
    public Guid BlockerAccountId { get; set; }
    public Guid BlockedAccountId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

// A 1:1 DM or a group chat - IsGroup distinguishes them, Name is a group's chosen title (null for
// 1:1, where the client just shows the other member's DisplayName). Membership lives in
// ConversationMember rather than a fixed AccountAId/AccountBId pair, so this scales to N members
// without a separate parallel "group conversation" model.
internal sealed class Conversation
{
    public Guid Id { get; set; }
    public bool IsGroup { get; set; }
    public string? Name { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

// LastReadAtUtc is a per-member read cursor ("everything up to this instant is read"), same idiom
// as ActivityReadMarker - replaces the old per-message DmMessage.ReadAtUtc, which only ever made
// sense when a conversation had exactly one other party to read anything.
internal sealed class ConversationMember
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid AccountId { get; set; }
    public DateTime JoinedAtUtc { get; set; }
    public DateTime? LastReadAtUtc { get; set; }
}

// Ciphertext + nonce + AES-GCM tag only - the server never sees plaintext or any encryption key.
// Static-static ECDH between two accounts' long-term Account.DmPublicKey values derives the same
// AES-256-GCM key on both ends, and that derivation is symmetric per pair regardless of who's
// "sender" - see AlphaChannel.Plugin/Crypto's DmCipher for the client-side half.
//
// Group E2E is sender-side fan-out, not new crypto: sending a message to a conversation with N
// other members produces N rows, each independently encrypted with the pairwise key for that one
// RecipientAccountId. GroupId ties those N rows together as "one logical message" - the sender can
// decrypt ANY one of them (their own pairwise key with that specific recipient reproduces the same
// shared secret), which is what lets them re-read their own sent history without a separate
// "to self" copy. For a 1:1 conversation this collapses to exactly one row, identical to before.
//
// CommitmentTag is HMAC-SHA512(frankingKey, plaintext), computed and sent by the sender alongside
// each ciphertext at send time. The frankingKey itself is embedded in the encrypted payload and
// never touches the server - but if a recipient (or the sender) later reports this message, their
// client can voluntarily reveal the plaintext + frankingKey, and the server can recompute the HMAC
// and compare it to this stored tag to confirm the reveal is genuine, without ever having been
// able to decrypt the message on its own. See Report.FrankingVerified.
internal sealed class DmMessage
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderAccountId { get; set; }
    public Guid RecipientAccountId { get; set; }
    public required byte[] Ciphertext { get; set; }
    public required byte[] Nonce { get; set; }
    public required byte[] Tag { get; set; }
    public required byte[] CommitmentTag { get; set; }
    public DateTime SentAtUtc { get; set; }
}

internal enum ActivityEventType
{
    StartedWatching,
    JoinedWatchAlong,
    FriendAccepted,
    PostLiked,
    PostReplied,
    Mentioned,
    VenueSaved,
    WentLive,
}

// Friends-only by construction for the "actor" side - the feed endpoint queries events belonging
// to the caller's accepted friends (plus their own). TargetAccountId is the one exception: it
// makes an event ALSO visible to one specific account regardless of friendship with the actor, for
// "someone interacted with your stuff" cases (a like/reply/mention) where the actor is a Tweeter
// follower rather than a friend - same one-directional-follow posture Task 11 already established.
internal sealed class ActivityEvent
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public ActivityEventType Type { get; set; }
    public string? Metadata { get; set; } // small JSON blob, e.g. { "title": "..." }
    public DateTime CreatedAtUtc { get; set; }
    public Guid? TargetAccountId { get; set; }
}

internal enum ReportStatus
{
    Open,
    Reviewed,
    ActionTaken,
    Dismissed,
}

// Tweeter: short text posts + one-directional follows, separate from Friendship (which is mutual
// and gates DMs/presence). No replies/media in v1 - see TweeterService's own header comment.
internal sealed class Post
{
    public Guid Id { get; set; }
    public Guid AuthorAccountId { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    // Reply thread - flat, not nested (a reply's own replies just point back at it, but the client
    // only ever renders one level at a time via GET /posts/{id}/replies). Null for a top-level post.
    public Guid? ParentPostId { get; set; }

    // A repost: Body is the optional quote-comment (empty string for a plain repost), the quoted
    // content itself lives on the referenced Post and is resolved at hydration time (see
    // TweeterService.HydrateAsync) rather than copied, so an edit/delete of the original is
    // reflected (or, for delete, the repost just shows "original post deleted").
    public Guid? RepostOfPostId { get; set; }

    // A link, not an uploaded file - deliberately not in the file-hosting/content-moderation
    // business, same reasoning as Account.AvatarIcon.
    public string? ImageUrl { get; set; }
}

internal sealed class PostLike
{
    public Guid Id { get; set; }
    public Guid PostId { get; set; }
    public Guid AccountId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

// One-directional, unlike Friendship - anyone can follow anyone (subject to the same
// LalafellVisibility/block checks as everything else social).
internal sealed class Follow
{
    public Guid Id { get; set; }
    public Guid FollowerAccountId { get; set; }
    public Guid FolloweeAccountId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

// One row per account - POST /activity/read moves LastReadAtUtc forward. Separate from ActivityEvent
// itself since one event row is visible in many different friends' feeds at once, so "read" can't
// live on the event - it has to be a per-viewer cursor.
internal sealed class ActivityReadMarker
{
    public Guid AccountId { get; set; }
    public DateTime LastReadAtUtc { get; set; }
}

internal sealed class Report
{
    public Guid Id { get; set; }
    public Guid ReporterAccountId { get; set; }
    public Guid ReportedAccountId { get; set; }
    public Guid? ReportedMessageId { get; set; }
    public Guid? ReportedPostId { get; set; }
    public required string Reason { get; set; }
    public string? Details { get; set; }
    public ReportStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public string? ReviewNote { get; set; }

    // Only ever populated for a DM-message report - the reporter's client voluntarily decrypted
    // and revealed this. FrankingVerified records whether it checked out against DmMessage's stored
    // CommitmentTag at the moment the report was filed - see DmMessage's own doc comment.
    public string? RevealedBody { get; set; }
    public string? FrankingKeyBase64 { get; set; }
    public bool? FrankingVerified { get; set; }
}

// A named, saved screen placement - client-side equivalent is Configuration.ScreenPositionPreset,
// but that's purely local. A Venue is the same idea made shareable: TerritoryTypeId anchors it to a
// specific zone (world-space X/Y/Z is meaningless outside the zone it was recorded in), so a friend
// visiting the same zone can load the exact same spot. Friends-only visibility (VenueService.
// GetFriendVenuesAsync), same posture as the rest of the social surface.
internal sealed class Venue
{
    public Guid Id { get; set; }
    public Guid OwnerAccountId { get; set; }
    public required string Name { get; set; }
    public int TerritoryTypeId { get; set; }
    public float ScreenX { get; set; }
    public float ScreenY { get; set; }
    public float ScreenZ { get; set; }
    public float ScreenYaw { get; set; }
    public float ScreenScale { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

// One per account - hash-only storage, same reasoning as AuthToken (a database dump shouldn't hand
// out live credentials). The raw key is only ever shown once, at (re)generation time, formatted as
// "{accountId}.{secret}" so MediaMTX's publish-auth webhook (LiveEndpoints' media group) can parse
// the account back out of the RTMP path without a lookup-by-secret scan.
internal sealed class StreamKey
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public required string KeyHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RotatedAtUtc { get; set; }
}

// MediaMTX's own publisher state (via its runOnReady/runOnNotReady hooks calling back into
// LiveEndpoints) is the source of truth for these rows, not a client-invoked start/stop - OBS can
// crash or lose connection without the plugin ever hearing about it, so trusting a client-asserted
// "I'm live" flag would drift from reality almost immediately. EndedAtUtc null means currently live.
internal sealed class LiveSession
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
}
