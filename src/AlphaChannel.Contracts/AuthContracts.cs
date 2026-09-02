namespace AlphaChannel.Contracts;

// REST contracts for the XIVAuth device-flow sign-in, mirrored from Aetherphone's
// /auth/xivauth/start + /auth/xivauth/poll shape. AlphaChannel's server is the actual OAuth client
// registered with XIVAuth (client_id/secret) - the plugin never talks to XIVAuth directly, it just
// opens a browser to VerificationUri and polls this server, same reasoning Aetherphone has: a
// Dalamud plugin can't receive an OAuth redirect callback.

// Fresh sign-in uses POST /auth/xivauth/start (anonymous). Linking an additional character to an
// already-signed-in account uses the separate POST /auth/xivauth/link/start (Bearer-authed) -
// which account to link into comes from the Authorization header there, never from a client-
// supplied field, so a forged request body can't link a character onto someone else's account.
// IsLalafell is read client-side from the live character model (not verified server-side beyond
// trusting the plugin) - see AlphaChannel.Server's Lalafell review flow for what it gates.
public sealed record AuthStartRequest(string CharacterName, string World, bool IsLalafell = false);

public sealed record AuthStartResponse(
    string FlowId,
    string UserCode,
    string VerificationUri,
    string? VerificationUriComplete,
    int IntervalSeconds,
    int ExpiresInSeconds);

public sealed record AuthPollRequest(string FlowId);

public enum AuthPollStatus
{
    Pending,       // normal "still waiting on the user" state during polling
    Success,
    Denied,
    Expired,
    Banned,
    Error,
}

public sealed record AuthPollResponse(
    AuthPollStatus Status,
    string? Token,
    AccountSummary? Account,
    string? ErrorMessage,
    bool IsNewAccount = false);

// Deliberately excludes the verified character name/world - see AlphaChannel.Server.Data.Account's
// doc comment. Self-view only (GET/PATCH /me) - includes InviteCode, which must never be exposed
// when viewing someone else's profile (see AccountProfileDto below for that case).
// AvatarImageUrl is a relative path like /avatars/{file} when the player uploaded a custom pic;
// clients resolve it against the relay base URL. Null = use AvatarIcon + AvatarColorHex chip.
public enum PatreonTier
{
    None = 0,
    Unknown = 1,
    Tier1 = 2,
    Tier2 = 3,
    Tier3 = 4,
}

public sealed record AccountSummary(
    string AccountId, string Handle, string DisplayName, string InviteCode,
    string? AvatarIcon, string AvatarColorHex, string? Bio, string? StatusMessage,
    string? AvatarImageUrl,
    PatreonTier PatreonTier = PatreonTier.None,
    bool IsDeveloper = false);

// The one deliberate exception to "real character name/world is never returned to a client" - only
// ever the caller's own linked characters, via GET /me/characters, never anyone else's.
public sealed record LinkedCharacterDto(string CharacterName, string World, bool IsPrimary);

// Asked once at account creation (IsNewAccount on the poll response), also editable later from
// Settings. Races is a free-form self-report, not used for any gating decision by itself.
public sealed record OnboardingRequest(string[] Races, bool WantsToSeeLalafellContent);

// DisplayName is what shows up throughout Friends/Alpha Chat/Activity/Tweeter, and (see
// FriendService.FindAccountByDisplayNameAsync) is also the add-a-friend lookup key - a chosen
// "gamer tag" players can actually remember and share, unlike the random Handle. Must be unique
// (case-insensitive); the server returns 409 if it's taken. AvatarIcon is a key into a client-side
// curated icon set used when no custom AvatarImageUrl is set. Every field is null-means-unchanged,
// so a caller can update just one at a time. Custom pictures use POST/DELETE /me/avatar instead.
public sealed record UpdateProfileRequest(
    string? DisplayName, string? AvatarIcon, string? AvatarColorHex, string? Bio, string? StatusMessage);

// Someone else's profile, via GET /accounts/{id}/profile - friends-only (or self, via GET /me
// instead). FriendsSinceUnix is null when viewing your own profile.
public sealed record AccountProfileDto(
    string AccountId, string Handle, string DisplayName,
    string? AvatarIcon, string AvatarColorHex, string? Bio, string? StatusMessage, long? FriendsSinceUnix,
    string? AvatarImageUrl,
    bool IsDeveloper = false,
    PatreonTier PatreonTier = PatreonTier.None);

public sealed record AdminPatchAccountRequest(PatreonTier? PatreonTier, bool? IsDeveloper);
