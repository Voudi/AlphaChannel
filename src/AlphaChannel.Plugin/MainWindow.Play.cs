using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
 


    //
    // =============================================================
    // Play Games state
    // =============================================================
    //

    private enum GameSystem
    {
        Snes,
        GameBoy,
        Nes,
        GameBoyAdvance,
        MasterSystem,
        GameGear
    }


    private GameSystem selectedGameSystem =
          GameSystem.Snes;

    //
    // "Armed" is intentionally separate from the encoder's IsBroadcasting
    // state. The host sees the broadcast as enabled while the encoder itself
    // runs only when at least one viewer is present.
    //
    private bool gameBroadcastArmed;
    private bool gameBroadcastForSnes;
    private GameSystem gameBroadcastSystem = GameSystem.Snes;
    private bool gameBroadcastStartFailed;
    private string? gameBroadcastPublishUrl;
    private string? gameBroadcastHlsUrl;
    private DateTime gameBroadcastViewerReadyUtc = DateTime.MaxValue;
    private string? gamePatreonAccessMessage;

    // MediaMTX does not expose the HLS playlist until FFmpeg has supplied the
    // first keyframe and segment. Keep viewers on the waiting screen during
    // that short startup window instead of handing mpv a URL that still 404s.
    private static readonly TimeSpan GameBroadcastViewerWarmup =
        TimeSpan.FromSeconds(4);

    internal bool IsGameBroadcastArmed => gameBroadcastArmed;

    internal string? ActiveGameBroadcastHlsUrl =>
        gameBroadcastArmed &&
        DateTime.UtcNow >= gameBroadcastViewerReadyUtc
            ? gameBroadcastHlsUrl
            : null;

    internal string? ActiveGameBroadcastTitle
    {
        get
        {
            if (!gameBroadcastArmed)
            {
                return null;
            }

            var romPath = gameBroadcastSystem switch
            {
                GameSystem.Snes => snesSelectedRomPath,
                GameSystem.Nes => nesSelectedRomPath,
                GameSystem.GameBoyAdvance => gameBoyAdvanceSelectedRomPath,
                GameSystem.MasterSystem => masterSystemSelectedRomPath,
                GameSystem.GameGear => gameGearSelectedRomPath,
                _ => gameBoySelectedRomPath
            };

            var gameName =
                string.IsNullOrWhiteSpace(
                    romPath)
                    ? string.Empty
                    : LibraryGameName(
                        romPath);

            if (!string.IsNullOrWhiteSpace(
                    gameName))
            {
                return $"Playing: {gameName}";
            }

            return gameBroadcastSystem switch
            {
                GameSystem.Snes => "SNES Gameplay",
                GameSystem.Nes => "NES Gameplay",
                GameSystem.GameBoyAdvance => "Game Boy Advance Gameplay",
                GameSystem.MasterSystem => "Master System / SG-1000 Gameplay",
                GameSystem.GameGear => "Game Gear Gameplay",
                _ => "Game Boy Gameplay"
            };
        }
    }


    private string snesSelectedRomPath =
        string.Empty;

    private string? snesLaunchError;

    private readonly FileDialogManager snesFileDialog =
        new();


    private string gameBoySelectedRomPath =
        string.Empty;

    private string? gameBoyLaunchError;

    private readonly FileDialogManager gameBoyFileDialog =
        new();

    private string nesSelectedRomPath = string.Empty;
    private string? nesLaunchError;
    private readonly FileDialogManager nesFileDialog = new();

    private string gameBoyAdvanceSelectedRomPath = string.Empty;
    private string? gameBoyAdvanceLaunchError;
    private readonly FileDialogManager gameBoyAdvanceFileDialog = new();

    private string masterSystemSelectedRomPath = string.Empty;
    private string? masterSystemLaunchError;
    private readonly FileDialogManager masterSystemFileDialog = new();

    private string gameGearSelectedRomPath = string.Empty;
    private string? gameGearLaunchError;
    private readonly FileDialogManager gameGearFileDialog = new();


    private bool snesControlsPopupRequested;

    private bool snesRomSourcesPopupRequested;


    private void DrawPlaySnesPage()
    {
        //
        // If a game is already running, keep the page locked
        // to the system that actually owns the TV.
        //

        if (screenController.Engine.IsPlayingSnes)
        {
            selectedGameSystem =
                GameSystem.Snes;
        }
        else if (screenController.Engine.IsPlayingGameBoy)
        {
            selectedGameSystem =
                GameSystem.GameBoy;
        }


        var isPlaying =
            selectedGameSystem ==
                GameSystem.Snes
                ? screenController.Engine
                    .IsPlayingSnes
                : screenController.Engine
                    .IsPlayingGameBoy;

        var controlsEnabled =
            selectedGameSystem ==
                GameSystem.Snes
                ? screenController.Engine
                    .SnesControlsEnabled
                : screenController.Engine
                    .GameBoyControlsEnabled;

        var availableWidth =
            ImGui.GetContentRegionAvail().X;


        //
        // =========================================================
        // Local-only notice
        // =========================================================
        //

        DrawGamesInfoBanner();

        ImGui.Dummy(
            UiVec(0f, 14f));


        //
        // =========================================================
        // Console selector
        // =========================================================
        //

        DrawGameSystemSelector();

        ImGui.Dummy(
            UiVec(0f, 14f));


        //
        // =========================================================
        // Top row
        //
        // Play a Game | Status
        // =========================================================
        //

        var topGap = Ui(14f);

        var playWidth =
            MathF.Max(
                420f,
                availableWidth *
                0.62f);

        var statusWidth =
            MathF.Max(
                280f,
                availableWidth -
                playWidth -
                topGap);

        var topHeight = Ui(430f);


        DrawSnesPanel(
            "##gamesPlayPanel",
            new Vector2(
                playWidth,
                topHeight),
            () =>
            {
                DrawGamesPlayPanel(
                    isPlaying);
            });


        ImGui.SameLine(
            0f,
            topGap);


        DrawSnesPanel(
            "##gamesStatusPanel",
            new Vector2(
                statusWidth,
                topHeight),
            () =>
            {
                DrawGamesStatusPanel(
                    isPlaying,
                    controlsEnabled);
            });


        ImGui.Dummy(
            UiVec(0f, 14f));


        //
        // =========================================================
        // Game Broadcast
        // =========================================================
        //

        DrawGameBroadcastPanel(
            isPlaying);

        ImGui.Dummy(
            UiVec(0f, 14f));


        //
        // =========================================================
        // Controls + Audio
        // =========================================================
        //

        DrawSnesControlsAudioPanel(
            isPlaying,
            controlsEnabled);


        ImGui.Dummy(
            UiVec(0f, 14f));


        //
        // =========================================================
        // Bindings
        //
        // Converted in the next pass.
        // =========================================================
        //

        DrawSnesBindingsPanel();


        ImGui.Dummy(
            UiVec(0f, 14f));


        //
        // =========================================================
        // Save information
        //
        // Converted in the next pass.
        // =========================================================
        //

        DrawSnesSavePanel();


        //
        // Extra scroll clearance for the persistent media transport bar.
        //

        ImGui.Dummy(
            new Vector2(
                0f,
                BottomBarHeight +
                Ui(25f)));


        ImGui.SetNextWindowSize(
            UiVec(940f, 600f),
            ImGuiCond.Appearing);

        ImGui.SetNextWindowPos(
            ImGui.GetMainViewport()
                .GetCenter(),
            ImGuiCond.Appearing,
            new Vector2(
                0.5f,
                0.5f));


        snesFileDialog.Draw();
        gameBoyFileDialog.Draw();

        DrawSnesControlsPopup();
        DrawSnesRomSourcesPopup();
    }

    // =============================================================
    // Notice banner
    // =============================================================

    private void DrawGamesInfoBanner()
    {
        const float height =
            82f;

        using var child =
            ImRaii.Child(
                "##gamesInfoBanner",
                new Vector2(
                    -1f,
                    height),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse);

        if (!child)
        {
            return;
        }


        var pos =
            ImGui.GetWindowPos();

        var size =
            ImGui.GetWindowSize();

        var drawList =
            ImGui.GetWindowDrawList();

        var min =
            pos;

        var max =
            pos +
            size;


        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X * 0.16f,
                    Accent.Y * 0.12f,
                    Accent.Z * 0.22f,
                    0.92f)),
            13f);

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.80f)),
            13f,
            ImDrawFlags.None,
            1.2f);


        //
        // Icon disc
        //

        var iconCenter =
            min +
            new Vector2(
                Ui(31f),
                height /
                2f);

        drawList.AddCircleFilled(
            iconCenter,
            16f,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.20f)),
            32);


        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            var icon =
                FontAwesomeIcon.InfoCircle
                    .ToIconString();

            var iconSize =
                ImGui.CalcTextSize(
                    icon);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconCenter -
                iconSize /
                2f,
                ImGui.GetColorU32(
                    Accent),
                icon);
        }


        //
        // Text
        //

        var textX =
            min.X +
            58f;

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                textX,
                min.Y +
                Ui(19f)),
            ImGui.GetColorU32(
                Accent),
            "LOCAL PLAY ONLY");

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                textX,
                min.Y +
                Ui(46f)),
            ImGui.GetColorU32(
                MutedText),
            "Games run locally on your computer and do not currently support Watch Party syncing.");
    }


    // =============================================================
    // System selector
    // =============================================================

    private void DrawGameSystemSelector()
    {
        var gameRunning =
            screenController.Engine
                .IsPlayingSnes ||
            screenController.Engine
                .IsPlayingGameBoy;

                DrawSnesPanel(
            "##gameSystemSelector",
            UiVec(-1f, 142f),
                    () =>
            {
                ImGui.TextUnformatted(
                    "Choose a System");

                ImGui.Dummy(
                    UiVec(0f, 4f));

                ImGui.TextColored(
                    MutedText,
                    gameRunning
                        ? "Exit the current game before changing systems."
                        : "Select the console you want to play.");

                ImGui.Dummy(
                    UiVec(0f, 12f));


                var available =
                    ImGui.GetContentRegionAvail().X;

                const float gap =
                    10f;

                var buttonWidth =
                    MathF.Max(
                        160f,
                        (available -
                         gap) /
                        2f);


                using (
                    ImRaii.Disabled(
                        gameRunning))
                {
                    if (DrawSnesSegmentButton(
                            "Super Nintendo",
                            selectedGameSystem ==
                            GameSystem.Snes,
                            new Vector2(
                                buttonWidth,
                                Ui(38f))))
                    {
                        selectedGameSystem =
                            GameSystem.Snes;

                        snesLaunchError =
                            null;

                        gameBoyLaunchError =
                            null;
                    }


                    ImGui.SameLine(
                        0f,
                        gap);


                    if (DrawSnesSegmentButton(
                            "Game Boy / Color",
                            selectedGameSystem ==
                            GameSystem.GameBoy,
                            new Vector2(
                                buttonWidth,
                                Ui(38f))))
                    {
                        selectedGameSystem =
                            GameSystem.GameBoy;

                        snesLaunchError =
                            null;

                        gameBoyLaunchError =
                            null;
                    }
                }
            });
    }


    // =============================================================
    // Play panel
    // =============================================================

    private void DrawGamesPlayPanel(
     bool isPlaying)
    {
        var isSnes =
            selectedGameSystem ==
            GameSystem.Snes;

        var systemName =
            isSnes
                ? "Super Nintendo"
                : "Game Boy / Game Boy Color";

        var selectedPath =
            isSnes
                ? snesSelectedRomPath
                : gameBoySelectedRomPath;

        var launchError =
            isSnes
                ? snesLaunchError
                : gameBoyLaunchError;


        DrawSnesSectionHeader(
            FontAwesomeIcon.Gamepad,
            "Play a Game",
            isPlaying
                ? $"Your current {systemName} session."
                : $"Select a {systemName} ROM from your computer.");


        ImGui.Dummy(
            UiVec(0f, 18f));


        //
        // ROM selector
        //

        ImGui.TextColored(
            MutedText,
            "ROM File");

        ImGui.Dummy(
            UiVec(0f, 5f));


        var displayPath =
            string.IsNullOrWhiteSpace(
                selectedPath)
                ? "No ROM selected"
                : selectedPath;

        var buttonWidth = Ui(112f);

        var rowWidth =
            ImGui.GetContentRegionAvail().X;


        ImGui.SetNextItemWidth(
            MathF.Max(
                180f,
                rowWidth -
                buttonWidth -
                10f));


        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                9f))
        {
            ImGui.InputText(
                isSnes
                    ? "##snesRomPath"
                    : "##gameBoyRomPath",
                ref displayPath,
                1024,
                ImGuiInputTextFlags.ReadOnly);
        }


        ImGui.SameLine(
            0f,
            10f);


        using (
            ImRaii.Disabled(
                isPlaying))
        {
            if (DrawSnesSecondaryButton(
                    FontAwesomeIcon.FolderOpen,
                    "Browse...",
                    new Vector2(
                        buttonWidth,
                        Ui(34f))))
            {
                if (isSnes)
                {
                    snesFileDialog.OpenFileDialog(
                        "Select SNES ROM",
                        ".sfc,.smc",
                        (success, path) =>
                        {
                            if (!success ||
                                string.IsNullOrWhiteSpace(
                                    path))
                            {
                                return;
                            }

                            var extension =
                                Path.GetExtension(
                                    path);

                            if (!extension.Equals(
                                    ".sfc",
                                    StringComparison.OrdinalIgnoreCase) &&
                                !extension.Equals(
                                    ".smc",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                snesLaunchError =
                                    "Please select an .sfc or .smc SNES ROM.";

                                return;
                            }

                            snesSelectedRomPath =
                                path;

                            snesLaunchError =
                                null;
                        });
                }
                else
                {
                    gameBoyFileDialog.OpenFileDialog(
                        "Select Game Boy ROM",
                        ".gb,.gbc,.dmg",
                        (success, path) =>
                        {
                            if (!success ||
                                string.IsNullOrWhiteSpace(
                                    path))
                            {
                                return;
                            }

                            var extension =
                                Path.GetExtension(
                                    path);

                            if (!extension.Equals(
                                    ".gb",
                                    StringComparison.OrdinalIgnoreCase) &&
                                !extension.Equals(
                                    ".gbc",
                                    StringComparison.OrdinalIgnoreCase) &&
                                !extension.Equals(
                                    ".dmg",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                gameBoyLaunchError =
                                    "Please select a .gb, .gbc or .dmg Game Boy ROM.";

                                return;
                            }

                            gameBoySelectedRomPath =
                                path;

                            gameBoyLaunchError =
                                null;
                        });
                }
            }
        }


        ImGui.Dummy(
            UiVec(0f, 7f));


        ImGui.TextColored(
            MutedText,
            isSnes
                ? "Supported ROMs:  .sfc   .smc"
                : "Supported ROMs:  .gb   .gbc   .dmg");


        ImGui.Dummy(
            UiVec(0f, 14f));


        //
        // Main action
        //

        if (isPlaying)
        {
            if (DrawSnesPrimaryAction(
          FontAwesomeIcon.Stop,
          "Exit Game & Despawn TV",
          true))
            {
                StopGameWatchPartyBroadcast();

                screenController.Engine
                    .StopVideo();

                snesLaunchError =
                    null;

                gameBoyLaunchError =
                    null;
            }
        }
        else
        {
            var hasRom =
     !string.IsNullOrWhiteSpace(
         selectedPath);

            var localVideoActive =
                screenController.Engine
                    .IsPlayingLocalVideo;

            var viewingWatchParty =
                stream.Mode ==
                StreamMode.Viewing;


            using (
                ImRaii.Disabled(
                    !hasRom ||
                    localVideoActive ||
                    viewingWatchParty))
            {
                if (DrawSnesPrimaryAction(
                        FontAwesomeIcon.Play,
                        "Start Playing",
                        false))
                {
                    snesLaunchError =
    null;

                    gameBoyLaunchError =
                        null;

                    //
                    // Ensure a previous game's armed HLS session cannot carry over
                    // into this newly selected game.
                    //

                    StopGameWatchPartyBroadcast();


                    var started =
                                            isSnes
                            ? screenController.Engine
                                .PlaySnes(
                                    snesSelectedRomPath)
                            : screenController.Engine
                                .PlayGameBoy(
                                    gameBoySelectedRomPath);


                    if (!started)
                    {
                        if (isSnes)
                        {
                            snesLaunchError =
                                screenController.Engine
                                    .LastError ??
                                "SNES failed to start.";
                        }
                        else
                        {
                            gameBoyLaunchError =
                                screenController.Engine
                                    .LastError ??
                                "Game Boy failed to start.";
                        }
                    }
                }
            }

            if (localVideoActive)
            {
                ImGui.Dummy(
                    UiVec(0f, 5f));

                ImGui.TextColored(
                    MutedText,
                    "Stop Local Video before starting a game.");
            }
            else if (viewingWatchParty)
            {
                ImGui.Dummy(
                    UiVec(0f, 5f));

                ImGui.TextColored(
                    Gold,
                    "Leave the current Watch Party before starting gameplay.");
            }
        }


        launchError =
            isSnes
                ? snesLaunchError
                : gameBoyLaunchError;


        if (!string.IsNullOrWhiteSpace(
                launchError))
        {
            ImGui.Dummy(
                UiVec(0f, 8f));

            ImGui.TextColored(
                Danger,
                launchError);
        }


        ImGui.Dummy(
            UiVec(0f, 7f));

        ImGui.Separator();

        ImGui.Dummy(
            UiVec(0f, 6f));


        //
        // External resources
        //

        ImGui.TextUnformatted(
            "Your ROM library");

        ImGui.Dummy(
            UiVec(0f, 4f));


        if (isSnes)
        {
            if (DrawSnesSecondaryButton(
                    FontAwesomeIcon.ExternalLinkAlt,
                    "SNES ROM Information",
                    UiVec(238f, 35f)))
            {
                snesRomSourcesPopupRequested =
                    true;
            }

            ImGui.Dummy(
                UiVec(0f, 7f));
        }


        ImGui.TextColored(
            MutedText,
            "Use ROM files you are permitted to use.");
    }


    // =============================================================
    // Status panel
    // =============================================================

    private void DrawGamesStatusPanel(
     bool isPlaying,
     bool controlsEnabled)
    {
        var isSnes =
            selectedGameSystem ==
            GameSystem.Snes;

        var systemName =
            isSnes
                ? "Super Nintendo"
                : "Game Boy / Color";

        var shortSystemName =
            isSnes
                ? "SNES"
                : "Game Boy";

        var selectedPath =
            isSnes
                ? snesSelectedRomPath
                : gameBoySelectedRomPath;


        DrawSnesSectionHeader(
            FontAwesomeIcon.Desktop,
            "Game Status",
            $"Current {systemName} emulator status.");


        ImGui.Dummy(
            UiVec(0f, 12f));


        //
        // Status badge
        //

        var statusText =
            isPlaying
                ? "Running"
                : "Ready";

        var statusColor =
            Good;

        var start =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();


        drawList.AddCircleFilled(
            start +
            UiVec(6f, 8f),
            5f,
            ImGui.GetColorU32(
                statusColor),
            20);


        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() +
            17f);

        ImGui.TextColored(
            statusColor,
            statusText);


        ImGui.Dummy(
            UiVec(0f, 5f));


        ImGui.TextColored(
            MutedText,
            isPlaying
                ? "A game is currently running."
                : "No game is currently running.");


        ImGui.Dummy(
            UiVec(0f, 12f));


        DrawSnesStatusLine(
            "System",
            systemName);

        DrawSnesStatusSeparator();


        DrawSnesStatusLine(
            "Game",
            isPlaying
                ? GetSelectedGameName()
                : "—");

        DrawSnesStatusSeparator();


        DrawSnesStatusLine(
            "ROM File",
            isPlaying &&
            !string.IsNullOrWhiteSpace(
                selectedPath)
                ? Path.GetFileName(
                    selectedPath)
                : "—");

        DrawSnesStatusSeparator();


        DrawSnesStatusLine(
            "Input",
            isPlaying &&
            controlsEnabled
                ? $"Control {shortSystemName}"
                : "Control FFXIV");

        DrawSnesStatusSeparator();


        DrawSnesStatusLine(
        "Watch Party",
        "Local only");
    }


    // =============================================================
    // Game Boy broadcasting
    // =============================================================

    private void DrawGameBroadcastPanel(
     bool isPlaying)
    {
        var engine =
            screenController.Engine;

        var isSnes =
            selectedGameSystem ==
            GameSystem.Snes;

        var systemName =
            isSnes
                ? "SNES"
                : "Game Boy";

        var isBroadcasting =
            gameBroadcastArmed &&
            gameBroadcastForSnes ==
            isSnes;

        var isUploading =
            isSnes
                ? engine.IsSnesBroadcasting
                : engine.IsGameBoyBroadcasting;


        DrawSnesPanel(
            "##gameBroadcastPanel",
            UiVec(-1f, 180f),
            () =>
            {
                DrawSnesSectionHeader(
                   FontAwesomeIcon.BroadcastTower,
                   "Game Broadcast",
                   isBroadcasting
                       ? isUploading
                           ? $"Your {systemName} gameplay is being sent to the Alpha Channel live relay."
                           : $"Your {systemName} broadcast is ready and waiting for a viewer."
                       : "Broadcast your gameplay live for others to watch!");


                ImGui.Dummy(
                    UiVec(0f, 16f));


                //
                // =====================================================
                // Currently broadcasting
                // =====================================================
                //

                if (isBroadcasting)
                {
                    using (
                        ImRaii.PushFont(
                            UiBuilder.IconFont))
                    {
                        ImGui.TextColored(
                            Good,
                            FontAwesomeIcon.Circle
                                .ToIconString());
                    }

                    ImGui.SameLine(
                        0f,
                        8f);

                    ImGui.TextColored(
                                       Good,
                                       isUploading
                                           ? "LIVE"
                                           : "BROADCASTING");


                    ImGui.Dummy(
                        UiVec(0f, 12f));


                    if (DrawSnesSecondaryButton(
                      FontAwesomeIcon.Stop,
                      "Stop Broadcast",
                      UiVec(170f, 36f)))
                    {
                        StopGameWatchPartyBroadcast();
                    }


                    // Testing only: retain the viewer URL control for diagnostics.
                    /*
                    if (CurrentSession is { } liveSession)
                    {
                        ImGui.SameLine(
                            0f,
                            10f);


                        if (DrawSnesSecondaryButton(
                                FontAwesomeIcon.Copy,
                                "Copy Viewer URL",
                                UiVec(180f, 36f)))
                        {
                            ImGui.SetClipboardText(
                                BuildMyHlsUrl(
                                    liveSession));
                        }
                    }
                    */


                    return;
                }


                //
                // =====================================================
                // Broadcast availability
                // =====================================================
                //

                ImGui.TextColored(
                    MutedText,
                    "To begin broadcasting you'll need to be hosting a Watch Party and the game running.");

                ImGui.Dummy(
                    UiVec(0f, 10f));

                var hostingWatchParty =
                    stream.Mode ==
                    StreamMode.Hosting;

                var signedIn =
                    CurrentSession is not null;

                var streamKey =
                    CurrentSession is { } session
                        ? Plugin.Cfg.StreamKeys
                            .GetValueOrDefault(
                                session.AccountId)
                        : null;

                var hasStreamKey =
                    !string.IsNullOrWhiteSpace(
                        streamKey);

                var canBeginBroadcast =
          hostingWatchParty &&
          isPlaying &&
          signedIn &&
          hasStreamKey;

                if (signedIn &&
                    !hasStreamKey)
                {
                    ImGui.TextColored(
                        Gold,
                        "Generate a secret stream key in Settings > Account before live streaming is available.");

                    ImGui.Dummy(
                        UiVec(0f, 8f));
                }

                using (ImRaii.Disabled(
                           !canBeginBroadcast))
                {
                    if (DrawSnesPrimaryAction(
                            FontAwesomeIcon.BroadcastTower,
                            "Start Broadcast",
                            !canBeginBroadcast))
                    {
                        StartGameWatchPartyBroadcast();
                    }
                }

                if (!hostingWatchParty &&
                    ImGui.IsItemHovered(
                        ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(
                        "Create or join as host of a Watch Party before broadcasting.");
                }
                else if (!isPlaying &&
                         ImGui.IsItemHovered(
                             ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(
                        $"Start a {systemName} game before broadcasting.");
                }
                else if (!hasStreamKey &&
            ImGui.IsItemHovered(
                ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(
                        "Generate a secret stream key in Settings > Account first.");
                }

            });
    }

    private bool StartGameWatchPartyBroadcast()
    {
        if (!HasConfirmedPatreonAccess())
        {
            gamePatreonAccessMessage =
                "A Patreon membership is required to broadcast gameplay.";

            Plugin.ChatGui.Print(
                "[AlphaChannel] A Patreon membership is required to broadcast gameplay.");

            return false;
        }

        if (stream.Mode !=
                   StreamMode.Hosting)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Host a Watch Party before broadcasting gameplay.");

            return false;
        }

        var engine =
            screenController.Engine;

        var isSnes =
            engine.IsPlayingSnes;

        var isGameBoy =
            engine.IsPlayingGameBoy;

        var isNes =
            engine.IsPlayingNes;

        var isGameBoyAdvance =
            engine.IsPlayingGameBoyAdvance;

        var isMasterSystem =
            engine.IsPlayingMasterSystem;

        var isGameGear =
            engine.IsPlayingGameGear;

        if (!isSnes &&
            !isGameBoy &&
            !isNes &&
            !isGameBoyAdvance &&
            !isMasterSystem &&
            !isGameGear)
        {
            return false;
        }

        if (CurrentSession is not { } session)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Sign in before broadcasting gameplay.");

            return false;
        }

        var streamKey =
     Plugin.Cfg.StreamKeys
         .GetValueOrDefault(
             session.AccountId);

        if (string.IsNullOrWhiteSpace(
                streamKey))
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] No stream key is available. Generate one from Settings > Account first.");

            return false;
        }

        //
        // Stop any previous encoder instance before arming a new source.
        //
        StopGameBroadcastEncoder();

        gameBroadcastForSnes =
            isSnes;

        gameBroadcastSystem = isSnes ? GameSystem.Snes : isNes ? GameSystem.Nes : isGameBoyAdvance ? GameSystem.GameBoyAdvance : isMasterSystem ? GameSystem.MasterSystem : isGameGear ? GameSystem.GameGear : GameSystem.GameBoy;

        gameBroadcastPublishUrl =
            $"{BuildRtmpServer()}/{streamKey}";

        gameBroadcastHlsUrl =
            $"{BuildMyHlsUrl(session)}" +
            $"?broadcast={Guid.NewGuid():N}";

        gameBroadcastStartFailed =
            false;

        gameBroadcastViewerReadyUtc =
            DateTime.MaxValue;

        gameBroadcastArmed =
            true;

        if (isSnes)
        {
            snesLaunchError =
                null;
        }
        else
        {
            if (isNes) nesLaunchError = null;
            else if (isGameBoyAdvance) gameBoyAdvanceLaunchError = null;
            else if (isMasterSystem) masterSystemLaunchError = null;
            else if (isGameGear) gameGearLaunchError = null;
            else gameBoyLaunchError = null;
        }

        //
        // Publish the game status immediately, but do not advertise the HLS
        // URL until FFmpeg has started and MediaMTX has had time to create its
        // first playlist segment.
        //
        _ = PublishGameplayWatchPartyAsync(
            null);

        Plugin.ChatGui.Print(
            "[AlphaChannel] Gameplay broadcast enabled. Uploading will begin when a viewer joins.");

        return true;
    }

    internal void UpdateGameBroadcastDemand()
    {
        UpdateBrowserBroadcastDemand();
        if (!gameBroadcastArmed)
        {
            return;
        }

        var engine =
            screenController.Engine;

        var sourceStillRunning = gameBroadcastSystem switch
        {
            GameSystem.Snes => engine.IsPlayingSnes,
            GameSystem.Nes => engine.IsPlayingNes,
            GameSystem.GameBoyAdvance => engine.IsPlayingGameBoyAdvance,
            GameSystem.MasterSystem => engine.IsPlayingMasterSystem,
            GameSystem.GameGear => engine.IsPlayingGameGear,
            _ => engine.IsPlayingGameBoy
        };

        //
        // Disarm if the room ended, hosting transferred away, or the
        // associated emulator stopped.
        //
        if (stream.Mode != StreamMode.Hosting ||
     !sourceStillRunning ||
     string.IsNullOrWhiteSpace(
         gameBroadcastPublishUrl))
        {
            StopGameWatchPartyBroadcast();
            return;
        }

        //
        // StreamClient.Roster contains viewers, not the local host.
        //
        var hasViewer =
            stream.Roster.Length > 0;

        var encoderRunning = gameBroadcastSystem switch
        {
            GameSystem.Snes => engine.IsSnesBroadcasting,
            GameSystem.Nes => engine.IsNesBroadcasting,
            GameSystem.GameBoyAdvance => engine.IsGameBoyAdvanceBroadcasting,
            GameSystem.MasterSystem => engine.IsMasterSystemBroadcasting,
            GameSystem.GameGear => engine.IsGameGearBroadcasting,
            _ => engine.IsGameBoyBroadcasting
        };

        if (!hasViewer)
        {
            if (encoderRunning)
            {
                StopGameBroadcastEncoder();
            }

            gameBroadcastViewerReadyUtc =
                DateTime.MaxValue;

            //
            // Permit one new start attempt when another viewer arrives.
            //
            gameBroadcastStartFailed =
                false;

            return;
        }

        if (encoderRunning ||
            gameBroadcastStartFailed)
        {
            return;
        }

        var started = gameBroadcastSystem switch
        {
            GameSystem.Snes => engine.StartSnesBroadcast(gameBroadcastPublishUrl),
            GameSystem.Nes => engine.StartNesBroadcast(gameBroadcastPublishUrl),
            GameSystem.GameBoyAdvance => engine.StartGameBoyAdvanceBroadcast(gameBroadcastPublishUrl),
            GameSystem.MasterSystem => engine.StartMasterSystemBroadcast(gameBroadcastPublishUrl),
            GameSystem.GameGear => engine.StartGameGearBroadcast(gameBroadcastPublishUrl),
            _ => engine.StartGameBoyBroadcast(gameBroadcastPublishUrl)
        };

        if (started)
        {
            gameBroadcastViewerReadyUtc =
                DateTime.UtcNow +
                GameBroadcastViewerWarmup;

            if (gameBroadcastSystem == GameSystem.Snes) snesLaunchError = null;
            else if (gameBroadcastSystem == GameSystem.Nes) nesLaunchError = null;
            else if (gameBroadcastSystem == GameSystem.GameBoyAdvance) gameBoyAdvanceLaunchError = null;
            else if (gameBroadcastSystem == GameSystem.MasterSystem) masterSystemLaunchError = null;
            else if (gameBroadcastSystem == GameSystem.GameGear) gameGearLaunchError = null;
            else gameBoyLaunchError = null;

            Plugin.ChatGui.Print(
                "[AlphaChannel] A viewer joined. Gameplay upload started.");

            return;
        }

        gameBroadcastStartFailed =
            true;

        var error =
            engine.LastError ??
            "Gameplay broadcast failed to start.";

        if (gameBroadcastSystem == GameSystem.Snes) snesLaunchError = error;
        else if (gameBroadcastSystem == GameSystem.Nes) nesLaunchError = error;
        else if (gameBroadcastSystem == GameSystem.GameBoyAdvance) gameBoyAdvanceLaunchError = error;
        else if (gameBroadcastSystem == GameSystem.MasterSystem) masterSystemLaunchError = error;
        else if (gameBroadcastSystem == GameSystem.GameGear) gameGearLaunchError = error;
        else gameBoyLaunchError = error;
    }

    private void StopGameBroadcastEncoder()
    {
        var engine =
            screenController.Engine;

        if (engine.IsSnesBroadcasting)
        {
            engine.StopSnesBroadcast();
        }

        if (engine.IsGameBoyBroadcasting)
        {
            engine.StopGameBoyBroadcast();
        }

        if (engine.IsNesBroadcasting)
        {
            engine.StopNesBroadcast();
        }

        if (engine.IsGameBoyAdvanceBroadcasting)
        {
            engine.StopGameBoyAdvanceBroadcast();
        }

        if (engine.IsMasterSystemBroadcasting)
        {
            engine.StopMasterSystemBroadcast();
        }

        if (engine.IsGameGearBroadcasting)
        {
            engine.StopGameGearBroadcast();
        }
    }

    private void StopGameWatchPartyBroadcast()
    {
        StopGameBroadcastEncoder();

        gameBroadcastArmed =
            false;

        gameBroadcastStartFailed =
            false;

        gameBroadcastPublishUrl =
            null;

        gameBroadcastHlsUrl =
            null;

        gameBroadcastViewerReadyUtc =
            DateTime.MaxValue;
    }
    private string GetSelectedGameName()
    {
        var path =
            selectedGameSystem ==
                GameSystem.Snes
                ? snesSelectedRomPath
                : gameBoySelectedRomPath;

        if (string.IsNullOrWhiteSpace(
                path))
        {
            return selectedGameSystem ==
                   GameSystem.Snes
                ? "SNES Game"
                : "Game Boy Game";
        }

        return LibraryGameName(
            path);
    }


    private void DrawSnesStatusLine(
        string label,
        string value)
    {
        ImGui.TextColored(
            MutedText,
            label);

        var valueWidth =
            ImGui.CalcTextSize(
                value).X;

        var right =
            ImGui.GetWindowContentRegionMax().X;

        var currentY =
            ImGui.GetCursorPosY() -
            ImGui.GetTextLineHeightWithSpacing();

        ImGui.SetCursorPos(
            new Vector2(
                MathF.Max(
                    ImGui.GetCursorPosX(),
                    right -
                    valueWidth),
                currentY));

        ImGui.TextUnformatted(
            value);
    }


    private static void DrawSnesStatusSeparator()
    {
        ImGui.Dummy(
            UiVec(0, 6));

        ImGui.Separator();

        ImGui.Dummy(
            UiVec(0, 6));
    }


    // =============================================================
    // Controls + Audio
    // =============================================================

    private void DrawSnesControlsAudioPanel(
    bool isPlaying,
    bool controlsEnabled)
    {
        var isSnes =
            selectedGameSystem ==
            GameSystem.Snes;

        var systemName =
            isSnes
                ? "SNES"
                : "Game Boy";

        var controlLabel =
            $"Control {systemName}";


                DrawSnesPanel(
            "##gamesControlsAudio",
            UiVec(-1f, 515f),
                                            () =>
            {
                DrawSnesSectionHeader(
                    FontAwesomeIcon.Gamepad,
                    "Controls & Audio",
                    $"Choose where input goes and adjust your {systemName} session.");

                ImGui.Dummy(
                    UiVec(0f, 18f));


                var available =
                    ImGui.GetContentRegionAvail().X;

                const float gap =
                    14f;

                var half =
                    (available -
                     gap) /
                    2f;


                //
                // =====================================================
                // Input control card
                // =====================================================
                //

                                DrawSnesInnerCard(
                    "##gamesInputCard",
                    new Vector2(
                        half,
                        Ui(390f)),
                                                                                    () =>
                    {
                        ImGui.TextUnformatted(
                            "Input Control");

                        ImGui.Dummy(
                            UiVec(0f, 4f));

                        ImGui.TextColored(
                            MutedText,
                            "Choose whether keyboard and controller input");

                        ImGui.TextColored(
                            MutedText,
                            $"controls the {systemName} or Final Fantasy XIV.");


                        ImGui.Dummy(
                            UiVec(0f, 14f));


                        var controlWidth =
                            MathF.Max(
                                120f,
                                (ImGui.GetContentRegionAvail().X -
                                 8f) /
                                2f);


                        //
                        // Emulator
                        //

                        using (
                            ImRaii.Disabled(
                                !isPlaying))
                        {
                            if (DrawSnesSegmentButton(
                                    controlLabel,
                                    controlsEnabled &&
                                    isPlaying,
                                    new Vector2(
                                        controlWidth,
                                        Ui(36f))))
                            {
                                if (isSnes)
                                {
                                    screenController.Engine
                                        .SetSnesControlsEnabled(
                                            true);
                                }
                                else
                                {
                                    screenController.Engine
                                        .SetGameBoyControlsEnabled(
                                            true);
                                }
                            }
                        }


                        ImGui.SameLine(
                            0f,
                            8f);


                        //
                        // FFXIV
                        //

                        if (DrawSnesSegmentButton(
                                "Control FFXIV",
                                !controlsEnabled ||
                                !isPlaying,
                                new Vector2(
                                    controlWidth,
                                    Ui(36f))))
                        {
                            if (isSnes)
                            {
                                screenController.Engine
                                    .SetSnesControlsEnabled(
                                        false);
                            }
                            else
                            {
                                screenController.Engine
                                    .SetGameBoyControlsEnabled(
                                        false);
                            }
                        }


                        ImGui.Dummy(
                            UiVec(0f, 11f));


                        ImGui.TextColored(
                            isPlaying &&
                            controlsEnabled
                                ? Good
                                : MutedText,
                            isPlaying &&
                            controlsEnabled
                                ? $"Currently controlling: {systemName}"
                                : "Currently controlling: FFXIV");


                        ImGui.Dummy(
                            UiVec(0f, 13f));


                        //
                        // =====================================================
                        // Full FFXIV keyboard lock
                        // =====================================================
                        //

                        var blockAllFfxivInput =
                            screenController.Engine
                                .BlockAllFfxivKeyboardInput;


                        using (
                            ImRaii.PushColor(
                                ImGuiCol.FrameBg,
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.16f))
                                .Push(
                                    ImGuiCol.FrameBgHovered,
                                    new Vector4(
                                        Accent.X,
                                        Accent.Y,
                                        Accent.Z,
                                        0.26f))
                                .Push(
                                    ImGuiCol.FrameBgActive,
                                    new Vector4(
                                        Accent.X,
                                        Accent.Y,
                                        Accent.Z,
                                        0.34f))
                                .Push(
                                    ImGuiCol.CheckMark,
                                    Accent))
                        {
                            if (ImGui.Checkbox(
                                    "Disable all FFXIV input when controlling game",
                                    ref blockAllFfxivInput))
                            {
                                screenController.Engine
                                    .SetBlockAllFfxivKeyboardInput(
                                        blockAllFfxivInput);
                            }
                        }


                        ImGui.Dummy(
                            UiVec(0f, 5f));


                        ImGui.PushTextWrapPos(
                            ImGui.GetCursorPosX() +
                            ImGui.GetContentRegionAvail().X);

                        SetUiFontScale(
                            0.76f);

                        ImGui.TextColored(
                            MutedText,
                            "Warning: When enabled, all keyboard input is blocked from FFXIV while game controls are active. " +
                            "When disabled, only keys assigned to the game are blocked.");

                        SetUiFontScale(
                            1f);

                        ImGui.PopTextWrapPos();


                        if (blockAllFfxivInput)
                        {
                            ImGui.Dummy(
                                UiVec(0f, 7f));

                            ImGui.PushTextWrapPos(
                                ImGui.GetCursorPosX() +
                                ImGui.GetContentRegionAvail().X);

                            ImGui.TextColored(
                                Gold,
                                "Need to force control back to FFXIV? Press Ctrl + F12 to force reset controls.");

                            ImGui.PopTextWrapPos();
                        }
                    });


                ImGui.SameLine(
                    0f,
                    gap);


                //
                // =====================================================
                // Audio card
                // =====================================================
                //

                                DrawSnesInnerCard(
                    "##gamesAudioCard",
                    new Vector2(
                        half,
                        Ui(390f)),
                                                                                                  () =>
                    {
                        ImGui.TextUnformatted(
                            "Audio");

                        ImGui.Dummy(
                            UiVec(0f, 7f));


                        ImGui.TextUnformatted(
                            "Adjust volume and FFXIV audio");

                        ImGui.Dummy(
                            UiVec(0f, 3f));

                        ImGui.TextColored(
                            MutedText,
                            "FFXIV audio will return to previous levels when unmuted.");

                        ImGui.Dummy(
                            UiVec(0f, 12f));


                        //
                        // Audio controls row
                        //

                        var audioRowWidth =
                            ImGui.GetContentRegionAvail().X;

                        var muteButtonWidth = Ui(135f);

                        var rowGap = Ui(18f);

                        var volume =
                            Plugin.Cfg.Volume;

                        var volumeText =
                            $"{volume}%";

                        var volumeTextWidth =
                            ImGui.CalcTextSize(
                                volumeText).X;

                        var sliderWidth =
                            MathF.Max(
                                110f,
                                audioRowWidth -
                                muteButtonWidth -
                                rowGap -
                                volumeTextWidth -
                                12f);


                        //
                        // =====================================================
                        // FFXIV mute + emulator volume
                        // =====================================================
                        //

                        var ffxivMuted =
                            IsFfxivSoundMuted();


                        if (DrawSnesSecondaryButton(
                                ffxivMuted
                                    ? FontAwesomeIcon.VolumeUp
                                    : FontAwesomeIcon.VolumeMute,
                                ffxivMuted
                                    ? "Restore FFXIV"
                                    : "Mute FFXIV",
                                new Vector2(
                                    muteButtonWidth,
                                    Ui(32f))))
                        {
                            SetFfxivSoundMuted(
                                !ffxivMuted);
                        }


                        ImGui.SameLine(
                            0f,
                            rowGap);


                        //
                        // Emulator volume
                        //

                        ImGui.SetNextItemWidth(
                            sliderWidth);


                        using (
                            ImRaii.PushColor(
                                ImGuiCol.FrameBg,
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.16f))
                                .Push(
                                    ImGuiCol.FrameBgHovered,
                                    new Vector4(
                                        Accent.X,
                                        Accent.Y,
                                        Accent.Z,
                                        0.24f))
                                .Push(
                                    ImGuiCol.FrameBgActive,
                                    new Vector4(
                                        Accent.X,
                                        Accent.Y,
                                        Accent.Z,
                                        0.30f))
                                .Push(
                                    ImGuiCol.SliderGrab,
                                    Accent)
                                .Push(
                                    ImGuiCol.SliderGrabActive,
                                    AccentHover))
                        {
                            if (ImGui.SliderInt(
                                    "##gamesVolume",
                                    ref volume,
                                    0,
                                    130,
                                    ""))
                            {
                                Plugin.Cfg.Volume =
                                    volume;

                                if (volume > 0 &&
                                    Plugin.Cfg.Muted)
                                {
                                    Plugin.Cfg.Muted =
                                        false;
                                }

                                video.SetVolume(
                                    Plugin.Cfg.Muted
                                        ? 0
                                        : volume);
                            }
                        }


                        if (ImGui.IsItemDeactivatedAfterEdit())
                        {
                            Plugin.Cfg.Save();
                        }


                        ImGui.SameLine(
                            0f,
                            8f);

                        ImGui.TextUnformatted(
                            volumeText);


                        //
                        // =====================================================
                        // TV mute
                        // =====================================================
                        //

                        ImGui.Dummy(
                            UiVec(0f, 12f));


                        var tvMuted =
                            Plugin.Cfg.Muted;


                        if (DrawSnesSecondaryButton(
                                tvMuted
                                    ? FontAwesomeIcon.VolumeUp
                                    : FontAwesomeIcon.VolumeMute,
                                tvMuted
                                    ? "Unmute TV"
                                    : "Mute TV",
                                new Vector2(
                                    muteButtonWidth,
                                    Ui(32f))))
                        {
                            tvMuted =
                                !tvMuted;

                            Plugin.Cfg.Muted =
                                tvMuted;

                            video.SetVolume(
                                tvMuted
                                    ? 0
                                    : Plugin.Cfg.Volume);

                            Plugin.Cfg.Save();
                        }


                        ImGui.Dummy(
       UiVec(0f, 12f));

                        ImGui.Separator();

                        ImGui.Dummy(
                            UiVec(0f, 8f));


                        //
                        // =====================================================
                        // Display
                        // =====================================================
                        //

                        ImGui.TextUnformatted(
                            "Display");

                        ImGui.Dummy(
                            UiVec(0f, 10f));


                        var crtEnabled =
                            isSnes
                                ? screenController.Engine
                                    .SnesCrtFilterEnabled
                                : screenController.Engine
                                    .GameBoyCrtFilterEnabled;


                        using (
                            ImRaii.PushColor(
                                ImGuiCol.FrameBg,
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.16f))
                                .Push(
                                    ImGuiCol.FrameBgHovered,
                                    new Vector4(
                                        Accent.X,
                                        Accent.Y,
                                        Accent.Z,
                                        0.26f))
                                .Push(
                                    ImGuiCol.FrameBgActive,
                                    new Vector4(
                                        Accent.X,
                                        Accent.Y,
                                        Accent.Z,
                                        0.34f))
                                .Push(
                                    ImGuiCol.CheckMark,
                                    Accent))
                        {
                            if (ImGui.Checkbox(
                                    "CRT Filter",
                                    ref crtEnabled))
                            {
                                if (isSnes)
                                {
                                    screenController.Engine
                                        .SetSnesCrtFilterEnabled(
                                            crtEnabled);
                                }
                                else
                                {
                                    screenController.Engine
                                        .SetGameBoyCrtFilterEnabled(
                                            crtEnabled);
                                }
                            }
                        }


                        ImGui.SameLine(
                            0f,
                            12f);

                        ImGui.TextColored(
                            MutedText,
                            isSnes
                                ? "Apply a CRT screen style filter while playing SNES games."
                                : "Apply a CRT screen style filter while playing Game Boy games.");
                    });


             
            });
    }


    // =============================================================
    // Binding panel
    // =============================================================

    private void DrawSnesBindingsPanel()
    {
        var isSnes =
            selectedGameSystem ==
            GameSystem.Snes;

        var systemName =
            isSnes
                ? "SNES"
                : "Game Boy";

        DrawSnesPanel(
            "##gamesBindingsPanel",
new Vector2(
    -1f,
    isSnes
        ? Ui(525f)
        : Ui(440f)),
            () =>
            {
                //
                // =====================================================
                // Header
                // =====================================================
                //

                ImGui.BeginGroup();

                ImGui.TextUnformatted(
                    $"{systemName} Controls");

                ImGui.Dummy(
                    UiVec(0f, 3f));

                ImGui.TextColored(
                    MutedText,
                    isSnes
                        ? "Keyboard bindings for Player 1."
                        : "Keyboard bindings used for Game Boy and Game Boy Color.");

                ImGui.EndGroup();


                //
                // Configure button
                //

                var buttonWidth = Ui(165f);

                var right =
                    ImGui.GetWindowContentRegionMax().X;

                var originalY =
                    ImGui.GetCursorPosY();

                ImGui.SetCursorPos(
                    new Vector2(
                        right -
                        buttonWidth,
                        originalY -
                        Ui(43f)));

                if (DrawSnesSecondaryButton(
                        FontAwesomeIcon.Cog,
                        "Configure Keyboard",
                        new Vector2(
                            buttonWidth,
                            Ui(32f))))
                {
                    snesControlsPopupRequested =
                        true;
                }


                ImGui.SetCursorPosY(
                    originalY);

                ImGui.Dummy(
                    UiVec(0f, 12f));


                //
                // =====================================================
                // Keyboard + controller notice
                // =====================================================
                //

                using (
                    ImRaii.PushStyle(
                        ImGuiStyleVar.WindowPadding,
                        UiVec(12f, 10f)))
                using (
                    ImRaii.PushStyle(
                        ImGuiStyleVar.ChildRounding,
                        8f))
                using (
                    ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.07f)))
                using (
                    ImRaii.PushColor(
                        ImGuiCol.Border,
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.35f)))
                using (
                    var inputNotice =
                        ImRaii.Child(
                            "##gamesInputSupportNotice",
                            new Vector2(
                                -1f,
                                Ui(64f)),
                            true,
                            ImGuiWindowFlags.NoScrollbar |
                            ImGuiWindowFlags.NoScrollWithMouse))
                {
                    if (inputNotice)
                    {
                        using (
                            ImRaii.PushFont(
                                UiBuilder.IconFont))
                        {
                            ImGui.TextColored(
                                Accent,
                                FontAwesomeIcon.Gamepad
                                    .ToIconString());
                        }

                        ImGui.SameLine(
                            0f,
                            8f);

                        ImGui.TextColored(
                            Accent,
                            "Keyboard & Controller Supported");

                        ImGui.Dummy(
                            UiVec(0f, 3f));

                        SetUiFontScale(
                            0.76f);

                        ImGui.TextColored(
                            MutedText,
                            "Use the keyboard bindings below, or play with a connected game controller.");

                        SetUiFontScale(
                            1f);
                    }
                }


                ImGui.Dummy(
                    UiVec(0f, 10f));


                //
                // =====================================================
                // Duplicate warning
                // =====================================================
                //

                if (HasDuplicateGameKeyBindings())
                {
                    using (
                        ImRaii.PushStyle(
                            ImGuiStyleVar.WindowPadding,
                            UiVec(10f, 8f)))
                    using (
                        ImRaii.PushStyle(
                            ImGuiStyleVar.ChildRounding,
                            7f))
                    using (
                        ImRaii.PushColor(
                            ImGuiCol.ChildBg,
                            new Vector4(
                                Gold.X,
                                Gold.Y,
                                Gold.Z,
                                0.08f)))
                    using (
                        ImRaii.PushColor(
                            ImGuiCol.Border,
                            new Vector4(
                                Gold.X,
                                Gold.Y,
                                Gold.Z,
                                0.45f)))
                    using (
                        var warning =
                            ImRaii.Child(
                                "##gamesDuplicateBindingsWarning",
                                new Vector2(
                                    -1f,
                                    Ui(48f)),
                                true,
                                ImGuiWindowFlags.NoScrollbar |
                                ImGuiWindowFlags.NoScrollWithMouse))
                    {
                        if (warning)
                        {
                            using (
                                ImRaii.PushFont(
                                    UiBuilder.IconFont))
                            {
                                ImGui.TextColored(
                                    Gold,
                                    FontAwesomeIcon.ExclamationTriangle
                                        .ToIconString());
                            }

                            ImGui.SameLine(
                                0f,
                                7f);

                            ImGui.TextColored(
                                Gold,
                                "Duplicate keybindings detected");

                            SetUiFontScale(
                                0.72f);

                            ImGui.TextColored(
                                MutedText,
                                isSnes
                                    ? "Two or more SNES controls are using the same keyboard key."
                                    : "Two or more Game Boy controls are using the same keyboard key.");

                            SetUiFontScale(
                                1f);
                        }
                    }

                    ImGui.Dummy(
                        UiVec(0f, 8f));
                }


                //
                // =====================================================
                // Bindings
                // =====================================================
                //

                if (isSnes)
                {
                    //
                    // SNES uses all 12 configured controls.
                    //

                    if (ImGui.BeginTable(
                            "##snesBindingsTable",
                            3,
                            ImGuiTableFlags.SizingStretchSame))
                    {
                        ImGui.TableNextColumn();

                        DrawSnesBindingRow(
                            "D-Pad Up",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyUp));

                        DrawSnesBindingRow(
                            "D-Pad Down",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyDown));

                        DrawSnesBindingRow(
                            "D-Pad Left",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyLeft));

                        DrawSnesBindingRow(
                            "D-Pad Right",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyRight));


                        ImGui.TableNextColumn();

                        DrawSnesBindingRow(
                            "A Button",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyA));

                        DrawSnesBindingRow(
                            "B Button",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyB));

                        DrawSnesBindingRow(
                            "X Button",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyX));

                        DrawSnesBindingRow(
                            "Y Button",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyY));


                        ImGui.TableNextColumn();

                        DrawSnesBindingRow(
                            "L Button",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyL));

                        DrawSnesBindingRow(
                            "R Button",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyR));

                        DrawSnesBindingRow(
                            "Start",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyStart));

                        DrawSnesBindingRow(
                            "Select",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeySelect));


                        ImGui.EndTable();
                    }
                }
                else
                {
                    //
                    // Game Boy reuses the matching SNES keyboard
                    // settings, but only exposes controls that actually
                    // exist on a Game Boy.
                    //

                    if (ImGui.BeginTable(
                            "##gameBoyBindingsTable",
                            2,
                            ImGuiTableFlags.SizingStretchSame))
                    {
                        ImGui.TableNextColumn();

                        DrawSnesBindingRow(
                            "D-Pad Up",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyUp));

                        DrawSnesBindingRow(
                            "D-Pad Down",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyDown));

                        DrawSnesBindingRow(
                            "D-Pad Left",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyLeft));

                        DrawSnesBindingRow(
                            "D-Pad Right",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyRight));


                        ImGui.TableNextColumn();

                        DrawSnesBindingRow(
                            "A Button",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyA));

                        DrawSnesBindingRow(
                            "B Button",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyB));

                        DrawSnesBindingRow(
                            "Start",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeyStart));

                        DrawSnesBindingRow(
                            "Select",
                            GetSnesKeyDisplayName(
                                (VirtualKey)Plugin.Cfg.SnesKeySelect));


                        ImGui.EndTable();
                    }
                }
            });
    }

    private bool HasDuplicateGameKeyBindings()
    {
        int[] bindings;

        if (selectedGameSystem ==
            GameSystem.Snes)
        {
            bindings =
            [
                Plugin.Cfg.SnesKeyUp,
            Plugin.Cfg.SnesKeyDown,
            Plugin.Cfg.SnesKeyLeft,
            Plugin.Cfg.SnesKeyRight,

            Plugin.Cfg.SnesKeyA,
            Plugin.Cfg.SnesKeyB,
            Plugin.Cfg.SnesKeyX,
            Plugin.Cfg.SnesKeyY,

            Plugin.Cfg.SnesKeyL,
            Plugin.Cfg.SnesKeyR,

            Plugin.Cfg.SnesKeyStart,
            Plugin.Cfg.SnesKeySelect
            ];
        }
        else
        {
            //
            // Game Boy only uses these eight controls.
            //

            bindings =
            [
                Plugin.Cfg.SnesKeyUp,
            Plugin.Cfg.SnesKeyDown,
            Plugin.Cfg.SnesKeyLeft,
            Plugin.Cfg.SnesKeyRight,

            Plugin.Cfg.SnesKeyA,
            Plugin.Cfg.SnesKeyB,

            Plugin.Cfg.SnesKeyStart,
            Plugin.Cfg.SnesKeySelect
            ];
        }

        return bindings
            .GroupBy(
                key => key)
            .Any(
                group =>
                    group.Count() > 1);
    }


    private void DrawSnesBindingRow(
        string action,
        string key)
    {
        const float height =
            35f;

        var origin =
            ImGui.GetCursorScreenPos();

        var width =
            ImGui.GetContentRegionAvail().X -
            6f;

        var drawList =
            ImGui.GetWindowDrawList();

        var min =
            origin;

        var max =
            origin +
            new Vector2(
                width,
                height);


        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    FrameBg.X,
                    FrameBg.Y,
                    FrameBg.Z,
                    0.72f)),
            8f);

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                BorderSubtle),
            8f);


        //
        // Action label
        //

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
    origin +
    UiVec(11, 8),
            ImGui.GetColorU32(
                Vector4.One),
            action);


        //
        // Key badge
        //

        var keyTextWidth =
            ImGui.CalcTextSize(
                key).X;

        var badgeWidth =
            keyTextWidth +
            20f;

        var badgeMin =
            new Vector2(
                max.X -
                badgeWidth -
                Ui(7f),
                min.Y + Ui(5f));

        var badgeMax =
            new Vector2(
                max.X - Ui(7f),
                max.Y - Ui(5f));


        drawList.AddRectFilled(
            badgeMin,
            badgeMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.16f)),
            6f);

        drawList.AddRect(
            badgeMin,
            badgeMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.52f)),
            6f);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                badgeMin.X +
                (badgeWidth -
                 keyTextWidth) /
                2f,
                badgeMin.Y + Ui(4f)),
                    ImGui.GetColorU32(
                Accent),
            key);


        ImGui.Dummy(
            new Vector2(
                width,
                height));

        ImGui.Dummy(
            UiVec(0, 4));
    }


    // =============================================================
    // Save data panel
    // =============================================================

    private void DrawSnesSavePanel()
    {
        const float height =
            92f;

        using var child =
            ImRaii.Child(
                "##snesSavePanel",
                new Vector2(
                    -1,
                    height),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse);

        if (!child)
        {
            return;
        }


        var pos =
            ImGui.GetWindowPos();

        var size =
            ImGui.GetWindowSize();

        var drawList =
            ImGui.GetWindowDrawList();


        drawList.AddRectFilled(
            pos,
            pos + size,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X * 0.11f,
                    Accent.Y * 0.10f,
                    Accent.Z * 0.18f,
                    0.95f)),
            13f);

        drawList.AddRect(
            pos,
            pos + size,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.62f)),
            13f,
            ImDrawFlags.None,
            1f);


        //
        // Save icon
        //

        var iconCenter =
            pos +
            new Vector2(
                Ui(31),
                height / 2f);

        drawList.AddCircleFilled(
            iconCenter,
            17f,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.18f)),
            32);

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            var icon =
                FontAwesomeIcon.Save
                    .ToIconString();

            var iconSize =
                ImGui.CalcTextSize(
                    icon);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconCenter -
                iconSize / 2f,
                ImGui.GetColorU32(
                    Accent),
                icon);
        }


        //
        // Copy
        //

        var x =
            pos.X + 61f;

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                x,
                pos.Y + Ui(18f)),
            ImGui.GetColorU32(
                Accent),
            "Save Data");

        var saveSystemName =
     selectedGameSystem ==
         GameSystem.Snes
         ? "SNES"
         : "Game Boy";

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                x,
                pos.Y + Ui(43f)),
            ImGui.GetColorU32(
                MutedText),
            $"Game saves are stored alongside your {saveSystemName} game files.");

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                x,
                pos.Y + Ui(64f)),
            ImGui.GetColorU32(
                MutedText),
            "If you move a ROM later, remember that its save data may need to move with it.");
    }

    // =============================================================
    // ROM sources information
    // =============================================================

    private void DrawSnesRomSourcesPopup()
    {
        if (snesRomSourcesPopupRequested)
        {
            ImGui.OpenPopup(
                "Finding SNES Games##snesRomSourcesPopup");

            snesRomSourcesPopupRequested =
                false;
        }

        ImGui.SetNextWindowSize(
            UiVec(680f, 470f),
            ImGuiCond.Appearing);

        var popupOpen =
            true;

        if (!ImGui.BeginPopupModal(
                "Finding SNES Games##snesRomSourcesPopup",
                ref popupOpen,
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoResize))
        {
            return;
        }


        //
        // Header
        //

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            ImGui.TextColored(
                Accent,
                FontAwesomeIcon.Globe
                    .ToIconString());
        }

        ImGui.SameLine(
            0f,
            8f);

        SetUiFontScale(
            1.15f);

        ImGui.TextUnformatted(
            "Finding SNES Games Online");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 8f));


        //
        // Main information
        //

        ImGui.TextWrapped(
            "It is possible to obtain SNES ROMs from many websites, which can be found using your preferred search engine.");

        ImGui.Dummy(
            UiVec(0f, 10f));

        ImGui.TextColored(
            Accent,
            "SNES file type");

        ImGui.Dummy(
            UiVec(0f, 8f));


        ImGui.TextWrapped(
            "An SNES ROM should normally use the .sfc or .smc file extension. " +
            "Checking the extension can help you identify whether a downloaded file is likely to be for the correct console.");

        ImGui.Dummy(UiVec(0f, 8f));

        ImGui.TextWrapped(
            "ROM downloads may be supplied inside a .zip file. If so, extract the ZIP file first, " +
            "then import the ROM file contained inside it.");


        ImGui.Dummy(
            UiVec(0f, 14f));


        //
        // Safety notice
        //

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(12f, 10f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    Gold.X,
                    Gold.Y,
                    Gold.Z,
                    0.07f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                new Vector4(
                    Gold.X,
                    Gold.Y,
                    Gold.Z,
                    0.35f)))
        using (
            var warning =
                ImRaii.Child(
                    "##snesRomSafetyNotice",
                    new Vector2(
                        -1f,
                        Ui(92f)),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (warning)
            {
                using (
                    ImRaii.PushFont(
                        UiBuilder.IconFont))
                {
                    ImGui.TextColored(
                        Gold,
                        FontAwesomeIcon.ExclamationTriangle
                            .ToIconString());
                }

                ImGui.SameLine(
                    0f,
                    7f);

                ImGui.TextColored(
                    Gold,
                    "Third-party download notice");

                ImGui.Dummy(
                    UiVec(0f, 4f));

                SetUiFontScale(
                    0.76f);

                ImGui.TextWrapped(
                    "Alpha Channel does not host " +
                    "these files and is not responsible for the content, safety, or " +
                    "legality of downloads from third-party websites. Only download " +
                    "ROMs you are legally permitted to use and exercise normal internet " +
                    "safety when downloading files from unfamiliar sources.");

                SetUiFontScale(
                    1f);
            }
        }


        ImGui.Dummy(
            UiVec(0f, 12f));


        //
        // Close
        //

        var closeWidth =
            120f;

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() +
            MathF.Max(
                0f,
                ImGui.GetContentRegionAvail().X -
                closeWidth));

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
        using (
            ImRaii.PushColor(
                ImGuiCol.Button,
                Accent)
                .Push(
                    ImGuiCol.ButtonHovered,
                    AccentHover)
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentActive))
        {
            if (ImGui.Button(
                    "Close",
                    new Vector2(
                        closeWidth,
                        Ui(36f))))
            {
                ImGui.CloseCurrentPopup();
            }
        }


        ImGui.EndPopup();
    }


    private void DrawSnesRomSourceButton(
        string name,
        string domain,
        string url)
    {
        const float height =
            46f;

        var width =
            ImGui.GetContentRegionAvail().X;

        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);

        var clicked =
            ImGui.InvisibleButton(
                $"##snesRomSource{name}",
                size);

        var hovered =
            ImGui.IsItemHovered();

        var drawList =
            ImGui.GetWindowDrawList();


        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                hovered
                    ? FrameBgHover
                    : FrameBg),
            8f);

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    hovered
                        ? 0.75f
                        : 0.35f)),
            8f,
            ImDrawFlags.None,
            1f);


        //
        // Link icon
        //

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                origin +
                UiVec(12f, 15f),
                ImGui.GetColorU32(
                    Accent),
                FontAwesomeIcon.ExternalLinkAlt
                    .ToIconString());
        }


        //
        // Site name + domain
        //

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            origin +
            UiVec(39f, 7f),
            ImGui.GetColorU32(
                Vector4.One),
            name);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            origin +
            UiVec(39f, 25f),
            ImGui.GetColorU32(
                MutedText),
            domain);


        //
        // Chevron
        //

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            var chevron =
                FontAwesomeIcon.ChevronRight
                    .ToIconString();

            var chevronSize =
                ImGui.CalcTextSize(
                    chevron);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    origin.X +
                    width -
                    chevronSize.X -
                    Ui(14f),
                    origin.Y +
                    (height -
                     chevronSize.Y) /
                    2f),
                ImGui.GetColorU32(
                    hovered
                        ? Accent
                        : MutedText),
                chevron);
        }


        if (clicked)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(
                        url)
                    {
                        UseShellExecute =
                            true
                    });
            }
            catch (Exception exception)
            {
                AepLog.Warning(
                    $"[SNES] Failed to open ROM source: {exception.Message}");
            }
        }
    }

    // =============================================================
    // Control configuration
    // =============================================================

    private void DrawSnesControlsPopup()
    {
        if (!snesControlsPopupRequested)
        {
            return;
        }


        var isSnes =
            selectedGameSystem ==
            GameSystem.Snes;

        var title =
            isSnes
                ? "Configure SNES Keyboard Controls"
                : "Configure Game Boy Keyboard Controls";

        var subtitle =
            isSnes
                ? "Choose the keyboard key used for each Player 1 SNES button."
                : "Choose the keyboard key used for each Game Boy button.";


        //
        // =============================================================
        // Popup dimensions
        // =============================================================
        //

        var popupWidth = Ui(620f);

        var popupHeight =
            isSnes
                ? 590f
                : 500f;


        //
        // Cover only the Alpha Channel window.
        //
        // This intentionally mirrors the username prompt overlay.
        //

        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            new Vector2(
                parentPos.X +
                (parentSize.X -
                 popupWidth) *
                0.5f,

                parentPos.Y +
                (parentSize.Y -
                 popupHeight) *
                0.5f);


        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);


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
                "##gameControlsOverlay",
                overlayFlags))
        {
            ImGui.End();

            return;
        }


        var drawList =
            ImGui.GetWindowDrawList();


        //
        // =============================================================
        // Darken Alpha Channel only
        // =============================================================
        //

        drawList.AddRectFilled(
            parentPos,
            parentPos +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.48f)));


        //
        // =============================================================
        // Popup card
        // =============================================================
        //

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);


        drawList.AddRectFilled(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    0.055f,
                    0.065f,
                    0.11f,
                    1f)),
            10f);


        drawList.AddRect(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.45f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);


        const float padding =
            20f;

        var contentWidth =
            popupWidth -
            (padding * 2f);


        //
        // =============================================================
        // Header
        // =============================================================
        //

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(17f)));


        SetUiFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            title);

        SetUiFontScale(
            1f);


        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(52f)));


        ImGui.TextColored(
            MutedText,
            subtitle);


        if (!isSnes)
        {
            ImGui.SetCursorScreenPos(
                popupPos +
                new Vector2(
                    padding,
                    Ui(77f)));

            SetUiFontScale(
                0.82f);

            ImGui.TextColored(
                MutedText,
                "Game Boy currently shares these bindings with the matching SNES controls.");

            SetUiFontScale(
                1f);
        }


        //
        // Divider
        //

        var dividerY =
            popupPos.Y +
            (isSnes
                ? 88f
                : 108f);

        drawList.AddLine(
            new Vector2(
                popupPos.X +
                padding,
                dividerY),
            new Vector2(
                popupMax.X -
                padding,
                dividerY),
            ImGui.GetColorU32(
                BorderSubtle),
            1f);


        //
        // =============================================================
        // Control configuration area
        // =============================================================
        //

        var controlsTop =
            dividerY +
            18f;

        var controlsBottom =
            popupMax.Y -
            78f;

        var controlsHeight =
            controlsBottom -
            controlsTop;


        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                controlsTop));


        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                Vector2.Zero))
        using (
            var controlsChild =
                ImRaii.Child(
                    isSnes
                        ? "##snesControlsConfigContent"
                        : "##gameBoyControlsConfigContent",
                    new Vector2(
                        contentWidth,
                        controlsHeight),
                    false))
        {
            if (controlsChild)
            {
                if (ImGui.BeginTable(
                        isSnes
                            ? "##snesControlConfigTable"
                            : "##gameBoyControlConfigTable",
                        2,
                        ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn(
                        "Control",
                        ImGuiTableColumnFlags.WidthStretch,
                        0.40f);

                    ImGui.TableSetupColumn(
                        "Key",
                        ImGuiTableColumnFlags.WidthStretch,
                        0.60f);


                    //
                    // D-pad
                    //

                    DrawSnesConfigRow(
                        "D-Pad Up",
                        Plugin.Cfg.SnesKeyUp,
                        value =>
                            Plugin.Cfg.SnesKeyUp =
                                value);

                    DrawSnesConfigRow(
                        "D-Pad Down",
                        Plugin.Cfg.SnesKeyDown,
                        value =>
                            Plugin.Cfg.SnesKeyDown =
                                value);

                    DrawSnesConfigRow(
                        "D-Pad Left",
                        Plugin.Cfg.SnesKeyLeft,
                        value =>
                            Plugin.Cfg.SnesKeyLeft =
                                value);

                    DrawSnesConfigRow(
                        "D-Pad Right",
                        Plugin.Cfg.SnesKeyRight,
                        value =>
                            Plugin.Cfg.SnesKeyRight =
                                value);


                    //
                    // A / B
                    //

                    DrawSnesConfigRow(
                        "A Button",
                        Plugin.Cfg.SnesKeyA,
                        value =>
                            Plugin.Cfg.SnesKeyA =
                                value);

                    DrawSnesConfigRow(
                        "B Button",
                        Plugin.Cfg.SnesKeyB,
                        value =>
                            Plugin.Cfg.SnesKeyB =
                                value);


                    //
                    // SNES-only controls
                    //

                    if (isSnes)
                    {
                        DrawSnesConfigRow(
                            "X Button",
                            Plugin.Cfg.SnesKeyX,
                            value =>
                                Plugin.Cfg.SnesKeyX =
                                    value);

                        DrawSnesConfigRow(
                            "Y Button",
                            Plugin.Cfg.SnesKeyY,
                            value =>
                                Plugin.Cfg.SnesKeyY =
                                    value);

                        DrawSnesConfigRow(
                            "L Button",
                            Plugin.Cfg.SnesKeyL,
                            value =>
                                Plugin.Cfg.SnesKeyL =
                                    value);

                        DrawSnesConfigRow(
                            "R Button",
                            Plugin.Cfg.SnesKeyR,
                            value =>
                                Plugin.Cfg.SnesKeyR =
                                    value);
                    }


                    //
                    // Start / Select
                    //

                    DrawSnesConfigRow(
                        "Start",
                        Plugin.Cfg.SnesKeyStart,
                        value =>
                            Plugin.Cfg.SnesKeyStart =
                                value);

                    DrawSnesConfigRow(
                        "Select",
                        Plugin.Cfg.SnesKeySelect,
                        value =>
                            Plugin.Cfg.SnesKeySelect =
                                value);


                    ImGui.EndTable();
                }
            }
        }


        //
        // =============================================================
        // Footer divider
        // =============================================================
        //

        var footerDividerY =
            popupMax.Y -
            65f;


        drawList.AddLine(
            new Vector2(
                popupPos.X +
                padding,
                footerDividerY),
            new Vector2(
                popupMax.X -
                padding,
                footerDividerY),
            ImGui.GetColorU32(
                BorderSubtle),
            1f);


        //
        // =============================================================
        // Footer buttons
        // =============================================================
        //

        var buttonGap = Ui(10f);

        var buttonWidth =
            (contentWidth -
             buttonGap) /
            2f;


        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));


        if (DrawSnesSecondaryButton(
                FontAwesomeIcon.Undo,
                "Restore Defaults",
                new Vector2(
                    buttonWidth,
                    Ui(36f))))
        {
            ResetSnesKeyboardControls();

            Plugin.Cfg.Save();
        }


        ImGui.SameLine(
            0f,
            buttonGap);


        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
        using (
            ImRaii.PushColor(
                ImGuiCol.Button,
                Accent)
                .Push(
                    ImGuiCol.ButtonHovered,
                    AccentHover)
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentActive))
        {
            if (ImGui.Button(
                    "Done",
                    new Vector2(
                        buttonWidth,
                        Ui(36f))))
            {
                Plugin.Cfg.Save();

                snesControlsPopupRequested =
                    false;
            }
        }


        ImGui.End();
    }


    private void DrawSnesConfigRow(
        string label,
        int currentValue,
        Action<int> setValue)
    {
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(
            0);

        ImGui.AlignTextToFramePadding();

        ImGui.TextUnformatted(
            label);


        ImGui.TableSetColumnIndex(
            1);

        var current =
            (VirtualKey)currentValue;

        var preview =
            GetSnesKeyDisplayName(
                current);

        ImGui.SetNextItemWidth(
            -1f);

        if (ImGui.BeginCombo(
                $"##snesKey_{label}",
                preview))
        {
            foreach (var key in GetSnesConfigurableKeys())
            {
                var selected =
                    key == current;

                if (ImGui.Selectable(
                        GetSnesKeyDisplayName(
                            key),
                        selected))
                {
                    setValue(
                        (int)key);
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }
    }


    private static VirtualKey[] GetSnesConfigurableKeys()
    {
        return
        [
            VirtualKey.UP,
        VirtualKey.DOWN,
        VirtualKey.LEFT,
        VirtualKey.RIGHT,

        VirtualKey.A,
        VirtualKey.B,
        VirtualKey.C,
        VirtualKey.D,
        VirtualKey.E,
        VirtualKey.F,
        VirtualKey.G,
        VirtualKey.H,
        VirtualKey.I,
        VirtualKey.J,
        VirtualKey.K,
        VirtualKey.L,
        VirtualKey.M,
        VirtualKey.N,
        VirtualKey.O,
        VirtualKey.P,
        VirtualKey.Q,
        VirtualKey.R,
        VirtualKey.S,
        VirtualKey.T,
        VirtualKey.U,
        VirtualKey.V,
        VirtualKey.W,
        VirtualKey.X,
        VirtualKey.Y,
        VirtualKey.Z,

        VirtualKey.RETURN,
        VirtualKey.SPACE,
        VirtualKey.LSHIFT,
        VirtualKey.RSHIFT
        ];
    }


    private static string GetSnesKeyDisplayName(
        VirtualKey key)
    {
        return key switch
        {
            VirtualKey.UP =>
                "Up Arrow",

            VirtualKey.DOWN =>
                "Down Arrow",

            VirtualKey.LEFT =>
                "Left Arrow",

            VirtualKey.RIGHT =>
                "Right Arrow",

            VirtualKey.RETURN =>
                "Enter",

            VirtualKey.SPACE =>
                "Space",

            VirtualKey.LSHIFT =>
                "Left Shift",

            VirtualKey.RSHIFT =>
                "Right Shift",

            _ =>
                key.ToString()
        };
    }


    private static void ResetSnesKeyboardControls()
    {
        Plugin.Cfg.SnesKeyUp =
            (int)VirtualKey.UP;

        Plugin.Cfg.SnesKeyDown =
            (int)VirtualKey.DOWN;

        Plugin.Cfg.SnesKeyLeft =
            (int)VirtualKey.LEFT;

        Plugin.Cfg.SnesKeyRight =
            (int)VirtualKey.RIGHT;

        Plugin.Cfg.SnesKeyA =
            (int)VirtualKey.X;

        Plugin.Cfg.SnesKeyB =
            (int)VirtualKey.Z;

        Plugin.Cfg.SnesKeyX =
            (int)VirtualKey.S;

        Plugin.Cfg.SnesKeyY =
            (int)VirtualKey.A;

        Plugin.Cfg.SnesKeyL =
            (int)VirtualKey.Q;

        Plugin.Cfg.SnesKeyR =
            (int)VirtualKey.W;

        Plugin.Cfg.SnesKeyStart =
            (int)VirtualKey.RETURN;

        Plugin.Cfg.SnesKeySelect =
            (int)VirtualKey.RSHIFT;
    }


    // =============================================================
    // Shared visual helpers
    // =============================================================

    private void DrawSnesSectionHeader(
        FontAwesomeIcon icon,
        string title,
        string subtitle)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        const float disc =
            34f;


        drawList.AddCircleFilled(
            origin +
            new Vector2(
                disc / 2f,
                disc / 2f),
            disc / 2f,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.18f)),
            32);


        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            var glyph =
                icon.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                origin +
                new Vector2(
                    disc / 2f -
                    glyphSize.X / 2f,
                    disc / 2f -
                    glyphSize.Y / 2f),
                ImGui.GetColorU32(
                    Accent),
                glyph);
        }


        ImGui.SetCursorScreenPos(
            origin +
            new Vector2(
                disc + Ui(10f),
                0));

        ImGui.TextUnformatted(
            title);

        ImGui.SetCursorScreenPos(
            origin +
            new Vector2(
                disc + Ui(10f),
                Ui(21f)));

        ImGui.TextColored(
            MutedText,
            subtitle);


        ImGui.SetCursorScreenPos(
            origin);

        ImGui.Dummy(
            new Vector2(
                1,
                disc));
    }


    private void DrawSnesPanel(
        string id,
        Vector2 size,
        Action draw)
    {
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(16, 16)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                13f))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                CardBg))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.34f)))
        using (
            var child =
                ImRaii.Child(
                    id,
                    size,
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child)
            {
                draw();
            }
        }
    }


    private void DrawSnesInnerCard(
        string id,
        Vector2 size,
        Action draw)
    {
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(14, 13)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                10f))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    FrameBg.X,
                    FrameBg.Y,
                    FrameBg.Z,
                    0.76f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                BorderSubtle))
        using (
            var child =
                ImRaii.Child(
                    id,
                    size,
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child)
            {
                draw();
            }
        }
    }


    private bool DrawSnesPrimaryAction(
        FontAwesomeIcon icon,
        string label,
        bool danger)
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        const float height =
            43f;

        var origin =
            ImGui.GetCursorScreenPos();

        var clicked =
            ImGui.InvisibleButton(
                $"##snesPrimary{label}",
                new Vector2(
                    width,
                    height));

        var hovered =
            ImGui.IsItemHovered();

        var drawList =
            ImGui.GetWindowDrawList();

        var fill =
            danger
                ? Danger
                : Accent;

        if (hovered)
        {
            fill =
                new Vector4(
                    MathF.Min(
                        1f,
                        fill.X + 0.08f),
                    MathF.Min(
                        1f,
                        fill.Y + 0.08f),
                    MathF.Min(
                        1f,
                        fill.Z + 0.08f),
                    fill.W);
        }


        drawList.AddRectFilled(
            origin,
            origin +
            new Vector2(
                width,
                height),
            ImGui.GetColorU32(
                fill),
            9f);


        var glyph =
            icon.ToIconString();

        Vector2 glyphSize;

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            glyphSize =
                ImGui.CalcTextSize(
                    glyph);
        }

        var labelSize =
            ImGui.CalcTextSize(
                label);

        var iconGap = Ui(10f);

        var totalWidth =
            glyphSize.X +
            iconGap +
            labelSize.X;

        var x =
            origin.X +
            (width -
             totalWidth) /
            2f;


        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    x,
                    origin.Y +
                    (height -
                     glyphSize.Y) /
                    2f),
                ImGui.GetColorU32(
                    Vector4.One),
                glyph);
        }


        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                x +
                glyphSize.X +
                iconGap,
                origin.Y +
                (height -
                 labelSize.Y) /
                2f),
            ImGui.GetColorU32(
                Vector4.One),
            label);


        return clicked;
    }


    private bool DrawSnesSecondaryButton(
        FontAwesomeIcon icon,
        string label,
        Vector2 size)
    {
        var clicked =
            ImGui.Button(
                $"##snesSecondary{label}",
                size);

        var min =
            ImGui.GetItemRectMin();

        var max =
            ImGui.GetItemRectMax();

        var hovered =
            ImGui.IsItemHovered();

        var drawList =
            ImGui.GetWindowDrawList();


        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                hovered
                    ? FrameBgHover
                    : FrameBg),
            8f);

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.55f)),
            8f,
            ImDrawFlags.None,
            1f);


        var glyph =
            icon.ToIconString();

        Vector2 glyphSize;

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            glyphSize =
                ImGui.CalcTextSize(
                    glyph);
        }

        var labelSize =
            ImGui.CalcTextSize(
                label);

        const float gap =
            8f;

        var total =
            glyphSize.X +
            gap +
            labelSize.X;

        var x =
            min.X +
            (size.X -
             total) /
            2f;


        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    x,
                    min.Y +
                    (size.Y -
                     glyphSize.Y) /
                    2f),
                ImGui.GetColorU32(
                    Accent),
                glyph);
        }


        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                x +
                glyphSize.X +
                gap,
                min.Y +
                (size.Y -
                 labelSize.Y) /
                2f),
            ImGui.GetColorU32(
                Vector4.One),
            label);


        return clicked;
    }


    private bool DrawSnesSegmentButton(
        string label,
        bool selected,
        Vector2 size)
    {
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
        using (
            ImRaii.PushColor(
                ImGuiCol.Button,
                selected
                    ? Accent
                    : FrameBg))
        using (
            ImRaii.PushColor(
                ImGuiCol.ButtonHovered,
                selected
                    ? AccentHover
                    : FrameBgHover))
        using (
            ImRaii.PushColor(
                ImGuiCol.ButtonActive,
                selected
                    ? AccentActive
                    : FrameBgHover))
        {
            return ImGui.Button(
                label,
                size);
        }
    }
}
