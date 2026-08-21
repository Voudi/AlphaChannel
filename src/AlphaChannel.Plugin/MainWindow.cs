using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
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
// hand-drawn ImDrawList primitives (MainWindow.Home.cs) where ImGui has no built-in equivalent -
// not a port of Aetherphone's Typography/Squircle kit, still too much surface area for this tool.
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

    // Set each frame in Draw() when a custom background texture is actually showing.
    private static bool customBackgroundActive;

    private static Vector4 FadeForCustomBg(Vector4 color, float alpha) =>
        customBackgroundActive ? new Vector4(color.X, color.Y, color.Z, alpha) : color;

    private static Vector4 Hex(int rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f,
        1f);

    private enum HomePage
    {
        Home,
        Player,
        VideoGrid,
        Screen,
        WatchAlong,
        Friends,
        Messages,
        Activity,
        Tweeter,
        Apps,
        PluginHub,
        Venues,
        GoLive,
        Settings,
    }

    private readonly ScreenController screenController;
    private readonly VideoPlayer video;
    private readonly AetherStreamQueue queue;
    private readonly StreamClient stream;
    private readonly ThumbnailCache thumbnails = new();
    private readonly Action requestRename;
    private readonly SignInFlow signInFlow;
    private readonly AuthClient authClient;
    private readonly FriendsClient friendsClient;
    private readonly ActivityClient activityClient;
    private readonly DmClient dmClient;
    private readonly ReportClient reportClient;
    private readonly TweeterClient tweeterClient;
    private readonly PluginHubClient pluginHubClient;
    private readonly VenuesClient venuesClient;
    private readonly LiveClient liveClient;
    private readonly TwitchClient twitchClient;
    private readonly Crypto.KeyVault keyVault;
    private readonly Whispers.WhisperMirror whisperMirror;

    // Called whenever sign-in/link/sign-out changes what CharacterSession belongs to the currently-
    // played character - the callback (Plugin.cs) is what actually writes Cfg.CharacterSessions and
    // saves, same split as requestRename above (MainWindow owns the UI, Plugin.cs owns persistence).
    private readonly Action<CharacterSession?> onSessionChanged;
    // Wide media-hub layout: left navigation + spacious content + social rail + compact player bar.
    private static readonly Vector2 WindowSize = new(1220, 840);
    private static readonly Vector2 MiniModeSize = new(260, 840);
    // Compact capsule chrome while tucked away - wide enough for brand + expand + close.
    private static readonly Vector2 MinimizedSize = new(276, 40);
    // Wider capsule when "Watching First Last" is showing (viewer-only join).
    private static readonly Vector2 MinimizedViewerSize = new(340, 40);
    private const int PositionPinFrames = 3;
    private bool windowMinimized;
    private bool miniMode;
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
    private string? joinError;

    private const float SidebarWidth = 185f;
    private const float RightRailWidth = 300f;
    private const float BottomBarHeight = 96f;

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
    private Action<string>? onNameConfirmed;

    //Scrollbar inactivity timer
    private double lastScrollInteractionTime;

    internal bool IsNamePromptActive => namePromptActive;

    // Updated every tick from Plugin.cs (cheap dictionary lookup there) - shown here instead of the
    // raw UserId so players never need to read each other an opaque GUID to join a stream.
    internal string? CurrentDisplayName { get; set; }

    // Also updated every tick from Plugin.cs, same reasoning as CurrentDisplayName - the signed-in
    // account (if any) for whichever character is currently being played, and the live character
    // name/world to sign in with if there isn't one yet.
    internal CharacterSession? CurrentSession { get; set; }
    internal string? CurrentCharacterName { get; set; }
    internal string? CurrentWorldName { get; set; }
    internal bool CurrentIsLalafell { get; set; }

    internal MainWindow(ScreenController screenController, VideoPlayer video, AetherStreamQueue queue,
        StreamClient stream, Action requestRename, AuthClient authClient, SignInFlow signInFlow,
        FriendsClient friendsClient, ActivityClient activityClient, DmClient dmClient, ReportClient reportClient,
        TweeterClient tweeterClient, PluginHubClient pluginHubClient, VenuesClient venuesClient, LiveClient liveClient,
        TwitchClient twitchClient, Crypto.KeyVault keyVault, Whispers.WhisperMirror whisperMirror,
        Action<CharacterSession?> onSessionChanged)
        : base("AlphaChannel###AlphaChannelMain")
    {
        this.screenController = screenController;
        this.video = video;
        this.queue = queue;
        this.stream = stream;
        this.requestRename = requestRename;
        this.authClient = authClient;
        this.signInFlow = signInFlow;
        this.friendsClient = friendsClient;
        this.activityClient = activityClient;
        this.dmClient = dmClient;
        this.reportClient = reportClient;
        this.tweeterClient = tweeterClient;
        this.pluginHubClient = pluginHubClient;
        this.venuesClient = venuesClient;
        this.liveClient = liveClient;
        this.twitchClient = twitchClient;
        this.keyVault = keyVault;
        this.whisperMirror = whisperMirror;
        this.onSessionChanged = onSessionChanged;

        whisperMirror.OnWhisperMessage += ApplyIncomingWhisper;

        stream.OnFriendRequestReceived += _ => friendsDirty = true;
        stream.OnFriendAccepted += _ => friendsDirty = true;
        stream.OnFriendRemoved += _ => friendsDirty = true;
        stream.OnPresenceUpdate += ApplyPresenceUpdate;
        stream.OnOnlineCount += count => usersOnlineCount = count;
        stream.OnActivityNew += _ => { activityDirty = true; activityUnreadDirty = true; };
        stream.OnDmMessage += ApplyIncomingDm;

        // Fixed size, no title bar/resize handles - reads as a real console/TV dashboard rather
        // than a floating dev-tool window. Actual size is set every frame in PreDraw (below), since
        // it toggles between WindowSize and MinimizedSize - SizeConstraints just has to be loose
        // enough to allow both (NoResize already blocks the player from dragging it anywhere else).
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        SizeCondition = ImGuiCond.Always;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimizedSize,
            MaximumSize = WindowSize,
        };

        stream.OnJoined += () => joinError = null;
        stream.OnDeclined += reason =>
        {
            joinError = string.IsNullOrEmpty(reason) ? "Could not find that host." : reason;
            if (proximityJoined)
            {
                proximityJoined = false;
                joinedHostDisplayName = null;
                viewerMode = false;
            }
        };
        stream.OnEnded += () =>
        {
            joinedHostDisplayName = null;
            viewerMode = false;
            proximityJoined = false;
        };

        maximizedPosition = Plugin.Cfg.MaximizedPosition;
        minimizedPosition = Plugin.Cfg.MinimizedPosition;
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
    internal void OpenViewerAndJoin(string hostDisplayName, bool fromProximity = false)
    {
        proximityJoined = fromProximity;
        viewerMode = true;
        currentPage = HomePage.Player;
        playerSourceTab = 0;
        SetMinimized(true);
        RequestPosition(minimizedPosition);
        IsOpen = true;
        DoJoin(hostDisplayName);
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

    internal void LeaveStream()
    {
        viewerMode = false;
        proximityJoined = false;
        joinedHostDisplayName = null;
        _ = stream.LeaveAsync();
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
        windowMinimized = false;
        IsOpen = false;
    }

    // Writes remembered placements when they changed — called on close and plugin unload.
    internal void PersistPositions()
    {
        if (Plugin.Cfg.MaximizedPosition == maximizedPosition &&
            Plugin.Cfg.MinimizedPosition == minimizedPosition)
        {
            return;
        }

        Plugin.Cfg.MaximizedPosition = maximizedPosition;
        Plugin.Cfg.MinimizedPosition = minimizedPosition;
        Plugin.Cfg.Save();
    }

    public override void OnClose() => PersistPositions();

    private void SetMinimized(bool minimized)
    {
        if (windowMinimized == minimized)
        {
            return;
        }

        windowMinimized = minimized;
        RequestPosition(minimized ? minimizedPosition : maximizedPosition);
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
    internal void RequestNamePrompt(string suggested, Action<string> onConfirmed)
    {
        if (namePromptActive)
        {
            return;
        }

        namePromptInput = suggested;
        onNameConfirmed = onConfirmed;
        namePromptActive = true;
        namePromptPending = true;
        IsOpen = true;
    }

    // Window.Size is only read once Begin() runs, which happens before Draw() - setting it from
    // inside Draw() would lag a frame behind a minimize/restore click, so it's set here instead
    // (Dalamud calls PreDraw before Begin every frame). Flags also flip here so the minimized
    // capsule can draw its own chrome (NoBackground) without NoMove blocking drag.
    public override void PreDraw()
    {
        Size = windowMinimized
            ? (viewerMode ? MinimizedViewerSize : MinimizedSize)
            : miniMode
                ? MiniModeSize
                : WindowSize;
        if (windowMinimized)
        {
            Flags = ImGuiWindowFlags.NoTitleBar
                    | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse
                    | ImGuiWindowFlags.NoBackground;
        }
        else
        {
            // Outer window never scrolls — Settings scrolls inside ##content instead.
            Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        }

        // Pin for a few frames after minimize/restore/reopen so the size swap doesn't leave the
        // window at the wrong corner; then clear Position so the player can drag freely again.
        if (pendingFrames > 0 && pendingPosition is { } target)
        {
            Position = target;
            PositionCondition = ImGuiCond.Always;
            pendingFrames--;
        }
        else
        {
            Position = null;
        }
    }

    private void OpenPlayerSearch(int tab, string value)
    {
        currentPage = HomePage.Player;
        activePlayerDrawer = PlayerDrawer.PlayVideo;

        playerSourceTab = tab;
        pendingPlayerSearch = value;
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
        Colors = ThemeCatalog.Get(Plugin.Cfg.UiTheme, Plugin.Cfg.UiBackground);
        EnsureCustomBackgroundLoaded();
        customBackgroundActive = Plugin.Cfg.UiBackground == UiBackground.Custom && customBackground is not null;
        using var theme = new ThemeScope();
        CaptureCurrentPosition();


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
        DrawNamePrompt();
        DrawSignInModal();
        DrawProfilePopup();
        // DrawGlowBorder();

        var avail = ImGui.GetContentRegionAvail();

        var playbackActive = queue.Current is not null;

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

        var showRightRail = false;
        var rightWidth = showRightRail ? RightRailWidth : 0f;
        var centerWidth = MathF.Max(avail.X - SidebarWidth - rightWidth, 0f);

        if (!miniMode)
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
                new Vector2(24, 18)))

            using (ImRaii.PushColor(ImGuiCol.ChildBg, SidebarBg))
            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16, 18)))
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
            var dividerList = ImGui.GetForegroundDrawList();

            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();

            dividerList.AddLine(
                new Vector2(sidebarEdge - 1f, windowPos.Y + 6f),
                new Vector2(sidebarEdge - 1f, windowPos.Y + windowSize.Y - 6f),
                ImGui.GetColorU32(new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.14f)),
                1f);

            ImGui.SameLine(0, 0);


            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12, 18)))
            using (var content = ImRaii.Child(
                "##content",
                new Vector2(centerWidth, topHeight),
                false,
                contentFlags))
            {
                if (content)
                {

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

                    if (playbackActive)
                    {
                        ImGui.Dummy(new Vector2(0, BottomBarHeight));
                    }

                    if (currentPage == HomePage.Home)
                    {
                        HandleHomeDragScroll();
                    }
                }
            }

           
        }

        if (showRightRail)
        {
            if (!miniMode)
            {
                ImGui.SameLine(0, 0);
            }
            using (ImRaii.PushColor(ImGuiCol.ChildBg, SidebarBg))
            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16, 18)))
            using (var rail = ImRaii.Child("##rightRail", new Vector2(RightRailWidth, topHeight), false, NavPaneFlags))
            {
                if (rail)
                {
                    if (playbackActive)
                    {
                        DrawPlaybackRightRail();
                    }
                    else
                    {
                        DrawHomeRightRail();
                    }
                }
            }
        }

        var showingPlaybackBar =
      playbackActive ||
        (ImGui.GetTime() - playbackStoppedAt) < 0.35f;

        if (showingPlaybackBar)
        {
            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();

            ImGui.SetCursorScreenPos(
                windowPos + new Vector2(
                    SidebarWidth,
                    windowSize.Y - BottomBarHeight));

            DrawBottomBar(playbackActive);
        }

        // Overlay last — its own ImGui window so clicks aren't eaten by the content/rail children.
        DrawWindowControlsStrip();

        DrawPlaybackErrorToast();
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
        const float buttonSize = 26f;
        const float gap = 8f;
        const float pad = 2f;
        const float glowClearance = 5f;

        var mainPos = ImGui.GetWindowPos();
        var mainSize = ImGui.GetWindowSize();
        var stripW = pad * 2 + buttonSize * 2 + gap;
        var stripH = pad * 2 + buttonSize;

        var stripPos = new Vector2(
            mainPos.X + mainSize.X - stripW - 10f,
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

            if (DrawWindowControlButton("##ctlMin", FontAwesomeIcon.WindowMinimize, buttonSize))
            {
                SetMinimized(true);
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
            drawList.AddText(origin + new Vector2(size, size) / 2f - textSize / 2f,
                ImGui.GetColorU32(glyphColor), glyph);
        }

        return clicked;
    }

    // Collapsed capsule - brand mark + expand control. Drag the bar to reposition; expand restores
    // via the chevron or a double-click (single-click-anywhere restore was blocking window moves).
    private void DrawMinimizedBar()
    {
        var origin = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
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

        // Accent orb instead of the chunky TV tile.
        var orbCenter = origin + new Vector2(18f, size.Y * 0.5f);
        drawList.AddCircleFilled(
            orbCenter,
            8f,
            ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.22f)));
        drawList.AddCircleFilled(orbCenter, 4.5f, ImGui.GetColorU32(Accent));

        var label = viewerMode && joinedHostDisplayName is { Length: > 0 } host
            ? $"Watching {host}"
            : "AlphaChannel";
        if (label.Length > 28)
        {
            label = label[..25] + "…";
        }

        var labelSize = ImGui.CalcTextSize(label);
        drawList.AddText(
            origin + new Vector2(32f, (size.Y - labelSize.Y) * 0.5f),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.92f)),
            label);

        const float chipSize = 24f;
        const float chipGap = 4f;
        var closeOrigin = origin + new Vector2(size.X - 8f - chipSize, (size.Y - chipSize) * 0.5f);
        var restoreOrigin = closeOrigin - new Vector2(chipSize + chipGap, 0f);
        var restoreClicked = DrawMinimizedRoundButton(
            "##windowRestore", restoreOrigin, chipSize, FontAwesomeIcon.ChevronUp, Accent);
        var closeClicked = DrawMinimizedRoundButton(
            "##windowCloseMini", closeOrigin, chipSize, FontAwesomeIcon.Times, Danger);

        // Drag region covers everything except the expand/close chips so NoTitleBar still moves.
        var dragWidth = MathF.Max(size.X - (chipSize * 2f) - chipGap - 12f, 0f);
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
        else if (restoreClicked || (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)))
        {
            SetMinimized(false);
        }
    }

    private bool DrawMinimizedRoundButton(
        string id, Vector2 origin, float size, FontAwesomeIcon icon, Vector4 hoverColor)
    {
        ImGui.SetCursorScreenPos(origin);
        ImGui.PushID(id);
        var clicked = ImGui.InvisibleButton("##ctl", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        var fill = hovered
            ? new Vector4(hoverColor.X, hoverColor.Y, hoverColor.Z, 0.28f)
            : new Vector4(1f, 1f, 1f, 0.06f);
        drawList.AddCircleFilled(origin + new Vector2(size, size) * 0.5f, size * 0.5f, ImGui.GetColorU32(fill));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var text = icon.ToIconString();
            var textSize = ImGui.CalcTextSize(text);
            drawList.AddText(
                UiBuilder.IconFont,
                ImGui.GetFontSize() * 0.78f,
                origin + new Vector2(size, size) * 0.5f - textSize * 0.39f,
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
            or HomePage.Venues or HomePage.GoLive)
        {
            currentPage = HomePage.Home;
        }

        switch (currentPage)
        {
            case HomePage.Home:
                DrawHome();
                break;
            case HomePage.Player:
                PageTitle("Browse", "Find something to watch.");
                DrawPlayerPage();
                break;
            case HomePage.VideoGrid:
                PageTitle(
                    "Browse Videos",
                    "Discover the latest videos from your topics.");
                DrawVideoGrid();
                break;
            case HomePage.WatchAlong:
                PageTitle("Watch Party", "Host or join a room and watch together.");
                DrawPlayerPage();
                break;
            case HomePage.Screen:
                PageTitle("Screen", "Place the picture in the world.");
                DrawScreenControls();
                break;
            case HomePage.Friends:
                PageTitle("Friends", "People you can invite and join.");
                DrawFriends();
                break;
            case HomePage.Apps:
                PageTitle("Apps", "Extra tools that live alongside the channel.");
                DrawApps();
                break;
            case HomePage.Messages:
                PageTitleBack("Alpha Chat", "Private messages between friends.", HomePage.Apps);
                DrawMessages();
                break;
            case HomePage.PluginHub:
                PageTitleBack("Plugin Hub", "What plugins friends have enabled.", HomePage.Apps);
                myPluginsDirty = true;
                DrawPluginHub();
                break;
            case HomePage.Tweeter:
                PageTitleBack("Tweeter", "Short posts from people you follow.", HomePage.Apps);
                DrawTweeter();
                break;
            case HomePage.Settings:
                PageTitle("Settings", "Account, look, and whispers.");
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
                min + new Vector2(2f, 2f),
                max - new Vector2(2f, 2f),
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
        // Compact brand: accent mark + wordmark (tagline lives on Home).
        var brandOrigin = ImGui.GetCursorScreenPos();
        var sidebarWidth = ImGui.GetContentRegionAvail().X;
        var drawList = ImGui.GetWindowDrawList();
        const float mark = 42f;

        EnsureAlphaIconLoaded();

        var centeredLogo = brandOrigin + new Vector2((sidebarWidth - mark) * 0.5f, 0);

        var alphaWrap = alphaIconImage?.GetWrapOrDefault();

        if (alphaWrap is not null)
        {
            drawList.AddImage(
                alphaWrap.Handle,
                centeredLogo,
                centeredLogo + new Vector2(mark, mark),
                Vector2.Zero,
                Vector2.One,
ImGui.GetColorU32(Vector4.One));
        }

        ImGui.Dummy(new Vector2(0, mark));

        var brandText = "ALPHA CHANNEL";
        var textWidth = ImGui.CalcTextSize(brandText).X;

        ImGui.SetCursorPosX(
            (sidebarWidth - textWidth) * 0.5f);

        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextUnformatted(brandText);
        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0, 14));

        if (CurrentSession is { } sidebarSession && friendsDirty && !friendsLoading)
        {
            RefreshFriends(sidebarSession.Token);
        }

        DrawNavItem(HomePage.Home, FontAwesomeIcon.Home, "Home");
        var playerLabel =
    queue.Entries.Count > 0
        ? $"Player ({queue.Entries.Count})"
        : "Player";

        DrawNavItem(
            HomePage.Player,
            FontAwesomeIcon.Play,
            playerLabel);
        DrawNavItem(HomePage.VideoGrid, FontAwesomeIcon.ThLarge, "Browse Videos");
        DrawNavItem(HomePage.WatchAlong, FontAwesomeIcon.Users, "Watch Party");
        DrawNavItem(HomePage.Screen, FontAwesomeIcon.Desktop, "Screen");
        DrawNavItem(HomePage.Friends, FontAwesomeIcon.UserFriends, "Friends", friendRequests.Incoming.Length);
        var appsActive = currentPage is HomePage.Apps or HomePage.Messages or HomePage.PluginHub
            or HomePage.Tweeter;
        var appsBadge = conversations.Sum(c => c.UnreadCount) + unreadWhisperKeys.Count;
        DrawNavItem(HomePage.Apps, FontAwesomeIcon.ThLarge, "Apps", appsBadge, forceActive: appsActive);
        DrawNavItem(HomePage.Settings, FontAwesomeIcon.Cog, "Settings");

        if (CurrentSession is { } dmSidebarSession
            && currentPage is HomePage.Messages or HomePage.Apps
            && conversationsDirty && !conversationsLoading)
        {
            RefreshConversations(dmSidebarSession.Token);
        }

        // Footer pinned above the content-region bottom. Theme ItemSpacing was eating the
        // version line (Dummy gap + spacing pushed it under the clip), so zero it here and
        // keep a little explicit slack under the version.
        // Rotate the ask ↔ "Donate on Ko-fi" every 30s; height fits the taller copy so the
        // footer doesn't jump when the label flips.
        var donateLabel = DonateLabels[((int)(ImGui.GetTime() / DonateRotateSeconds)) % DonateLabels.Length];
        var footerWidth = MathF.Max(40f, ImGui.GetContentRegionAvail().X);
        var wrapWidth = MathF.Max(40f, footerWidth - 16f);
        var donateH = 40f;
        foreach (var candidate in DonateLabels)
        {
            donateH = MathF.Max(donateH, ImGui.CalcTextSize(candidate, false, wrapWidth).Y + 14f);
        }

        const float footerGap = 8f;
        const float bottomSlack = 10f;
        var versionH = ImGui.GetTextLineHeightWithSpacing();
        var footerH = 300f;

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        {
            var footerStartY = ImGui.GetWindowContentRegionMax().Y - footerH;
            if (footerStartY > ImGui.GetCursorPosY())
            {
                ImGui.SetCursorPosY(footerStartY);
            }

            var footerOrigin = ImGui.GetCursorScreenPos();
            var panelWidth = ImGui.GetContentRegionAvail().X;
            var footerHeight = 160f;

           

            ImGui.Dummy(new Vector2(0, 8));



            DrawSupportLink(
     "♥  Join on Patreon",
     32f,
     PatreonOrange,
     PatreonOrangeHover,
     "https://www.patreon.com/alphachannel");

            ImGui.Dummy(new Vector2(0, 6));

            DrawDonateLink("♥  Donate on Ko-fi", 32f);

            ImGui.Dummy(new Vector2(0, 105));

            DrawSidebarProfile();

            ImGui.Dummy(new Vector2(0, 35));

            DrawVersionFooter();
        }
    }

    private static void DrawNavGroup(string label)
    {
        ImGui.Spacing();
        ImGui.TextColored(MutedText, label);
        ImGui.Dummy(new Vector2(0, 2));
    }

    // forceActive keeps Apps highlighted while you're inside an app (Chat / Hub / Tweeter).
    private void DrawNavItem(HomePage page, FontAwesomeIcon icon, string label, int badgeCount = 0,
        bool forceActive = false)
    {
        var active = forceActive || currentPage == page;
        ImGui.PushID((int)page);

        var rowStart = ImGui.GetCursorScreenPos();
        var rowSize = new Vector2(ImGui.GetContentRegionAvail().X, 38f);
        var drawList = ImGui.GetWindowDrawList();

        var clicked = ImGui.InvisibleButton("##navrow", rowSize);
        var hovered = ImGui.IsItemHovered();

        if (active)
        {
            drawList.AddRectFilled(rowStart, rowStart + rowSize, ImGui.GetColorU32(Accent), 10f);
        }
        else if (hovered)
        {
            drawList.AddRectFilled(rowStart, rowStart + rowSize, ImGui.GetColorU32(CardBgHover), 10f);
        }

        var textColor = active ? Vector4.One : MutedText;
        drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), rowStart + new Vector2(12, 9),
            ImGui.GetColorU32(textColor), icon.ToIconString());using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        drawList.AddText(rowStart + new Vector2(38, 9), ImGui.GetColorU32(textColor), label);

        if (badgeCount > 0)
        {
            var badgeText = badgeCount > 9 ? "9+" : badgeCount.ToString();
            var badgeCenter = rowStart + new Vector2(rowSize.X - 14, rowSize.Y / 2);
            drawList.AddCircleFilled(badgeCenter, 8f, ImGui.GetColorU32(active ? Vector4.One : Danger));
            var textSize = ImGui.CalcTextSize(badgeText);
            drawList.AddText(badgeCenter - textSize / 2,
                ImGui.GetColorU32(active ? Accent : Vector4.One), badgeText);
        }

        ImGui.PopID();

        if (clicked)
        {
            currentPage = page;
            if (page == HomePage.Apps)
            {
                conversationsDirty = true;
            }
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

        var scrollbarX = windowPos.X + windowSize.X - 7f;
        var scrollbarTop = windowPos.Y + 8f;
        var scrollbarBottomClearance = BottomBarHeight + 12f;
        var scrollbarHeight = windowSize.Y - scrollbarBottomClearance - 16f;

        var thumbHeight =
            MathF.Max(
                40f,
                scrollbarHeight * (windowSize.Y / (windowSize.Y + maxScroll)));

        var scrollPercent = scrollY / maxScroll;

        var thumbY =
            scrollbarTop +
            (scrollbarHeight - thumbHeight) * scrollPercent;

        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            new Vector2(scrollbarX, scrollbarTop),
            new Vector2(scrollbarX + 4f, scrollbarTop + scrollbarHeight),
            ImGui.GetColorU32(new Vector4(
    Accent.X,
    Accent.Y,
    Accent.Z,
    0.10f)),
            3f);

        drawList.AddRect(
    new Vector2(scrollbarX - 1f, thumbY - 1f),
    new Vector2(scrollbarX + 7f, thumbY + thumbHeight + 1f),
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
            new Vector2(scrollbarX + 6f, thumbY + thumbHeight),
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
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(onlineFriends > 0 || stream.IsConnected ? Good : MutedText,
                FontAwesomeIcon.Circle.ToIconString());
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
            ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX() + 8f, right - labelWidth));
            ImGui.TextColored(MutedText, label);
        }
    }

    // Ko-fi brand pink — left-nav footer above the version. Alternates ask ↔ CTA every 30s.

    private static readonly Vector4 PatreonOrange = new(1f, 0.55f, 0.15f, 1f);
    private static readonly Vector4 PatreonOrangeHover = new(1f, 0.68f, 0.30f, 1f);
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
        var buttonOrigin = origin + new Vector2(12f, 0);

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

        drawList.AddText(
            textPos,
            ImGui.GetColorU32(hovered ? hoverColor : color),
            label);
    }

    private void DrawSidebarProfile()
    {
        var origin = ImGui.GetCursorScreenPos();

        var session = CurrentSession;

        DrawAvatarChip(
            session?.AvatarIcon,
            session?.AvatarColorHex,
            42,
            session?.AvatarImageUrl);

        ImGui.SetCursorScreenPos(
            origin + new Vector2(55, 8));

        if (!string.IsNullOrEmpty(CurrentCharacterName))
        {
            ImGui.TextUnformatted(CurrentCharacterName);
        }
        else
        {
            ImGui.TextUnformatted("Unknown");
        }

        ImGui.SetCursorScreenPos(
            origin + new Vector2(55, 28));

        ImGui.TextColored(
    Good,
    "● Online");

        ImGui.SetCursorScreenPos(
            origin + new Vector2(55, 48));

        var friendsOnline = friends.Count(f => f.Online);

        ImGui.SetWindowFontScale(0.9f);

        ImGui.TextColored(
            MutedText,
            $"{friendsOnline} friends online");

        ImGui.SetWindowFontScale(1f);
    }

    private void DrawDonateLink(string label, float height)
    {
        var width = ImGui.GetContentRegionAvail().X - 24f;
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        var buttonOrigin = origin + new Vector2(12f, 0);

        ImGui.SetCursorScreenPos(buttonOrigin);


        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
    }

       

    private static string? cachedVersionText;

    private static void DrawVersionFooter()
    {
        cachedVersionText ??= typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "dev";
        var text = $"AlphaChannel v{cachedVersionText}";
        var textWidth = ImGui.CalcTextSize(text).X;
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (avail - textWidth) * 0.5f));
        ImGui.SetWindowFontScale(0.75f);
        ImGui.TextColored(MutedText, text);
        ImGui.SetWindowFontScale(1f);
    }

    // Every non-Home page starts with back + title + a one-line purpose so each Channel reads as
    // its own place, not a clone of every other tab with a different header string.
    private void PageTitle(string text, string purpose) => PageTitleBack(text, purpose, HomePage.Home);

    private void PageTitleBack(string text, string purpose, HomePage backPage)
    {
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.12f))
                   .Push(ImGuiCol.ButtonHovered, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.22f))
                   .Push(ImGuiCol.ButtonActive, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.30f))
                   .Push(ImGuiCol.Text, AccentHover))
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (ImGui.Button($"{FontAwesomeIcon.ArrowLeft.ToIconString()}##backPage", new Vector2(34, 30)))
                {
                    currentPage = backPage;
                }
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(backPage == HomePage.Home ? "Back to Home" : "Back to Apps");
        }

        ImGui.SameLine(0, 12);
        ImGui.BeginGroup();
        ImGui.SetWindowFontScale(1.35f);
        ImGui.TextUnformatted(text);
        ImGui.SetWindowFontScale(1f);
        ImGui.TextColored(MutedText, purpose);
        ImGui.EndGroup();

        ImGui.Dummy(new Vector2(0, 8));
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddRectFilled(origin, origin + new Vector2(width, 1f),
            ImGui.GetColorU32(BorderSubtle));
        ImGui.Dummy(new Vector2(width, 18f));
    }

    // Consistent accent-colored sub-headers within a page — same weight on every Channel.
    private static void SectionHeader(string text)
    {
        ImGui.TextColored(Accent, text);
        ImGui.Dummy(new Vector2(0, 4));
    }

    // Soft panel for content that needs grouping. Height must be >0 — Child size.y=0 means
    // "fill remaining host height" in ImGui, which swallowed the Player search section below.
    private static void DrawCard(string id, Action draw)
    {
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16, 14)))
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
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(20, 18)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 14f))
        using (var stage = ImRaii.Child(id, new Vector2(-1, 1), false,
                   PaddedChild | ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (stage)
            {
                draw();
            }
        }

        var end = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddRectFilled(origin, new Vector2(origin.X + 3f, end.Y),
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

        drawList.AddLine(origin + new Vector2(7, 0), origin + new Vector2(7, height),
            ImGui.GetColorU32(BorderSubtle), 1.5f);
        drawList.AddCircleFilled(origin + new Vector2(7, 12), unread ? 4.5f : 3.5f,
            ImGui.GetColorU32(unread ? Accent : MutedText));

        ImGui.SetCursorScreenPos(origin + new Vector2(22, 4));
        ImGui.PushTextWrapPos(origin.X + 22f + wrapWidth);
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();

        var afterY = ImGui.GetCursorScreenPos().Y;
        ImGui.SetCursorScreenPos(new Vector2(origin.X, MathF.Max(afterY, origin.Y + height) + 2f));
        ImGui.PopID();
    }

    private static void DrawPlainEmpty(string message, string? buttonLabel = null, Action? onClick = null)
    {
        ImGui.Dummy(new Vector2(0, 8));
        ImGui.TextColored(MutedText, message);
        if (buttonLabel is not null && onClick is not null)
        {
            ImGui.Spacing();
            if (ImGui.Button(buttonLabel, new Vector2(160, 30)))
            {
                onClick();
            }
        }

        ImGui.Dummy(new Vector2(0, 8));
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
        if (namePromptPending)
        {
            ImGui.OpenPopup("Choose your name");
            namePromptPending = false;
        }

        ImGui.SetNextWindowSize(new Vector2(320, 0));
        if (ImGui.BeginPopupModal("Choose your name", ImGuiWindowFlags.NoResize))
        {
            ImGui.TextWrapped("Pick the name other viewers will see for you.");
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText("##displayName", ref namePromptInput, 32);
            if (ImGui.Button("Confirm") && namePromptInput.Trim().Length > 0)
            {
                onNameConfirmed?.Invoke(namePromptInput.Trim());
                onNameConfirmed = null;
                namePromptActive = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void DrawRoster(string label, bool allowPromote)
    {
        // ---------------------------------------------------------
        // TEMPORARY UI PREVIEW
        // ---------------------------------------------------------

        const bool showPreviewViewer = true;

        var realCount =
            stream.Roster.Length;

        var displayCount =
            realCount +
            (showPreviewViewer ? 1 : 0);

        var openParen =
            label.LastIndexOf('(');

        var displayLabel =
            openParen >= 0
                ? $"{label[..openParen]}({displayCount})"
                : label;

        ImGui.SetWindowFontScale(1.08f);

        ImGui.TextColored(
            Vector4.One,
            displayLabel);

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 8f));

        if (realCount == 0 &&
            !showPreviewViewer)
        {
            ImGui.TextColored(
                MutedText,
                "Waiting for people to join...");

            return;
        }

        if (realCount > 0)
        {
            DrawAvatarStack(
                stream.Roster,
                maxShown: 12);

            ImGui.Dummy(
                new Vector2(0f, 10f));
        }

        // ---------------------------------------------------------
        // Real participants
        // ---------------------------------------------------------

        for (var index = 0;
             index < realCount;
             index++)
        {
            var participant =
                stream.Roster[index];

            ImGui.PushID(index);

            DrawPartyRosterRow(
                participant.DisplayName,
                allowPromote,
                canUseActions: true,
                onPromote: () =>
                    _ = stream.TransferHostAsync(
                        participant.UserId));

            ImGui.PopID();

            ImGui.Dummy(
                new Vector2(0f, 6f));
        }

        // ---------------------------------------------------------
        // Temporary example viewer
        // ---------------------------------------------------------

        if (showPreviewViewer)
        {
            ImGui.PushID(
                "##previewPartyViewer");

            DrawPartyRosterRow(
                "Example Viewer",
                allowPromote,
                canUseActions: false,
                onPromote: null);

            ImGui.PopID();

            ImGui.Dummy(
                new Vector2(0f, 6f));
        }
    }

    private void DrawPartyRosterRow(
    string displayName,
    bool allowPromote,
    bool canUseActions,
    Action? onPromote)
    {
        const float rowHeight = 52f;

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
                    new Vector2(18f, 26f),
                    4f,
                    ImGui.GetColorU32(Good));

            // Name.
            ImGui.GetWindowDrawList()
                .AddText(
                    origin +
                    new Vector2(32f, 18f),
                    ImGui.GetColorU32(
                        Vector4.One),
                    displayName);

            if (!allowPromote)
            {
                return;
            }

            const float rightPadding = 12f;
            const float gap = 8f;

            var kickSize =
                new Vector2(112f, 30f);

            var hostSize =
                new Vector2(104f, 30f);

            // -----------------------------------------------------
            // Kick from room — UI only for now
            // -----------------------------------------------------

            var kickPos =
                new Vector2(
                    origin.X +
                    rowWidth -
                    rightPadding -
                    kickSize.X,
                    origin.Y + 11f);

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
        PersistPositions();
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

        public ThemeScope()
        {
            ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBg);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, WindowBg);
            ImGui.PushStyleColor(ImGuiCol.PopupBg, CardBg);
            ImGui.PushStyleColor(ImGuiCol.Button, Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActive);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, FrameBg);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, FrameBgHover);
            ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent); var footerH = 190f;
            ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, AccentActive);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 12f);
            ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 12f);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(12, 10));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12, 8));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(8, 6));
        }

        public void Dispose()
        {
            ImGui.PopStyleVar(StyleCount);
            ImGui.PopStyleColor(ColorCount);
        }
    }
}
