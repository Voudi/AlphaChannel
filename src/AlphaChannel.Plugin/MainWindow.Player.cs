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
                Ui(46f));

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
    private string addMediaVideoSearchQuery = string.Empty;


    //
    // Local Video
    //

    private string localVideoSelectedPath =
        string.Empty;

    private string? localVideoError;

    private string? localVideoPatreonAccessMessage;


    //
    // Local-video Watch Party broadcast state.
    //
    // Armed means the host chose to broadcast the file. FFmpeg itself only
    // runs while at least one viewer is present.
    //

    private bool localVideoBroadcastArmed;
    private bool localVideoBroadcastStartFailed;
    private bool localVideoBroadcastEncoderExpectedRunning;
    private bool localVideoBroadcastPaused;

    private string? localVideoBroadcastPublishUrl;
    private string? localVideoBroadcastHlsUrl;
    private float pendingLocalVideoResumePosition;
    private double localVideoBroadcastStartPosition;
    private DateTime localVideoBroadcastStartTimeUtc;

    private bool HasConfirmedPatreonAccess()
    {
        return patreonAccessConfirmed &&
               HasConfiguredPatreonAccess();
    }

    private void RefreshLocalVideoPatreonAccess()
    {
        if (!HasConfiguredPatreonAccess())
        {
            patreonAccessConfirmed = false;
            localVideoPatreonAccessMessage =
                "No active Patreon membership was found.";
            return;
        }

        patreonAccessConfirmed = true;
        localVideoPatreonAccessMessage = null;
        localVideoError = null;

        Plugin.ChatGui.Print(
            $"[AlphaChannel] Patreon access confirmed (tier {Plugin.Cfg.PatreonMembershipTier}).");
    }

    internal string? ActiveLocalVideoBroadcastHlsUrl =>
        localVideoBroadcastArmed
            ? localVideoBroadcastHlsUrl
            : null;

    internal string? ActiveLocalVideoBroadcastTitle
    {
        get
        {
            if (!localVideoBroadcastArmed)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(
                    localVideoSelectedPath))
            {
                return "Local Video";
            }

            var videoName =
                Path.GetFileNameWithoutExtension(
                    localVideoSelectedPath);

            return string.IsNullOrWhiteSpace(
                videoName)
                ? "Local Video"
                : $"Local Video: {videoName}";
        }
    }

    internal bool ActiveLocalVideoBroadcastPaused =>
        localVideoBroadcastArmed &&
        localVideoBroadcastPaused;


    private readonly FileDialogManager localVideoFileDialog =
        new();


    private PlayerDrawer activePlayerDrawer =
        PlayerDrawer.Player;

    /// <summary>
    /// True while Add Media is displaying the source-selection landing page.
    /// Selecting a source changes this to false and opens that source's form.
    /// </summary>
    private bool showingAddMediaSources =
        true;
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
                activePlayerDrawer =
                    drawer;
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
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                start,
                ImGui.GetColorU32(textColor),
                iconText);
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            start + new Vector2(iconSize.X + gap, 0f),
            ImGui.GetColorU32(textColor),
            label);
    }

    private void DrawPlayerPreviewDrawer()
    {
        SetUiFontScale(1.15f);
        ImGui.TextColored(
            Vector4.One,
            "Playback Status");
        SetUiFontScale(1f);
        SetUiFontScale(0.72f);
        ImGui.TextColored(
            MutedText,
            "See what is playing and where it is being watched.");
        SetUiFontScale(1f);
        ImGui.Dummy(UiVec(0f, 14f));

        var engine = screenController.Engine;
        var current = queue.Current;
        var hasPlayback = current is not null ||
                          video.IsPlayingGame ||
                          video.IsPlayingBrowser ||
                          video.IsPlayingLocalVideo ||
                          video.IsPlayingImage ||
                          (stream.Mode == StreamMode.Viewing &&
                           !string.IsNullOrWhiteSpace(stream.CurrentRoomState?.Url));

        if (!hasPlayback)
        {
            DrawPlayerStatusEmptyState();
            return;
        }

        var (title, source, icon) = GetPlayerStatusMedia(current);
        var (position, duration, paused) = video.GetProgress();
        var status = engine.IsShowingWaitingScreen || video.State == Video.VideoPlaybackState.Loading
            ? "Loading"
            : paused || video.State == Video.VideoPlaybackState.Paused
                ? "Paused"
                : "Playing";

        DrawPlayerStatusMediaCard(current, title, source, icon, status, position, duration);
        ImGui.Dummy(UiVec(0f, 14f));

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var gap = Ui(14f);
        var cardWidth = MathF.Max(Ui(280f), (availableWidth - gap) * 0.5f);

        DrawPlayerViewingModeCard(cardWidth);
        ImGui.SameLine(0f, gap);
        DrawPlayerCompactQueueCard(MathF.Max(Ui(280f), availableWidth - cardWidth - gap));
    }

    private (string Title, string Source, FontAwesomeIcon Icon) GetPlayerStatusMedia(
        Video.VideoQueueEntry? current)
    {
        var engine = screenController.Engine;

        if (current is not null)
        {
            return (
                string.IsNullOrWhiteSpace(current.Title) ? "Now Playing" : current.Title,
                current.Source,
                video.IsAudioOnly ? FontAwesomeIcon.BroadcastTower : FontAwesomeIcon.Video);
        }

        if (engine.IsPlayingGame)
        {
            var system = engine.IsPlayingSnes
                ? GameSystem.Snes
                : engine.IsPlayingNes
                    ? GameSystem.Nes
                    : engine.IsPlayingGameBoyAdvance
                        ? GameSystem.GameBoyAdvance
                        : engine.IsPlayingMasterSystem
                            ? GameSystem.MasterSystem
                        : engine.IsPlayingGameGear
                            ? GameSystem.GameGear
                            : GameSystem.GameBoy;
            var path = system switch
            {
                GameSystem.Snes => snesSelectedRomPath,
                GameSystem.Nes => nesSelectedRomPath,
                GameSystem.GameBoyAdvance => gameBoyAdvanceSelectedRomPath,
                GameSystem.MasterSystem => masterSystemSelectedRomPath,
                GameSystem.GameGear => gameGearSelectedRomPath,
                _ => gameBoySelectedRomPath
            };
            var systemName = system switch
            {
                GameSystem.Snes => "Super Nintendo",
                GameSystem.Nes => "Nintendo Entertainment System",
                GameSystem.GameBoyAdvance => "Game Boy Advance",
                GameSystem.MasterSystem => "Master System / SG-1000",
                GameSystem.GameGear => "Game Gear",
                _ => "Game Boy / Color"
            };
            var gameName = string.IsNullOrWhiteSpace(path) ? string.Empty : LibraryGameName(path);
            return (string.IsNullOrWhiteSpace(gameName) ? $"{systemName} game" : gameName, systemName, FontAwesomeIcon.Gamepad);
        }

        if (engine.IsPlayingBrowser)
        {
            return (engine.Browser?.Title ?? "Web Browser", "Alpha Channel Browser", FontAwesomeIcon.Globe);
        }

        if (engine.IsPlayingLocalVideo)
        {
            var name = string.IsNullOrWhiteSpace(localVideoSelectedPath)
                ? "Local Video"
                : Path.GetFileNameWithoutExtension(localVideoSelectedPath);
            return (name, "Local video", FontAwesomeIcon.Video);
        }

        if (engine.IsPlayingImage)
        {
            return (engine.GetMediaTitle() ?? "Images", "Image viewer", FontAwesomeIcon.Images);
        }

        var roomTitle = stream.CurrentRoomState?.MediaTitle;
        return (string.IsNullOrWhiteSpace(roomTitle) ? "Watch Party media" : roomTitle, "Watch Party", FontAwesomeIcon.Tv);
    }

    private void DrawPlayerStatusMediaCard(
        Video.VideoQueueEntry? current,
        string title,
        string source,
        FontAwesomeIcon icon,
        string status,
        float position,
        float duration)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var height = Ui(184f);

        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 18f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
                   .Push(ImGuiCol.Border, BorderSubtle))
        {
            ImGui.BeginChild("##playerStatusMedia", new Vector2(width, height), true, ImGuiWindowFlags.NoScrollbar);

            var thumbSize = UiVec(230f, 130f);
            var thumbOrigin = ImGui.GetCursorScreenPos();
            DrawPlayerStatusThumbnail(current, icon, thumbOrigin, thumbSize);
            ImGui.Dummy(thumbSize);

            var actionsWidth = Ui(205f);
            var detailsX = thumbOrigin.X + thumbSize.X + Ui(20f);
            var detailsWidth = MathF.Max(Ui(120f), width - thumbSize.X - actionsWidth - Ui(76f));
            ImGui.SetCursorScreenPos(new Vector2(detailsX, thumbOrigin.Y + Ui(12f)));

            SetUiFontScale(1.08f);
            ImGui.TextColored(Vector4.One, TruncateToWidth(title, detailsWidth));
            SetUiFontScale(1f);

            ImGui.Dummy(UiVec(0f, 6f));
            DrawPlayerStatusPill(status);

            if (!string.IsNullOrWhiteSpace(source))
            {
                ImGui.Dummy(UiVec(0f, 7f));
                ImGui.TextColored(MutedText, TruncateToWidth(source, detailsWidth));
            }

            if (duration > 0f)
            {
                ImGui.Dummy(UiVec(0f, 6f));
                ImGui.TextColored(MutedText, $"{FormatTime(position)} of {FormatTime(duration)}");
            }

            var actionX = thumbOrigin.X + width - actionsWidth - Ui(36f);
            ImGui.SetCursorScreenPos(new Vector2(actionX, thumbOrigin.Y + Ui(18f)));
            DrawPlayerTvButton(actionsWidth);
            ImGui.SetCursorScreenPos(new Vector2(actionX, thumbOrigin.Y + Ui(76f)));
            DrawPlayerStatusActionButton(
                "##openMiniPlayerStatus",
                FontAwesomeIcon.Expand,
                "Open Mini Player",
                actionsWidth,
                false,
                () => OnMiniPlayerRequested?.Invoke());

            ImGui.EndChild();
        }
    }

    private void DrawPlayerStatusThumbnail(
        Video.VideoQueueEntry? current,
        FontAwesomeIcon icon,
        Vector2 origin,
        Vector2 size)
    {
        var drawList = ImGui.GetWindowDrawList();
        var thumbnailUrl = current?.ThumbnailUrl ??
                           (stream.Mode == StreamMode.Viewing
                               ? stream.CurrentRoomState?.MediaThumbnailUrl
                               : null);
        var thumbnail = thumbnails.Get(thumbnailUrl);
        if (thumbnail is not null)
        {
            drawList.AddImageRounded(thumbnail.Handle, origin, origin + size, Vector2.Zero, Vector2.One, uint.MaxValue, Ui(8f));
            return;
        }

        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(FrameBg), Ui(8f));
        var iconText = icon.ToIconString();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var iconSize = ImGui.CalcTextSize(iconText);
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), origin + (size - iconSize) * 0.5f, ImGui.GetColorU32(Accent), iconText);
        }
    }

    private void DrawPlayerStatusPill(string status)
    {
        var color = status == "Playing" ? Good : status == "Paused" ? Accent : MutedText;
        ImGui.TextColored(color, "●");
        ImGui.SameLine(0f, Ui(6f));
        ImGui.TextColored(color, status);
    }

    private void DrawPlayerTvButton(float width)
    {
        var engine = screenController.Engine;
        var spawned = stream.Mode == StreamMode.Viewing ? ViewerTvEnabled : engine.IsActive;
        DrawPlayerStatusActionButton(
            "##togglePlayerStatusTv",
            spawned ? FontAwesomeIcon.Times : FontAwesomeIcon.Tv,
            spawned ? "Despawn TV" : "Spawn TV",
            width,
            spawned,
            () =>
            {
                if (stream.Mode == StreamMode.Viewing)
                {
                    if (ViewerTvEnabled)
                    {
                        DespawnViewerTv(stopPlayback: !(IsMiniPlayerOpen?.Invoke() ?? false));
                    }
                    else
                    {
                        ViewerTvEnabled = true;
                        OnViewerTvSpawnRequested?.Invoke();
                    }
                }
                else if (engine.IsActive)
                {
                    engine.DespawnScreen();
                }
                else
                {
                    engine.RespawnScreen();
                }
            });
    }

    private void DrawPlayerViewingModeCard(float width)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 16f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
                   .Push(ImGuiCol.Border, BorderSubtle))
        {
            ImGui.BeginChild("##playerViewingMode", new Vector2(width, Ui(215f)), true, ImGuiWindowFlags.NoScrollbar);
            DrawPlayerCardLabel("VIEWING MODE");
            ImGui.Dummy(UiVec(0f, 8f));

            var heading = stream.Mode switch
            {
                StreamMode.Hosting => "Hosting Watch Party",
                StreamMode.Viewing => $"Watching with {joinedHostDisplayName ?? "the host"}",
                _ => "Playing solo"
            };
            var detail = stream.Mode switch
            {
                StreamMode.Hosting => stream.Roster.Length == 1 ? "1 viewer is watching with you." : $"{stream.Roster.Length} viewers are watching with you.",
                StreamMode.Viewing => "Playback is synced to the host.",
                _ => "Only you can currently see this."
            };
            ImGui.TextColored(Good, "●");
            ImGui.SameLine(0f, Ui(6f));
            ImGui.TextColored(Vector4.One, heading);
            ImGui.Dummy(UiVec(0f, 5f));
            ImGui.TextColored(MutedText, detail);
            ImGui.Dummy(UiVec(0f, 18f));

            DrawPlayerStatusActionButton(
                "##playerWatchPartyAction",
                FontAwesomeIcon.Users,
                stream.Mode == StreamMode.None ? "Create Watch Party" : "Open Watch Party",
                ImGui.GetContentRegionAvail().X,
                stream.Mode == StreamMode.None,
                () => currentPage = HomePage.WatchAlong);
            ImGui.EndChild();
        }
    }

    private void DrawPlayerCompactQueueCard(float width)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 16f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
                   .Push(ImGuiCol.Border, BorderSubtle))
        {
            ImGui.BeginChild("##playerCompactQueue", new Vector2(width, Ui(215f)), true, ImGuiWindowFlags.NoScrollbar);
            DrawPlayerCardLabel("UP NEXT");
            ImGui.Dummy(UiVec(0f, 9f));

            if (queue.Entries.Count == 0)
            {
                ImGui.TextColored(Vector4.One, "Nothing queued");
                ImGui.Dummy(UiVec(0f, 5f));
                ImGui.TextColored(MutedText, "Add media whenever you're ready.");
            }
            else
            {
                var next = queue.Entries[0];
                ImGui.TextColored(Vector4.One, TruncateToWidth(next.Title, ImGui.GetContentRegionAvail().X));
                ImGui.Dummy(UiVec(0f, 5f));
                var nextDetail = next.Duration is { } nextDuration
                    ? $"{next.Source}  •  {FormatTime((float)nextDuration.TotalSeconds)}"
                    : next.Source;
                ImGui.TextColored(MutedText, TruncateToWidth(nextDetail, ImGui.GetContentRegionAvail().X));
            }

            ImGui.SetCursorPosY(Ui(150f));
            DrawPlayerStatusActionButton(
                "##viewPlayerQueue",
                FontAwesomeIcon.List,
                "View Queue",
                ImGui.GetContentRegionAvail().X,
                false,
                () => activePlayerDrawer = PlayerDrawer.Queue);
            ImGui.EndChild();
        }
    }

    private void DrawPlayerCardLabel(string text)
    {
        SetUiFontScale(0.78f);
        ImGui.TextColored(MutedText, text);
        SetUiFontScale(1f);
    }

    private void DrawPlayerStatusEmptyState()
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(24f, 24f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
                   .Push(ImGuiCol.Border, BorderSubtle))
        {
            ImGui.BeginChild("##playerStatusEmpty", new Vector2(ImGui.GetContentRegionAvail().X, Ui(300f)), true, ImGuiWindowFlags.NoScrollbar);
            var contentWidth = ImGui.GetContentRegionAvail().X;
            ImGui.Dummy(UiVec(0f, 48f));
            DrawCenteredPlayerIcon(FontAwesomeIcon.Tv);
            ImGui.Dummy(UiVec(0f, 14f));
            DrawCenteredPlayerText("Nothing is playing", Vector4.One, 1.12f);
            ImGui.Dummy(UiVec(0f, 6f));
            DrawCenteredPlayerText("Choose something from Add Media to put it on your TV.", MutedText, 0.88f);
            ImGui.Dummy(UiVec(0f, 24f));

            var buttonWidth = Ui(190f);
            var gap = Ui(12f);
            ImGui.SetCursorPosX(Ui(24f) + MathF.Max(0f, (contentWidth - buttonWidth * 2f - gap) * 0.5f));
            DrawPlayerStatusActionButton("##emptyAddMedia", FontAwesomeIcon.Plus, "Add Media", buttonWidth, true, () => activePlayerDrawer = PlayerDrawer.PlayVideo);
            ImGui.SameLine(0f, gap);
            DrawPlayerStatusActionButton("##emptyWatchParty", FontAwesomeIcon.Users, stream.Mode == StreamMode.None ? "Create Watch Party" : "Open Watch Party", buttonWidth, false, () => currentPage = HomePage.WatchAlong);
            ImGui.EndChild();
        }
    }

    private static void DrawCenteredPlayerIcon(FontAwesomeIcon icon)
    {
        var text = icon.ToIconString();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.SetCursorPosX((ImGui.GetWindowSize().X - ImGui.CalcTextSize(text).X) * 0.5f);
            ImGui.TextColored(Accent, text);
        }
    }

    private void DrawCenteredPlayerText(string text, Vector4 color, float scale)
    {
        SetUiFontScale(scale);
        ImGui.SetCursorPosX((ImGui.GetWindowSize().X - ImGui.CalcTextSize(text).X) * 0.5f);
        ImGui.TextColored(color, text);
        SetUiFontScale(1f);
    }

    private void DrawPlayerStatusActionButton(
        string id,
        FontAwesomeIcon icon,
        string label,
        float width,
        bool primary,
        Action action)
    {
        var background = primary ? Accent : FrameBg;
        var hover = primary ? AccentHover : FrameBgHover;
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f)))
        using (ImRaii.PushColor(ImGuiCol.Button, background)
                   .Push(ImGuiCol.ButtonHovered, hover)
                   .Push(ImGuiCol.ButtonActive, primary ? AccentActive : hover))
        {
            var position = ImGui.GetCursorScreenPos();
            var size = new Vector2(width, Ui(40f));
            if (ImGui.Button(id, size))
            {
                action();
            }
            DrawPlayerActionButtonContent(position, size, icon, label, Vector4.One);
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

        if (showingAddMediaSources)
        {
            DrawAddMediaSourceLanding();
            return;
        }

        if (ImGui.Button(
                "\u2039  All media sources##allMediaSources",
                UiVec(190f, 36f)))
        {
            showingAddMediaSources =
                true;

            return;
        }

        ImGui.Dummy(
            UiVec(0f, 8f));

        ImGui.Separator();

        ImGui.Dummy(
            UiVec(0f, 12f));

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

    private void DrawAddMediaSourceLanding()
    {
        var sectionGap = Ui(12f);
        var headingGap = Ui(2f);
        var searchRowTop = ImGui.GetCursorPosY();

        DrawAddMediaVideoSearch();

        ImGui.SetCursorPosY(
            searchRowTop +
            Ui(36f) +
            sectionGap);

        var sourceHeadingTop = ImGui.GetCursorPosY();
        var sourceHeadingHeight = ImGui.GetTextLineHeight();

        ImGui.TextColored(
            Vector4.One,
            "Choose a media source:");

        ImGui.SetCursorPosY(
            sourceHeadingTop +
            sourceHeadingHeight +
            headingGap);

        SetUiFontScale(
            0.88f);

        var sourceSubtitleTop = ImGui.GetCursorPosY();
        var sourceSubtitleHeight = ImGui.GetTextLineHeight();

        ImGui.TextColored(
            MutedText,
            "Select a media source to play on your Alpha Channel TV.");

        SetUiFontScale(
            1f);

        ImGui.SetCursorPosY(
            sourceSubtitleTop +
            sourceSubtitleHeight +
            sectionGap);

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var cardGap = Ui(12f);

        var cardHeight = Ui(62f);

        var columnWidth =
            (availableWidth -
             (cardGap * 2f)) /
            3f;

        var twoColumnWidth =
            (columnWidth * 2f) +
             cardGap;

        var halfWidth =
            (availableWidth -
              cardGap) /
            2f;

        //
        // =========================================================
        // Row 1
        //
        // Web Link spans two columns because it is the general
        // quick-play option. YouTube occupies the third column.
        // =========================================================
        //

        var sourceRowTop = ImGui.GetCursorPosY();

        DrawAddMediaSourceCard(
            "webLink",
            FontAwesomeIcon.Link,
            "Web Link",
            "Paste a supported video or webpage URL.",
            new Vector2(
                twoColumnWidth,
                cardHeight),
            () => OpenAddMediaSource(0));

        ImGui.SameLine(
            0f,
            cardGap);

        DrawAddMediaSourceCard(
            "youtube",
            FontAwesomeIcon.PlayCircle,
            "YouTube",
            "Search or paste a YouTube link.",
            new Vector2(
                columnWidth,
                cardHeight),
            () => OpenAddMediaSource(1));

        ImGui.SetCursorPosY(
            sourceRowTop +
            cardHeight +
            cardGap);

        //
        // =========================================================
        // Row 2
        //
        // Twitch | Dailymotion | Stream Live
        // =========================================================
        //

        sourceRowTop = ImGui.GetCursorPosY();

        DrawAddMediaSourceCard(
            "twitch",
            FontAwesomeIcon.Tv,
            "Twitch",
            "Watch any live Twitch channel",
            new Vector2(
                columnWidth,
                cardHeight),
            () => OpenAddMediaSource(2));

        ImGui.SameLine(
            0f,
            cardGap);

        DrawAddMediaSourceCard(
            "dailymotion",
            FontAwesomeIcon.Film,
            "Dailymotion",
            "Search for Dailymotion videos.",
            new Vector2(
                columnWidth,
                cardHeight),
            () => OpenAddMediaSource(3));

        ImGui.SameLine(
            0f,
            cardGap);

        DrawAddMediaSourceCard(
            "streamLive",
            FontAwesomeIcon.BroadcastTower,
            "Stream Live",
            "Live stream from your PC",
            new Vector2(
                columnWidth,
                cardHeight),
            () => OpenAddMediaSource(4));

        ImGui.SetCursorPosY(
            sourceRowTop +
            cardHeight +
            cardGap);

        //
        // =========================================================
        // Row 3
        //
        // Music / DJ | Images / Slideshows | Local Video
        // =========================================================
        //

        sourceRowTop = ImGui.GetCursorPosY();

        DrawAddMediaSourceCard(
            "musicDj",
            FontAwesomeIcon.Music,
            "Radio / DJ Live",
            "Play radio streams or DJ live",
            new Vector2(
                columnWidth,
                cardHeight),
            () => OpenAddMediaSource(5));

        ImGui.SameLine(
            0f,
            cardGap);

        DrawAddMediaSourceCard(
            "images",
            FontAwesomeIcon.Images,
            "Images / Slideshows",
            "Show an image or slideshow.",
            new Vector2(
                columnWidth,
                cardHeight),
            () => OpenAddMediaSource(6));

        ImGui.SameLine(
            0f,
            cardGap);

        DrawAddMediaSourceCard(
            "localVideo",
            FontAwesomeIcon.Film,
            "Local Video",
            "Play a video from your computer.",
            new Vector2(
                columnWidth,
                cardHeight),
            () => OpenAddMediaSource(7));

        ImGui.SetCursorPosY(
            sourceRowTop +
            cardHeight +
            sectionGap);

        //
        // =========================================================
        // Retro Games
        // =========================================================
        //

        DrawAddMediaCategory(
            "RETRO GAMES",
            sectionGap);

        var gameRowTop = ImGui.GetCursorPosY();

        DrawAddMediaSourceCard(
            "snes",
            FontAwesomeIcon.Gamepad,
            "SNES",
            "Play a Super Nintendo game.",
            new Vector2(
                halfWidth,
                cardHeight),
            () =>
            {
                selectedGameSystem =
                    GameSystem.Snes;

                currentPage =
                    HomePage.PlaySnes;
            });

        ImGui.SameLine(
            0f,
            cardGap);

        DrawAddMediaSourceCard(
            "gameBoy",
            FontAwesomeIcon.Gamepad,
            "Game Boy",
            "Play a Game Boy game.",
            new Vector2(
                halfWidth,
                cardHeight),
            () =>
            {
                selectedGameSystem =
                    GameSystem.GameBoy;

                currentPage =
                    HomePage.PlaySnes;
            });

        ImGui.SetCursorPosY(
            gameRowTop +
            cardHeight +
            cardGap);

        gameRowTop = ImGui.GetCursorPosY();

        DrawAddMediaSourceCard(
            "nes",
            FontAwesomeIcon.Gamepad,
            "NES",
            "Play a Nintendo Entertainment System game.",
            new Vector2(
                halfWidth,
                cardHeight),
            () =>
            {
                selectedGameSystem =
                    GameSystem.Nes;

                currentPage =
                    HomePage.PlaySnes;
            });

        ImGui.SameLine(
            0f,
            cardGap);

        DrawAddMediaSourceCard(
            "gameBoyAdvance",
            FontAwesomeIcon.Gamepad,
            "Game Boy Advance",
            "Play a Game Boy Advance game.",
            new Vector2(
                halfWidth,
                cardHeight),
            () =>
            {
                selectedGameSystem =
                    GameSystem.GameBoyAdvance;

                currentPage =
                    HomePage.PlaySnes;
            });

        ImGui.SetCursorPosY(
            gameRowTop +
            cardHeight +
            cardGap);

        DrawAddMediaSourceCard(
            "masterSystem",
            FontAwesomeIcon.Gamepad,
            "Master System / SG-1000",
            "Play a Sega Master System or SG-1000 game.",
            new Vector2(
                halfWidth,
                cardHeight),
            () =>
            {
                selectedGameSystem =
                    GameSystem.MasterSystem;

                currentPage =
                    HomePage.PlaySnes;
            });

        ImGui.SameLine(
            0f,
            cardGap);

        DrawAddMediaSourceCard(
            "gameGear",
            FontAwesomeIcon.Gamepad,
            "Game Gear",
            "Play a Sega Game Gear game.",
            new Vector2(
                halfWidth,
                cardHeight),
            () =>
            {
                selectedGameSystem =
                    GameSystem.GameGear;

                currentPage =
                    HomePage.PlaySnes;
            });
    }

    private void DrawAddMediaVideoSearch()
    {
        var buttonWidth = Ui(128f);
        ImGui.SetNextItemWidth(MathF.Max(
            Ui(180f),
            ImGui.GetContentRegionAvail().X - buttonWidth - Ui(10f)));
        var submitted = ImGui.InputTextWithHint(
            "##addMediaVideoSearch",
            "Find videos by search term...",
            ref addMediaVideoSearchQuery,
            300,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine(0f, Ui(10f));
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(addMediaVideoSearchQuery)))
        {
            submitted |= GameLayoutButton(
                "Search Videos",
                FontAwesomeIcon.Search,
                buttonWidth,
                true,
                height: 36f);
        }

        if (submitted && !string.IsNullOrWhiteSpace(addMediaVideoSearchQuery))
        {
            var query = addMediaVideoSearchQuery.Trim();
            addMediaVideoSearchQuery = string.Empty;
            OpenUnifiedVideoSearch(query);
        }
    }

    private void DrawAddMediaCategory(
        string title,
        float bottomGap)
    {
        SetUiFontScale(
            0.78f);

        var categoryTop = ImGui.GetCursorPosY();
        var categoryHeight = ImGui.GetTextLineHeight();

        ImGui.TextColored(
            MutedText,
            title);

        SetUiFontScale(
            1f);

        ImGui.SetCursorPosY(
            categoryTop +
            categoryHeight +
            bottomGap);
    }

    private void OpenAddMediaSource(
        int sourceTab)
    {
        playerSourceTab =
            sourceTab;

        showingAddMediaSources =
            false;
    }

    private void DrawAddMediaSourceCard(
        string id,
        FontAwesomeIcon icon,
        string title,
        string description,
        Vector2 size,
        Action onClick)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var clicked =
            ImGui.InvisibleButton(
                $"##addMediaSource_{id}",
                size);

        var hovered =
            ImGui.IsItemHovered();

        if (clicked)
        {
            onClick();
        }

        var drawList =
            ImGui.GetWindowDrawList();

        var minimum =
            origin;

        var maximum =
            origin + size;

        var background =
            hovered
                ? new Vector4(
                    0.085f,
                    0.10f,
                    0.16f,
                    1f)
                : new Vector4(
                    0.055f,
                    0.065f,
                    0.105f,
                    1f);

        var border =
            hovered
                ? Accent
                : new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.22f);

        drawList.AddRectFilled(
            minimum,
            maximum,
            ImGui.GetColorU32(
                background),
            9f);

        drawList.AddRect(
            minimum,
            maximum,
            ImGui.GetColorU32(
                border),
            9f,
            ImDrawFlags.None,
            hovered ? 1.5f : 1f);

        var iconText =
            icon.ToIconString();

        var iconPosition =
            new Vector2(
                minimum.X + Ui(20f),
                minimum.Y + Ui(20f));

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconPosition,
                ImGui.GetColorU32(
                    hovered
                        ? AccentHover
                        : Accent),
                iconText);
        }

        var textLeft =
            minimum.X + 52f;

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                textLeft,
                minimum.Y + Ui(15f)),
            ImGui.GetColorU32(
                Vector4.One),
            title);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                textLeft,
                minimum.Y + Ui(38f)),
            ImGui.GetColorU32(
                MutedText),
            description);

        var arrow =
            "\u203A";

        var arrowSize =
            ImGui.CalcTextSize(
                arrow);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                maximum.X -
                arrowSize.X -
                Ui(18f),
                minimum.Y +
                ((size.Y - arrowSize.Y) *
                 0.5f)),
            ImGui.GetColorU32(
                hovered
                    ? AccentHover
                    : MutedText),
            arrow);
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
            UiVec(0f, 8f));


        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var categoryWidth = Ui(92f);

        const float gap =
            10f;

        var buttonHeight = Ui(44f);


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
                Ui(10f)));


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

        var contentGap = Ui(10f);


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
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
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

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                contentStart.X +
                iconSize.X +
                contentGap,
                contentStart.Y),
            ImGui.GetColorU32(
                Vector4.One),
            title);
    }

    private void BeginLocalVideoWatchPartyBroadcast(
    bool startPlayback)
    {
        if (!HasConfirmedPatreonAccess())
        {
            localVideoError =
                "A Patreon membership is required to broadcast local videos.";

            return;
        }

        if (stream.Mode ==
            StreamMode.Viewing)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Leave your current Watch Party before broadcasting a local video.");

            return;
        }

        if (stream.Mode ==
            StreamMode.Hosting)
        {
            StartLocalVideoBroadcastInHostedRoom(
                startPlayback);

            return;
        }

        if (!startPlayback)
        {
            pendingLocalVideoResumePosition =
                MathF.Max(
                    0f,
                    video.GetProgress().Position);
        }
        else
        {
            pendingLocalVideoResumePosition =
                0f;
        }

        pendingWatchPartyMediaKind =
            startPlayback
                ? PendingWatchPartyMediaKind.LocalVideoStartAndBroadcast
                : PendingWatchPartyMediaKind.LocalVideoCurrentPlaybackBroadcast;

        watchPartyCreationPopupOpen =
            true;

        createRoomPassword =
            string.Empty;

        createLockedRoomPasswordError =
            null;
    }

    private void StartLocalVideoBroadcastInHostedRoom(
    bool startPlayback,
    bool navigateToWatchParty = false)
    {
        if (!HasConfirmedPatreonAccess())
        {
            localVideoError =
                "A Patreon membership is required to broadcast local videos.";

            return;
        }

        if (stream.Mode !=
            StreamMode.Hosting)
        {
            return;
        }

        if (startPlayback)
        {
            pendingLocalVideoResumePosition =
                0f;

            StartSelectedLocalVideo(
                broadcast: true);
        }
        else
        {
            //
            // Creating a Watch Party rebuilds the TV and therefore stops
            // the existing local MPV playback. Restore the selected file
            // at the position captured before opening the popup.
            //

            if (!video.IsPlayingLocalVideo)
            {
                if (string.IsNullOrWhiteSpace(
                        localVideoSelectedPath) ||
                    !File.Exists(
                        localVideoSelectedPath))
                {
                    localVideoError =
                        "The selected local video file could not be found.";

                    return;
                }

                var resumed =
                    video.PlayLocalVideo(
                        localVideoSelectedPath);

                if (!resumed)
                {
                    localVideoError =
                        video.LastError ??
                        "Local video playback could not be resumed.";

                    return;
                }

                video.SetOverlayTitle(
                    Path.GetFileNameWithoutExtension(
                        localVideoSelectedPath),
                    "Local Video");

                if (pendingLocalVideoResumePosition >
                    0f)
                {
                    video.Seek(
                        pendingLocalVideoResumePosition);
                }
            }

            if (!StartLocalVideoWatchPartyBroadcast())
            {
                return;
            }

            pendingLocalVideoResumePosition =
                0f;
        }

        if (!localVideoBroadcastArmed)
        {
            return;
        }

        if (navigateToWatchParty)
        {
            currentPage =
                HomePage.WatchAlong;

            partyPanelTab =
                PartyPanelTab.NowPlaying;
        }
    }

    private bool StartLocalVideoWatchPartyBroadcast()
    {
        if (!HasConfirmedPatreonAccess())
        {
            localVideoError =
                "A Patreon membership is required to broadcast local videos.";

            Plugin.ChatGui.Print(
                "[AlphaChannel] A Patreon membership is required to broadcast local videos.");

            return false;
        }

        if (stream.Mode !=
            StreamMode.Hosting)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Host a Watch Party before broadcasting a local video.");

            return false;
        }

        if (!video.IsPlayingLocalVideo)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Start the local video before broadcasting it.");

            return false;
        }

        if (string.IsNullOrWhiteSpace(
                localVideoSelectedPath) ||
            !File.Exists(
                localVideoSelectedPath))
        {
            localVideoError =
                "The selected local video file could not be found.";

            return false;
        }

        if (CurrentSession is not { } session)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Sign in before broadcasting a local video.");

            return false;
        }

        var streamKey =
            Plugin.Cfg.StreamKeys
                .GetValueOrDefault(
                    session.AccountId);

        if (string.IsNullOrWhiteSpace(
                streamKey))
        {
            localVideoError =
                "No stream key is available. Generate one from Settings > Account first.";

            return false;
        }

        //
        // A single account has one MediaMTX live path. Make sure an old
        // emulator broadcaster cannot continue owning it.
        //

        StopGameWatchPartyBroadcast();
        StopLocalVideoWatchPartyBroadcast();

        screenController.Engine
            .StopLocalVideoBroadcast();

        localVideoBroadcastPublishUrl =
            $"{BuildRtmpServer()}/{streamKey}";

        localVideoBroadcastHlsUrl =
    $"{BuildMyHlsUrl(session)}" +
    $"?broadcast={Guid.NewGuid():N}";

        localVideoBroadcastStartFailed =
            false;

        localVideoBroadcastEncoderExpectedRunning =
            false;

        localVideoBroadcastPaused =
            false;

        localVideoBroadcastArmed =
            true;

        localVideoError =
            null;

        //
        // Advertise only the public HLS URL. The RTMP URL contains the
        // private stream key and must never be sent through stream.state.
        //

        _ = PublishLocalVideoWatchPartyAsync(
            localVideoBroadcastHlsUrl);

        Plugin.ChatGui.Print(
            stream.Roster.Length > 0
                ? "[AlphaChannel] Local video broadcast enabled. Upload will start now."
                : "[AlphaChannel] Local video broadcast enabled. Uploading will begin when a viewer joins.");

        return true;
    }


    private async Task PublishLocalVideoWatchPartyAsync(
        string hlsUrl)
    {
        if (string.IsNullOrWhiteSpace(
                hlsUrl))
        {
            return;
        }

        var videoName =
         string.IsNullOrWhiteSpace(
             localVideoSelectedPath)
             ? string.Empty
             : Path.GetFileNameWithoutExtension(
                 localVideoSelectedPath);

        var title =
            ActiveLocalVideoBroadcastTitle ??
            "Local Video";

        await stream.PublishStateAsync(
            hlsUrl,
            0d,
            false,
            screenController.Engine.ScreenPosition,
            screenController.Engine.ScreenYaw,
            screenController.Engine.ScreenScale,
            title,
            null);
    }


    internal void UpdateLocalVideoBroadcastDemand()
    {
        if (!localVideoBroadcastArmed)
        {
            return;
        }

        if (!HasConfirmedPatreonAccess())
        {
            StopLocalVideoWatchPartyBroadcast();
            return;
        }

        var engine =
            screenController.Engine;

        //
        // Disarm when the room closes, hosting transfers away, the local
        // video stops, or its original file is no longer available.
        //

        if (stream.Mode != StreamMode.Hosting ||
            !video.IsPlayingLocalVideo ||
            string.IsNullOrWhiteSpace(
                localVideoSelectedPath) ||
            !File.Exists(
                localVideoSelectedPath) ||
            string.IsNullOrWhiteSpace(
                localVideoBroadcastPublishUrl))
        {
            StopLocalVideoWatchPartyBroadcast();
            return;
        }

        var (position, duration, paused) =
            video.GetProgress();

        localVideoBroadcastPaused =
            paused;

        //
        // Stop cleanly at the end instead of repeatedly launching FFmpeg
        // at the final frame.
        //

        if (duration > 0f &&
            position >=
            duration - 0.75f)
        {
            StopLocalVideoWatchPartyBroadcast();
            return;
        }

        var hasViewer =
            stream.Roster.Length > 0;

        //
        // Pausing the host or losing the last viewer stops network upload.
        // On resume/rejoin, FFmpeg starts again at MPV's current timestamp.
        //

        if (!hasViewer ||
            paused)
        {
            localVideoBroadcastEncoderExpectedRunning =
                false;

            if (engine.IsLocalVideoBroadcasting)
            {
                engine.StopLocalVideoBroadcast();
            }

            localVideoBroadcastStartFailed =
                false;

            return;
        }

        var encoderRunning =
            engine.IsLocalVideoBroadcasting;

        //
        // If FFmpeg was expected to remain alive but exited, report the
        // failure and avoid relaunching it every framework frame.
        //

        if (localVideoBroadcastEncoderExpectedRunning &&
            !encoderRunning)
        {
            localVideoBroadcastEncoderExpectedRunning =
                false;

            localVideoBroadcastStartFailed =
                true;

            localVideoError =
                engine.LocalVideoBroadcastError ??
                "The local video relay encoder stopped unexpectedly.";

            return;
        }

        if (encoderRunning)
        {
            //
            // Detect a host seek. Small differences are normal because MPV
            // and FFmpeg begin independently. A large difference after the
            // startup grace period means FFmpeg should be restarted at MPV's
            // new timestamp.
            //

            var runningFor =
                DateTime.UtcNow -
                localVideoBroadcastStartTimeUtc;

            var expectedPosition =
                localVideoBroadcastStartPosition +
                runningFor.TotalSeconds;

            var drift =
                Math.Abs(
                    position -
                    expectedPosition);

            if (runningFor <
                    TimeSpan.FromSeconds(5) ||
                drift <= 4d)
            {
                return;
            }

            localVideoBroadcastEncoderExpectedRunning =
                false;

            engine.StopLocalVideoBroadcast();

            localVideoBroadcastStartFailed =
                false;
        }

        if (localVideoBroadcastStartFailed)
        {
            return;
        }

        var started =
            engine.StartLocalVideoBroadcast(
                localVideoSelectedPath,
                localVideoBroadcastPublishUrl,
                position);

        if (!started)
        {
            localVideoBroadcastStartFailed =
                true;

            localVideoBroadcastEncoderExpectedRunning =
                false;

            localVideoError =
                engine.LastError ??
                engine.LocalVideoBroadcastError ??
                "The local video broadcast could not be started.";

            return;
        }

        localVideoBroadcastStartPosition =
            position;

        localVideoBroadcastStartTimeUtc =
            DateTime.UtcNow;

        localVideoBroadcastEncoderExpectedRunning =
            true;

        localVideoError =
            null;

        Plugin.ChatGui.Print(
            "[AlphaChannel] A viewer joined. Local video upload started.");
    }


    private void StopLocalVideoWatchPartyBroadcast()
    {
        localVideoBroadcastEncoderExpectedRunning =
            false;

        screenController.Engine
            .StopLocalVideoBroadcast();

        localVideoBroadcastArmed =
            false;

        localVideoBroadcastStartFailed =
            false;

        localVideoBroadcastPaused =
            false;

        localVideoBroadcastPublishUrl =
            null;

        localVideoBroadcastHlsUrl =
            null;
    }

    private void StartSelectedLocalVideo(
    bool broadcast)
    {
        localVideoError =
            null;

        if (string.IsNullOrWhiteSpace(
                localVideoSelectedPath) ||
            !File.Exists(
                localVideoSelectedPath))
        {
            localVideoError =
                "The selected local video file could not be found.";

            return;
        }

        var started =
            video.PlayLocalVideo(
                localVideoSelectedPath);

        if (!started)
        {
            localVideoError =
                video.LastError ??
                "Local video playback could not be started.";

            return;
        }

        video.SetOverlayTitle(
            Path.GetFileNameWithoutExtension(
                localVideoSelectedPath),
            "Local Video");

        if (broadcast &&
            !StartLocalVideoWatchPartyBroadcast())
        {
            localVideoError ??=
                "The video is playing locally, but its Watch Party broadcast could not be started.";
        }
    }

    private void DrawLocalVideoPatreonHeart(
        Vector2 center)
    {
        var drawList =
            ImGui.GetWindowDrawList();

        var heart =
            FontAwesomeIcon.Heart.ToIconString();

        var padlock =
            FontAwesomeIcon.Lock.ToIconString();

        float iconFontSize;
        Vector2 heartSize;
        Vector2 lockSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            iconFontSize =
                ImGui.GetFontSize();

            SetUiFontScale(
                2.05f);

            heartSize =
                ImGui.CalcTextSize(
                    heart);

            SetUiFontScale(
                0.62f);

            lockSize =
                ImGui.CalcTextSize(
                    padlock);

            SetUiFontScale(
                1f);
        }

        drawList.AddText(
            UiBuilder.IconFont,
            iconFontSize * 2.05f,
            center -
            heartSize *
            0.5f,
            ImGui.GetColorU32(
                PatreonOrange),
            heart);

        drawList.AddText(
            UiBuilder.IconFont,
            iconFontSize * 0.62f,
            center -
            lockSize *
            0.5f,
            ImGui.GetColorU32(
                new Vector4(
                    0.055f,
                    0.06f,
                    0.09f,
                    1f)),
            padlock);
    }

    private void DrawLockedLocalVideoBroadcastButton(
        string id,
        string label,
        Vector2 size)
    {
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   PatreonOrange)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       PatreonOrangeHover)
                   .Push(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           0.92f,
                           0.42f,
                           0.08f,
                           1f)))
        {
            var origin =
                ImGui.GetCursorScreenPos();

            if (ImGui.Button(
                    id,
                    size))
            {
                patreonPopupOpen = true;
            }

            DrawPlayerActionButtonContent(
                origin,
                size,
                FontAwesomeIcon.Lock,
                label,
                Vector4.One);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "A Patreon membership is required to broadcast local videos.");
        }
    }

    private void DrawLocalVideoPatreonGate()
    {
        ImGui.Dummy(
            UiVec(0f, 16f));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   10f)
                   .Push(
                       ImGuiStyleVar.ChildBorderSize,
                       1f)
                   .Push(
                       ImGuiStyleVar.WindowPadding,
                       UiVec(18f, 12f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       0.075f,
                       0.06f,
                       0.12f,
                       1f))
                   .Push(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.82f)))
        {
            if (ImGui.BeginChild(
                    "##localVideoPatreonGate",
                    UiVec(-1f, 145f),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
            {
                var panelMin =
                    ImGui.GetWindowPos();

                var panelMax =
                    panelMin +
                    ImGui.GetWindowSize();

                var panelSize =
                    ImGui.GetWindowSize();

                var contentOrigin =
                    ImGui.GetCursorScreenPos();

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
                            0.09f)),
                    10f);

                drawList.AddCircleFilled(
                    new Vector2(
                        panelMax.X -
                        panelSize.X * 0.12f,
                        (panelMin.Y + panelMax.Y) *
                        0.5f),
                    panelSize.Y *
                    1.25f,
                    ImGui.GetColorU32(
                        new Vector4(
                            PatreonOrange.X,
                            PatreonOrange.Y,
                            PatreonOrange.Z,
                            0.07f)),
                    64);

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
                        Ui(10f),
                        panelMin.Y),
                    orangeBorder);

                drawList.AddLine(
                    new Vector2(
                        panelMax.X,
                        panelMin.Y +
                        Ui(10f)),
                    new Vector2(
                        panelMax.X,
                        panelMax.Y -
                        Ui(10f)),
                    orangeBorder);

                drawList.AddLine(
                    new Vector2(
                        panelMax.X -
                        Ui(10f),
                        panelMax.Y),
                    new Vector2(
                        middleX,
                        panelMax.Y),
                    orangeBorder);

                DrawLocalVideoPatreonHeart(
                    contentOrigin +
                    UiVec(38f, 58f));

                var actionWidth =
                    MathF.Min(
                        270f,
                        panelSize.X *
                        0.30f);

                var actionX =
                    panelSize.X -
                    18f -
                    actionWidth;

                var copyX =
                    88f;

                var copyWidth =
                    MathF.Max(
                        220f,
                        actionX -
                        copyX -
                        24f);

                ImGui.SetCursorPos(
                    new Vector2(
                        copyX,
                        Ui(20f)));

                SetUiFontScale(
                    0.82f);

                ImGui.TextColored(
                    PatreonOrange,
                    "PATREON FEATURE");

                ImGui.SetCursorPosX(
                    copyX);

                SetUiFontScale(
                    1.16f);

                ImGui.TextColored(
                    Vector4.One,
                    "Local video broadcasting");

                SetUiFontScale(
                    1f);

                ImGui.SetCursorPosX(
                    copyX);

                ImGui.PushTextWrapPos(
                    copyX +
                    copyWidth);

                ImGui.TextColored(
                    MutedText,
                    "Local playback is free. Join our Patreon to broadcast local videos to your Watch Party.");

                ImGui.PopTextWrapPos();

                ImGui.SetCursorPos(
                    new Vector2(
                        actionX,
                        Ui(27f)));

                DrawDjActionButton(
                    "##unlockLocalVideoWithPatreon",
                    FontAwesomeIcon.LockOpen,
                    "Unlock with Patreon",
                    new Vector2(
                        actionWidth,
                        Ui(40f)),
                    false,
                    () =>
                    {
                        patreonPopupOpen = true;
                    },
                    true);

                ImGui.SetCursorPos(
                    new Vector2(
                        actionX,
                        Ui(72f)));

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
                            "Already a member? Refresh access##refreshLocalVideoPatreon",
                            new Vector2(
                                actionWidth,
                                Ui(25f))))
                    {
                        RefreshLocalVideoPatreonAccess();
                    }
                }

                if (localVideoPatreonAccessMessage is { } accessMessage)
                {
                    ImGui.SetCursorPos(
                        new Vector2(
                            actionX,
                            Ui(105f)));

                    SetUiFontScale(
                        0.82f);

                    ImGui.TextColored(
                        Danger,
                        accessMessage);

                    SetUiFontScale(
                        1f);
                }
            }

            ImGui.EndChild();
        }
    }

    private void DrawLocalVideoSource()
    {
        var engine =
            screenController.Engine;

        var isPlayingLocal =
            video.IsPlayingLocalVideo;

        var hostingWatchParty =
            stream.Mode ==
            StreamMode.Hosting;

        var viewingWatchParty =
            stream.Mode ==
            StreamMode.Viewing;


        //
        // Heading
        //

        SetUiFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Local Video");

        if (HasConfirmedPatreonAccess())
        {
            DrawPatreonFeatureTag();
        }

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 4f));

        ImGui.TextColored(
            MutedText,
            "Play a video from your computer or broadcast it to your Watch Party.");

        ImGui.Dummy(
            UiVec(0f, 16f));


        //
        // Active local-video card
        //

        if (isPlayingLocal)
        {
            var (position, duration, paused) =
                video.GetProgress();

            var playingName =
                string.IsNullOrWhiteSpace(
                    localVideoSelectedPath)
                    ? "Local video"
                    : Path.GetFileName(
                        localVideoSelectedPath);

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.ChildRounding,
                       10f))
            using (ImRaii.PushColor(
                       ImGuiCol.ChildBg,
                       new Vector4(
                           FrameBg.X,
                           FrameBg.Y,
                           FrameBg.Z,
                           0.82f)))
            using (ImRaii.PushColor(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           localVideoBroadcastArmed
                               ? 0.75f
                               : 0.32f)))
            using (var card =
                   ImRaii.Child(
                       "##activeLocalVideoCard",
new Vector2(
    -1f,
    Ui(250f)),
                       true,
                       ImGuiWindowFlags.NoScrollbar |
                       ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (card)
                {
                    using (ImRaii.PushFont(
                               UiBuilder.IconFont))
                    {
                        ImGui.TextColored(
                            Accent,
                            FontAwesomeIcon.Film
                                .ToIconString());
                    }

                    ImGui.SameLine(
                        0f,
                        9f);

                    SetUiFontScale(
                        0.82f);

                    ImGui.TextColored(
                        MutedText,
                        localVideoBroadcastArmed
                            ? "NOW PLAYING · WATCH PARTY"
                            : "NOW PLAYING LOCALLY");

                    SetUiFontScale(
                        1f);

                    ImGui.Dummy(
                        UiVec(0f, 7f));

                    SetUiFontScale(
                        1.08f);

                    ImGui.TextWrapped(
                        playingName);

                    SetUiFontScale(
                        1f);

                    ImGui.Dummy(
                        UiVec(0f, 8f));

                    string statusText;
                    Vector4 statusColor;

                    if (!localVideoBroadcastArmed)
                    {
                        statusText =
                            paused
                                ? "Local playback paused"
                                : "Playing on your TV";

                        statusColor =
                            MutedText;
                    }
                    else if (paused)
                    {
                        statusText =
                            "Broadcast paused";

                        statusColor =
                            Gold;
                    }
                    else if (stream.Roster.Length == 0)
                    {
                        statusText =
                            "Broadcast ready — waiting for viewers";

                        statusColor =
                            Gold;
                    }
                    else if (engine.IsLocalVideoBroadcasting)
                    {
                        statusText =
                            stream.Roster.Length == 1
                                ? "Broadcasting to 1 viewer"
                                : $"Broadcasting to {stream.Roster.Length} viewers";

                        statusColor =
                            new Vector4(
                                0.25f,
                                0.85f,
                                0.45f,
                                1f);
                    }
                    else
                    {
                        statusText =
                            "Starting relay upload…";

                        statusColor =
                            Accent;
                    }

                    ImGui.TextColored(
                        statusColor,
                        statusText);

                    ImGui.Dummy(
                        UiVec(0f, 8f));

                    var progress =
                        duration > 0f
                            ? Math.Clamp(
                                position / duration,
                                0f,
                                1f)
                            : 0f;

                    var elapsedText =
                        TimeSpan
                            .FromSeconds(
                                Math.Max(
                                    0f,
                                    position))
                            .ToString(
                                duration >= 3600f
                                    ? @"h\:mm\:ss"
                                    : @"m\:ss");

                    var durationText =
                        duration > 0f
                            ? TimeSpan
                                .FromSeconds(
                                    duration)
                                .ToString(
                                    duration >= 3600f
                                        ? @"h\:mm\:ss"
                                        : @"m\:ss")
                            : "--:--";

                    ImGui.ProgressBar(
                        progress,
                        new Vector2(
                            -1f,
                            Ui(18f)),
                        $"{elapsedText} / {durationText}");

                    ImGui.Dummy(
                        UiVec(0f, 11f));

                    var buttonGap = Ui(10f);

                    var availableWidth =
                        ImGui.GetContentRegionAvail().X;

                    var buttonWidth =
                        MathF.Max(
                            150f,
                            (availableWidth -
                             buttonGap) /
                            2f);

                    var activeHasBroadcastCredentials =
                        CurrentSession is { } activeSession &&
                        !string.IsNullOrWhiteSpace(
                            Plugin.Cfg.StreamKeys
                                .GetValueOrDefault(
                                    activeSession.AccountId));

                    var canStartBroadcast =
                        !viewingWatchParty &&
                        activeHasBroadcastCredentials &&
                        HasConfirmedPatreonAccess();

                    if (hostingWatchParty &&
    HasConfirmedPatreonAccess() &&
    !activeHasBroadcastCredentials)
                    {
                        ImGui.TextColored(
                            Gold,
                            "Generate a secret stream key in Settings > Account before live streaming is available.");

                        ImGui.Dummy(
                            UiVec(0f, 8f));
                    }

                    //
                    // Left button: start or stop broadcasting.
                    //

                    using (ImRaii.PushStyle(
                               ImGuiStyleVar.FrameRounding,
                               8f))
                    {
                        if (localVideoBroadcastArmed)
                        {
                            if (ImGui.Button(
                                    "Stop Broadcast",
                                    new Vector2(
                                        buttonWidth,
                                        Ui(38f))))
                            {
                                StopLocalVideoWatchPartyBroadcast();

                                localVideoError =
                                    null;
                            }
                        }
                        else
                        {
                            if (!HasConfirmedPatreonAccess())
                            {
                                DrawLockedLocalVideoBroadcastButton(
                                    "##unlockActiveLocalVideoBroadcast",
                                    "Broadcast to Watch Party",
                                    new Vector2(
                                        buttonWidth,
                                        Ui(38f)));
                            }
                            else
                            {
                                using (ImRaii.Disabled(
                                           !canStartBroadcast))
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
                                            "Broadcast to Watch Party",
                                            new Vector2(
                                                buttonWidth,
                                                Ui(38f))))
                                    {
                                        BeginLocalVideoWatchPartyBroadcast(
                                            startPlayback: false);
                                    }
                                }

                                if (viewingWatchParty &&
                                    ImGui.IsItemHovered(
                                        ImGuiHoveredFlags.AllowWhenDisabled))
                                {
                                    ImGui.SetTooltip(
                                        "Leave your current Watch Party before broadcasting this video.");
                                }
                                else if (!activeHasBroadcastCredentials &&
                                         ImGui.IsItemHovered(
                                             ImGuiHoveredFlags.AllowWhenDisabled))
                                {
                                    ImGui.SetTooltip(
                                        "Generate a secret stream key in Settings > Account first.");
                                }
                            }
                        }
                    }


                    ImGui.SameLine(
                        0f,
                        buttonGap);


                    //
                    // Right button: stop playback and remove the TV.
                    //

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
                                "Stop Video & Despawn TV",
                                new Vector2(
                                    buttonWidth,
                                    Ui(38f))))
                        {
                            StopLocalVideoWatchPartyBroadcast();

                            video.Stop();

                            localVideoError =
                                null;
                        }
                    }
                }
            }

            if (!HasConfirmedPatreonAccess())
            {
                DrawLocalVideoPatreonGate();
            }

            if (!string.IsNullOrWhiteSpace(
                    localVideoError))
            {
                ImGui.Dummy(
                    UiVec(0f, 8f));

                ImGui.TextColored(
                    Danger,
                    localVideoError);
            }

            return;
        }


        //
        // Availability
        //

        var gameActive = engine.IsPlayingGame || engine.IsPlayingBrowser;

        var normalPlaybackActive =
            queue.Current is not null;

        var localPlaybackAvailable =
            !viewingWatchParty &&
            !gameActive &&
            !normalPlaybackActive;

        if (!localPlaybackAvailable)
        {
            string reason;

            if (viewingWatchParty)
            {
                reason =
                    "Leave the current Watch Party before playing a local video.";
            }
            else if (gameActive)
            {
                reason =
                    "Exit the current game before playing a local video.";
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
                UiVec(0f, 12f));
        }


        //
        // File selector
        //

        ImGui.TextColored(
            MutedText,
            "Video File");

        ImGui.Dummy(
            UiVec(0f, 5f));

        var displayPath =
            string.IsNullOrWhiteSpace(
                localVideoSelectedPath)
                ? "No video selected"
                : localVideoSelectedPath;

        var browseButtonWidth = Ui(120f);

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
                        Ui(34f))))
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
            UiVec(0f, 7f));

        SetUiFontScale(
            0.80f);

        ImGui.TextColored(
            MutedText,
            "The file remains on your computer. It is only uploaded while Watch Party viewers are present.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 16f));


        //
        // Play and broadcast buttons
        //

        var hasFile =
            !string.IsNullOrWhiteSpace(
                localVideoSelectedPath) &&
            File.Exists(
                localVideoSelectedPath);

        var canPlay =
            localPlaybackAvailable &&
            hasFile;

        var hasBroadcastCredentials =
            CurrentSession is { } session &&
            !string.IsNullOrWhiteSpace(
                Plugin.Cfg.StreamKeys
                    .GetValueOrDefault(
                        session.AccountId));

        var canBroadcast =
            canPlay &&
            !viewingWatchParty &&
            hasBroadcastCredentials &&
            HasConfirmedPatreonAccess();

        if (hostingWatchParty &&
            HasConfirmedPatreonAccess() &&
            CurrentSession is not null &&
            !hasBroadcastCredentials)
        {
            ImGui.TextColored(
                Gold,
                "Generate a secret stream key in Settings > Account before live streaming is available.");

            ImGui.Dummy(
                UiVec(0f, 8f));
        }

        var actionGap = Ui(10f);

        var actionWidth =
            MathF.Max(
                180f,
                (ImGui.GetContentRegionAvail().X -
                 actionGap) /
                2f);

        using (ImRaii.Disabled(
                   !canPlay))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        {
            if (ImGui.Button(
                    "Play Local Video",
                    new Vector2(
                        actionWidth,
                        Ui(42f))))
            {
                StartSelectedLocalVideo(
                    broadcast: false);
            }
        }

        ImGui.SameLine(
            0f,
            actionGap);

        if (!HasConfirmedPatreonAccess())
        {
            DrawLockedLocalVideoBroadcastButton(
                "##unlockLocalVideoBroadcast",
                "Broadcast Local Video",
                new Vector2(
                    actionWidth,
                    Ui(42f)));
        }
        else
        {
            using (ImRaii.Disabled(
                       !canBroadcast))
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
                        "Broadcast Local Video",
                        new Vector2(
                            actionWidth,
                            Ui(42f))))
                {
                    BeginLocalVideoWatchPartyBroadcast(
                        startPlayback: true);
                }
            }

            if (viewingWatchParty &&
                ImGui.IsItemHovered(
                    ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(
                    "Leave your current Watch Party before broadcasting a local video.");
            }
            else if (!hasBroadcastCredentials &&
                     ImGui.IsItemHovered(
                         ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(
                    "Generate a secret stream key in Settings > Account first.");
            }
        }

        if (!HasConfirmedPatreonAccess())
        {
            DrawLocalVideoPatreonGate();
        }

        if (!string.IsNullOrWhiteSpace(
                localVideoError))
        {
            ImGui.Dummy(
                UiVec(0f, 10f));

            ImGui.TextColored(
                Danger,
                localVideoError);
        }


        //
        // Draw the file-dialog window.
        //

        ImGui.SetNextWindowSize(
            UiVec(900f, 600f),
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
        SetUiFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Play a video link");

        SetUiFontScale(1f);

        ImGui.Dummy(
            UiVec(0f, 10f));


        //
        // =========================================================
        // URL input
        // =========================================================
        //

        ImGui.SetNextItemWidth(-Ui(66f));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                UiVec(14f, 10f)))
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
                UiVec(12f, 10f)))
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
                UiVec(48f, 0f)))
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
            UiVec(0f, 5f));


        //
        // Help text
        //

        SetUiFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "Paste a direct video link or supported webpage URL.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 14f));


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
                UiVec(160f, 38f);


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

        if (stream.Mode == StreamMode.None)
        {
            ImGui.SameLine(
                0f,
                14f);

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       8f))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       FrameBgHover)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       Accent)
                   .Push(
                       ImGuiCol.ButtonActive,
                       AccentActive))
            {
                var buttonPos =
                    ImGui.GetCursorScreenPos();

                var buttonSize =
                    UiVec(190f, 38f);

                if (ImGui.Button(
                        "##playLinkInWatchParty",
                        buttonSize) &&
                    !string.IsNullOrWhiteSpace(
                        urlInput))
                {
                    BeginWebLinkWatchParty(
                        new Video.VideoQueueEntry(
                            urlInput.Trim(),
                            urlInput.Trim(),
                            string.Empty,
                            null,
                            null));

                    urlInput =
                        string.Empty;
                }

                DrawPlayerActionButtonContent(
                    buttonPos,
                    buttonSize,
                    FontAwesomeIcon.Users,
                    "Play in Watch Party",
                    Vector4.One);
            }
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
                UiVec(170f, 38f);


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
                UiVec(0f, 8f));


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
            UiVec(0f, 18f));

        ImGui.Separator();

        ImGui.Dummy(
            UiVec(0f, 14f));

        SetUiFontScale(
            1.08f);

        ImGui.TextColored(
            Vector4.One,
            "Recommended websites");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 2f));

        SetUiFontScale(
            0.84f);

        ImGui.TextColored(
            MutedText,
            "Popular websites commonly supported by Alpha Channel.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 10f));


        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var siteGap = Ui(10f);

        var siteCardWidth =
            (availableWidth -
             (siteGap * 3f)) /
            4f;

        var siteCardSize =
            new Vector2(
                siteCardWidth,
                Ui(56f));


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
            UiVec(0f, 8f));


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
            UiVec(0f, 8f));


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
            UiVec(0f, 10f));

        SetUiFontScale(
            0.84f);

        ImGui.TextColored(
            MutedText,
            "We support many other websites and we're constantly working to add more.");

        SetUiFontScale(
            1f);


        //
        // =========================================================
        // FAQ
        // =========================================================
        //

        ImGui.Dummy(
            UiVec(0f, 16f));

        ImGui.Separator();

        ImGui.Dummy(
            UiVec(0f, 14f));

        SetUiFontScale(
            1.08f);

        ImGui.TextColored(
            Vector4.One,
            "Frequently asked questions");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 10f));


        var faqGap = Ui(12f);

        var faqCardWidth =
            (ImGui.GetContentRegionAvail().X -
             faqGap) /
            2f;

        var faqCardSize =
            new Vector2(
                faqCardWidth,
                Ui(112f));


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
            UiVec(0f, 12f));


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

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    origin.X + Ui(18f),
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

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X + Ui(48f),
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
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    origin.X + Ui(17f),
                    origin.Y + Ui(18f)),
                ImGui.GetColorU32(
                    Accent),
                iconText);
        }


        //
        // Question
        //

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X + Ui(48f),
                origin.Y + Ui(15f)),
            ImGui.GetColorU32(
                Vector4.One),
            title);


        //
        // Answer
        //

        var bodyColor =
            ImGui.GetColorU32(
                MutedText);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X + Ui(48f),
                origin.Y + Ui(44f)),
            bodyColor,
            line1);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X + Ui(48f),
                origin.Y + Ui(62f)),
            bodyColor,
            line2);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X + Ui(48f),
                origin.Y + Ui(80f)),
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
                Ui(76f));

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
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    origin.X + Ui(18f),
                    origin.Y + Ui(20f)),
                ImGui.GetColorU32(
                    Accent),
                iconText);
        }


        //
        // Heading
        //

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X + Ui(52f),
                origin.Y + Ui(14f)),
            ImGui.GetColorU32(
                Accent),
            "Unsure if a video or webpage is supported?");


        //
        // Supporting text
        //

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X + Ui(52f),
                origin.Y + Ui(42f)),
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
            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                start,
                ImGui.GetColorU32(color),
                iconText);
        }

        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            start + new Vector2(iconSize.X + gap, 0f),
            ImGui.GetColorU32(color),
            label);
    }
}

