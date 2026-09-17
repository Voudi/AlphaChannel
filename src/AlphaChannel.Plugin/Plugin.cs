using System.Collections.Concurrent;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.Net;
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
    //
    // TEMPORARY TESTING SWITCH
    //
    // When true, the username/topic onboarding and first-launch splash
    // are shown once on every plugin load. Saved usernames, topics and
    // caches are retained.
    //
    internal const bool ForceFirstLaunchExperienceForTesting =
        false;

    private bool forcedFirstLaunchPromptRequested;

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!; [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
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
    private readonly MiniPlayerWindow miniPlayerWindow;
    private readonly MiniChatWindow miniChatWindow;
    private readonly AuthClient authClient;
    private readonly SignInFlow signInFlow;
    private readonly FriendsClient friendsClient;
    private readonly ActivityClient activityClient;
    private readonly DmClient dmClient;
    private readonly ReportClient reportClient;
    private readonly VenuesClient venuesClient;
    private readonly LiveClient liveClient;
    private readonly RoomsClient roomsClient;
    private readonly RadioClient radioClient;
    private readonly TwitchClient twitchClient;
    private readonly KeysClient keysClient;
    private readonly AlphaChannel.Plugin.Crypto.KeyVault keyVault;
    private readonly NearbyAutoWatch nearbyAutoWatch;
    private readonly ConcurrentQueue<Action> frameworkActions = new();
    private readonly CancellationTokenSource sessionValidationLifetime = new();
    private Task? sessionValidationTask;
    private int disposeStarted;
    private ulong observedSessionContentId;
    private ulong validatedSessionContentId;
    private ulong validatingSessionContentId;
    private DateTime nextSessionValidationAttemptUtc = DateTime.MinValue;

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
    private bool loggedEmptyRemoteState;

    // MediaMTX can briefly return 404 while a new RTMP publisher creates
    // its HLS manifest. Retry live URLs without launching a new renderer
    // every framework update.
    private DateTime nextLiveHlsRetryUtc =
        DateTime.MinValue;

    private static readonly TimeSpan LiveHlsRetryDelay =
        TimeSpan.FromSeconds(
            2);

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
        Cfg = ConfigurationMigration.Load(
            PluginInterface,
            out var configurationWasReset);

        if (configurationWasReset)
        {
            ChatGui.Print(
                "Your Alpha Channel config file has been reset to allow compatibility with a newer version and migration system. This will only happen once");
        }

#if !DEBUG
        // Public builds always use the authenticated production endpoint. This
        // also repairs configurations retained after running a Debug build.
        if (!string.Equals(
                Cfg.RelayServerUrl?.TrimEnd('/'),
                Configuration.ProductionRelayServerUrl,
                StringComparison.OrdinalIgnoreCase) ||
            Cfg.ShowServerStackSwitcher)
        {
            Cfg.RelayServerUrl = Configuration.ProductionRelayServerUrl;
            Cfg.ShowServerStackSwitcher = false;
            Cfg.Save();
        }
#endif

        // Old builds defaulted AutoWatchNearby to true; that scan wiped YouTube typing by
        // flipping Player tabs / joining nearby names. Force off until the feature is re-enabled.
        if (Cfg.AutoWatchNearby)
        {
            Cfg.AutoWatchNearby = false;
            Cfg.Save();
        }

        // VideoEngine initializes DxHandler in its constructor; no separate call is needed here.
        screenController = new ScreenController(() => true);
        video = new VideoPlayer(screenController.Engine);
        video.SetVolume(Cfg.Muted ? 0 : Cfg.Volume);

        video.CookiesPath =
            YouTubeEmbeddedBrowserSession.IsConnected(Cfg)
                ? YouTubeEmbeddedBrowserSession.CookieFilePath
                : null;

        //
        // Repair/migrate saved queue slots before reading ActiveQueueSlot.
        //
        QueueManager.NormalizeConfiguration(
            Cfg);

        var activeProfile =
            Cfg.SavedQueueProfiles[
                Cfg.ActiveQueueSlot];

        queue =
            new AetherStreamQueue(
                video,
                activeProfile?.Entries ??
                Enumerable.Empty<VideoQueueRecord>());
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
        venuesClient = new VenuesClient(Cfg);
        liveClient = new LiveClient(Cfg);
        roomsClient = new RoomsClient(Cfg);
        radioClient = new RadioClient(Cfg);
        twitchClient = new TwitchClient(Cfg);
        keysClient = new KeysClient(Cfg);
        keyVault = new AlphaChannel.Plugin.Crypto.KeyVault(Cfg, keysClient);
        mainWindow = new MainWindow(screenController, video, queue, stream, RequestRename, authClient, signInFlow,
            friendsClient, activityClient, dmClient, reportClient, venuesClient, liveClient,
            roomsClient, radioClient,
            twitchClient, keyVault, UpdateSessionForCurrentCharacter);

        mainWindow.OnViewerTvSpawnRequested = SpawnViewerTv;

        miniPlayerWindow = new MiniPlayerWindow(
            video,
            screenController.Engine,
            stream,
            () => mainWindow.GetMiniModeTitle(),
            OpenMiniChat,
            () => mainWindow.OpenUi());

        miniChatWindow = new MiniChatWindow(
            stream,
            () => mainWindow.GetMiniPartyTitle(),
            mainWindow.DrawMiniPartyChatContents,
            OpenMiniPlayer,
            mainWindow.OpenFullWatchPartyChat);

        mainWindow.OnMiniPlayerRequested = OpenMiniPlayer;
        mainWindow.OnMiniChatRequested = OpenMiniChat;
        mainWindow.IsMiniPlayerOpen = () => miniPlayerWindow.IsOpen;

        nearbyAutoWatch = new NearbyAutoWatch(stream, mainWindow);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(miniPlayerWindow);
        windowSystem.AddWindow(miniChatWindow);

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        ContextMenu.OnMenuOpened += OnMenuOpened;
        CommandManager.AddHandler("/alpha", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Alpha Channel. /alpha watch <name> | leave | stage | patreon true/false.",
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

            case "patreon":
                if (string.Equals(
                        rest,
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    Cfg.PatreonMember = true;
                    Cfg.PatreonMembershipTier = 3;
                    Cfg.Save();
                    mainWindow.NotifyLocalPatreonMembershipChanged();
                    ChatGui.Print(
                        "[AlphaChannel] Patreon simulation enabled at tier 3. Use Refresh access on the DJ Live page.");
                }
                else if (string.Equals(
                             rest,
                             "false",
                             StringComparison.OrdinalIgnoreCase))
                {
                    Cfg.PatreonMember = false;
                    Cfg.PatreonMembershipTier = null;
                    Cfg.Save();
                    mainWindow.NotifyLocalPatreonMembershipChanged();
                    ChatGui.Print(
                        "[AlphaChannel] Patreon simulation disabled.");
                }
                else
                {
                    ChatGui.Print(
                        "Usage: /alpha patreon true|false");
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

        // "Make it easier for people to find one another": resolves by the target's actual FFXIV
        // character identity (name+world), not a chosen name anyone has to know/type - if you can
        // see them, you can add them. Needs both a signed-in caller and a resolvable world (cross-
        // world/instanced targets don't always carry one, so world resolution remains guarded by
        // the try/catch below.
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
                    Name = "Join Stream",
                    PrefixChar = 'A',
                    PrefixColor = 588,
                    OnClicked = clickedArgs =>
                    {
                        _ = Task.Run(async () =>
                        {
                            var result = await friendsClient.FindJoinableStreamByCharacterAsync(
                                session.Token, characterName, world);
                            if (result is null)
                            {
                                frameworkActions.Enqueue(() =>
                                    ChatGui.Print($"[AlphaChannel] {characterName} is not hosting a joinable Watch Party."));
                                return;
                            }

                            if (result.Kind == AlphaChannel.Contracts.RoomKind.Locked)
                            {
                                frameworkActions.Enqueue(() =>
                                    ChatGui.Print($"[AlphaChannel] {characterName}'s Watch Party requires a password. Join it from the Party Directory."));
                                return;
                            }

                            frameworkActions.Enqueue(() =>
                            {
                                queue.Clear();
                                mainWindow.OpenViewerAndJoin(
                                    result.AccountId,
                                    visibleHostName: result.DisplayName);
                            });
                        });
                    },
                });

                args.AddMenuItem(new MenuItem
                {
                    Name = "Add Alpha Channel Friend",
                    PrefixChar = 'A',
                    PrefixColor = 588,
                    OnClicked = clickedArgs =>
                    {
                        _ = Task.Run(async () =>
                        {
                            var outcome = await friendsClient.SendRequestByCharacterAsync(session.Token, characterName, world);
                            frameworkActions.Enqueue(() =>
                                mainWindow.HandleAddFriendByCharacterResult(outcome, characterName));
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
        var engine =
            screenController.Engine;

        var isViewing =
            stream.Mode ==
            StreamMode.Viewing;

        //
        // A viewer must have explicitly spawned their TV. Solo and hosting
        // modes use the engine's own active state.
        //
        if (!engine.IsActive ||
            (
                isViewing &&
                !mainWindow.ViewerTvEnabled
            ))
        {
            screenRangePaused =
                false;

            screenRangeWarningShown =
                false;

            return;
        }

        var localPlayer =
            ObjectTable.LocalPlayer;

        //
        // Zoning can temporarily remove LocalPlayer. Treat that as being
        // outside the allowed range.
        //
        if (localPlayer is null)
        {
            if (!screenRangePaused)
            {
                if (isViewing)
                {
                    ChatGui.Print(
                        "[AlphaChannel] TV despawned because you're no longer near it.");

                    AepLog.Info(
                        "[WatchParty] Viewer left TV area; despawning local TV.");

                    mainWindow.DespawnViewerTv(stopPlayback: !miniPlayerWindow.IsOpen);
                }
                else
                {
                    ChatGui.Print(
                        miniPlayerWindow.IsOpen
                            ? "[AlphaChannel] TV despawned; playback is continuing in Mini Player."
                            : "[AlphaChannel] Playback paused because you're no longer near the TV.");

                    AepLog.Info(
                        miniPlayerWindow.IsOpen
                            ? "[Screen] Player left TV area; continuing playback in Mini Player."
                            : "[Screen] Player left TV area; pausing playback and despawning TV.");

                    if (!miniPlayerWindow.IsOpen)
                    {
                        video.Pause(true);
                    }

                    engine.DespawnScreen();
                }

                screenRangePaused =
                    true;
            }

            return;
        }

        var distance =
            Vector3.Distance(
                localPlayer.Position,
                engine.ScreenPosition);

        //
        // Warn once while approaching the cutoff.
        //
        if (distance >
                HostScreenWarnDistance &&
            distance <=
                HostScreenPauseDistance &&
            !screenRangeWarningShown)
        {
            ChatGui.Print(
                isViewing || miniPlayerWindow.IsOpen
                    ? "[AlphaChannel] You're getting too far from the TV. Move closer or it will despawn."
                    : "[AlphaChannel] You're getting too far from the TV. Move closer or playback will pause and the TV will despawn.");

            screenRangeWarningShown =
                true;
        }

        //
        // Apply the hard cutoff once.
        //
        if (distance >
            HostScreenPauseDistance)
        {
            if (!screenRangePaused)
            {
                if (isViewing)
                {
                    ChatGui.Print(
                        "[AlphaChannel] TV despawned because you moved too far away.");

                    AepLog.Info(
                        $"[WatchParty] Viewer is {distance:F1} yalms from TV; despawning local TV.");

                    mainWindow.DespawnViewerTv(stopPlayback: !miniPlayerWindow.IsOpen);
                }
                else
                {
                    ChatGui.Print(
                        miniPlayerWindow.IsOpen
                            ? "[AlphaChannel] TV despawned; playback is continuing in Mini Player."
                            : "[AlphaChannel] Playback paused and the TV despawned because you moved too far away.");

                    AepLog.Info(
                        miniPlayerWindow.IsOpen
                            ? $"[Screen] Player is {distance:F1} yalms from TV; continuing playback in Mini Player."
                            : $"[Screen] Player is {distance:F1} yalms from TV; pausing playback and despawning TV.");

                    if (!miniPlayerWindow.IsOpen)
                    {
                        video.Pause(true);
                    }

                    engine.DespawnScreen();
                }

                screenRangePaused =
                    true;
            }

            return;
        }

        //
        // Reset the warning after returning to the safe area. Playback and
        // the TV deliberately remain paused/despawned until manually restored.
        //
        if (distance <=
            HostScreenWarnDistance)
        {
            screenRangeWarningShown =
                false;

            screenRangePaused =
                false;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        while (frameworkActions.TryDequeue(out var action))
        {
            action();
        }

        screenController.OnFrameworkUpdate();
        queue.OnFrameworkUpdate();
        video.UpdateIdleScreensaver();
        EnsureCharacterHasName();
        var contentId = ReadLocalContentId();
        mainWindow.CurrentDisplayName = Cfg.CharacterDisplayNames.GetValueOrDefault(contentId);
        mainWindow.CurrentSession = Cfg.CharacterSessions.GetValueOrDefault(contentId);
        UpdateSessionValidation(contentId, mainWindow.CurrentSession);
        mainWindow.CurrentCharacterName = ObjectTable.LocalPlayer?.Name.TextValue;
        mainWindow.CurrentWorldName = ReadLocalWorldName();
        mainWindow.RefreshHostedRoomWorldMetadata();
        mainWindow.UpdateLocalPartyAlerts();
        mainWindow.UpdatePromotionalAlerts();
        mainWindow.CurrentIsLalafell = ReadIsLalafell();
        if (mainWindow.ApplyPendingJoinQueueClear())
        {
            //
            // Joining may be confirmed just after the host's first state was
            // received. The local cleanup above stops the old browser/game and
            // TV, so forget every previous playback guard and replay only the
            // state that belongs to the newly joined room. This also allows the
            // TV spawn prompt to appear again for the new room.
            //
            lastReceivedRemoteUrl = null;
            lastAppliedRemoteUrl = null;
            waitingForMedia = true;
            loggedEmptyRemoteState = false;
            nextLiveHlsRetryUtc = DateTime.MinValue;
            latestRemoteState = stream.CurrentRoomState;
            pendingRemoteState = stream.CurrentRoomState;
            AepLog.Info(
                "[WatchParty] Viewer join confirmed; reset local playback and replayed the new room state.");
        }
        mainWindow.ApplyPendingRoomEndedReset();

        if (stream.Mode == StreamMode.None && miniChatWindow.IsOpen)
        {
            miniChatWindow.IsOpen = false;
        }

        if (pendingRemoteState is { } remoteState)
        {
            pendingRemoteState = null;
            ApplyRemoteState(remoteState);
        }

        if ((mainWindow.ViewerTvEnabled || miniPlayerWindow.IsOpen) &&
            waitingForMedia &&
            !screenController.Engine.IsActive)
        {
            AepLog.Warning("[WatchParty] Attempting waiting screen spawn");
            video.ShowWaitingScreen();
            if (!mainWindow.ViewerTvEnabled && miniPlayerWindow.IsOpen)
            {
                screenController.Engine.DespawnScreen();
            }
        }

        ApplyAutoPause();
        ApplyHostScreenRangePause();

        UpdateRecentlyWatched();

        UpdateReactions();

        nearbyAutoWatch.OnFrameworkUpdate();

        //
        // Local playback remains local until the user explicitly creates
        // a Watch Party. Once hosting, publish the current media and current
        // timestamp exactly as before.
        //
        // SNES/Game Boy use a separate armed broadcast URL. Their encoder is
        // demand-controlled by UpdateGameBroadcastDemand(), but the room
        // continues advertising the HLS URL while the broadcast is armed.
        //
        var current =
            queue.Current;

        var engine =
            screenController.Engine;

        mainWindow.UpdateGameBroadcastDemand();
        mainWindow.UpdateLocalVideoBroadcastDemand();

        if (stream.Mode == StreamMode.Hosting)
        {
            var localVideoBroadcastUrl =
     mainWindow.ActiveLocalVideoBroadcastHlsUrl;

            var gameBroadcastUrl =
                mainWindow.ActiveGameBroadcastHlsUrl;

            var browserBroadcastUrl =
                mainWindow.ActiveBrowserBroadcastHlsUrl;

            var relayBroadcastUrl =
                !string.IsNullOrWhiteSpace(
                    localVideoBroadcastUrl)
                    ? localVideoBroadcastUrl
                    : !string.IsNullOrWhiteSpace(
                        browserBroadcastUrl)
                        ? browserBroadcastUrl
                        : gameBroadcastUrl;

            if (!string.IsNullOrWhiteSpace(
                    relayBroadcastUrl))
            {
                var isLocalVideoBroadcast =
                    !string.IsNullOrWhiteSpace(
                        localVideoBroadcastUrl);

                var isBrowserBroadcast =
                    !isLocalVideoBroadcast &&
                    !string.IsNullOrWhiteSpace(
                        browserBroadcastUrl);

                _ = stream.PublishStateAsync(
                    relayBroadcastUrl,
                    0d,
                    isLocalVideoBroadcast &&
                    mainWindow.ActiveLocalVideoBroadcastPaused,
                    engine.IsActive
                        ? engine.ScreenPosition
                        : null,
                    engine.IsActive
                        ? engine.ScreenYaw
                        : null,
 engine.IsActive
    ? engine.ScreenScale
    : null,
engine.IsActive
    ? engine.DisableFixedScreenScaleRatio
    : null,
engine.IsActive
    ? engine.ScreenWidthScale
    : null,
engine.IsActive
    ? engine.ScreenHeightScale
    : null,
                  isLocalVideoBroadcast
    ? mainWindow.ActiveLocalVideoBroadcastTitle ??
      "Local Video"
    : isBrowserBroadcast
    ? mainWindow.ActiveBrowserBroadcastTitle ??
      "Web Browser"
    : mainWindow.ActiveGameBroadcastTitle ??
      "Gameplay",
null);
            }
            else
            {
                var (position, _, paused) =
                    current is null
                        ? (0d, 0d, true)
                        : video.GetProgress();

                //
                // Direct URL entries use the URL as their temporary
                // title until AetherStreamQueue finishes resolving
                // metadata. Do not expose that placeholder as a title.
                //
                var mediaTitle =
                    mainWindow.IsGameBroadcastArmed
                        ? mainWindow.ActiveGameBroadcastTitle
                        : current is not null &&
                    !string.IsNullOrWhiteSpace(
                        current.Title) &&
                    !string.Equals(
                        current.Title,
                        current.Url,
                        StringComparison.OrdinalIgnoreCase)
                        ? current.Title
                        : null;

                var publishedUrl =
    current?.Url;

                if (!string.IsNullOrWhiteSpace(
                        publishedUrl) &&
                    engine.IsAudioOnly)
                {
                    publishedUrl =
                        AudioVisualizerSelection.AddToUrl(
                            publishedUrl,
                            mainWindow.PartyVisualizerMode,
                            mainWindow.PartyVisualizerTheme);
                }

                _ = stream.PublishStateAsync(
                    publishedUrl,
                    position,
                                    paused,
                    engine.IsActive
                        ? engine.ScreenPosition
                        : null,
                    engine.IsActive
                        ? engine.ScreenYaw
                        : null,
                    engine.IsActive
                        ? engine.ScreenScale
                        : null,
                    mediaTitle,
                    current?.ThumbnailUrl);

                if (current is not null)
                {
                    video.SetOverlayTitle(
                        current.Title,
                        current.Source);
                }
            }
        }
    }

    internal void UpdateRecentlyWatched()
    {
        if (queue.Current is not { } current ||
            current.IsTransient)
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
        var contentId =
            ReadLocalContentId();

        if (contentId == 0 ||
            mainWindow.IsNamePromptActive)
        {
            return;
        }

        //
        // Temporary testing mode: show the onboarding once on every plugin
        // load, even when this character already has a saved username.
        //
        if (ForceFirstLaunchExperienceForTesting)
        {
            if (forcedFirstLaunchPromptRequested ||
                !mainWindow.IsOpen)
            {
                return;
            }

            forcedFirstLaunchPromptRequested =
                true;

            var suggested =
                Cfg.CharacterDisplayNames
                    .GetValueOrDefault(
                        contentId) ??
                ObjectTable.LocalPlayer?
                    .Name.TextValue ??
                "Player";

            PromptForName(
                contentId,
                suggested);

            return;
        }

        if (Cfg.CharacterDisplayNames.ContainsKey(
                contentId))
        {
            return;
        }

        //
        // Don't force the whole plugin UI open just because the character
        // still needs a username. Wait until they open Alpha Channel.
        //
        if (!mainWindow.IsOpen)
        {
            return;
        }

        PromptForName(
            contentId,
            ObjectTable.LocalPlayer?
                .Name.TextValue ??
            "Player");
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
            AepLog.Info(
                "[WatchParty] Spawned viewer TV with the waiting screen while media state is pending.");
            return;
        }

        ApplyRemoteState(state);
        screenController.Engine.RespawnScreen();
        AepLog.Info(
            "[WatchParty] Spawned viewer TV and applied the host's current media state.");
    }

    private void UpdateSessionValidation(
        ulong contentId,
        CharacterSession? session)
    {
        if (contentId != observedSessionContentId)
        {
            observedSessionContentId = contentId;
            validatedSessionContentId = 0;
            validatingSessionContentId = 0;
            nextSessionValidationAttemptUtc = DateTime.MinValue;
            mainWindow.SessionValidationInProgress = false;
        }

        if (contentId == 0)
        {
            return;
        }

        if (session is null)
        {
            validatedSessionContentId = contentId;
            mainWindow.SessionValidationInProgress = false;
            return;
        }

        // A structurally incomplete session cannot authenticate and is safe to
        // remove locally without making a request. The encrypted vault itself
        // is left intact if loading it failed; Configuration never supplies a
        // partially loaded vault in that case.
        if (string.IsNullOrWhiteSpace(session.Token) ||
            string.IsNullOrWhiteSpace(session.AccountId))
        {
            Cfg.CharacterSessions.Remove(contentId);
            Cfg.Save();
            validatedSessionContentId = contentId;
            mainWindow.SessionValidationInProgress = false;
            return;
        }

        if (validatedSessionContentId == contentId ||
            validatingSessionContentId == contentId ||
            DateTime.UtcNow < nextSessionValidationAttemptUtc)
        {
            return;
        }

        validatingSessionContentId = contentId;
        mainWindow.SessionValidationInProgress = true;
        var token = session.Token;

        sessionValidationTask = Task.Run(async () =>
        {
            var result = await authClient
                .ValidateSessionAsync(token, sessionValidationLifetime.Token)
                .ConfigureAwait(false);

            frameworkActions.Enqueue(() =>
            {
                if (ReadLocalContentId() != contentId)
                {
                    return;
                }

                validatingSessionContentId = 0;
                mainWindow.SessionValidationInProgress = false;

                if (result.Account is { } account)
                {
                    if (!Cfg.CharacterSessions.TryGetValue(contentId, out var current) ||
                        !string.Equals(current.Token, token, StringComparison.Ordinal))
                    {
                        return;
                    }

                    mainWindow.NotifySessionRefreshed(
                        current.AvatarImageUrl,
                        account.AvatarImageUrl);
                    current.AccountId = account.AccountId;
                    current.Handle = account.Handle;
                    current.DisplayName = account.DisplayName;
                    current.InviteCode = account.InviteCode;
                    current.AvatarIcon = account.AvatarIcon;
                    current.AvatarColorHex = account.AvatarColorHex;
                    current.AvatarImageUrl = account.AvatarImageUrl;
                    current.Bio = account.Bio;
                    current.StatusMessage = account.StatusMessage;
                    Cfg.Save();
                    validatedSessionContentId = contentId;
                    return;
                }

                if (result.Failure?.Failure == NetworkFailure.Unauthorized)
                {
                    // Only a definite authentication rejection removes the
                    // token. Timeouts, outages and malformed responses retain
                    // it so a temporary problem cannot sign the player out.
                    if (Cfg.CharacterSessions.TryGetValue(contentId, out var current) &&
                        string.Equals(current.Token, token, StringComparison.Ordinal))
                    {
                        Cfg.CharacterSessions.Remove(contentId);
                        Cfg.Save();
                    }

                    validatedSessionContentId = contentId;
                    return;
                }

                // Retry temporary failures quietly. The existing session stays
                // usable while the server or network recovers.
                nextSessionValidationAttemptUtc =
                    DateTime.UtcNow.AddMinutes(5);
            });
        });
    }

    private void OpenMiniPlayer()
    {
        miniPlayerWindow.IsOpen = true;

        if (stream.Mode != StreamMode.Viewing)
        {
            return;
        }

        var state = latestRemoteState;
        if (state is null || string.IsNullOrWhiteSpace(state.Url))
        {
            waitingForMedia = true;
            video.ShowWaitingScreen();
            screenController.Engine.DespawnScreen();
            return;
        }

        ApplyRemoteState(state);
    }

    private void OpenMiniChat()
    {
        if (stream.Mode is not (StreamMode.Hosting or StreamMode.Viewing))
        {
            ChatGui.Print("[AlphaChannel] Join or create a Watch Party to use Mini Chat.");
            return;
        }

        miniChatWindow.IsOpen = true;
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

        //
        // Always remember the host's latest state, including while the
        // viewer has chosen not to spawn their local TV.
        //
        latestRemoteState =
            message;

        if (string.IsNullOrWhiteSpace(message.Url))
        {
            if (!loggedEmptyRemoteState)
            {
                AepLog.Info(
                    "[WatchParty] Waiting for the host's media stream.");
                loggedEmptyRemoteState = true;
            }

            waitingForMedia =
                true;

            //
            // A livestream source may briefly disappear while FFmpeg and
            // MediaMTX change publishers. Let the same URL be applied again
            // when it returns.
            //
            lastAppliedRemoteUrl =
                null;

            nextLiveHlsRetryUtc =
                DateTime.MinValue;

            return;
        }

        waitingForMedia =
            false;

        loggedEmptyRemoteState =
            false;

        //
        // Viewers do not automatically spawn a TV. The first shared state
        // received for this room offers them the choice instead.
        //
        if (!mainWindow.ViewerTvEnabled &&
            !miniPlayerWindow.IsOpen)
        {
            mainWindow.RequestViewerTvSpawnPrompt();
            return;
        }

        var url =
     message.Url;

        var isLiveHls =
            Uri.TryCreate(
                url,
                UriKind.Absolute,
                out var liveMediaUri) &&
            liveMediaUri.Port == 8888 &&
            liveMediaUri.AbsolutePath.StartsWith(
                "/live/",
                StringComparison.OrdinalIgnoreCase) &&
            liveMediaUri.AbsolutePath.EndsWith(
                "/index.m3u8",
                StringComparison.OrdinalIgnoreCase);

        var urlChanged =
            !string.Equals(
                url,
                lastAppliedRemoteUrl,
                StringComparison.Ordinal);

        var livePlaybackNeedsRetry =
            isLiveHls &&
            video.State is
                VideoPlaybackState.Failed or
                VideoPlaybackState.Idle;

        var retryIsDue =
            DateTime.UtcNow >=
            nextLiveHlsRetryUtc;

        if (urlChanged)
        {
            lastAppliedRemoteUrl =
                url;

            nextLiveHlsRetryUtc =
                DateTime.UtcNow +
                LiveHlsRetryDelay;

            video.Play(
                url);
        }
        else if (livePlaybackNeedsRetry &&
                 retryIsDue)
        {
            //
            // Keep the viewer's TV present while MediaMTX prepares the new HLS
            // manifest, then make another clean playback attempt. This also
            // recovers after the first attempt entered Failed rather than Idle.
            //
            video.ShowWaitingScreen();

            nextLiveHlsRetryUtc =
                DateTime.UtcNow +
                LiveHlsRetryDelay;

            AepLog.Info(
                "[WatchParty] Retrying live HLS playback after a temporary failure.");

            video.Play(
                url);
        }
        else if (!isLiveHls &&
                 video.State ==
                 VideoPlaybackState.Idle)
        {
            //
            // Preserve the previous same-URL restart behaviour for ordinary
            // media after joining or resetting local playback.
            //
            video.Play(
                url);
        }

        //
        // Ordinary videos use position correction. Live HLS and audio-only
        // streams are not safely seekable and must remain attached to their
        // live edge.
        //
        if (!isLiveHls &&
            !video.IsAudioOnly)
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

        if (mainWindow.PartySyncTvPlacement &&
            message.ScreenX is { } x &&
            message.ScreenY is { } y &&
            message.ScreenZ is { } z &&
            message.ScreenYaw is { } yaw &&
            message.ScreenScale is { } scale)
        {
            screenController.Engine.ApplyRemoteScreenTransform(
                new Vector3(
                    x,
                    y,
                    z),
                yaw,
                scale,
                message.ScreenDisableFixedScaleRatio ??
                false,
                message.ScreenWidthScale,
                message.ScreenHeightScale);
        }

        if (!mainWindow.ViewerTvEnabled &&
            miniPlayerWindow.IsOpen)
        {
            screenController.Engine.DespawnScreen();
        }
    }

    private static unsafe ulong ReadLocalContentId()
    {
        var state = PlayerState.Instance();
        return state is null ? 0 : state->ContentId;
    }

    // CurrentWorld follows world visits and data-centre travel.
    // HomeWorld would always return the character's original server.
    private static string? ReadLocalWorldName()
    {
        var player =
            ObjectTable.LocalPlayer;

        if (player is null)
        {
            return null;
        }

        try
        {
            var worldName =
                player.CurrentWorld.Value.Name
                    .ToString();

            return string.IsNullOrWhiteSpace(
                    worldName)
                ? null
                : worldName;
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[World] Could not resolve current world: {exception.Message}");

            return null;
        }
    }

    // Lalafell race ID in the character customization data.
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
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }

        static void Cleanup(
            string name,
            Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                AepLog.Error(
                    $"[Shutdown] {name} cleanup failed: {exception}");
            }
        }

        Cleanup("session validation cancellation", sessionValidationLifetime.Cancel);
        Cleanup(
            "session validation worker",
            () =>
            {
                if (sessionValidationTask is { IsCompleted: false } &&
                    !sessionValidationTask.Wait(TimeSpan.FromSeconds(3)))
                {
                    AepLog.Warning(
                        "[Shutdown] Session validation worker did not stop within 3 seconds.");
                }
            });
        Cleanup("/alpha command", () => CommandManager.RemoveHandler("/alpha"));
        Cleanup("/wp command", () => CommandManager.RemoveHandler("/wp"));
        Cleanup("context menu", () => ContextMenu.OnMenuOpened -= OnMenuOpened);
        Cleanup("main UI callback", () => PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow);
        Cleanup("window draw callback", () => PluginInterface.UiBuilder.Draw -= windowSystem.Draw);
        Cleanup("framework update callback", () => Framework.Update -= OnFrameworkUpdate);

        Cleanup("main window", mainWindow.Dispose);
        Cleanup("mini player", miniPlayerWindow.Dispose);
        Cleanup("mini chat", miniChatWindow.Dispose);
        Cleanup("nearby auto-watch", nearbyAutoWatch.Dispose);
        Cleanup("queue", queue.Dispose);

        Cleanup("stream event", () => stream.OnState -= OnRemoteState);
        Cleanup("rename event", () => stream.OnRenameRequired -= OnRenameRequired);
        Cleanup("realtime connection", stream.Dispose);

        Cleanup("video player", video.Dispose);
        Cleanup("screen controller", screenController.Dispose);
        Cleanup("DirectX hook", DxHandler.Dispose);
        Cleanup("session validation token", sessionValidationLifetime.Dispose);
    }
}
