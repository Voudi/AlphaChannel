using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// "Go Live" self-hosted streaming - OBS pushes RTMP to our own MediaMTX ingest, friends watch the
// resulting HLS stream through the exact same "play a URL" primitive (queue.PlayNow) the existing
// YouTube/Twitch flow already uses. See Server/Live/LiveService.cs for the server half and why the
// stream key format keeps the secret out of the public HLS URL.
internal sealed partial class MainWindow
{
    private bool liveStatusDirty = true;
    private bool liveStatusLoading;
    private LiveStatusDto? liveStatus;
    private bool streamKeyRevealed;
    private bool obsConnectionChecking;
    private bool obsConnectionOnline;
    private string? obsConnectionError;
    private bool keyRotating;
    private string? keyError;
    private bool keyRegenerateConfirmPending;

    private bool obsSetupGuideOpen;
    private int obsSetupGuideStep;

    private bool friendsLiveDirty = true;
    private LiveFriendDto[] friendsLive = [];

    private void DrawGoLive()
    {
        if (CurrentSession is not { } session)
        {
            DrawPlainEmpty(
                "OBS ingest + stream keys live here after you sign in.",
                "Open Settings",
                () => currentPage = HomePage.Settings);

            return;
        }

        if (liveStatusDirty && !liveStatusLoading)
        {
            RefreshLiveStatus(session);
        }

        if (friendsLiveDirty)
        {
            RefreshFriendsLive(session.Token);
        }

        // Keep the Go Live content scrollable without scrolling
        // the Player header/source navigation above it.
        using var content = ImRaii.Child(
            "##goLiveContent",
            new Vector2(-1f, -1f),
            false,
            ImGuiWindowFlags.None);

        if (!content)
        {
            return;
        }

        // ---------------------------------------------------------
        // Heading
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Go Live with OBS");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(0f, 8f));

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
            var buttonPos =
                ImGui.GetCursorScreenPos();

            var buttonSize =
                new Vector2(
                    170f,
                    34f);

            if (ImGui.Button(
                "##obsSetupGuide",
                buttonSize))
            {
                obsSetupGuideStep = 0;
                obsSetupGuideOpen = true;
            }

            DrawPlayerActionButtonContent(
                buttonPos,
                buttonSize,
                FontAwesomeIcon.BookOpen,
                "OBS Setup Guide",
                Vector4.One);
        }

        ImGui.Dummy(
            new Vector2(0f, 12f));

        // ---------------------------------------------------------
        // Stream status
        // ---------------------------------------------------------

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.045f,
                0.06f,
                0.10f,
                1f)))
        using (var statusCard =
            ImRaii.Child(
                "##goLiveStatus",
                new Vector2(
                    -1f,
                    110f),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (statusCard)
            {
                // -------------------------------------------------
                // LIVE / OFFLINE
                // -------------------------------------------------

                ImGui.SetCursorPos(
                    new Vector2(
                        14f,
                        12f));

                using (ImRaii.PushFont(
                    UiBuilder.IconFont))
                {
                    ImGui.TextColored(
                        obsConnectionOnline
                            ? Good
                            : MutedText,
                        FontAwesomeIcon.Circle
                            .ToIconString());
                }

                ImGui.SameLine(
                    0f,
                    8f);

                ImGui.TextColored(
                    obsConnectionOnline
                        ? Good
                        : MutedText,
                    obsConnectionOnline
                        ? "LIVE"
                        : "OFFLINE");

                // -------------------------------------------------
                // Main text
                // -------------------------------------------------

                ImGui.SetCursorPos(
                    new Vector2(
                        14f,
                        39f));

                ImGui.TextColored(
                    Vector4.One,
                    obsConnectionOnline
                        ? "OBS stream detected"
                        : "Not streaming right now");

                // -------------------------------------------------
                // Supporting text
                // -------------------------------------------------

                ImGui.SetCursorPos(
                    new Vector2(
                        14f,
                        65f));

                ImGui.SetWindowFontScale(
                    0.82f);

                ImGui.TextColored(
                    MutedText,
                    obsConnectionOnline
                        ? "Your OBS stream is ready to broadcast."
                        : obsConnectionChecking
                            ? "Checking for your OBS stream..."
                            : "Start streaming from OBS, then check the connection.");

                ImGui.SetWindowFontScale(
                    1f);

                // -------------------------------------------------
                // Right-side action buttons
                // -------------------------------------------------

                if (obsConnectionOnline)
                {
                    var broadcastSize =
                        new Vector2(
                            190f,
                            36f);

                    var refreshSize =
                        new Vector2(
                            42f,
                            36f);

                    var totalWidth =
                        broadcastSize.X +
                        8f +
                        refreshSize.X;

                    ImGui.SetCursorPos(
                        new Vector2(
                            ImGui.GetWindowWidth() -
                            totalWidth -
                            14f,
                            29f));

                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        8f))
                    using (ImRaii.PushColor(
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
                            "Broadcast Stream to TV",
                            broadcastSize))
                        {
                            var hlsUrl =
                                BuildMyHlsUrl(
                                    session);

                            queue.PlayNow(
                                new VideoQueueEntry(
                                    hlsUrl,
                                    "My OBS live stream",
                                    "Live",
                                    null,
                                    null));

                            currentPage =
                                HomePage.Player;
                        }
                    }

                    ImGui.SameLine(
                        0f,
                        8f);

                    using (ImRaii.Disabled(
          obsConnectionChecking))
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        8f))
                    using (ImRaii.PushColor(
                        ImGuiCol.Button,
                        Accent)
                        .Push(
                            ImGuiCol.ButtonHovered,
                            AccentHover)
                        .Push(
                            ImGuiCol.ButtonActive,
                            AccentActive))
                    {
                        var buttonPos =
                            ImGui.GetCursorScreenPos();

                        if (ImGui.Button(
                            "##refreshObsConnection",
                            refreshSize))
                        {
                            CheckObsConnection(
                                session);
                        }

                        // Draw the refresh glyph ourselves using the
                        // correct Dalamud icon font.
                        using (ImRaii.PushFont(
                            UiBuilder.IconFont))
                        {
                            var icon =
                                FontAwesomeIcon.SyncAlt
                                    .ToIconString();

                            var iconSize =
                                ImGui.CalcTextSize(
                                    icon);

                            ImGui.GetWindowDrawList()
                                .AddText(
                                    buttonPos +
                                    new Vector2(
                                        (refreshSize.X - iconSize.X) * 0.5f,
                                        (refreshSize.Y - iconSize.Y) * 0.5f),
                                    ImGui.GetColorU32(
                                        Vector4.One),
                                    icon);
                        }
                    }
                }
                else
                {
                    var checkSize =
                        new Vector2(
                            150f,
                            36f);

                    ImGui.SetCursorPos(
                        new Vector2(
                            ImGui.GetWindowWidth() -
                            checkSize.X -
                            14f,
                            29f));

                    using (ImRaii.Disabled(
                        obsConnectionChecking))
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        8f))
                    {
                        if (ImGui.Button(
                            obsConnectionChecking
                                ? "Checking..."
                                : "Check Connection",
                            checkSize))
                        {
                            CheckObsConnection(
                                session);
                        }
                    }
                }
            }
        }

        if (obsConnectionError is { Length: > 0 } connectionError)
        {
            ImGui.Dummy(
                new Vector2(
                    0f,
                    6f));

            ImGui.SetWindowFontScale(
                0.82f);

            ImGui.TextColored(
                MutedText,
                connectionError);

            ImGui.SetWindowFontScale(
                1f);
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                18f));

       

        // ---------------------------------------------------------
        // OBS setup
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(1.08f);

        ImGui.TextColored(
            Vector4.One,
            "OBS setup");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 4f));

        ImGui.SetWindowFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "Connect OBS using the server and stream key below.");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 14f));

        // ---------------------------------------------------------
        // Server
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(0.88f);

        ImGui.TextColored(
            MutedText,
            "Server");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 4f));

        var rtmpServer = BuildRtmpServer();

        ImGui.SetNextItemWidth(-126f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                new Vector2(14f, 10f)))
        using (ImRaii.PushColor(
            ImGuiCol.FrameBg,
            new Vector4(0.045f, 0.06f, 0.105f, 1f))
            .Push(
                ImGuiCol.FrameBgHovered,
                new Vector4(0.045f, 0.06f, 0.105f, 1f))
            .Push(
                ImGuiCol.FrameBgActive,
                new Vector4(0.045f, 0.06f, 0.105f, 1f)))
        {
            ImGui.InputText(
                "##rtmpServer",
                ref rtmpServer,
                256,
                ImGuiInputTextFlags.ReadOnly);
        }

        ImGui.SameLine(0f, 10f);

        // Copy server button
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            new Vector4(0.055f, 0.07f, 0.115f, 1f))
            .Push(
                ImGuiCol.ButtonHovered,
                new Vector4(0.075f, 0.095f, 0.15f, 1f))
            .Push(
                ImGuiCol.ButtonActive,
                new Vector4(0.075f, 0.095f, 0.15f, 1f)))
        {
            var buttonPos =
                ImGui.GetCursorScreenPos();

            var buttonSize =
                new Vector2(110f, 38f);

            if (ImGui.Button(
                "##copyServer",
                buttonSize))
            {
                ImGui.SetClipboardText(
                    rtmpServer);
            }

            DrawPlayerActionButtonContent(
                buttonPos,
                buttonSize,
                FontAwesomeIcon.Copy,
                "Copy",
                Vector4.One);
        }

        ImGui.Dummy(new Vector2(0f, 16f));

        // ---------------------------------------------------------
        // Stream key
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(0.88f);

        ImGui.TextColored(
            MutedText,
            "Stream key");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 4f));

        var cachedKey =
            Plugin.Cfg.StreamKeys.GetValueOrDefault(
                session.AccountId);

        if (cachedKey is null)
        {
            ImGui.SetWindowFontScale(0.82f);

            ImGui.TextColored(
                MutedText,
                liveStatus?.HasKey ?? false
                    ? "A stream key exists on another install. Regenerate it to use it here."
                    : "No stream key yet. Generate one to connect OBS.");

            ImGui.SetWindowFontScale(1f);

            ImGui.Dummy(new Vector2(0f, 8f));
        }
        else
        {
            var displayKey =
                streamKeyRevealed
                    ? cachedKey
                    : new string(
                        '•',
                        Math.Min(
                            cachedKey.Length,
                            32));

            ImGui.SetNextItemWidth(-1f);

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f)
                .Push(
                    ImGuiStyleVar.FramePadding,
                    new Vector2(14f, 10f)))
            using (ImRaii.PushColor(
                ImGuiCol.FrameBg,
                new Vector4(0.045f, 0.06f, 0.105f, 1f))
                .Push(
                    ImGuiCol.FrameBgHovered,
                    new Vector4(0.045f, 0.06f, 0.105f, 1f))
                .Push(
                    ImGuiCol.FrameBgActive,
                    new Vector4(0.045f, 0.06f, 0.105f, 1f)))
            {
                ImGui.InputText(
                    "##streamKey",
                    ref displayKey,
                    256,
                    ImGuiInputTextFlags.ReadOnly);
            }

            ImGui.Dummy(new Vector2(0f, 8f));

            // Reveal / Hide
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(0.055f, 0.07f, 0.115f, 1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(0.075f, 0.095f, 0.15f, 1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(0.075f, 0.095f, 0.15f, 1f)))
            {
                var buttonPos =
                    ImGui.GetCursorScreenPos();

                var buttonSize =
                    new Vector2(110f, 34f);

                if (ImGui.Button(
                    "##toggleStreamKey",
                    buttonSize))
                {
                    streamKeyRevealed =
                        !streamKeyRevealed;
                }

                DrawPlayerActionButtonContent(
                    buttonPos,
                    buttonSize,
                    streamKeyRevealed
                        ? FontAwesomeIcon.EyeSlash
                        : FontAwesomeIcon.Eye,
                    streamKeyRevealed
                        ? "Hide"
                        : "Reveal",
                    Vector4.One);
            }

            ImGui.SameLine(0f, 8f);

            // Copy key
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(0.055f, 0.07f, 0.115f, 1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(0.075f, 0.095f, 0.15f, 1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(0.075f, 0.095f, 0.15f, 1f)))
            {
                var buttonPos =
                    ImGui.GetCursorScreenPos();

                var buttonSize =
                    new Vector2(118f, 34f);

                if (ImGui.Button(
                    "##copyStreamKey",
                    buttonSize))
                {
                    ImGui.SetClipboardText(
                        cachedKey);
                }

                DrawPlayerActionButtonContent(
                    buttonPos,
                    buttonSize,
                    FontAwesomeIcon.Copy,
                    "Copy key",
                    Vector4.One);
            }

            ImGui.SameLine(0f, 8f);
        }

        // Generate / Regenerate
        using (ImRaii.Disabled(keyRotating))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            new Vector4(0.055f, 0.07f, 0.115f, 1f))
            .Push(
                ImGuiCol.ButtonHovered,
                new Vector4(0.075f, 0.095f, 0.15f, 1f))
            .Push(
                ImGuiCol.ButtonActive,
                new Vector4(0.075f, 0.095f, 0.15f, 1f)))
        {
            var buttonPos =
                ImGui.GetCursorScreenPos();

            var buttonSize =
                new Vector2(142f, 34f);

            if (ImGui.Button(
                "##regenerateStreamKey",
                buttonSize))
            {
                if (cachedKey is null)
                {
                    RotateStreamKey(session);
                }
                else
                {
                    keyRegenerateConfirmPending =
                        true;
                }
            }

            DrawPlayerActionButtonContent(
                buttonPos,
                buttonSize,
                FontAwesomeIcon.SyncAlt,
                cachedKey is null
                    ? "Generate"
                    : "Regenerate",
                Vector4.One);
        }

        // Regenerate confirmation
        if (keyRegenerateConfirmPending)
        {
            ImGui.Dummy(
                new Vector2(0f, 10f));

            ImGui.TextColored(
                Danger,
                "Regenerating disconnects OBS sessions using the old key. Continue?");

            ImGui.Dummy(
                new Vector2(0f, 6f));

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                Danger))
            {
                if (ImGui.Button(
                    "Yes, regenerate"))
                {
                    keyRegenerateConfirmPending =
                        false;

                    RotateStreamKey(session);
                }
            }

            ImGui.SameLine(0f, 8f);

            if (ImGui.Button("Cancel"))
            {
                keyRegenerateConfirmPending =
                    false;
            }
        }

        if (keyError is { Length: > 0 } error)
        {
            ImGui.Dummy(
                new Vector2(0f, 8f));

            ImGui.TextColored(
                Danger,
                error);
        }

        ImGui.Dummy(
            new Vector2(0f, 22f));

        // ---------------------------------------------------------
        // Friends live
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(1.08f);

        ImGui.TextColored(
            Vector4.One,
            $"Friends live ({friendsLive.Length})");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(0f, 8f));

        if (friendsLive.Length == 0)
        {
            ImGui.SetWindowFontScale(0.88f);

            ImGui.TextColored(
                MutedText,
                "Nobody you know is live.");

            ImGui.SetWindowFontScale(1f);
        }
        else
        {
            foreach (var friend in friendsLive)
            {
                ImGui.PushID(
                friend.AccountId);

            const float rowHeight = 58f;

            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(0.045f, 0.06f, 0.10f, 1f)))
            using (var row = ImRaii.Child(
                $"##friendLive_{friend.AccountId}",
                new Vector2(-6f, rowHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (row)
                {
                    var origin =
                        ImGui.GetCursorScreenPos();

                    // Live dot
                    using (ImRaii.PushFont(
                        UiBuilder.IconFont))
                    {
                        ImGui.GetWindowDrawList()
                            .AddText(
                                origin +
                                new Vector2(14f, 21f),
                                ImGui.GetColorU32(Good),
                                FontAwesomeIcon.Circle
                                    .ToIconString());
                    }

                    // Friend name
                    ImGui.GetWindowDrawList()
                        .AddText(
                            origin +
                            new Vector2(38f, 20f),
                            ImGui.GetColorU32(
                                Vector4.One),
                            friend.DisplayName);

                    // Watch button
                    var watchSize =
                        new Vector2(104f, 34f);

                    var watchPos =
                        new Vector2(
                            origin.X +
                            ImGui.GetWindowWidth() -
                            116f,
                            origin.Y +
                            (rowHeight -
                             watchSize.Y) *
                            0.5f);

                    ImGui.SetCursorScreenPos(
                        watchPos);

                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        8f))
                    using (ImRaii.PushColor(
                        ImGuiCol.Button,
                        Accent)
                        .Push(
                            ImGuiCol.ButtonHovered,
                            AccentHover)
                        .Push(
                            ImGuiCol.ButtonActive,
                            AccentActive))
                    {
                        var buttonPos =
                            ImGui.GetCursorScreenPos();

                        if (ImGui.Button(
                            $"##watch_{friend.AccountId}",
                            watchSize))
                        {
                            queue.PlayNow(
                                new VideoQueueEntry(
                                    friend.HlsUrl,
                                    $"{friend.DisplayName}'s stream",
                                    "Live",
                                    null,
                                    null));

                            currentPage =
                                HomePage.Player;
                        }

                        DrawPlayerActionButtonContent(
                            buttonPos,
                            watchSize,
                            FontAwesomeIcon.Play,
                            "Watch",
                            Vector4.One);
                    }
                }
            }

                ImGui.PopID();

                ImGui.Dummy(
                    new Vector2(0f, 8f));
            }
        }

        DrawObsSetupGuide(session);
    }

    private void DrawObsSetupGuide(
    CharacterSession session)
    {
        if (obsSetupGuideOpen)
        {
            ImGui.OpenPopup(
                "OBS Setup Guide##obsGuide");
            obsSetupGuideOpen = false;
        }

        ImGui.SetNextWindowSize(
            new Vector2(
                650f,
                520f),
            ImGuiCond.Appearing);

        ImGui.SetNextWindowSizeConstraints(
            new Vector2(
                560f,
                460f),
            new Vector2(
                800f,
                700f));

        var popupOpen = true;

        if (!ImGui.BeginPopupModal(
                "OBS Setup Guide##obsGuide",
                ref popupOpen,
                ImGuiWindowFlags.NoCollapse))
        {
            return;
        }

        // -----------------------------------------------------
        // Heading
        // -----------------------------------------------------

        ImGui.SetWindowFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Set up OBS for Alpha Channel");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                4f));

        ImGui.SetWindowFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "Follow these steps to broadcast OBS to your Alpha Channel TV.");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));

        // -----------------------------------------------------
        // Step selector
        // -----------------------------------------------------

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var spacing = 8f;

        var stepWidth =
            (availableWidth -
             (spacing * 4f)) /
            5f;

        for (var i = 0; i < 5; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(
                    0f,
                    spacing);
            }

            var selected =
                obsSetupGuideStep == i;

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                selected
                    ? Accent
                    : new Vector4(
                        0.055f,
                        0.07f,
                        0.115f,
                        1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    selected
                        ? AccentHover
                        : new Vector4(
                            0.075f,
                            0.095f,
                            0.15f,
                            1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentActive))
            {
                if (ImGui.Button(
                    $"{i + 1}##obsGuideStep{i}",
                    new Vector2(
                        stepWidth,
                        36f)))
                {
                    obsSetupGuideStep = i;
                }
            }
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));

        ImGui.Separator();

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));

        // -----------------------------------------------------
        // Scrollable guide contents
        // -----------------------------------------------------

        var footerHeight = 58f;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.WindowPadding,
            new Vector2(
                16f,
                14f)))
        using (var guideContent =
            ImRaii.Child(
                "##obsGuideContent",
                new Vector2(
                    -1f,
                    -footerHeight),
                false,
                ImGuiWindowFlags.None))
        {
            if (guideContent)
            {
                switch (obsSetupGuideStep)
                {
                    case 0:
                        DrawObsGuideInstall();
                        break;

                    case 1:
                        DrawObsGuideSource();
                        break;

                    case 2:
                        DrawObsGuideOutput();
                        break;

                    case 3:
                        DrawObsGuideConnection(
                            session);
                        break;

                    case 4:
                        DrawObsGuideGoLive();
                        break;
                }

                // Extra breathing room after the final
                // item when scrolled to the bottom.
                ImGui.Dummy(
                    new Vector2(
                        0f,
                        6f));
            }
        }

        // -----------------------------------------------------
        // Fixed navigation footer
        // -----------------------------------------------------

        ImGui.Separator();

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));

        if (obsSetupGuideStep > 0)
        {
            if (ImGui.Button(
                "Back",
                new Vector2(
                    90f,
                    32f)))
            {
                obsSetupGuideStep--;
            }
        }
        else
        {
            ImGui.Dummy(
                new Vector2(
                    90f,
                    32f));
        }

        ImGui.SameLine();

        var rightButtonWidth =
            100f;

        ImGui.SetCursorPosX(
            ImGui.GetWindowWidth() -
            rightButtonWidth -
            16f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            7f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            Accent)
            .Push(
                ImGuiCol.ButtonHovered,
                AccentHover)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            if (obsSetupGuideStep < 4)
            {
                if (ImGui.Button(
                    "Next",
                    new Vector2(
                        rightButtonWidth,
                        32f)))
                {
                    obsSetupGuideStep++;
                }
            }
            else
            {
                if (ImGui.Button(
                    "Done",
                    new Vector2(
                        rightButtonWidth,
                        32f)))
                {
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.EndPopup();
    }

    private void DrawObsGuideInstall()
    {
        DrawObsGuideHeading(
            "1. Install OBS Studio",
            "OBS is the program you'll be using to share your screen / audio with your watch party.");

        ImGui.TextWrapped(
            "Download and install OBS Studio. If you already have OBS installed, you can skip this step.");

        ImGui.Dummy(
            new Vector2(
                0f,
                16f));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
        using (ImRaii.PushColor(
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
                "Open OBS Website",
                new Vector2(
                    170f,
                    36f)))
            {
                Dalamud.Utility.Util.OpenLink(
                    "https://obsproject.com/");
            }
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));

        ImGui.SetWindowFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "OBS Studio is free and open source.");

        ImGui.SetWindowFontScale(
            1f);
    }

    private void DrawObsGuideSource()
    {
        DrawObsGuideHeading(
            "2. Create your OBS source",
            "Choose what you want your viewers to see and hear.");

        ImGui.TextWrapped(
            "In the main OBS window, use the Sources panel at the bottom and press + to add your video source.");

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        DrawObsGuideBullet(
            "Game Capture",
            "Best for streaming a game.");

        DrawObsGuideBullet(
            "Window Capture",
            "Streams one specific application window.");

        DrawObsGuideBullet(
            "Display Capture",
            "Streams everything visible on a monitor.");

        DrawObsGuideBullet(
            "Media Source",
            "Useful for broadcasting a local video file.");

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));

        ImGui.TextColored(
            Gold,
            "Don't forget your audio.");

        ImGui.Dummy(
            new Vector2(
                0f,
                4f));

        ImGui.TextWrapped(
            "Check the OBS Audio Mixer and make sure Desktop Audio, your media source, or whichever audio source you want to broadcast is moving when sound plays.");

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));

        ImGui.TextWrapped(
            "If no sound is being picked up then you'll need to check the Audio tab in the OBS settings.");
    }

    private void DrawObsGuideOutput()
    {
        DrawObsGuideHeading(
            "3. Configure streaming quality",
            "These settings are recommended for smooth Alpha Channel playback.");

        ImGui.TextWrapped(
            "These are only our recommendations. You are able to stream at higher quality levels, but you'll likely want to check that it's loading okay for your viewers.");

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));

        ImGui.TextWrapped(
            "Open OBS Settings, then configure the following:");

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        DrawObsGuideSetting(
            "Video > Base Canvas",
            "1920 x 1080");

        DrawObsGuideSetting(
            "Video > Output Resolution",
            "1280 x 720");

        DrawObsGuideSetting(
            "Video > FPS",
            "30");

        DrawObsGuideSetting(
            "Output > Encoder",
            "NVIDIA NVENC H.264 if available");

        DrawObsGuideSetting(
            "Output > Rate Control",
            "CBR");

        DrawObsGuideSetting(
            "Output > Bitrate",
            "4000 Kbps");

        DrawObsGuideSetting(
            "Output > Keyframe Interval",
            "2 seconds");

        DrawObsGuideSetting(
            "Output > Preset",
            "P5: Slow (Good Quality)");

        DrawObsGuideSetting(
            "Output > Multipass",
            "Single Pass");

        DrawObsGuideSetting(
            "Output > Profile",
            "High");

        DrawObsGuideSetting(
            "Output > Audio Bitrate",
            "160 Kbps");
    }

    private void DrawObsGuideConnection(
    CharacterSession session)
    {
        DrawObsGuideHeading(
            "4. Connect OBS to Alpha Channel",
            "Enter your Alpha Channel server and secret stream key in OBS.");

        ImGui.TextWrapped(
            "Open OBS Settings > Stream. Set Service to Custom, then enter the Server and Stream Key shown below.");

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));

        var server =
            BuildRtmpServer();

        DrawObsGuideCopyField(
            "Server",
            server,
            "##guideServer");

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        var key =
            Plugin.Cfg.StreamKeys.GetValueOrDefault(
                session.AccountId);

        if (key is { Length: > 0 })
        {
            DrawObsGuideCopyField(
                "Stream Key",
                key,
                "##guideKey");

            ImGui.Dummy(
                new Vector2(
                    0f,
                    8f));

            ImGui.SetWindowFontScale(
                0.80f);

            ImGui.TextColored(
                Gold,
                "Keep your stream key private. Anyone with it could publish to your stream.");

            ImGui.SetWindowFontScale(
                1f);
        }
        else
        {
            ImGui.TextColored(
                Gold,
                "You don't currently have a stream key on this installation.");

            ImGui.Dummy(
                new Vector2(
                    0f,
                    6f));

            ImGui.TextWrapped(
                "Close this guide and use Generate under OBS setup, then return to this step.");
        }
    }

    private void DrawObsGuideGoLive()
    {
        DrawObsGuideHeading(
            "5. Start streaming",
            "You're ready to connect OBS to your Alpha Channel TV.");

        DrawObsGuideNumberedLine(
            "1",
            "Click Start Streaming in OBS.");

        DrawObsGuideNumberedLine(
            "2",
            "Return to Alpha Channel and open Player > Go Live.");

        DrawObsGuideNumberedLine(
            "3",
            "Press Check Connection.");

        DrawObsGuideNumberedLine(
            "4",
            "Wait for the status card to turn green and show LIVE.");

        DrawObsGuideNumberedLine(
            "5",
            "Press Broadcast Stream to TV.");

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));

        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.055f,
                0.07f,
                0.115f,
                1f)))
        using (var note =
            ImRaii.Child(
                "##obsGuideFinalNote",
                new Vector2(
                    -1f,
                    72f),
                false,
                ImGuiWindowFlags.NoScrollbar))
        {
            if (note)
            {
                ImGui.SetCursorPos(
                    new Vector2(
                        12f,
                        10f));

                ImGui.TextColored(
                    Gold,
                    "OBS streaming and TV broadcasting are separate.");

                ImGui.SetCursorPosX(
                    12f);

                ImGui.TextWrapped(
                    "Pressing 'Broadcast Stream to TV' is what actually starts sharing your stream with your Alpha Channel watch party and allows you to view it in-game.");
            }
        }
    }

    private void DrawObsGuideHeading(
    string title,
    string subtitle)
    {
        ImGui.SetWindowFontScale(
            1.08f);

        ImGui.TextColored(
            Vector4.One,
            title);

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                4f));

        ImGui.SetWindowFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            subtitle);

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                16f));
    }

    private void DrawObsGuideBullet(
        string title,
        string description)
    {
        ImGui.TextColored(
            Accent,
            "•");

        ImGui.SameLine();

        ImGui.TextColored(
            Vector4.One,
            title);

        ImGui.SameLine();

        ImGui.TextColored(
            MutedText,
            $"— {description}");

        ImGui.Dummy(
            new Vector2(
                0f,
                5f));
    }

    private void DrawObsGuideSetting(
        string name,
        string value)
    {
        var startX =
            ImGui.GetCursorPosX();

        ImGui.TextColored(
            MutedText,
            name);

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            startX + 250f);

        ImGui.TextColored(
            Vector4.One,
            value);

        ImGui.Dummy(
            new Vector2(
                0f,
                5f));
    }

    private void DrawObsGuideNumberedLine(
        string number,
        string text)
    {
        ImGui.TextColored(
            Accent,
            number + ".");

        ImGui.SameLine(
            0f,
            8f);

        ImGui.TextWrapped(
            text);

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));
    }

    private void DrawObsGuideCopyField(
        string label,
        string value,
        string id)
    {
        ImGui.SetWindowFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            label);

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                4f));

        ImGui.SetNextItemWidth(
            -90f);

        var display =
            value;

        ImGui.InputText(
            id,
            ref display,
            512,
            ImGuiInputTextFlags.ReadOnly);

        ImGui.SameLine(
            0f,
            8f);

        if (ImGui.Button(
            $"Copy##{id}",
            new Vector2(
                78f,
                0f)))
        {
            ImGui.SetClipboardText(
                value);
        }
    }

    private string BuildRtmpServer()
    {
        var host = new Uri(Plugin.Cfg.RelayServerUrl).Host;
        return $"rtmp://{host}:1935/live";
    }

    private string BuildMyHlsUrl(CharacterSession session)
    {
        var host =
            new Uri(
                Plugin.Cfg.RelayServerUrl)
            .Host;

        return
            $"http://{host}:8888/live/{session.AccountId}/index.m3u8";
    }

    private void CheckObsConnection(CharacterSession session)
    {
        if (obsConnectionChecking)
        {
            return;
        }

        obsConnectionChecking = true;
        obsConnectionError = null;

        var hlsUrl =
            BuildMyHlsUrl(session);

        _ = Task.Run(async () =>
        {
            try
            {
                using var http =
                    new HttpClient
                    {
                        Timeout =
                            TimeSpan.FromSeconds(5),
                    };

                using var request =
                    new HttpRequestMessage(
                        HttpMethod.Get,
                        hlsUrl);

                using var response =
                    await http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead);

                obsConnectionOnline =
                    response.IsSuccessStatusCode;

                if (!obsConnectionOnline)
                {
                    obsConnectionError =
                        $"Stream not available ({(int)response.StatusCode}).";
                }
            }
            catch
            {
                obsConnectionOnline = false;
                obsConnectionError =
                    "No OBS stream detected.";
            }
            finally
            {
                obsConnectionChecking = false;
            }
        });
    }

    private void RotateStreamKey(CharacterSession session)
    {
        keyRotating = true;
        keyError = null;
        var token = session.Token;
        var accountId = session.AccountId;
        _ = Task.Run(async () =>
        {
            var key = await liveClient.RotateKeyAsync(token);
            keyRotating = false;
            if (key is null)
            {
                keyError = "Couldn't generate a stream key.";
                return;
            }

            Plugin.Cfg.StreamKeys[accountId] = key;
            Plugin.Cfg.Save();
            streamKeyRevealed = true;
            liveStatusDirty = true;
        });
    }

    private void RefreshLiveStatus(CharacterSession session)
    {
        liveStatusDirty = false;
        liveStatusLoading = true;
        var token = session.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                liveStatus = await liveClient.GetMyStatusAsync(token);
            }
            finally
            {
                liveStatusLoading = false;
            }
        });
    }

    private void RefreshFriendsLive(string bearerToken)
    {
        friendsLiveDirty = false;
        _ = Task.Run(async () => friendsLive = await liveClient.GetFriendsLiveAsync(bearerToken));
    }
}
