using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Player is the single watch surface: source switcher, quiet empty deck, queue, and watch party.
internal sealed partial class MainWindow
{
    private enum PlayerDrawer
    {
        Player,
        PlayVideo,
        Queue
    }

    private void DrawPlayerDrawerTabs()
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;

        const float gap = 8f;
        const int tabCount = 3;

        var buttonWidth =
            (availableWidth - (gap * (tabCount - 1))) /
            tabCount;

        var buttonSize =
            new Vector2(
                buttonWidth,
                46f);

        DrawPlayerDrawerTab(
            FontAwesomeIcon.Tv,
            "Now Playing",
            PlayerDrawer.Player,
            buttonSize);

        ImGui.SameLine(0, gap);

        DrawPlayerDrawerTab(
            FontAwesomeIcon.Plus,
            "Add Media",
            PlayerDrawer.PlayVideo,
            buttonSize);

        ImGui.SameLine(0, gap);

        DrawPlayerDrawerTab(
            FontAwesomeIcon.List,
            $"Queue ({queue.Entries.Count})",
            PlayerDrawer.Queue,
            buttonSize);
    }
    private double queueAddedFeedbackUntil;


    //
    // Local Video
    //

    private string localVideoSelectedPath =
        string.Empty;

    private string? localVideoError;

    private readonly FileDialogManager localVideoFileDialog =
        new();


    private PlayerDrawer activePlayerDrawer =
        PlayerDrawer.Player;
    private void DrawPlayerPage()
    {
        DrawPlayerDrawerTabs();

        ImGui.Spacing();

        ImGui.Separator();

        ImGui.Spacing();
        ImGui.Spacing();

        switch (activePlayerDrawer)
        {
            case PlayerDrawer.Player:
                DrawPlayerPreviewDrawer();
                break;

            case PlayerDrawer.PlayVideo:
                DrawPlayVideoDrawer();
                break;

            case PlayerDrawer.Queue:
                DrawQueueDrawer();
                break;
        }
    }

    private void DrawPlayerDrawerTab(
    FontAwesomeIcon icon,
    string label,
    PlayerDrawer drawer,
    Vector2 size)
    {
        var selected = activePlayerDrawer == drawer;

        var buttonPos = ImGui.GetCursorScreenPos();

        var bg = selected
            ? Accent
            : new Vector4(0.055f, 0.07f, 0.115f, 1f);

        var hoverBg = selected
            ? AccentHover
            : new Vector4(0.075f, 0.095f, 0.15f, 1f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            bg)
            .Push(
                ImGuiCol.ButtonHovered,
                hoverBg)
            .Push(
                ImGuiCol.ButtonActive,
                selected ? AccentActive : hoverBg))
        {
            if (ImGui.Button(
                $"##drawer_{drawer}",
                size))
            {
                activePlayerDrawer = drawer;
            }
        }

        var drawList = ImGui.GetWindowDrawList();

        if (!selected)
        {
            drawList.AddRect(
                buttonPos,
                buttonPos + size,
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.10f)),
                8f,
                ImDrawFlags.None,
                1f);
        }

        var iconText = icon.ToIconString();

        Vector2 iconSize;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(iconText);
        }

        var textSize = ImGui.CalcTextSize(label);

        const float gap = 9f;

        var totalWidth =
            iconSize.X +
            gap +
            textSize.X;

        var start = new Vector2(
            buttonPos.X + (size.X - totalWidth) * 0.5f,
            buttonPos.Y + (size.Y - textSize.Y) * 0.5f);

        var textColor = selected
            ? Vector4.One
            : MutedText;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                start,
                ImGui.GetColorU32(textColor),
                iconText);
        }

        drawList.AddText(
            start + new Vector2(iconSize.X + gap, 0f),
            ImGui.GetColorU32(textColor),
            label);
    }

    private void DrawPlayerPreviewDrawer()
    {
        var current = queue.Current;

        // ---------------------------------------------------------
        // Now playing
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Now Playing Preview");

        ImGui.SetWindowFontScale(1f);

        ImGui.SetWindowFontScale(0.72f);

        ImGui.TextColored(
            MutedText,
            "Check what's displayed on your room's virtual screen");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(0f, 4f));

        DrawPlayerPreviewSurface(current);

        ImGui.Dummy(
            new Vector2(0f, 6f));

        // ---------------------------------------------------------
        // Up next
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "UP NEXT");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(0f, 4f));

        DrawPlayerUpNext();
    }

    private void DrawPlayerPreviewSurface(
        Video.VideoQueueEntry? current)
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        // Keep the preview relatively cinematic without consuming
        // the entire page vertically.
        var previewWidth =
     MathF.Min(
         availableWidth,
         680f);

        var previewHeight =
            previewWidth * 9f / 16f;

        var remaining =
            availableWidth - previewWidth;

        if (remaining > 0f)
        {
            ImGui.SetCursorPosX(
                ImGui.GetCursorPosX() +
                remaining * 0.5f);
        }

        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                previewWidth,
                previewHeight);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    0.025f,
                    0.032f,
                    0.055f,
                    1f)),
            10f);

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.14f)),
            10f,
            ImDrawFlags.None,
            1f);

        if (current is null)
        {
            DrawPlayerEmptyPreview(
                origin,
                size);
        }
        else
        {
            // Live video texture goes here in the next step.
            DrawPlayerWaitingPreview(
                origin,
                size);
        }

        ImGui.Dummy(size);

        if (current is null)
        {
            return;
        }

        ImGui.Dummy(
            new Vector2(0f, 10f));

        ImGui.SetWindowFontScale(1.08f);

        ImGui.TextColored(
            Vector4.One,
            current.Title);

        ImGui.SetWindowFontScale(1f);

        if (!string.IsNullOrWhiteSpace(
                current.Source))
        {
            ImGui.Dummy(
                new Vector2(0f, 2f));

            ImGui.SetWindowFontScale(0.88f);

            ImGui.TextColored(
                MutedText,
                current.Source);

            ImGui.SetWindowFontScale(1f);
        }
    }

    private void DrawPlayerEmptyPreview(
        Vector2 origin,
        Vector2 size)
    {
        var drawList =
            ImGui.GetWindowDrawList();

        var icon =
            FontAwesomeIcon.PlayCircle
                .ToIconString();

        Vector2 iconSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            iconSize =
                ImGui.CalcTextSize(icon);

            drawList.AddText(
                origin +
                new Vector2(
                    (size.X - iconSize.X) * 0.5f,
                    size.Y * 0.5f - 34f),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.75f)),
                icon);
        }

        const string message =
            "Choose a video to begin playing";

        var messageSize =
            ImGui.CalcTextSize(message);

        drawList.AddText(
            origin +
            new Vector2(
                (size.X - messageSize.X) * 0.5f,
                size.Y * 0.5f + 5f),
            ImGui.GetColorU32(
                MutedText),
            message);
    }

    private void DrawPlayerWaitingPreview(
    Vector2 origin,
    Vector2 size)
    {
        var engine =
            screenController.Engine;

        var drawList =
            ImGui.GetWindowDrawList();

        if (engine.IsActive &&
            engine.PreviewTextureHandle != nint.Zero)
        {
            drawList.AddImageRounded(
     new ImTextureID(
         unchecked((ulong)engine.PreviewTextureHandle)),
     origin,
     origin + size,
     Vector2.Zero,
     Vector2.One,
     uint.MaxValue,
     10f);

            return;
        }

        const string message =
            "Preparing video preview...";

        var messageSize =
            ImGui.CalcTextSize(message);

        drawList.AddText(
            origin +
            new Vector2(
                (size.X - messageSize.X) * 0.5f,
                (size.Y - messageSize.Y) * 0.5f),
            ImGui.GetColorU32(
                MutedText),
            message);
    }

    private void DrawPlayerUpNext()
    {
        if (queue.Entries.Count == 0)
        {
            const float emptyHeight = 92f;

            var origin =
                ImGui.GetCursorScreenPos();

            var width =
                ImGui.GetContentRegionAvail().X;

            var size =
                new Vector2(
                    width,
                    emptyHeight);

            var drawList =
                ImGui.GetWindowDrawList();

            drawList.AddRectFilled(
                origin,
                origin + size,
                ImGui.GetColorU32(
                    new Vector4(
                        0.035f,
                        0.045f,
                        0.075f,
                        1f)),
                8f);

            drawList.AddRect(
                origin,
                origin + size,
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.12f)),
                8f);

            const string message =
                "There's no video in the queue.";

            var textSize =
                ImGui.CalcTextSize(message);

            drawList.AddText(
                origin +
                new Vector2(
                    (size.X - textSize.X) * 0.5f,
                    (size.Y - textSize.Y) * 0.5f),
                ImGui.GetColorU32(
                    MutedText),
                message);

            ImGui.Dummy(size);
        }
        else
        {
            var next =
                queue.Entries[0];

            const float rowHeight = 92f;
            const float thumbWidth = 156f;

            var origin =
                ImGui.GetCursorScreenPos();

            var width =
                ImGui.GetContentRegionAvail().X;

            var size =
                new Vector2(
                    width,
                    rowHeight);

            var drawList =
                ImGui.GetWindowDrawList();

            drawList.AddRectFilled(
                origin,
                origin + size,
                ImGui.GetColorU32(
                    new Vector4(
                        0.045f,
                        0.06f,
                        0.10f,
                        1f)),
                8f);

            var thumbnail =
                thumbnails.Get(
                    next.ThumbnailUrl);

            if (thumbnail is not null)
            {
                drawList.AddImageRounded(
                    thumbnail.Handle,
                    origin,
                    origin +
                    new Vector2(
                        thumbWidth,
                        rowHeight),
                    Vector2.Zero,
                    Vector2.One,
                    uint.MaxValue,
                    8f);
            }
            else
            {
                drawList.AddRectFilled(
                    origin,
                    origin +
                    new Vector2(
                        thumbWidth,
                        rowHeight),
                    ImGui.GetColorU32(
                        new Vector4(
                            0.025f,
                            0.032f,
                            0.055f,
                            1f)),
                    8f);
            }

            var contentX =
                origin.X +
                thumbWidth +
                14f;

            ImGui.SetCursorScreenPos(
                new Vector2(
                    contentX,
                    origin.Y + 17f));

            ImGui.TextColored(
                Vector4.One,
                next.Title);

            if (!string.IsNullOrWhiteSpace(
                    next.Source))
            {
                ImGui.SetCursorScreenPos(
                    new Vector2(
                        contentX,
                        origin.Y + 48f));

                ImGui.SetWindowFontScale(0.86f);

                ImGui.TextColored(
                    MutedText,
                    next.Source);

                ImGui.SetWindowFontScale(1f);
            }

            ImGui.SetCursorScreenPos(
                new Vector2(
                    origin.X,
                    origin.Y + rowHeight));

            ImGui.Dummy(
                new Vector2(
                    width,
                    1f));
        }
    }

    private void DrawPlayVideoDrawer()
    {
        if (pendingPlayerSearch != null)
        {
            var pendingSearch =
                pendingPlayerSearch.Trim();

            switch (playerSourceTab)
            {
                case 0:
                    // Link
                    // Kept in case something else routes here later.
                    urlInput = pendingSearch;
                    break;

                case 1:
                    // YouTube
                    searchQuery = pendingSearch;
                    searchResults = null;

                    if (!string.IsNullOrWhiteSpace(searchQuery) &&
                        !isSearching)
                    {
                        isSearching = true;

                        _ = RunSearchAsync(
                            searchQuery);
                    }

                    break;

                case 2:
                    // Twitch
                    twitchChannelInput = pendingSearch;
                    twitchResult = null;
                    twitchError = null;

                    if (!string.IsNullOrWhiteSpace(twitchChannelInput) &&
                        !isCheckingTwitch)
                    {
                        isCheckingTwitch = true;

                        _ = RunTwitchCheckAsync(
                            twitchChannelInput);
                    }

                    break;

                case 3:
                    // Dailymotion
                    dailymotionSearchQuery = pendingSearch;
                    dailymotionSearchResults = null;
                    dailymotionSearchError = null;

                    if (!string.IsNullOrWhiteSpace(dailymotionSearchQuery) &&
                        !isSearchingDailymotion)
                    {
                        isSearchingDailymotion = true;

                        _ = RunDailymotionSearchAsync(
                            dailymotionSearchQuery);
                    }

                    break;
            }

            pendingPlayerSearch = null;
        }

        DrawPlayerSourceTabs();

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));

        ImGui.Separator();

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));

        switch (playerSourceTab)
        {
            case 0:
                DrawLinkSource();
                break;

            case 1:
                DrawYouTubeSearch();
                break;

            case 2:
                DrawTwitchCheck();
                break;

            case 3:
                DrawDailymotionSearch();
                break;

            case 4:
                DrawGoLive();
                break;

            case 5:
                DrawDJLive();
                break;

            case 6:
                DrawImagesSlideshows();
                break;

            case 7:
                DrawLocalVideoSource();
                break;
        }
    }

    private void DrawQueueDrawer()
    {
        DrawQueue();
    }

    private void DrawPlayerSourceTabs()
    {
        //
        // =========================================================
        // Source selector
        // =========================================================
        //

        ImGui.TextColored(
            MutedText,
            "SOURCE");

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));


        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float categoryWidth =
            92f;

        const float gap =
            10f;

        const float buttonHeight =
            44f;


        //
        // =========================================================
        // Online
        // =========================================================
        //

        var onlineButtonWidth =
            (availableWidth -
             categoryWidth -
             (gap * 4f)) /
            4f;


        var onlineButtonSize =
            new Vector2(
                onlineButtonWidth,
                buttonHeight);


        //
        // Category label
        //

        var onlineLabelStart =
            ImGui.GetCursorScreenPos();

        var onlineLabelSize =
            ImGui.CalcTextSize(
                "Online");

        ImGui.SetCursorScreenPos(
            new Vector2(
                onlineLabelStart.X,
                onlineLabelStart.Y +
                (buttonHeight - onlineLabelSize.Y) *
                0.5f));

        ImGui.TextColored(
            MutedText,
            "Online");


        //
        // Move back to the row origin, then start buttons
        //

        ImGui.SetCursorScreenPos(
            new Vector2(
                onlineLabelStart.X +
                categoryWidth,
                onlineLabelStart.Y));


        DrawPlayerSourceButton(
            FontAwesomeIcon.Link,
            "Link",
            0,
            onlineButtonSize);


        ImGui.SameLine(
            0f,
            gap);


        DrawPlayerSourceButton(
            FontAwesomeIcon.PlayCircle,
            "YouTube",
            1,
            onlineButtonSize);


        ImGui.SameLine(
            0f,
            gap);


        DrawPlayerSourceButton(
            FontAwesomeIcon.Tv,
            "Twitch",
            2,
            onlineButtonSize);


        ImGui.SameLine(
            0f,
            gap);


        DrawPlayerSourceButton(
            FontAwesomeIcon.Film,
            "Dailymotion",
            3,
            onlineButtonSize);


        //
        // Move to next row
        //

        ImGui.SetCursorScreenPos(
            new Vector2(
                onlineLabelStart.X,
                onlineLabelStart.Y +
                buttonHeight +
                10f));


        //
        // =========================================================
        // Other
        // =========================================================
        //

        var otherButtonWidth =
          (availableWidth -
           categoryWidth -
           (gap * 4f)) /
          4f;


        var otherButtonSize =
            new Vector2(
                otherButtonWidth,
                buttonHeight);


        var otherLabelStart =
            ImGui.GetCursorScreenPos();

        var otherLabelSize =
            ImGui.CalcTextSize(
                "Other");

        ImGui.SetCursorScreenPos(
            new Vector2(
                otherLabelStart.X,
                otherLabelStart.Y +
                (buttonHeight - otherLabelSize.Y) *
                0.5f));

        ImGui.TextColored(
            MutedText,
            "Other");


        //
        // Move back to row origin, then start buttons
        //

        ImGui.SetCursorScreenPos(
            new Vector2(
                otherLabelStart.X +
                categoryWidth,
                otherLabelStart.Y));


        DrawPlayerSourceButton(
            FontAwesomeIcon.BroadcastTower,
            "Stream Live (OBS)",
            4,
            otherButtonSize);


        ImGui.SameLine(
            0f,
            gap);


        DrawPlayerSourceButton(
            FontAwesomeIcon.Music,
            "Music / DJ",
            5,
            otherButtonSize);


        ImGui.SameLine(
            0f,
            gap);


        DrawPlayerSourceButton(
            FontAwesomeIcon.Images,
            "Images / Slideshows",
            6,
            otherButtonSize);


        ImGui.SameLine(
            0f,
            gap);


        DrawPlayerSourceButton(
            FontAwesomeIcon.Film,
            "Local Video",
            7,
            otherButtonSize);


        //
        // Ensure the cursor finishes underneath the final row.
        //

        ImGui.SetCursorScreenPos(
            new Vector2(
                otherLabelStart.X,
                otherLabelStart.Y +
                buttonHeight));
    }


    private void DrawPlayerSourceButton(
        FontAwesomeIcon icon,
        string title,
        int tab,
        Vector2 size)
    {
        var selected =
            playerSourceTab == tab;

        var origin =
            ImGui.GetCursorScreenPos();


        //
        // Invisible button handles interaction while the draw list
        // gives us complete control over the appearance.
        //

        var clicked =
            ImGui.InvisibleButton(
                $"##source_{tab}",
                size);

        var hovered =
            ImGui.IsItemHovered();


        if (clicked)
        {
            playerSourceTab =
                tab;
        }


        var drawList =
            ImGui.GetWindowDrawList();

        var min =
            origin;

        var max =
            origin + size;


        //
        // =========================================================
        // Background
        // =========================================================
        //

        var background =
            selected
                ? new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.10f)
                : hovered
                    ? new Vector4(
                        FrameBgHover.X,
                        FrameBgHover.Y,
                        FrameBgHover.Z,
                        0.92f)
                    : new Vector4(
                        FrameBg.X,
                        FrameBg.Y,
                        FrameBg.Z,
                        0.78f);


        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                background),
            8f);


        //
        // =========================================================
        // Border
        // =========================================================
        //

        var border =
            selected
                ? Accent
                : hovered
                    ? new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.42f)
                    : new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.18f);


        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                border),
            8f,
            ImDrawFlags.None,
            selected
                ? 1.5f
                : 1f);


        //
        // =========================================================
        // Icon + label
        // =========================================================
        //

        var iconText =
            icon.ToIconString();

        Vector2 iconSize;


        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            iconSize =
                ImGui.CalcTextSize(
                    iconText);
        }


        var titleSize =
            ImGui.CalcTextSize(
                title);

        const float contentGap =
            10f;


        var contentWidth =
            iconSize.X +
            contentGap +
            titleSize.X;


        var contentStart =
            new Vector2(
                origin.X +
                (size.X - contentWidth) *
                0.5f,
                origin.Y +
                (size.Y - titleSize.Y) *
                0.5f);


        //
        // Icon
        //

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            drawList.AddText(
                contentStart,
                ImGui.GetColorU32(
                    selected
                        ? Accent
                        : MutedText),
                iconText);
        }


        //
        // Label
        //

        drawList.AddText(
            new Vector2(
                contentStart.X +
                iconSize.X +
                contentGap,
                contentStart.Y),
            ImGui.GetColorU32(
                Vector4.One),
            title);
    }

    private void DrawLocalVideoSource()
    {
        var engine =
            screenController.Engine;

        var isPlayingLocal =
            video.IsPlayingLocalVideo;


        //
        // =========================================================
        // Heading
        // =========================================================
        //

        ImGui.SetWindowFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Play a local video");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                4f));

        ImGui.TextColored(
            MutedText,
            "Play a video file directly from your computer.");

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));


        //
        // =========================================================
        // Local-only notice
        // =========================================================
        //

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   10f))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.08f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.42f)))
        using (var notice =
               ImRaii.Child(
                   "##localVideoNotice",
                   new Vector2(
                       -1f,
                       76f),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (notice)
            {
                using (ImRaii.PushFont(
                           UiBuilder.IconFont))
                {
                    ImGui.TextColored(
                        Accent,
                        FontAwesomeIcon.InfoCircle
                            .ToIconString());
                }

                ImGui.SameLine(
                    0f,
                    8f);

                ImGui.TextColored(
                    Accent,
                    "LOCAL PLAY ONLY");

                ImGui.Dummy(
                    new Vector2(
                        0f,
                        3f));

                ImGui.TextColored(
                    MutedText,
                    "Local video files do not currently support Watch Party syncing.");

                ImGui.TextColored(
                    MutedText,
                    "Stop local playback before using other Alpha Channel media features.");
            }
        }


        ImGui.Dummy(
            new Vector2(
                0f,
                18f));


        //
        // =========================================================
        // Active local session
        // =========================================================
        //

        if (isPlayingLocal)
        {
            ImGui.SetWindowFontScale(
                0.82f);

            ImGui.TextColored(
                MutedText,
                "NOW PLAYING LOCALLY");

            ImGui.SetWindowFontScale(
                1f);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    5f));

            var playingName =
                string.IsNullOrWhiteSpace(
                    localVideoSelectedPath)
                    ? "Local video"
                    : Path.GetFileName(
                        localVideoSelectedPath);

            ImGui.SetWindowFontScale(
                1.08f);

            ImGui.TextWrapped(
                playingName);

            ImGui.SetWindowFontScale(
                1f);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    14f));


            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       8f))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       Danger)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       new Vector4(
                           MathF.Min(
                               Danger.X + 0.08f,
                               1f),
                           MathF.Min(
                               Danger.Y + 0.08f,
                               1f),
                           MathF.Min(
                               Danger.Z + 0.08f,
                               1f),
                           1f))
                   .Push(
                       ImGuiCol.ButtonActive,
                       Danger))
            {
                if (ImGui.Button(
                        "Stop Local Video & Despawn TV",
                        new Vector2(
                            290f,
                            40f)))
                {
                    video.Stop();

                    localVideoError =
                        null;
                }
            }


            ImGui.Dummy(
                new Vector2(
                    0f,
                    8f));

            ImGui.TextColored(
                MutedText,
                "Other playback features remain unavailable until local playback is stopped.");

            return;
        }


        //
        // =========================================================
        // Availability
        // =========================================================
        //

        var inWatchParty =
            stream.Mode != StreamMode.None;

        var snesActive =
            engine.IsPlayingSnes;

        var normalPlaybackActive =
            queue.Current is not null ||
            engine.IsActive;

        var localPlaybackAvailable =
            !inWatchParty &&
            !snesActive &&
            !normalPlaybackActive;


        if (!localPlaybackAvailable)
        {
            string reason;

            if (inWatchParty)
            {
                reason =
                    "Leave or end your Watch Party before playing a local video.";
            }
            else if (snesActive)
            {
                reason =
                    "Exit the current SNES game before playing a local video.";
            }
            else
            {
                reason =
                    "Stop the current media playback before playing a local video.";
            }

            using (ImRaii.PushColor(
                       ImGuiCol.Text,
                       Gold))
            {
                ImGui.TextWrapped(
                    reason);
            }

            ImGui.Dummy(
                new Vector2(
                    0f,
                    12f));
        }


        //
        // =========================================================
        // File selector
        // =========================================================
        //

        ImGui.TextColored(
            MutedText,
            "Video File");

        ImGui.Dummy(
            new Vector2(
                0f,
                5f));


        var displayPath =
            string.IsNullOrWhiteSpace(
                localVideoSelectedPath)
                ? "No video selected"
                : localVideoSelectedPath;

        const float browseButtonWidth =
            120f;

        var rowWidth =
            ImGui.GetContentRegionAvail().X;

        ImGui.SetNextItemWidth(
            MathF.Max(
                180f,
                rowWidth -
                browseButtonWidth -
                10f));


        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        {
            ImGui.InputText(
                "##localVideoPath",
                ref displayPath,
                2048,
                ImGuiInputTextFlags.ReadOnly);
        }


        ImGui.SameLine(
            0f,
            10f);


        using (ImRaii.Disabled(
                   !localPlaybackAvailable))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        {
            if (ImGui.Button(
                    "Browse...",
                    new Vector2(
                        browseButtonWidth,
                        34f)))
            {
                localVideoFileDialog
                    .OpenFileDialog(
                        "Select Local Video",
                        ".*",
                        (success, path) =>
                        {
                            if (!success ||
                                string.IsNullOrWhiteSpace(
                                    path))
                            {
                                return;
                            }

                            if (!File.Exists(
                                    path))
                            {
                                localVideoError =
                                    "The selected video file could not be found.";

                                return;
                            }

                            localVideoSelectedPath =
                                path;

                            localVideoError =
                                null;
                        });
            }
        }


        ImGui.Dummy(
            new Vector2(
                0f,
                7f));

        ImGui.SetWindowFontScale(
            0.80f);

        ImGui.TextColored(
            MutedText,
            "File type is validated by the video player.");

        ImGui.SetWindowFontScale(
            1f);


        ImGui.Dummy(
            new Vector2(
                0f,
                16f));


        //
        // =========================================================
        // Play
        // =========================================================
        //

        var hasFile =
            !string.IsNullOrWhiteSpace(
                localVideoSelectedPath) &&
            File.Exists(
                localVideoSelectedPath);

        var canPlay =
            localPlaybackAvailable &&
            hasFile;


        using (ImRaii.Disabled(
                   !canPlay))
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
                    "Play Local Video",
                    new Vector2(
                        190f,
                        40f)))
            {
                localVideoError =
                    null;

                var started =
                    video.PlayLocalVideo(
                        localVideoSelectedPath);

                if (!started)
                {
                    localVideoError =
                        video.LastError ??
                        "Local video playback could not be started.";
                }
                else
                {
                    video.SetOverlayTitle(
                        Path.GetFileNameWithoutExtension(
                            localVideoSelectedPath),
                        "Local Video");
                }
            }
        }


        if (!string.IsNullOrWhiteSpace(
                localVideoError))
        {
            ImGui.Dummy(
                new Vector2(
                    0f,
                    8f));

            ImGui.TextColored(
                Danger,
                localVideoError);
        }


        //
        // File dialog must be drawn every frame while this source
        // page is active.
        //

        ImGui.SetNextWindowSize(
            new Vector2(
                900f,
                600f),
            ImGuiCond.Appearing);

        ImGui.SetNextWindowPos(
            ImGui.GetMainViewport()
                .GetCenter(),
            ImGuiCond.Appearing,
            new Vector2(
                0.5f,
                0.5f));

        localVideoFileDialog.Draw();
    }

    private static string TruncateVideoTitle(string title)
    {
        const int maxLength = 60;

        if (string.IsNullOrWhiteSpace(title) ||
            title.Length <= maxLength)
        {
            return title;
        }

        return title[..maxLength] + "...";
    }

    private void DrawLinkSource()
    {
        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Play a video link");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));


        //
        // =========================================================
        // URL input
        // =========================================================
        //

        ImGui.SetNextItemWidth(-66f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                new Vector2(
                    14f,
                    10f)))
        using (ImRaii.PushColor(
            ImGuiCol.FrameBg,
            new Vector4(
                0.045f,
                0.06f,
                0.105f,
                1f))
            .Push(
                ImGuiCol.FrameBgHovered,
                new Vector4(
                    0.065f,
                    0.085f,
                    0.14f,
                    1f))
            .Push(
                ImGuiCol.FrameBgActive,
                new Vector4(
                    0.065f,
                    0.085f,
                    0.14f,
                    1f)))
        {
            ImGui.InputTextWithHint(
                "##url",
                "Paste a video or supported webpage URL (https)",
                ref urlInput,
                2000);
        }


        ImGui.SameLine(
            0f,
            10f);


        //
        // Paste button
        //

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                new Vector2(
                    12f,
                    10f)))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            Accent)
            .Push(
                ImGuiCol.ButtonHovered,
                AccentHover)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            if (ImGui.Button(
                FontAwesomeIcon.Clipboard
                    .ToIconString(),
                new Vector2(
                    48f,
                    0f)))
            {
                var clipboard =
                    ImGui.GetClipboardText();

                if (!string.IsNullOrWhiteSpace(
                        clipboard))
                {
                    urlInput =
                        clipboard.Trim();
                }
            }
        }


        ImGui.Dummy(
            new Vector2(
                0f,
                5f));


        //
        // Help text
        //

        ImGui.SetWindowFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "Paste a direct video link or supported webpage URL.");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));


        //
        // =========================================================
        // Play now
        // =========================================================
        //

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

            var buttonSize =
                new Vector2(
                    160f,
                    38f);


            if (ImGui.Button(
      "##playNow",
      buttonSize))
            {
                if (urlInput.Length > 0)
                {
                    var entry =
                        new Video.VideoQueueEntry(
                            urlInput,
                            urlInput,
                            string.Empty,
                            null,
                            null);

                    HandlePlayNow(
                        entry);

                    urlInput =
                        string.Empty;
                }
            }


            DrawPlayerActionButtonContent(
                buttonPos,
                buttonSize,
                FontAwesomeIcon.Play,
                "Play now",
                Vector4.One);
        }


        ImGui.SameLine(
            0f,
            14f);


        //
        // =========================================================
        // Add to queue
        // =========================================================
        //

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
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
                    38f);


            if (ImGui.Button(
         "##addToQueue",
         buttonSize))
            {
                if (urlInput.Length > 0)
                {
                    var entry =
                        new Video.VideoQueueEntry(
                            urlInput,
                            urlInput,
                            string.Empty,
                            null,
                            null);

                    HandleAddToQueue(
                        entry);

                    urlInput =
                        string.Empty;
                }
            }


            ImGui.GetWindowDrawList()
                .AddRect(
                    buttonPos,
                    buttonPos + buttonSize,
                    ImGui.GetColorU32(
                        new Vector4(
                            MutedText.X,
                            MutedText.Y,
                            MutedText.Z,
                            0.16f)),
                    8f,
                    ImDrawFlags.None,
                    1f);


            DrawPlayerActionButtonContent(
                buttonPos,
                buttonSize,
                FontAwesomeIcon.Plus,
                "Add to queue",
                Vector4.One);
        }


        //
        // Queue feedback
        //

        if (ImGui.GetTime() <
            queueAddedFeedbackUntil)
        {
            ImGui.Dummy(
                new Vector2(
                    0f,
                    8f));


            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    Good,
                    FontAwesomeIcon.Check
                        .ToIconString());
            }


            ImGui.SameLine(
                0f,
                6f);


            ImGui.TextColored(
                Good,
                "Video added to queue");
        }

        //
        // =========================================================
        // Recommended websites
        // =========================================================
        //

        ImGui.Dummy(
            new Vector2(
                0f,
                18f));

        ImGui.Separator();

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));

        ImGui.SetWindowFontScale(
            1.08f);

        ImGui.TextColored(
            Vector4.One,
            "Recommended websites");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                2f));

        ImGui.SetWindowFontScale(
            0.84f);

        ImGui.TextColored(
            MutedText,
            "Popular websites commonly supported by Alpha Channel.");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));


        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float siteGap =
            10f;

        var siteCardWidth =
            (availableWidth -
             (siteGap * 3f)) /
            4f;

        var siteCardSize =
            new Vector2(
                siteCardWidth,
                56f);


        //
        // Row 1
        //

        DrawSupportedWebsiteCard(
            FontAwesomeIcon.PlayCircle,
            "YouTube",
            siteCardSize);

        ImGui.SameLine(
            0f,
            siteGap);

        DrawSupportedWebsiteCard(
            FontAwesomeIcon.BroadcastTower,
            "Twitch",
            siteCardSize);

        ImGui.SameLine(
            0f,
            siteGap);

        DrawSupportedWebsiteCard(
            FontAwesomeIcon.Film,
            "Vimeo",
            siteCardSize);

        ImGui.SameLine(
            0f,
            siteGap);

        DrawSupportedWebsiteCard(
            FontAwesomeIcon.Film,
            "Dailymotion",
            siteCardSize);


        ImGui.Dummy(
            new Vector2(
                0f,
                8f));


        //
        // Row 2
        //

        DrawSupportedWebsiteCard(
            FontAwesomeIcon.Link,
            "Reddit",
            siteCardSize);

        ImGui.SameLine(
            0f,
            siteGap);

        DrawSupportedWebsiteCard(
            FontAwesomeIcon.Tv,
            "Bilibili",
            siteCardSize);

        ImGui.SameLine(
            0f,
            siteGap);

        DrawSupportedWebsiteCard(
            FontAwesomeIcon.Images,
            "Internet Archive",
            siteCardSize);

        ImGui.SameLine(
            0f,
            siteGap);

        DrawSupportedWebsiteCard(
            FontAwesomeIcon.Tv,
            "Cartoon Vault",
            siteCardSize);


        ImGui.Dummy(
            new Vector2(
                0f,
                8f));


        //
        // Row 3
        //

        DrawSupportedWebsiteCard(
            FontAwesomeIcon.Images,
            "9GAG",
            siteCardSize);

        ImGui.SameLine(
            0f,
            siteGap);

        DrawSupportedWebsiteCard(
            FontAwesomeIcon.PlayCircle,
            "TubiTV",
            siteCardSize);

        ImGui.SameLine(
            0f,
            siteGap);

        DrawSupportedWebsiteCard(
            FontAwesomeIcon.BroadcastTower,
            "Kick",
            siteCardSize);


        ImGui.Dummy(
            new Vector2(
                0f,
                10f));

        ImGui.SetWindowFontScale(
            0.84f);

        ImGui.TextColored(
            MutedText,
            "We support many other websites and we're constantly working to add more.");

        ImGui.SetWindowFontScale(
            1f);


        //
        // =========================================================
        // FAQ
        // =========================================================
        //

        ImGui.Dummy(
            new Vector2(
                0f,
                16f));

        ImGui.Separator();

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));

        ImGui.SetWindowFontScale(
            1.08f);

        ImGui.TextColored(
            Vector4.One,
            "Frequently asked questions");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));


        const float faqGap =
            12f;

        var faqCardWidth =
            (ImGui.GetContentRegionAvail().X -
             faqGap) /
            2f;

        var faqCardSize =
            new Vector2(
                faqCardWidth,
                112f);


        DrawLinkFaqCard(
            FontAwesomeIcon.Search,
            "Are these the only websites you support?",
            "No. Alpha Channel can work with hundreds of large and",
            "small websites, and we're constantly working to",
            "improve compatibility.",
            faqCardSize);


        ImGui.SameLine(
            0f,
            faqGap);


        DrawLinkFaqCard(
            FontAwesomeIcon.Link,
            "Do I need a direct video link?",
            "Not always. A direct link to a video file is usually more",
            "reliable, but Alpha Channel can also scan some webpages",
            "to locate and extract an embedded video.",
            faqCardSize);


        //
        // =========================================================
        // Final support callout
        // =========================================================
        //

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));


        DrawLinkSupportCallout();
    }


    private static void DrawSupportedWebsiteCard(
        FontAwesomeIcon icon,
        string label,
        Vector2 size)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.Dummy(
            size);


        //
        // Background
        //

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    0.045f,
                    0.06f,
                    0.10f,
                    0.82f)),
            8f);


        //
        // Border
        //

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.16f)),
            8f,
            ImDrawFlags.None,
            1f);


        //
        // Icon
        //

        var iconText =
            icon.ToIconString();

        Vector2 iconSize;

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            iconSize =
                ImGui.CalcTextSize(
                    iconText);

            drawList.AddText(
                new Vector2(
                    origin.X + 18f,
                    origin.Y +
                    (size.Y - iconSize.Y) * 0.5f),
                ImGui.GetColorU32(
                    Accent),
                iconText);
        }


        //
        // Website name
        //

        var labelSize =
            ImGui.CalcTextSize(
                label);

        drawList.AddText(
            new Vector2(
                origin.X + 48f,
                origin.Y +
                (size.Y - labelSize.Y) * 0.5f),
            ImGui.GetColorU32(
                Vector4.One),
            label);
    }


    private static void DrawLinkFaqCard(
        FontAwesomeIcon icon,
        string title,
        string line1,
        string line2,
        string line3,
        Vector2 size)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.Dummy(
            size);


        //
        // Background
        //

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    0.045f,
                    0.06f,
                    0.10f,
                    0.82f)),
            9f);


        //
        // Border
        //

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.16f)),
            9f,
            ImDrawFlags.None,
            1f);


        //
        // Icon
        //

        var iconText =
            icon.ToIconString();

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            drawList.AddText(
                new Vector2(
                    origin.X + 17f,
                    origin.Y + 18f),
                ImGui.GetColorU32(
                    Accent),
                iconText);
        }


        //
        // Question
        //

        drawList.AddText(
            new Vector2(
                origin.X + 48f,
                origin.Y + 15f),
            ImGui.GetColorU32(
                Vector4.One),
            title);


        //
        // Answer
        //

        var bodyColor =
            ImGui.GetColorU32(
                MutedText);

        drawList.AddText(
            new Vector2(
                origin.X + 48f,
                origin.Y + 44f),
            bodyColor,
            line1);

        drawList.AddText(
            new Vector2(
                origin.X + 48f,
                origin.Y + 62f),
            bodyColor,
            line2);

        drawList.AddText(
            new Vector2(
                origin.X + 48f,
                origin.Y + 80f),
            bodyColor,
            line3);
    }


    private static void DrawLinkSupportCallout()
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var width =
            ImGui.GetContentRegionAvail().X;

        var size =
            new Vector2(
                width,
                76f);

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.Dummy(
            size);


        //
        // Purple-tinted background
        //

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.09f)),
            9f);


        //
        // Accent border
        //

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.70f)),
            9f,
            ImDrawFlags.None,
            1f);


        //
        // Icon
        //

        var iconText =
            FontAwesomeIcon.Search
                .ToIconString();

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            drawList.AddText(
                new Vector2(
                    origin.X + 18f,
                    origin.Y + 20f),
                ImGui.GetColorU32(
                    Accent),
                iconText);
        }


        //
        // Heading
        //

        drawList.AddText(
            new Vector2(
                origin.X + 52f,
                origin.Y + 14f),
            ImGui.GetColorU32(
                Accent),
            "Unsure if a video or webpage is supported?");


        //
        // Supporting text
        //

        drawList.AddText(
            new Vector2(
                origin.X + 52f,
                origin.Y + 42f),
            ImGui.GetColorU32(
                MutedText),
            "Try it out. Alpha Channel will attempt to locate and play the video automatically.");
    }



    private static void DrawPlayerActionButtonContent(
        Vector2 buttonPos,
        Vector2 buttonSize,
        FontAwesomeIcon icon,
        string label,
        Vector4 color)
    {
        var iconText = icon.ToIconString();

        Vector2 iconSize;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(iconText);
        }

        var labelSize = ImGui.CalcTextSize(label);

        const float gap = 6f;

        var totalWidth =
            iconSize.X +
            gap +
            labelSize.X;

        var start = new Vector2(
            buttonPos.X + (buttonSize.X - totalWidth) * 0.5f,
            buttonPos.Y + (buttonSize.Y - labelSize.Y) * 0.5f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.GetWindowDrawList().AddText(
                start,
                ImGui.GetColorU32(color),
                iconText);
        }

        ImGui.GetWindowDrawList().AddText(
            start + new Vector2(iconSize.X + gap, 0f),
            ImGui.GetColorU32(color),
            label);
    }
}

