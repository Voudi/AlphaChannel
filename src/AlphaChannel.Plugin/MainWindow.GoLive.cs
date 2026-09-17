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
    private string? livePatreonAccessMessage;

    private void RefreshLivePatreonAccess()
    {
        if (!HasConfiguredPatreonAccess())
        {
            patreonAccessConfirmed = false;
            livePatreonAccessMessage =
                "No active Patreon membership was found.";
            return;
        }

        patreonAccessConfirmed = true;
        livePatreonAccessMessage = null;
        obsConnectionError = null;

        Plugin.ChatGui.Print(
            $"[AlphaChannel] Patreon access confirmed (tier {Plugin.Cfg.PatreonMembershipTier}).");
    }

    private enum LiveStreamGuideKind
    {
        None,
        ObsStudio,
        GenericRtmp,
    }

    private LiveStreamGuideKind liveStreamGuideKind;
    private int obsSetupGuideStep;
    private int genericStreamGuideStep;

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

        SetUiFontScale(
            1.3f);

        ImGui.TextColored(
            Vector4.One,
            "Live Stream");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 2f));

        SetUiFontScale(
            0.88f);

        ImGui.TextColored(
            MutedText,
            "Stream from OBS Studio or another RTMP-compatible broadcasting app.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 16f));

        // ---------------------------------------------------------
        // Three-step workflow
        // ---------------------------------------------------------

        var cachedStreamKey =
            Plugin.Cfg.StreamKeys.GetValueOrDefault(
                session.AccountId);

        var livePatreonUnlocked =
            HasConfirmedPatreonAccess();

        var hasStreamKey =
            livePatreonUnlocked &&
            !string.IsNullOrWhiteSpace(
                cachedStreamKey);

        var currentLiveStep =
            !hasStreamKey
                ? 1
                : obsConnectionOnline
                    ? 3
                    : 2;

        var workflowOrigin =
            ImGui.GetCursorScreenPos();

        var workflowWidth =
            ImGui.GetContentRegionAvail().X;

        var workflowHeight =
            Ui(54f);

        var workflowDrawList =
            ImGui.GetWindowDrawList();

        var stepCenters =
            new[]
            {
                workflowOrigin.X +
                (workflowWidth * 0.12f),

                workflowOrigin.X +
                (workflowWidth * 0.50f),

                workflowOrigin.X +
                (workflowWidth * 0.88f)
            };

        var stepLabels =
            new[]
            {
                "Add connection details",
                "Start streaming in your app",
                "Broadcast to TV"
            };

        for (var index = 0;
             index < 2;
             index++)
        {
            var lineStart =
                new Vector2(
                    stepCenters[index] +
                    Ui(18f),
                    workflowOrigin.Y +
                    Ui(18f));

            var lineEnd =
                new Vector2(
                    stepCenters[index + 1] -
                    Ui(18f),
                    workflowOrigin.Y +
                    Ui(18f));

            workflowDrawList.AddLine(
                lineStart,
                lineEnd,
                ImGui.GetColorU32(
                    !livePatreonUnlocked &&
                    index == 0
                        ? Accent
                        : index + 1 <
                    currentLiveStep
                        ? Accent
                        : new Vector4(
                            MutedText.X,
                            MutedText.Y,
                            MutedText.Z,
                            0.34f)),
                Ui(2f));
        }

        for (var index = 0;
             index < 3;
             index++)
        {
            var stepNumber =
                index + 1;

            var active =
                stepNumber ==
                currentLiveStep;

            var completed =
                stepNumber <
                currentLiveStep;

            var patreonLockedStep =
                !livePatreonUnlocked &&
                index == 0;

            var circleCenter =
                new Vector2(
                    stepCenters[index],
                    workflowOrigin.Y +
                    Ui(18f));

            workflowDrawList.AddCircleFilled(
                circleCenter,
                Ui(16f),
                ImGui.GetColorU32(
                    patreonLockedStep
                        ? PatreonOrange
                        : active
                        ? Accent
                        : completed
                            ? new Vector4(
                                Accent.X,
                                Accent.Y,
                                Accent.Z,
                                0.55f)
                            : new Vector4(
                                0.10f,
                                0.13f,
                                0.20f,
                                1f)),
                32);

            var numberText =
                patreonLockedStep
                    ? FontAwesomeIcon.Lock.ToIconString()
                    : stepNumber.ToString();

            Vector2 numberSize;

            if (patreonLockedStep)
            {
                using (ImRaii.PushFont(
                           UiBuilder.IconFont))
                {
                    numberSize =
                        ImGui.CalcTextSize(
                            numberText);
                }
            }
            else
            {
                numberSize =
                    ImGui.CalcTextSize(
                        numberText);
            }

            if (patreonLockedStep)
            {
                workflowDrawList.AddText(
                    UiBuilder.IconFont,
                    ImGui.GetFontSize(),
                    circleCenter -
                    numberSize *
                    0.5f,
                    ImGui.GetColorU32(
                        new Vector4(
                            0.055f,
                            0.06f,
                            0.09f,
                            1f)),
                    numberText);
            }
            else
            {
                workflowDrawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    circleCenter -
                    numberSize *
                    0.5f,
                    ImGui.GetColorU32(
                        Vector4.One),
                    numberText);
            }

            var labelSize =
                ImGui.CalcTextSize(
                    stepLabels[index]);

            workflowDrawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    circleCenter.X -
                    (labelSize.X * 0.5f),
                    workflowOrigin.Y +
                    Ui(39f)),
                ImGui.GetColorU32(
                    patreonLockedStep
                        ? PatreonOrange
                        : active
                        ? Accent
                        : MutedText),
                stepLabels[index]);
        }

        ImGui.Dummy(
            new Vector2(
                workflowWidth,
                workflowHeight));

        ImGui.Dummy(
            UiVec(0f, 12f));

        // ---------------------------------------------------------
        // Incoming-stream status
        // ---------------------------------------------------------

        var statusCardHeight =
            Ui(116f);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   9f))
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
                       statusCardHeight),
                   false,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (statusCard)
            {
                var origin =
                    ImGui.GetCursorScreenPos();

                var cardWidth =
                    ImGui.GetWindowWidth();

                var drawList =
                    ImGui.GetWindowDrawList();

                drawList.AddRect(
                    origin,
                    origin +
                    new Vector2(
                        cardWidth,
                        statusCardHeight),
                    ImGui.GetColorU32(
                        new Vector4(
                            obsConnectionOnline
                                ? Good.X
                                : MutedText.X,
                            obsConnectionOnline
                                ? Good.Y
                                : MutedText.Y,
                            obsConnectionOnline
                                ? Good.Z
                                : MutedText.Z,
                            obsConnectionOnline
                                ? 0.25f
                                : 0.14f)),
                    9f,
                    ImDrawFlags.None,
                    1f);

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        origin.X +
                        Ui(16f),
                        origin.Y +
                        Ui(15f)));

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
                    Ui(8f));

                ImGui.TextColored(
                    obsConnectionOnline
                        ? Good
                        : MutedText,
                    obsConnectionOnline
                        ? "STREAM DETECTED"
                        : obsConnectionChecking
                            ? "CHECKING CONNECTION"
                            : "WAITING FOR STREAM");

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        origin.X +
                        Ui(16f),
                        origin.Y +
                        Ui(46f)));

                ImGui.TextColored(
                    Vector4.One,
                    obsConnectionOnline
                        ? "Incoming stream detected"
                        : obsConnectionChecking
                            ? "Looking for an incoming stream..."
                            : "No incoming stream detected");

                SetUiFontScale(
                    0.82f);

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        origin.X +
                        Ui(16f),
                        origin.Y +
                        Ui(76f)));

                ImGui.TextColored(
                    MutedText,
                    obsConnectionOnline
                        ? "Your incoming stream is ready to broadcast."
                        : obsConnectionChecking
                            ? "This normally takes only a few seconds."
                            : "Start streaming from your broadcasting app, then check the connection.");

                SetUiFontScale(
                    1f);

                var checkSize =
                    new Vector2(
                        Ui(142f),
                        Ui(38f));

                var broadcastSize =
                    new Vector2(
                        Ui(172f),
                        Ui(38f));

                var actionGap =
                    Ui(9f);

                var totalActionWidth =
                    checkSize.X +
                    actionGap +
                    broadcastSize.X;

                var actionX =
                    origin.X +
                    cardWidth -
                    totalActionWidth -
                    Ui(14f);

                var actionY =
                    origin.Y +
                    ((statusCardHeight -
                      checkSize.Y) *
                     0.5f);

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        actionX,
                        actionY));

                if (!livePatreonUnlocked)
                {
                    using (ImRaii.Disabled())
                    using (ImRaii.PushStyle(
                               ImGuiStyleVar.FrameRounding,
                               8f))
                    {
                        var lockedCheckOrigin =
                            ImGui.GetCursorScreenPos();

                        ImGui.Button(
                            "##lockedLiveConnectionCheck",
                            checkSize);

                        DrawPlayerActionButtonContent(
                            lockedCheckOrigin,
                            checkSize,
                            FontAwesomeIcon.Lock,
                            "Check connection",
                            MutedText);
                    }

                    if (ImGui.IsItemHovered(
                            ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip(
                            "Unlock live streaming to check your connection.");
                    }
                }
                else
                {
                    using (ImRaii.Disabled(
                               obsConnectionChecking))
                    using (ImRaii.PushStyle(
                               ImGuiStyleVar.FrameRounding,
                               8f))
                    {
                        if (ImGui.Button(
                                obsConnectionChecking
                                    ? "Checking..."
                                    : "Check connection",
                                checkSize))
                        {
                            CheckObsConnection(
                                session);
                        }
                    }
                }

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        actionX +
                        checkSize.X +
                        actionGap,
                        actionY));

                using (ImRaii.Disabled(
                           !livePatreonUnlocked ||
                           !obsConnectionOnline ||
                           stream.Mode ==
                           StreamMode.Viewing))
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
         "Broadcast to TV",
         broadcastSize))
                    {
                        BeginLiveWatchPartyBroadcast(
                            session);
                    }
                }

                if (ImGui.IsItemHovered(
          ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    if (!livePatreonUnlocked)
                    {
                        ImGui.SetTooltip(
                            "A Patreon membership is required to broadcast live streams.");
                    }
                    else if (!obsConnectionOnline)
                    {
                        ImGui.SetTooltip(
                            "Start streaming from your broadcasting app first.");
                    }
                    else if (stream.Mode ==
                             StreamMode.Viewing)
                    {
                        ImGui.SetTooltip(
                            "Leave your current Watch Party before broadcasting.");
                    }
                }
            }
        }

        if (obsConnectionError is { Length: > 0 } connectionError)
        {
            ImGui.Dummy(
                UiVec(0f, 6f));

            SetUiFontScale(
                0.82f);

            ImGui.TextColored(
                MutedText,
                connectionError);

            SetUiFontScale(
                1f);
        }

        ImGui.Dummy(
            UiVec(0f, 18f));



        // ---------------------------------------------------------
        // Compact setup cards
        // ---------------------------------------------------------

        var setupAvailableWidth =
            ImGui.GetContentRegionAvail().X;

        var setupGap =
            Ui(12f);

        var connectionCardWidth =
            (setupAvailableWidth -
             setupGap) *
            0.54f;

        var softwareCardWidth =
            setupAvailableWidth -
            connectionCardWidth -
            setupGap;

        var setupCardHeight =
            Ui(286f);

        DrawLiveConnectionCard(
            session,
            connectionCardWidth,
            setupCardHeight);

        ImGui.SameLine(
            0f,
            setupGap);

        DrawLiveSoftwareCard(
            softwareCardWidth,
            setupCardHeight);


    }

    private void DrawLiveConnectionCard(
    CharacterSession session,
    float width,
    float height)
    {
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   9f))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       0.045f,
                       0.06f,
                       0.10f,
                       1f)))
        using (var card = ImRaii.Child(
                   "##liveConnectionCard",
                   new Vector2(
                       width,
                       height),
                   false,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card)
            {
                return;
            }

            var origin =
                ImGui.GetCursorScreenPos();

            var cardWidth =
                ImGui.GetWindowWidth();

            if (!HasConfirmedPatreonAccess())
            {
                DrawLivePatreonConnectionGate();

                return;
            }

            var padding =
                Ui(16f);

            ImGui.SetCursorScreenPos(
                origin +
                new Vector2(
                    padding,
                    Ui(14f)));

            SetUiFontScale(
                1.08f);

            ImGui.TextColored(
                Vector4.One,
                "Connect your streaming app");

            DrawPatreonFeatureTag();

            SetUiFontScale(
                1f);

            ImGui.SetCursorScreenPos(
                origin +
                new Vector2(
                    padding,
                    Ui(43f)));

            SetUiFontScale(
                0.80f);

            ImGui.TextColored(
                MutedText,
                "Enter these details in your broadcasting software.");

            SetUiFontScale(
                1f);

            var rtmpServer =
                BuildRtmpServer();

            // -------------------------------------------------
            // Server URL
            // -------------------------------------------------

            ImGui.SetCursorScreenPos(
                origin +
                new Vector2(
                    padding,
                    Ui(78f)));

            SetUiFontScale(
                0.82f);

            ImGui.TextColored(
                MutedText,
                "Server URL");

            SetUiFontScale(
                1f);

            var copyServerSize =
                new Vector2(
                    Ui(74f),
                    Ui(36f));

            var serverFieldWidth =
                cardWidth -
                (padding * 2f) -
                copyServerSize.X -
                Ui(8f);

            ImGui.SetCursorScreenPos(
                origin +
                new Vector2(
                    padding,
                    Ui(99f)));

            ImGui.SetNextItemWidth(
                serverFieldWidth);

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       7f)
                   .Push(
                       ImGuiStyleVar.FramePadding,
                       new Vector2(
                           Ui(11f),
                           Ui(9f))))
            using (ImRaii.PushColor(
                       ImGuiCol.FrameBg,
                       new Vector4(
                           0.025f,
                           0.035f,
                           0.065f,
                           1f))
                   .Push(
                       ImGuiCol.FrameBgHovered,
                       new Vector4(
                           0.025f,
                           0.035f,
                           0.065f,
                           1f))
                   .Push(
                       ImGuiCol.FrameBgActive,
                       new Vector4(
                           0.025f,
                           0.035f,
                           0.065f,
                           1f)))
            {
                ImGui.InputText(
                    "##compactRtmpServer",
                    ref rtmpServer,
                    256,
                    ImGuiInputTextFlags.ReadOnly);
            }

            ImGui.SameLine(
                0f,
                Ui(8f));

            if (ImGui.Button(
                    "Copy##compactServerCopy",
                    copyServerSize))
            {
                ImGui.SetClipboardText(
                    rtmpServer);
            }

            // -------------------------------------------------
            // Stream key
            // -------------------------------------------------

            ImGui.SetCursorScreenPos(
                origin +
                new Vector2(
                    padding,
                    Ui(151f)));

            SetUiFontScale(
                0.82f);

            ImGui.TextColored(
                MutedText,
                "Stream key");

            SetUiFontScale(
                1f);

            var cachedKey =
                Plugin.Cfg.StreamKeys.GetValueOrDefault(
                    session.AccountId);

            if (!string.IsNullOrWhiteSpace(
                    cachedKey))
            {
                var displayKey =
                    streamKeyRevealed
                        ? cachedKey
                        : new string(
                            '•',
                            Math.Min(
                                cachedKey.Length,
                                24));

                var revealSize =
                    new Vector2(
                        Ui(72f),
                        Ui(36f));

                var copyKeySize =
                    new Vector2(
                        Ui(66f),
                        Ui(36f));

                var keyFieldWidth =
                    cardWidth -
                    (padding * 2f) -
                    revealSize.X -
                    copyKeySize.X -
                    Ui(16f);

                ImGui.SetCursorScreenPos(
                    origin +
                    new Vector2(
                        padding,
                        Ui(172f)));

                ImGui.SetNextItemWidth(
                    keyFieldWidth);

                using (ImRaii.PushStyle(
                           ImGuiStyleVar.FrameRounding,
                           7f)
                       .Push(
                           ImGuiStyleVar.FramePadding,
                           new Vector2(
                               Ui(11f),
                               Ui(9f))))
                using (ImRaii.PushColor(
                           ImGuiCol.FrameBg,
                           new Vector4(
                               0.025f,
                               0.035f,
                               0.065f,
                               1f))
                       .Push(
                           ImGuiCol.FrameBgHovered,
                           new Vector4(
                               0.025f,
                               0.035f,
                               0.065f,
                               1f))
                       .Push(
                           ImGuiCol.FrameBgActive,
                           new Vector4(
                               0.025f,
                               0.035f,
                               0.065f,
                               1f)))
                {
                    ImGui.InputText(
                        "##compactStreamKey",
                        ref displayKey,
                        512,
                        ImGuiInputTextFlags.ReadOnly);
                }

                ImGui.SameLine(
                    0f,
                    Ui(8f));

                if (ImGui.Button(
                        streamKeyRevealed
                            ? "Hide##compactKeyReveal"
                            : "Reveal##compactKeyReveal",
                        revealSize))
                {
                    streamKeyRevealed =
                        !streamKeyRevealed;
                }

                ImGui.SameLine(
                    0f,
                    Ui(8f));

                if (ImGui.Button(
                        "Copy##compactKeyCopy",
                        copyKeySize))
                {
                    ImGui.SetClipboardText(
                        cachedKey);
                }
            }
            else
            {
                ImGui.SetCursorScreenPos(
                    origin +
                    new Vector2(
                        padding,
                        Ui(177f)));

                ImGui.TextColored(
                    Gold,
                    "No stream key is saved on this installation.");

                ImGui.SetCursorScreenPos(
                    origin +
                    new Vector2(
                        padding,
                        Ui(205f)));

                SetUiFontScale(
                    0.78f);

                ImGui.TextColored(
                    MutedText,
                    "Generate one from Settings > Account.");

                SetUiFontScale(
                    1f);
            }

            // -------------------------------------------------
            // Security note
            // -------------------------------------------------

            ImGui.SetCursorScreenPos(
                origin +
                new Vector2(
                    padding,
                    height -
                    Ui(40f)));

            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    Gold,
                    FontAwesomeIcon.ExclamationTriangle
                        .ToIconString());
            }

            ImGui.SameLine(
                0f,
                Ui(7f));

            SetUiFontScale(
                0.78f);

            ImGui.TextColored(
                Gold,
                "Keep your stream key private.");

            SetUiFontScale(
                1f);
        }
    }

    private void DrawLivePatreonConnectionGate()
    {
        var panelMin =
            ImGui.GetWindowPos();

        var panelSize =
            ImGui.GetWindowSize();

        var panelMax =
            panelMin +
            panelSize;

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            panelMin,
            panelMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.10f)),
            9f);

        drawList.AddCircleFilled(
            new Vector2(
                panelMax.X -
                panelSize.X *
                0.12f,
                (panelMin.Y + panelMax.Y) *
                0.5f),
            panelSize.Y *
            0.92f,
            ImGui.GetColorU32(
                new Vector4(
                    PatreonOrange.X,
                    PatreonOrange.Y,
                    PatreonOrange.Z,
                    0.075f)),
            64);

        drawList.AddRect(
            panelMin,
            panelMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.82f)),
            9f,
            ImDrawFlags.RoundCornersAll,
            1f);

        var orangeBorder =
            ImGui.GetColorU32(
                new Vector4(
                    PatreonOrange.X,
                    PatreonOrange.Y,
                    PatreonOrange.Z,
                    0.82f));

        var middleX =
            (panelMin.X + panelMax.X) *
            0.5f;

        drawList.AddLine(
            new Vector2(
                middleX,
                panelMin.Y),
            new Vector2(
                panelMax.X -
                Ui(9f),
                panelMin.Y),
            orangeBorder);

        drawList.AddLine(
            new Vector2(
                panelMax.X,
                panelMin.Y +
                Ui(9f)),
            new Vector2(
                panelMax.X,
                panelMax.Y -
                Ui(9f)),
            orangeBorder);

        drawList.AddLine(
            new Vector2(
                panelMax.X -
                Ui(9f),
                panelMax.Y),
            new Vector2(
                middleX,
                panelMax.Y),
            orangeBorder);

        DrawLocalVideoPatreonHeart(
            new Vector2(
                (panelMin.X + panelMax.X) *
                0.5f,
                panelMin.Y +
                Ui(44f)));

        ImGui.SetCursorPosY(
            Ui(72f));

        DrawPatreonCenteredText(
            "PATREON FEATURE",
            PatreonOrange,
            0.82f);

        DrawPatreonCenteredText(
            "Unlock live streaming",
            Vector4.One,
            1.22f);

        DrawPatreonCenteredText(
            "Join our Patreon to access your private stream connection",
            MutedText,
            0.86f);

        DrawPatreonCenteredText(
            "details and broadcast from OBS or another streaming app.",
            MutedText,
            0.86f);

        var unlockSize =
            new Vector2(
                MathF.Min(
                    Ui(280f),
                    panelSize.X -
                    Ui(44f)),
                Ui(42f));

        ImGui.SetCursorPosX(
            (panelSize.X -
             unlockSize.X) *
            0.5f);

        DrawDjActionButton(
            "##unlockLiveStreamingWithPatreon",
            FontAwesomeIcon.LockOpen,
            "Unlock with Patreon",
            unlockSize,
            false,
            () =>
            {
                patreonPopupOpen = true;
            },
            true);

        var refreshSize =
            new Vector2(
                unlockSize.X,
                Ui(25f));

        ImGui.SetCursorPosX(
            (panelSize.X -
             refreshSize.X) *
            0.5f);

        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   Vector4.Zero)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.14f))
                   .Push(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.24f))
                   .Push(
                       ImGuiCol.Text,
                       MutedText))
        {
            if (ImGui.Button(
                    "Already a member? Refresh access##refreshLivePatreon",
                    refreshSize))
            {
                RefreshLivePatreonAccess();
            }
        }

        if (livePatreonAccessMessage is { } accessMessage)
        {
            DrawPatreonCenteredText(
                accessMessage,
                Danger,
                0.78f);
        }
    }

    private void DrawLiveSoftwareCard(
        float width,
        float height)
    {
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   9f))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       0.045f,
                       0.06f,
                       0.10f,
                       1f)))
        using (var card = ImRaii.Child(
                   "##liveSoftwareCard",
                   new Vector2(
                       width,
                       height),
                   false,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card)
            {
                return;
            }

            var origin =
                ImGui.GetCursorScreenPos();

            var cardWidth =
                ImGui.GetWindowWidth();

            var padding =
                Ui(16f);

            ImGui.SetCursorScreenPos(
                origin +
                new Vector2(
                    padding,
                    Ui(14f)));

            SetUiFontScale(
                1.08f);

            ImGui.TextColored(
                Vector4.One,
                "Streaming software");

            SetUiFontScale(
                1f);

            var recommendationTop =
                origin.Y +
                Ui(50f);

            var recommendationHeight =
                Ui(150f);

            var recommendationMin =
                new Vector2(
                    origin.X +
                    padding,
                    recommendationTop);

            var recommendationMax =
                new Vector2(
                    origin.X +
                    cardWidth -
                    padding,
                    recommendationTop +
                    recommendationHeight);

            var drawList =
                ImGui.GetWindowDrawList();

            drawList.AddRectFilled(
                recommendationMin,
                recommendationMax,
                ImGui.GetColorU32(
                    new Vector4(
                        0.025f,
                        0.035f,
                        0.065f,
                        1f)),
                Ui(8f));

            drawList.AddRect(
                recommendationMin,
                recommendationMax,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.20f)),
                Ui(8f),
                ImDrawFlags.None,
                1f);

            ImGui.SetCursorScreenPos(
                recommendationMin +
                new Vector2(
                    Ui(14f),
                    Ui(14f)));

            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    Accent,
                    FontAwesomeIcon.Desktop
                        .ToIconString());
            }

            ImGui.SameLine(
                0f,
                Ui(10f));

            ImGui.TextColored(
                Vector4.One,
                "OBS Studio");

            ImGui.SameLine(
                0f,
                Ui(8f));

            SetUiFontScale(
                0.70f);

            ImGui.TextColored(
                Accent,
                "RECOMMENDED");

            SetUiFontScale(
                1f);

            ImGui.SetCursorScreenPos(
                recommendationMin +
                new Vector2(
                    Ui(14f),
                    Ui(48f)));

            SetUiFontScale(
                0.78f);

            ImGui.TextColored(
                MutedText,
                "Free, widely supported, and easy to configure.");

            SetUiFontScale(
                1f);

            var buttonGap =
                Ui(8f);

            var innerButtonWidth =
                ((recommendationMax.X -
                  recommendationMin.X) -
                 Ui(28f) -
                 buttonGap) /
                2f;

            ImGui.SetCursorScreenPos(
                recommendationMin +
                new Vector2(
                    Ui(14f),
                    Ui(93f)));

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       7f))
            {
                if (ImGui.Button(
          "OBS setup guide##compactObsGuide",
          new Vector2(
              innerButtonWidth,
              Ui(38f))))
                {
                    obsSetupGuideStep =
                        0;

                    liveStreamGuideKind =
                        LiveStreamGuideKind.ObsStudio;
                }

                ImGui.SameLine(
                    0f,
                    buttonGap);

                if (ImGui.Button(
        "Other apps##otherStreamingApps",
        new Vector2(
            innerButtonWidth,
            Ui(38f))))
                {
                    genericStreamGuideStep =
                        0;

                    liveStreamGuideKind =
                        LiveStreamGuideKind.GenericRtmp;
                }
            }

            ImGui.SetCursorScreenPos(
                origin +
                new Vector2(
                    padding,
                    Ui(222f)));

            SetUiFontScale(
                0.76f);

            ImGui.PushTextWrapPos(
                origin.X +
                cardWidth -
                padding);

            ImGui.TextColored(
                MutedText,
                "Also works with apps that support a custom RTMP server and stream key.");

            ImGui.PopTextWrapPos();

            SetUiFontScale(
                1f);
        }
    }

    private void DrawLiveStreamGuideOverlay()
    {
        if (liveStreamGuideKind ==
            LiveStreamGuideKind.None)
        {
            return;
        }

        var parentPosition =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var panelWidth =
            MathF.Min(
                Ui(700f),
                parentSize.X -
                Ui(40f));

        var panelHeight =
            MathF.Min(
                Ui(560f),
                parentSize.Y -
                Ui(40f));

        var panelPosition =
            parentPosition +
            new Vector2(
                (parentSize.X -
                 panelWidth) *
                0.5f,

                (parentSize.Y -
                 panelHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
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
                "##liveStreamGuideOverlay",
                overlayFlags))
        {
            ImGui.End();

            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            parentPosition,
            parentPosition +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.56f)));

        ImGui.SetCursorScreenPos(
            panelPosition);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   Ui(14f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildBorderSize,
                   Ui(1f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.WindowPadding,
                   UiVec(
                       24f,
                       20f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       0.025f,
                       0.03f,
                       0.06f,
                       0.995f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.82f)))
        using (var panel = ImRaii.Child(
                   "##liveStreamGuidePanel",
                   new Vector2(
                       panelWidth,
                       panelHeight),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (panel)
            {
                var contentOrigin =
                    ImGui.GetCursorScreenPos();

                var closeSize =
                    new Vector2(
                        Ui(32f),
                        Ui(32f));

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        panelPosition.X +
                        panelWidth -
                        Ui(24f) -
                        closeSize.X,
                        panelPosition.Y +
                        Ui(16f)));

                DrawDjActionButton(
                    "##closeLiveStreamGuide",
                    FontAwesomeIcon.Times,
                    string.Empty,
                    closeSize,
                    false,
                    CloseLiveStreamGuide);

                ImGui.SetCursorScreenPos(
                    contentOrigin);

                switch (liveStreamGuideKind)
                {
                    case LiveStreamGuideKind.ObsStudio:
                        if (CurrentSession is { } session)
                        {
                            DrawDjGuideShell(
                                "OBS Studio Setup",
                                "Configure OBS Studio to send live video to your Alpha Channel TV.",
                                5,
                                ref obsSetupGuideStep,
                                step =>
                                {
                                    switch (step)
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
                                },
                                CloseLiveStreamGuide);
                        }

                        break;

                    case LiveStreamGuideKind.GenericRtmp:
                        DrawDjGuideShell(
                            "Other RTMP App Setup",
                            "Connect another RTMP-compatible broadcasting app to Alpha Channel.",
                            4,
                            ref genericStreamGuideStep,
                            step =>
                            {
                                switch (step)
                                {
                                    case 0:
                                        DrawGenericStreamGuideChooseApp();

                                        break;

                                    case 1:
                                        DrawGenericStreamGuideConnection();

                                        break;

                                    case 2:
                                        DrawGenericStreamGuideQuality();

                                        break;

                                    case 3:
                                        DrawGenericStreamGuideGoLive();

                                        break;
                                }
                            },
                            CloseLiveStreamGuide);

                        break;
                }
            }
        }

        ImGui.End();
    }

    private void CloseLiveStreamGuide()
    {
        liveStreamGuideKind =
            LiveStreamGuideKind.None;
    }

    private void DrawGenericStreamGuideChooseApp()
    {
        DrawObsGuideHeading(
            "1. Choose a broadcasting app",
            "Use software that supports a custom RTMP server and stream key.");

        ImGui.TextWrapped(
            "Alpha Channel is not limited to OBS Studio. Any broadcasting application that can publish to a custom RTMP destination may be compatible.");

        ImGui.Dummy(
            UiVec(0f, 14f));

        DrawObsGuideBullet(
            "Streamlabs Desktop",
            "A streaming-focused application based on OBS.");

        DrawObsGuideBullet(
            "XSplit Broadcaster",
            "A commercial broadcasting and scene-production application.");

        DrawObsGuideBullet(
            "vMix",
            "A production suite for advanced live-video workflows.");

        DrawObsGuideBullet(
            "FFmpeg",
            "A command-line option for sending existing video or generated output.");

        ImGui.Dummy(
            UiVec(0f, 14f));

        ImGui.TextColored(
            Gold,
            "Look for Custom RTMP or Custom Streaming Service.");

        ImGui.Dummy(
            UiVec(0f, 5f));

        ImGui.TextWrapped(
            "The exact wording and location varies between applications. If the app cannot accept both a server URL and stream key, it may not be suitable.");
    }

    private void DrawGenericStreamGuideConnection()
    {
        DrawObsGuideHeading(
            "2. Enter the connection details",
            "Copy Alpha Channel's server URL and stream key into your broadcasting app.");

        ImGui.TextWrapped(
            "Open your application's streaming, broadcast or output settings. Choose its Custom RTMP option, then enter the following values.");

        ImGui.Dummy(
            UiVec(0f, 14f));

        var server =
            BuildRtmpServer();

        DrawObsGuideCopyField(
            "Server URL",
            server,
            "##genericGuideServer");

        ImGui.Dummy(
            UiVec(0f, 12f));

        var key =
            CurrentSession is { } session
                ? Plugin.Cfg.StreamKeys.GetValueOrDefault(
                    session.AccountId)
                : null;

        if (key is { Length: > 0 })
        {
            DrawObsGuideCopyField(
                "Stream Key",
                key,
                "##genericGuideKey");

            ImGui.Dummy(
                UiVec(0f, 10f));

            ImGui.TextColored(
                Gold,
                "Keep your stream key private.");

            ImGui.Dummy(
                UiVec(0f, 4f));

            SetUiFontScale(
                0.80f);

            ImGui.TextWrapped(
                "Anyone with this key may be able to publish video to your Alpha Channel stream.");

            SetUiFontScale(
                1f);
        }
        else
        {
            ImGui.TextColored(
                Gold,
                "No stream key is saved on this installation.");

            ImGui.Dummy(
                UiVec(0f, 6f));

            ImGui.TextWrapped(
                "Close this guide and generate a stream key from Settings > Account.");
        }
    }

    private void DrawGenericStreamGuideQuality()
    {
        DrawObsGuideHeading(
            "3. Configure video and audio",
            "Use broadly compatible settings for reliable playback.");

        ImGui.TextWrapped(
            "The option names vary between applications. Use the closest available equivalents to these recommended settings.");

        ImGui.Dummy(
            UiVec(0f, 14f));

        DrawObsGuideSetting(
            "Output Resolution",
            "1280 x 720");

        DrawObsGuideSetting(
            "Frame Rate",
            "30 FPS");

        DrawObsGuideSetting(
            "Video Encoder",
            "H.264");

        DrawObsGuideSetting(
            "Rate Control",
            "CBR");

        DrawObsGuideSetting(
            "Video Bitrate",
            "4000 Kbps");

        DrawObsGuideSetting(
            "Keyframe Interval",
            "2 seconds");

        DrawObsGuideSetting(
            "Audio Encoder",
            "AAC");

        DrawObsGuideSetting(
            "Audio Bitrate",
            "160 Kbps");

        ImGui.Dummy(
            UiVec(0f, 12f));

        SetUiFontScale(
            0.80f);

        ImGui.TextColored(
            MutedText,
            "Higher resolutions and bitrates may work, but can take longer to load or cause buffering for viewers.");

        SetUiFontScale(
            1f);
    }

    private void DrawGenericStreamGuideGoLive()
    {
        DrawObsGuideHeading(
            "4. Start broadcasting",
            "Start the external stream, confirm it reaches Alpha Channel, then broadcast it to your TV.");

        DrawObsGuideNumberedLine(
            "1",
            "Start streaming or broadcasting from your chosen application.");

        DrawObsGuideNumberedLine(
            "2",
            "Return to Alpha Channel and open Media Player > Add Media > Stream Live.");

        DrawObsGuideNumberedLine(
            "3",
            "Press Check connection.");

        DrawObsGuideNumberedLine(
            "4",
            "Wait for the status card to show STREAM DETECTED.");

        DrawObsGuideNumberedLine(
            "5",
            "Press Broadcast to TV.");

        ImGui.Dummy(
            UiVec(0f, 14f));

        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       0.055f,
                       0.07f,
                       0.115f,
                       1f)))
        using (var note = ImRaii.Child(
                   "##genericStreamGuideFinalNote",
                   new Vector2(
                       -1f,
                       Ui(76f)),
                   false,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (note)
            {
                ImGui.SetCursorPos(
                    new Vector2(
                        Ui(12f),
                        Ui(10f)));

                ImGui.TextColored(
                    Gold,
                    "Connecting and broadcasting are separate steps.");

                ImGui.SetCursorPosX(
                    Ui(12f));

                ImGui.PushTextWrapPos(
                    ImGui.GetWindowWidth() -
                    Ui(12f));

                ImGui.TextWrapped(
                    "Starting your broadcasting app sends the stream to Alpha Channel. Broadcast to TV then shares it through your Watch Party.");

                ImGui.PopTextWrapPos();
            }
        }
    }

    

    private void DrawObsGuideInstall()
    {
        DrawObsGuideHeading(
            "1. Install OBS Studio",
            "OBS is the program you'll be using to share your screen / audio with your watch party.");

        ImGui.TextWrapped(
            "Download and install OBS Studio. If you already have OBS installed, you can skip this step.");

        ImGui.Dummy(
            UiVec(0f, 16f));

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
                UiVec(170f, 36f)))
            {
                Dalamud.Utility.Util.OpenLink(
                    "https://obsproject.com/");
            }
        }

        ImGui.Dummy(
            UiVec(0f, 14f));

        SetUiFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "OBS Studio is free and open source.");

        SetUiFontScale(
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
            UiVec(0f, 12f));

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
            UiVec(0f, 14f));

        ImGui.TextColored(
            Gold,
            "Don't forget your audio.");

        ImGui.Dummy(
            UiVec(0f, 4f));

        ImGui.TextWrapped(
            "Check the OBS Audio Mixer and make sure Desktop Audio, your media source, or whichever audio source you want to broadcast is moving when sound plays.");

        ImGui.Dummy(
            UiVec(0f, 8f));

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
            UiVec(0f, 10f));

        ImGui.TextWrapped(
            "Open OBS Settings, then configure the following:");

        ImGui.Dummy(
            UiVec(0f, 12f));

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
            UiVec(0f, 14f));

        var server =
            BuildRtmpServer();

        DrawObsGuideCopyField(
            "Server",
            server,
            "##guideServer");

        ImGui.Dummy(
            UiVec(0f, 12f));

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
                UiVec(0f, 8f));

            SetUiFontScale(
                0.80f);

            ImGui.TextColored(
                Gold,
                "Keep your stream key private. Anyone with it could publish to your stream.");

            SetUiFontScale(
                1f);
        }
        else
        {
            ImGui.TextColored(
                Gold,
                "You don't currently have a stream key on this installation.");

            ImGui.Dummy(
                UiVec(0f, 6f));

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
            "Return to this page on Alpha Channel.");

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
            UiVec(0f, 14f));

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
                    Ui(72f)),
                false,
                ImGuiWindowFlags.NoScrollbar))
        {
            if (note)
            {
                ImGui.SetCursorPos(
                    UiVec(12f, 10f));

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
        SetUiFontScale(
            1.08f);

        ImGui.TextColored(
            Vector4.One,
            title);

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 4f));

        SetUiFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            subtitle);

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 16f));
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
            UiVec(0f, 5f));
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
            UiVec(0f, 5f));
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
            UiVec(0f, 8f));
    }

    private void DrawObsGuideCopyField(
        string label,
        string value,
        string id)
    {
        SetUiFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            label);

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 4f));

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
            UiVec(78f, 0f)))
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

    private void BeginLiveWatchPartyBroadcast(
    CharacterSession session)
    {
        if (!HasConfirmedPatreonAccess())
        {
            obsConnectionError =
                "A Patreon membership is required to broadcast live streams.";

            Plugin.ChatGui.Print(
                "[AlphaChannel] A Patreon membership is required to broadcast live streams.");

            return;
        }

        if (!obsConnectionOnline)
        {
            return;
        }

        if (stream.Mode ==
            StreamMode.Viewing)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Leave your current Watch Party before broadcasting your live stream.");

            return;
        }

        if (stream.Mode ==
            StreamMode.Hosting)
        {
            StartLiveBroadcastInHostedRoom();

            return;
        }

        pendingWatchPartyMediaKind =
            PendingWatchPartyMediaKind.LiveStreamBroadcast;

        watchPartyCreationPopupOpen =
            true;

        createRoomPassword =
            string.Empty;

        createLockedRoomPasswordError =
            null;
    }

    private void StartLiveBroadcastInHostedRoom(
        bool navigateToWatchParty = false)
    {
        if (!HasConfirmedPatreonAccess() ||
            CurrentSession is not { } session ||
            !obsConnectionOnline)
        {
            return;
        }

        var hlsUrl =
            BuildMyHlsUrl(
                session);

        queue.PlayTransient(
            new VideoQueueEntry(
                hlsUrl,
                "Live Streaming",
                "Live Stream",
                null,
                null,
                0d,
                true));

        if (navigateToWatchParty)
        {
            currentPage =
                HomePage.WatchAlong;

            partyPanelTab =
                PartyPanelTab.NowPlaying;
        }

        Plugin.ChatGui.Print(
            "[AlphaChannel] Now broadcasting live stream to Watch Party");
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
        if (!HasConfirmedPatreonAccess())
        {
            obsConnectionOnline = false;
            obsConnectionError =
                "Unlock live streaming to check your connection.";
            return;
        }

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
                using var timeout =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(5));

                using var http =
                    Net.PluginHttpClients.CreateProbeClient();

                using var request =
                    new HttpRequestMessage(
                        HttpMethod.Get,
                        hlsUrl);

                using var response =
                    await http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token);

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
                keyError =
                    liveClient.LastFailure?.UserMessage ??
                    "Couldn't generate a stream key.";
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
