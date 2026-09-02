using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AlphaChannel.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider InteropProvider { get; private set; } = null!;
    [PluginService] internal static INamePlateGui NamePlateGui { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IGamepadState GamepadState { get; private set; } = null!;

    internal static Configuration Cfg { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("AlphaChannel");
    private readonly ScreenController screenController;
    private readonly VideoPlayer video;
    private readonly AetherStreamQueue queue;
    private readonly StreamClient stream;
    private readonly MainWindow mainWindow;
    private readonly AuthClient authClient;
    private readonly SignInFlow signInFlow;
    private readonly FriendsClient friendsClient;
    private readonly ActivityClient activityClient;
    private readonly DmClient dmClient;
    private readonly ReportClient reportClient;
    private readonly TweeterClient tweeterClient;
    private readonly PluginHubClient pluginHubClient;
    private readonly VenuesClient venuesClient;
    private readonly LiveClient liveClient;
    private readonly TwitchClient twitchClient;
    private readonly KeysClient keysClient;
    private readonly AlphaChannel.Plugin.Crypto.KeyVault keyVault;
    private readonly Whispers.WhisperMirror whisperMirror;
    private readonly NearbyAutoWatch nearbyAutoWatch;
    private ulong lastWhisperContentId = ulong.MaxValue;

    // TEMP: allows us to test the username UI even when a username already exists.
    private bool devUsernamePromptShown;

    // Written from the network thread (OnRemoteState), read/cleared on the main thread
    // (OnFrameworkUpdate) - a plain reference field is fine here, a single pointer swap is already
    // atomic in .NET and only the latest state ever matters, no torn reads to guard against.
    private volatile AlphaChannel.Contracts.StreamControl? pendingRemoteState;
    private string? lastReceivedRemoteUrl;

    // True only when WE paused playback because of combat/a cutscene, not when the host paused it
    // manually - otherwise leaving combat would un-pause a video the host deliberately stopped.
    private bool autoPaused;

    private double lastRecentlyWatchedSave;

    // How far a viewer's local position can drift from the host's reported position before it's
    // worth a corrective seek. Below this, natural playback + network jitter accounts for the gap.
    private const float SyncToleranceSeconds = 2.5f;

    // The URL last handed to video.Play() as a viewer. VideoEngine.PlayVideo's own guard against
    // redundant reloads only kicks in once its internal mpv instance actually exists - for a fresh
    // video that can take up to several seconds (a YouTube rate-limit cooldown runs before the
    // instance is created), and the host publishes state every tick with no diff-check, so without
    // this a viewer could fire off dozens of concurrent PlayVideo calls for the same URL before the
    // first one ever finishes initializing - exactly what looked like a permanently frozen screen.
    private string? lastAppliedRemoteUrl;
    private bool waitingForMedia;

    private bool screenRangePaused;
    private bool screenRangeWarningShown;

    private const float HostScreenWarnDistance = 35f;
    private const float HostScreenPauseDistance = 45f;

    // Latest state received from the host. Kept even while this viewer has
    // chosen not to spawn their local TV.
    private AlphaChannel.Contracts.StreamControl? latestRemoteState;

    // Same accent color as MainWindow's theme, duplicated here rather than shared since this is
    // the only in-world reaction color used (no per-icon color mapping for v1 - see
    // MainWindow.Reactions.cs's own note on why the buttons only send a glyph, not a color).
    private static readonly (float R, float G, float B) ReactionColor = (0.55f, 0.60f, 1.0f);
    private static readonly TimeSpan ReactionLifetime = TimeSpan.FromSeconds(3);
    private readonly List<InWorldReaction> activeReactions = new();

    public string Name => "AlphaChannel";

    public Plugin()
    {
        Cfg = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Cfg.Initialize(PluginInterface);
        // Old builds defaulted AutoWatchNearby to true; that scan wiped YouTube typing by
        // flipping Player tabs / joining nearby names. Force off until the feature is re-enabled.
        if (Cfg.AutoWatchNearby)
        {
            Cfg.AutoWatchNearby = false;
            Cfg.Save();
        }

        // VideoEngine's own constructor calls DxHandler.Initialise, matching the original
        // Aetherphone ordering - no separate call needed here.
        screenController = new ScreenController(() => true);
        video = new VideoPlayer(screenController.Engine);
        video.SetVolume(Cfg.Muted ? 0 : Cfg.Volume);
        video.CookiesPath = Cfg.YouTubeCookiesPath;
        video.UseFirefoxCookies = Cfg.UseFirefoxCookies;
        var activeProfile =
    Cfg.SavedQueueProfiles[Cfg.ActiveQueueSlot];

        queue = new AetherStreamQueue(
            video,
            activeProfile?.Entries ?? Enumerable.Empty<VideoQueueRecord>());
        stream = new StreamClient(Cfg, () => Cfg.CharacterDisplayNames.GetValueOrDefault(ReadLocalContentId()),
            () => Cfg.CharacterSessions.GetValueOrDefault(ReadLocalContentId()));
        stream.OnState += OnRemoteState;
        stream.OnRenameRequired += OnRenameRequired;
        stream.Start();

        authClient = new AuthClient(Cfg);
        signInFlow = new SignInFlow(authClient);
        friendsClient = new FriendsClient(Cfg);
        activityClient = new ActivityClient(Cfg);
        dmClient = new DmClient(Cfg);
        reportClient = new ReportClient(Cfg);
        tweeterClient = new TweeterClient(Cfg);
        pluginHubClient = new PluginHubClient(Cfg);
        venuesClient = new VenuesClient(Cfg);
        liveClient = new LiveClient(Cfg);
        twitchClient = new TwitchClient(Cfg);
        keysClient = new KeysClient(Cfg);
        keyVault = new AlphaChannel.Plugin.Crypto.KeyVault(Cfg, keysClient);
        whisperMirror = new Whispers.WhisperMirror(Cfg, PluginInterface.ConfigDirectory.FullName);

        mainWindow = new MainWindow(screenController, video, queue, stream, RequestRename, authClient, signInFlow,
            friendsClient, activityClient, dmClient, reportClient, tweeterClient, pluginHubClient, venuesClient, liveClient,
            twitchClient, keyVault, whisperMirror, UpdateSessionForCurrentCharacter);

        mainWindow.OnViewerTvSpawnRequested = SpawnViewerTv;

        nearbyAutoWatch = new NearbyAutoWatch(stream, mainWindow);
        windowSystem.AddWindow(mainWindow);

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        ContextMenu.OnMenuOpened += OnMenuOpened;
        CommandManager.AddHandler("/alpha", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Alpha Channel. /alpha watch <name> | leave | stage.",
        });
        CommandManager.AddHandler("/wp", new CommandInfo(OnWatchPartyChatCommand)
        {
            HelpMessage = "Send a message to your current Alpha Channel watch party.",
        });
    }

    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Trim();
        if (args.Length == 0)
        {
            ToggleMainWindow();
            return;
        }

        var space = args.IndexOf(' ');
        var verb = (space < 0 ? args : args[..space]).ToLowerInvariant();
        var rest = space < 0 ? string.Empty : args[(space + 1)..].Trim();

        switch (verb)
        {
            case "watch":
                if (rest.Length == 0)
                {
                    ChatGui.Print("Usage: /alpha watch <host name>");
                    return;
                }

                // Viewer mode needs AlphaChannel installed — ScreenPainter draws locally from /rt.
                queue.Clear();
                mainWindow.OpenViewerAndJoin(rest);
                ChatGui.Print($"[AlphaChannel] Joining {rest}… Expand the capsule for the full UI.");
                break;

            case "leave":
                mainWindow.LeaveStream();
                ChatGui.Print("[AlphaChannel] Left the stream.");
                break;

            case "stage":
                // Convenience dance for stage presence. Penumbra VFX screen pack is parked for later.
                try
                {
                    SendChatCommand("/dance");
                }
                catch (Exception exception)
                {
                    ChatGui.Print($"[AlphaChannel] Couldn't run /dance: {exception.Message}");
                }

                break;

            case "snes":
                {
                    var romPath =
                        rest.Trim().Trim('"');

                    if (romPath.Length == 0)
                    {
                        ChatGui.Print(
                            "Usage: /alpha snes <full path to .sfc/.smc ROM>");

                        return;
                    }

                    bool started =
                        screenController.Engine.PlaySnes(
                            romPath);

                    if (!started)
                    {
                        ChatGui.Print(
                            $"[AlphaChannel] SNES failed: {screenController.Engine.LastError ?? "Unknown error"}");
                    }
                    else
                    {
                        ChatGui.Print(
                            "[AlphaChannel] SNES game started.");
                    }

                    break;
                }

            case "snes-stop":
                {
                    screenController.Engine.StopVideo();

                    ChatGui.Print(
                        "[AlphaChannel] SNES game stopped.");

                    break;
                }

            default:
                ToggleMainWindow();
                break;
        }
    }

    private void OnWatchPartyChatCommand(
    string command,
    string arguments)
    {
        var message =
            arguments.Trim();

        if (stream.Mode is not (StreamMode.Hosting or StreamMode.Viewing))
        {
            ChatGui.Print(
                "[AlphaChannel] You are not currently in a watch party.");
            return;
        }

        if (message.Length == 0)
        {
            ChatGui.Print(
                "Usage: /wp <message>");
            return;
        }

        _ = stream.SendChatAsync(
            message);
    }

    private static unsafe void SendChatCommand(string command)
    {
        var utf8 = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.FromString(command);
        try
        {
            FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance()->ProcessChatBoxEntry(utf8);
        }
        finally
        {
            utf8->Dtor(true);
        }
    }

    // Right-click a player -> Join Stream as AlphaChannel viewer (capsule + ScreenPainter). Works
    // whenever that player kept the name the first-connect prompt suggests by default (their real
    // character name) - same name-matching the manual "Host's name" field in the window already
    // relies on, this is just a shortcut that skips typing it. Both players need AlphaChannel.
    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetDefault { TargetName.Length: > 0 } target)
        {
            return;
        }

        args.AddMenuItem(new MenuItem
        {
            Name = "Join Stream",
            PrefixChar = 'A',
            PrefixColor = 588,
            OnClicked = _ =>
            {
                queue.Clear();
                mainWindow.OpenViewerAndJoin(target.TargetName);
            },
        });

        // "Make it easier for people to find one another": resolves by the target's actual FFXIV
        // character identity (name+world), not a chosen name anyone has to know/type - if you can
        // see them, you can add them. Needs both a signed-in caller and a resolvable world (cross-
        // world/instanced targets don't always carry one - see the try/catch below, same defensive
        // pattern WhisperMirror.cs uses for the same RowRef<World> API).
        if (mainWindow.CurrentSession is { } session)
        {
            var characterName = target.TargetName;
            string? world = null;
            try
            {
                world = target.TargetHomeWorld.IsValid ? target.TargetHomeWorld.Value.Name.ToString() : null;
            }
            catch (Exception exception)
            {
                AepLog.Warning($"[Friends] couldn't resolve target world: {exception.Message}");
            }

            if (world is { Length: > 0 })
            {
                args.AddMenuItem(new MenuItem
                {
                    Name = "Add AlphaChannel Friend",
                    PrefixChar = 'A',
                    PrefixColor = 588,
                    OnClicked = clickedArgs =>
                    {
                        _ = Task.Run(async () =>
                        {
                            var ok = await friendsClient.SendRequestByCharacterAsync(session.Token, characterName, world);
                            mainWindow.HandleAddFriendByCharacterResult(ok, characterName);
                        });
                    },
                });
            }
        }
    }

    private void ToggleMainWindow()
    {
        if (mainWindow.IsOpen)
        {
            mainWindow.CloseUi();
            return;
        }

        mainWindow.OpenUi();
    }

    private void ApplyHostScreenRangePause()
    {
        // Viewer playback is handled separately.
        if (stream.Mode != StreamMode.Hosting)
        {
            screenRangePaused = false;
            screenRangeWarningShown = false;
            return;
        }

        // Nothing currently playing locally.
        if (queue.Current is null || !screenController.Engine.IsActive)
        {
            screenRangePaused = false;
            screenRangeWarningShown = false;
            return;
        }

        var localPlayer = ObjectTable.LocalPlayer;

        // LocalPlayer commonly disappears briefly during zoning/teleport.
        // Treat that the same as leaving the TV's usable area.
        if (localPlayer is null)
        {
            if (!screenRangePaused)
            {
                ChatGui.Print(
                    "[AlphaChannel] Playback paused because you're no longer near the TV.");

                AepLog.Info(
                    "[WatchParty] Host left TV area; pausing playback.");

                video.Pause(true);
                screenRangePaused = true;
            }

            return;
        }

        var distance = Vector3.Distance(
            localPlayer.Position,
            screenController.Engine.ScreenPosition);

        // One warning while approaching the cutoff.
        if (distance > HostScreenWarnDistance &&
            distance <= HostScreenPauseDistance &&
            !screenRangeWarningShown)
        {
            ChatGui.Print(
                "[AlphaChannel] You're getting too far from the TV. Move closer or playback will pause.");

            screenRangeWarningShown = true;
        }

        // Hard cutoff.
        if (distance > HostScreenPauseDistance)
        {
            if (!screenRangePaused)
            {
                ChatGui.Print(
                    "[AlphaChannel] Playback paused because you're too far from the TV.");

                AepLog.Info(
                    $"[WatchParty] Host is {distance:F1} yalms from TV; pausing playback.");

                video.Pause(true);
                screenRangePaused = true;
            }

            return;
        }

        // Back inside the safe zone. Allow a future warning/pause cycle,
        // but deliberately do NOT resume playback automatically.
        if (distance <= HostScreenWarnDistance)
        {
            screenRangeWarningShown = false;
            screenRangePaused = false;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        screenController.OnFrameworkUpdate();
        queue.OnFrameworkUpdate();
        EnsureCharacterHasName();
        var contentId = ReadLocalContentId();
        if (contentId != lastWhisperContentId)
        {
            lastWhisperContentId = contentId;
            whisperMirror.SetCharacter(contentId);
            mainWindow.ResetWhisperUi();
        }

        mainWindow.CurrentDisplayName = Cfg.CharacterDisplayNames.GetValueOrDefault(contentId);
        mainWindow.CurrentSession = Cfg.CharacterSessions.GetValueOrDefault(contentId);
        mainWindow.CurrentCharacterName = ObjectTable.LocalPlayer?.Name.TextValue;
        mainWindow.CurrentWorldName = ReadLocalWorldName();
        mainWindow.CurrentIsLalafell = ReadIsLalafell();

        if (pendingRemoteState is { } remoteState)
        {
            pendingRemoteState = null;
            ApplyRemoteState(remoteState);
        }

        if (mainWindow.ViewerTvEnabled &&
            waitingForMedia &&
            !screenController.Engine.IsActive)
        {
            AepLog.Warning("[WatchParty] Attempting waiting screen spawn");
            video.ShowWaitingScreen();
        }

        ApplyAutoPause();
        ApplyHostScreenRangePause();

        UpdateRecentlyWatched();

        UpdateReactions();

        nearbyAutoWatch.OnFrameworkUpdate();

        // Hosting: push the local queue's current state out to the relay every tick it changes
        // meaningfully - PublishStateAsync itself is cheap to call repeatedly (a JSON send), the
        // server is what dedupes/broadcasts, so no local diff-check is needed for a v1.
        // Mode != Viewing, not Mode == Hosting: PublishStateAsync is what SETS Mode to Hosting in
        // the first place, so gating on it already being Hosting is a deadlock - a fresh host
        // (Mode.None) would never publish, never become Hosting, and nobody could ever join them.
        // Mode != Viewing still correctly blocks a host who was just transferred away (Mode flips
        // to Viewing) from continuing to publish their own stale local queue state.
        var current = queue.Current;

        if (stream.Mode != StreamMode.Viewing &&
            current is not null &&
            screenController.Engine.IsActive)
        {
            var (position, _, paused) = video.GetProgress();

            _ = stream.PublishStateAsync(
                current.Url,
                position,
                paused,
                screenController.Engine.ScreenPosition,
                screenController.Engine.ScreenYaw,
                screenController.Engine.ScreenScale);

            video.SetOverlayTitle(
                current.Title,
                current.Source);
        }
    }

    internal void UpdateRecentlyWatched()
    {
        if (queue.Current is not { } current)
        {
            return;
        }

        var now = ImGui.GetTime();

        if (now - lastRecentlyWatchedSave < 15)
        {
            return;
        }

        lastRecentlyWatchedSave = now;

        var (position, duration, _) = video.GetProgress();

        mainWindow.UpdateRecentlyWatched(
            current,
            position,
            duration);
    }

    // Only touches playback while actually hosting - a viewer's playback is driven entirely by the
    // host's own stream.state pushes, auto-pausing it locally too would just fight that.
    private void ApplyAutoPause()
    {
        if (stream.Mode != StreamMode.Hosting)
        {
            autoPaused = false;
            return;
        }

        var shouldPause = Condition[ConditionFlag.InCombat] || Condition[ConditionFlag.WatchingCutscene] ||
            Condition[ConditionFlag.WatchingCutscene78];
        var isPaused = video.GetProgress().Paused;

        if (shouldPause && !isPaused)
        {
            video.Pause(true);
            autoPaused = true;
        }
        else if (!shouldPause && autoPaused)
        {
            video.Pause(false);
            autoPaused = false;
        }
    }

    // Drains stream.IncomingReactions (the sole consumer - MainWindow's reaction buttons only
    // send, they don't also drain, since a ConcurrentQueue only lets one consumer actually get
    // each item) and pushes the current animated particle set to the in-world screen every tick.
    // Spawns near the bottom of the screen (uv.y close to 1, just above the title banner's own
    // band) and rises toward the top over ReactionLifetime, matching the GUI's earlier "fly up"
    // behavior but rendered on the actual video screen instead.
    private void UpdateReactions()
    {
        while (stream.IncomingReactions.TryDequeue(
                   out var incomingReaction))
        {
            // Existing in-world reaction.
            activeReactions.Add(
                new InWorldReaction(
                    DateTime.UtcNow,
                    (float)(
                        reactionRandom.NextDouble() *
                        0.3 -
                        0.15)));

// Mirror the same received reaction into the
// chronological Watch Party activity feed.
//
// Preserve the authenticated Alpha Channel account ID
// so the feed can resolve the sender's avatar locally.
mainWindow.AddPartyReactionToFeed(
    incomingReaction.UserId,
    incomingReaction.DisplayName,
    incomingReaction.Glyph);
        }

        activeReactions.RemoveAll(
            reaction =>
                DateTime.UtcNow -
                reaction.SpawnedAt >=
                ReactionLifetime);

        var particles =
            new List<ReactionParticle>(
                activeReactions.Count);

        var now =
            DateTime.UtcNow;

        foreach (var reaction in activeReactions)
        {
            var progress =
                Math.Clamp(
                    (float)(
                        now -
                        reaction.SpawnedAt)
                    .TotalSeconds /
                    (float)
                    ReactionLifetime.TotalSeconds,
                    0f,
                    1f);

            var x =
                Math.Clamp(
                    0.5f +
                    reaction.XJitter,
                    0.05f,
                    0.95f);

            var y =
                0.85f -
                progress *
                0.7f;

            var alpha =
                1f -
                progress;

            particles.Add(
                new ReactionParticle(
                    x,
                    y,
                    alpha,
                    0.05f,
                    ReactionColor.R,
                    ReactionColor.G,
                    ReactionColor.B));
        }

        video.SetReactions(
            particles);
    }

    private readonly Random reactionRandom = new();

    private readonly record struct InWorldReaction(DateTime SpawnedAt, float XJitter);

    // Runs every tick (cheap dictionary lookup) rather than once at startup because LocalContentId
    // is 0 until the player is actually logged into a character - a dev plugin can load at the
    // title screen, well before that's known.
    private void EnsureCharacterHasName()
    {
        var contentId = ReadLocalContentId();

        if (contentId == 0 ||
            mainWindow.IsNamePromptActive)
        {
            return;
        }

        // TEMP DEVELOPMENT:
        // Force the username prompt once per plugin session,
        // but only after the user has opened Alpha Channel themselves.
        if (!devUsernamePromptShown &&
            mainWindow.IsOpen)
        {
            devUsernamePromptShown = true;

            var suggested =
                Cfg.CharacterDisplayNames.GetValueOrDefault(contentId) ??
                ObjectTable.LocalPlayer?.Name.TextValue ??
                "Player";

            PromptForName(
                contentId,
                suggested);

            return;
        }

        // Existing normal behaviour.
        if (Cfg.CharacterDisplayNames.ContainsKey(contentId))
        {
            return;
        }

        // Don't force the whole plugin UI open just because the
        // character still needs a username. Wait until they open it.
        if (!mainWindow.IsOpen)
        {
            return;
        }

        PromptForName(
            contentId,
            ObjectTable.LocalPlayer?.Name.TextValue ?? "Player");
    }

    // Manually triggered from MainWindow's "Rename" button - same flow as the automatic
    // first-connect prompt above, just invocable any time instead of only once per character.
    private void RequestRename()
    {
        var contentId = ReadLocalContentId();
        if (contentId == 0 || mainWindow.IsNamePromptActive)
        {
            return;
        }

        var suggested = Cfg.CharacterDisplayNames.GetValueOrDefault(contentId) ??
            ObjectTable.LocalPlayer?.Name.TextValue ?? "Player";
        PromptForName(contentId, suggested);
    }

    private void PromptForName(ulong contentId, string suggested)
    {
        mainWindow.RequestNamePrompt(suggested, name =>
        {
            Cfg.CharacterDisplayNames[contentId] = name;
            Cfg.Save();
            _ = stream.SendHelloAsync(name);
        });
    }

    // An admin cleared this player's name server-side (see AlphaChannel.Server's
    // /admin/reset-username) - drop the local record too so EnsureCharacterHasName re-prompts them
    // on the very next tick, same code path as the first-connect flow.
    private void OnRenameRequired()
    {
        var contentId = ReadLocalContentId();
        if (contentId != 0)
        {
            Cfg.CharacterDisplayNames.Remove(contentId);
            Cfg.Save();
        }
    }

    // stream.OnState fires from StreamClient's WebSocket receive loop - a background thread, not
    // the game's main thread. video.Play and the screen transform both touch main-thread-only game
    // state (this is exactly what threw "Not on main thread!" when applied here directly), so this
    // just records the latest message and OnFrameworkUpdate applies it on the next tick instead.
    private void OnRemoteState(AlphaChannel.Contracts.StreamControl message)
    {
        if (message.Url == lastReceivedRemoteUrl)
        {
            pendingRemoteState = message;
            return;
        }

        lastReceivedRemoteUrl = message.Url;
        pendingRemoteState = message;
    }

    private void SpawnViewerTv()
    {
        if (stream.Mode != StreamMode.Viewing)
        {
            return;
        }

        var state = latestRemoteState;

        if (state is null || string.IsNullOrEmpty(state.Url))
        {
            waitingForMedia = true;
            video.ShowWaitingScreen();
            return;
        }

        ApplyRemoteState(state);
    }

    // Viewer path (including /achannel watch): apply URL/position/pause + screen transform to this
    // client's local ScreenPainter. Relay /rt only — not Penumbra — so anyone without AlphaChannel
    // (e.g. Lightless-only) cannot see the screen.
    private void ApplyRemoteState(AlphaChannel.Contracts.StreamControl message)
    {
        if (stream.Mode != StreamMode.Viewing)
        {
            AepLog.Warning($"[WatchParty] Ignoring state because mode is {stream.Mode}");
            return;
        }

        // Always remember the host's latest state, even when this viewer has
        // chosen not to spawn their local TV.
        latestRemoteState = message;

        // Joining the watch party no longer automatically starts local
        // playback or creates a screen.
        if (!mainWindow.ViewerTvEnabled)
        {
            return;
        }

        if (string.IsNullOrEmpty(message.Url))
        {
            AepLog.Warning("[WatchParty] Received empty media state");
            waitingForMedia = true;
            return;
        }

        waitingForMedia = false;

        var url = message.Url;

        // Also re-trigger whenever the local player is genuinely idle (e.g. right after Join's own
        // queue.Clear()/video.Stop()) even if the URL happens to match the last one applied -
        // otherwise rejoining the same still-playing host would never actually restart playback.
        if (url != lastAppliedRemoteUrl || video.State == VideoPlaybackState.Idle)
        {
            lastAppliedRemoteUrl = url;
            video.Play(url);
        }

        bool isLiveHls =
     url.Contains(
         ":8888/live/",
         StringComparison.OrdinalIgnoreCase)
     &&
     url.EndsWith(
         "/index.m3u8",
         StringComparison.OrdinalIgnoreCase);

        if (!isLiveHls)
        {
            if (message.PositionSeconds is double remotePosition)
            {
                var localPosition =
                    video.GetProgress().Position;

                if (MathF.Abs(
                        localPosition -
                        (float)remotePosition) >
                    SyncToleranceSeconds)
                {
                    video.Seek(
                        (float)remotePosition);
                }
            }
        }

        video.Pause(
            message.Paused ?? false);
        video.SetOverlayTitle(url, string.Empty);

        if (message.ScreenX is { } x && message.ScreenY is { } y && message.ScreenZ is { } z &&
            message.ScreenYaw is { } yaw && message.ScreenScale is { } scale)
        {
            screenController.Engine.ApplyRemoteScreenTransform(new Vector3(x, y, z), yaw, scale);
        }
    }

    private static unsafe ulong ReadLocalContentId()
    {
        var state = PlayerState.Instance();
        return state is null ? 0 : state->ContentId;
    }

    private static string? ReadLocalWorldName() => ObjectTable.LocalPlayer?.HomeWorld.Value.Name.ToString();

    // Race 3 = Lalafell, per the same customize-byte-array lookup Aetherphone's Velvet feature
    // already uses (Apps/Velvet/VelvetShell.cs's IsLalafellCharacter) for its own Lalafell-specific
    // access gating - kept as a private const here rather than an enum since this is the only place
    // AlphaChannel needs it.
    private const byte LalafellRaceId = 3;

    private static bool ReadIsLalafell()
    {
        var local = ObjectTable.LocalPlayer;
        if (local is null)
        {
            return false;
        }

        var customize = local.Customize;
        var raceIndex = (int)CustomizeIndex.Race;
        return customize.Length > raceIndex && customize[raceIndex] == LalafellRaceId;
    }

    // Writes (or, for sign-out, removes) the CharacterSession for whichever character is currently
    // being played - the one piece of persistence MainWindow's sign-in UI isn't allowed to do
    // itself, same split as PromptForName above.
    private void UpdateSessionForCurrentCharacter(CharacterSession? session)
    {
        var contentId = ReadLocalContentId();
        if (contentId == 0)
        {
            return;
        }

        if (session is null)
        {
            Cfg.CharacterSessions.Remove(contentId);
        }
        else
        {
            Cfg.CharacterSessions[contentId] = session;
        }

        Cfg.Save();
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler("/alpha");
        CommandManager.RemoveHandler("/wp");
        ContextMenu.OnMenuOpened -= OnMenuOpened;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        Framework.Update -= OnFrameworkUpdate;

        mainWindow.Dispose();
        nearbyAutoWatch.Dispose();
        whisperMirror.Dispose();
        stream.Dispose();
        screenController.Dispose();
        DxHandler.Dispose();
    }
}
