using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin.Auth;

// One of these per FFXIV character that's been signed in on this install, keyed by LocalContentId
// in Configuration.CharacterSessions - same idiom as CharacterDisplayNames. Multiple characters can
// point at the same AccountId once linked (see AuthClient's link flow), which is what makes
// multi-character linking "just work" everywhere downstream that keys off AccountId.
[Serializable]
internal sealed class CharacterSession
{
    public string AccountId { get; set; } = "";
    public string Token { get; set; } = "";
    public string Handle { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? AvatarIcon { get; set; }
    public string AvatarColorHex { get; set; } = "#9966FA";
    // Relative relay path (/avatars/...) when a custom picture is set; null = icon+color chip.
    public string? AvatarImageUrl { get; set; }
    public string? Bio { get; set; }
    public string? StatusMessage { get; set; }

    // Redeemable by anyone you share it with out of band (Discord, voice) to instantly become
    // friends, no name search needed - see FriendService.RedeemInviteCodeAsync. Rotates whenever
    // someone actually redeems it (so a shared code can't be reused by someone else who saw it),
    // which is why this is refreshed from the server rather than treated as a fixed value.
    public string InviteCode { get; set; } = "";

    public PatreonTier PatreonTier { get; set; } = PatreonTier.None;
    public bool IsDeveloper { get; set; }
}
