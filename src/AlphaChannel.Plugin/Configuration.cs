using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.Crypto;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;
namespace AlphaChannel.Plugin;
using AlphaChannel.Plugin.Video;

[Serializable]
internal sealed class ScreenPositionPreset
{
    public string Name { get; set; } = "";

    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }

    //
    // Retained for presets created before independent width/height
    // scaling was introduced.
    //
    public float Scale { get; set; } = 1.0f;

    public bool DisableFixedScaleRatio { get; set; }

    //
    // Nullable so an old preset without these JSON properties can fall
    // back to Scale instead of deserializing them as zero.
    //
    public float? WidthScale { get; set; }
    public float? HeightScale { get; set; }
}

[Serializable]
internal sealed class GameLibraryEntry
{
    public string Icon { get; set; } = "Gamepad";
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
}

// Persisted queue data, kept separate from VideoQueueEntry so saved configuration
// does not depend on the queue's runtime implementation.
[Serializable]
internal sealed class VideoQueueRecord
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public double? DurationSeconds { get; set; }
    public string? ThumbnailUrl { get; set; }

    // Position to resume from when playback was paused or stopped.
    public double ResumePositionSeconds { get; set; }
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
internal sealed class SavedRadioStationRecord
{
    public string Name { get; set; } = "";

    public string Url { get; set; } = "";
}

[Serializable]
internal sealed class SavedImageRecord
{
    public Guid Id { get; set; } =
        Guid.NewGuid();

    public string Name { get; set; } =
        string.Empty;

    public string Url { get; set; } =
        string.Empty;

    public DateTime SavedAtUtc { get; set; } =
        DateTime.UtcNow;
}

[Serializable]
internal sealed class CachedBrowseVideoRecord
{
    public string Title { get; set; } =
        string.Empty;

    public string Url { get; set; } =
        string.Empty;

    public string ChannelName { get; set; } =
        string.Empty;

    public double? DurationSeconds { get; set; }

    public string? ThumbnailUrl { get; set; }

    public long? ViewCount { get; set; }

    public DateTimeOffset? UploadDate { get; set; }

    public string? ChannelId { get; set; }
}

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    internal const string ProductionRelayServerUrl = "https://alphachannel.duckdns.org";

    // Keep the legacy default at 1 so configurations written before the
    // version field existed are treated as pre-baseline configurations.
    // New configurations receive the current version in ConfigurationMigration.
    public int Version { get; set; } = 1;
    public List<GameLibraryEntry> GameLibrary { get; set; } = new();
    public string? SelectedSnesLibraryHash { get; set; }
    public string? SelectedGameBoyLibraryHash { get; set; }
    public string? SelectedNesLibraryHash { get; set; }
    public string? SelectedGameBoyAdvanceLibraryHash { get; set; }
    public string? SelectedMasterSystemLibraryHash { get; set; }
    public string? SelectedGameGearLibraryHash { get; set; }

    // First-run experience has been completed.
    public bool HasCompletedFirstLaunch { get; set; }

    public string RelayServerUrl { get; set; } = ProductionRelayServerUrl;

    // Local Patreon entitlement simulation. These values are intentionally
    // separate so future server-backed membership can retain the tier value.
    public bool PatreonMember { get; set; }

    public int? PatreonMembershipTier { get; set; }

    // Off by default — prod/dev stack switcher stays out of player Settings. Flip true in the
    // plugin config JSON only when you need to point this install at the isolated dev relay.
    public bool ShowServerStackSwitcher { get; set; }

    // Keyed by IClientState.LocalContentId, same idiom as CharacterDisplayNames below - a
    // character's sign-in is tied to the FFXIV character, not the plugin install. Two entries can
    // point at the same AccountId once linked (see Auth/AuthClient.cs). Only Watch-along, Friends,
    // Messages, and Activity require an entry here for the current character; Player/Screen/
    // Settings keep working with none, same zero-friction default as before accounts existed.
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<ulong, CharacterSession> CharacterSessions { get; set; } = new();

    // Newtonsoft deserialize-only bridge for configurations written before
    // secrets moved into the encrypted vault. ShouldSerialize keeps this
    // plaintext property out of every newly saved configuration.
    [Newtonsoft.Json.JsonProperty("CharacterSessions")]
    private Dictionary<ulong, CharacterSession>? LegacyCharacterSessions
    {
        get => null;
        set
        {
            if (value is not null)
                CharacterSessions = value;
        }
    }

    private bool ShouldSerializeLegacyCharacterSessions() => false;

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
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, string> StreamKeys { get; set; } = new();

    [Newtonsoft.Json.JsonProperty("StreamKeys")]
    private Dictionary<string, string>? LegacyStreamKeys
    {
        get => null;
        set
        {
            if (value is not null)
                StreamKeys = value;
        }
    }

    private bool ShouldSerializeLegacyStreamKeys() => false;

    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }

    //
    // Default to the real FFmpeg frequency spectrum during testing.
    // ClassicBars preserves the existing 13-bar shader.
    //
    public AudioVisualizerMode AudioVisualizerMode { get; set; } =
        AudioVisualizerMode.FfmpegSpectrum;

    //
    // SNES Player 1 keyboard controls.
    //
    // Stored as VirtualKey integer values so existing config files
    // continue to deserialize cleanly and bindings can be changed
    // without touching the emulator core.
    //

    public int SnesKeyUp { get; set; } =
        (int)VirtualKey.UP;

    public int SnesKeyDown { get; set; } =
        (int)VirtualKey.DOWN;

    public int SnesKeyLeft { get; set; } =
        (int)VirtualKey.LEFT;

    public int SnesKeyRight { get; set; } =
        (int)VirtualKey.RIGHT;

    public int SnesKeyA { get; set; } =
        (int)VirtualKey.X;

    public int SnesKeyB { get; set; } =
        (int)VirtualKey.Z;

    public int SnesKeyX { get; set; } =
        (int)VirtualKey.S;

    public int SnesKeyY { get; set; } =
        (int)VirtualKey.A;

    public int SnesKeyL { get; set; } =
        (int)VirtualKey.Q;

    public int SnesKeyR { get; set; } =
        (int)VirtualKey.W;

    public int SnesKeyStart { get; set; } =
        (int)VirtualKey.RETURN;

    public int SnesKeySelect { get; set; } =
        (int)VirtualKey.RSHIFT;

    // Shared Player 1 controller layout. Each emulator uses the controls its
    // original console provides; values map directly to Dalamud GamepadButtons.
    public int GamepadUp { get; set; } = (int)GamepadButtons.DpadUp;
    public int GamepadDown { get; set; } = (int)GamepadButtons.DpadDown;
    public int GamepadLeft { get; set; } = (int)GamepadButtons.DpadLeft;
    public int GamepadRight { get; set; } = (int)GamepadButtons.DpadRight;
    public int GamepadA { get; set; } = (int)GamepadButtons.East;
    public int GamepadB { get; set; } = (int)GamepadButtons.South;
    public int GamepadX { get; set; } = (int)GamepadButtons.North;
    public int GamepadY { get; set; } = (int)GamepadButtons.West;
    public int GamepadL { get; set; } = (int)GamepadButtons.L1;
    public int GamepadR { get; set; } = (int)GamepadButtons.R1;
    public int GamepadStart { get; set; } = (int)GamepadButtons.Start;
    public int GamepadSelect { get; set; } = (int)GamepadButtons.Select;

    /// <summary>
    /// UTC Unix timestamp until which YouTube PO-token compatibility
    /// mode should remain active.
    ///
    /// Zero means normal Android playback.
    /// </summary>
    public long YouTubePoCompatibilityUntilUnixSeconds
    {
        get;
        set;
    }

    /// <summary>
    /// When enabled, all YouTube videos use the higher-quality mweb
    /// PO-token route instead of beginning with the faster Android route.
    ///
    /// Disabled preserves the automatic Android → PO-token fallback policy.
    /// </summary>
    public bool PreferHighQualityYouTubeVideos
    {
        get;
        set;
    }


    /// <summary>
    /// Explicit consent to use the YouTube account signed into Alpha
    /// Channel's embedded browser. Detecting a browser login never enables
    /// this automatically.
    /// </summary>
    public bool YouTubeEmbeddedBrowserSessionEnabled { get; set; }

    // Keyed by IClientState.LocalContentId - the display name a player picked is tied to the FFXIV
    // character they were playing when they picked it, not to the Windows/plugin install, so an alt
    // gets its own prompt instead of inheriting the main character's name.
    public Dictionary<ulong, string> CharacterDisplayNames { get; set; } = new();
    public List<VideoQueueRecord> VideoQueue { get; set; } = new();

    //
    // Start empty so Newtonsoft does not append deserialized profiles
    // after three pre-created null entries. QueueManager fills the list
    // to the currently allowed slot count after configuration is loaded.
    //
    public List<SavedQueueProfile?> SavedQueueProfiles { get; set; } =
        new();

    public int ActiveQueueSlot { get; set; } = 0;

    // Shared across every saved queue. Existing configurations that do
    // not contain this property receive the enabled default.
    public bool AutoPlayNextQueueVideo { get; set; } = true;

    // YouTube videos favourited by the player.
    // Store stable YouTube video IDs rather than full URLs.
    public List<string> FavouriteYouTubeVideoIds { get; set; } =
        new();

    //
    // Persisted metadata for favourited YouTube videos.
    //
    // The list follows FavouriteYouTubeVideoIds ordering, with the most
    // recently favourited video first.
    //
    public List<CachedBrowseVideoRecord> FavouriteVideoCache
    {
        get;
        set;
    } = new();

    public DateTime FavouriteVideoCacheUpdatedUtc
    {
        get;
        set;
    }

    public string? FavouriteVideoCacheSignature
    {
        get;
        set;
    }

    //
    // Persisted Browse Videos discovery cache.
    //
    // Videos are grouped by the topic that fetched them because the same
    // YouTube video may legitimately appear under multiple topics.
    //
    public Dictionary<string, List<CachedBrowseVideoRecord>>
        BrowseVideoTopicCache
    {
        get;
        set;
    } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime BrowseVideoCacheUpdatedUtc
    {
        get;
        set;
    }

    public string? BrowseVideoCacheTopicSignature
    {
        get;
        set;
    }

    //
    // Persisted FFXIV homepage discovery results, independent of selected topics.
    //
    public List<CachedBrowseVideoRecord> FfxivVideoCache { get; set; } = new();

    public DateTime FfxivVideoCacheUpdatedUtc { get; set; }

    //
    // Persisted subscription feed cache.
    //
    // Entries are grouped by stable YouTube channel ID so the UI can filter
    // subscriptions and apply the 5/10/15 limit independently per channel.
    //
    public Dictionary<string, List<CachedBrowseVideoRecord>>
        SubscriptionVideoChannelCache
    {
        get;
        set;
    } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime SubscriptionVideoCacheUpdatedUtc
    {
        get;
        set;
    }

    public string? SubscriptionVideoCacheSignature
    {
        get;
        set;
    }

    //
    // Twitch channels favourited by the player.
    //
    // Store normalized lower-case channel usernames rather than full URLs.
    // Live status and stream metadata are temporary and are not written to
    // configuration.
    //

    public List<string> FavouriteTwitchChannels { get; set; } =
        new();

    public List<RecentlyWatchedVideoRecord> RecentlyWatchedVideos { get; set; } =
        new();

    //
    // User-added direct internet-radio streams.
    //

    public List<SavedRadioStationRecord> SavedRadioStations { get; set; } =
        new();

    //
    // Personal image gallery. Only the public image URL and local
    // presentation details are persisted; downloaded preview textures
    // remain part of the temporary image-preview cache.
    //

    public List<SavedImageRecord> SavedImages { get; set; } =
        new();

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

    // Plugin window size. Design is the original 1220×840 canvas; named presets clamp to the game viewport.
    public UiWindowSizePreset WindowSizePreset { get; set; } = UiWindowSizePreset.Design;

    public float WindowWidth { get; set; } = 1220f;

    public float WindowHeight { get; set; } = 840f;

    // Copied under the plugin config folder when the player applies a custom image (png/jpg/webp).
    public string? CustomBackgroundPath { get; set; }

    // Dark overlay strength over the custom image (0 = full photo, 1 = fully dimmed).
    public float CustomBackgroundDim { get; set; } = 0.30f;

    // Home welcome illustration (couch / castle art). Off = text + CTA only.
    public bool ShowHomeHeroImage { get; set; } = true;

    // Optional FFXIV discovery shelf on the media-hub Home page.
    public bool ShowFfxivYouTubeSection { get; set; } = true;

    // Subscribed YouTube topic preferences.
    //
    // New installations begin with no subscribed topics. Existing values
    // already written to configuration JSON remain unchanged.
    public bool TrendingGaming { get; set; }
    public bool TrendingMMORPG { get; set; }
    public bool TrendingFinalFantasy { get; set; }
    public bool TrendingAnime { get; set; }
    public bool TrendingMovies { get; set; }
    public bool TrendingTvShows { get; set; }
    public bool TrendingMusic { get; set; }
    public bool TrendingMemes { get; set; }

    public bool TrendingCartoons { get; set; }
    public bool TrendingHorror { get; set; }
    public bool TrendingSciFi { get; set; }
    public bool TrendingComedy { get; set; }
    public bool TrendingMinecraft { get; set; }
    public bool TrendingDisney { get; set; }
    public bool TrendingFantasy { get; set; }

    public bool TrendingWildlife { get; set; }
    public bool TrendingArchitecture { get; set; }
    public bool TrendingScience { get; set; }
    public bool TrendingSpace { get; set; }
    public bool TrendingHistory { get; set; }
    public bool TrendingTechnology { get; set; }
    public bool TrendingUrbanExploration { get; set; }

    public bool TrendingPets { get; set; }
    public bool TrendingFood { get; set; }
    public bool TrendingTravel { get; set; }
    public bool TrendingCars { get; set; }
    public bool TrendingSports { get; set; }
    public bool TrendingArtsAndCrafts { get; set; }
    public bool TrendingCosplaying { get; set; }
    public bool TrendingDiy { get; set; }
    public bool TrendingFashion { get; set; }

    // Optional replacement for the bundled Home hero art (copied under Backgrounds/).
    public string? CustomHomeHeroPath { get; set; }

    // Mirror incoming Alpha Channel watch-party chat messages into the FFXIV chatbox.
    public bool RelayPartyChatToGameChat { get; set; }

    // Remember the full window and minimized capsule positions across minimize, restore, and reopen.
    public Vector2? MaximizedPosition { get; set; }
    public Vector2? MinimizedPosition { get; set; }

    public Vector2? MiniPlayerPosition { get; set; }
    public float MiniPlayerWidth { get; set; } = 560f;
    public float MiniPlayerHeight { get; set; } = 380f;
    public Vector2? MiniChatPosition { get; set; }
    public float MiniChatWidth { get; set; } = 390f;
    public float MiniChatHeight { get; set; } = 510f;

    // Walk-up auto-view for public DJ sets and streams. Off by default — probing nearby players
    // can feel like the UI is reloading until a real public host is found.
    public bool AutoWatchNearby { get; set; } = false;

    // How close (yalms) another player must be before AutoWatchNearby tries to join them.
    public float AutoWatchRadiusYalms { get; set; } = 18f;

    public Vector3 ScreenPosition { get; set; }

    public float ScreenYaw { get; set; }

    //
    // Retained for compatibility with older configuration files.
    //
    public float ScreenScale { get; set; } = 1f;

    public bool DisableFixedScreenScaleRatio { get; set; }

    public float ScreenWidthScale { get; set; } = 1f;

    public float ScreenHeightScale { get; set; } = 1f;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    [NonSerialized]
    private LocalSecretVault? localSecretVault;

    [NonSerialized]
    private bool persistenceEnabled = true;

    public void Initialize(
        IDalamudPluginInterface pi,
        bool enablePersistence = true)
    {
        pluginInterface = pi;
        persistenceEnabled = enablePersistence;
        var containedLegacySecrets = CharacterSessions.Count > 0 || StreamKeys.Count > 0;
        localSecretVault = new LocalSecretVault(pi.ConfigDirectory.FullName);
        if (localSecretVault.Restore(this) && containedLegacySecrets)
        {
            // Re-save immediately so JsonIgnore removes the old plaintext copies
            // after the encrypted vault has safely received them.
            Save();
        }
    }

    public void Save()
    {
        // A configuration created as a safe fallback for a newer, unsupported
        // schema must never overwrite that newer file.
        if (!persistenceEnabled)
            return;

        localSecretVault?.Save(this);
        pluginInterface?.SavePluginConfig(this);
    }
}
