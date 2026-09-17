using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using System.Diagnostics;

namespace AlphaChannel.Plugin;

// Split into partials by concern (MainWindow.Home.cs, .Playback.cs, .Queue.cs, .Search.cs,
// .Screen.cs, .Settings.cs, .Reactions.cs) - this file has the window skeleton: the sidebar nav,
// the theme/palette, the name prompt, and watch-along/roster (shared between the Home dashboard's
// Live Now card and the dedicated Watch-along page). Smart-TV-dashboard look (dark background,
// purple neon glow border, sidebar nav, rounded cards) built with plain ImGui style pushes plus
// hand-drawn ImDrawList primitives (MainWindow.Home.cs) where ImGui has no built-in equivalent.
internal sealed partial class MainWindow : Window, IDisposable
{
    // Active palette for this frame - set at the top of Draw() from Cfg.UiTheme so every partial
    // (and ThemeScope) reads the same colors without threading a palette through each helper.
    // Mockup default is Purple (deep navy + violet accent + magenta/cyan glow).
    private static ThemeColors Colors = ThemeCatalog.Get(UiTheme.Purple);

    private static Vector4 Accent => Colors.Accent;
    private static Vector4 AccentHover => Colors.AccentHover;
    private static Vector4 AccentActive => Colors.AccentActive;
    private static Vector4 BlueGlow => Colors.BlueGlow;
    private static Vector4 MagentaGlow => Colors.MagentaGlow;
    private static Vector4 Gold => Colors.Gold;
    private static Vector4 GoldHover => Colors.GoldHover;
    private static Vector4 FrameBg => FadeForCustomBg(Colors.FrameBg, 0.30f);
    private static Vector4 FrameBgHover => FadeForCustomBg(Colors.FrameBgHover, 0.38f);
    private static Vector4 Danger => Colors.Danger;
    private static Vector4 Good => Colors.Good;
    // Custom wallpaper mode: panels are ~75% see-through so the image reads through.
    private static Vector4 WindowBg => FadeForCustomBg(Colors.WindowBg, 0.22f);
    private static Vector4 SidebarBg => FadeForCustomBg(Colors.SidebarBg, 0.28f);
    private static Vector4 CardBg => FadeForCustomBg(Colors.CardBg, 0.25f);
    private static Vector4 CardBgHover => FadeForCustomBg(Colors.CardBgHover, 0.35f);
    private static Vector4 MutedText => Colors.MutedText;
    private static readonly Vector4 BorderSubtle = new(1f, 1f, 1f, 0.085f);
    private ISharedImmediateTexture? alphaIconImage;
    private ISharedImmediateTexture? reactPreviewImage;
    private ISharedImmediateTexture? watchPartyHeaderImage;


    // Set each frame in Draw() when a custom background texture is actually showing.
    private static bool customBackgroundActive;


    private static Vector4 FadeForCustomBg(Vector4 color, float alpha) =>
        customBackgroundActive ? new Vector4(color.X, color.Y, color.Z, alpha) : color;

    // First launch experience
    private bool showingFirstLaunch;

    // Tracks the username prompt for this plugin load only. This is not
    // written to Configuration, so it resets whenever the plugin reloads.
    private bool launchNamePromptRequested;

    private float firstLaunchFadeAlpha = 1f;
    private bool firstLaunchFadingOut;
    private bool firstLaunchStarted;

    private bool firstLaunchLoadingComplete;

    private bool firstLaunchWindowDragging;

    private Vector2 firstLaunchWindowDragOffset;

    //
    // The splash begins immediately, pauses for onboarding after the welcome
    // text, then resumes its fixed loading sequence after confirmation.
    //
    private bool firstLaunchSetupSubmitted;
    private bool firstLaunchSetupFadingOut;

    private double firstLaunchSetupSubmittedAt;
    private double firstLaunchLoadingStartedAt;

    private const double FirstLaunchWelcomeDuration =
        3.5;

    private const double FirstLaunchSetupFadeDuration =
        0.75;

    private const double FirstLaunchLoadingDuration =
        16.0;

    private bool roomEndedPlaybackResetPending;
    private bool hostLeaveConfirmationRequested;

    private double firstLaunchStartedAt;

    private int firstLaunchPhase;

    private float firstLaunchTextProgress;

    private double firstLaunchLastMessageChange;

    private int firstLaunchMessageIndex;

    private static readonly string[] FirstLaunchMessages =
    [
        "Building Video Cache...",
    "Fetching video data...",
    "Polishing the screen...",
    "Gets distracted and starts doomscrolling...",
    "Watching cat videos..."
    ];

    private string firstLaunchStatus =
        "Starting AlphaChannel...";

    private static Vector4 Hex(int rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f,
        1f);

    private enum HomePage
    {
        Home,
        Player,
        PlaySnes,
        // Alpha Channel embedded-browser integration.
        Browser,
        InternetArchive,
        UnifiedSearch,
        VideoGrid,
        Screen,
        WatchAlong,
        Friends,
        Messages,
        Activity,
        PartyDirectory,
        GoLive,
        Settings,
    }

    private readonly ScreenController screenController;
    private readonly VideoPlayer video;
    private readonly AetherStreamQueue queue;
    private readonly QueueManager queueManager;
    private readonly StreamClient stream;
    private readonly ThumbnailCache thumbnails =
        new();

    //
    // User-provided still images and slideshow previews use the strict image
    // validation path. Ordinary YouTube/Twitch thumbnails remain separate.
    //
    private readonly SafeImagePreviewCache imagePreviews =
        new();

    private readonly Action requestRename;
    private readonly SignInFlow signInFlow;
    private readonly AuthClient authClient;
    private readonly FriendsClient friendsClient;
    private readonly ActivityClient activityClient;
    private readonly DmClient dmClient;
    private readonly ReportClient reportClient;
    private readonly VenuesClient venuesClient;
    private readonly LiveClient liveClient;
    private readonly RoomsClient roomsClient;
    private readonly RadioClient radioClient;
    private readonly TwitchClient twitchClient;
    private readonly Crypto.KeyVault keyVault;

    // Called whenever sign-in/link/sign-out changes what CharacterSession belongs to the currently-
    // played character - the callback (Plugin.cs) is what actually writes Cfg.CharacterSessions and
    // saves, same split as requestRename above (MainWindow owns the UI, Plugin.cs owns persistence).
    private readonly Action<CharacterSession?> onSessionChanged;
    // The approved full-size layout is the largest the normal window may become.
    // At 85% scale the dashboard remains usable without allowing it to collapse
    // all the way to the separate minimized capsule dimensions.
    //
    // The approved full layout remains the maximum size. Brand-new
    // installations initially open at approximately 80% of it.
    //
    private static readonly Vector2 WindowSize =
    new(1220f, 840f);

    //
    // Allow the experimental 1.5x interface preset to use a larger design
    // canvas. Ordinary presets still apply their own named size.
    //
    private static readonly Vector2 MaximumWindowSize =
        WindowSize * 1.5f;

    // The 4K preset keeps its existing initial size, but may be manually
    // resized up to 50% beyond the ordinary expanded-window limit.
    private static readonly Vector2 UhdMaximumWindowSize =
        MaximumWindowSize * 1.5f;

    private static readonly Vector2 FirstLaunchWindowSize =
        new(
            WindowSize.X * 0.90f,
            WindowSize.Y * 0.90f);

    private static readonly Vector2 MinimumWindowSize =
        new(950f, 730f);

    private const float FixedLayoutScale =
     1f;

    private float layoutScale =
        FixedLayoutScale;

    // Drawing helpers include a small number of static primitives shared by
    // the partial files. They read this frame-local value so their geometry
    // follows the active main-window preset as well.
    private static float activeLayoutScale =
        FixedLayoutScale;

    private bool windowSizeSavePending;

    private void RefreshLayoutScale(
        Vector2 windowSize)
    {
        layoutScale =
            !windowMinimized &&
            !showingFirstLaunch &&
            Plugin.Cfg.WindowSizePreset == UiWindowSizePreset.Uhd
                ? 1.5f
                : FixedLayoutScale;

        activeLayoutScale =
            layoutScale;
    }

    private static float Ui(
        float px) =>
        px * activeLayoutScale;

    private static Vector2 UiVec(
        float x,
        float y) =>
        new(
            x * activeLayoutScale,
            y * activeLayoutScale);

    // Explicit font adjustments remain relative to the active global font
    // scale. GlobalFontScaleScope already applies the UHD multiplier, so
    // multiplying it here as well would scale adjusted text twice.
    private static void SetUiFontScale(float relativeScale) =>
        ImGui.SetWindowFontScale(relativeScale);

    private static Vector2 NamedWindowSize(UiWindowSizePreset preset) => preset switch
    {
        UiWindowSizePreset.FullHd => WindowSize,
        UiWindowSizePreset.Qhd => WindowSize,
        UiWindowSizePreset.Uhd => WindowSize * 1.5f,
        _ => WindowSize,
    };

    private Vector2 CurrentMaximumWindowSize =>
        Plugin.Cfg.WindowSizePreset == UiWindowSizePreset.Uhd
            ? UhdMaximumWindowSize
            : MaximumWindowSize;

    private Vector2 ClampWindowSize(Vector2 size)
    {
        var maximumSize = CurrentMaximumWindowSize;

        return new Vector2(
     Math.Clamp(
                size.X,
                MinimumWindowSize.X,
                maximumSize.X),
            Math.Clamp(
                size.Y,
                MinimumWindowSize.Y,
                maximumSize.Y));
    }

    private void LoadWindowSizeFromConfig()
    {
        var cfg =
            Plugin.Cfg;

        //
        // A valid saved normal-window size must satisfy the normal window's
        // minimum constraints. This also repairs configurations written by
        // older builds that accidentally stored the minimized capsule size.
        //
        var hasValidSavedSize =
            cfg.WindowSizePreset is
                UiWindowSizePreset.Custom or
                UiWindowSizePreset.Uhd &&
            cfg.WindowWidth >=
                650f &&
            cfg.WindowHeight >=
                MinimumWindowSize.Y;

        if (hasValidSavedSize)
        {
            userWindowSize =
                ClampWindowSize(
                    new Vector2(
                        cfg.WindowWidth,
                        cfg.WindowHeight));
        }
        else if (cfg.WindowSizePreset is
                 UiWindowSizePreset.FullHd or
                 UiWindowSizePreset.Qhd or
                 UiWindowSizePreset.Uhd)
        {
            userWindowSize =
                ClampWindowSize(
                    NamedWindowSize(
                        cfg.WindowSizePreset));
        }
        else
        {
            cfg.WindowSizePreset =
                UiWindowSizePreset.Design;

            userWindowSize =
                ClampWindowSize(
                    WindowSize);

            cfg.WindowWidth =
                userWindowSize.X;

            cfg.WindowHeight =
                userWindowSize.Y;

            cfg.Save();
        }

        //
        // Apply the restored normal size on the first visible frame.
        //
        userResized =
            true;
    }

    private void ApplyWindowSizePreset(UiWindowSizePreset preset)
    {
        var cfg = Plugin.Cfg;
        cfg.WindowSizePreset = preset;
        var size = preset == UiWindowSizePreset.Custom
            ? ClampWindowSize(userWindowSize)
            : ClampWindowSize(NamedWindowSize(preset));
        userWindowSize = size;
        cfg.WindowWidth = size.X;
        cfg.WindowHeight = size.Y;
        userResized = true;
        cfg.Save();
    }
    // Mini Mode begins as a compact status bar. The player and chat actions
    // currently open their matching full-size pages; the later detachable
    // panels can take over those actions without changing this chrome.
    private static readonly Vector2 MinimizedSize = new(650f, 44f);
    private const int PositionPinFrames = 3;
    private bool windowMinimized;
    private bool userResized;
    private Vector2 userWindowSize = WindowSize;

    private bool wasResizing;
    // True after /achannel watch or context-menu Join Stream: stay minimized; screen still
    // draws via ScreenPainter + /rt sync. Requires AlphaChannel on both sides — not Lightless.
    private bool viewerMode;
    // Set when NearbyAutoWatch started the join — range leave only applies to these sessions.
    private bool proximityJoined;
    private Vector2? maximizedPosition;
    private Vector2? minimizedPosition;
    private Vector2? pendingPosition;
    private int pendingFrames;

    private HomePage currentPage = HomePage.Home;


    // Transition Animation
    private HomePage lastAnimatedPage = HomePage.Home;
    private double pageTransitionStartedAt = -1d;

    private string joinHostNameInput = string.Empty;
    private string joinPasswordInput = string.Empty;
    private string? joinError;
    private string createRoomDescription = string.Empty;
    private string createRoomLocation = string.Empty;
    private int createRoomKindIndex;
    private string createRoomPassword = string.Empty;
    private int createRoomCategoryIndex;
    private bool createRoomAdultOnly;

    //
    // Locked-room creation is confirmed through a dedicated password
    // popup rather than showing the password inside the main form.
    //
    private bool createLockedRoomPasswordPopupRequested;
    private string? createLockedRoomPasswordError;

    //
    // The locked-room password popup is shared by room creation and
    // editing an existing hosted room.
    //
    private bool createLockedRoomPasswordForRoomEdit;

    //
    // Host-only editing state for the active Watch Party details tab.
    //
    private bool partyRoomEditing;

    private static readonly string[] WatchPartyCategoryOptions =
    [
        "YouTube",
        "Movies",
        "TV",
        "Twitch",
        "Cartoons",
        "Live Stream",
        "Gaming",
        "DJ",
        "Music",
        "Images",
        "Promotional",
    ];
    private string roomBrowsePassword = string.Empty;
    private string? roomBrowseTitle;
    private RoomDirectoryDto[] roomBrowseList = [];
    private bool roomBrowseLoading;
    private string? roomBrowseError;
    private RoomDirectoryDto[] homeWatchPartyRooms = [];
    private double homeWatchPartyFetchedAt = -999;
    private bool homeWatchPartyLoading;
    private bool roomBrowseFriendsOnly;
    private bool clearQueueWhenJoined;
    private bool joinClearQueuePending;
    private RadioCredentialsDto? radioCredentials;
    private string? radioError;

    private const float DesignSidebarWidth = 210f;
    private float SidebarWidth => Ui(DesignSidebarWidth);
    private const float DesignBottomBarHeight = 96f;
    private static float BottomBarHeight => Ui(DesignBottomBarHeight);

    // Borderless Child windows ignore WindowPadding in this ImGui build unless AlwaysUseWindowPadding
    private const ImGuiWindowFlags PaddedChild =
    ImGuiWindowFlags.AlwaysUseWindowPadding |
    ImGuiWindowFlags.NoScrollbar;

    private const ImGuiWindowFlags NavPaneFlags = PaddedChild | ImGuiWindowFlags.NoScrollWithMouse;

    // Not from StreamClient - see the comment where it's set (DoJoin) for why: HostId gets
    // overwritten with the host's real UserId once StreamJoined arrives, so this is the only
    // place the friendly name a viewer actually typed survives for display.
    private string? joinedHostDisplayName;

    private bool namePromptPending;
    private bool namePromptActive;
    private string namePromptInput = string.Empty;

    private bool createQueuePopupOpen;
    private int creatingQueueIndex = -1;
    private bool editingQueueProfile;
    private string newQueueName = string.Empty;
    private string newQueueIcon = "Tv";
    private string? queueEditorError;

    private bool deleteQueuePopupOpen;
    private int deletingQueueIndex = -1;
    private string deletingQueueName = string.Empty;

    private bool clearQueuePopupOpen;
    private string clearingQueueName = string.Empty;
    private int clearingQueueVideoCount;

    private Action<string>? onNameConfirmed;

    //Scrollbar inactivity timer
    private double lastScrollInteractionTime;

    // ---------------------------------------------------------
    // Welcome splash particles
    // ---------------------------------------------------------

    private const float FirstLaunchFormWidth =
      520f;

    private const float FirstLaunchFormHeight =
        385f;

    private sealed class LoadingCardParticle
    {
        public Vector2 Position;

        public float Width;
        public float Height;

        public float Speed;

        public float Alpha;

        public float Drift;

        public double SpawnTime;

        public float ShimmerOffset;

        public bool IsLeftSide;
    }

    private static readonly
        (
            FontAwesomeIcon Icon,
            string Title
        )[]
        SplashLoadingFeatures =
        [
            (
            FontAwesomeIcon.Music,
            "Live DJ"
        ),
        (
            FontAwesomeIcon.Users,
            "Watch Parties"
        ),
        (
            FontAwesomeIcon.Gamepad,
            "Retro Games"
        ),
        (
            FontAwesomeIcon.Film,
            "Movies & Shows"
        )
        ];

    private double lastLoadingCardSpawn;

    //
    // Blank cards normally choose a random side. After three consecutive
    // cards on one side, force the next one onto the opposite side.
    //
    private bool? lastBlankCardSpawnedLeft;

    private int consecutiveBlankCardsOnSameSide;

    //
    // Particle positions use screen coordinates. Track the splash area's
    // previous position so existing particles follow the window when it moves.
    //
    private Vector2? previousLoadingCardAreaMinimum;

    private readonly List<LoadingCardParticle>
        loadingCards =
            [];

    internal bool IsNamePromptActive => namePromptActive;

    internal bool ViewerTvEnabled { get; private set; }
    internal Action? OnViewerTvSpawnRequested { get; set; }
    internal Action? OnMiniPlayerRequested { get; set; }
    internal Action? OnMiniChatRequested { get; set; }
    internal Func<bool>? IsMiniPlayerOpen { get; set; }

    private bool viewerTvSpawnPromptRequested;
    private bool viewerTvSpawnPromptShownForCurrentRoom;

    internal void RequestViewerTvSpawnPrompt()
    {
        if (stream.Mode != StreamMode.Viewing ||
            ViewerTvEnabled ||
            viewerTvSpawnPromptShownForCurrentRoom)
        {
            return;
        }

        viewerTvSpawnPromptShownForCurrentRoom =
            true;

        viewerTvSpawnPromptRequested =
            true;
    }

    internal void DespawnViewerTv(bool stopPlayback = true)
    {
        if (!ViewerTvEnabled)
        {
            return;
        }

        //
        // Match the Watch Party Spawn/Despawn TV button. Keep
        // viewerTvSpawnPromptShownForCurrentRoom true so incoming host states
        // do not immediately ask to spawn the TV again.
        //
        ViewerTvEnabled =
            false;

        viewerTvSpawnPromptRequested =
            false;

        if (stopPlayback)
        {
            video.Stop();
        }
        else
        {
            screenController.Engine.DespawnScreen();
        }
    }

    private void ResetViewerTvSpawnPrompt()
    {
        viewerTvSpawnPromptRequested =
            false;

        viewerTvSpawnPromptShownForCurrentRoom =
            false;

        ViewerTvEnabled =
            false;
    }

    // Updated every tick from Plugin.cs (cheap dictionary lookup there) - shown here instead of the
    // raw UserId so players never need to read each other an opaque GUID to join a stream.
    internal string? CurrentDisplayName { get; set; }

    // Also updated every tick from Plugin.cs, same reasoning as CurrentDisplayName - the signed-in
    // account (if any) for whichever character is currently being played, and the live character
    // name/world to sign in with if there isn't one yet.
    internal CharacterSession? CurrentSession { get; set; }
    internal bool SessionValidationInProgress { get; set; }
    internal string? CurrentCharacterName { get; set; }
    internal string? CurrentWorldName { get; set; }
    internal bool CurrentIsLalafell { get; set; }

    internal MainWindow(ScreenController screenController, VideoPlayer video, AetherStreamQueue queue,
        StreamClient stream, Action requestRename, AuthClient authClient, SignInFlow signInFlow,
        FriendsClient friendsClient, ActivityClient activityClient, DmClient dmClient, ReportClient reportClient,
        VenuesClient venuesClient, LiveClient liveClient,
        RoomsClient roomsClient, RadioClient radioClient,
        TwitchClient twitchClient, Crypto.KeyVault keyVault,
        Action<CharacterSession?> onSessionChanged)
        : base("AlphaChannel###AlphaChannelMain")
    {
        this.screenController = screenController;
        this.video = video;
        this.queue = queue;

        queueManager =
            new QueueManager(
                Plugin.Cfg,
                queue);

        this.stream = stream;
        this.requestRename = requestRename;
        this.authClient = authClient;
        this.signInFlow = signInFlow;
        this.friendsClient = friendsClient;
        this.activityClient = activityClient;
        this.dmClient = dmClient;
        this.reportClient = reportClient;
        this.venuesClient = venuesClient;
        this.liveClient = liveClient;
        this.roomsClient = roomsClient;
        this.radioClient = radioClient;
        this.twitchClient = twitchClient;
        this.keyVault = keyVault;
        this.onSessionChanged = onSessionChanged;

        stream.OnFriendRequestReceived += _ => friendsDirty = true;
        stream.OnFriendAccepted += _ => friendsDirty = true;
        stream.OnFriendRemoved += _ => friendsDirty = true;
        stream.OnPresenceUpdate += ApplyPresenceUpdate;
        stream.OnOnlineCount += count => usersOnlineCount = count;
        stream.OnActivityNew += _ => { activityDirty = true; activityUnreadDirty = true; };
        stream.OnDmMessage += ApplyIncomingDm;

        // Borderless dashboard window with normal ImGui resize handles.
        // The full approved layout is the maximum; PreDraw temporarily swaps
        // constraints only while the custom minimized capsule is active.
        //
        // Alpha Channel persists its normal and minimized layouts itself.
        // Prevent ImGui/Dalamud from saving the temporary 276x40 minimized
        // capsule as the main window's size when the plugin unloads.
        //
        Flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings;

        SizeCondition = ImGuiCond.FirstUseEver;
        Size = WindowSize;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumWindowSize,
            MaximumSize = CurrentMaximumWindowSize,
        };

        LoadWindowSizeFromConfig();
        InitializeLocalLibraryRecovery();

        //
        // The welcome-animation size is applied temporarily by PreDraw() while
        // showingFirstLaunch is true. It must not replace userWindowSize or be
        // written into the user's saved configuration.
        //

        stream.OnJoined += () =>
        {
            joinError = null;
            if (clearQueueWhenJoined)
            {
                joinClearQueuePending = true;
                clearQueueWhenJoined = false;
            }
        };
        stream.OnDeclined += reason =>
        {
            joinError = string.IsNullOrEmpty(reason) ? "Could not find that host." : reason;
            if (viewerMode)
            {
                proximityJoined = false;
                joinedHostDisplayName = null;
                viewerMode = false;
                SetMinimized(false);
            }
        };
        stream.OnEnded += () =>
        {
            joinedHostDisplayName =
                null;

            viewerMode =
                false;

            proximityJoined =
                false;

            //
            // StreamClient events arrive from the socket receive thread.
            // Defer video/screen mutation until the framework thread.
            //
            roomEndedPlaybackResetPending =
                true;
        };

        maximizedPosition = Plugin.Cfg.MaximizedPosition;
        minimizedPosition = Plugin.Cfg.MinimizedPosition;

        //
        // Only run the welcome presentation until the user has completed it
        // for the first time.
        //
        showingFirstLaunch =
     Plugin.ForceFirstLaunchExperienceForTesting ||
     !Plugin.Cfg.HasCompletedFirstLaunch;
    }

    // /achannel and Dalamud's OpenMainUi both land here so a second activation always closes,
    // including when the window is sitting in its minimized capsule.
    internal void OpenUi()
    {
        SetMinimized(false);
        RequestPosition(maximizedPosition);
        IsOpen = true;
    }

    // Full-window join (Home / Party "Join" field). Prefer OpenViewerAndJoin for quick watch.
    internal void OpenPlayerAndJoin(string hostDisplayName)
    {
        proximityJoined = false;
        viewerMode = false;
        currentPage = HomePage.Player;
        playerSourceTab = 0;
        OpenUi();
        DoJoin(hostDisplayName);
    }

    // Viewer-only: AlphaChannel required. Capsule UI + ScreenPainter; sync is still /rt URL/position
    // (ApplyRemoteState) — no Penumbra texture pipe, so Lightless alone cannot show the screen.
    // fromProximity: NearbyAutoWatch owns leave-on-range; manual /watch keeps the session until Leave.
    internal void OpenViewerAndJoin(
        string hostLookup, bool fromProximity = false, string? visibleHostName = null)
    {
        proximityJoined = fromProximity;
        viewerMode = true;
        currentPage = HomePage.Player;
        playerSourceTab = 0;
        SetMinimized(true);
        RequestPosition(minimizedPosition);
        IsOpen = true;
        DoJoin(hostLookup, visibleHostName: visibleHostName);
    }

    // Typing animation for loading screen
    private static string GetTypewriterText(
        string text,
        double elapsed,
        double startDelay = 0.5,
        double charactersPerSecond = 12)
    {
        if (elapsed < startDelay)
        {
            return string.Empty;
        }

        var visibleCharacters =
            (int)((elapsed - startDelay) * charactersPerSecond);

        return text[..Math.Clamp(
            visibleCharacters,
            0,
            text.Length)];
    }

    // Silent proximity probe — join without opening chrome until ShowProximityViewer (URL confirmed).
    // Does not clear the local queue (DoJoin does); wiping playback was resetting hosts' screens.
    internal void BeginProximityJoin(string hostDisplayName)
    {
        if (hostDisplayName.Length == 0)
        {
            return;
        }

        proximityJoined = true;
        viewerMode = true;
        clearQueueWhenJoined = false;
        // Do not touch playerSourceTab / queue — probes must not yank the YouTube search box.
        joinedHostDisplayName = hostDisplayName.Trim();
        _ = stream.JoinAsync(hostDisplayName.Trim());
    }

    // True when this client is driving its own screen/queue (hosting or solo play) — auto-watch
    // must not join/clear over the top of that.
    internal bool HasLocalPlayback =>
        stream.Mode == StreamMode.Hosting
        || queue.Current is not null
        || screenController.Engine.IsActive;

    internal void ShowProximityViewer()
    {
        if (!proximityJoined)
        {
            return;
        }

        SetMinimized(true);
        RequestPosition(minimizedPosition);
        IsOpen = true;
    }

    private void RequestLeaveWatchParty()
    {
        //
        // Viewers can leave immediately. Only hosts need confirmation
        // because their action closes the room for everyone.
        //
        if (stream.Mode != StreamMode.Hosting)
        {
            LeaveStream();
            partyChatItems.Clear();
            return;
        }

        hostLeaveConfirmationRequested =
            true;
    }

    internal void LeaveStream()
    {
        viewerMode =
            false;

        proximityJoined =
            false;

        joinedHostDisplayName =
            null;

        StopGameWatchPartyBroadcast();
        StopLocalVideoWatchPartyBroadcast();

        ResetNormalWatchPartyPlayback();
        ResetViewerTvSpawnPrompt();

        _ = stream.LeaveAsync();

        gameplayStreamOfferDismissed =
            false;

        browserStreamOfferDismissed =
            false;
    }

    internal bool ApplyPendingJoinQueueClear()
    {
        if (!joinClearQueuePending)
        {
            return false;
        }

        joinClearQueuePending =
            false;

        ResetPlaybackForViewerJoin();
        return true;
    }

    internal void ApplyPendingRoomEndedReset()
    {
        if (!roomEndedPlaybackResetPending)
        {
            return;
        }

        roomEndedPlaybackResetPending =
            false;

        ResetNormalWatchPartyPlayback();
        ResetViewerTvSpawnPrompt();

        joinedHostDisplayName =
            null;

        viewerMode =
            false;

        proximityJoined =
            false;

        gameplayStreamOfferDismissed =
            false;

        browserStreamOfferDismissed =
            false;
    }

    private void ResetNormalWatchPartyPlayback()
    {
        //
        // A viewer must explicitly choose whether to spawn a TV in the
        // newly joined room.
        //
        ViewerTvEnabled =
            false;

        var engine =
            screenController.Engine;

        //
        // Do not stop or despawn emulator or embedded-browser content.
        //
        if (engine.IsPlayingGame || engine.IsPlayingBrowser)
        {
            return;
        }

        video.Stop();
        queue.Clear();
    }

    private void ResetPlaybackForViewerJoin()
    {
        //
        // Do this only after the relay confirms the join. A declined join
        // must leave the user's existing solo playback untouched.
        //
        ResetViewerTvSpawnPrompt();

        StopGameWatchPartyBroadcast();
        StopBrowserWatchPartyBroadcast();
        StopLocalVideoWatchPartyBroadcast();

        djBroadcastingToWatchParty =
            false;

        djAutoMuteNoticeVisible =
            false;

        djAutoMutedStreamUrl =
            null;

        //
        // Queue.Clear ultimately calls VideoEngine.StopVideo(), which also
        // tears down exclusive playback such as games, the browser, local
        // video, and images before the viewer adopts the host's state.
        //
        queue.Clear();
    }

    private void StopPlayback()
    {
        video.Stop();
        queue.Clear();
    }

    internal string? JoinedHostDisplayName => joinedHostDisplayName;
    internal bool ProximityJoined => proximityJoined;

    internal void ClearProximityJoin() => proximityJoined = false;

    internal void CloseUi()
    {
        PersistPositions();

        //
        // Preserve windowMinimized while closed. OpenUi() calls
        // SetMinimized(false), which then restores userWindowSize on the
        // first expanded frame.
        //
        IsOpen =
            false;
    }

    // Writes remembered placements when they changed — called on close and plugin unload.
    // Writes remembered placements and any pending normal-window size.
    // Called on close and plugin unload, including while minimized.
    internal void PersistPositions()
    {
        var positionsChanged =
            Plugin.Cfg.MaximizedPosition !=
                maximizedPosition ||
            Plugin.Cfg.MinimizedPosition !=
                minimizedPosition;

        if (!positionsChanged &&
            !windowSizeSavePending)
        {
            return;
        }

        Plugin.Cfg.MaximizedPosition =
            maximizedPosition;

        Plugin.Cfg.MinimizedPosition =
            minimizedPosition;

        Plugin.Cfg.WindowSizePreset =
            UiWindowSizePreset.Custom;

        Plugin.Cfg.WindowWidth =
            userWindowSize.X;

        Plugin.Cfg.WindowHeight =
            userWindowSize.Y;

        Plugin.Cfg.Save();

        windowSizeSavePending =
            false;
    }

    public override void OnClose() => PersistPositions();

    private void SetMinimized(
    bool minimized,
    Vector2? normalWindowSize = null)
    {
        if (windowMinimized ==
            minimized)
        {
            return;
        }

        if (minimized &&
            normalWindowSize is { } capturedSize)
        {
            //
            // Capture the full window size before switching the same ImGui
            // window to the small minimized capsule.
            //
            userWindowSize =
                ClampWindowSize(
                    capturedSize);

            Plugin.Cfg.WindowSizePreset =
                UiWindowSizePreset.Custom;

            Plugin.Cfg.WindowWidth =
                userWindowSize.X;

            Plugin.Cfg.WindowHeight =
                userWindowSize.Y;

            //
            // Save immediately. Once windowMinimized becomes true, the normal
            // Draw() size-saving branch no longer runs. Without this save,
            // unloading while minimized can restore an older, smaller size.
            //
            Plugin.Cfg.Save();

            windowSizeSavePending =
                false;
        }

        windowMinimized =
            minimized;

        if (!minimized)
        {
            //
            // Force the remembered normal size for one frame when restoring.
            //
            userResized =
                true;
        }

        RequestPosition(
            minimized
                ? minimizedPosition
                : maximizedPosition);
    }

    private void RequestPosition(Vector2? target)
    {
        if (target is not { } position)
        {
            return;
        }

        pendingPosition = position;
        pendingFrames = PositionPinFrames;
    }

    private void CaptureCurrentPosition()
    {
        var pos = ImGui.GetWindowPos();
        if (windowMinimized)
        {
            minimizedPosition = pos;
        }
        else
        {
            maximizedPosition = pos;
        }
    }

    // Called from Plugin.cs once per character that hasn't picked a name yet, or after an admin
    // reset - suggested is pre-filled (their real character name) so confirming needs no typing.
    internal void RequestNamePrompt(
      string suggested,
      Action<string> onConfirmed)
    {
        if (namePromptActive)
        {
            return;
        }

        namePromptInput =
            suggested;

        onNameConfirmed =
            onConfirmed;

        namePromptActive =
            true;

        namePromptPending =
            true;

        //
        // First launch must wait for this prompt to be submitted before
        // beginning its splash animation.
        //
        launchNamePromptRequested =
            true;

        IsOpen =
            true;
    }

    // First launch process
    private void BeginFirstLaunch()
    {
        showingFirstLaunch =
            true;

        firstLaunchStartedAt =
            ImGui.GetTime();

        firstLaunchPhase =
            0;

        firstLaunchTextProgress =
            0f;

        firstLaunchLastMessageChange =
            ImGui.GetTime();

        firstLaunchMessageIndex =
            0;

        firstLaunchLoadingComplete =
            false;

        firstLaunchSetupSubmitted =
            false;

        firstLaunchSetupFadingOut =
            false;

        firstLaunchSetupSubmittedAt =
            0d;

        firstLaunchLoadingStartedAt =
            0d;

        firstLaunchFadingOut =
            false;

        firstLaunchFadeAlpha =
            1f;

        previousLoadingCardAreaMinimum =
            null;

        loadingCards.Clear();

        lastBlankCardSpawnedLeft =
            null;

        consecutiveBlankCardsOnSameSide =
            0;

        lastLoadingCardSpawn =
            ImGui.GetTime() -
            1.2d;
    }

    private async Task CompleteFirstLaunchAsync()
    {
        try
        {
            await Task.Delay(
                    TimeSpan.FromSeconds(
                        FirstLaunchLoadingDuration))
                .ConfigureAwait(false);
        }
        finally
        {
            firstLaunchLoadingComplete =
                true;

            Plugin.Cfg.HasCompletedFirstLaunch =
                true;

            Plugin.Cfg.Save();

            firstLaunchFadingOut =
                true;
        }
    }

    // Loading screen text transitions
    private static float FadeBetween(
    double elapsed,
    double start,
    double duration)
    {
        var progress =
            (float)Math.Clamp(
                (elapsed - start) / duration,
                0,
                1);

        // Smooth easing
        return progress * progress * (3f - 2f * progress);
    }

    private void UpdateLoadingCards(
    Vector2 areaMin,
    Vector2 areaMax)
    {
        var now =
            ImGui.GetTime();

        //
        // Keep existing particles attached to the splash when the window moves.
        //
        if (previousLoadingCardAreaMinimum is
            { } previousAreaMin)
        {
            var windowMovement =
                areaMin -
                previousAreaMin;

            if (MathF.Abs(
                    windowMovement.X) >
                0.01f ||
                MathF.Abs(
                    windowMovement.Y) >
                0.01f)
            {
                foreach (var existingCard in
                         loadingCards)
                {
                    existingCard.Position +=
                        windowMovement;
                }
            }
        }

        previousLoadingCardAreaMinimum =
            areaMin;

        if (now -
            lastLoadingCardSpawn >
            1.1d)
        {
            var sideLeft =
                Random.Shared.Next(
                    0,
                    100) <
                50;

            //
            // After three consecutive cards on one side, force the next card
            // onto the opposite side before returning to random selection.
            //
            if (lastBlankCardSpawnedLeft is
                { } previousSide &&
                consecutiveBlankCardsOnSameSide >=
                3)
            {
                sideLeft =
                    !previousSide;
            }

            if (lastBlankCardSpawnedLeft ==
                sideLeft)
            {
                consecutiveBlankCardsOnSameSide++;
            }
            else
            {
                lastBlankCardSpawnedLeft =
                    sideLeft;

                consecutiveBlankCardsOnSameSide =
                    1;
            }

            var width =
                Random.Shared.Next(
                    80,
                    150);

            var height =
                Random.Shared.Next(
                    50,
                    90);

            var horizontalInset =
                Random.Shared.Next(
                    20,
                    180);

            var spawnX =
                sideLeft
                    ? areaMin.X +
                      horizontalInset
                    : areaMax.X -
                      width -
                      horizontalInset;

            loadingCards.Add(
                new LoadingCardParticle
                {
                    Position =
                        new Vector2(
                            spawnX,
                            areaMax.Y +
                            height +
                            Ui(20f)),

                    Width =
                        width,

                    Height =
                        height,

                    Speed =
                        Random.Shared.Next(
                            35,
                            80),

                    Drift =
                        Random.Shared.NextSingle() *
                        12f -
                        6f,

                    Alpha =
                        0f,

                    SpawnTime =
                        now,

                    ShimmerOffset =
                        Random.Shared.NextSingle(),

                    IsLeftSide =
                        sideLeft
                });

            lastLoadingCardSpawn =
                now;
        }

        var deltaTime =
            ImGui.GetIO().DeltaTime;

        foreach (var card in
                 loadingCards)
        {
            card.Position.Y -=
                card.Speed *
                deltaTime;

            card.Position.X +=
                MathF.Sin(
                    (float)(
                        now +
                        card.SpawnTime) *
                    0.65f) *
                8f *
                deltaTime;

            var age =
                now -
                card.SpawnTime;

            var fadeIn =
                (float)Math.Clamp(
                    age /
                    1.5d,
                    0d,
                    1d);

            var topFade =
                Math.Clamp(
                    (
                        card.Position.Y -
                        areaMin.Y
                    ) /
                    110f,
                    0f,
                    1f);

            card.Alpha =
                fadeIn *
                topFade;
        }

        loadingCards.RemoveAll(
            card =>
                card.Position.Y +
                card.Height <
                areaMin.Y -
                30f);
    }

    private float GetFirstLaunchSetupAlpha(
    double elapsed)
    {
        var fadeIn =
            (float)Math.Clamp(
                (
                    elapsed -
                    FirstLaunchWelcomeDuration
                ) /
                FirstLaunchSetupFadeDuration,
                0d,
                1d);

        if (!firstLaunchSetupFadingOut)
        {
            return fadeIn;
        }

        var fadeOut =
            (float)Math.Clamp(
                (
                    ImGui.GetTime() -
                    firstLaunchSetupSubmittedAt
                ) /
                FirstLaunchSetupFadeDuration,
                0d,
                1d);

        return fadeIn *
               (1f - fadeOut);
    }

    private float GetFirstLaunchLoadingAlpha()
    {
        if (!firstLaunchSetupSubmitted)
        {
            return 0f;
        }

        return (float)Math.Clamp(
            (
                ImGui.GetTime() -
                firstLaunchSetupSubmittedAt
            ) /
            FirstLaunchSetupFadeDuration,
            0d,
            1d);
    }

    private void SubmitFirstLaunchSetup()
    {
        if (firstLaunchSetupSubmitted)
        {
            return;
        }

        var selectedTopicCount =
            GetSubscribedTopicCount();

        if (string.IsNullOrWhiteSpace(
                namePromptInput) ||
            selectedTopicCount is < 3 or > 15)
        {
            return;
        }

        Plugin.Cfg.Save();

        onNameConfirmed?.Invoke(
            namePromptInput.Trim());

        onNameConfirmed =
            null;

        firstLaunchSetupSubmitted =
            true;

        firstLaunchSetupFadingOut =
            true;

        firstLaunchSetupSubmittedAt =
            ImGui.GetTime();

        firstLaunchLoadingStartedAt =
            ImGui.GetTime();

        firstLaunchLastMessageChange =
            ImGui.GetTime();

        firstLaunchMessageIndex =
            0;

        //
        // Start the shared cache preparation as soon as the form is submitted.
        // Normal freshness and topic-signature rules still determine whether a
        // YouTube request is actually required.
        //
        browseVideoRequested =
            true;

        topicVideoStartupRequested =
            true;

        homeYouTubeResults =
            null;

        homeYouTubeSelectedTopics.Clear();

        isLoadingHomeYouTube =
            true;

        _ =
            PrepareTopicVideosForLaunchAsync(
                forceRefresh: false);

        _ =
            CompleteFirstLaunchAsync();
    }

    private void DrawFirstLaunchSetupForm(
        Vector2 center,
        double elapsed)
    {
        if (!namePromptActive ||
            elapsed <
            FirstLaunchWelcomeDuration)
        {
            return;
        }

        var alpha =
            GetFirstLaunchSetupAlpha(
                elapsed);

        if (alpha <= 0f)
        {
            if (firstLaunchSetupSubmitted)
            {
                namePromptActive =
                    false;

                namePromptPending =
                    false;
            }

            return;
        }

        const float formWidth =
     FirstLaunchFormWidth;

        const float padding =
            18f;

        var formLeft =
            center.X -
            formWidth * 0.5f;

        var formTop =
            center.Y -
            22f;

        using var formAlpha =
            ImRaii.PushStyle(
                ImGuiStyleVar.Alpha,
                alpha *
                firstLaunchFadeAlpha);

        ImGui.SetCursorScreenPos(
            new Vector2(
                formLeft,
                formTop));

        ImGui.TextColored(
            Vector4.One,
            "Choose your username");

        ImGui.SetCursorScreenPos(
            new Vector2(
                formLeft,
                formTop + Ui(25f)));

        ImGui.TextColored(
            MutedText,
            "Choose the username other Alpha Channel users will see.");

        ImGui.SameLine(
            0f,
            7f);

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            ImGui.TextDisabled(
                FontAwesomeIcon.InfoCircle.ToIconString());
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "This is the name used to add you to friends lists and join your watch party.");
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                formLeft,
                formTop + Ui(52f)));

        ImGui.SetNextItemWidth(
            formWidth);

        using (ImRaii.PushColor(
            ImGuiCol.FrameBg,
            new Vector4(
                0.025f,
                0.03f,
                0.055f,
                0.96f)))
        using (ImRaii.PushColor(
            ImGuiCol.Border,
            new Vector4(
                Accent.X,
                Accent.Y,
                Accent.Z,
                0.78f)))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameBorderSize,
            1f))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            5f))
        {
            ImGui.InputText(
                "##firstLaunchUsernameInput",
                ref namePromptInput,
                32);
        }

        var dividerY =
            formTop +
            94f;

        ImGui.GetWindowDrawList()
            .AddLine(
                new Vector2(
                    formLeft,
                    dividerY),
                new Vector2(
                    formLeft +
                    formWidth,
                    dividerY),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.32f *
                        alpha)),
                1f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                formLeft,
                formTop + Ui(108f)));

        ImGui.TextColored(
            Vector4.One,
            "Subscribe to your favourite topics");

        ImGui.SetCursorScreenPos(
            new Vector2(
                formLeft,
                formTop + Ui(133f)));

        ImGui.TextColored(
            MutedText,
            "Choose at least 3 topics to receive relevant video recommendations.");

        ImGui.SameLine(
            0f,
            7f);

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            ImGui.TextDisabled(
                FontAwesomeIcon.InfoCircle.ToIconString());
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "You can change these later in Settings.");
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                formLeft,
                formTop + Ui(160f)));

        ImGui.PushID(
            "embeddedWelcomeTopicSelection");

        DrawTrendingTopicTags(
            columnHeight: 145f,
            availableWidthOverride:
                formWidth);

        ImGui.PopID();

       

        var selectedTopicCount =
       GetSubscribedTopicCount();

        var topicCountValid =
            selectedTopicCount is >= 3 and <= 15;

        var informationRowY =
            formTop +
            313f;

        //
        // Selected-topic count on the left.
        //
        ImGui.SetCursorScreenPos(
            new Vector2(
                formLeft,
                informationRowY));

        ImGui.TextColored(
            topicCountValid
                ? Accent
                : new Vector4(
                    1f,
                    0.55f,
                    0.35f,
                    1f),
            $"{selectedTopicCount} topics selected (minimum 3)");

        //
        // Selection-limit warning on the right side of the same row.
        //
        if (topicSelectionLimitWarning)
        {
            const string limitWarning =
                "You can select up to 15 topics.";

            var limitWarningSize =
                ImGui.CalcTextSize(
                    limitWarning);

            ImGui.SetCursorScreenPos(
                new Vector2(
                    formLeft +
                    formWidth -
                    limitWarningSize.X,
                    informationRowY));

            ImGui.TextColored(
                new Vector4(
                    1f,
                    0.55f,
                    0.35f,
                    1f),
                limitWarning);
        }

        var valid =
            !string.IsNullOrWhiteSpace(
                namePromptInput) &&
            topicCountValid &&
            !firstLaunchSetupSubmitted;

        //
        // Wide, centred Continue button.
        //
        var continueButtonWidth = Ui(240f);

        var continueButtonHeight = Ui(36f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                formLeft +
                (formWidth -
                 continueButtonWidth) *
                0.5f,
                formTop +
                Ui(341f)));

        if (!valid)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button(
                "Continue",
                new Vector2(
                    continueButtonWidth,
                    continueButtonHeight)))
        {
            SubmitFirstLaunchSetup();
        }

        if (!valid)
        {
            ImGui.EndDisabled();
        }
    }

    private void DrawFirstLaunchDragBlocker(
    string id,
    Vector2 minimum,
    Vector2 maximum,
    Vector2 windowPosition)
    {
        var size =
            maximum -
            minimum;

        if (size.X <= 0f ||
            size.Y <= 0f)
        {
            return;
        }

        ImGui.SetCursorScreenPos(
            minimum);

        ImGui.InvisibleButton(
            id,
            size);

        var mouse =
            ImGui.GetMousePos();

        if (ImGui.IsItemClicked(
                ImGuiMouseButton.Left))
        {
            firstLaunchWindowDragging =
                true;

            firstLaunchWindowDragOffset =
                mouse -
                windowPosition;
        }

        if (firstLaunchWindowDragging &&
            ImGui.IsItemActive() &&
            ImGui.IsMouseDown(
                ImGuiMouseButton.Left))
        {
            var newPosition =
                mouse -
                firstLaunchWindowDragOffset;

            //
            // Move the underlying Alpha Channel window.
            //
            ImGui.SetWindowPos(
                "AlphaChannel###AlphaChannelMain",
                newPosition,
                ImGuiCond.Always);

            //
            // Move the splash overlay with it.
            //
            ImGui.SetWindowPos(
                newPosition,
                ImGuiCond.Always);

            maximizedPosition =
                newPosition;
        }

        if (!ImGui.IsMouseDown(
                ImGuiMouseButton.Left))
        {
            firstLaunchWindowDragging =
                false;
        }
    }

    private void DrawFirstLaunchInteractionShield(
        Vector2 windowPosition,
        Vector2 windowSize,
        Vector2 center,
        double elapsed)
    {
        var windowMinimum =
            windowPosition;

        var windowMaximum =
            windowPosition +
            windowSize;

        var setupFormVisible =
            namePromptActive &&
            elapsed >=
            FirstLaunchWelcomeDuration;

        if (!setupFormVisible)
        {
            //
            // During the loading portion, the entire splash is one draggable
            // interaction shield.
            //
            DrawFirstLaunchDragBlocker(
                "##firstLaunchFullShield",
                windowMinimum,
                windowMaximum,
                windowPosition);

            return;
        }

        const float formWidth =
    FirstLaunchFormWidth;

        const float formHeight =
            FirstLaunchFormHeight;

        var formMinimum =
            new Vector2(
                center.X -
                formWidth * 0.5f,
                center.Y -
                Ui(22f));

        var formMaximum =
            formMinimum +
            new Vector2(
                formWidth,
                formHeight);

        //
        // Keep the exclusion rectangle inside the splash window.
        //
        formMinimum =
            Vector2.Max(
                formMinimum,
                windowMinimum);

        formMaximum =
            Vector2.Min(
                formMaximum,
                windowMaximum);

        //
        // Top shield.
        //
        DrawFirstLaunchDragBlocker(
            "##firstLaunchTopShield",
            windowMinimum,
            new Vector2(
                windowMaximum.X,
                formMinimum.Y),
            windowPosition);

        //
        // Bottom shield.
        //
        DrawFirstLaunchDragBlocker(
            "##firstLaunchBottomShield",
            new Vector2(
                windowMinimum.X,
                formMaximum.Y),
            windowMaximum,
            windowPosition);

        //
        // Left shield alongside the form.
        //
        DrawFirstLaunchDragBlocker(
            "##firstLaunchLeftShield",
            new Vector2(
                windowMinimum.X,
                formMinimum.Y),
            new Vector2(
                formMinimum.X,
                formMaximum.Y),
            windowPosition);

        //
        // Right shield alongside the form.
        //
        DrawFirstLaunchDragBlocker(
            "##firstLaunchRightShield",
            new Vector2(
                formMaximum.X,
                formMinimum.Y),
            new Vector2(
                windowMaximum.X,
                formMaximum.Y),
            windowPosition);
    }

    private void DrawFirstLaunchSpinnerFeatures(
     ImDrawListPtr drawList,
     Vector2 spinnerCenter,
     float loadingAlpha)
    {
        if (loadingAlpha <= 0f)
        {
            return;
        }

        var featureAlpha =
            Math.Clamp(
                loadingAlpha *
                firstLaunchFadeAlpha,
                0f,
                1f);

        var positions =
            new[]
            {
            spinnerCenter +
            UiVec(-220f, -52f),

            spinnerCenter +
            UiVec(220f, -52f),

            spinnerCenter +
            UiVec(-220f, 76f),

            spinnerCenter +
            UiVec(220f, 76f)
            };

        for (var featureIndex = 0;
             featureIndex <
             SplashLoadingFeatures.Length;
             featureIndex++)
        {
            var feature =
                SplashLoadingFeatures[
                    featureIndex];

            var featureCenter =
                positions[
                    featureIndex];

            var iconGlyph =
                feature.Icon.ToIconString();

            Vector2 iconSize;

            SetUiFontScale(
                1.65f);

            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                iconSize =
                    ImGui.CalcTextSize(
                        iconGlyph);

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    new Vector2(
                        featureCenter.X -
                        iconSize.X *
                        0.5f,
                        featureCenter.Y),
                    ImGui.GetColorU32(
                        new Vector4(
                            AccentHover.X,
                            AccentHover.Y,
                            AccentHover.Z,
                            featureAlpha)),
                    iconGlyph);
            }

            SetUiFontScale(
                1f);

            var titleSize =
                ImGui.CalcTextSize(
                    feature.Title);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    featureCenter.X -
                    titleSize.X *
                    0.5f,
                    featureCenter.Y +
                    Ui(34f)),
                ImGui.GetColorU32(
                    new Vector4(
                        1f,
                        1f,
                        1f,
                        featureAlpha)),
                feature.Title);
        }
    }

    // First time launch welcome splash screen
    private void DrawFirstLaunchOverlay()
    {
        if (firstLaunchFadingOut)
        {
            firstLaunchFadeAlpha -=
                ImGui.GetIO().DeltaTime /
                1.5f;

            if (firstLaunchFadeAlpha <= 0f)
            {
                firstLaunchFadeAlpha =
                    0f;

                showingFirstLaunch =
                    false;

                //
                // Keep the window at its current splash dimensions. Setting
                // userResized here would immediately replace them with the saved
                // normal size and make the window visibly shrink as the username
                // prompt appears.
                //
            }
        }

        var windowPos =
            ImGui.GetWindowPos();

        var windowSize =
            ImGui.GetWindowSize();

        var min =
            windowPos;

        var max =
            new Vector2(
                windowPos.X + windowSize.X,
                windowPos.Y + windowSize.Y);

        // -------------------------------------------------
        // Input blocker
        // -------------------------------------------------
        // A real top-level ImGui window is required here.
        // An InvisibleButton inside the main window does not reliably block
        // clicks against the sidebar/content child windows underneath.

        ImGui.SetNextWindowPos(
            windowPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            windowSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(0f);

        const ImGuiWindowFlags blockerFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking;

        if (!ImGui.Begin(
         "##firstLaunchInputBlockerWindow",
         blockerFlags))
        {
            ImGui.End();
            return;
        }

       

        var drawList =
            ImGui.GetWindowDrawList();

        // Full window themed overlay


        // Full window themed overlay
        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    0.03f,
                    0.02f,
                    0.08f,
                    0.96f *
                    firstLaunchFadeAlpha)));

        var cardAreaMin = new Vector2(
            windowPos.X + Ui(20),
            windowPos.Y + Ui(40));

        var cardAreaMax = new Vector2(
            windowPos.X + windowSize.X - Ui(20),
            windowPos.Y + windowSize.Y - Ui(40));

        UpdateLoadingCards(cardAreaMin, cardAreaMax);

        drawList.PushClipRect(
            cardAreaMin,
            cardAreaMax,
            true);

        foreach (var card in
          loadingCards)
        {
            var cardMin =
                card.Position;

            var cardMax =
                card.Position +
                new Vector2(
                    card.Width,
                    card.Height);

            var effectiveAlpha =
                Math.Clamp(
                    card.Alpha *
                    firstLaunchFadeAlpha,
                    0f,
                    1f);

            //
            // Rising fade trail.
            //
            for (var trailIndex = 1;
                 trailIndex <= 3;
                 trailIndex++)
            {
                var trailOffset =
                    trailIndex *
                    24f;

                drawList.AddRectFilled(
                    cardMin +
                    new Vector2(
                        0f,
                        trailOffset),
                    cardMax +
                    new Vector2(
                        0f,
                        trailOffset +
                        Ui(20f)),
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            effectiveAlpha *
                            (
                                0.04f /
                                trailIndex
                            ))),
                    8f);
            }

            //
            // Translucent blank placeholder.
            //
            drawList.AddRectFilled(
                cardMin,
                cardMax,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        effectiveAlpha *
                        0.10f)),
                8f);

            drawList.AddRectFilled(
                cardMin +
                UiVec(10f, 10f),
                new Vector2(
                    cardMax.X -
                    Ui(10f),
                    cardMin.Y +
                    card.Height *
                    0.55f),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X *
                        0.65f,
                        Accent.Y *
                        0.65f,
                        Accent.Z *
                        0.65f,
                        effectiveAlpha *
                        0.20f)),
                6f);

            var shimmer =
                (float)(
                    (
                        Math.Sin(
                            ImGui.GetTime() *
                            3d +
                            card.ShimmerOffset *
                            10f) *
                        0.5d
                    ) +
                    0.5d);

            var shimmerX =
                cardMin.X +
                card.Width *
                shimmer;

            drawList.AddRectFilled(
                new Vector2(
                    shimmerX -
                    Ui(18f),
                    cardMin.Y),
                new Vector2(
                    shimmerX +
                    Ui(18f),
                    cardMax.Y),
                ImGui.GetColorU32(
                    new Vector4(
                        1f,
                        1f,
                        1f,
                        effectiveAlpha *
                        0.06f)),
                8f);

            drawList.AddRectFilled(
                cardMin +
                new Vector2(
                    Ui(10f),
                    card.Height -
                    Ui(28f)),
                new Vector2(
                    cardMin.X +
                    card.Width *
                    0.75f,
                    cardMin.Y +
                    card.Height -
                    Ui(20f)),
                ImGui.GetColorU32(
                    new Vector4(
                        0.7f,
                        0.7f,
                        0.8f,
                        effectiveAlpha *
                        0.35f)),
                4f);

            drawList.AddRectFilled(
                cardMin +
                new Vector2(
                    Ui(10f),
                    card.Height -
                    Ui(14f)),
                new Vector2(
                    cardMin.X +
                    card.Width *
                    0.5f,
                    cardMin.Y +
                    card.Height -
                    Ui(7f)),
                ImGui.GetColorU32(
                    new Vector4(
                        0.55f,
                        0.55f,
                        0.65f,
                        effectiveAlpha *
                        0.20f)),
                4f);
        }


        drawList.PopClipRect();

        var center =
            new Vector2(
                windowPos.X + windowSize.X / 2,
                windowPos.Y + windowSize.Y / 2);


        // ---------------------------------------------
        // AlphaChannel branding
        // ---------------------------------------------

        var logoTop =
            windowPos.Y + 70;

        if (alphaIconImage is not null)
        {
            var logoSize = Ui(110f);

            drawList.AddImage(
                alphaIconImage.GetWrapOrEmpty()
                    .Handle,
                new Vector2(
                    center.X - logoSize / 2,
                    logoTop),
                new Vector2(
                    center.X + logoSize / 2,
                    logoTop + logoSize));
        }

        var brandText =
            "ALPHA CHANNEL";

        SetUiFontScale(1.8f);

        var brandSize =
            ImGui.CalcTextSize(brandText);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                center.X - brandSize.X / 2,
                logoTop + Ui(130)),
            ImGui.GetColorU32(
                Vector4.One),
            brandText);

        SetUiFontScale(1f);

        var tagline =
    "Your gateway to shared media in Eorzea.";

        var taglineSize =
            ImGui.CalcTextSize(tagline);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                center.X - taglineSize.X / 2,
                logoTop + Ui(190)),
            ImGui.GetColorU32(
                new Vector4(
                    0.65f,
                    0.62f,
                    0.75f,
                    0.85f *
firstLaunchFadeAlpha)),
tagline);

        // Branding glow divider
        var lineY =
            logoTop + 220;

        drawList.AddLine(
            new Vector2(
                center.X - Ui(55),
                lineY),
            new Vector2(
                center.X + Ui(55),
                lineY),
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.8f)),
            3f);

        // ---------------------------------------------
        // Phase timing
        // ---------------------------------------------

        var elapsed =
            ImGui.GetTime() -
            firstLaunchStartedAt;

        var loadingElapsed =
            firstLaunchSetupSubmitted
                ? ImGui.GetTime() -
                  firstLaunchLoadingStartedAt
                : 0d;

        string introText;

        if (!firstLaunchSetupSubmitted)
        {
            firstLaunchPhase =
                0;

            //
            // Once typing completes this remains visible while onboarding fades in
            // and while the user chooses their topics.
            //
            introText =
                GetTypewriterText(
                    "Welcome to Alpha Channel",
                    Math.Min(
                        elapsed,
                        FirstLaunchWelcomeDuration),
                    1.5,
                    12);
        }
        else
        {
            firstLaunchPhase =
                1;

            introText =
                GetTypewriterText(
                    "We're just getting things ready for you...",
                    loadingElapsed,
                    0.5,
                    10);
        }

        var statusText =
            FirstLaunchMessages[
                firstLaunchMessageIndex];

        if (firstLaunchSetupSubmitted &&
            loadingElapsed > 6d &&
            ImGui.GetTime() -
            firstLaunchLastMessageChange >
            3d)
        {
            firstLaunchMessageIndex++;

            if (firstLaunchMessageIndex >=
                FirstLaunchMessages.Length)
            {
                firstLaunchMessageIndex =
                    0;
            }

            firstLaunchLastMessageChange =
                ImGui.GetTime();
        }
        // ---------------------------------------------
        // Draw intro text
        // ---------------------------------------------

        if (!string.IsNullOrEmpty(
                introText))
        {
            SetUiFontScale(
                1.35f);

            var introSize =
                ImGui.CalcTextSize(
                    introText);

            var introY =
                center.Y -
                70f;

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    center.X -
                    introSize.X * 0.5f,
                    introY),
                ImGui.GetColorU32(
                    new Vector4(
                        1f,
                        1f,
                        1f,
                        firstLaunchFadeAlpha)),
                introText);

            SetUiFontScale(
                1f);
        }

        // ---------------------------------------------
        // Draw loading status
        // ---------------------------------------------

        if (firstLaunchSetupSubmitted &&
            loadingElapsed >= 6d)
        {
            var statusSize =
                ImGui.CalcTextSize(
                    statusText);

            var statusAlpha =
                Math.Clamp(
                    (float)(
                        (
                            loadingElapsed -
                            6d
                        ) /
                        0.75d
                    ),
                    0f,
                    1f);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
     new Vector2(
         center.X -
         statusSize.X *
         0.5f,
         center.Y +
         Ui(250f)),
                 ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        firstLaunchFadeAlpha *
                        statusAlpha)),
                statusText);
        }

        // ---------------------------------------------
        // Embedded onboarding form
        // ---------------------------------------------

        DrawFirstLaunchSetupForm(
            center,
            elapsed);

        // ---------------------------------------------
        // TV-style spinner
        // ---------------------------------------------

        var loadingAlpha =
            GetFirstLaunchLoadingAlpha();

        if (loadingAlpha > 0f)
        {
            var spinnerCenter =
                new Vector2(
                    center.X,
                    center.Y + Ui(135f));

            DrawFirstLaunchSpinnerFeatures(
                drawList,
                spinnerCenter,
                loadingAlpha);

            const float radius =
                90f;

            const float thickness =
                8f;

            var time =
                (float)ImGui.GetTime();

            var startAngle =
                time * 3f;

            //
            // Segmented arc with a fading tail.
            //
            const int segments =
                80;

            for (var index = 0;
                 index < segments;
                 index++)
            {
                var progress =
                    index /
                    (float)segments;

                var angle =
                    startAngle +
                    progress *
                    MathF.PI *
                    2f;

                var alpha =
                    MathF.Pow(
                        progress,
                        2.4f) *
                    firstLaunchFadeAlpha *
                    loadingAlpha;

                var point =
                    spinnerCenter +
                    new Vector2(
                        MathF.Cos(
                            angle),
                        MathF.Sin(
                            angle)) *
                    radius;

                drawList.AddCircleFilled(
                    point,
                    thickness,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            alpha)));
            }

            //
            // Bright moving head.
            //
            var headAngle =
                startAngle;

            var head =
                spinnerCenter +
                new Vector2(
                    MathF.Cos(
                        headAngle),
                    MathF.Sin(
                        headAngle)) *
                radius;

            drawList.AddCircleFilled(
                head,
                14f,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        firstLaunchFadeAlpha *
                        loadingAlpha)));
        }

        //
        // Block the complete splash surface while leaving only the embedded
        // form available for interaction. Every blocked area also acts as a
        // window-dragging surface.
        //
        DrawFirstLaunchInteractionShield(
            windowPos,
            windowSize,
            center,
            elapsed);

        //
        // Close ##firstLaunchInputBlockerWindow, which was opened at the
        // beginning of this method.
        //
        ImGui.End();
    }


    // PreDraw runs before the main window's Begin().
    // Keep Size populated only while a programmatic size must be applied.
    // Setting Size to null is what stops Dalamud from submitting
    // SetNextWindowSize for the normal user-resizable window.
    // PreDraw runs before the main window's Begin().
    //
    // MinimizedSize and FirstLaunchWindowSize are temporary presentation
    // sizes. userWindowSize is the user's remembered normal size and is
    // the only one that should be persisted.
    public override void PreDraw()
    {
        if (windowMinimized)
        {
            Flags |=
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoBackground;

            //
            // Mini Mode uses its own fixed status-bar dimensions.
            //
            SizeConstraints =
                new WindowSizeConstraints
                {
                    MinimumSize = MinimizedSize,
                    MaximumSize = MinimizedSize,
                };

            SizeCondition =
                ImGuiCond.Always;

            Size =
                MinimizedSize;

            return;
        }

        if (showingFirstLaunch)
        {
            Flags &=
                ~ImGuiWindowFlags.NoBackground;

            //
            // The splash uses fixed dimensions. Remove the resize grips entirely
            // so attempting to drag an edge cannot disturb or dismiss onboarding.
            //
            Flags |=
                ImGuiWindowFlags.NoResize;

            var splashSize =
                ClampWindowSize(
                    FirstLaunchWindowSize);

            SizeConstraints =
                new WindowSizeConstraints
                {
                    MinimumSize = splashSize,
                    MaximumSize = splashSize,
                };

            SizeCondition =
                ImGuiCond.Always;

            Size =
                splashSize;

            //
            // LoadWindowSizeFromConfig() sets userResized so the saved size is
            // normally applied on the first frame. The splash has already supplied
            // the desired startup dimensions, so consume that pending request here.
            // Otherwise it fires immediately after the splash and shrinks the window.
            //
            userResized =
                false;

            return;
        }

        //
        // The splash has finished, so restore ordinary window resizing.
        //
        Flags &=
            ~(ImGuiWindowFlags.NoResize |
              ImGuiWindowFlags.NoBackground);

        //
        // Normal expanded-window constraints.
        //
        SizeConstraints =
            new WindowSizeConstraints
            {
                MinimumSize = MinimumWindowSize,
                MaximumSize = CurrentMaximumWindowSize,
            };

        if (userResized)
        {
            userWindowSize =
                ClampWindowSize(
                    userWindowSize);

            SizeCondition =
                ImGuiCond.Always;

            Size =
                userWindowSize;

            userResized =
                false;

            return;
        }

        //
        // Allow normal user resizing when no programmatic size needs to
        // be applied.
        //
        Size =
            null;

        SizeCondition =
            ImGuiCond.None;
    }

    private void OpenPlayerSearch(
        int tab,
        string value)
    {
        currentPage =
            HomePage.Player;

        activePlayerDrawer =
            PlayerDrawer.PlayVideo;

        playerSourceTab =
            tab;

        pendingPlayerSearch =
            value;

        //
        // Searches opened from elsewhere in the plugin should go directly
        // to their requested source instead of showing the landing page.
        //
        showingAddMediaSources =
            false;
    }

    private void EnsureAlphaIconLoaded()
    {
        if (alphaIconImage is not null)
        {
            return;
        }

        var path = Path.Combine(
            Plugin.PluginInterface.AssemblyLocation.DirectoryName!,
            "Assets",
            "alphaicon.png");

        if (File.Exists(path))
        {
            alphaIconImage = Plugin.TextureProvider.GetFromFile(path);
        }
    }

    private void EnsureReactPreviewLoaded()
    {
        if (reactPreviewImage is not null)
        {
            return;
        }

        var path = Path.Combine(
            Plugin.PluginInterface.AssemblyLocation.DirectoryName!,
            "Assets",
            "reactpreview.png");


        if (File.Exists(path))
        {
            reactPreviewImage =
                Plugin.TextureProvider.GetFromFile(path);
        }
    }

    private void EnsureWatchPartyHeaderLoaded()
    {
        if (watchPartyHeaderImage is not null)
        {
            return;
        }

        var path = Path.Combine(
            Plugin.PluginInterface.AssemblyLocation.DirectoryName!,
            "Assets",
            "watchpartyheader.png");

        if (File.Exists(path))
        {
            watchPartyHeaderImage =
                Plugin.TextureProvider.GetFromFile(path);
        }
    }

    private void HandleHomeDragScroll()
    {
        // Mouse wheel scrolling is handled natively by ImGui.
        // This adds click-and-drag scrolling when dragging empty Home background.
        if (!ImGui.IsWindowHovered())
        {
            return;
        }

        // Don't steal drags from buttons, media cards, search inputs, etc.
        if (ImGui.IsAnyItemHovered() ||
            ImGui.IsAnyItemActive())
        {
            return;
        }

        if (!ImGui.IsMouseDragging(
                ImGuiMouseButton.Left,
                4f))
        {
            return;
        }

        var delta = ImGui.GetIO().MouseDelta.Y;

        if (MathF.Abs(delta) < 0.01f)
        {
            return;
        }

        ImGui.SetScrollY(
            Math.Clamp(
                ImGui.GetScrollY() - delta,
                0f,
                ImGui.GetScrollMaxY()));

        lastScrollInteractionTime = ImGui.GetTime();
    }

    public override void Draw()
    {
        Colors =
            ThemeCatalog.Get(
                Plugin.Cfg.UiTheme,
                Plugin.Cfg.UiBackground);

        //
        // The splash may return before DrawSidebar(), so its branding image
        // must be loaded independently of the sidebar.
        //
        EnsureAlphaIconLoaded();

        EnsureCustomBackgroundLoaded();
        EnsureReactPreviewLoaded();
        EnsureWatchPartyHeaderLoaded();
        customBackgroundActive = Plugin.Cfg.UiBackground == UiBackground.Custom && customBackground is not null;
        EnsureSubscriptionVideosLoaded();

        RefreshLayoutScale(ImGui.GetWindowSize());
        using var fontScale = new GlobalFontScaleScope(layoutScale);
        using var theme = new ThemeScope(layoutScale);
        CaptureCurrentPosition();
        if (!windowMinimized)
        {
            var currentSize = ClampWindowSize(ImGui.GetWindowSize());
            var sizeDelta = currentSize - userWindowSize;
            var sizeChanged =
                MathF.Abs(sizeDelta.X) > 1f ||
                MathF.Abs(sizeDelta.Y) > 1f;

            var userDraggingResize =
                ImGui.IsMouseDragging(
                    ImGuiMouseButton.Left,
                    1f) &&
                !ImGui.IsAnyItemActive();

            if (userDraggingResize && sizeChanged)
            {
                // Record the size without setting userResized. Setting that
                // flag here would make PreDraw force the size on the following
                // frame and fight ImGui while the drag is still in progress.
                userWindowSize = currentSize;
                if (Plugin.Cfg.WindowSizePreset !=
                    UiWindowSizePreset.Uhd)
                {
                    Plugin.Cfg.WindowSizePreset =
                        UiWindowSizePreset.Custom;
                }
                Plugin.Cfg.WindowWidth = userWindowSize.X;
                Plugin.Cfg.WindowHeight = userWindowSize.Y;
                windowSizeSavePending = true;
            }

            if (windowSizeSavePending &&
                !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                Plugin.Cfg.Save();
                windowSizeSavePending = false;
            }
        }

        if (windowMinimized)
        {
            customBackgroundActive = false;
            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
            {
                DrawMinimizedBar();
            }

            return;
        }

        DrawCustomBackgroundLayer();

        //
        // While onboarding or the splash is visible, do not draw the
        // underlying page at all. Several media cards use direct mouse
        // rectangle checks, so drawing them before an overlay would allow
        // their actions to run even when visually covered.
        //
        if (showingFirstLaunch)
        {
            if (!firstLaunchStarted)
            {
                firstLaunchStarted =
                    true;

                BeginFirstLaunch();
            }

            DrawFirstLaunchOverlay();

            return;
        }

        DrawSignInModal();

        // DrawGlowBorder();

        var avail =
            ImGui.GetContentRegionAvail();

        var playbackActive =
    queue.Current is not null ||
    video.IsPlayingLocalVideo;

        if (playbackActive && !playbackWasActive)
        {
            playbackStartedAt = ImGui.GetTime();
        }

        if (!playbackActive && playbackWasActive)
        {
            playbackStoppedAt = ImGui.GetTime();
        }

        playbackWasActive = playbackActive;

        if (playbackActive && !lastPlaybackState)
        {
            playbackStartedAt = ImGui.GetTime();
        }

        lastPlaybackState = playbackActive;

        var topHeight = MathF.Max(
            avail.Y,
            120f);

        var centerWidth = MathF.Max(avail.X - SidebarWidth, 0f);


        {


            ImGui.SameLine(0, 0);

            // Settings keeps a scrollbar so the long preferences sheet stays usable;
            // every other page hides chrome scrollbars.
            var contentFlags = currentPage switch
            {
                HomePage.Settings =>
     PaddedChild,

                HomePage.Home =>
                    PaddedChild,

                _ =>
                    PaddedChild,
            };

            var contentOrigin = ImGui.GetCursorScreenPos();

            using (ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(24, 18)))

            using (ImRaii.PushColor(ImGuiCol.ChildBg, SidebarBg))
            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(16, 18)))
            using (var sidebar = ImRaii.Child(
                "##sidebar",
                new Vector2(SidebarWidth, topHeight),
                false,
                NavPaneFlags))
            {
                if (sidebar)
                {
                    DrawSidebar();
                }
            }



            // Sidebar right border - subtle theme accent divider
            var sidebarEdge = ImGui.GetItemRectMax().X;
            var dividerList =
    ImGui.GetWindowDrawList();

            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();

            dividerList.AddLine(
                new Vector2(sidebarEdge - 1f, windowPos.Y + Ui(6f)),
                new Vector2(sidebarEdge - 1f, windowPos.Y + windowSize.Y - Ui(6f)),
                ImGui.GetColorU32(new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.14f)),
                1f);

            ImGui.SameLine(0, 0);


            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12, 18)))
            using (var content = ImRaii.Child(
                "##content",
                new Vector2(centerWidth, topHeight),
                false,
                contentFlags))
            {
                if (content)
                {
                    SetUiFontScale(1f);

                    // ---------------------------------------------------------
                    // Page entrance transition
                    // ---------------------------------------------------------

                    if (currentPage != lastAnimatedPage)
                    {
                        lastAnimatedPage = currentPage;
                        pageTransitionStartedAt = ImGui.GetTime();
                    }

                    const float pageTransitionDuration = 0.30f;
                    const float pageTransitionDistance = 18f;

                    float pageTransitionProgress;

                    if (pageTransitionStartedAt < 0d)
                    {
                        pageTransitionProgress = 1f;
                    }
                    else
                    {
                        pageTransitionProgress =
                            Math.Clamp(
                                (float)((ImGui.GetTime() - pageTransitionStartedAt) /
                                        pageTransitionDuration),
                                0f,
                                1f);
                    }

                    // Smooth ease-out rather than a linear movement.
                    var pageTransitionEased =
                        1f -
                        MathF.Pow(
                            1f - pageTransitionProgress,
                            3f);

                    var pageTransitionOffset =
                        pageTransitionDistance *
                        (1f - pageTransitionEased);

                    var pageTransitionAlpha =
                        0.15f +
                        (0.85f * pageTransitionEased);

                    // Start the new page very slightly lower and settle it into place.
                    if (pageTransitionOffset > 0.01f)
                    {
                        ImGui.SetCursorPosY(
                            ImGui.GetCursorPosY() +
                            pageTransitionOffset);
                    }

                    // Fade only the page content.
                    // The scrollbar and player bar remain unaffected.
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.Alpha,
                        pageTransitionAlpha))
                    {
                        DrawContent();
                    }

                    DrawCustomContentScrollbar();

                    if (playbackActive &&
    ShouldShowBottomPlaybackBar)
                    {
                        ImGui.Dummy(
                            new Vector2(
                                0,
                                BottomBarHeight));
                    }

                    if (currentPage == HomePage.Home)
                    {
                        HandleHomeDragScroll();
                    }
                }
            }




            var showingPlaybackBar =
                ShouldShowBottomPlaybackBar &&
                (
                    playbackActive ||
                    (ImGui.GetTime() - playbackStoppedAt) < 0.35f
                );

            if (showingPlaybackBar)
            {
                //
                // sidebarEdge comes directly from the sidebar child's item rectangle,
                // so it includes the main window's padding and is the authoritative
                // boundary between the sidebar and page content.
                //
                DrawBottomBar(
                    playbackActive,
                    sidebarEdge);
            }

            // Overlay last — its own ImGui window so clicks aren't eaten by the content/rail children.
            DrawWindowControlsStrip();

            DrawPlaybackErrorToast();

            //
            // Friend profiles and the DJ connection guide must be drawn after
            // the sidebar, content, scrollbar and divider.
            //

            DrawProfilePopup();
            DrawPatreonPopup();
            DrawDjConnectionGuideOverlay();
            DrawLiveStreamGuideOverlay();
            DrawDjStationEditorOverlay();
            DrawPendingWatchPartyCreationOverlay();
            DrawGamePageDialogs();
            DrawInternetArchiveEpisodeDialog();
            DrawYouTubeSubscriptionsOverlay();
            DrawYouTubeTopicsOverlay();

            //
            // Queue creation/editing uses the same global overlay style
            // as username, controller, and Watch Party password prompts.
            //
            DrawCreateQueuePopup();
            DrawDeleteQueuePopup();
            DrawClearQueuePopup();

            // Watch-party viewer media decisions must be drawn globally.
            // A request can originate from Home, Add Media, Browse Videos, etc.
            DrawViewerMediaActionPopup();

            //
            // The viewer TV decision must be global because the shared
            // state can arrive while any Watch Party sub-tab is selected.
            //
            DrawViewerTvSpawnPrompt();
            DrawHostLeaveConfirmationPopup();
            //
            // Draw password prompts globally so they appear above the
            // content panel and block interaction underneath them.
            //
            DrawCreateLockedRoomPasswordPopup();

            DrawPartyDirectoryPasswordPopup();

            //
            // The splash returns from Draw() before reaching this point.
            // Normal interface startup continues here after it has finished.
            //
            EnsureTopicVideoStartup();

            //
            // Keep the standalone popup for later manual username changes
            // and administrator-triggered resets.
            //
            DrawNamePrompt();
        }
    }

    // No title bar means no native minimize/close chrome - these two replace it. Minimize collapses
    // the window down to MinimizedSize (see PreDraw) rather than just hiding content at full size,
    // so it actually reads as "tucked out of the way" instead of an empty box; close just does what
    // /achannel already does (IsOpen = false).
    //
    // Floated in a tiny sibling window just above the neon glow so (1) it sits outside the main
    // chrome and (2) hit-testing works — parent InvisibleButtons under child panes never receive
    // clicks even when painted with the foreground draw list.
    // Chrome is outline-only (no solid fill) so it doesn't read as a double-stacked pill.
    private void DrawWindowControlsStrip()
    {
        var buttonSize = Ui(26f);
        const float gap = 8f;
        const float pad = 2f;
        var glowClearance = Ui(5f);

        var mainPos = ImGui.GetWindowPos();
        var mainSize = ImGui.GetWindowSize();
        var stripW = pad * 2 + buttonSize * 2 + gap;
        var stripH = pad * 2 + buttonSize;

        var stripPos = new Vector2(
            mainPos.X + mainSize.X - stripW - Ui(10f),
            mainPos.Y - stripH - glowClearance);

        ImGui.SetNextWindowPos(stripPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(stripW, stripH), ImGuiCond.Always);

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(pad, pad))
                   .Push(ImGuiStyleVar.ItemSpacing, new Vector2(gap, 0f)))
        {
            const ImGuiWindowFlags flags =
                ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoMove
                | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.NoDocking
                | ImGuiWindowFlags.NoBackground;

            if (!ImGui.Begin("##alphaWindowControls", flags))
            {
                ImGui.End();
                return;
            }

            if (DrawWindowControlButton(
                     "##ctlMin",
                     FontAwesomeIcon.WindowMinimize,
                     buttonSize))
            {
                // We are currently drawing the small controls-strip window.
                // Pass the captured main-window size explicitly so that strip
                // size is never mistaken for the normal Alpha Channel size.
                SetMinimized(
                    true,
                    mainSize);
            }

            ImGui.SameLine(0, gap);

            if (DrawWindowControlButton("##ctlClose", FontAwesomeIcon.Times, buttonSize))
            {
                CloseUi();
            }

            ImGui.End();
        }
    }

    // Invisible hit target + theme-glow outline (Accent / MagentaGlow from the picked theme).
    private static bool DrawWindowControlButton(string id, FontAwesomeIcon icon, float size)
    {
        var origin = ImGui.GetCursorScreenPos();
        ImGui.PushID(id);
        var clicked = ImGui.InvisibleButton("##hit", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        var drawList = ImGui.GetWindowDrawList();
        // Idle: soft MagentaGlow (same family as the outer halo). Hover: Accent rim strength.
        var background = hovered
            ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.20f)
            : new Vector4(CardBg.X, CardBg.Y, CardBg.Z, 0.95f);

        drawList.AddRectFilled(
            origin,
            origin + new Vector2(size, size),
            ImGui.GetColorU32(background),
            8f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            var textSize = ImGui.CalcTextSize(glyph);
            var glyphColor = hovered
                ? AccentHover
                : new Vector4(Accent.X, Accent.Y, Accent.Z, 0.70f);
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), origin + new Vector2(size, size) / 2f - textSize / 2f,
                ImGui.GetColorU32(glyphColor), glyph);
        }

        return clicked;
    }

    // Compact Mini Mode status bar. It remains draggable and keeps the old
    // capsule's persisted position while exposing useful playback/party shortcuts.
    private void DrawMinimizedBar()
    {
        var origin = ImGui.GetWindowPos();
        var size = MinimizedSize;
        var drawList = ImGui.GetWindowDrawList();
        var rounding = size.Y * 0.5f;

        // Body only on the window list (clipped). Glow/rim go through DrawGlowBorder on the
        // foreground list so the halo isn't cut off into a hard red stroke.
        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(new Vector4(SidebarBg.X, SidebarBg.Y, SidebarBg.Z, 0.96f)),
            rounding);

        DrawGlowBorder(rounding);

        var chipSize = Ui(26f);
        var chipGap = Ui(4f);
        // Snap the controls to the capsule's visual centre. Half-pixel positions
        // make these small circular buttons look uneven after rasterisation.
        var controlsY = MathF.Round(origin.Y + (size.Y - chipSize) * 0.5f);
        var closeOrigin = new Vector2(origin.X + size.X - Ui(8f) - chipSize, controlsY);
        var restoreOrigin = closeOrigin - new Vector2(chipSize + chipGap, 0f);
        var chatOrigin = restoreOrigin - new Vector2(chipSize + chipGap, 0f);
        var playerOrigin = chatOrigin - new Vector2(chipSize + chipGap, 0f);

        var inParty = stream.Mode is StreamMode.Hosting or StreamMode.Viewing;
        var canControlPlayback = stream.Mode != StreamMode.Viewing &&
                                 video.State is VideoPlaybackState.Playing or VideoPlaybackState.Paused;
        var (position, duration, paused) = video.GetProgress();
        _ = position;
        _ = duration;

        var transportOrigin = playerOrigin - new Vector2(chipSize + chipGap, 0f);
        var rightContentEdge = canControlPlayback ? transportOrigin.X : playerOrigin.X;

        // Brand mark.
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var logo = FontAwesomeIcon.PlayCircle.ToIconString();
            var logoSize = ImGui.CalcTextSize(logo);
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                origin + new Vector2(Ui(14f), (size.Y - logoSize.Y) * 0.5f),
                ImGui.GetColorU32(Accent),
                logo);
        }

        var title = GetMiniModeTitle();
        var titleStart = origin.X + 38f;
        var statusReserve = inParty ? 126f : 16f;
        var titleWidth = MathF.Max(80f, rightContentEdge - titleStart - statusReserve);
        title = TruncateToWidth(title, titleWidth);
        var titleSize = ImGui.CalcTextSize(title);
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(titleStart, origin.Y + (size.Y - titleSize.Y) * 0.5f),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.94f)),
            title);

        if (inParty)
        {
            var viewerText = stream.Roster.Length == 1
                ? "1 watching"
                : $"{stream.Roster.Length} watching";
            var viewerSize = ImGui.CalcTextSize(viewerText);
            var viewerX = rightContentEdge - viewerSize.X - 18f;
            drawList.AddCircleFilled(
                new Vector2(viewerX - Ui(8f), origin.Y + size.Y * 0.5f),
                3.5f,
                ImGui.GetColorU32(Good));
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(viewerX, origin.Y + (size.Y - viewerSize.Y) * 0.5f),
                ImGui.GetColorU32(MutedText),
                viewerText);
        }

        if (canControlPlayback &&
            DrawMinimizedRoundButton(
                "##miniTransport",
                transportOrigin,
                chipSize,
                paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause,
                Accent,
                paused ? "Play" : "Pause"))
        {
            video.Pause(!paused);
        }

        var playerClicked = DrawMinimizedRoundButton(
            "##miniPlayer",
            playerOrigin,
            chipSize,
            FontAwesomeIcon.Tv,
            Accent,
            "Video Player");
        var chatClicked = DrawMinimizedRoundButton(
            "##miniChat",
            chatOrigin,
            chipSize,
            FontAwesomeIcon.Comments,
            Accent,
            "Chat");
        var restoreClicked = DrawMinimizedRoundButton(
            "##windowRestore", restoreOrigin, chipSize, FontAwesomeIcon.Expand, Accent,
            "Return to full size window");
        var closeClicked = DrawMinimizedRoundButton(
            "##windowCloseMini", closeOrigin, chipSize, FontAwesomeIcon.Times, Danger,
            "Close");

        // Drag region covers the title/status area but never any action chip.
        var dragWidth = MathF.Max(rightContentEdge - origin.X - 8f, 0f);
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##minimizedDrag", new Vector2(dragWidth, size.Y));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        }

        if (closeClicked)
        {
            CloseUi();
        }
        else if (playerClicked)
        {
            OnMiniPlayerRequested?.Invoke();
        }
        else if (chatClicked)
        {
            OnMiniChatRequested?.Invoke();
        }
        else if (restoreClicked ||
                 (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)))
        {
            SetMinimized(false);
        }
    }

    internal string GetMiniModeTitle()
    {
        var title = queue.Current?.Title;

        if (string.IsNullOrWhiteSpace(title))
        {
            title = stream.CurrentRoomState?.MediaTitle;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = screenController.Engine.GetMediaTitle();
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        if (stream.Mode == StreamMode.Viewing &&
            joinedHostDisplayName is { Length: > 0 } host)
        {
            return $"Watching {host}";
        }

        return inPartyLabel();

        string inPartyLabel() =>
            stream.Mode == StreamMode.Hosting
                ? "Your Watch Party"
                : "Alpha Channel Mini";
    }

    internal string GetMiniPartyTitle()
    {
        if (stream.Mode == StreamMode.Viewing &&
            joinedHostDisplayName is { Length: > 0 } host)
        {
            return $"{host}'s Watch Party";
        }

        return stream.Mode == StreamMode.Hosting
            ? "Your Watch Party"
            : "Watch Party Chat";
    }

    internal void OpenFullWatchPartyChat()
    {
        currentPage = HomePage.WatchAlong;
        partyPanelTab = PartyPanelTab.Chat;
        OpenUi();
    }

    private static string TruncateToWidth(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
        {
            return text;
        }

        const string ellipsis = "…";
        while (text.Length > 1 &&
               ImGui.CalcTextSize(text + ellipsis).X > width)
        {
            text = text[..^1];
        }

        return text + ellipsis;
    }

    private bool DrawMinimizedRoundButton(
        string id, Vector2 origin, float size, FontAwesomeIcon icon, Vector4 hoverColor,
        string tooltip)
    {
        ImGui.SetCursorScreenPos(origin);
        ImGui.PushID(id);
        var clicked = ImGui.InvisibleButton("##ctl", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        if (hovered)
        {
            ImGui.SetTooltip(tooltip);
        }

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var text = icon.ToIconString();
            // Font Awesome glyphs have different internal bearings, and drawing
            // them at the full UI font size makes the wider symbols appear off
            // centre. Keep a consistent visual footprint inside every chip and
            // compensate for the icon font's high baseline.
            const float glyphScale = 0.82f;
            var glyphSize = ImGui.CalcTextSize(text) * glyphScale;
            var opticalOffset = icon switch
            {
                FontAwesomeIcon.Tv => UiVec(2f, 0.5f),
                FontAwesomeIcon.Comments => UiVec(2f, 0.5f),
                FontAwesomeIcon.Expand => UiVec(2.5f, 1f),
                FontAwesomeIcon.Times => UiVec(1.5f, 0.5f),
                FontAwesomeIcon.Play => new Vector2(0.5f, 1f),
                FontAwesomeIcon.Pause => new Vector2(0f, 1f),
                _ => new Vector2(0f, 1f),
            };
            drawList.AddText(
                UiBuilder.IconFont,
                ImGui.GetFontSize() * glyphScale,
                origin +
                new Vector2(size, size) * 0.5f -
                glyphSize * 0.5f +
                opticalOffset,
                ImGui.GetColorU32(hovered ? hoverColor : new Vector4(1f, 1f, 1f, 0.78f)),
                text);
        }

        ImGui.PopID();
        return clicked;
    }

    private void DrawContent()
    {
        // Still parked from launch cut — bounce home if somehow selected.
        if (currentPage is HomePage.Activity
            or HomePage.GoLive)
        {
            currentPage = HomePage.Home;
        }

        switch (currentPage)
        {
            case HomePage.Home:
                DrawHome();
                break;
            case HomePage.Player:
                PageTitle(
                    "Add Media",
                    "Choose what you want to put on the TV.");
                DrawPlayerPage();
                break;
            case HomePage.VideoGrid:
                PageTitle(
                    "Browse YouTube",
                    "Discover the latest videos from your topics and subscriptions");
                DrawVideoGrid();
                break;
            case HomePage.PlaySnes:
                PageTitle(
                    "Play Games",
                    "Play classic games on your in-game screen and broadcast to friends.");
                DrawCompactGamesPage();
                break;
            // Alpha Channel embedded-browser integration.
            case HomePage.Browser:
                PageTitle("Browser", "Browse the web on your in-game screen and share it with friends.");
                DrawBrowserPage();
                break;
            case HomePage.InternetArchive:
                PageTitle("Internet Archive", "Discover and play video from the Internet Archive.");
                DrawInternetArchivePage();
                break;
            case HomePage.UnifiedSearch:
                PageTitle("Search Videos", "Search YouTube, Dailymotion and the Internet Archive.");
                DrawUnifiedSearchPage();
                break;
            case HomePage.WatchAlong:
                PageTitle(
                    "Watch Party",
                    "Host or join a room and watch together.");
                DrawWatchPartyPage();
                break;
            case HomePage.PartyDirectory:
                PageTitleBack(
                    "Public Watch Parties",
                    "Discover rooms, venues, and people watching together.",
                    HomePage.WatchAlong);
                DrawPartyDirectoryPage();
                break;
            case HomePage.Screen:
                PageTitle("Screen", "Place the picture in the world.");
                DrawScreenControls();
                break;
            case HomePage.Friends:
                PageTitle("Friends", "People you can invite and join.");
                DrawFriends();
                break;
            case HomePage.Messages:
                PageTitleBack("Alpha Chat", "Private messages between friends.", HomePage.Friends);
                DrawMessages();
                break;
            case HomePage.Settings:
                PageTitle("Settings", "Account, appearance, and plugin preferences.");
                DrawSettings();
                break;
        }
    }

    // Soft neon halo around the window. Must use the foreground draw list — the window draw list
    // clips to the window rect, which cuts off any outward glow and leaves a hard rim.
    // roundingOverride: capsule uses half-height; full window uses the default 16.
    private void DrawGlowBorder(float rounding = 16f)
    {
        var drawList = ImGui.GetForegroundDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();

        // Very restrained violet halo — enough to separate the window from the game,
        // but no longer reads as an RGB/neon frame.
        for (var layer = 3; layer >= 1; layer--)
        {
            var outset = 0f;
            var alpha = 0.018f + (4 - layer) * 0.012f;

            drawList.AddRect(
                min - new Vector2(outset, outset),
                max + new Vector2(outset, outset),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        alpha)),
                rounding + outset * 0.45f,
                ImDrawFlags.None,
                1.5f + layer * 0.25f);
        }

        // Thin muted violet perimeter.
        drawList.AddRect(
            min + new Vector2(0.5f, 0.5f),
            max - new Vector2(0.5f, 0.5f),
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.48f)),
            rounding,
            ImDrawFlags.None,
            1.1f);

        // Barely-visible inner edge gives the frame a little depth without introducing cyan.
        if (rounding < max.Y * 0.45f)
        {
            drawList.AddRect(
                min + UiVec(2f, 2f),
                max - UiVec(2f, 2f),
                ImGui.GetColorU32(
                    new Vector4(
                        1f,
                        1f,
                        1f,
                        0.035f)),
                MathF.Max(4f, rounding - 2f),
                ImDrawFlags.None,
                1f);
        }
    }

    private void DrawSidebar()
    {
        // The sidebar is the first area converted to the experimental UHD
        // scale. Setting its window scale here also lets ordinary ImGui text
        // and the nested navigation child inherit the larger font metrics.
        SetUiFontScale(1f);

        //
        // =========================================================
        // FIXED SIDEBAR BRANDING
        // =========================================================
        //
        // The Alpha Channel logo and wordmark always remain visible
        // at the top of the sidebar.
        //

        var brandOrigin =
          ImGui.GetCursorScreenPos();

        // Local X position where the sidebar's usable content begins.
        // This includes the child window's left padding.
        var sidebarContentStartX =
            ImGui.GetCursorPosX();

        var sidebarWidth =
            ImGui.GetContentRegionAvail().X;

        var drawList =
            ImGui.GetWindowDrawList();

        var mark = Ui(42f);


        EnsureAlphaIconLoaded();


        var centeredLogo =
            brandOrigin +
            new Vector2(
                (sidebarWidth - mark) * 0.5f,
                0f);


        var alphaWrap =
            alphaIconImage?
                .GetWrapOrDefault();


        if (alphaWrap is not null)
        {
            drawList.AddImage(
                alphaWrap.Handle,
                centeredLogo,
                centeredLogo +
                new Vector2(
                    mark,
                    mark),
                Vector2.Zero,
                Vector2.One,
                ImGui.GetColorU32(
                    Vector4.One));
        }


        ImGui.Dummy(
            new Vector2(
                0f,
                mark));


        var brandText =
            "ALPHA CHANNEL";

        var brandScale = 1.25f;
        SetUiFontScale(brandScale);
        var textWidth = ImGui.CalcTextSize(brandText).X;
        if (textWidth > sidebarWidth && textWidth > 1f)
        {
            brandScale *= sidebarWidth / textWidth;
            SetUiFontScale(brandScale);
            textWidth = ImGui.CalcTextSize(brandText).X;
        }

        ImGui.SetCursorPosX(
        sidebarContentStartX +
        MathF.Max(
            0f,
            (sidebarWidth - textWidth) * 0.5f));

        ImGui.TextUnformatted(brandText);
        SetUiFontScale(1f);


        //
        // Keep the existing heading-hide threshold based on the
        // OUTER sidebar height.
        //
        // DrawNavGroup() will run inside the scrolling child below,
        // so it must not use that smaller child's height.
        //

        var hideHeadingsBelowHeight = Ui(790f);

        var compactSidebar =
            ImGui.GetWindowHeight() <=
            hideHeadingsBelowHeight;


        //
        // Refresh sidebar data before entering the scrolling child.
        //

        if (CurrentSession is { } sidebarSession &&
            friendsDirty &&
            !friendsLoading)
        {
            RefreshFriends(
                sidebarSession.Token);
        }


        //
        // =========================================================
        // SCROLLABLE NAVIGATION AREA
        // =========================================================
        //
        // Everything between the fixed logo and fixed support row
        // lives inside this child.
        //
        // NoScrollbar hides the scrollbar chrome.
        //
        // We intentionally DO NOT use NoScrollWithMouse here, so
        // the mouse wheel can scroll this region whenever its
        // contents are taller than the available height.
        //

        var supportFooterHeight = Ui(34f);


        var availableMiddleHeight =
            ImGui.GetContentRegionAvail().Y;


        var navigationHeight =
            MathF.Max(
                1f,
                availableMiddleHeight -
                supportFooterHeight);


        const ImGuiWindowFlags navigationFlags =
            ImGuiWindowFlags.AlwaysUseWindowPadding |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoSavedSettings;


        using (ImRaii.PushStyle(
            ImGuiStyleVar.WindowPadding,
            Vector2.Zero))
        using (var navigation = ImRaii.Child(
            "##sidebarNavigation",
            new Vector2(
                -1f,
                navigationHeight),
            false,
            navigationFlags))
        {
            if (navigation)
            {
                DrawNavGroup(
                    "Discover",
                    compactSidebar);


                DrawNavItem(
                    HomePage.Home,
                    FontAwesomeIcon.Home,
                    "Home");


                var playerLabel =
                    queue.Entries.Count > 0
                        ? $"Media Player ({queue.Entries.Count})"
                        : "Media Player";


                DrawNavItem(
                    HomePage.Player,
                    FontAwesomeIcon.Play,
                    playerLabel);


                DrawNavItem(
                    HomePage.VideoGrid,
                    FontAwesomeIcon.ThLarge,
                    "Browse YouTube");


                DrawNavItem(
                    HomePage.PlaySnes,
                    FontAwesomeIcon.Gamepad,
                    "Play Games");

                // Alpha Channel embedded-browser integration.
                DrawNavItem(
                    HomePage.Browser,
                    FontAwesomeIcon.Globe,
                    "Browser");

                DrawNavItem(
                    HomePage.InternetArchive,
                    FontAwesomeIcon.Film,
                    "Internet Archive");


                DrawNavGroup(
                    "Social",
                    compactSidebar);


                DrawNavItem(
                    HomePage.WatchAlong,
                    FontAwesomeIcon.Users,
                    "Watch Party");


                DrawNavItem(
                    HomePage.Friends,
                    FontAwesomeIcon.UserFriends,
                    "Friends",
                    friendRequests.Incoming.Length);


                DrawNavItem(
                     HomePage.PartyDirectory,
                     FontAwesomeIcon.Search,
                     "Party Directory");


                DrawNavGroup(
                    "Tools",
                    compactSidebar);


                DrawNavItem(
                    HomePage.Screen,
                    FontAwesomeIcon.Desktop,
                    "Screen");


                DrawNavItem(
                    HomePage.Settings,
                    FontAwesomeIcon.Cog,
                    "Settings");


                if (CurrentSession is { } dmSidebarSession &&
                    currentPage == HomePage.Messages &&
                    conversationsDirty &&
                    !conversationsLoading)
                {
                    RefreshConversations(
                        dmSidebarSession.Token);
                }
            }
        }


        //
        // =========================================================
        // FIXED SUPPORT FOOTER
        // =========================================================
        //
        // Patreon and Discord are outside the scrolling child.
        //
        // They therefore remain anchored at the bottom regardless
        // of where the navigation list has been scrolled.
        //

        //
        // Small breathing room above the fixed support row.
        //

        ImGui.Dummy(
            UiVec(0f, 2f));


        var supportRowWidth =
            ImGui.GetContentRegionAvail().X;


        var supportGap = Ui(5f);


        var supportButtonWidth =
            (supportRowWidth - supportGap) *
            0.5f;


        DrawCompactSupportLink(
            "♥ Patreon",
            supportButtonWidth,
            Ui(28f),
            PatreonOrange,
            PatreonOrangeHover,
            null);


        ImGui.SameLine(
            0f,
            supportGap);


        DrawCompactSupportLink(
            "● Discord",
            supportButtonWidth,
            Ui(28f),
            new Vector4(
                0.42f,
                0.52f,
                1.00f,
                1f),
            new Vector4(
                0.58f,
                0.66f,
                1.00f,
                1f),
            "https://discord.gg/YOUR_INVITE_HERE");

        //
        // Keep the support buttons just off the bottom edge.
        //

        ImGui.Dummy(
            UiVec(0f, 3f));
    }

    private void DrawNavGroup(
     string label,
     bool compactSidebar)
    {
        //
        // ---------------------------------------------------------
        // Compact-height sidebar groups
        // ---------------------------------------------------------
        //
        // The caller measures the OUTER sidebar height before
        // entering the scrollable navigation child.
        //
        // This preserves the existing outer-sidebar threshold even
        // though these headings now live inside another child.
        //

        if (compactSidebar)
        {
            ImGui.Dummy(
                new Vector2(
                    0f,
                    Ui(6f)));

            return;
        }


        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(4f)));


        SetUiFontScale(
            0.85f);


        ImGui.TextColored(
            MutedText,
            label);


        SetUiFontScale(
            1f);


        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(1f)));
    }

    private void DrawNavItem(HomePage page, FontAwesomeIcon icon, string label, int badgeCount = 0,
        bool forceActive = false)
    {
        var active = forceActive || currentPage == page;
        ImGui.PushID((int)page);

        var rowStart = ImGui.GetCursorScreenPos();
        var rowSize = new Vector2(ImGui.GetContentRegionAvail().X, Ui(38f));
        var drawList = ImGui.GetWindowDrawList();

        var clicked = ImGui.InvisibleButton("##navrow", rowSize);
        var hovered = ImGui.IsItemHovered();

        if (active)
        {
            drawList.AddRectFilled(rowStart, rowStart + rowSize, ImGui.GetColorU32(Accent), Ui(10f));
        }
        else if (hovered)
        {
            drawList.AddRectFilled(rowStart, rowStart + rowSize, ImGui.GetColorU32(CardBgHover), Ui(10f));
        }

        var textColor = active ? Vector4.One : MutedText;
        drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), rowStart + UiVec(12, 9),
            ImGui.GetColorU32(textColor), icon.ToIconString());using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), rowStart + UiVec(38, 9),
            ImGui.GetColorU32(textColor), label);

        if (badgeCount > 0)
        {
            var badgeText = badgeCount > 9 ? "9+" : badgeCount.ToString();
            var badgeCenter = rowStart + new Vector2(rowSize.X - Ui(14f), rowSize.Y / 2);
            drawList.AddCircleFilled(badgeCenter, Ui(8f), ImGui.GetColorU32(active ? Vector4.One : Danger));
            var textSize = ImGui.CalcTextSize(badgeText);
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), badgeCenter - textSize / 2,
                ImGui.GetColorU32(active ? Accent : Vector4.One), badgeText);
        }

        ImGui.PopID();

        if (clicked)
        {
            currentPage = page;
        }
    }

    private void DrawCustomContentScrollbar()
    {
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();

        var scrollY = ImGui.GetScrollY();
        var maxScroll = ImGui.GetScrollMaxY();

        var scrollingRecently =
    ImGui.GetTime() - lastScrollInteractionTime < 1.2f;

        var scrollbarAlpha = scrollingRecently ? 0.85f : 0.28f;

        if (maxScroll <= 0f)
            return;

        var scrollbarX = windowPos.X + windowSize.X - Ui(7f);
        var scrollbarTop = windowPos.Y + Ui(8f);
        var scrollbarBottomClearance = BottomBarHeight + Ui(12f);
        var scrollbarHeight = windowSize.Y - scrollbarBottomClearance - Ui(16f);

        var thumbHeight =
            MathF.Max(
                Ui(40f),
                scrollbarHeight * (windowSize.Y / (windowSize.Y + maxScroll)));

        var scrollPercent = scrollY / maxScroll;

        var thumbY =
            scrollbarTop +
            (scrollbarHeight - thumbHeight) * scrollPercent;

        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            new Vector2(scrollbarX, scrollbarTop),
            new Vector2(scrollbarX + Ui(4f), scrollbarTop + scrollbarHeight),
            ImGui.GetColorU32(new Vector4(
    Accent.X,
    Accent.Y,
    Accent.Z,
    0.10f)),
            3f);

        drawList.AddRect(
    new Vector2(scrollbarX - 1f, thumbY - 1f),
    new Vector2(scrollbarX + Ui(7f), thumbY + thumbHeight + 1f),
    ImGui.GetColorU32(
        new Vector4(
            Accent.X,
            Accent.Y,
            Accent.Z,
            0.25f)),
    4f,
    ImDrawFlags.None,
    1f);

        drawList.AddRectFilled(
            new Vector2(scrollbarX, thumbY),
            new Vector2(scrollbarX + Ui(6f), thumbY + thumbHeight),
            ImGui.GetColorU32(
    new Vector4(
        Accent.X,
        Accent.Y,
        Accent.Z,
        scrollbarAlpha)),
            3f);
    }

    private void DrawWatchingStat()
    {
        var onlineFriends = friends.Count(f => f.Online);
        var realtimeColor = stream.ConnectionState switch
        {
            RealtimeConnectionState.Connected => Good,
            RealtimeConnectionState.Connecting or
            RealtimeConnectionState.Reconnecting => ConnectionPendingOrange,
            _ => Danger,
        };
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(realtimeColor,
                FontAwesomeIcon.Circle.ToIconString());
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Alpha Channel: {stream.ConnectionStatusText}");
        }

        ImGui.SameLine();
        if (stream.Mode != StreamMode.None)
        {
            ImGui.TextUnformatted($"{stream.Roster.Length}");
            ImGui.SameLine();
            ImGui.TextColored(MutedText, "in party");
        }
        else
        {
            ImGui.TextUnformatted($"{onlineFriends}");
            ImGui.SameLine();
            ImGui.TextColored(MutedText, onlineFriends == 1 ? "friend online" : "friends online");
        }

        if (usersOnlineCount > 0 || stream.IsConnected)
        {
            var label = usersOnlineCount == 1 ? "1 user" : $"{usersOnlineCount} users";
            var labelWidth = ImGui.CalcTextSize(label).X;
            var right = ImGui.GetWindowContentRegionMax().X;
            ImGui.SameLine();
            ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX() + Ui(8f), right - labelWidth));
            ImGui.TextColored(MutedText, label);
        }
    }

    // Ko-fi brand pink — left-nav footer above the version. Alternates ask ↔ CTA every 30s.

    private static readonly Vector4 PatreonOrange = new(1f, 0.55f, 0.15f, 1f);
    private static readonly Vector4 PatreonOrangeHover = new(1f, 0.68f, 0.30f, 1f);
    private static readonly Vector4 ConnectionPendingOrange = new(1f, 0.55f, 0.15f, 1f);
    private static readonly string[] DonateLabels =
    [
        "Hey, like what you see?\nConsider supporting us",
        "Donate on Ko-fi",
    ];
    private const double DonateRotateSeconds = 30;

    private static void DrawSupportLink(
    string label,
    float height,
    Vector4 color,
    Vector4 hoverColor,
    string url)
    {
        var width = ImGui.GetContentRegionAvail().X - 24f;
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        var buttonOrigin = origin + UiVec(12f, 0);

        ImGui.SetCursorScreenPos(buttonOrigin);

        if (ImGui.InvisibleButton($"##{label}", size))
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                AepLog.Warning($"[Support] Failed to open browser: {exception.Message}");
            }
        }

        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRect(
            buttonOrigin,
            buttonOrigin + size,
            ImGui.GetColorU32(hovered ? hoverColor : color),
            8f,
            ImDrawFlags.None,
            1f);

        var textSize = ImGui.CalcTextSize(label);
        var textPos = buttonOrigin + new Vector2(
            (width - textSize.X) * 0.5f,
            (height - textSize.Y) * 0.5f);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
      textPos,
      ImGui.GetColorU32(hovered ? hoverColor : color),
      label);
    }


    // ---------------------------------------------------------
    // Compact support link
    // ---------------------------------------------------------
    //
    // Unlike DrawSupportLink(), this one accepts an explicit
    // width so several support links can share one row.
    //

    private void DrawCompactSupportLink(
        string label,
        float width,
        float height,
        Vector4 color,
        Vector4 hoverColor,
        string? url)
    {
        var buttonOrigin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);


        ImGui.SetCursorScreenPos(
            buttonOrigin);


        if (ImGui.InvisibleButton(
                $"##support_{label}",
                size))
        {
            if (url is null)
            {
                patreonPopupOpen = true;
            }
            else
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception exception)
                {
                    AepLog.Warning($"[Support] Failed to open browser: {exception.Message}");
                }
            }
        }


        var hovered =
            ImGui.IsItemHovered();


        var drawList =
            ImGui.GetWindowDrawList();


        drawList.AddRect(
            buttonOrigin,
            buttonOrigin + size,
            ImGui.GetColorU32(
                hovered
                    ? hoverColor
                    : color),
            Ui(8f),
            ImDrawFlags.None,
            Ui(1f));


        var textSize =
            ImGui.CalcTextSize(
                label);


        var textPos =
            buttonOrigin +
            new Vector2(
                (width - textSize.X) * 0.5f,
                (height - textSize.Y) * 0.5f);


        drawList.AddText(
            ImGui.GetFont(),
            ImGui.GetFontSize(),
            textPos,
            ImGui.GetColorU32(
                hovered
                    ? hoverColor
                    : color),
            label);
    }


  

    private void DrawDonateLink(string label, float height)
    {
        var width = ImGui.GetContentRegionAvail().X - 24f;
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        var buttonOrigin = origin + UiVec(12f, 0);

        ImGui.SetCursorScreenPos(buttonOrigin);


        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
    }

       

  

    // Every non-Home page starts with back + title + a one-line purpose so each Channel reads as
    // its own place, not a clone of every other tab with a different header string.
    private void PageTitle(string text, string purpose) => PageTitleBack(text, purpose, HomePage.Home);

    private void PageTitleBack(
     string text,
     string purpose,
     HomePage backPage)
    {
        //
        // ---------------------------------------------------------
        // Shared non-Home page header
        // ---------------------------------------------------------
        //

        var headerStartX =
            ImGui.GetCursorPosX();

        var headerY =
            ImGui.GetCursorPosY();

        var headerWidth =
            ImGui.GetContentRegionAvail().X;


        //
        // ---------------------------------------------------------
        // Left: Back button + page title
        // ---------------------------------------------------------
        //

        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.12f))
               .Push(
                   ImGuiCol.ButtonHovered,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.22f))
               .Push(
                   ImGuiCol.ButtonActive,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.30f))
               .Push(
                   ImGuiCol.Text,
                   AccentHover))
        {
            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                if (ImGui.Button(
                        $"{FontAwesomeIcon.ArrowLeft.ToIconString()}##backPage",
                        UiVec(34f, 30f)))
                {
                    currentPage =
                        backPage;
                }
            }
        }


        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                backPage == HomePage.Home
                    ? "Back to Home"
                    : "Back to Apps");
        }


        ImGui.SameLine(
            0f,
            12f);


        ImGui.BeginGroup();


        SetUiFontScale(
            1.35f);

        ImGui.TextUnformatted(
            text);

        SetUiFontScale(
            1f);


        ImGui.TextColored(
            MutedText,
            purpose);


        ImGui.EndGroup();


        //
        // ---------------------------------------------------------
        // Right: profile + social status
        // ---------------------------------------------------------
        //
        // Match the Home profile block visually.
        //
        // Unlike Home, this shared non-Home version has its own
        // responsive threshold and stays visible until the available
        // header width drops below 450px.
        //

        if (headerWidth >= 450f)
        {
            var session =
                CurrentSession;


            var friendsOnline =
                friends.Count(
                    friend => friend.Online);


            var displayName =
                !string.IsNullOrWhiteSpace(
                    session?.DisplayName)
                    ? session.DisplayName
                    : "Unknown";


            var friendsText =
                friendsOnline == 1
                    ? "1 friend online"
                    : $"{friendsOnline} friends online";


            var watchersText =
                usersOnlineCount == 1
                    ? "1 watcher online"
                    : $"{usersOnlineCount} watchers online";


            //
            // Same dimensions as Home.
            //

            var avatarSize = Ui(38f);

            var profileWidth = Ui(185f);


            //
            // Same 10px right inset as Home.
            //

            var profileX =
                headerStartX +
                headerWidth -
                profileWidth -
                10f;


            //
            // Same vertical offset as Home.
            //

            var profileY =
                headerY - 2f;


            var profileOrigin =
                new Vector2(
                    profileX,
                    profileY);


            //
            // Avatar
            //

            ImGui.SetCursorPos(
                profileOrigin);


            DrawAvatarChip(
                session?.AvatarIcon,
                session?.AvatarColorHex,
                avatarSize,
                session?.AvatarImageUrl);


            //
            // Text starts just to the right of the avatar.
            //

            var textX =
                profileX +
                avatarSize +
                10f;


            //
            // First row:
            //
            // ● Kodie
            //

            ImGui.SetCursorPos(
                new Vector2(
                    textX,
                    profileY + 1f));


            ImGui.TextColored(
                stream.ConnectionState switch
                {
                    RealtimeConnectionState.Connected => Good,
                    RealtimeConnectionState.Connecting or
                    RealtimeConnectionState.Reconnecting => ConnectionPendingOrange,
                    _ => Danger,
                },
                "●");

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"Alpha Channel: {stream.ConnectionStatusText}");
            }


            ImGui.SameLine(
                0f,
                5f);


            ImGui.TextUnformatted(
                displayName);


            //
            // Second row:
            //
            // 1 friend online
            //

            ImGui.SetCursorPos(
                new Vector2(
                    textX,
                    profileY + Ui(17f)));


            SetUiFontScale(
                0.84f);


            ImGui.TextColored(
                MutedText,
                friendsText);


            //
            // Third row:
            //
            // 2 watchers online
            //

            ImGui.SetCursorPos(
                new Vector2(
                    textX,
                    profileY + Ui(35f)));


            SetUiFontScale(
                1.02f);


            ImGui.TextColored(
                Good,
                watchersText);


            SetUiFontScale(
                1f);
        }


        //
        // ---------------------------------------------------------
        // Header divider
        // ---------------------------------------------------------
        //
        // Explicitly restore the cursor below the header because the
        // profile above uses absolute cursor positioning.
        //

        ImGui.SetCursorPos(
            new Vector2(
                headerStartX,
                headerY + Ui(54f)));


        ImGui.Dummy(
            UiVec(0f, 8f));


        var origin =
            ImGui.GetCursorScreenPos();

        var width =
            ImGui.GetContentRegionAvail().X;


        ImGui.GetWindowDrawList().AddRectFilled(
            origin,
            origin +
            new Vector2(
                width,
                1f),
            ImGui.GetColorU32(
                BorderSubtle));


        ImGui.Dummy(
            new Vector2(
                width,
                Ui(18f)));
    }

    // Consistent accent-colored sub-headers within a page — same weight on every Channel.
    private static void SectionHeader(string text)
    {
        ImGui.TextColored(Accent, text);
        ImGui.Dummy(new Vector2(0, 0));
    }

    // Soft panel for content that needs grouping. Height must be >0 — Child size.y=0 means
    // "fill remaining host height" in ImGui, which swallowed the Player search section below.
    private static void DrawCard(string id, Action draw)
    {
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(16, 14)))
        using (var card = ImRaii.Child(id, new Vector2(-1, 1), false,
                   PaddedChild | ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (card)
            {
                draw();
            }
        }

        ImGui.Spacing();
    }

    // Tall accent-edged panel for the "main thing" on media/live pages (now playing, room status).
    private static void DrawStage(string id, Action draw)
    {
        var origin = ImGui.GetCursorScreenPos();
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBgHover))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(20, 18)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f)))
        using (var stage = ImRaii.Child(id, new Vector2(-1, 1), false,
                   PaddedChild | ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (stage)
            {
                draw();
            }
        }

        var end = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddRectFilled(origin, new Vector2(origin.X + Ui(3f), end.Y),
            ImGui.GetColorU32(Accent), 2f);
        ImGui.Spacing();
        ImGui.Spacing();
    }

    // Activity feed row: left rail + text, no card chrome.
    private static void DrawTimelineRow(string id, string text, bool unread = false)
    {
        ImGui.PushID(id);
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var wrapWidth = MathF.Max(ImGui.GetContentRegionAvail().X - 28f, 40f);
        var textHeight = ImGui.CalcTextSize(text, false, wrapWidth).Y;
        var height = MathF.Max(textHeight + 12f, 28f);

        drawList.AddLine(origin + UiVec(7, 0), origin + new Vector2(Ui(7), height),
            ImGui.GetColorU32(BorderSubtle), 1.5f);
        drawList.AddCircleFilled(origin + UiVec(7, 12), unread ? 4.5f : 3.5f,
            ImGui.GetColorU32(unread ? Accent : MutedText));

        ImGui.SetCursorScreenPos(origin + UiVec(22, 4));
        ImGui.PushTextWrapPos(origin.X + 22f + wrapWidth);
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();

        var afterY = ImGui.GetCursorScreenPos().Y;
        ImGui.SetCursorScreenPos(new Vector2(origin.X, MathF.Max(afterY, origin.Y + height) + Ui(2f)));
        ImGui.PopID();
    }

    private static void DrawPlainEmpty(string message, string? buttonLabel = null, Action? onClick = null)
    {
        ImGui.Dummy(UiVec(0, 8));
        ImGui.TextColored(MutedText, message);
        if (buttonLabel is not null && onClick is not null)
        {
            ImGui.Spacing();
            if (ImGui.Button(buttonLabel, UiVec(160, 30)))
            {
                onClick();
            }
        }

        ImGui.Dummy(UiVec(0, 8));
    }

    private static void DrawEmptyCard(string id, string message, string? buttonLabel = null, Action? onClick = null)
    {
        DrawCard(id, () =>
        {
            ImGui.TextColored(MutedText, message);
            if (buttonLabel is null || onClick is null)
            {
                return;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(buttonLabel))
            {
                onClick();
            }
        });
    }

    private void DrawNamePrompt()
    {
        if (!namePromptActive)
        {
            return;
        }

        var promptWidth = Ui(620f);
        var promptHeight = Ui(455f);

        //
        // Cover only the Alpha Channel window.
        //
        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var promptPos =
            new Vector2(
                parentPos.X +
                (parentSize.X - promptWidth) * 0.5f,
                parentPos.Y +
                (parentSize.Y - promptHeight) * 0.5f);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!ImGui.Begin(
                "##usernameOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        //
        // Darken Alpha Channel behind the prompt.
        //
        drawList.AddRectFilled(
            parentPos,
            parentPos + parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.48f)));

        //
        // Draw the username card itself.
        //
        var promptMax =
            promptPos +
            new Vector2(
                promptWidth,
                promptHeight);

        drawList.AddRectFilled(
            promptPos,
            promptMax,
            ImGui.GetColorU32(
                new Vector4(
                    0.055f,
                    0.065f,
                    0.11f,
                    1f)),
            10f);

        drawList.AddRect(
            promptPos,
            promptMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.45f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        //
        // Everything below is inside the SAME overlay window,
        // so clicking the dark area can no longer cover the prompt.
        //
        const float padding = 20f;

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(17f)));

        SetUiFontScale(
            1.3f);

        ImGui.TextColored(
            AccentHover,
            "Welcome to Alpha Channel!");

        SetUiFontScale(
            1f);

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(53f)));

        SetUiFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Choose your username");

        SetUiFontScale(
            1f);

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(79f)));

        ImGui.TextColored(
            MutedText,
            "Choose the username other Alpha Channel users will see.");

        ImGui.SameLine(
            0f,
            7f);

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            ImGui.TextDisabled(
                FontAwesomeIcon.InfoCircle.ToIconString());
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "This is the name used to add you to friends lists and join your watch party.");
        }

        //
        // Username input.
        //
        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(111f)));

        ImGui.SetNextItemWidth(
            promptWidth -
            (padding * 2f));

        using (ImRaii.PushColor(
            ImGuiCol.FrameBg,
            new Vector4(
                0.025f,
                0.03f,
                0.055f,
                1f)))
        using (ImRaii.PushColor(
            ImGuiCol.Border,
            new Vector4(
                Accent.X,
                Accent.Y,
                Accent.Z,
                0.75f)))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameBorderSize,
            1f))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            5f))
        {
            ImGui.InputText(
                "##usernameInput",
                ref namePromptInput,
                32);
        }

        //
        // Topic subscriptions.
        //
        //
        // Divider between username and topic selection.
        //
        var dividerY =
            promptPos.Y +
            158f;

        drawList.AddLine(
            new Vector2(
                promptPos.X +
                padding,
                dividerY),
            new Vector2(
                promptMax.X -
                padding,
                dividerY),
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.28f)),
            1f);

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(172f)));

        ImGui.TextColored(
            Vector4.One,
            "Subscribe to your favourite topics");

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(198f)));

        ImGui.TextColored(
            MutedText,
            "Choose at least 3 topics to receive relevant video recommendations.");

        ImGui.SameLine(
            0f,
            7f);

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            ImGui.TextDisabled(
                FontAwesomeIcon.InfoCircle.ToIconString());
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "You can change these later in Settings.");
        }

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(230f)));

        ImGui.PushID(
            "welcomeTopicSelection");

        DrawTrendingTopicTags(
    columnHeight: 145f,
    availableWidthOverride:
        promptWidth -
        (padding * 2f));

        ImGui.PopID();

        var selectedTopicCount =
            GetSubscribedTopicCount();

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(384f)));

        var topicCountValid =
            selectedTopicCount is >= 3 and <= 15;

        ImGui.TextColored(
            topicCountValid
                ? Accent
                : new Vector4(
                    1f,
                    0.55f,
                    0.35f,
                    1f),
             $"{selectedTopicCount} topics selected (minimum 3)");

        var valid =
            !string.IsNullOrWhiteSpace(
                namePromptInput) &&
            topicCountValid;

        //
        // Confirm button.
        //
        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(414f)));

        if (!valid)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button(
          "Confirm",
          UiVec(120f, 32f)))
        {
            Plugin.Cfg.Save();

            onNameConfirmed?.Invoke(
                namePromptInput.Trim());

            onNameConfirmed =
                null;

            namePromptActive =
                false;

            namePromptPending =
                false;

            //
            // First-launch topics have now been submitted. Start the same
            // shared Topics-cache pipeline used by Browse Videos while the
            // splash animation runs independently.
            //
            browseVideoRequested =
                true;

            topicVideoStartupRequested =
                true;

            homeYouTubeResults =
                null;

            homeYouTubeSelectedTopics.Clear();

            isLoadingHomeYouTube =
                true;

            _ =
                PrepareTopicVideosForLaunchAsync(
                    forceRefresh: false);
        }

        if (!valid)
        {
            ImGui.EndDisabled();
        }

        if (topicSelectionLimitWarning)
        {
            ImGui.SameLine(
                0f,
                10f);

            ImGui.TextColored(
                new Vector4(
                    1f,
                    0.55f,
                    0.35f,
                    1f),
                "You may only choose up to 15 topics, " +
                "please remove one to add another");
        }

        ImGui.End();
    }

    private void DrawRoster(
     string label,
     bool allowPromote)
    {
        var realCount =
            stream.Roster.Length;

        SetUiFontScale(
            1.08f);

        ImGui.TextColored(
            Vector4.One,
            label);

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 8f));

        if (realCount == 0)
        {
            ImGui.TextColored(
                MutedText,
                "Waiting for people to join...");

            return;
        }

        DrawAvatarStack(
            stream.Roster,
            maxShown: 12);

        ImGui.Dummy(
            UiVec(0f, 10f));

        for (var index = 0;
             index < realCount;
             index++)
        {
            var participant =
                stream.Roster[index];

            ImGui.PushID(
                participant.UserId);

            DrawPartyRosterRow(
                participant.DisplayName,
                allowPromote,
                canUseActions: true,
                onPromote:
                    () =>
                    {
                        _ =
                            stream.TransferHostAsync(
                                participant.UserId);
                    });

            ImGui.PopID();

            ImGui.Dummy(
                UiVec(0f, 6f));
        }
    }

    private void DrawPartyRosterRow(
    string displayName,
    bool allowPromote,
    bool canUseActions,
    Action? onPromote)
    {
        var rowHeight = Ui(52f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f)))
        using (var row = ImRaii.Child(
            "##partyParticipantRow",
            new Vector2(-1f, rowHeight),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!row)
            {
                return;
            }

            var origin =
                ImGui.GetCursorScreenPos();

            var rowWidth =
                ImGui.GetWindowWidth();

            // Online/live indicator.
            ImGui.GetWindowDrawList()
                .AddCircleFilled(
                    origin +
                    UiVec(18f, 26f),
                    4f,
                    ImGui.GetColorU32(Good));

            // Name.
            ImGui.GetWindowDrawList()
                .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    origin +
                    UiVec(32f, 18f),
                    ImGui.GetColorU32(
                        Vector4.One),
                    displayName);

            if (!allowPromote)
            {
                return;
            }

            var rightPadding = Ui(12f);
            const float gap = 8f;

            var kickSize =
                UiVec(112f, 30f);

            var hostSize =
                UiVec(104f, 30f);

            // -----------------------------------------------------
            // Kick from room — UI only for now
            // -----------------------------------------------------

            var kickPos =
                new Vector2(
                    origin.X +
                    rowWidth -
                    rightPadding -
                    kickSize.X,
                    origin.Y + Ui(11f));

            ImGui.SetCursorScreenPos(
                kickPos);

            using (ImRaii.Disabled(
                !canUseActions))
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(
                    0.16f,
                    0.055f,
                    0.07f,
                    1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(
                        0.22f,
                        0.07f,
                        0.09f,
                        1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(
                        0.25f,
                        0.08f,
                        0.10f,
                        1f)))
            {
                // Deliberately no action yet.
                ImGui.Button(
                    "Kick from room",
                    kickSize);
            }

            // -----------------------------------------------------
            // Make host
            // -----------------------------------------------------

            var hostPos =
                new Vector2(
                    kickPos.X -
                    gap -
                    hostSize.X,
                    kickPos.Y);

            ImGui.SetCursorScreenPos(
                hostPos);

            using (ImRaii.Disabled(
                !canUseActions))
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(
                    0.055f,
                    0.07f,
                    0.115f,
                    1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(
                        0.075f,
                        0.095f,
                        0.15f,
                        1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(
                        0.075f,
                        0.095f,
                        0.15f,
                        1f)))
            {
                if (ImGui.Button(
                    "Make host",
                    hostSize) &&
                    canUseActions)
                {
                    onPromote?.Invoke();
                }
            }
        }
    }

    public void Dispose()
    {
        StopBrowserWatchPartyBroadcast();
        Interlocked.Increment(ref unifiedSearchGeneration);
        unifiedSearchCts?.Cancel();
        unifiedSearchCts?.Dispose();
        unifiedSearchCts = null;
        DisposeInternetArchive();
        PersistPositions();

        signInCts?.Cancel();
        signInCts?.Dispose();
        signInCts = null;

        browseVideosCts?.Cancel();
        browseVideosCts?.Dispose();
        browseVideosCts = null;

        subscriptionVideosCts?.Cancel();
        subscriptionVideosCts?.Dispose();
        subscriptionVideosCts = null;

        Interlocked.Increment(
            ref subscriptionVideoLoadGeneration);

        Interlocked.Increment(
            ref topicSettingsRefreshGeneration);

        imagePreviews.Dispose();
        thumbnails.Dispose();

        homeHero?.Dispose();
        homeHero = null;

        customBackground?.Dispose();
        customBackground = null;
    }

    // Shared by every partial that wants a play/pause/skip/volume-style glyph button instead of a
    // text label - Dalamud bundles FontAwesome already, no extra font asset needed.
    private static bool IconButton(FontAwesomeIcon icon)
    {
        using var iconFont = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.Button(icon.ToIconString());
    }

    private readonly struct ThemeScope : IDisposable
    {
        private const int ColorCount = 10;
        private const int StyleCount = 7;

        public ThemeScope(float scale)
        {
            ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBg);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, WindowBg);
            ImGui.PushStyleColor(ImGuiCol.PopupBg, CardBg);
            ImGui.PushStyleColor(ImGuiCol.Button, Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActive);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, FrameBg);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, FrameBgHover);
            ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
            ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, AccentActive);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 12f * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 12f * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(12f, 10f) * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, 8f) * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(8f, 6f) * scale);
        }

        public void Dispose()
        {
            ImGui.PopStyleVar(StyleCount);
            ImGui.PopStyleColor(ColorCount);
        }
    }

    private readonly struct GlobalFontScaleScope : IDisposable
    {
        private readonly float previousScale;

        public GlobalFontScaleScope(float scale)
        {
            var io = ImGui.GetIO();
            previousScale = io.FontGlobalScale;
            io.FontGlobalScale = previousScale * scale;
        }

        public void Dispose()
        {
            ImGui.GetIO().FontGlobalScale = previousScale;
        }
    }
}
