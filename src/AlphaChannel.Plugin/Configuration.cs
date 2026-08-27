using AlphaChannel.Plugin.Auth;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace AlphaChannel.Plugin;

[Serializable]
internal sealed class ScreenPositionPreset
{
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
    public float Scale { get; set; } = 1.0f;
}

// Ported from Aetherphone's Configuration.cs's own VideoQueueRecord - kept as its own DTO rather
// than serializing VideoQueueEntry directly so this doesn't couple to AetherStreamQueue's
// internals (it also carries a Guid Id and mutable fields that don't belong in a saved record).
[Serializable]
internal sealed class VideoQueueRecord
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public double? DurationSeconds { get; set; }
    public string? ThumbnailUrl { get; set; }
}

[Serializable]
internal sealed class RecentlyWatchedVideoRecord
{
    public string Url { get; set; } = "";

    public string Title { get; set; } = "";

    public string? ThumbnailUrl { get; set; }

    public string ChannelName { get; set; } = "";

    public double WatchedSeconds { get; set; }

    public double DurationSeconds { get; set; }

    public DateTime LastWatchedUtc { get; set; }
}

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // First-run experience has been completed.
    public bool HasCompletedFirstLaunch { get; set; }

    public string RelayServerUrl { get; set; } = "https://alphachannel.duckdns.org";

    // Off by default — prod/dev stack switcher stays out of player Settings. Flip true in the
    // plugin config JSON only when you need to point this install at the isolated dev relay.
    public bool ShowServerStackSwitcher { get; set; }

    // Keyed by IClientState.LocalContentId, same idiom as CharacterDisplayNames below - a
    // character's sign-in is tied to the FFXIV character, not the plugin install. Two entries can
    // point at the same AccountId once linked (see Auth/AuthClient.cs). Only Watch-along, Friends,
    // Messages, and Activity require an entry here for the current character; Player/Screen/
    // Settings keep working with none, same zero-friction default as before accounts existed.
    public Dictionary<ulong, CharacterSession> CharacterSessions { get; set; } = new();

    // Keyed by AccountId (not LocalContentId) - DM identity belongs to the account, not whichever
    // character happens to be signed in as it. Base64 PKCS8 private key, DPAPI-protected on Windows
    // where available (see Crypto/KeyVault.cs) - the key never leaves this machine either way, this
    // is defense-in-depth against other local processes, not network protection.
    public Dictionary<string, string> DmPrivateKeys { get; set; } = new();

    // Keyed by AccountId, same reasoning as DmPrivateKeys - a locally-cached copy of the raw stream
    // key purely for convenience redisplay on the Go Live page (the server only ever stores a hash,
    // see Server/Data/Entities.cs's StreamKey). If this local cache is ever lost, the only recovery
    // is hitting Regenerate - that's acceptable one-time friction, not a bug, since regenerating
    // also instantly invalidates anyone else's copy of the old key.
    public Dictionary<string, string> StreamKeys { get; set; } = new();

    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }

    // Path to a cookies.txt file the player exported themselves from their own logged-in browser
    // session - never anything we generate or transmit, just a local file path handed to yt-dlp so
    // age-restricted videos can play. Opt-in and empty by default.
    public string? YouTubeCookiesPath { get; set; }

    // Alternative to YouTubeCookiesPath - reads cookies directly from a local Firefox profile
    // instead of a manually-exported file. Best-effort: depends on yt-dlp being able to locate and
    // read that profile from inside this process (see MpvRenderer's own note on the caveats).
    public bool UseFirefoxCookies { get; set; }

    // Keyed by IClientState.LocalContentId - the display name a player picked is tied to the FFXIV
    // character they were playing when they picked it, not to the Windows/plugin install, so an alt
    // gets its own prompt instead of inheriting the main character's name.
    public Dictionary<ulong, string> CharacterDisplayNames { get; set; } = new();

    public List<VideoQueueRecord> VideoQueue { get; set; } = new();

    // YouTube videos favourited by the player.
    // Store stable YouTube video IDs rather than full URLs.
    public List<string> FavouriteYouTubeVideoIds { get; set; } = new();

    public List<RecentlyWatchedVideoRecord> RecentlyWatchedVideos { get; set; } = new ();

    // YouTube channels subscribed to inside AlphaChannel.
    // Store stable channel IDs rather than display names.
    public List<string> SubscribedYouTubeChannelIds { get; set; } = new();

    // Display-name cache for locally-managed YouTube subscriptions.
    // The channel ID remains the real identity.
    public Dictionary<string, string> SubscribedYouTubeChannelNames { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<ScreenPositionPreset> ScreenPresets { get; set; } = new();

    // Plugin window chrome palette - see UiTheme.cs / ThemeCatalog. Defaults to Purple (mockup).
    public UiTheme UiTheme { get; set; } = UiTheme.Purple;

    // Window/sidebar/card surfaces — independent of accent color. Theme = use the accent pack's defaults.
    public UiBackground UiBackground { get; set; } = UiBackground.Theme;

    // Copied under the plugin config folder when the player applies a custom image (png/jpg/webp).
    public string? CustomBackgroundPath { get; set; }

    // Dark overlay strength over the custom image (0 = full photo, 1 = fully dimmed).
    public float CustomBackgroundDim { get; set; } = 0.30f;

    // Home welcome illustration (couch / castle art). Off = text + CTA only.
    public bool ShowHomeHeroImage { get; set; } = true;

    // Optional FFXIV discovery shelf on the media-hub Home page.
    public bool ShowFfxivYouTubeSection { get; set; } = true;

    // Trending video topic preferences.
    public bool TrendingGaming { get; set; } = true;
    public bool TrendingMMORPG { get; set; } = true;
    public bool TrendingFinalFantasy { get; set; } = true;
    public bool TrendingAnime { get; set; } = true;
    public bool TrendingMovies { get; set; } = true;
    public bool TrendingTvShows { get; set; } = true;
    public bool TrendingMusic { get; set; } = true;
    public bool TrendingMemes { get; set; } = true;

    public bool TrendingWildlife { get; set; } = true;
    public bool TrendingArchitecture { get; set; } = true;
    public bool TrendingScience { get; set; } = true;
    public bool TrendingSpace { get; set; } = true;
    public bool TrendingHistory { get; set; } = true;
    public bool TrendingTechnology { get; set; } = true;

    public bool TrendingPets { get; set; } = true;
    public bool TrendingFood { get; set; } = true;
    public bool TrendingTravel { get; set; } = true;
    public bool TrendingCars { get; set; } = true;
    public bool TrendingSports { get; set; } = true;

    // Optional replacement for the bundled Home hero art (copied under Backgrounds/).
    public string? CustomHomeHeroPath { get; set; }

    // Persist native /tell history per character under the plugin config directory (Whispers/).
    // Off = session-only mirror; on = Linkpearl-style archive (default).
    public bool ArchiveWhispersToDisk { get; set; } = true;

    // Mirror incoming Alpha Channel watch-party chat messages into the FFXIV chatbox.
    public bool RelayPartyChatToGameChat { get; set; }

    // Last placements for the full window and the minimized capsule — same idea as Aetherphone's
    // MaximizedPosition / MinimizedPosition so minimize/restore/reopen land where you left them.
    public Vector2? MaximizedPosition { get; set; }
    public Vector2? MinimizedPosition { get; set; }

    // Walk-up auto-view for public DJ sets and streams. Off by default — probing nearby players
    // can feel like the UI is reloading until a real public host is found.
    public bool AutoWatchNearby { get; set; } = false;

    // How close (yalms) another player must be before AutoWatchNearby tries to join them.
    public float AutoWatchRadiusYalms { get; set; } = 18f;

    public Vector3 ScreenPosition { get; set; }
    public float ScreenYaw { get; set; }
    public float ScreenScale { get; set; } = 1f;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}
