using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool gameKeyboardControlsOpenRequested;
    private bool gameControllerControlsOpenRequested;
    private string? controllerBindingCapture;
    private Action<int>? controllerBindingSetter;
    private bool controllerCaptureWaitingForNeutral;

    private void DrawCompactGamesPage()
    {
        // The shell uses generous spacing and transparent frames. This compact
        // page needs its own spacing and visible input surfaces.
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8f, 6f));
        using var frames = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10f, 6f))
            .Push(ImGuiStyleVar.FrameRounding, Ui(7f));
        using var inputColors = ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0.13f, 0.11f, 0.23f, 1f))
            .Push(ImGuiCol.FrameBgHovered, new Vector4(0.20f, 0.16f, 0.32f, 1f))
            .Push(ImGuiCol.FrameBgActive, new Vector4(0.25f, 0.19f, 0.40f, 1f))
            .Push(ImGuiCol.SliderGrab, Accent).Push(ImGuiCol.SliderGrabActive, AccentHover)
            .Push(ImGuiCol.CheckMark, Accent);
        RestoreLibrarySelection();
        var engine = screenController.Engine;
        if (engine.IsPlayingSnes) selectedGameSystem = GameSystem.Snes;
        else if (engine.IsPlayingGameBoy) selectedGameSystem = GameSystem.GameBoy;
        else if (engine.IsPlayingNes) selectedGameSystem = GameSystem.Nes;
        else if (engine.IsPlayingGameBoyAdvance) selectedGameSystem = GameSystem.GameBoyAdvance;
        else if (engine.IsPlayingMasterSystem) selectedGameSystem = GameSystem.MasterSystem;
        else if (engine.IsPlayingGameGear) selectedGameSystem = GameSystem.GameGear;
        var playing = engine.IsPlayingGame;
        var width = ImGui.GetContentRegionAvail().X;
        var gap = Ui(14f);
        var tabWidth = (width - gap * 2f) / 3f;

        using (ImRaii.Disabled(playing))
        {
            if (GameLayoutButton("Super Nintendo", FontAwesomeIcon.Gamepad, tabWidth, selectedGameSystem == GameSystem.Snes, height: Ui(44f)))
                selectedGameSystem = GameSystem.Snes;
            if (playing && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Exit the current game before switching systems.");
            ImGui.SameLine(0f, gap);
            if (GameLayoutButton("Game Boy / Color", FontAwesomeIcon.Gamepad, tabWidth, selectedGameSystem == GameSystem.GameBoy, height: Ui(44f)))
                selectedGameSystem = GameSystem.GameBoy;
            if (playing && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Exit the current game before switching systems.");
            ImGui.SameLine(0f, gap);
            if (GameLayoutButton("Master System / SG-1000", FontAwesomeIcon.Gamepad, tabWidth, selectedGameSystem == GameSystem.MasterSystem, height: Ui(44f)))
                selectedGameSystem = GameSystem.MasterSystem;
            if (playing && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Exit the current game before switching systems.");
        }
        ImGui.Dummy(UiVec(0f, 6f));
        using (ImRaii.Disabled(playing))
        {
            if (GameLayoutButton("NES", FontAwesomeIcon.Gamepad, tabWidth, selectedGameSystem == GameSystem.Nes, height: Ui(44f)))
                selectedGameSystem = GameSystem.Nes;
            if (playing && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Exit the current game before switching systems.");
            ImGui.SameLine(0f, gap);
            if (GameLayoutButton("Game Boy Advance", FontAwesomeIcon.Gamepad, tabWidth, selectedGameSystem == GameSystem.GameBoyAdvance, height: Ui(44f)))
                selectedGameSystem = GameSystem.GameBoyAdvance;
            if (playing && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Exit the current game before switching systems.");
            ImGui.SameLine(0f, gap);
            if (GameLayoutButton("Game Gear", FontAwesomeIcon.Gamepad, tabWidth, selectedGameSystem == GameSystem.GameGear, height: Ui(44f)))
                selectedGameSystem = GameSystem.GameGear;
            if (playing && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Exit the current game before switching systems.");
        }
        ImGui.Dummy(UiVec(0f, 10f));

        // Keep the approved two-column layout at every window size.
        var leftWidth = (width - gap) * 0.55f;
        GameLayoutPanel("##compactGame", leftWidth, Ui(280f), () => DrawCompactGame(selectedGameSystem, playing));
        ImGui.SameLine(0f, gap);
        GameLayoutPanel("##compactBroadcast", width - leftWidth - gap,
            Ui(280f), () => DrawCompactGameBroadcast(selectedGameSystem, playing));
        ImGui.Dummy(UiVec(0f, 10f));

        GameLayoutPanel("##compactGameControls", width, Ui(275f), () =>
        {
            GameLayoutHeading("Controls & Audio", FontAwesomeIcon.Gamepad);
            var available = ImGui.GetContentRegionAvail().X;
            var columnGap = Ui(40f);
            var columnWidth = (available - columnGap) / 2f;
            var divider = ImGui.GetCursorScreenPos() + new Vector2(columnWidth + columnGap / 2f, 0f);
            ImGui.GetWindowDrawList().AddLine(divider, divider + new Vector2(0f, Ui(185f)), ImGui.GetColorU32(BorderSubtle), Ui(1f));
            using (var input = ImRaii.Child("##compactInput", new Vector2(columnWidth, Ui(195f)), false))
            {
                if (input) DrawCompactGameInput(selectedGameSystem, playing);
            }
            ImGui.SameLine(0f, columnGap);
            using (var audio = ImRaii.Child("##compactAudio", new Vector2(columnWidth, Ui(195f)), false))
            {
                if (audio) DrawCompactGameAudio(selectedGameSystem);
            }
        });
        ImGui.Dummy(new Vector2(0f, BottomBarHeight + Ui(16f)));
    }

    private bool GameLayoutButton(string label, FontAwesomeIcon icon, float width,
        bool primary = false, bool danger = false, float height = 36f)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(MathF.Max(1f, width), Ui(height));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, primary || danger ? 0f : Ui(1f));
        using var colors = ImRaii.PushColor(ImGuiCol.Button,
            danger ? Danger : primary ? Accent : new Vector4(0.055f, 0.07f, 0.115f, 1f))
            .Push(ImGuiCol.ButtonHovered, danger ? Danger : primary ? AccentHover : CardBgHover)
            .Push(ImGuiCol.ButtonActive, danger ? Danger : AccentActive)
            .Push(ImGuiCol.Border, BorderSubtle);
        var clicked = ImGui.Button("##game_" + label, size);
        var textSize = ImGui.CalcTextSize(label);
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont)) iconSize = ImGui.CalcTextSize(icon.ToIconString());
        var x = origin.X + (size.X - textSize.X - iconSize.X - Ui(8f)) / 2f;
        var color = ImGui.GetColorU32(ImGuiCol.Text);
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + size, true);
        using (ImRaii.PushFont(UiBuilder.IconFont))
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(x, origin.Y + (size.Y - iconSize.Y) / 2f), color, icon.ToIconString());
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(x + iconSize.X + Ui(8f), origin.Y + (size.Y - textSize.Y) / 2f), color, label);
        draw.PopClipRect();
        return clicked;
    }

    private void GameLayoutPanel(string id, float width, float height, Action draw)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(16f, 16f));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(12f));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        // Keep overflow scrollable for large fonts and long error messages.
        using var child = ImRaii.Child(id, new Vector2(width, height), true);
        if (child) draw();
    }

    private void GameLayoutHeading(string title, FontAwesomeIcon icon, string? status = null, Vector4? statusColor = null,
        bool patreonFeature = false)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont)) ImGui.TextColored(Accent, icon.ToIconString());
        ImGui.SameLine(0f, Ui(10f));
        ImGui.TextUnformatted(title);
        if (status is not null)
        {
            ImGui.SameLine(0f, Ui(16f));
            ImGui.TextColored(statusColor ?? MutedText, status);
        }
        if (patreonFeature)
            DrawPatreonFeatureTag();
        ImGui.Dummy(UiVec(0f, 10f));
    }

    private void DrawCompactGame(GameSystem system, bool playing)
    {
        var error = system == GameSystem.Snes ? snesLaunchError : system == GameSystem.Nes ? nesLaunchError : system == GameSystem.GameBoyAdvance ? gameBoyAdvanceLaunchError : system == GameSystem.MasterSystem ? masterSystemLaunchError : system == GameSystem.GameGear ? gameGearLaunchError : gameBoyLaunchError;
        GameLayoutHeading("Game", FontAwesomeIcon.Gamepad,
            error is not null ? "Needs attention" : playing ? "Playing" : "Ready", error is not null ? Danger : Good);
        DrawLibrarySelector(system, playing);
        var path = system == GameSystem.Snes ? snesSelectedRomPath : system == GameSystem.Nes ? nesSelectedRomPath : system == GameSystem.GameBoyAdvance ? gameBoyAdvanceSelectedRomPath : system == GameSystem.MasterSystem ? masterSystemSelectedRomPath : system == GameSystem.GameGear ? gameGearSelectedRomPath : gameBoySelectedRomPath;
        var engine = screenController.Engine;
        var blocked = engine.IsPlayingLocalVideo || stream.Mode == StreamMode.Viewing;
        using (ImRaii.Disabled(!playing && (string.IsNullOrWhiteSpace(path) || blocked)))
        {
            if (GameLayoutButton(playing ? "Exit Game & Despawn TV" : "Start Playing",
                playing ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play, ImGui.GetContentRegionAvail().X, !playing, playing))
            {
                StopGameWatchPartyBroadcast();
                snesLaunchError = gameBoyLaunchError = nesLaunchError = gameBoyAdvanceLaunchError = masterSystemLaunchError = gameGearLaunchError = null;
                if (playing) engine.StopVideo();
                else if (!(system == GameSystem.Snes ? engine.PlaySnes(path) : system == GameSystem.Nes ? engine.PlayNes(path) : system == GameSystem.GameBoyAdvance ? engine.PlayGameBoyAdvance(path) : system == GameSystem.MasterSystem ? engine.PlayMasterSystem(path) : system == GameSystem.GameGear ? engine.PlayGameGear(path) : engine.PlayGameBoy(path)))
                {
                    if (system == GameSystem.Snes) snesLaunchError = engine.LastError ?? "SNES failed to start.";
                    else if (system == GameSystem.Nes) nesLaunchError = engine.LastError ?? "NES failed to start.";
                    else if (system == GameSystem.GameBoyAdvance) gameBoyAdvanceLaunchError = engine.LastError ?? "Game Boy Advance failed to start.";
                    else if (system == GameSystem.MasterSystem) masterSystemLaunchError = engine.LastError ?? "Master System / SG-1000 failed to start.";
                    else if (system == GameSystem.GameGear) gameGearLaunchError = engine.LastError ?? "Game Gear failed to start.";
                    else gameBoyLaunchError = engine.LastError ?? "Game Boy failed to start.";
                }
            }
        }
        if (!playing && blocked)
            ImGui.TextWrapped(stream.Mode == StreamMode.Viewing
                ? "Leave the current Watch Party before starting gameplay."
                : "Stop Local Video before starting a game.");
        if (!string.IsNullOrWhiteSpace(error))
        {
            using var errorColor = ImRaii.PushColor(ImGuiCol.Text, Danger);
            ImGui.TextWrapped(error);
        }
    }

    private void BrowseCompactGameRom(bool snes)
    {
        var dialog = snes ? snesFileDialog : gameBoyFileDialog;
        dialog.OpenFileDialog(snes ? "Select SNES ROM" : "Select Game Boy ROM",
            snes ? ".sfc,.smc" : ".gb,.gbc,.dmg", (success, path) =>
            {
                if (!success || string.IsNullOrWhiteSpace(path)) return;
                var extension = Path.GetExtension(path);
                var allowed = snes ? new[] { ".sfc", ".smc" } : new[] { ".gb", ".gbc", ".dmg" };
                if (!allowed.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    if (snes) snesLaunchError = "Please select an .sfc or .smc SNES ROM.";
                    else gameBoyLaunchError = "Please select a .gb, .gbc or .dmg Game Boy ROM.";
                    return;
                }
                if (snes) { snesSelectedRomPath = path; snesLaunchError = null; }
                else { gameBoySelectedRomPath = path; gameBoyLaunchError = null; }
            });
    }

    private void DrawCompactGameBroadcast(GameSystem system, bool playing)
    {
        var armed = gameBroadcastArmed && gameBroadcastSystem == system;
        var uploading = system == GameSystem.Snes ? screenController.Engine.IsSnesBroadcasting : system == GameSystem.Nes ? screenController.Engine.IsNesBroadcasting : system == GameSystem.GameBoyAdvance ? screenController.Engine.IsGameBoyAdvanceBroadcasting : system == GameSystem.MasterSystem ? screenController.Engine.IsMasterSystemBroadcasting : system == GameSystem.GameGear ? screenController.Engine.IsGameGearBroadcasting : screenController.Engine.IsGameBoyBroadcasting;

        if (!armed && !HasConfirmedPatreonAccess())
        {
            DrawCompactGamePatreonGate(system);
            return;
        }

        GameLayoutHeading("Broadcast", FontAwesomeIcon.BroadcastTower,
            armed ? gameBroadcastStartFailed ? "Broadcast failed" : uploading ? "Live" : "Waiting for viewers" : "Offline",
            armed ? gameBroadcastStartFailed ? Danger : Good : MutedText,
            patreonFeature: true);
        ImGui.TextWrapped("Share your gameplay with your Watch Party.");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Friends can watch your gameplay; broadcasting does not give them control.");
        ImGui.Dummy(UiVec(0f, 8f));
        if (armed)
        {
            ImGui.TextWrapped(gameBroadcastStartFailed ? "Unable to start uploading. Stop and restart the broadcast to retry."
                : uploading ? "Your gameplay is being uploaded to the live relay."
                : "Upload starts when a viewer joins and pauses when everyone leaves.");
            ImGui.Spacing();
            if (GameLayoutButton("Stop Broadcast", FontAwesomeIcon.Stop, ImGui.GetContentRegionAvail().X, danger: true))
                StopGameWatchPartyBroadcast();
            // Testing only: temporarily enable this control to retrieve the viewer URL.
            // if (CurrentSession is { } liveSession &&
            //     GameLayoutButton("Copy Viewer URL", FontAwesomeIcon.Copy, ImGui.GetContentRegionAvail().X))
            //     ImGui.SetClipboardText(BuildMyHlsUrl(liveSession));
            return;
        }
        var signedIn = CurrentSession is not null;
        var hosting = stream.Mode == StreamMode.Hosting;
        var viewing = stream.Mode == StreamMode.Viewing;
        var hasKey = CurrentSession is { } session && !string.IsNullOrWhiteSpace(Plugin.Cfg.StreamKeys.GetValueOrDefault(session.AccountId));
        var reason = !signedIn ? "Sign in to share your gameplay."
            : viewing ? "Leave the current Watch Party before hosting your own."
            : !hasKey ? "Generate a stream key in Settings > Account."
            : !hosting ? null
            : !playing ? "Start a game to begin broadcasting." : "Ready to broadcast.";
        if (!string.IsNullOrWhiteSpace(reason))
        {
            ImGui.TextWrapped(reason);
            ImGui.Spacing();
        }
        if (!signedIn || !hasKey)
        {
            if (GameLayoutButton("Open Settings", FontAwesomeIcon.Cog, ImGui.GetContentRegionAvail().X)) currentPage = HomePage.Settings;
        }
        else if (!hosting)
        {
            using var disabled = ImRaii.Disabled(viewing);
            if (GameLayoutButton("Create Watch Party", FontAwesomeIcon.Users, ImGui.GetContentRegionAvail().X))
            {
                pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.GameRoom;
                watchPartyCreationPopupOpen = true;
                createRoomPassword = string.Empty;
                createLockedRoomPasswordError = null;
            }
        }
        ImGui.Spacing();
        using (ImRaii.Disabled(!signedIn || !hasKey || !hosting || !playing))
            if (GameLayoutButton("Start Broadcast", FontAwesomeIcon.BroadcastTower, ImGui.GetContentRegionAvail().X, true))
                StartGameWatchPartyBroadcast();
    }

    private void RefreshGamePatreonAccess()
    {
        if (!HasConfiguredPatreonAccess())
        {
            patreonAccessConfirmed = false;
            gamePatreonAccessMessage =
                "No active Patreon membership was found.";
            return;
        }

        patreonAccessConfirmed = true;
        gamePatreonAccessMessage = null;

        Plugin.ChatGui.Print(
            $"[AlphaChannel] Patreon access confirmed (tier {Plugin.Cfg.PatreonMembershipTier}).");
    }

    private static string GameSystemBroadcastName(GameSystem system) =>
        system switch
        {
            GameSystem.Snes => "SNES",
            GameSystem.Nes => "NES",
            GameSystem.GameBoyAdvance => "Game Boy Advance",
            GameSystem.MasterSystem => "Master System / SG-1000",
            GameSystem.GameGear => "Game Gear",
            _ => "Game Boy"
        };

    private void DrawCompactGamePatreonGate(GameSystem system)
    {
        var panelMin = ImGui.GetWindowPos();
        var panelMax = panelMin + ImGui.GetWindowSize();
        var drawList = ImGui.GetWindowDrawList();
        var orangeBorder = ImGui.GetColorU32(new Vector4(
            PatreonOrange.X, PatreonOrange.Y, PatreonOrange.Z, 0.9f));
        var accentBorder = ImGui.GetColorU32(new Vector4(
            Accent.X, Accent.Y, Accent.Z, 0.9f));
        var middleX = (panelMin.X + panelMax.X) * 0.5f;

        drawList.AddCircleFilled(
            new Vector2(panelMax.X - Ui(24f), panelMin.Y + Ui(118f)),
            Ui(155f),
            ImGui.GetColorU32(new Vector4(
                PatreonOrange.X, PatreonOrange.Y, PatreonOrange.Z, 0.055f)),
            64);
        drawList.AddLine(panelMin + UiVec(10f, 0f), new Vector2(middleX, panelMin.Y), accentBorder, Ui(1.2f));
        drawList.AddLine(new Vector2(middleX, panelMin.Y), new Vector2(panelMax.X - Ui(10f), panelMin.Y), orangeBorder, Ui(1.2f));
        drawList.AddLine(new Vector2(panelMax.X, panelMin.Y + Ui(10f)), new Vector2(panelMax.X, panelMax.Y - Ui(10f)), orangeBorder, Ui(1.2f));
        drawList.AddLine(new Vector2(panelMax.X - Ui(10f), panelMax.Y), new Vector2(middleX, panelMax.Y), orangeBorder, Ui(1.2f));

        GameLayoutHeading(
            "Broadcast",
            FontAwesomeIcon.BroadcastTower,
            "PATREON FEATURE",
            PatreonOrange);

        var contentStart = ImGui.GetCursorScreenPos();
        var contentWidth = ImGui.GetContentRegionAvail().X;
        DrawLocalVideoPatreonHeart(
            contentStart + new Vector2(contentWidth * 0.5f, Ui(20f)));
        ImGui.Dummy(UiVec(0f, 43f));

        DrawPatreonCenteredText(
            $"Broadcast {GameSystemBroadcastName(system)} gameplay",
            Vector4.One,
            1.2f);
        DrawPatreonCenteredText(
            "Play games locally for free. Join our Patreon",
            MutedText);
        DrawPatreonCenteredText(
            "to broadcast your gameplay to a Watch Party.",
            MutedText);
        ImGui.Dummy(UiVec(0f, 5f));

        var buttonWidth = MathF.Min(contentWidth, Ui(390f));
        ImGui.SetCursorPosX(
            (ImGui.GetWindowWidth() - buttonWidth) * 0.5f);
        DrawDjActionButton(
            "##unlockGameBroadcastWithPatreon",
            FontAwesomeIcon.LockOpen,
            "Unlock with Patreon",
            new Vector2(buttonWidth, Ui(38f)),
            false,
            () => patreonPopupOpen = true,
            true);

        ImGui.SetCursorPosX(
            (ImGui.GetWindowWidth() - buttonWidth) * 0.5f);
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
                   .Push(ImGuiCol.ButtonHovered, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.14f))
                   .Push(ImGuiCol.ButtonActive, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.24f))
                   .Push(ImGuiCol.Text, MutedText))
        {
            if (ImGui.Button(
                    "Already a member? Refresh access##refreshGamePatreon",
                    new Vector2(buttonWidth, Ui(25f))))
            {
                RefreshGamePatreonAccess();
            }
        }

        if (gamePatreonAccessMessage is { } accessMessage)
        {
            SetUiFontScale(0.82f);
            DrawPatreonCenteredText(accessMessage, Danger);
            SetUiFontScale(1f);
        }
    }

    private void DrawCompactGameInput(GameSystem system, bool playing)
    {
        ImGui.TextUnformatted("Input control");
        ImGui.TextColored(MutedText, "Choose how to control the game.");
        ImGui.Dummy(UiVec(0f, 8f));
        var engine = screenController.Engine;
        var enabled = playing && (system == GameSystem.Snes ? engine.SnesControlsEnabled : engine.GameBoyControlsEnabled);
        var half = (ImGui.GetContentRegionAvail().X - Ui(8f)) / 2f;
        using (ImRaii.Disabled(!playing))
            if (GameLayoutButton(system == GameSystem.Snes ? "Control SNES" : system == GameSystem.Nes ? "Control NES" : system == GameSystem.GameBoyAdvance ? "Control Game Boy Advance" : system == GameSystem.MasterSystem ? "Control MS / SG-1000" : system == GameSystem.GameGear ? "Control Game Gear" : "Control Game Boy", FontAwesomeIcon.Gamepad, half, enabled))
            {
                if (system == GameSystem.Snes) engine.SetSnesControlsEnabled(true); else engine.SetGameBoyControlsEnabled(true);
            }
        ImGui.SameLine(0f, Ui(8f));
        if (GameLayoutButton("Control FFXIV", FontAwesomeIcon.Desktop, half, !enabled))
        {
            if (system == GameSystem.Snes) engine.SetSnesControlsEnabled(false); else engine.SetGameBoyControlsEnabled(false);
        }
        ImGui.Spacing();
        ImGui.TextColored(MutedText, "Keyboard & controller supported.");
        ImGui.Spacing();
        var configureWidth = (ImGui.GetContentRegionAvail().X - Ui(8f)) / 2f;
        if (GameLayoutButton("Configure Keyboard", FontAwesomeIcon.Keyboard, configureWidth))
            gameKeyboardControlsOpenRequested = true;
        ImGui.SameLine(0f, Ui(8f));
        if (GameLayoutButton("Configure Controller", FontAwesomeIcon.Gamepad, configureWidth))
            gameControllerControlsOpenRequested = true;
    }

    private void DrawCompactGameAudio(GameSystem system)
    {
        ImGui.TextUnformatted("Audio & display");
        var volume = Plugin.Cfg.Volume;
        ImGui.TextColored(MutedText, "TV volume");
        ImGui.SetNextItemWidth(MathF.Max(Ui(50f), ImGui.GetContentRegionAvail().X - Ui(52f)));
        if (ImGui.SliderInt("##compactGameVolume", ref volume, 0, 130, ""))
        {
            Plugin.Cfg.Volume = volume;
            if (volume > 0) Plugin.Cfg.Muted = false;
            video.SetVolume(Plugin.Cfg.Muted ? 0 : volume);
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) Plugin.Cfg.Save();
        ImGui.SameLine(0f, Ui(8f));
        ImGui.TextUnformatted($"{volume}%");
        ImGui.Spacing();
        var half = (ImGui.GetContentRegionAvail().X - Ui(8f)) / 2f;
        if (GameLayoutButton(Plugin.Cfg.Muted ? "Unmute TV" : "Mute TV", Plugin.Cfg.Muted ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute, half))
        {
            Plugin.Cfg.Muted = !Plugin.Cfg.Muted;
            video.SetVolume(Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume);
            Plugin.Cfg.Save();
        }
        ImGui.SameLine(0f, Ui(8f));
        var muted = IsFfxivSoundMuted();
        if (GameLayoutButton(muted ? "Restore FFXIV" : "Mute FFXIV", muted ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute, half)) SetFfxivSoundMuted(!muted);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Unmuting restores your previous FFXIV audio levels.");
        ImGui.Dummy(UiVec(0f, 6f));
        ImGui.Separator();
        ImGui.Spacing();
        var crt = system == GameSystem.Snes ? screenController.Engine.SnesCrtFilterEnabled : system == GameSystem.Nes ? screenController.Engine.NesCrtFilterEnabled : system == GameSystem.GameBoyAdvance ? screenController.Engine.GameBoyAdvanceCrtFilterEnabled : system == GameSystem.MasterSystem ? screenController.Engine.MasterSystemCrtFilterEnabled : system == GameSystem.GameGear ? screenController.Engine.GameGearCrtFilterEnabled : screenController.Engine.GameBoyCrtFilterEnabled;
        if (DrawGameCrtToggle(ref crt))
        {
            if (system == GameSystem.Snes) screenController.Engine.SetSnesCrtFilterEnabled(crt);
            else if (system == GameSystem.Nes) screenController.Engine.SetNesCrtFilterEnabled(crt);
            else if (system == GameSystem.GameBoyAdvance) screenController.Engine.SetGameBoyAdvanceCrtFilterEnabled(crt);
            else if (system == GameSystem.MasterSystem) screenController.Engine.SetMasterSystemCrtFilterEnabled(crt);
            else if (system == GameSystem.GameGear) screenController.Engine.SetGameGearCrtFilterEnabled(crt);
            else screenController.Engine.SetGameBoyCrtFilterEnabled(crt);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Apply a CRT screen style filter to the game.");
    }

    private void DrawCompactGameKeyboardPopup(bool snes)
    {
        var hasShoulderButtons = snes || selectedGameSystem == GameSystem.GameBoyAdvance;
        if (!BeginGameDialog("Keyboard controls", "Configure Keyboard", FontAwesomeIcon.Keyboard, 620f, hasShoulderButtons ? 560f : 510f)) return;
        ImGui.TextUnformatted(snes ? "Super Nintendo controls" : selectedGameSystem == GameSystem.Nes ? "Nintendo Entertainment System controls" : selectedGameSystem == GameSystem.GameBoyAdvance ? "Game Boy Advance controls" : selectedGameSystem == GameSystem.MasterSystem ? "Master System / SG-1000 controls" : selectedGameSystem == GameSystem.GameGear ? "Game Gear controls" : "Game Boy / Color controls");
        ImGui.TextWrapped("Configure Player 1 keyboard bindings below.");
        if (!snes) ImGui.TextWrapped($"{(selectedGameSystem == GameSystem.Nes ? "NES" : selectedGameSystem == GameSystem.GameBoyAdvance ? "Game Boy Advance" : selectedGameSystem == GameSystem.MasterSystem ? "Master System / SG-1000" : selectedGameSystem == GameSystem.GameGear ? "Game Gear" : "Game Boy")} shares these bindings with the matching SNES controls.");
        if (ImGui.BeginTable("##compactBindings", 2, ImGuiTableFlags.SizingStretchProp))
        {
            DrawGameBindingCell("D-Pad Up", Plugin.Cfg.SnesKeyUp, v => Plugin.Cfg.SnesKeyUp = v);
            DrawGameBindingCell("D-Pad Down", Plugin.Cfg.SnesKeyDown, v => Plugin.Cfg.SnesKeyDown = v);
            DrawGameBindingCell("D-Pad Left", Plugin.Cfg.SnesKeyLeft, v => Plugin.Cfg.SnesKeyLeft = v);
            DrawGameBindingCell("D-Pad Right", Plugin.Cfg.SnesKeyRight, v => Plugin.Cfg.SnesKeyRight = v);
            DrawGameBindingCell("A Button", Plugin.Cfg.SnesKeyA, v => Plugin.Cfg.SnesKeyA = v);
            DrawGameBindingCell("B Button", Plugin.Cfg.SnesKeyB, v => Plugin.Cfg.SnesKeyB = v);
            if (hasShoulderButtons)
            {
                if (snes)
                {
                    DrawGameBindingCell("X Button", Plugin.Cfg.SnesKeyX, v => Plugin.Cfg.SnesKeyX = v);
                    DrawGameBindingCell("Y Button", Plugin.Cfg.SnesKeyY, v => Plugin.Cfg.SnesKeyY = v);
                }
                DrawGameBindingCell("L Button", Plugin.Cfg.SnesKeyL, v => Plugin.Cfg.SnesKeyL = v);
                DrawGameBindingCell("R Button", Plugin.Cfg.SnesKeyR, v => Plugin.Cfg.SnesKeyR = v);
            }
            DrawGameBindingCell("Start", Plugin.Cfg.SnesKeyStart, v => Plugin.Cfg.SnesKeyStart = v);
            DrawGameBindingCell("Select", Plugin.Cfg.SnesKeySelect, v => Plugin.Cfg.SnesKeySelect = v);
            ImGui.EndTable();
        }
        if (HasDuplicateGameKeyBindings()) ImGui.TextWrapped("Some buttons share the same keyboard key.");
        ImGui.Separator();
        var block = screenController.Engine.BlockAllFfxivKeyboardInput;
        if (ImGui.Checkbox("Block all FFXIV keyboard input while controlling game", ref block))
            screenController.Engine.SetBlockAllFfxivKeyboardInput(block);
        ImGui.TextWrapped("When disabled, only assigned game keys are blocked. Press Ctrl + F12 to return control to FFXIV.");
        if (GameLayoutButton("Restore Defaults", FontAwesomeIcon.Undo, ImGui.GetContentRegionAvail().X))
        {
            ResetSnesKeyboardControls();
            Plugin.Cfg.Save();
        }
        if (GameLayoutButton("Save & Close", FontAwesomeIcon.Check, ImGui.GetContentRegionAvail().X, true))
        {
            Plugin.Cfg.Save();
            activeGameDialog = null;
        }
        EndGameDialog();
    }

    private void DrawCompactGameControllerPopup(bool snes)
    {
        var hasShoulderButtons = snes || selectedGameSystem == GameSystem.GameBoyAdvance;
        if (!BeginGameDialog("Controller controls", "Configure Controller", FontAwesomeIcon.Gamepad, 660f,
                hasShoulderButtons ? 610f : 560f)) return;

        ImGui.TextUnformatted(snes ? "Super Nintendo controls" : selectedGameSystem == GameSystem.Nes
            ? "Nintendo Entertainment System controls" : selectedGameSystem == GameSystem.GameBoyAdvance
                ? "Game Boy Advance controls" : selectedGameSystem == GameSystem.MasterSystem
                    ? "Master System / SG-1000 controls" : selectedGameSystem == GameSystem.GameGear
                        ? "Game Gear controls" : "Game Boy / Color controls");
        ImGui.TextWrapped("Select a control, then press the button on your controller to assign it.");
        if (!snes)
            ImGui.TextWrapped($"{(selectedGameSystem == GameSystem.Nes ? "NES" : selectedGameSystem == GameSystem.GameBoyAdvance ? "Game Boy Advance" : selectedGameSystem == GameSystem.MasterSystem ? "Master System / SG-1000" : selectedGameSystem == GameSystem.GameGear ? "Game Gear" : "Game Boy")} shares this controller layout with the matching SNES controls.");

        ImGui.Spacing();
        if (ImGui.BeginTable("##compactControllerBindings", 2, ImGuiTableFlags.SizingStretchProp))
        {
            DrawControllerBindingCell("D-Pad Up", Plugin.Cfg.GamepadUp, v => Plugin.Cfg.GamepadUp = v);
            DrawControllerBindingCell("D-Pad Down", Plugin.Cfg.GamepadDown, v => Plugin.Cfg.GamepadDown = v);
            DrawControllerBindingCell("D-Pad Left", Plugin.Cfg.GamepadLeft, v => Plugin.Cfg.GamepadLeft = v);
            DrawControllerBindingCell("D-Pad Right", Plugin.Cfg.GamepadRight, v => Plugin.Cfg.GamepadRight = v);
            DrawControllerBindingCell("A Button", Plugin.Cfg.GamepadA, v => Plugin.Cfg.GamepadA = v);
            DrawControllerBindingCell("B Button", Plugin.Cfg.GamepadB, v => Plugin.Cfg.GamepadB = v);
            if (hasShoulderButtons)
            {
                if (snes)
                {
                    DrawControllerBindingCell("X Button", Plugin.Cfg.GamepadX, v => Plugin.Cfg.GamepadX = v);
                    DrawControllerBindingCell("Y Button", Plugin.Cfg.GamepadY, v => Plugin.Cfg.GamepadY = v);
                }
                DrawControllerBindingCell("L Button", Plugin.Cfg.GamepadL, v => Plugin.Cfg.GamepadL = v);
                DrawControllerBindingCell("R Button", Plugin.Cfg.GamepadR, v => Plugin.Cfg.GamepadR = v);
            }
            DrawControllerBindingCell("Start", Plugin.Cfg.GamepadStart, v => Plugin.Cfg.GamepadStart = v);
            DrawControllerBindingCell("Select", Plugin.Cfg.GamepadSelect, v => Plugin.Cfg.GamepadSelect = v);
            ImGui.EndTable();
        }

        CaptureControllerBinding();

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.12f, 0.09f, 0.035f, 0.72f))
            .Push(ImGuiCol.Border, Gold))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12f, 10f)))
        using (var info = ImRaii.Child("##controllerCompatibility", UiVec(-1f, 72f), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (info)
            {
                ImGui.TextColored(Gold, "Controller compatibility");
                ImGui.TextWrapped("The controller must be enabled and recognized by Final Fantasy XIV and Dalamud. Controllers that FFXIV cannot detect may not work in Alpha Channel.");
            }
        }

        if (GameLayoutButton("Restore Defaults", FontAwesomeIcon.Undo, ImGui.GetContentRegionAvail().X))
        {
            ResetGamepadControls();
            controllerBindingCapture = null;
            controllerBindingSetter = null;
            Plugin.Cfg.Save();
        }
        if (GameLayoutButton("Save & Close", FontAwesomeIcon.Check, ImGui.GetContentRegionAvail().X, true))
        {
            Plugin.Cfg.Save();
            controllerBindingCapture = null;
            controllerBindingSetter = null;
            activeGameDialog = null;
        }
        EndGameDialog();
    }

    private void DrawControllerBindingCell(string label, int value, Action<int> setValue)
    {
        ImGui.TableNextColumn();
        ImGui.PushID("controller_" + label);
        if (ImGui.BeginTable("##controllerBindingPair", 2,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("Control", ImGuiTableColumnFlags.WidthStretch, 0.38f);
            ImGui.TableSetupColumn("Button", ImGuiTableColumnFlags.WidthStretch, 0.62f);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            ImGui.TableNextColumn();
            var listening = controllerBindingCapture == label;
            using (ImRaii.PushColor(ImGuiCol.Button, listening ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.55f) : CardBg))
            {
                var caption = listening
                    ? controllerCaptureWaitingForNeutral ? "Release buttons..." : "Press a button..."
                    : GamepadButtonDisplayName((GamepadButtons)value);
                if (ImGui.Button(caption, new Vector2(-1f, 0f)))
                {
                    controllerBindingCapture = label;
                    controllerBindingSetter = setValue;
                    controllerCaptureWaitingForNeutral = true;
                }
            }
            ImGui.EndTable();
        }
        ImGui.PopID();
    }

    private void CaptureControllerBinding()
    {
        if (controllerBindingSetter is null) return;
        if (controllerCaptureWaitingForNeutral)
        {
            if (AssignableGamepadButtons.All(button => Plugin.GamepadState.Raw(button) == 0))
                controllerCaptureWaitingForNeutral = false;
            return;
        }

        foreach (var button in AssignableGamepadButtons)
        {
            if (Plugin.GamepadState.Pressed(button) == 0) continue;
            controllerBindingSetter((int)button);
            controllerBindingCapture = null;
            controllerBindingSetter = null;
            return;
        }
    }

    private static readonly GamepadButtons[] AssignableGamepadButtons =
    [
        GamepadButtons.DpadUp, GamepadButtons.DpadDown, GamepadButtons.DpadLeft, GamepadButtons.DpadRight,
        GamepadButtons.North, GamepadButtons.South, GamepadButtons.West, GamepadButtons.East,
        GamepadButtons.L1, GamepadButtons.L2, GamepadButtons.L3,
        GamepadButtons.R1, GamepadButtons.R2, GamepadButtons.R3,
        GamepadButtons.Start, GamepadButtons.Select
    ];

    private static string GamepadButtonDisplayName(GamepadButtons button) => button switch
    {
        GamepadButtons.North => "Top face button",
        GamepadButtons.South => "Bottom face button",
        GamepadButtons.West => "Left face button",
        GamepadButtons.East => "Right face button",
        GamepadButtons.DpadUp => "D-pad Up",
        GamepadButtons.DpadDown => "D-pad Down",
        GamepadButtons.DpadLeft => "D-pad Left",
        GamepadButtons.DpadRight => "D-pad Right",
        GamepadButtons.L1 => "L1",
        GamepadButtons.L2 => "L2",
        GamepadButtons.L3 => "L3",
        GamepadButtons.R1 => "R1",
        GamepadButtons.R2 => "R2",
        GamepadButtons.R3 => "R3",
        GamepadButtons.Start => "Start",
        GamepadButtons.Select => "Select / Back",
        _ => button.ToString()
    };

    private static void ResetGamepadControls()
    {
        Plugin.Cfg.GamepadUp = (int)GamepadButtons.DpadUp;
        Plugin.Cfg.GamepadDown = (int)GamepadButtons.DpadDown;
        Plugin.Cfg.GamepadLeft = (int)GamepadButtons.DpadLeft;
        Plugin.Cfg.GamepadRight = (int)GamepadButtons.DpadRight;
        Plugin.Cfg.GamepadA = (int)GamepadButtons.East;
        Plugin.Cfg.GamepadB = (int)GamepadButtons.South;
        Plugin.Cfg.GamepadX = (int)GamepadButtons.North;
        Plugin.Cfg.GamepadY = (int)GamepadButtons.West;
        Plugin.Cfg.GamepadL = (int)GamepadButtons.L1;
        Plugin.Cfg.GamepadR = (int)GamepadButtons.R1;
        Plugin.Cfg.GamepadStart = (int)GamepadButtons.Start;
        Plugin.Cfg.GamepadSelect = (int)GamepadButtons.Select;
    }

}
