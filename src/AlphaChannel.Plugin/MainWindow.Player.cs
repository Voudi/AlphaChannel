using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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
    private PlayerDrawer activePlayerDrawer = PlayerDrawer.Player;
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

        ImGui.Dummy(new Vector2(0f, 18f));

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
        }
    }

    private void DrawQueueDrawer()
    {
        DrawQueue();
    }

    private void DrawPlayerSourceTabs()
    {
        ImGui.TextColored(
            MutedText,
            "SOURCE");

        ImGui.Dummy(new Vector2(0f, 6f));

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float gap = 8f;
        const float buttonHeight = 38f;

        const int tabCount = 6;

        var buttonWidth =
            (availableWidth -
             (gap * (tabCount - 1))) /
            tabCount;

        var buttonSize =
            new Vector2(
                buttonWidth,
                buttonHeight);


        DrawPlayerSourceTab(
            FontAwesomeIcon.Link,
            "Link",
            0,
            buttonSize);

        ImGui.SameLine(0, gap);

        DrawPlayerSourceTab(
            FontAwesomeIcon.PlayCircle,
            "YouTube",
            1,
            buttonSize);

        ImGui.SameLine(0, gap);

        DrawPlayerSourceTab(
            FontAwesomeIcon.Tv,
            "Twitch",
            2,
            buttonSize);

        ImGui.SameLine(0, gap);

        DrawPlayerSourceTab(
            FontAwesomeIcon.Film,
            "Dailymotion",
            3,
            buttonSize);

        ImGui.SameLine(0, gap);

        DrawPlayerSourceTab(
            FontAwesomeIcon.BroadcastTower,
            "Stream Live (OBS)",
            4,
            buttonSize);

        ImGui.SameLine(0, gap);

        DrawPlayerSourceTab(
            FontAwesomeIcon.Music,
            "Music / DJ",
            5,
            buttonSize);
    }

    private void DrawPlayerSourceTab(
    FontAwesomeIcon icon,
    string label,
    int tab,
    Vector2 size)
    {
        var selected = playerSourceTab == tab;

        var buttonPos = ImGui.GetCursorScreenPos();

        var buttonBg = selected
            ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.10f)
            : new Vector4(0.045f, 0.06f, 0.10f, 1f);

        var hoverBg = selected
            ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.16f)
            : new Vector4(0.07f, 0.09f, 0.14f, 1f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            7f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            buttonBg)
            .Push(
                ImGuiCol.ButtonHovered,
                hoverBg)
            .Push(
                ImGuiCol.ButtonActive,
                hoverBg))
        {
            if (ImGui.Button(
                $"##source_{tab}",
                size))
            {
                playerSourceTab = tab;
            }
        }

        var drawList = ImGui.GetWindowDrawList();

        // Thin border like the mockup.
        drawList.AddRect(
            buttonPos,
            buttonPos + size,
            ImGui.GetColorU32(
                selected
                    ? Accent
                    : new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.22f)),
            7f,
            ImDrawFlags.None,
            selected ? 1.5f : 1f);

        var iconText = icon.ToIconString();

        Vector2 iconSize;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(iconText);
        }

        var textSize = ImGui.CalcTextSize(label);

        const float iconGap = 8f;

        var totalWidth =
            iconSize.X +
            iconGap +
            textSize.X;

        var textStart = new Vector2(
            buttonPos.X + (size.X - totalWidth) * 0.5f,
            buttonPos.Y + (size.Y - textSize.Y) * 0.5f);

        var color = selected
            ? AccentHover
            : MutedText;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                textStart,
                ImGui.GetColorU32(color),
                iconText);
        }

        drawList.AddText(
            textStart + new Vector2(iconSize.X + iconGap, 0f),
            ImGui.GetColorU32(color),
            label);
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
            "Play a direct video link");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 10f));

        // URL input
        ImGui.SetNextItemWidth(-66f);

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
                new Vector4(0.065f, 0.085f, 0.14f, 1f))
            .Push(
                ImGuiCol.FrameBgActive,
                new Vector4(0.065f, 0.085f, 0.14f, 1f)))
        {
            ImGui.InputTextWithHint(
                "##url",
                "Paste a YouTube, Twitch, or direct video URL (https)",
                ref urlInput,
                2000);
        }

        ImGui.SameLine(0f, 10f);

        // Paste button
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                new Vector2(12f, 10f)))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            Accent)
            .Push(
                ImGuiCol.ButtonHovered,
                AccentHover)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            if (ImGui.Button(
                FontAwesomeIcon.Clipboard.ToIconString(),
                new Vector2(48f, 0f)))
            {
                var clipboard = ImGui.GetClipboardText();

                if (!string.IsNullOrWhiteSpace(clipboard))
                {
                    urlInput = clipboard.Trim();
                }
            }
        }

        ImGui.Dummy(new Vector2(0f, 5f));

        // Support text
        ImGui.SetWindowFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "Supports YouTube, Twitch, Vimeo, and direct .mp4 links.");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 14f));

        // Play now button
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
            var buttonPos = ImGui.GetCursorScreenPos();
            var buttonSize = new Vector2(160f, 38f);

            if (ImGui.Button(
                "##playNow",
                buttonSize))
            {
                if (urlInput.Length > 0)
                {
                    queue.PlayNow(
                        new Video.VideoQueueEntry(
                            urlInput,
                            urlInput,
                            string.Empty,
                            null,
                            null));

                    urlInput = string.Empty;
                }
            }

            DrawPlayerActionButtonContent(
                buttonPos,
                buttonSize,
                FontAwesomeIcon.Play,
                "Play now",
                Vector4.One);
        }

        ImGui.SameLine(0f, 14f);

        // Add to queue button
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
            var buttonPos = ImGui.GetCursorScreenPos();
            var buttonSize = new Vector2(170f, 38f);

            if (ImGui.Button(
     "##addToQueue",
     buttonSize))
            {
                if (urlInput.Length > 0)
                {
                    queue.Add(
                        new Video.VideoQueueEntry(
                            urlInput,
                            urlInput,
                            string.Empty,
                            null,
                            null));

                    urlInput = string.Empty;

                    queueAddedFeedbackUntil =
                        ImGui.GetTime() + 2.0;
                }
            }

            ImGui.GetWindowDrawList().AddRect(
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
        if (ImGui.GetTime() < queueAddedFeedbackUntil)
        {
            ImGui.Dummy(new Vector2(0f, 8f));

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    Good,
                    FontAwesomeIcon.Check.ToIconString());
            }

            ImGui.SameLine(0f, 6f);

            ImGui.TextColored(
                Good,
                "Video added to queue");
        }
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

