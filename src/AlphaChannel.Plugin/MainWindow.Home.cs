using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using YoutubeExplode.Videos;

namespace AlphaChannel.Plugin;

// Welcome Home — mockup layout with only real capabilities (no fake browse/retro/voice).
internal sealed partial class MainWindow
{
    private static readonly Vector4[] AvatarPalette =
    [
        new(0.55f, 0.35f, 0.95f, 1f),
        new(0.95f, 0.45f, 0.55f, 1f),
        new(0.35f, 0.65f, 0.95f, 1f),
        new(0.95f, 0.70f, 0.30f, 1f),
        new(0.40f, 0.85f, 0.65f, 1f),
    ];
    // Player source tabs: Home CTAs set this before navigating to Player.
    private int playerSourceTab;
    private string friendSearch = string.Empty;
    private string homeSearch = string.Empty;
    private string? pendingPlayerSearch;
    private bool homeSearchPopupOpen;
    private int homeSearchInputVersion;
    private int homeVideoColumnCount = 5;

    private const int FavouritePageSize = 20;
    private int favouriteVideosVisibleCount = FavouritePageSize;


    private readonly HashSet<string> temporaryYouTubeSubscriptions =
        new(StringComparer.OrdinalIgnoreCase);

    private Vector2 homeSearchInputPos;
    private ISharedImmediateTexture? addFriendImage;
    private readonly Dictionary<string, ISharedImmediateTexture?> capabilityImages = new();

    // Constant spacing on right side
    private const float HomeContentRightInsetDesign = 18f;
    private float HomeContentRightInset => Ui(HomeContentRightInsetDesign);



    private const string FeaturedVideoUrl =
    "https://www.youtube.com/watch?v=zTTtd6bnhFs";

    private const string FeaturedVideoTitle =
        "FINAL FANTASY XIV: ENDWALKER Full Trailer";

    private const string FeaturedVideoChannel =
        "FINAL FANTASY XIV";

    private const string FeaturedVideoThumbnail =
        "https://i.ytimg.com/vi/zTTtd6bnhFs/maxresdefault.jpg";

    private sealed record FeaturedSlide(
    string Url,
    string VideoId);

    private static readonly FeaturedSlide[] FeaturedSlides =
    [
        new(
        "https://www.youtube.com/watch?v=99uyS9WCV38",
        "99uyS9WCV38"),

    new(
        "https://www.youtube.com/watch?v=_Nepqo6ML4Q",
        "_Nepqo6ML4Q"),

    new(
        "https://www.youtube.com/watch?v=AO2xe-T-MP4",
        "AO2xe-T-MP4"),

    new(
        "https://www.youtube.com/watch?v=ecjI-T1zP-o",
        "ecjI-T1zP-o"),
];

    private VideoSearchEntry?[] featuredSlideResults =
    new VideoSearchEntry?[FeaturedSlides.Length];
    private bool featuredSlidesRequested;

    private int featuredRetryCount;
    private double featuredNextRetryAt;
    private bool featuredRetryRunning;


    private int featuredSlideIndex;
    private int featuredNextSlideIndex = 1;

    private double featuredSlideSettledAt = -1d;
    private double featuredTransitionStartedAt = -1d;

    private bool featuredTransitioning;
    private int featuredTransitionDirection = 1;

    private ISharedImmediateTexture? GetCapabilityImage(string fileName)
{
    if (capabilityImages.TryGetValue(fileName, out var cached))
    {
        return cached;
    }

    var path = Path.Combine(
        Plugin.PluginInterface.AssemblyLocation.DirectoryName!,
        "Assets",
        fileName);

    ISharedImmediateTexture? image = null;

    if (File.Exists(path))
    {
        image = Plugin.TextureProvider.GetFromFile(path);
    }

    capabilityImages[fileName] = image;
    return image;
}

    private void DrawHomeSearchSuggestion(
    FontAwesomeIcon icon,
    string title,
    string? subtitle,
    string id)
    {
        var rowHeight = Ui(34f);

        var origin =
            ImGui.GetCursorScreenPos();

        var width =
            ImGui.GetContentRegionAvail().X;

        ImGui.InvisibleButton(
            $"##homeSearchSuggestion_{id}",
            new Vector2(
                width,
                subtitle is null
                    ? rowHeight
                    : Ui(44f)));

        var hovered =
            ImGui.IsItemHovered();

        var drawList =
            ImGui.GetWindowDrawList();

        if (hovered)
        {
            drawList.AddRectFilled(
                origin,
                origin +
                new Vector2(
                    width,
                    subtitle is null
                        ? rowHeight
                        : Ui(44f)),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.12f)),
                7f);
        }

        var iconPos =
            origin +
            new Vector2(
                Ui(8f),
                subtitle is null
                    ? Ui(9f)
                    : Ui(13f));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconPos,
                ImGui.GetColorU32(
                    hovered
                        ? AccentHover
                        : Accent),
                icon.ToIconString());
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            origin +
            new Vector2(
                Ui(31f),
                subtitle is null
                    ? Ui(8f)
                    : Ui(6f)),
            ImGui.GetColorU32(Vector4.One),
            title);

        if (subtitle is not null)
        {
            var displaySubtitle =
                subtitle.Length > 62
                    ? subtitle[..59] + "..."
                    : subtitle;

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                origin +
                UiVec(31f, 24f),
                ImGui.GetColorU32(MutedText),
                displaySubtitle);
        }
    }

    private void TryRetryFeaturedSlides()
    {
        if (!featuredSlidesRequested ||
            featuredRetryRunning)
        {
            return;
        }

        if (featuredRetryCount >= 2)
        {
            return;
        }

        if (ImGui.GetTime() < featuredNextRetryAt)
        {
            return;
        }

        var missing =
            featuredSlideResults.Any(
                slide => slide is null);

        if (!missing)
        {
            return;
        }

        featuredRetryRunning = true;
        featuredRetryCount++;

        _ = RetryFeaturedSlidesAsync();
    }

    private async Task RetryFeaturedSlidesAsync()
    {
        try
        {
            await LoadFeaturedSlidesAsync();
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Featured Retry] {exception.Message}");
        }
        finally
        {
            featuredRetryRunning = false;

            if (featuredRetryCount < 2 &&
                featuredSlideResults.Any(
                    slide => slide is null))
            {
                featuredNextRetryAt =
                    ImGui.GetTime() + 30.0;
            }
        }
    }

    private void DrawHome()
    {
        var contentPaddingLeft = Ui(24f);

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() + contentPaddingLeft);

        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing,
            new Vector2(
                ImGui.GetStyle().ItemSpacing.X,
                ImGui.GetStyle().ItemSpacing.Y));

        var searchWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.42f, Ui(280f), Ui(520f));



        if (!featuredSlidesRequested)
        {
            featuredSlidesRequested = true;

            featuredRetryCount = 0;
            featuredNextRetryAt = ImGui.GetTime() + 10.0;

            _ = LoadFeaturedSlidesAsync();

        }
        TryRetryFeaturedSlides();

        if (Plugin.Cfg.ShowFfxivYouTubeSection &&
            !ffxivYouTubeRequested)
        {
            ffxivYouTubeRequested = true;
            isLoadingFfxivYouTube = true;

            _ = LoadFfxivYouTubeAsync();
        }

        if (Plugin.Cfg.ShowHomeHeroImage)
        {
            EnsureHomeHeroLoaded();
        }

    

        var searchActive =
    ImGui.IsItemActive();

        var searchClicked =
            ImGui.IsItemClicked();

        var trimmedSearch =
            homeSearch.Trim();

        homeSearchPopupOpen =
            !string.IsNullOrWhiteSpace(trimmedSearch);

        var looksLikeUrl =
            Uri.TryCreate(
                trimmedSearch,
                UriKind.Absolute,
                out var searchUri)
            &&
            (
                searchUri.Scheme == Uri.UriSchemeHttp ||
                searchUri.Scheme == Uri.UriSchemeHttps
            );

        if (!string.IsNullOrWhiteSpace(trimmedSearch))
        {
            var searchMin =
                homeSearchInputPos;

            var searchMax =
                homeSearchInputPos +
                new Vector2(
                    searchWidth,
                    ImGui.GetFrameHeight());

            var popupPos =
                new Vector2(
                    searchMin.X,
                    searchMax.Y + Ui(6f));


            var drawList =
                ImGui.GetForegroundDrawList();

            var popupSize =
                new Vector2(
                    searchWidth,
                    looksLikeUrl ? Ui(108f) : Ui(100f));

            drawList.AddRectFilled(
                popupPos,
                popupPos + popupSize,
                ImGui.GetColorU32(CardBg),
                10f);

            var textPos = popupPos + UiVec(16f, 14f);

            string suggestionText;

            if (looksLikeUrl)
            {
                suggestionText = "▶  Play video";
            }
            else
            {
                suggestionText = $"▶  Search for \"{trimmedSearch}\" videos";
            }

            var rowHeight = looksLikeUrl ? 44f : 38f;
            var rowSpacing = 4f;

            var rows = looksLikeUrl
                ? new[]
                {
        "▶  Play video",
        "+  Add video to queue"
                }
                : new[]
                {
        $"▶  Search for \"{trimmedSearch}\" videos",
        $"▶  Find Twitch channel \"{trimmedSearch}\""
                };

            for (int i = 0; i < rows.Length; i++)
            {
                var rowMin =
                    popupPos +
                    new Vector2(
                        Ui(8f),
                        Ui(8f) + i * (rowHeight + rowSpacing));

                var rowMax =
                    rowMin +
                    new Vector2(
                        searchWidth - Ui(16f),
                        rowHeight);

                // ---------------------------------------------------------
                // Hover detection
                // ---------------------------------------------------------

                var mousePos = ImGui.GetMousePos();

                var rowHovered =
                    mousePos.X >= rowMin.X &&
                    mousePos.X <= rowMax.X &&
                    mousePos.Y >= rowMin.Y &&
                    mousePos.Y <= rowMax.Y;

                // ---------------------------------------------------------
                // Row background
                // ---------------------------------------------------------

                drawList.AddRectFilled(
                    rowMin,
                    rowMax,
                    ImGui.GetColorU32(
                        rowHovered
                            ? new Vector4(
                                Accent.X,
                                Accent.Y,
                                Accent.Z,
                                0.18f)
                            : new Vector4(
                                0.12f,
                                0.15f,
                                0.22f,
                                1f)),
                    6f);

                // ---------------------------------------------------------
                // Row text
                // ---------------------------------------------------------

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    rowMin + UiVec(12f, 8f),
                    ImGui.GetColorU32(Vector4.One),
                    rows[i]);

                if (looksLikeUrl)
                {
                    var displayUrl =
                        trimmedSearch.Length > 62
                            ? trimmedSearch[..59] + "..."
                            : trimmedSearch;

                    drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                        rowMin + UiVec(12f, 24f),
                        ImGui.GetColorU32(MutedText),
                        displayUrl);
                }

                // ---------------------------------------------------------
                // Mouse cursor
                // ---------------------------------------------------------

                if (rowHovered)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                }

                // ---------------------------------------------------------
                // Click
                // ---------------------------------------------------------

                if (rowHovered &&
    ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    if (looksLikeUrl)
                    {
                        switch (i)
                        {
                            case 0:
                                // Play immediately, stay on Home.
                                queue.PlayNow(
                                    new VideoQueueEntry(
                                        trimmedSearch,
                                        trimmedSearch,
                                        string.Empty,
                                        null,
                                        null));
                                break;

                            case 1:
                                // Add to queue, stay on Home.
                                queue.Add(
                                    new VideoQueueEntry(
                                        trimmedSearch,
                                        trimmedSearch,
                                        string.Empty,
                                        null,
                                        null));

                                queueAddedFeedbackUntil =
                                    ImGui.GetTime() + 2.0;
                                break;
                        }
                    }
                    else
                    {
                        switch (i)
                        {
                            case 0:
                                OpenUnifiedVideoSearch(trimmedSearch);
                                break;

                            case 1:
                                // Twitch
                                OpenPlayerSearch(
                                    2,
                                    trimmedSearch);
                                break;
                        }
                    }

                    // Clear the Home search after ANY action.
                    homeSearch = string.Empty;
                    homeSearchPopupOpen = false;
                    homeSearchInputVersion++;
                }
            }

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                !searchActive)
            {
                homeSearchPopupOpen = false;
            }
        }



        ImGui.Dummy(UiVec(0f, -6f));


        // ---------------------------------------------------------
        // Header row
        // ---------------------------------------------------------

        var contentWidth = ImGui.GetContentRegionAvail().X;
        var startX = ImGui.GetCursorPosX();
        var headerY = ImGui.GetCursorPosY();

        var showWatchers = contentWidth >= Ui(500f);


        // ---------------------------------------------------------
        // Left: Welcome text
        // ---------------------------------------------------------
        ImGui.SetCursorPos(
        new Vector2(
            startX + Ui(5f),
            headerY + Ui(6f)));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                Accent,
                FontAwesomeIcon.Users.ToIconString());
        }

        ImGui.SameLine(0, 8);

        SetUiFontScale(1.08f);

        ImGui.TextColored(
            MutedText,
            "Welcome to ");

        ImGui.SameLine(0, 0);

        ImGui.TextColored(
            Accent,
            "Alpha Channel");

        SetUiFontScale(1f);


        // ---------------------------------------------------------
        // Centre: Search bar
        // ---------------------------------------------------------

        //
        // Keep the search bar centered as it narrows with the window.
        //

        var searchX =
            startX +
            (contentWidth - searchWidth) *
            0.5f;


        ImGui.SetCursorPos(
            new Vector2(
                searchX,
                headerY));


        ImGui.SetNextItemWidth(
            searchWidth);



            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                Ui(18f)))
        {
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FramePadding,
                UiVec(36f, 9f)))
            {
                homeSearchInputPos =
                    ImGui.GetCursorScreenPos();

                ImGui.InputTextWithHint(
                    $"##homeSearch_{homeSearchInputVersion}",
                    "Search videos, channels, or paste a link...",
                    ref homeSearch,
                    256);


            }
        }


        // ---------------------------------------------------------
        // Search icon reserved area
        // ---------------------------------------------------------

        var searchDrawList =
            ImGui.GetWindowDrawList();

        var searchHeight =
            ImGui.GetFrameHeight();

        var iconAreaMin =
            homeSearchInputPos +
            UiVec(2f, 2f);

        var iconAreaMax =
            homeSearchInputPos +
            new Vector2(
                Ui(35f),
                searchHeight - Ui(2f));

        var iconAreaColor =
    ImGui.GetColorU32(FrameBg);

        // Cover any horizontally-scrolled text that would otherwise
        // slide underneath the magnifying glass.
        searchDrawList.AddRectFilled(
            iconAreaMin,
            iconAreaMax,
            iconAreaColor,
            16f,
            ImDrawFlags.RoundCornersLeft);


        // ---------------------------------------------------------
        // Search icon
        // ---------------------------------------------------------

        var iconPos =
            homeSearchInputPos +
            UiVec(14f, 7f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            searchDrawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconPos,
                ImGui.GetColorU32(Accent),
                FontAwesomeIcon.Search.ToIconString());
        }


        // ---------------------------------------------------------
        // Right: profile + social status
        // ---------------------------------------------------------
        //
        // This replaces the old standalone "Watchers Online" text.
        //
        // Keep this tied to the existing showWatchers responsive rule.
        // When the Home window becomes narrow enough that watchers used
        // to disappear, this entire Home profile block disappears too.
        //
        // The non-Home header version will be added separately later and
        // will NOT use this responsive hiding rule.
        //

        if (showWatchers)
        {
            var session =
                CurrentSession;


            var friendsOnline =
                friends.Count(
                    friend => friend.Online);


            var displayName =
                !string.IsNullOrWhiteSpace(
                    session?.DisplayName)
                    ? session.DisplayName
                    : "Unknown";


            var friendsText =
                friendsOnline == 1
                    ? "1 friend online"
                    : $"{friendsOnline} friends online";


            var watchersText =
                usersOnlineCount == 1
                    ? "1 watcher online"
                    : $"{usersOnlineCount} watchers online";


            //
            // Compact three-line layout:
            //
            // [avatar]  ● Kodie
            //           1 friend online
            //           2 watchers online
            //

            var avatarSize = Ui(38f);
            var profileWidth = Ui(185f);


            var profileX =
                startX +
                contentWidth -
                profileWidth -
                10f;


            var profileY =
                headerY - 2f;


            var profileOrigin =
                new Vector2(
                    profileX,
                    profileY);


            //
            // Avatar
            //

            ImGui.SetCursorPos(
                profileOrigin);


            DrawAvatarChip(
                session?.AvatarIcon,
                session?.AvatarColorHex,
                avatarSize,
                session?.AvatarImageUrl);


            //
            // Text starts just to the right of the avatar.
            //

            var textX =
                profileX +
                avatarSize +
                10f;


            //
            // First row:
            //
            // ● Kodie
            //

            ImGui.SetCursorPos(
                new Vector2(
                    textX,
                    profileY + 1f));


            ImGui.TextColored(
                Good,
                "●");


            ImGui.SameLine(
                0f,
                5f);


            ImGui.TextUnformatted(
                displayName);


            //
            // Second row:
            //
            // 1 friend online
            //

            ImGui.SetCursorPos(
                new Vector2(
                    textX,
                    profileY + Ui(17f)));


            //
            // Friends are deliberately the quieter secondary status.
            //

            SetUiFontScale(
                0.84f);


            ImGui.TextColored(
                MutedText,
                friendsText);


            //
            // Third row:
            //
            // 2 watchers online
            //
            // Give this slightly more visual weight than the friends
            // count because it describes Alpha Channel activity.
            //

            ImGui.SetCursorPos(
            new Vector2(
                textX,
                profileY + Ui(35f)));


            //
            // Watcher activity is the strongest secondary status in this
            // profile block, so give it a little more size and live color.
            //

            SetUiFontScale(
                1.02f);


            ImGui.TextColored(
                Good,
                watchersText);


            SetUiFontScale(
                1f);
        }

        SetUiFontScale(1f);
        ImGui.Dummy(UiVec(0f, 2f));

        //
        // Subtitle becomes more compact shortly before the search bar
        // itself switches to its narrower layout.
        //
        // Search bar shrinks at 600f.
        // Subtitle shortens slightly earlier at 670f.
        //

        var subtitle =
            contentWidth < 670f
                ? "Your shared media hub in FFXIV"
                : "Your shared media hub in FFXIV — Watch, play and listen together, anywhere in Eorzea";


        var icon =
            FontAwesomeIcon.PlayCircle.ToIconString();

        float iconWidth;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconWidth =
                ImGui.CalcTextSize(icon).X;
        }

        var subtitleWidth =
            ImGui.CalcTextSize(subtitle).X;

        var totalWidth =
            iconWidth + 8f + subtitleWidth;

        var subtitleStartX =
            startX + (contentWidth - totalWidth) * 0.5f;


        ImGui.SetCursorPosX(subtitleStartX);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                Accent,
                icon);
        }

        ImGui.SameLine(0, 8);

        ImGui.TextColored(
            MutedText,
            subtitle);
        ImGui.Dummy(new Vector2(0f, 0f));

        // ---------------------------------------------------------
        // Featured
        // ---------------------------------------------------------

        DrawMediaHubFeatured();

        DrawHomeQuickStart();

        ImGui.Dummy(
            UiVec(0f, 2f));

        DrawHomeYouTubeShelf();

        ImGui.Dummy(
            UiVec(0f, 2f));

        DrawWatchPartiesShelf();

        if (Plugin.Cfg.ShowFfxivYouTubeSection)
        {
            ImGui.Dummy(UiVec(0f, 2f));
            DrawFfxivYouTubeShelf();
        }

        ImGui.Dummy(UiVec(0f, 2f));

        DrawRecentlyWatchedShelf();
        ImGui.PopStyleVar();
    }

    private void DrawHomeQuickStart()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var gap =
            Ui(10f);

        var showMoreMedia =
            ImGui.GetWindowSize().X >=
            MinimumWindowSize.X +
            150f;

        var cardCount =
            showMoreMedia
                ? 5
                : 4;

        var cardWidth =
            (availableWidth -
             gap *
             (cardCount - 1)) /
            cardCount;

        var cardHeight =
            Ui(64f);

        DrawHomeQuickStartCard(
            "watchVideo",
            FontAwesomeIcon.Play,
            Accent,
            "Watch a Video",
            "Find something to play",
            cardWidth,
            cardHeight,
            () =>
            {
                currentPage =
                    HomePage.Player;

                activePlayerDrawer =
                    PlayerDrawer.PlayVideo;

                playerSourceTab =
                    0;

                showingAddMediaSources =
                    false;
            });

        ImGui.SameLine(
            0f,
            gap);

        DrawHomeQuickStartCard(
            "watchParty",
            FontAwesomeIcon.Users,
            Hex(0xEC4899),
            "Start a Watch Party",
            "Watch with friends",
            cardWidth,
            cardHeight,
            () => currentPage =
                HomePage.WatchAlong);

        ImGui.SameLine(
            0f,
            gap);

        DrawHomeQuickStartCard(
            "retroGames",
            FontAwesomeIcon.Gamepad,
            Hex(0x3B82F6),
            "Play Retro Games",
            "Play classic games",
            cardWidth,
            cardHeight,
            () => currentPage =
                HomePage.PlaySnes);

        ImGui.SameLine(
            0f,
            gap);

        DrawHomeQuickStartCard(
            "browser",
            FontAwesomeIcon.Globe,
            Hex(0x14B8A6),
            "Open Browser",
            "Browse the web",
            cardWidth,
            cardHeight,
            () => currentPage =
                HomePage.Browser);

        if (showMoreMedia)
        {
            ImGui.SameLine(
                0f,
                gap);

            DrawHomeQuickStartCard(
                "moreMedia",
                FontAwesomeIcon.PlusSquare,
                Hex(0xF97316),
                "More Media",
                "See every media source",
                cardWidth,
                cardHeight,
                () =>
                {
                    currentPage =
                        HomePage.Player;

                    activePlayerDrawer =
                        PlayerDrawer.PlayVideo;

                    showingAddMediaSources =
                        true;
                });
        }
    }

    private void DrawHomeQuickStartCard(
        string id,
        FontAwesomeIcon icon,
        Vector4 iconColor,
        string title,
        string description,
        float width,
        float height,
        Action onClick)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);

        if (ImGui.InvisibleButton(
                $"##homeQuickStart_{id}",
                size))
        {
            onClick();
        }

        var hovered =
            ImGui.IsItemHovered();

        if (hovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);
        }

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                hovered
                    ? CardBgHover
                    : new Vector4(
                        CardBg.X,
                        CardBg.Y,
                        CardBg.Z,
                        0.45f)),
            Ui(9f));

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                hovered
                    ? new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.82f)
                    : new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.48f)),
            Ui(9f),
            ImDrawFlags.None,
            hovered
                ? Ui(1.5f)
                : Ui(1f));

        var iconBoxSize =
            Ui(38f);

        var iconBoxMin =
            origin +
            UiVec(12f, 13f);

        drawList.AddRectFilled(
            iconBoxMin,
            iconBoxMin +
            new Vector2(
                iconBoxSize,
                iconBoxSize),
            ImGui.GetColorU32(
                new Vector4(
                    iconColor.X,
                    iconColor.Y,
                    iconColor.Z,
                    hovered
                        ? 1f
                        : 0.88f)),
            Ui(8f));

        var iconText =
            icon.ToIconString();

        Vector2 iconTextSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            iconTextSize =
                ImGui.CalcTextSize(
                    iconText);
        }

        drawList.AddText(
            UiBuilder.IconFont,
            ImGui.GetFontSize(),
            iconBoxMin +
            new Vector2(
                (iconBoxSize - iconTextSize.X) * 0.5f,
                (iconBoxSize - iconTextSize.Y) * 0.5f),
            ImGui.GetColorU32(
                Vector4.One),
            iconText);

        var textX =
            iconBoxMin.X +
            iconBoxSize +
            Ui(10f);

        var textWidth =
            Math.Max(
                Ui(20f),
                origin.X +
                width -
                Ui(10f) -
                textX);

        DrawWrappedLines(
            drawList,
            new Vector2(
                textX,
                origin.Y +
                Ui(15f)),
            textWidth,
            ImGui.GetTextLineHeight(),
            2,
            ImGui.GetColorU32(
                Vector4.One),
            title);

        SetUiFontScale(
            0.78f);

        DrawWrappedLines(
            drawList,
            new Vector2(
                textX,
                origin.Y +
                Ui(36f)),
            textWidth,
            ImGui.GetTextLineHeight(),
            1,
            ImGui.GetColorU32(
                MutedText),
            description);

        SetUiFontScale(
            1f);
    }

    private int GetHomeVideoColumnCount(float windowWidth)
    {
        switch (homeVideoColumnCount)
        {
            case 5:
                if (windowWidth < Ui(780f))
                    homeVideoColumnCount = 4;
                break;

            case 4:
                if (windowWidth >= Ui(900f))
                    homeVideoColumnCount = 5;
                else if (windowWidth < Ui(560f))
                    homeVideoColumnCount = 3;
                break;

            case 3:
                if (windowWidth >= Ui(720f))
                    homeVideoColumnCount = 4;
                break;
        }

        return homeVideoColumnCount;
    }

    private void DrawHomeYouTubeShelf()
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            ImGui.TextColored(
                new Vector4(
                    1f,
                    0.25f,
                    0.35f,
                    1f),
                FontAwesomeIcon.Fire.ToIconString());
        }

        ImGui.SameLine(
            0f,
            Ui(8f));

        ImGui.Text(
            "Trending on YouTube");

        ImGui.SameLine(
            0f,
            Ui(8f));

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            ImGui.TextColored(
                MutedText,
                FontAwesomeIcon.InfoCircle.ToIconString());
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Adjust your trending video topics in Settings.");
        }

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            ImGui.GetWindowContentRegionMax().X -
            HomeContentRightInset -
            Ui(22f));

        var refreshPosition =
      ImGui.GetCursorScreenPos();

        var refreshGlyph =
            FontAwesomeIcon.Sync.ToIconString();

        Vector2 refreshGlyphSize;

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            refreshGlyphSize =
                ImGui.CalcTextSize(
                    refreshGlyph);
        }

        var refreshHovered =
            ImGui.IsMouseHoveringRect(
                refreshPosition,
                refreshPosition +
                refreshGlyphSize);

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            ImGui.TextColored(
                refreshHovered
                    ? AccentHover
                    : MutedText,
                refreshGlyph);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Show different trending videos from your cached topics");

            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);
        }

        if (ImGui.IsItemClicked())
        {
            RefreshHomeYouTubeFromCache();
        }

        const float compactCardHeightDesign =
            188f;

        var cardHeight =
            Ui(
                compactCardHeightDesign);

        if (homeYouTubeResults is not
            { Count: > 0 } results)
        {
            if (isLoadingHomeYouTube)
            {
                DrawMediaHubLoadingCards(
                    cardHeight);
            }
            else
            {
                DrawMediaHubShelfCards(
                    cardHeight);
            }

            return;
        }

        var windowWidth =
            ImGui.GetWindowSize().X;

        var cardCount =
            GetHomeVideoColumnCount(
                windowWidth);

        var gap =
            Ui(12f);

        var visibleCount =
            Math.Min(
                cardCount,
                results.Count);

        var cardWidth =
            (width -
             gap *
             (cardCount - 1)) /
            cardCount;

        for (var index = 0;
             index < visibleCount;
             index++)
        {
            if (index > 0)
            {
                ImGui.SameLine(
                    0f,
                    gap);
            }

            ImGui.PushID(
                $"homeYoutube_{index}");

            DrawCompactHomeYouTubeCardSurface(
                results[index],
                cardWidth,
                cardHeight,
                useThumbnailActions: true);

            ImGui.PopID();
        }
    }

    private void DrawHomeShelfHeading(
     FontAwesomeIcon icon,
     string title,
     Vector4 iconColor,
     bool showSeeAll = true,
     bool addBottomSpacing = true,
     Action? onSeeAll = null)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        var iconGap = Ui(8f);

        Vector2 iconSize;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph =
                icon.ToIconString();

            iconSize =
                ImGui.CalcTextSize(glyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    origin.X,
                    origin.Y + 1f),
                ImGui.GetColorU32(iconColor),
                glyph);
        }

        // Move the normal ImGui title to the right of the icon.
        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() +
            iconSize.X +
            iconGap);

        SetUiFontScale(1.08f);

        ImGui.TextUnformatted(title);

        SetUiFontScale(1f);

        if (showSeeAll)
        {
            const string seeAll =
                "See all";

            var seeAllSize =
                ImGui.CalcTextSize(seeAll);

            Vector2 chevronSize;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                chevronSize =
                    ImGui.CalcTextSize(
                        FontAwesomeIcon.ChevronRight.ToIconString());
            }

            var chevronGap = Ui(7f);

            var right =
                ImGui.GetWindowPos().X +
                ImGui.GetWindowContentRegionMax().X -
                HomeContentRightInset;

            var seeAllX =
                right -
                seeAllSize.X -
                chevronGap -
                chevronSize.X;

            var seeAllMin =
    new Vector2(
        seeAllX,
        origin.Y);

            var seeAllMax =
                new Vector2(
                    right,
                    origin.Y + ImGui.GetTextLineHeight() + Ui(6f));

            var seeAllHovered =
                ImGui.IsMouseHoveringRect(
                    seeAllMin,
                    seeAllMax);

            var seeAllColor =
                seeAllHovered
                    ? AccentHover
                    : MutedText;

            if (seeAllHovered)
            {
                ImGui.SetMouseCursor(
                    ImGuiMouseCursor.Hand);
            }

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    seeAllX,
                    origin.Y + Ui(3f)),
                ImGui.GetColorU32(seeAllColor),
                seeAll);

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    new Vector2(
                        seeAllX +
                        seeAllSize.X +
                        chevronGap,
                        origin.Y + Ui(2f)),
                    ImGui.GetColorU32(seeAllColor),
                    FontAwesomeIcon.ChevronRight.ToIconString());
            }

            if (onSeeAll is not null &&
                seeAllHovered &&
                ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                onSeeAll();
            }
        }
        if (addBottomSpacing)
        {
            ImGui.Dummy(
                UiVec(0f, 5f));
        }
    }


    private void DrawMediaHubShelfCards(
      float cardHeight)
    {
        var width = ImGui.GetContentRegionAvail().X;

        var itemCount =
            GetHomeVideoColumnCount(ImGui.GetWindowSize().X);

        var gap = Ui(10f);

        var cardWidth =
            (width - gap * (itemCount - 1)) /
            itemCount;

        for (var index = 0;
             index < itemCount;
             index++)
        {
            if (index > 0)
            {
                ImGui.SameLine(0f, gap);
            }

            ImGui.PushID($"placeholder_{index}");

            DrawMediaHubPlaceholderCard(
                cardWidth,
                cardHeight);

            ImGui.PopID();
        }
    }


    private void DrawMediaHubLoadingCards(
        float cardHeight)
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        var itemCount =
            GetHomeVideoColumnCount(ImGui.GetWindowSize().X);

        var gap = Ui(10f);

        var cardWidth =
            (width - gap * (itemCount - 1)) /
            itemCount;

        for (var index = 0;
             index < itemCount;
             index++)
        {
            if (index > 0)
            {
                ImGui.SameLine(
                    0f,
                    gap);
            }

            ImGui.PushID(
                $"loading_{index}");

            DrawMediaHubLoadingCard(
                cardWidth,
                cardHeight);

            ImGui.PopID();
        }
    }

    private void DrawMediaHubLoadingCard(
    float width,
    float height)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.InvisibleButton(
            "##loadingCard",
            size);

        var thumbnailHeight =
            Ui(116f);

        drawList.AddRectFilled(
            origin,
            origin +
            size,
            ImGui.GetColorU32(
                new Vector4(
                    CardBg.X,
                    CardBg.Y,
                    CardBg.Z,
                    0.72f)),
            Ui(10f));

        drawList.AddRect(
            origin,
            origin +
            size,
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.20f)),
            Ui(10f),
            ImDrawFlags.None,
            Ui(1f));

        drawList.AddRectFilled(
            origin,
            origin +
            new Vector2(
                width,
                thumbnailHeight),
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.12f)),
            Ui(9f));

        var center =
            origin +
            new Vector2(
                width *
                0.5f,
                thumbnailHeight *
                0.5f);

        var radius =
            Ui(14f);

        var rotation =
            (float)ImGui.GetTime() *
            4.5f;

        drawList.AddCircle(
            center,
            radius,
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.16f)),
            32,
            Ui(2.5f));

        drawList.PathArcTo(
            center,
            radius,
            rotation,
            rotation +
            MathF.PI *
            1.5f,
            24);

        drawList.PathStroke(
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.95f)),
            ImDrawFlags.None,
            Ui(3.2f));

        var textX =
            origin.X +
            Ui(8f);

        var titleY =
            origin.Y +
            thumbnailHeight +
            Ui(9f);

        drawList.AddRectFilled(
            new Vector2(
                textX,
                titleY),
            new Vector2(
                origin.X +
                width *
                0.78f,
                titleY +
                Ui(7f)),
            ImGui.GetColorU32(
                new Vector4(
                    1f,
                    1f,
                    1f,
                    0.18f)),
            Ui(3f));

        drawList.AddRectFilled(
            new Vector2(
                textX,
                titleY +
                Ui(15f)),
            new Vector2(
                origin.X +
                width *
                0.58f,
                titleY +
                Ui(22f)),
            ImGui.GetColorU32(
                new Vector4(
                    1f,
                    1f,
                    1f,
                    0.10f)),
            Ui(3f));

        drawList.AddRectFilled(
            new Vector2(
                textX,
                titleY +
                Ui(35f)),
            new Vector2(
                origin.X +
                width *
                0.43f,
                titleY +
                Ui(42f)),
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.14f)),
            Ui(3f));

        var footerHeight =
            Ui(30f);

        var footerPadding =
            Ui(6f);

        var footerGap =
            Ui(6f);

        var footerTop =
            origin.Y +
            height -
            footerPadding -
            footerHeight;

        var availableFooterWidth =
            width -
            footerPadding *
            2f;

        var buttonWidth =
            (availableFooterWidth -
             footerGap) *
            0.5f;

        drawList.AddRectFilled(
            new Vector2(
                origin.X +
                footerPadding,
                footerTop),
            new Vector2(
                origin.X +
                footerPadding +
                buttonWidth,
                footerTop +
                footerHeight),
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.20f)),
            Ui(6f));

        drawList.AddRectFilled(
            new Vector2(
                origin.X +
                footerPadding +
                buttonWidth +
                footerGap,
                footerTop),
            new Vector2(
                origin.X +
                width -
                footerPadding,
                footerTop +
                footerHeight),
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.10f)),
            Ui(6f));
    }

    private static string? GetYouTubeVideoId(
    string url)
    {
        var videoId =
            VideoId.TryParse(url);

        return videoId?.Value;
    }

    private static readonly Vector4[] BrowseTopicTagPalette =
[
    new(0.55f, 0.32f, 0.95f, 1f),
    new(0.18f, 0.58f, 0.92f, 1f),
    new(0.10f, 0.68f, 0.56f, 1f),
    new(0.90f, 0.45f, 0.20f, 1f),
    new(0.82f, 0.30f, 0.52f, 1f),
    new(0.68f, 0.52f, 0.12f, 1f),
    new(0.35f, 0.52f, 0.92f, 1f),
    new(0.72f, 0.28f, 0.82f, 1f)
];

    private static Vector4 GetBrowseTopicTagColour(
        string topic)
    {
        unchecked
        {
            uint hash =
                2166136261;

            foreach (var character in topic)
            {
                hash ^=
                    char.ToUpperInvariant(
                        character);

                hash *=
                    16777619;
            }

            return BrowseTopicTagPalette[
                hash %
                BrowseTopicTagPalette.Length];
        }
    }

    private void DrawCompactHomeYouTubeCardSurface(
    VideoSearchEntry videoResult,
    float width,
    float height,
    bool useThumbnailActions = false)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin +
            size,
            ImGui.GetColorU32(
                new Vector4(
                    CardBg.X,
                    CardBg.Y,
                    CardBg.Z,
                    0.72f)),
            Ui(10f));

        DrawHomeYouTubeCard(
            videoResult,
            width,
            height,
            compactHomeLayout: true,
            showBrowseFavouriteAction: true,
            useThumbnailActions: useThumbnailActions);

        var cardHovered =
            useThumbnailActions &&
            ImGui.GetMousePos().X >= origin.X &&
            ImGui.GetMousePos().X <= origin.X + size.X &&
            ImGui.GetMousePos().Y >= origin.Y &&
            ImGui.GetMousePos().Y <= origin.Y + size.Y;

        drawList.AddRect(
            origin,
            origin +
            size,
            ImGui.GetColorU32(
                cardHovered
                    ? new Vector4(
                        AccentHover.X,
                        AccentHover.Y,
                        AccentHover.Z,
                        0.82f)
                    : new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.20f)),
            Ui(10f),
            ImDrawFlags.None,
            Ui(1f));
    }

    private void DrawHomeYouTubeCard(
     VideoSearchEntry result,
     float width,
     float height,
     bool browseLayout = false,
     string? browseTopic = null,
     bool showBrowseFavouriteAction = false,
     bool compactHomeLayout = false,
     bool useThumbnailActions = false)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();

        ImGui.InvisibleButton(
            "##homeYoutubeCard",
            size);

        var hovered = ImGui.IsItemHovered();

        var current =
            queue.Current;

        var isNowPlaying =
            current is not null &&
            string.Equals(
                current.Url,
                result.Url,
                StringComparison.OrdinalIgnoreCase);

        var thumbnailHeight = Ui(116f);

        // ---------------------------------------------------------
        // Thumbnail
        // ---------------------------------------------------------

        var thumbnail =
            thumbnails.Get(result.ThumbnailUrl);

        if (thumbnail is not null)
        {
            drawList.AddImageRounded(
                thumbnail.Handle,
                origin,
                origin + new Vector2(
                    width,
                    thumbnailHeight),
                Vector2.Zero,
                Vector2.One,
                uint.MaxValue,
                9f);
        }
        else
        {
            drawList.AddRectFilled(
                origin,
                origin + new Vector2(
                    width,
                    thumbnailHeight),
                ImGui.GetColorU32(CardBg),
                9f);
        }

        // Slight darkening at thumbnail bottom helps the duration badge.
        drawList.AddRectFilled(
            origin + new Vector2(
                0f,
                thumbnailHeight - Ui(22f)),
            origin + new Vector2(
                width,
                thumbnailHeight),
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.18f)),
            0f);

        // ---------------------------------------------------------
        // Now Playing badge
        // ---------------------------------------------------------

        if (isNowPlaying)
        {
            const string badgeText =
                "NOW PLAYING";

            var badgeSize =
                ImGui.CalcTextSize(
                    badgeText);

            var badgeMin =
                origin +
                UiVec(7f, 6f);

            var badgeMax =
                badgeMin +
                new Vector2(
                    badgeSize.X + Ui(10f),
                    badgeSize.Y + Ui(5f));

            drawList.AddRectFilled(
                badgeMin,
                badgeMax,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.95f)),
                5f);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                badgeMin +
                UiVec(5f, 2f),
                ImGui.GetColorU32(
                    Vector4.One),
                badgeText);
        }

        // ---------------------------------------------------------
        // Upload date badge
        // ---------------------------------------------------------

        if (result.UploadDate is { } uploadDate)
        {
            var dateText =
                FormatRelativeUploadDate(uploadDate);

            var dateSize =
                ImGui.CalcTextSize(dateText);

            var badgeMin =
                origin +
                UiVec(7f, 6f);

            var badgeMax =
                badgeMin +
                new Vector2(
                    dateSize.X + Ui(10f),
                    dateSize.Y + Ui(5f));

            drawList.AddRectFilled(
                badgeMin,
                badgeMax,
                ImGui.GetColorU32(
                    new Vector4(
                        0f,
                        0f,
                        0f,
                        0.75f)),
                5f);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                badgeMin +
                UiVec(5f, 2f),
                ImGui.GetColorU32(Vector4.One),
                dateText);
        }

        // ---------------------------------------------------------
        // Duration
        // ---------------------------------------------------------

        if (result.Duration is { } duration)
        {
            var durationText =
                FormatTime(
                    (float)duration.TotalSeconds);

            var durationSize =
                ImGui.CalcTextSize(durationText);

            var badgeMin =
                new Vector2(
                    origin.X +
                    width -
                    durationSize.X -
                    Ui(10f),
                    origin.Y +
                    thumbnailHeight -
                    Ui(20f));

            var badgeMax =
                new Vector2(
                    origin.X +
                    width -
                    Ui(4f),
                    origin.Y +
                    thumbnailHeight -
                    Ui(4f));

            drawList.AddRectFilled(
                badgeMin,
                badgeMax,
                ImGui.GetColorU32(
                    new Vector4(
                        0f,
                        0f,
                        0f,
                        0.82f)),
                4f);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    badgeMin.X + Ui(3f),
                    badgeMin.Y +
                    ((badgeMax.Y - badgeMin.Y) - durationSize.Y) * 0.5f),
                ImGui.GetColorU32(Vector4.One),
                durationText);
        }

        // ---------------------------------------------------------
        // Browse topic badge
        // ---------------------------------------------------------

        var hasBrowseTopic =
            browseLayout &&
            !string.IsNullOrWhiteSpace(
                browseTopic);

        if (hasBrowseTopic)
        {
            var topicText =
                browseTopic!.Trim();

            var maximumTopicWidth =
                MathF.Max(
                    width *
                    0.56f,
                    Ui(55f));

            while (topicText.Length > 1 &&
                   ImGui.CalcTextSize(
                       topicText).X +
                   Ui(14f) >
                   maximumTopicWidth)
            {
                topicText =
                    topicText[..^1];
            }

            if (!string.Equals(
                    topicText,
                    browseTopic,
                    StringComparison.Ordinal))
            {
                topicText =
                    topicText.TrimEnd() +
                    "…";
            }

            var topicTextSize =
                ImGui.CalcTextSize(
                    topicText);

            var topicColour =
                GetBrowseTopicTagColour(
                    browseTopic!);

            var topicMin =
                new Vector2(
                    origin.X +
                    Ui(5f),
                    origin.Y +
                    thumbnailHeight -
                    Ui(24f));

            var topicMax =
                topicMin +
                new Vector2(
                    topicTextSize.X +
                    Ui(14f),
                    Ui(19f));

            drawList.AddRectFilled(
                topicMin,
                topicMax,
                ImGui.GetColorU32(
                    new Vector4(
                        topicColour.X,
                        topicColour.Y,
                        topicColour.Z,
                        0.94f)),
                Ui(5f));

            drawList.AddRect(
                topicMin,
                topicMax,
                ImGui.GetColorU32(
                    new Vector4(
                        1f,
                        1f,
                        1f,
                        0.24f)),
                Ui(5f),
                ImDrawFlags.None,
                Ui(1f));

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    topicMin.X +
                    Ui(7f),
                    topicMin.Y +
                    (Ui(19f) -
                     topicTextSize.Y) *
                    0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                topicText);
        }

        // ---------------------------------------------------------
        // Compact Home view-count badge
        // ---------------------------------------------------------

        if (compactHomeLayout &&
            result.ViewCount is { } compactViews)
        {
            var viewsText =
                FormatViewCount(
                    compactViews);

            var viewsTextSize =
                ImGui.CalcTextSize(
                    viewsText);

            var viewsMin =
                new Vector2(
                    origin.X +
                    Ui(5f),
                    origin.Y +
                    thumbnailHeight -
                    Ui(24f));

            var viewsMax =
                viewsMin +
                new Vector2(
                    viewsTextSize.X +
                    Ui(14f),
                    Ui(19f));

            drawList.AddRectFilled(
                viewsMin,
                viewsMax,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.82f)),
                Ui(5f));

            drawList.AddRect(
                viewsMin,
                viewsMax,
                ImGui.GetColorU32(
                    new Vector4(
                        AccentHover.X,
                        AccentHover.Y,
                        AccentHover.Z,
                        0.82f)),
                Ui(5f),
                ImDrawFlags.None,
                Ui(1f));

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    viewsMin.X +
                    Ui(7f),
                    viewsMin.Y +
                    (Ui(19f) -
                     viewsTextSize.Y) *
                    0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                viewsText);
        }

        // ---------------------------------------------------------
        // Live playback progress
        // ---------------------------------------------------------

        if (isNowPlaying)
        {
            var (position, playbackDuration, _) =
    video.GetProgress();

            if (playbackDuration > 0f)
            {
                var progress =
                    Math.Clamp(
                        position / playbackDuration,
                        0f,
                        1f);

                var progressHeight = Ui(3f);

                var progressY =
                    origin.Y +
                    thumbnailHeight -
                    progressHeight;

                // Remaining track.
                drawList.AddRectFilled(
                    new Vector2(
                        origin.X,
                        progressY),
                    new Vector2(
                        origin.X + width,
                        origin.Y + thumbnailHeight),
                    ImGui.GetColorU32(
                        new Vector4(
                            1f,
                            1f,
                            1f,
                            0.16f)));

                // Played portion.
                drawList.AddRectFilled(
                    new Vector2(
                        origin.X,
                        progressY),
                    new Vector2(
                        origin.X +
                        width * progress,
                        origin.Y + thumbnailHeight),
                    ImGui.GetColorU32(
                        Accent));
            }
        }


        // ---------------------------------------------------------
        // Text beneath thumbnail
        // ---------------------------------------------------------

        var textX = origin.X + 2f;
        var textWidth = MathF.Max(width - 4f, 40f);
        var lineHeight = ImGui.GetTextLineHeight();

        var titleY =
            origin.Y +
            thumbnailHeight +
            (compactHomeLayout
                ? Ui(7f)
                : Ui(10f));


        DrawWrappedLines(
            drawList,
            new Vector2(
                textX,
                titleY),
            textWidth,
            lineHeight,
            2,
            ImGui.GetColorU32(Vector4.One),
            result.Title);

        var channel =
         TruncateHomeMediaText(
             result.ChannelName,
             17);

        var metadataY =
            titleY +
            lineHeight *
            2f +
            (compactHomeLayout
                ? Ui(4f)
                : Ui(5f));



        var channelY =
    metadataY;

        var userIcon =
            FontAwesomeIcon.User.ToIconString();

        float iconWidth;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconWidth =
                ImGui.CalcTextSize(userIcon).X;

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    textX,
                    channelY),
                ImGui.GetColorU32(
                    new Vector4(
                        AccentHover.X,
                        AccentHover.Y,
                        AccentHover.Z,
                        0.78f)),
                userIcon);
        }

        // ---------------------------------------------------------
        // Plugin-managed YouTube subscription button
        //
        // Channel name is being used as the temporary identity.
        // Eventually this should use the actual YouTube channel ID.
        // ---------------------------------------------------------

        var isSubscribed =
            !string.IsNullOrWhiteSpace(result.ChannelId) &&
            Plugin.Cfg.SubscribedYouTubeChannelIds.Contains(
                result.ChannelId,
                StringComparer.OrdinalIgnoreCase);

        var subscribeButtonSize = Ui(24f);

        var subscribeMin =
            new Vector2(
                origin.X + width - subscribeButtonSize - Ui(2f),
                channelY - Ui(2f));

        var subscribeMax =
            subscribeMin +
            new Vector2(
                subscribeButtonSize,
                subscribeButtonSize);

        var subscribeMouse =
            ImGui.GetMousePos();

        var subscribeHovered =
            subscribeMouse.X >= subscribeMin.X &&
            subscribeMouse.X <= subscribeMax.X &&
            subscribeMouse.Y >= subscribeMin.Y &&
            subscribeMouse.Y <= subscribeMax.Y;

        // Leave enough room so a long channel name doesn't run
        // underneath the subscribe button.
        var channelTextMaxWidth =
            MathF.Max(
                subscribeMin.X -
                (textX + iconWidth + 5f) -
                7f,
                20f);

        var displayChannel = channel;

        while (displayChannel.Length > 1 &&
               ImGui.CalcTextSize(displayChannel).X >
               channelTextMaxWidth)
        {
            displayChannel =
                displayChannel[..^1];
        }

        if (!string.Equals(
                displayChannel,
                channel,
                StringComparison.Ordinal))
        {
            displayChannel =
                displayChannel.TrimEnd() + "…";
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                textX + iconWidth + Ui(5f),
                channelY),
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.82f)),
            displayChannel);

        var channelTextMin =
    new Vector2(
        textX + iconWidth + Ui(5f),
        channelY);

        var channelTextSize =
            ImGui.CalcTextSize(
                displayChannel);

        var channelTextMax =
            channelTextMin +
            channelTextSize;

        var channelHovered =
            ImGui.GetMousePos().X >= channelTextMin.X &&
            ImGui.GetMousePos().X <= channelTextMax.X &&
            ImGui.GetMousePos().Y >= channelTextMin.Y &&
            ImGui.GetMousePos().Y <= channelTextMax.Y;

        if (channelHovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);

            ImGui.SetTooltip(
                $"View {result.ChannelName}");

            if (ImGui.IsMouseClicked(
                    ImGuiMouseButton.Left))
            {
                if (!string.IsNullOrWhiteSpace(
                        result.ChannelId))
                {
                    OpenYouTubeChannel(
                        result.ChannelId,
                        result.ChannelName);
                }
            }
        }

        // ---------------------------------------------------------
        // Minimal subscribe button
        // ---------------------------------------------------------

        if (subscribeHovered || isSubscribed)
        {
            drawList.AddCircleFilled(
                subscribeMin +
                new Vector2(
                    subscribeButtonSize * 0.5f,
                    subscribeButtonSize * 0.5f),
                subscribeButtonSize * 0.5f,
                ImGui.GetColorU32(
                    isSubscribed
                        ? new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.22f)
                        : new Vector4(
                            1f,
                            1f,
                            1f,
                            0.08f)));
        }

        // ---------------------------------------------------------
        // + / check icon
        // ---------------------------------------------------------

        var subscribeIcon =
            isSubscribed
                ? FontAwesomeIcon.Check
                : FontAwesomeIcon.Plus;

        var subscribeGlyph =
            subscribeIcon.ToIconString();

        Vector2 subscribeGlyphSize;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            subscribeGlyphSize =
                ImGui.CalcTextSize(
                    subscribeGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                subscribeMin +
                new Vector2(
                    (subscribeButtonSize - subscribeGlyphSize.X) * 0.5f,
                    (subscribeButtonSize - subscribeGlyphSize.Y) * 0.5f),
                ImGui.GetColorU32(
                    isSubscribed
                        ? AccentHover
                        : subscribeHovered
                            ? Vector4.One
                            : MutedText),
                subscribeGlyph);
        }

 

        // ---------------------------------------------------------
        // Subscribe interaction
        // ---------------------------------------------------------

        if (subscribeHovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);

            ImGui.SetTooltip(
                isSubscribed
                    ? $"Unsubscribe from {result.ChannelName}"
                    : $"Subscribe to {result.ChannelName}");

            if (ImGui.IsMouseClicked(
                    ImGuiMouseButton.Left))
            {
                if (!string.IsNullOrWhiteSpace(
         result.ChannelId))
                {
                    if (isSubscribed)
                    {
                        Plugin.Cfg.SubscribedYouTubeChannelIds.RemoveAll(
                            id => string.Equals(
                                id,
                                result.ChannelId,
                                StringComparison.OrdinalIgnoreCase));

                        Plugin.Cfg.SubscribedYouTubeChannelNames.Remove(
                            result.ChannelId);
                    }
                    else
                    {
                        Plugin.Cfg.SubscribedYouTubeChannelIds.Add(
                            result.ChannelId);

                        Plugin.Cfg.SubscribedYouTubeChannelNames[
                            result.ChannelId] =
                            result.ChannelName;
                    }

                    Plugin.Cfg.Save();
                }
            }
        }

        if (!compactHomeLayout &&
            result.ViewCount is { } views)
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    textX,
                    channelY +
                    lineHeight +
                    Ui(3f)),
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.58f)),
                FormatViewCount(views));
        }

        // ---------------------------------------------------------
        // Hover overlay
        // ---------------------------------------------------------

        var actionClicked = false;

        var favouriteVideoId =
            GetYouTubeVideoId(result.Url);

        var isFavourite =
            favouriteVideoId is not null &&
            Plugin.Cfg.FavouriteYouTubeVideoIds.Contains(
                favouriteVideoId,
                StringComparer.OrdinalIgnoreCase);

        if (useThumbnailActions)
        {
            var thumbnailMaximum =
                origin +
                new Vector2(
                    width,
                    thumbnailHeight);

            var thumbnailCenter =
                origin +
                new Vector2(
                    width * 0.5f,
                    thumbnailHeight * 0.5f);

            var mouse =
                ImGui.GetMousePos();

            if (hovered)
            {
                drawList.AddRectFilled(
                    origin,
                    thumbnailMaximum,
                    ImGui.GetColorU32(
                        new Vector4(
                            0f,
                            0f,
                            0f,
                            0.46f)),
                    Ui(9f));

                drawList.AddRect(
                    origin,
                    thumbnailMaximum,
                    ImGui.GetColorU32(
                        new Vector4(
                            AccentHover.X,
                            AccentHover.Y,
                            AccentHover.Z,
                            0.76f)),
                    Ui(9f),
                    ImDrawFlags.None,
                    Ui(1f));

                var playRadius = Ui(24f);
                var queueRadius = Ui(20f);
                var controlGap = Ui(10f);
                var controlsWidth =
                    playRadius * 2f +
                    controlGap +
                    queueRadius * 2f;

                var playCenter =
                    new Vector2(
                        thumbnailCenter.X -
                        controlsWidth * 0.5f +
                        playRadius,
                        thumbnailCenter.Y);

                var queueCenter =
                    new Vector2(
                        playCenter.X +
                        playRadius +
                        controlGap +
                        queueRadius,
                        thumbnailCenter.Y);

                var playHovered =
                    Vector2.DistanceSquared(
                        mouse,
                        playCenter) <=
                    playRadius * playRadius;

                var queueHovered =
                    Vector2.DistanceSquared(
                        mouse,
                        queueCenter) <=
                    queueRadius * queueRadius;

                drawList.AddCircleFilled(
                    playCenter,
                    playRadius,
                    ImGui.GetColorU32(
                        playHovered
                            ? AccentHover
                            : Accent),
                    32);

                drawList.AddCircle(
                    playCenter,
                    playRadius,
                    ImGui.GetColorU32(
                        new Vector4(
                            1f,
                            1f,
                            1f,
                            0.30f)),
                    32,
                    Ui(1f));

                drawList.AddCircleFilled(
                    queueCenter,
                    queueRadius,
                    ImGui.GetColorU32(
                        queueHovered
                            ? CardBgHover
                            : new Vector4(
                                0.05f,
                                0.06f,
                                0.10f,
                                0.92f)),
                    32);

                drawList.AddCircle(
                    queueCenter,
                    queueRadius,
                    ImGui.GetColorU32(
                        queueHovered
                            ? AccentHover
                            : new Vector4(
                                1f,
                                1f,
                                1f,
                                0.30f)),
                    32,
                    Ui(1f));

                var playGlyph =
                    FontAwesomeIcon.Play.ToIconString();

                var queueGlyph =
                    FontAwesomeIcon.ListUl.ToIconString();

                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    var playGlyphSize =
                        ImGui.CalcTextSize(
                            playGlyph);

                    var queueGlyphSize =
                        ImGui.CalcTextSize(
                            queueGlyph);

                    drawList.AddText(
                        ImGui.GetFont(),
                        ImGui.GetFontSize(),
                        playCenter -
                        playGlyphSize * 0.5f,
                        ImGui.GetColorU32(Vector4.One),
                        playGlyph);

                    drawList.AddText(
                        ImGui.GetFont(),
                        ImGui.GetFontSize(),
                        queueCenter -
                        queueGlyphSize * 0.5f,
                        ImGui.GetColorU32(Vector4.One),
                        queueGlyph);
                }

                if (playHovered ||
                    queueHovered)
                {
                    ImGui.SetMouseCursor(
                        ImGuiMouseCursor.Hand);

                    ImGui.SetTooltip(
                        playHovered
                            ? "Play now"
                            : "Add to queue");
                }

                if (ImGui.IsMouseClicked(
                        ImGuiMouseButton.Left))
                {
                    if (playHovered)
                    {
                        actionClicked = true;

                        HandlePlayNow(
                            new VideoQueueEntry(
                                result.Url,
                                result.Title,
                                result.ChannelName,
                                result.Duration,
                                result.ThumbnailUrl));
                    }
                    else if (queueHovered)
                    {
                        actionClicked = true;

                        HandleAddToQueue(
                            new VideoQueueEntry(
                                result.Url,
                                result.Title,
                                result.ChannelName,
                                result.Duration,
                                result.ThumbnailUrl));

                        if (!ShouldUseViewerMediaActions)
                        {
                            queueAddedFeedbackUntil =
                                ImGui.GetTime() +
                                2.0;
                        }
                    }
                }
            }
            else
            {
                var playRadius = Ui(18f);

                drawList.AddCircleFilled(
                    thumbnailCenter,
                    playRadius,
                    ImGui.GetColorU32(
                        new Vector4(
                            0.03f,
                            0.04f,
                            0.07f,
                            0.66f)),
                    28);

                drawList.AddCircle(
                    thumbnailCenter,
                    playRadius,
                    ImGui.GetColorU32(
                        new Vector4(
                            1f,
                            1f,
                            1f,
                            0.32f)),
                    28,
                    Ui(1f));

                var playGlyph =
                    FontAwesomeIcon.Play.ToIconString();

                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    var playGlyphSize =
                        ImGui.CalcTextSize(
                            playGlyph);

                    drawList.AddText(
                        ImGui.GetFont(),
                        ImGui.GetFontSize(),
                        thumbnailCenter -
                        playGlyphSize * 0.5f,
                        ImGui.GetColorU32(
                            new Vector4(
                                1f,
                                1f,
                                1f,
                                0.92f)),
                        playGlyph);
                }
            }
        }

        if (hovered &&
            !browseLayout &&
            !compactHomeLayout)
        {
            drawList.AddRectFilled(
                origin,
                origin +
                new Vector2(
                    width,
                    thumbnailHeight),
                ImGui.GetColorU32(
                    new Vector4(
                        0f,
                        0f,
                        0f,
                        0.52f)),
                9f);

            drawList.AddRect(
                origin,
                origin +
                new Vector2(
                    width,
                    thumbnailHeight),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.70f)),
                9f,
                ImDrawFlags.None,
                1f);

            // -----------------------------------------------------
            // Favourite
            // -----------------------------------------------------

            var favouriteButtonSize = Ui(27f);

            var favouriteMin =
                new Vector2(
                    origin.X + width - favouriteButtonSize - Ui(7f),
                    origin.Y + Ui(7f));

            var favouriteMax =
                favouriteMin +
                new Vector2(
                    favouriteButtonSize,
                    favouriteButtonSize);

            var mouse =
                ImGui.GetMousePos();

            var favouriteHovered =
                mouse.X >= favouriteMin.X &&
                mouse.X <= favouriteMax.X &&
                mouse.Y >= favouriteMin.Y &&
                mouse.Y <= favouriteMax.Y;

            // Small dark floating button.
            drawList.AddCircleFilled(
                favouriteMin +
                new Vector2(
                    favouriteButtonSize * 0.5f,
                    favouriteButtonSize * 0.5f),
                favouriteButtonSize * 0.5f,
                ImGui.GetColorU32(
                    favouriteHovered
                        ? new Vector4(
                            0.10f,
                            0.12f,
                            0.17f,
                            0.96f)
                        : new Vector4(
                            0.05f,
                            0.06f,
                            0.09f,
                            0.86f)));

            // Subtle border.
            drawList.AddCircle(
                favouriteMin +
                new Vector2(
                    favouriteButtonSize * 0.5f,
                    favouriteButtonSize * 0.5f),
                favouriteButtonSize * 0.5f,
                ImGui.GetColorU32(
                    favouriteHovered
                        ? AccentHover
                        : new Vector4(
                            1f,
                            1f,
                            1f,
                            0.28f)),
                24,
                1f);

            // Heart icon.
            var favouriteIcon =
                isFavourite
                    ? FontAwesomeIcon.Heart
                    : FontAwesomeIcon.Heart;

            var favouriteGlyph =
                favouriteIcon.ToIconString();

            Vector2 favouriteGlyphSize;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                favouriteGlyphSize =
                    ImGui.CalcTextSize(favouriteGlyph);

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    favouriteMin +
                    new Vector2(
                        (favouriteButtonSize - favouriteGlyphSize.X) * 0.5f,
                        (favouriteButtonSize - favouriteGlyphSize.Y) * 0.5f),
                    ImGui.GetColorU32(
                        isFavourite
                            ? AccentHover
                            : favouriteHovered
                                ? Vector4.One
                                : new Vector4(
                                    1f,
                                    1f,
                                    1f,
                                    0.82f)),
                    favouriteGlyph);
            }

            if (favouriteHovered)
            {
                ImGui.SetMouseCursor(
                    ImGuiMouseCursor.Hand);
            }

            var buttonGap = Ui(6f);
            var buttonHeight = Ui(28f);

            var availableButtonWidth =
                MathF.Max(width - 16f, 80f);

            var playWidth =
                availableButtonWidth * 0.48f;

            var queueWidth =
                availableButtonWidth -
                playWidth -
                buttonGap;

            var buttonY =
                origin.Y +
                thumbnailHeight -
                buttonHeight -
                8f;

            var playMin =
                new Vector2(
                    origin.X + Ui(8f),
                    buttonY);

            var playMax =
                playMin +
                new Vector2(
                    playWidth,
                    buttonHeight);

            var queueMin =
                new Vector2(
                    playMax.X + buttonGap,
                    buttonY);

            var queueMax =
                queueMin +
                new Vector2(
                    queueWidth,
                    buttonHeight);

            var playHovered =
                mouse.X >= playMin.X &&
                mouse.X <= playMax.X &&
                mouse.Y >= playMin.Y &&
                mouse.Y <= playMax.Y;

            var queueHovered =
                mouse.X >= queueMin.X &&
                mouse.X <= queueMax.X &&
                mouse.Y >= queueMin.Y &&
                mouse.Y <= queueMax.Y;

            // -----------------------------------------------------
            // Play
            // -----------------------------------------------------

            drawList.AddRectFilled(
                playMin,
                playMax,
                ImGui.GetColorU32(
                    playHovered
                        ? AccentHover
                        : Accent),
                6f);

            const string playLabel = "Play";

            var playLabelSize =
                ImGui.CalcTextSize(playLabel);

            Vector2 playGlyphSize;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                playGlyphSize =
                    ImGui.CalcTextSize(
                        FontAwesomeIcon.Play.ToIconString());
            }

            var playTotalWidth =
                playGlyphSize.X +
                6f +
                playLabelSize.X;

            var playStartX =
                playMin.X +
                (playWidth - playTotalWidth) * 0.5f;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    new Vector2(
                        playStartX,
                        playMin.Y +
                        (buttonHeight - playGlyphSize.Y) * 0.5f),
                    ImGui.GetColorU32(Vector4.One),
                    FontAwesomeIcon.Play.ToIconString());
            }

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    playStartX +
                    playGlyphSize.X +
                    Ui(6f),
                    playMin.Y +
                    (buttonHeight - playLabelSize.Y) * 0.5f),
                ImGui.GetColorU32(Vector4.One),
                playLabel);

            // -----------------------------------------------------
            // Queue
            // -----------------------------------------------------

            drawList.AddRectFilled(
                queueMin,
                queueMax,
                ImGui.GetColorU32(
                    queueHovered
                        ? CardBgHover
                        : CardBg),
                6f);

            drawList.AddRect(
                queueMin,
                queueMax,
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.25f)),
                6f,
                ImDrawFlags.None,
                1f);

            const string queueLabel = "Queue";

            var queueLabelSize =
                ImGui.CalcTextSize(queueLabel);

            Vector2 queueGlyphSize;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                queueGlyphSize =
                    ImGui.CalcTextSize(
                        FontAwesomeIcon.Plus.ToIconString());
            }

            var queueTotalWidth =
                queueGlyphSize.X +
                6f +
                queueLabelSize.X;

            var queueStartX =
                queueMin.X +
                (queueWidth - queueTotalWidth) * 0.5f;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    new Vector2(
                        queueStartX,
                        queueMin.Y +
                        (buttonHeight - queueGlyphSize.Y) * 0.5f),
                    ImGui.GetColorU32(Vector4.One),
                    FontAwesomeIcon.Plus.ToIconString());
            }

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    queueStartX +
                    queueGlyphSize.X +
                    Ui(6f),
                    queueMin.Y +
                    (buttonHeight - queueLabelSize.Y) * 0.5f),
                ImGui.GetColorU32(Vector4.One),
                queueLabel);

            // -----------------------------------------------------
            // Manual click handling
            // -----------------------------------------------------

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                // Favourite
                if (favouriteHovered)
                {
                    actionClicked = true;

                    if (favouriteVideoId is not null)
                    {
                        var isNowFavourite =
         !isFavourite;

                        if (isFavourite)
                        {
                            Plugin.Cfg.FavouriteYouTubeVideoIds.RemoveAll(
                                id =>
                                    string.Equals(
                                        id,
                                        favouriteVideoId,
                                        StringComparison.OrdinalIgnoreCase));
                        }
                        else
                        {
                            Plugin.Cfg.FavouriteYouTubeVideoIds.RemoveAll(
                                id =>
                                    string.Equals(
                                        id,
                                        favouriteVideoId,
                                        StringComparison.OrdinalIgnoreCase));

                            Plugin.Cfg.FavouriteYouTubeVideoIds.Insert(
                                0,
                                favouriteVideoId);
                        }

                        UpdateFavouriteVideoCacheAfterToggle(
                            result,
                            isNowFavourite);
                    }
                }

                // Play
                else if (playHovered)
                {
                    actionClicked = true;

                    HandlePlayNow(
                        new VideoQueueEntry(
                            result.Url,
                            result.Title,
                            result.ChannelName,
                            result.Duration,
                            result.ThumbnailUrl));
                }

                // Queue
                else if (queueHovered)
                {
                    actionClicked = true;

                    HandleAddToQueue(
                        new VideoQueueEntry(
                            result.Url,
                            result.Title,
                            result.ChannelName,
                            result.Duration,
                            result.ThumbnailUrl));

                    if (!ShouldUseViewerMediaActions)
                    {
                        queueAddedFeedbackUntil =
                            ImGui.GetTime() + 2.0;
                    }
                }
            }
        }

        if ((browseLayout ||
             compactHomeLayout) &&
            !useThumbnailActions)
        {
            var footerHeight =
                compactHomeLayout
                    ? Ui(30f)
                    : Ui(34f);

            var footerGap =
                compactHomeLayout
                    ? Ui(6f)
                    : Ui(7f);

            var footerPadding =
                compactHomeLayout
                    ? Ui(6f)
                    : Ui(8f);

            var footerTop =
                origin.Y +
                height -
                footerPadding -
                footerHeight;

            var availableFooterWidth =
                width -
                footerPadding *
                2f;

            var playButtonWidth =
                (availableFooterWidth -
                 footerGap) *
                0.5f;

            var queueButtonWidth =
                availableFooterWidth -
                footerGap -
                playButtonWidth;

            var playMin =
                new Vector2(
                    origin.X +
                    footerPadding,
                    footerTop);

            var playMax =
                playMin +
                new Vector2(
                    playButtonWidth,
                    footerHeight);

            var queueMin =
                new Vector2(
                    playMax.X +
                    footerGap,
                    footerTop);

            var queueMax =
                queueMin +
                new Vector2(
                    queueButtonWidth,
                    footerHeight);

            var mouse =
                ImGui.GetMousePos();

            var browsePlayHovered =
                mouse.X >=
                playMin.X &&
                mouse.X <=
                playMax.X &&
                mouse.Y >=
                playMin.Y &&
                mouse.Y <=
                playMax.Y;

            var browseQueueHovered =
                mouse.X >=
                queueMin.X &&
                mouse.X <=
                queueMax.X &&
                mouse.Y >=
                queueMin.Y &&
                mouse.Y <=
                queueMax.Y;

            DrawBrowseCardActionButton(
                drawList,
                playMin,
                playMax,
                FontAwesomeIcon.Play,
                "Play",
                browsePlayHovered,
                true);

            DrawBrowseCardActionButton(
                drawList,
                queueMin,
                queueMax,
                FontAwesomeIcon.Plus,
                "Queue",
                browseQueueHovered,
                false);

            if (browsePlayHovered ||
                browseQueueHovered)
            {
                ImGui.SetMouseCursor(
                    ImGuiMouseCursor.Hand);
            }

            if (ImGui.IsMouseClicked(
                    ImGuiMouseButton.Left))
            {
                if (browsePlayHovered)
                {
                    actionClicked =
                        true;

                    HandlePlayNow(
                        new VideoQueueEntry(
                            result.Url,
                            result.Title,
                            result.ChannelName,
                            result.Duration,
                            result.ThumbnailUrl));
                }
                else if (browseQueueHovered)
                {
                    actionClicked =
                        true;

                    HandleAddToQueue(
                        new VideoQueueEntry(
                            result.Url,
                            result.Title,
                            result.ChannelName,
                            result.Duration,
                            result.ThumbnailUrl));

                    if (!ShouldUseViewerMediaActions)
                    {
                        queueAddedFeedbackUntil =
                            ImGui.GetTime() +
                            2.0;
                    }
                }
            }
        }

        if ((browseLayout ||
      compactHomeLayout) &&
     showBrowseFavouriteAction &&
     favouriteVideoId is not null)
        {
            var favouriteButtonSize =
                Ui(28f);

            var favouriteMin =
                new Vector2(
                    origin.X +
                    width -
                    favouriteButtonSize -
                    Ui(7f),
                    origin.Y +
                    Ui(7f));

            var favouriteMax =
                favouriteMin +
                new Vector2(
                    favouriteButtonSize,
                    favouriteButtonSize);

            var mouse =
                ImGui.GetMousePos();

            var browseFavouriteHovered =
                mouse.X >=
                favouriteMin.X &&
                mouse.X <=
                favouriteMax.X &&
                mouse.Y >=
                favouriteMin.Y &&
                mouse.Y <=
                favouriteMax.Y;

            drawList.AddCircleFilled(
                favouriteMin +
                new Vector2(
                    favouriteButtonSize *
                    0.5f,
                    favouriteButtonSize *
                    0.5f),
                favouriteButtonSize *
                0.5f,
                ImGui.GetColorU32(
                    browseFavouriteHovered
                        ? new Vector4(
                            0.12f,
                            0.14f,
                            0.20f,
                            0.98f)
                        : new Vector4(
                            0.04f,
                            0.05f,
                            0.08f,
                            0.88f)));

            var heartGlyph =
                FontAwesomeIcon.Heart.ToIconString();

            Vector2 heartSize;

            using (
                ImRaii.PushFont(
                    UiBuilder.IconFont))
            {
                heartSize =
                    ImGui.CalcTextSize(
                        heartGlyph);

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    favouriteMin +
                    new Vector2(
                        (favouriteButtonSize -
                         heartSize.X) *
                        0.5f,
                        (favouriteButtonSize -
                         heartSize.Y) *
                        0.5f),
                    ImGui.GetColorU32(
                        isFavourite
                            ? AccentHover
                            : Vector4.One),
                    heartGlyph);
            }

            if (browseFavouriteHovered)
            {
                ImGui.SetMouseCursor(
                    ImGuiMouseCursor.Hand);

                ImGui.SetTooltip(
                    isFavourite
                        ? "Remove from favourites"
                        : "Add to favourites");

                if (ImGui.IsMouseClicked(
                        ImGuiMouseButton.Left))
                {
                    actionClicked =
                        true;

                    var isNowFavourite =
                        !isFavourite;

                    Plugin.Cfg.FavouriteYouTubeVideoIds.RemoveAll(
                        id =>
                            string.Equals(
                                id,
                                favouriteVideoId,
                                StringComparison.OrdinalIgnoreCase));

                    if (isNowFavourite)
                    {
                        Plugin.Cfg.FavouriteYouTubeVideoIds.Insert(
                            0,
                            favouriteVideoId);
                    }

                    UpdateFavouriteVideoCacheAfterToggle(
                        result,
                        isNowFavourite);
                }
            }
        }

        // ---------------------------------------------------------
        // Clicking elsewhere on the card = Play
        // ---------------------------------------------------------

        if (!actionClicked &&
            !subscribeHovered &&
            !channelHovered &&
            hovered &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var mouse =
                ImGui.GetMousePos();

            // Don't treat the bottom overlay-control strip as
            // a generic card click.
            var actionAreaTop =
                origin.Y +
                thumbnailHeight -
                36f;

            if (useThumbnailActions ||
                mouse.Y < actionAreaTop ||
                mouse.Y > origin.Y + thumbnailHeight)
            {
                HandlePlayNow(
                    new VideoQueueEntry(
                        result.Url,
                        result.Title,
                        result.ChannelName,
                        result.Duration,
                        result.ThumbnailUrl));
            }
        }
    }


    private void DrawBrowseCardActionButton(
    ImDrawListPtr drawList,
    Vector2 minimum,
    Vector2 maximum,
    FontAwesomeIcon icon,
    string label,
    bool hovered,
    bool primary)
    {
        var background =
            primary
                ? hovered
                    ? AccentHover
                    : Accent
                : hovered
                    ? CardBgHover
                    : new Vector4(
                        0.055f,
                        0.07f,
                        0.115f,
                        1f);

        drawList.AddRectFilled(
            minimum,
            maximum,
            ImGui.GetColorU32(
                background),
            Ui(7f));

        if (!primary)
        {
            drawList.AddRect(
                minimum,
                maximum,
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        hovered
                            ? 0.46f
                            : 0.26f)),
                Ui(7f),
                ImDrawFlags.None,
                Ui(1f));
        }

        var iconText =
            icon.ToIconString();

        Vector2 iconSize;

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            iconSize =
                ImGui.CalcTextSize(
                    iconText);
        }

        var labelSize =
            ImGui.CalcTextSize(
                label);

        var contentGap =
            Ui(7f);

        var contentWidth =
            iconSize.X +
            contentGap +
            labelSize.X;

        var buttonWidth =
            maximum.X -
            minimum.X;

        var buttonHeight =
            maximum.Y -
            minimum.Y;

        var contentX =
            minimum.X +
            (buttonWidth -
             contentWidth) *
            0.5f;

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    contentX,
                    minimum.Y +
                    (buttonHeight -
                     iconSize.Y) *
                    0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                iconText);
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                contentX +
                iconSize.X +
                contentGap,
                minimum.Y +
                (buttonHeight -
                 labelSize.Y) *
                0.5f),
            ImGui.GetColorU32(
                primary
                    ? Vector4.One
                    : new Vector4(
                        0.88f,
                        0.90f,
                        0.96f,
                        1f)),
            label);
    }



    private static string TruncateHomeMediaText(
    string text,
    int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(0, maxLength - 1)] + "…";
    }

    private void StartFeaturedTransition(
     int nextIndex,
     int direction = 1)
    {
        if (featuredTransitioning ||
            nextIndex == featuredSlideIndex ||
            nextIndex < 0 ||
            nextIndex >= FeaturedSlides.Length)
        {
            return;
        }

        featuredNextSlideIndex =
            nextIndex;

        featuredTransitionDirection =
            direction >= 0 ? 1 : -1;

        featuredTransitionStartedAt =
            ImGui.GetTime();

        featuredTransitioning = true;
    }

    private void DrawMediaHubFeatured()
    {
        var height = Ui(260f);
        var rounding = Ui(14f);

        const double holdDuration = 5.5;
        const float transitionDuration = 0.85f;

        var origin =
            ImGui.GetCursorScreenPos();

        var width =
            ImGui.GetContentRegionAvail().X;

        var size =
            new Vector2(
                width,
                height);

        var drawList =
            ImGui.GetWindowDrawList();

        var now =
            ImGui.GetTime();

        // ---------------------------------------------------------
        // Initialise carousel timing
        // ---------------------------------------------------------

        if (featuredSlideSettledAt < 0d)
        {
            featuredSlideSettledAt = now;
        }

        // ---------------------------------------------------------
        // Banner hover
        // ---------------------------------------------------------

        var mouse =
            ImGui.GetMousePos();

        var bannerHovered =
            mouse.X >= origin.X &&
            mouse.X <= origin.X + width &&
            mouse.Y >= origin.Y &&
            mouse.Y <= origin.Y + height;

        // Pause the automatic timer while the user is interacting
        // with the hero.
        if (bannerHovered &&
            !featuredTransitioning)
        {
            featuredSlideSettledAt = now;
        }

        // ---------------------------------------------------------
        // Automatic rotation
        // ---------------------------------------------------------

        if (!featuredTransitioning &&
            !bannerHovered &&
            now - featuredSlideSettledAt >= holdDuration)
        {
            StartFeaturedTransition(
                (featuredSlideIndex + 1) %
                FeaturedSlides.Length);
        }

        // ---------------------------------------------------------
        // Transition progress
        // ---------------------------------------------------------

        var transitionProgress = 1f;

        if (featuredTransitioning)
        {
            transitionProgress =
                Math.Clamp(
                    (float)(
                        (now - featuredTransitionStartedAt) /
                        transitionDuration),
                    0f,
                    1f);

            if (transitionProgress >= 1f)
            {
                featuredSlideIndex =
                    featuredNextSlideIndex;

                featuredTransitioning = false;

                featuredSlideSettledAt =
                    now;

                transitionProgress = 1f;
            }
        }

        // Smoothstep:
        // softer start + softer landing than a linear slide.
        var eased =
            transitionProgress < 0.5f
                ? 4f *
                  transitionProgress *
                  transitionProgress *
                  transitionProgress
                : 1f -
                  MathF.Pow(
                      -2f * transitionProgress + 2f,
                      3f) / 2f;

        // ---------------------------------------------------------
        // Base panel
        // ---------------------------------------------------------

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(CardBg),
            rounding);

        // Everything belonging to the carousel is clipped
        // to the hero rectangle while it moves.
        drawList.PushClipRect(
            origin,
            origin + size,
            true);

        if (featuredTransitioning)
        {
            var outgoingOffset =
                -width *
                eased *
                featuredTransitionDirection;

            var incomingOffset =
                width *
                (1f - eased) *
                featuredTransitionDirection;

            DrawFeaturedSlide(
                FeaturedSlides[featuredSlideIndex],
                featuredSlideResults[
                    featuredSlideIndex],
                origin + new Vector2(
                    outgoingOffset,
                    0f),
                size,
                rounding,
                1f - (0.20f * eased),
                false);

            DrawFeaturedSlide(
                FeaturedSlides[featuredNextSlideIndex],
                featuredSlideResults[
                    featuredNextSlideIndex],
                origin + new Vector2(
                    incomingOffset,
                    0f),
                size,
                rounding,
                0.65f +
                (0.35f * eased),
                false);
        }
        else
        {
            DrawFeaturedSlide(
                FeaturedSlides[featuredSlideIndex],
                featuredSlideResults[
                    featuredSlideIndex],
                origin,
                size,
                rounding,
                1f,
                true);
        }

        drawList.PopClipRect();

        // ---------------------------------------------------------
        // Border always stays fixed
        // ---------------------------------------------------------

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.28f)),
            rounding,
            ImDrawFlags.None,
            1f);

        // ---------------------------------------------------------
        // Carousel indicators
        // ---------------------------------------------------------

        var dotY =
            origin.Y +
            height -
            20f;

        var dotGap = Ui(15f);
        var dotRadius = Ui(3.5f);
        var activePillWidth = Ui(13f);

        var totalIndicatorWidth =
            activePillWidth +
            dotGap * 3f;

        var indicatorStartX =
            origin.X +
            width -
            totalIndicatorWidth -
            16f;

        for (var dot = 0;
     dot < FeaturedSlides.Length;
     dot++)
        {
            var displayedIndex =
                featuredTransitioning
                    ? featuredNextSlideIndex
                    : featuredSlideIndex;

            var isActive =
                dot == displayedIndex;

            var x =
                indicatorStartX +
                dot * dotGap;

            var dotCenter =
                new Vector2(
                    x,
                    dotY);

            // Slightly generous hit area so the tiny dots
            // don't feel fiddly to click.
            var hitMin =
                dotCenter -
                UiVec(7f, 7f);

            var hitMax =
                dotCenter +
                UiVec(7f, 7f);

            var dotHovered =
                !featuredTransitioning &&
                mouse.X >= hitMin.X &&
                mouse.X <= hitMax.X &&
                mouse.Y >= hitMin.Y &&
                mouse.Y <= hitMax.Y;

            if (dotHovered)
            {
                ImGui.SetMouseCursor(
                    ImGuiMouseCursor.Hand);

                if (ImGui.IsMouseClicked(
                        ImGuiMouseButton.Left))
                {
                    var direction =
                        dot > featuredSlideIndex
                            ? 1
                            : -1;

                    StartFeaturedTransition(
                        dot,
                        direction);
                }
            }

            if (isActive)
            {
                drawList.AddRectFilled(
                    new Vector2(
                        x - activePillWidth * 0.5f,
                        dotY - Ui(2.5f)),
                    new Vector2(
                        x + activePillWidth * 0.5f,
                        dotY + Ui(2.5f)),
                    ImGui.GetColorU32(
                        AccentHover),
                    3f);
            }
            else
            {
                var dotColor =
                    dotHovered
                        ? new Vector4(
                            AccentHover.X,
                            AccentHover.Y,
                            AccentHover.Z,
                            0.78f)
                        : new Vector4(
                            MutedText.X,
                            MutedText.Y,
                            MutedText.Z,
                            0.42f);

                drawList.AddCircleFilled(
                    dotCenter,
                    dotHovered
                        ? dotRadius + 0.75f
                        : dotRadius,
                    ImGui.GetColorU32(
                        dotColor));
            }
        }

        // Claim the hero's layout space exactly once.
        ImGui.SetCursorScreenPos(origin);

        ImGui.Dummy(size);
    }

    private void DrawFeaturedSlide(
        FeaturedSlide slide,
        VideoSearchEntry? videoResult,
        Vector2 origin,
        Vector2 size,
        float rounding,
        float contentAlpha,
        bool interactive)
    {
        var drawList =
            ImGui.GetWindowDrawList();

        var width =
            size.X;

        var height =
            size.Y;

        var videoTitle =
    videoResult?.Title ??
    "Loading featured video...";

        var channelName =
            videoResult?.ChannelName ??
            "YouTube";

        var thumbnailUrl =
            videoResult?.ThumbnailUrl ??
            $"https://i.ytimg.com/vi/{slide.VideoId}/maxresdefault.jpg";

        var viewText =
            videoResult?.ViewCount is { } views
                ? FormatViewCount(views)
                : "Loading...";

        // ---------------------------------------------------------
        // Background image
        // ---------------------------------------------------------

        var featuredThumbnail =
            thumbnails.Get(
                thumbnailUrl);

        if (featuredThumbnail is not null)
        {
            var (uv0, uv1) =
                CoverUvs(
                    featuredThumbnail.Width,
                    featuredThumbnail.Height,
                    width,
                    height);

            drawList.AddImageRounded(
                featuredThumbnail.Handle,
                origin,
                origin + size,
                uv0,
                uv1,
                uint.MaxValue,
                rounding);
        }
        else if (homeHero is { } fallback)
        {
            var (uv0, uv1) =
                CoverUvs(
                    fallback.Width,
                    fallback.Height,
                    width,
                    height);

            drawList.AddImageRounded(
                fallback.Handle,
                origin,
                origin + size,
                uv0,
                uv1,
                uint.MaxValue,
                rounding);
        }

        // ---------------------------------------------------------
        // Readability gradient
        // ---------------------------------------------------------

        drawList.AddRectFilledMultiColor(
    origin,
    origin + size,
    ImGui.GetColorU32(
        new Vector4(
            0.015f,
            0.025f,
            0.055f,
            1.0f)),
    ImGui.GetColorU32(
        new Vector4(
            0.015f,
            0.025f,
            0.055f,
            0.25f)),
    ImGui.GetColorU32(
        new Vector4(
            0.015f,
            0.025f,
            0.055f,
            0.25f)),
    ImGui.GetColorU32(
        new Vector4(
            0.015f,
            0.025f,
            0.055f,
            0.65f)));

        // ---------------------------------------------------------
        // Small secondary content motion
        // ---------------------------------------------------------

        var contentOffset =
            (1f - contentAlpha) * 6f;

        var textX =
            origin.X +
            24f +
            contentOffset;

        uint WithAlpha(
            Vector4 color,
            float alpha)
        {
            return ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    color.W *
                    Math.Clamp(
                        alpha,
                        0f,
                        1f)));
        }
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
    new Vector2(
        textX,
        origin.Y + Ui(35f)),
    WithAlpha(
        AccentHover,
        contentAlpha),
    "FEATURED");
        // ---------------------------------------------------------
        // Eyebrow
        // ---------------------------------------------------------

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                textX,
               origin.Y + Ui(55f)),
            WithAlpha(
                AccentHover,
                contentAlpha),
            viewText);

        // ---------------------------------------------------------
        // Title
        // ---------------------------------------------------------

        var savedCursor =
            ImGui.GetCursorScreenPos();

        SetUiFontScale(1.55f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                textX,
               origin.Y + Ui(95f)));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.Alpha,
            contentAlpha))
        {
            ImGui.PushTextWrapPos(
      textX + 430f);

            ImGui.TextWrapped(
                videoTitle);

            ImGui.PopTextWrapPos();
        }

        SetUiFontScale(1f);

        // ---------------------------------------------------------
        // Channel / category
        // ---------------------------------------------------------

        ImGui.SetCursorScreenPos(
            new Vector2(
                textX,
                origin.Y + Ui(165f)));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.Alpha,
            contentAlpha))
        {
            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    AccentHover,
                    FontAwesomeIcon.PlayCircle
                        .ToIconString());
            }

            ImGui.SameLine(0f, 7f);

            ImGui.TextColored(
          Vector4.One,
          channelName);
        }

        ImGui.SetCursorScreenPos(
            savedCursor);

        // ---------------------------------------------------------
        // Buttons
        // ---------------------------------------------------------

        var buttonY =
            origin.Y +
            height -
            58f;

        var watchMin =
            new Vector2(
                textX,
                buttonY);

        var watchMax =
            watchMin +
            UiVec(118f, 36f);

        var togetherMin =
            new Vector2(
                watchMax.X + Ui(8f),
                buttonY);

        var togetherMax =
            togetherMin +
            UiVec(142f, 36f);

        var mouse =
            ImGui.GetMousePos();

        var watchHovered =
            interactive &&
            mouse.X >= watchMin.X &&
            mouse.X <= watchMax.X &&
            mouse.Y >= watchMin.Y &&
            mouse.Y <= watchMax.Y;

        var togetherHovered =
            interactive &&
            mouse.X >= togetherMin.X &&
            mouse.X <= togetherMax.X &&
            mouse.Y >= togetherMin.Y &&
            mouse.Y <= togetherMax.Y;

        drawList.AddRectFilled(
            watchMin,
            watchMax,
            WithAlpha(
                watchHovered
                    ? AccentHover
                    : Accent,
                contentAlpha),
            8f);

        drawList.AddRectFilled(
            togetherMin,
            togetherMax,
            WithAlpha(
                togetherHovered
                    ? CardBgHover
                    : new Vector4(
                        CardBgHover.X,
                        CardBgHover.Y,
                        CardBgHover.Z,
                        0.92f),
                contentAlpha),
            8f);

        // ---------------------------------------------------------
        // Watch Now label
        // ---------------------------------------------------------

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                watchMin +
                UiVec(15f, 10f),
                WithAlpha(
                    Vector4.One,
                    contentAlpha),
                FontAwesomeIcon.Play
                    .ToIconString());
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            watchMin +
            UiVec(38f, 9f),
            WithAlpha(
                Vector4.One,
                contentAlpha),
            "Watch Now");

        // ---------------------------------------------------------
        // Watch Together label
        // ---------------------------------------------------------

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                togetherMin +
                UiVec(14f, 10f),
                WithAlpha(
                    Vector4.One,
                    contentAlpha),
                FontAwesomeIcon.UserFriends
                    .ToIconString());
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            togetherMin +
            UiVec(38f, 9f),
            WithAlpha(
                Vector4.One,
                contentAlpha),
            "Watch Together");

        // ---------------------------------------------------------
        // Interaction only on settled slide
        // ---------------------------------------------------------

        if (!interactive)
        {
            return;
        }

        if (watchHovered ||
            togetherHovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);
        }

        if (!ImGui.IsMouseClicked(
         ImGuiMouseButton.Left))
        {
            return;
        }

        if (!watchHovered &&
            !togetherHovered)
        {
            return;
        }

        if (videoResult is null)
        {
            return;
        }

        queue.PlayNow(
            new VideoQueueEntry(
                videoResult.Url,
                videoResult.Title,
                videoResult.ChannelName,
                videoResult.Duration,
                videoResult.ThumbnailUrl));

        video.Pause(false);
    }

    private void DrawMediaHubShelf(
    string title,
    int itemCount,
    float cardHeight)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var drawList = ImGui.GetWindowDrawList();

        // ---------------------------------------------------------
        // Heading
        // ---------------------------------------------------------

        SetUiFontScale(1.08f);
        ImGui.TextUnformatted(title);
        SetUiFontScale(1f);

        var seeAll = "See all  >";
        var seeAllWidth = ImGui.CalcTextSize(seeAll).X;

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            ImGui.GetWindowContentRegionMax().X -
            seeAllWidth);

        ImGui.TextColored(
            AccentHover,
            seeAll);

        ImGui.Dummy(new Vector2(0f, Ui(5f)));

        // ---------------------------------------------------------
        // Placeholder cards
        // ---------------------------------------------------------

        var gap = Ui(10f);

        var cardWidth =
            (width - gap * (itemCount - 1)) /
            itemCount;

        for (var index = 0;
             index < itemCount;
             index++)
        {
            if (index > 0)
            {
                ImGui.SameLine(0f, gap);
            }

            ImGui.PushID(index);
            DrawMediaHubPlaceholderCard(
                cardWidth,
                cardHeight);
            ImGui.PopID();
        }
    }

    private void DrawMediaHubPlaceholderCard(
    float width,
    float height)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();

        ImGui.InvisibleButton(
            "##mediaCard",
            size);

        var hovered = ImGui.IsItemHovered();

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                hovered
                    ? CardBgHover
                    : CardBg),
            10f);

        // Temporary thumbnail area.
        var thumbnailHeight =
            MathF.Max(height * 0.62f, 44f);

        drawList.AddRectFilled(
            origin,
            origin + new Vector2(
                width,
                thumbnailHeight),
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    hovered ? 0.14f : 0.075f)),
            10f,
            ImDrawFlags.RoundCornersTop);

        // Placeholder title / metadata lines.
        drawList.AddRectFilled(
            origin + new Vector2(Ui(10f), thumbnailHeight + Ui(9f)),
            origin + new Vector2(
                width * 0.72f,
                thumbnailHeight + Ui(13f)),
            ImGui.GetColorU32(
                new Vector4(1f, 1f, 1f, 0.34f)),
            2f);

        if (height >= 90f)
        {
            drawList.AddRectFilled(
                origin + new Vector2(Ui(10f), thumbnailHeight + Ui(21f)),
                origin + new Vector2(
                    width * 0.48f,
                    thumbnailHeight + Ui(24f)),
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.30f)),
                2f);
        }

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    1f,
                    1f,
                    1f,
                    hovered ? 0.10f : 0.045f)),
            10f,
            ImDrawFlags.None,
            1f);
    }

    private void DrawFfxivYouTubeShelf()
    {
        const string title = "FFXIV on YouTube";

        var columns =
            GetHomeVideoColumnCount(
                ImGui.GetWindowSize().X);

        var gap = Ui(12f);
        var cardHeight = Ui(188f);

        var width =
            ImGui.GetContentRegionAvail().X;

        // ---------------------------------------------------------
        // Heading
        // ---------------------------------------------------------

        DrawHomeShelfHeading(
            FontAwesomeIcon.PlayCircle,
            title,
            AccentHover,
            false,
            false);

        ImGui.SameLine();

        ImGui.PushID("ffxivYoutubeHide");

        ImGui.SetCursorPosX(
            ImGui.GetWindowContentRegionMax().X -
            HomeContentRightInset -
            ImGui.CalcTextSize("Hide this section").X -
            30f);

        var hideStart =
            ImGui.GetCursorScreenPos();

        Vector2 hideIconSize;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            hideIconSize =
                ImGui.CalcTextSize(
                    FontAwesomeIcon.Eye.ToIconString());
        }

        var hideTextSize =
            ImGui.CalcTextSize(
                "Hide this section");

        var hideTotalWidth =
            hideIconSize.X +
            6f +
            hideTextSize.X;

        var hideMin =
            hideStart;

        var hideMax =
            hideStart +
            new Vector2(
                hideTotalWidth,
                MathF.Max(
                    hideIconSize.Y,
                    hideTextSize.Y) + Ui(4f));

        var hideHovered =
            ImGui.IsMouseHoveringRect(
                hideMin,
                hideMax);

        var hideColor =
            hideHovered
                ? AccentHover
                : MutedText;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                hideColor,
                FontAwesomeIcon.Eye.ToIconString());
        }

        ImGui.SameLine(0f, 6f);

        ImGui.TextColored(
            hideColor,
            "Hide this section");

        if (hideHovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);

            if (ImGui.IsMouseClicked(
                ImGuiMouseButton.Left))
            {
                Plugin.Cfg.ShowFfxivYouTubeSection = false;
                Plugin.Cfg.Save();
            }
        }

        ImGui.PopID();

        // ---------------------------------------------------------
        // Results
        // ---------------------------------------------------------

        var cardWidth =
            (width - gap * (columns - 1)) /
            columns;

        if (ffxivYouTubeResults is not { Count: > 0 } results)
        {
            for (var index = 0;
                 index < columns;
                 index++)
            {
                if (index > 0)
                {
                    ImGui.SameLine(
                        0f,
                        gap);
                }

                ImGui.PushID(
                    $"ffxivLoading_{index}");

                if (isLoadingFfxivYouTube)
                {
                    DrawMediaHubLoadingCard(
                        cardWidth,
                        cardHeight);
                }
                else
                {
                    DrawMediaHubPlaceholderCard(
                        cardWidth,
                        cardHeight);
                }

                ImGui.PopID();
            }

            return;
        }

        var visibleCount =
            Math.Min(
                columns,
                results.Count);

        for (var index = 0;
             index < visibleCount;
             index++)
        {
            if (index > 0)
            {
                ImGui.SameLine(
                    0f,
                    gap);
            }

            ImGui.PushID(
                $"ffxivYoutube_{index}");

            DrawCompactHomeYouTubeCardSurface(
                results[index],
                cardWidth,
                cardHeight,
                useThumbnailActions: true);

            ImGui.PopID();
        }
    }

    private void DrawRecentlyWatchedShelf()
    {
        DrawHomeShelfHeading(
            FontAwesomeIcon.History,
            "Recently Watched",
            Accent,
            showSeeAll: false,
            addBottomSpacing: false);

        var drawList =
      ImGui.GetWindowDrawList();

        var trashGlyph =
            FontAwesomeIcon.Trash.ToIconString();

        Vector2 trashSize;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            trashSize =
                ImGui.CalcTextSize(trashGlyph);
        }

        var trashX =
            ImGui.GetWindowPos().X +
            ImGui.GetWindowContentRegionMax().X -
            HomeContentRightInset -
            trashSize.X;

        var trashY =
            ImGui.GetCursorScreenPos().Y -
            ImGui.GetTextLineHeight() -
            18f;

        var trashMin =
            new Vector2(
                trashX,
                trashY);

        var trashMax =
            trashMin +
            trashSize;

        var trashHovered =
            ImGui.IsMouseHoveringRect(
                trashMin,
                trashMax);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                trashMin,
                ImGui.GetColorU32(
                    trashHovered
                        ? AccentHover
                        : MutedText),
                trashGlyph);
        }

        if (trashHovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);

            ImGui.SetTooltip(
                "Clear recently watched videos");

            if (ImGui.IsMouseClicked(
                    ImGuiMouseButton.Left))
            {
                Plugin.Cfg.RecentlyWatchedVideos.Clear();
                Plugin.Cfg.Save();
            }
        }

 

        var videos =
            Plugin.Cfg.RecentlyWatchedVideos
                .Where(IsResumableHistoryMedia)
                .ToList();

        if (videos is not { Count: > 0 })
        {
            DrawMediaHubShelfCards(
                Ui(224f));

            return;
        }


        var cardCount =
            GetHomeVideoColumnCount(
                ImGui.GetWindowSize().X);

        var gap = Ui(12f);
        var cardHeight = Ui(190f);


        var width =
            ImGui.GetContentRegionAvail().X;

        var cardWidth =
            (width - gap * (cardCount - 1)) /
            cardCount;


        var visibleCount =
            Math.Min(
                cardCount,
                videos.Count);


        for (var index = 0;
             index < visibleCount;
             index++)
        {
            if (index > 0)
            {
                ImGui.SameLine(
                    0f,
                    gap);
            }


            var watched =
                videos[index];


            ImGui.PushID(
                $"recentlyWatched_{index}");


            var origin =
                ImGui.GetCursorScreenPos();

            var size =
                new Vector2(
                    cardWidth,
                    cardHeight);


            ImGui.InvisibleButton(
                "##recentCard",
                size);


            var headingDrawList =
                ImGui.GetWindowDrawList();


            var thumbnail =
                thumbnails.Get(
                    watched.ThumbnailUrl);


            var thumbnailHeight = Ui(116f);


            if (thumbnail is not null)
            {
                drawList.AddImageRounded(
                    thumbnail.Handle,
                    origin,
                    origin +
                    new Vector2(
                        cardWidth,
                        thumbnailHeight),
                    Vector2.Zero,
                    Vector2.One,
                    uint.MaxValue,
                    9f);
            }
            else
            {
                drawList.AddRectFilled(
                    origin,
                    origin +
                    new Vector2(
                        cardWidth,
                        thumbnailHeight),
                    ImGui.GetColorU32(CardBg),
                    9f);
            }


            // Progress bar
            if (watched.DurationSeconds > 0)
            {
                var progress =
                    Math.Clamp(
                        watched.WatchedSeconds /
                        watched.DurationSeconds,
                        0,
                        1);


                var progressHeight = Ui(3f);


                drawList.AddRectFilled(
                    new Vector2(
                        origin.X,
                        origin.Y +
                        thumbnailHeight -
                        progressHeight),
                    new Vector2(
                        origin.X +
                        cardWidth * (float)progress,
                        origin.Y +
                        thumbnailHeight),
                    ImGui.GetColorU32(
                        Accent));
            }


            DrawWrappedLines(
                drawList,
                origin +
                new Vector2(
                    Ui(2f),
                    thumbnailHeight + Ui(10f)),
                cardWidth - 4f,
                ImGui.GetTextLineHeight(),
                2,
                ImGui.GetColorU32(
                    Vector4.One),
                watched.Title);


            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(
                    ImGuiMouseCursor.Hand);


                if (ImGui.IsMouseClicked(
                        ImGuiMouseButton.Left))
                {
                    queue.PlayNow(
                        new VideoQueueEntry(
                            watched.Url,
                            watched.Title,
                            watched.ChannelName,
                            TimeSpan.FromSeconds(
                                watched.DurationSeconds),
                            watched.ThumbnailUrl));


                    // Resume from previous position.
                    video.Seek(
                        (float)watched.WatchedSeconds);
                }
            }


            ImGui.PopID();
        }
    }

    private void DrawRecentlyWatchedCard(
     string title,
     string imageName,
     bool lastWatched,
     float progress,
     float width,
     float height)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.InvisibleButton(
            $"##recent_{title}",
            size);

        var hovered =
            ImGui.IsItemHovered();

        var thumbnailHeight = Ui(92f);

        // ---------------------------------------------------------
        // Thumbnail
        // ---------------------------------------------------------

        var image =
            GetCapabilityImage(imageName);

        var imageWrap =
            image?.GetWrapOrDefault();

        if (imageWrap is not null)
        {
            var (uv0, uv1) =
                CoverUvs(
                    imageWrap.Width,
                    imageWrap.Height,
                    width,
                    thumbnailHeight);

            drawList.AddImageRounded(
                imageWrap.Handle,
                origin,
                origin +
                new Vector2(
                    width,
                    thumbnailHeight),
                uv0,
                uv1,
                uint.MaxValue,
                10f);
        }
        else
        {
            drawList.AddRectFilled(
                origin,
                origin +
                new Vector2(
                    width,
                    thumbnailHeight),
                ImGui.GetColorU32(CardBg),
                10f);
        }

        // ---------------------------------------------------------
        // Watch progress
        // ---------------------------------------------------------

        progress =
            Math.Clamp(
                progress,
                0f,
                1f);

        var progressHeight = Ui(3f);

        var progressY =
            origin.Y +
            thumbnailHeight -
            progressHeight;

        // Very subtle remaining-track line.
        drawList.AddRectFilled(
            new Vector2(
                origin.X,
                progressY),
            new Vector2(
                origin.X + width,
                origin.Y + thumbnailHeight),
            ImGui.GetColorU32(
                new Vector4(
                    1f,
                    1f,
                    1f,
                    0.10f)));

        // Watched portion.
        if (progress > 0f)
        {
            drawList.AddRectFilled(
                new Vector2(
                    origin.X,
                    progressY),
                new Vector2(
                    origin.X + width * progress,
                    origin.Y + thumbnailHeight),
                ImGui.GetColorU32(Accent));
        }

        // ---------------------------------------------------------
        // Last watched badge
        // ---------------------------------------------------------

        if (lastWatched)
        {
            const string badgeText =
                "LAST WATCHED";

            var badgeSize =
                ImGui.CalcTextSize(
                    badgeText);

            var badgeMin =
                origin +
                UiVec(7f, 6f);

            var badgeMax =
                badgeMin +
                new Vector2(
                    badgeSize.X + Ui(10f),
                    badgeSize.Y + Ui(5f));

            drawList.AddRectFilled(
                badgeMin,
                badgeMax,
                ImGui.GetColorU32(
                    new Vector4(
    CardBgHover.X,
    CardBgHover.Y,
    CardBgHover.Z,
    0.75f)),
                5f);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                badgeMin +
                UiVec(5f, 2f),
                ImGui.GetColorU32(
                    Vector4.One),
                badgeText);
        }

        // ---------------------------------------------------------
        // Hover overlay + Continue Watching
        // ---------------------------------------------------------

        if (hovered)
        {
            drawList.AddRectFilled(
                origin,
                origin +
                new Vector2(
                    width,
                    thumbnailHeight),
                ImGui.GetColorU32(
                    new Vector4(
                        0f,
                        0f,
                        0f,
                        0.48f)),
                10f);

            drawList.AddRect(
                origin,
                origin +
                new Vector2(
                    width,
                    thumbnailHeight),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.65f)),
                10f,
                ImDrawFlags.None,
                1f);

            const string buttonText =
                "Continue Watching";

            var buttonTextSize =
                ImGui.CalcTextSize(
                    buttonText);

            var buttonHeight =
                Ui(28f);

            var buttonWidth =
                MathF.Min(
                    width - 16f,
                    buttonTextSize.X + 24f);

            var buttonMin =
                new Vector2(
                    origin.X +
                    (width - buttonWidth) * 0.5f,
                    origin.Y +
                    thumbnailHeight -
                    buttonHeight -
                    Ui(8f));

            var buttonMax =
                buttonMin +
                new Vector2(
                    buttonWidth,
                    buttonHeight);

            var mouse =
                ImGui.GetMousePos();

            var buttonHovered =
                mouse.X >= buttonMin.X &&
                mouse.X <= buttonMax.X &&
                mouse.Y >= buttonMin.Y &&
                mouse.Y <= buttonMax.Y;

            drawList.AddRectFilled(
                buttonMin,
                buttonMax,
                ImGui.GetColorU32(
                    buttonHovered
                        ? AccentHover
                        : Accent),
                6f);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    buttonMin.X +
                    (buttonWidth - buttonTextSize.X) * 0.5f,
                    buttonMin.Y +
                    (buttonHeight - buttonTextSize.Y) * 0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                buttonText);

            // Placeholder only for now.
            // Later this is where we'll resume the actual history item.
            if (buttonHovered &&
                ImGui.IsMouseClicked(
                    ImGuiMouseButton.Left))
            {
                // No backend yet.
            }
        }

        // ---------------------------------------------------------
        // Title
        // ---------------------------------------------------------

        var displayTitle =
            TruncateHomeMediaText(
                title,
                28);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            origin +
            new Vector2(
                Ui(2f),
                thumbnailHeight + Ui(8f)),
            ImGui.GetColorU32(
                Vector4.One),
            displayTitle);
    }

    private void RefreshHomeWatchPartiesIfNeeded()
    {
        var now = ImGui.GetTime();
        if (now - homeWatchPartyFetchedAt < 5d)
        {
            return;
        }

        var token = CurrentSession?.Token;
        if (string.IsNullOrEmpty(token))
        {
            homeWatchPartyRooms = [];
            homeWatchPartyFetchedAt = now;
            return;
        }

        if (homeWatchPartyLoading)
        {
            return;
        }

        homeWatchPartyFetchedAt = now;
        homeWatchPartyLoading = true;
        _ = Task.Run(async () =>
        {
            try
            {
                homeWatchPartyRooms =
                    await roomsClient.ListAsync(token).ConfigureAwait(false);
            }
            finally
            {
                homeWatchPartyLoading = false;
            }
        });
    }

    private void OpenWatchPartyPage()
    {
        currentPage =
            HomePage.PartyDirectory;
    }

    private void JoinHomeWatchParty(
    RoomDirectoryDto room)
    {
        //
        // Home Watch Party cards should behave like Party Directory
        // results and take the user directly to the Watch Party page.
        //
        currentPage =
            HomePage.WatchAlong;

        if (room.Kind ==
            RoomKind.Locked)
        {
            //
            // Use the same global password prompt as the Party
            // Directory instead of navigating to the manual join form.
            //
            partyDirectoryPasswordRoom =
                room;

            partyDirectoryPassword =
                string.Empty;

            partyDirectoryPasswordError =
                null;

            partyDirectoryPasswordPopupRequested =
                true;

            return;
        }

        DoJoin(
            room.HostDisplayName);
    }

    private enum WatchPartyCategory
    {
        Unknown,
        YouTube,
        Movies,
        Tv,
        Twitch,
        Cartoons,
        LiveStream,
        Gaming,
        Dj,
        Music,
        Images,
        Promotional,
    }

    private static bool TryReadWatchPartyMetadata(
        RoomDirectoryDto room,
        out WatchPartyCategory category,
        out bool adultOnly,
        out string serverName,
        out string visibleDescription)
    {
        category =
            WatchPartyCategory.Unknown;

        adultOnly =
            false;

        serverName =
            string.Empty;

        var rawDescription =
            room.Description ??
            string.Empty;

        visibleDescription =
            rawDescription.Trim();

        var tokenStart =
            rawDescription.IndexOf(
                "<#",
                StringComparison.Ordinal);

        if (tokenStart < 0)
        {
            return false;
        }

        var tokenEnd =
            rawDescription.IndexOf(
                "#>",
                tokenStart + 2,
                StringComparison.Ordinal);

        if (tokenEnd < 0)
        {
            return false;
        }

        var tokenContents =
            rawDescription.Substring(
                tokenStart + 2,
                tokenEnd -
                tokenStart -
                2);

        var tokenParts =
            tokenContents.Split(
                '#',
                StringSplitOptions.TrimEntries);

        // Existing rooms use:
        // <#YouTube#0#>
        //
        // New rooms use:
        // <#YouTube#0#Balmung#>
        if (tokenParts.Length < 2)
        {
            return false;
        }

        category =
            tokenParts[0]
                .Trim()
                .ToUpperInvariant() switch
            {
                "YOUTUBE" =>
                    WatchPartyCategory.YouTube,

                "MOVIES" =>
                    WatchPartyCategory.Movies,

                "TV" =>
                    WatchPartyCategory.Tv,

                "TWITCH" =>
                    WatchPartyCategory.Twitch,

                "CARTOONS" =>
                    WatchPartyCategory.Cartoons,

                "LIVE STREAM" =>
                    WatchPartyCategory.LiveStream,

                "LIVESTREAM" =>
                    WatchPartyCategory.LiveStream,

                "GAMING" =>
                    WatchPartyCategory.Gaming,

                "DJ" =>
                    WatchPartyCategory.Dj,

                "MUSIC" =>
                    WatchPartyCategory.Music,

                "IMAGES" =>
                    WatchPartyCategory.Images,

                "PROMOTIONAL" =>
                    WatchPartyCategory.Promotional,

                _ =>
                    WatchPartyCategory.Unknown,
            };

        if (category ==
            WatchPartyCategory.Unknown)
        {
            return false;
        }

        var ratingText =
            tokenParts[1].Trim();

        if (ratingText != "0" &&
            ratingText != "1")
        {
            category =
                WatchPartyCategory.Unknown;

            return false;
        }

        adultOnly =
            ratingText == "1";

        if (tokenParts.Length >= 3)
        {
            serverName =
                tokenParts[2].Trim();
        }

        // Hide the entire metadata marker from the visible description.
        visibleDescription =
            (
                rawDescription[..tokenStart] +
                rawDescription[(tokenEnd + 2)..]
            ).Trim();

        return true;
    }

    private static string WatchPartyDescription(
        RoomDirectoryDto room)
    {
        TryReadWatchPartyMetadata(
            room,
            out _,
            out _,
            out _,
            out var visibleDescription);

        if (!string.IsNullOrWhiteSpace(
                visibleDescription))
        {
            return visibleDescription;
        }

        return "No description set for this Watch Party.";
    }

    private static WatchPartyCategory GetWatchPartyCategory(
        RoomDirectoryDto room)
    {
        TryReadWatchPartyMetadata(
            room,
            out var category,
            out _,
            out _,
            out _);

        return category;
    }

    private static bool WatchPartyIsAdultOnly(
        RoomDirectoryDto room)
    {
        return
            TryReadWatchPartyMetadata(
                room,
                out _,
                out var adultOnly,
                out _,
                out _) &&
            adultOnly;
    }

    private static string WatchPartyServer(
        RoomDirectoryDto room)
    {
        TryReadWatchPartyMetadata(
            room,
            out _,
            out _,
            out var serverName,
            out _);

        return serverName;
    }

    private static string WatchPartyLocation(
        RoomDirectoryDto room)
    {
        return !string.IsNullOrWhiteSpace(
                room.Location)
            ? room.Location!
            : "Location not specified";
    }

    private static string WatchPartyLocationTooltip(
        RoomDirectoryDto room)
    {
        var location =
            WatchPartyLocation(
                room);

        var server =
            WatchPartyServer(
                room);

        return string.IsNullOrWhiteSpace(
                server)
            ? location
            : $"{location} — {server}";
    }
    private static string WatchPartyVisibilityText(RoomKind kind) =>
        kind switch
        {
            RoomKind.Locked => "LOCKED",
            RoomKind.Venue => "VENUE",
            _ => "PUBLIC",
        };

    private static FontAwesomeIcon WatchPartyVisibilityIcon(RoomKind kind) =>
        kind switch
        {
            RoomKind.Locked => FontAwesomeIcon.Lock,
            RoomKind.Venue => FontAwesomeIcon.Video,
            _ => FontAwesomeIcon.Globe,
        };

    private static Vector4 WatchPartyVisibilityColor(RoomKind kind) =>
        kind switch
        {
            RoomKind.Locked => new Vector4(
                1.00f,
                0.35f,
                0.20f,
                1f),

            RoomKind.Venue => new Vector4(
                1.00f,
                0.72f,
                0.20f,
                1f),

            _ => new Vector4(
                0.35f,
                0.90f,
                0.48f,
                1f),
        };

    private static string WatchPartyPlaybackStateText(
        RoomDirectoryDto room)
    {
        if (!room.HasMedia)
        {
            return "WAITING";
        }

        return room.Paused
            ? "PAUSED"
            : "PLAYING";
    }

    private static Vector4 WatchPartyPlaybackStateColor(
        RoomDirectoryDto room)
    {
        if (!room.HasMedia)
        {
            return new Vector4(
                0.48f,
                0.70f,
                1.00f,
                1f);
        }

        if (room.Paused)
        {
            return new Vector4(
                1.00f,
                0.72f,
                0.20f,
                1f);
        }

        return new Vector4(
            0.35f,
            0.90f,
            0.48f,
            1f);
    }

    private static FontAwesomeIcon WatchPartyPlaybackStateIcon(
        RoomDirectoryDto room)
    {
        if (!room.HasMedia)
        {
            return FontAwesomeIcon.Circle;
        }

        return room.Paused
            ? FontAwesomeIcon.Pause
            : FontAwesomeIcon.Play;
    }

    private static FontAwesomeIcon WatchPartyCategoryIcon(
       RoomDirectoryDto room)
    {
        return GetWatchPartyCategory(
            room) switch
        {
            WatchPartyCategory.YouTube =>
                FontAwesomeIcon.PlayCircle,

            WatchPartyCategory.Movies =>
                FontAwesomeIcon.Film,

            WatchPartyCategory.Tv =>
                FontAwesomeIcon.Tv,

            WatchPartyCategory.Twitch =>
                FontAwesomeIcon.CommentDots,

            WatchPartyCategory.Cartoons =>
                FontAwesomeIcon.Smile,

            WatchPartyCategory.LiveStream =>
                FontAwesomeIcon.BroadcastTower,

            WatchPartyCategory.Gaming =>
                FontAwesomeIcon.Gamepad,

            WatchPartyCategory.Dj =>
                FontAwesomeIcon.Headphones,

            WatchPartyCategory.Music =>
                FontAwesomeIcon.Music,

            WatchPartyCategory.Images =>
                FontAwesomeIcon.Images,

            WatchPartyCategory.Promotional =>
                FontAwesomeIcon.Bullhorn,

            _ =>
                FontAwesomeIcon.Video,
        };
    }

    private static string WatchPartyCategoryText(
        RoomDirectoryDto room)
    {
        return GetWatchPartyCategory(
            room) switch
        {
            WatchPartyCategory.YouTube =>
                "YOUTUBE",

            WatchPartyCategory.Movies =>
                "MOVIES",

            WatchPartyCategory.Tv =>
                "TV",

            WatchPartyCategory.Twitch =>
                "TWITCH",

            WatchPartyCategory.Cartoons =>
                "CARTOONS",

            WatchPartyCategory.LiveStream =>
                "LIVE STREAM",

            WatchPartyCategory.Gaming =>
                "GAMING",

            WatchPartyCategory.Dj =>
                "DJ",

            WatchPartyCategory.Music =>
                "MUSIC",

            WatchPartyCategory.Images =>
                "IMAGES",

            WatchPartyCategory.Promotional =>
                "PROMOTIONAL",

            _ =>
                "MEDIA",
        };
    }

    private RoomDirectoryDto[] GetRankedHomeWatchPartyRooms()
    {
        //
        // Discovery priority:
        //
        // 1. Public + Venue rooms.
        // 2. Highest viewer count first.
        // 3. Locked rooms fill remaining slots.
        //
        // A large locked room therefore does not displace an available
        // Public or Venue room.
        //
        return homeWatchPartyRooms
            .OrderBy(
                room =>
                    room.Kind == RoomKind.Locked
                        ? 1
                        : 0)
            .ThenByDescending(
                room => room.ViewerCount)
            .ThenBy(
                room => room.HostDisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void DrawWatchPartiesShelf()
    {
        RefreshHomeWatchPartiesIfNeeded();

        var width =
            ImGui.GetContentRegionAvail().X;

        var gap =
            Ui(10f);

        //
        // Four columns at the approved full window size.
        // Smaller windows wrap to three or two columns instead of
        // crushing every card into an unusably narrow slot.
        //
        var columnCount =
            width >= Ui(860f)
                ? 4
                : width >= Ui(620f)
                    ? 3
                    : 2;

        var cardWidth =
            (width -
             gap * (columnCount - 1)) /
            columnCount;

        var rankedRooms =
            GetRankedHomeWatchPartyRooms();

        // Keep the established room-card height whenever a room is available. The fully empty
        // create/join state has much less content, so it only needs half of that vertical space.
        var cardHeight =
            Ui(rankedRooms.Length == 0
                ? 180f
                : 330f);

        //
        // With four or more rooms:
        //
        // - outside a party: show three rooms and Create / Join
        // - inside a party:  show the top four rooms
        //
        var showFourthRoom =
            rankedRooms.Length >= 4 &&
            stream.Mode != StreamMode.None;

        var roomLimit =
            showFourthRoom
                ? 4
                : 3;

        var rooms =
            rankedRooms
                .Take(roomLimit)
                .ToArray();

        var showCreateCard =
            !showFourthRoom;

        DrawHomeShelfHeading(
            FontAwesomeIcon.Users,
            "Watch Parties",
            AccentHover,
            addBottomSpacing: false,
            onSeeAll: OpenWatchPartyPage);

        var slotIndex =
            0;

        for (var index = 0;
             index < rooms.Length;
             index++)
        {
            if (slotIndex > 0 &&
                slotIndex % columnCount != 0)
            {
                ImGui.SameLine(
                    0f,
                    gap);
            }

            var room =
                rooms[index];

            ImGui.PushID(
                $"homeWatchPartyRoom_{room.HostAccountId}_{index}");

            DrawWatchPartyCard(
                room,
                cardWidth,
                cardHeight,
                () => JoinHomeWatchParty(room));

            ImGui.PopID();

            slotIndex++;
        }

        if (showCreateCard)
        {
            var usedColumns =
                slotIndex %
                columnCount;

            var remainingColumns =
                usedColumns == 0
                    ? columnCount
                    : columnCount -
                      usedColumns;

            var createCardWidth =
                cardWidth *
                remainingColumns +
                gap *
                (remainingColumns - 1);

            if (usedColumns > 0)
            {
                ImGui.SameLine(
                    0f,
                    gap);
            }

            DrawCreateWatchPartyCard(
                createCardWidth,
                cardHeight);
        }
    }

    private void DrawWatchPartyCard(
     RoomDirectoryDto room,
     float width,
     float height,
     Action? onJoin)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);

        var cardMax =
            origin +
            size;

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.InvisibleButton(
            $"##watchPartyCard_{room.HostAccountId}",
            size);

        var cardHovered =
            ImGui.IsItemHovered();

        // ---------------------------------------------------------
        // Card background and outline
        // ---------------------------------------------------------

        drawList.AddRectFilled(
            origin,
            cardMax,
            ImGui.GetColorU32(
                cardHovered
                    ? CardBgHover
                    : CardBg),
            Ui(10f));

        drawList.AddRect(
            origin,
            cardMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    cardHovered
                        ? 0.58f
                        : 0.22f)),
            Ui(10f),
            ImDrawFlags.None,
            Ui(1f));

        // ---------------------------------------------------------
        // Room artwork
        // ---------------------------------------------------------

        var headerHeight =
            Ui(120f);

        IDalamudTextureWrap? imageWrap =
            homeHero;

        if (imageWrap is not null)
        {
            var (uv0, uv1) =
                CoverUvs(
                    imageWrap.Width,
                    imageWrap.Height,
                    width,
                    headerHeight);

            drawList.AddImageRounded(
                imageWrap.Handle,
                origin,
                origin +
                new Vector2(
                    width,
                    headerHeight),
                uv0,
                uv1,
                uint.MaxValue,
                Ui(10f),
                ImDrawFlags.RoundCornersTop);
        }
        else
        {
            drawList.AddRectFilled(
                origin,
                origin +
                new Vector2(
                    width,
                    headerHeight),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.16f)),
                Ui(10f),
                ImDrawFlags.RoundCornersTop);
        }

        //
        // Artwork shading keeps the two header badges readable.
        //
        drawList.AddRectFilledMultiColor(
            origin,
            origin +
            new Vector2(
                width,
                headerHeight),
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.14f)),
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.14f)),
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.62f)),
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.62f)));

        // ---------------------------------------------------------
        // Currently playing title
        // ---------------------------------------------------------

        if (room.HasMedia &&
            !string.IsNullOrWhiteSpace(
                room.MediaTitle))
        {
            var fullMediaTitle =
                room.MediaTitle!;

            var displayMediaTitle =
                fullMediaTitle;

            var maximumTitleWidth =
                MathF.Max(
                    width -
                    Ui(20f),
                    Ui(40f));

            while (displayMediaTitle.Length > 1 &&
                   ImGui.CalcTextSize(
                       displayMediaTitle).X >
                   maximumTitleWidth)
            {
                displayMediaTitle =
                    displayMediaTitle[..^1];
            }

            if (!string.Equals(
                    displayMediaTitle,
                    fullMediaTitle,
                    StringComparison.Ordinal))
            {
                displayMediaTitle =
                    displayMediaTitle.TrimEnd() +
                    "…";
            }

            var mediaTitleSize =
                ImGui.CalcTextSize(
                    displayMediaTitle);

            var mediaTitleMin =
                new Vector2(
                    origin.X +
                    Ui(8f),
                    origin.Y +
                    headerHeight -
                    mediaTitleSize.Y -
                    Ui(9f));

            var mediaTitleMax =
                new Vector2(
                    mediaTitleMin.X +
                    mediaTitleSize.X +
                    Ui(12f),
                    mediaTitleMin.Y +
                    mediaTitleSize.Y +
                    Ui(6f));

            drawList.AddRectFilled(
                mediaTitleMin -
                UiVec(
                    6f,
                    3f),
                mediaTitleMax -
                UiVec(
                    6f,
                    3f),
                ImGui.GetColorU32(
                    new Vector4(
                        0.015f,
                        0.02f,
                        0.035f,
                        0.88f)),
                Ui(5f));

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                mediaTitleMin,
                ImGui.GetColorU32(
                    Vector4.One),
                displayMediaTitle);

            if (ImGui.IsMouseHoveringRect(
                    mediaTitleMin,
                    mediaTitleMax))
            {
                ImGui.SetTooltip(
                    fullMediaTitle);
            }
        }

        // ---------------------------------------------------------
        // Playback badge
        // ---------------------------------------------------------

        var playbackText =
            WatchPartyPlaybackStateText(
                room);

        var playbackColor =
            WatchPartyPlaybackStateColor(
                room);

        var playbackGlyph =
            WatchPartyPlaybackStateIcon(
                    room)
                .ToIconString();

        Vector2 playbackGlyphSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            playbackGlyphSize =
                ImGui.CalcTextSize(
                    playbackGlyph);
        }

        if (!room.HasMedia)
        {
            playbackGlyphSize =
                new Vector2(
                    Ui(4f),
                    Ui(4f));
        }

        var playbackTextSize =
            ImGui.CalcTextSize(
                playbackText);

        var badgeHeight =
            Ui(22f);

        var playbackBadgeWidth =
            Ui(7f) +
            playbackGlyphSize.X +
            Ui(5f) +
            playbackTextSize.X +
            Ui(7f);

        var playbackMin =
            origin +
            UiVec(
                8f,
                8f);

        var playbackMax =
            playbackMin +
            new Vector2(
                playbackBadgeWidth,
                badgeHeight);

        drawList.AddRectFilled(
            playbackMin,
            playbackMax,
            ImGui.GetColorU32(
                new Vector4(
                    0.015f,
                    0.02f,
                    0.035f,
                    0.92f)),
            Ui(5f));

        drawList.AddRect(
            playbackMin,
            playbackMax,
            ImGui.GetColorU32(
                new Vector4(
                    playbackColor.X,
                    playbackColor.Y,
                    playbackColor.Z,
                    0.82f)),
            Ui(5f));

        //
        // Waiting uses a small hand-drawn dot. Playing and Paused
        // continue to use their Font Awesome symbols.
        //
        if (!room.HasMedia)
        {
            drawList.AddCircleFilled(
                new Vector2(
                    playbackMin.X +
                    Ui(7f) +
                    playbackGlyphSize.X *
                    0.5f,
                    playbackMin.Y +
                    badgeHeight *
                    0.5f),
                Ui(3.25f),
                ImGui.GetColorU32(
                    playbackColor),
                16);
        }
        else
        {
            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    new Vector2(
                        playbackMin.X +
                        Ui(7f),
                        playbackMin.Y +
                        (badgeHeight -
                         playbackGlyphSize.Y) *
                        0.5f),
                    ImGui.GetColorU32(
                        playbackColor),
                    playbackGlyph);
            }
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                playbackMin.X +
                Ui(7f) +
                playbackGlyphSize.X +
                Ui(5f),
                playbackMin.Y +
                (badgeHeight -
                 playbackTextSize.Y) *
                0.5f),
            ImGui.GetColorU32(
                playbackColor),
            playbackText);

        // ---------------------------------------------------------
        // Viewer badge
        // ---------------------------------------------------------

        var viewerText =
            room.ViewerCount.ToString();

        var viewerTextSize =
            ImGui.CalcTextSize(
                viewerText);

        var eyeGlyph =
            FontAwesomeIcon.Eye
                .ToIconString();

        Vector2 eyeGlyphSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            eyeGlyphSize =
                ImGui.CalcTextSize(
                    eyeGlyph);
        }

        var viewerBadgeWidth =
            Ui(7f) +
            eyeGlyphSize.X +
            Ui(5f) +
            viewerTextSize.X +
            Ui(7f);

        var viewerMin =
            new Vector2(
                origin.X +
                width -
                viewerBadgeWidth -
                Ui(8f),
                origin.Y +
                Ui(8f));

        var viewerMax =
            viewerMin +
            new Vector2(
                viewerBadgeWidth,
                badgeHeight);

        drawList.AddRectFilled(
            viewerMin,
            viewerMax,
            ImGui.GetColorU32(
                new Vector4(
                    0.015f,
                    0.02f,
                    0.035f,
                    0.92f)),
            Ui(5f));

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    viewerMin.X +
                    Ui(7f),
                    viewerMin.Y +
                    (badgeHeight -
                     eyeGlyphSize.Y) *
                    0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                eyeGlyph);
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                viewerMin.X +
                Ui(7f) +
                eyeGlyphSize.X +
                Ui(5f),
                viewerMin.Y +
                (badgeHeight -
                 viewerTextSize.Y) *
                0.5f),
            ImGui.GetColorU32(
                Vector4.One),
            viewerText);

        if (ImGui.IsMouseHoveringRect(
                viewerMin,
                viewerMax))
        {
            ImGui.SetTooltip(
                room.ViewerCount == 1
                    ? "1 watching"
                    : $"{room.ViewerCount} watching");
        }

        // ---------------------------------------------------------
        // Media medallion
        // ---------------------------------------------------------

        var categoryGlyph =
            WatchPartyCategoryIcon(
                    room)
                .ToIconString();

        var categoryCenter =
            new Vector2(
                origin.X +
                width * 0.5f,
                origin.Y +
                headerHeight -
                Ui(7f));

        var categoryRadius =
            Ui(20f);

        drawList.AddCircleFilled(
            categoryCenter,
            categoryRadius +
            Ui(3f),
            ImGui.GetColorU32(
                new Vector4(
                    0.015f,
                    0.02f,
                    0.035f,
                    0.98f)),
            32);

        drawList.AddCircleFilled(
            categoryCenter,
            categoryRadius,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.28f)),
            32);

        drawList.AddCircle(
            categoryCenter,
            categoryRadius,
            ImGui.GetColorU32(
                AccentHover),
            32,
            Ui(1.5f));

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var categoryGlyphSize =
                ImGui.CalcTextSize(
                    categoryGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                categoryCenter -
                categoryGlyphSize *
                0.5f,
                ImGui.GetColorU32(
                    Vector4.One),
                categoryGlyph);
        }

        // ---------------------------------------------------------
        // Host identity
        // ---------------------------------------------------------

        var avatarSize =
            Ui(34f);

        var avatarPos =
             new Vector2(
                 origin.X +
                 Ui(10f),
                 origin.Y +
                 headerHeight +
                 Ui(10f));

        var hostName =
            string.IsNullOrWhiteSpace(
                room.HostDisplayName)
                ? "Unknown Host"
                : room.HostDisplayName;

        //
        // Use the same unrestricted profile resolver used by Watch
        // Party chat. This first tries the direct server profile route
        // and then the public account-search fallback.
        //
        if (!string.IsNullOrWhiteSpace(
                room.HostAccountId))
        {
            EnsurePartyAvatarLoaded(
                room.HostAccountId,
                hostName);
        }

        PartyAvatarInfo? hostAvatar =
       null;

        if (!string.IsNullOrWhiteSpace(
                room.HostAccountId) &&
            partyAvatarCache.TryGetValue(
                room.HostAccountId,
                out var cachedHostAvatar))
        {
            hostAvatar =
                cachedHostAvatar;
        }

        if (hostAvatar is not null &&
            (
                !string.IsNullOrWhiteSpace(
                    hostAvatar.AvatarIcon) ||
                !string.IsNullOrWhiteSpace(
                    hostAvatar.AvatarImageUrl)
            ))
        {
            ImGui.SetCursorScreenPos(
                avatarPos);

            ImGui.PushID(
                $"homeRoomHost_{room.HostAccountId}");

            DrawAvatarChip(
                hostAvatar.AvatarIcon,
                hostAvatar.AvatarColorHex,
                avatarSize,
                hostAvatar.AvatarImageUrl);

            ImGui.PopID();
        }
        else
        {
            //
            // The lookup may still be loading, may have failed, or the
            // host may not have selected an avatar. Always show a user
            // symbol rather than leaving the circle empty.
            //
            var fallbackCenter =
                avatarPos +
                new Vector2(
                    avatarSize * 0.5f,
                    avatarSize * 0.5f);

            drawList.AddCircleFilled(
                fallbackCenter,
                avatarSize * 0.5f,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.22f)),
                32);

            drawList.AddCircle(
                fallbackCenter,
                avatarSize * 0.5f,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.62f)),
                32,
                Ui(1f));

            var fallbackGlyph =
                FontAwesomeIcon.User
                    .ToIconString();

            Vector2 fallbackGlyphSize;

            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                fallbackGlyphSize =
                    ImGui.CalcTextSize(
                        fallbackGlyph);

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    fallbackCenter -
                    fallbackGlyphSize *
                    0.5f,
                    ImGui.GetColorU32(
                        AccentHover),
                    fallbackGlyph);
            }
        }

        var hostedByText =
            $"Hosted by: {hostName}";

        var hostTextPos =
            new Vector2(
                avatarPos.X +
                avatarSize +
                Ui(8f),
                avatarPos.Y +
                (avatarSize -
                 ImGui.GetTextLineHeight()) *
                0.5f);

        var hostTextWidth =
            MathF.Max(
                origin.X +
                width -
                Ui(10f) -
                hostTextPos.X,
                Ui(30f));

        var displayHostText =
            hostedByText;

        while (displayHostText.Length > 1 &&
               ImGui.CalcTextSize(
                   displayHostText).X >
               hostTextWidth)
        {
            displayHostText =
                displayHostText[..^1];
        }

        if (!string.Equals(
                displayHostText,
                hostedByText,
                StringComparison.Ordinal))
        {
            displayHostText =
                displayHostText.TrimEnd() +
                "…";
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            hostTextPos,
            ImGui.GetColorU32(
                Vector4.One),
            displayHostText);

        if (ImGui.IsMouseHoveringRect(
                hostTextPos,
                hostTextPos +
                new Vector2(
                    hostTextWidth,
                    ImGui.GetTextLineHeight())))
        {
            ImGui.SetTooltip(
                hostedByText);
        }

        // ---------------------------------------------------------
        // Description
        // ---------------------------------------------------------

        var description =
            WatchPartyDescription(
                room);

        var descriptionMin =
            new Vector2(
                origin.X +
                Ui(10f),
                origin.Y +
                headerHeight +
                Ui(52f));

        var descriptionHeight =
            Ui(46f);

        var descriptionMax =
            new Vector2(
                origin.X +
                width -
                Ui(10f),
                descriptionMin.Y +
                descriptionHeight);

        drawList.AddRectFilled(
            descriptionMin,
            descriptionMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.09f)),
            Ui(6f));

        drawList.AddRect(
            descriptionMin,
            descriptionMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.13f)),
            Ui(6f));

        var descriptionTextMin =
            descriptionMin +
            UiVec(
                7f,
                6f);

        DrawWrappedLines(
            drawList,
            descriptionTextMin,
            MathF.Max(
                descriptionMax.X -
                descriptionTextMin.X -
                Ui(7f),
                Ui(40f)),
            ImGui.GetTextLineHeight(),
            2,
            ImGui.GetColorU32(
                new Vector4(
                    0.88f,
                    0.88f,
                    0.94f,
                    1f)),
            description);

        if (ImGui.IsMouseHoveringRect(
                descriptionMin,
                descriptionMax))
        {
            ImGui.SetTooltip(
                description);
        }

        // ---------------------------------------------------------
        // Location
        // ---------------------------------------------------------

        var location =
            WatchPartyLocation(
                room);

        var locationY =
            descriptionMax.Y +
            Ui(8f);

        var mapGlyph =
            FontAwesomeIcon.MapMarkerAlt
                .ToIconString();

        Vector2 mapGlyphSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            mapGlyphSize =
                ImGui.CalcTextSize(
                    mapGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    origin.X +
                    Ui(11f),
                    locationY),
                ImGui.GetColorU32(
                    AccentHover),
                mapGlyph);
        }

        var locationTextX =
            origin.X +
            Ui(11f) +
            mapGlyphSize.X +
            Ui(6f);

        var locationAvailableWidth =
            MathF.Max(
                origin.X +
                width -
                Ui(10f) -
                locationTextX,
                Ui(30f));

        const int locationDisplayLimit =
            30;

        //
        // Limit the visible card text to 30 characters. The complete
        // location remains available through the hover tooltip.
        //
        var displayLocation =
            location.Length >
            locationDisplayLimit
                ? location[
                    ..locationDisplayLimit]
                : location;

        //
        // Retain the width-based safeguard for cards displayed in a
        // narrow resized window.
        //
        while (displayLocation.Length > 1 &&
               ImGui.CalcTextSize(
                   displayLocation + "…").X >
               locationAvailableWidth)
        {
            displayLocation =
                displayLocation[..^1];
        }

        if (!string.Equals(
                displayLocation,
                location,
                StringComparison.Ordinal))
        {
            displayLocation =
                displayLocation.TrimEnd() +
                "…";
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                locationTextX,
                locationY),
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.94f)),
            displayLocation);

        // Include both the pin and the location text in the hover area.
        // The visible card continues showing only the location.
        if (ImGui.IsMouseHoveringRect(
                new Vector2(
                    origin.X +
                    Ui(8f),
                    locationY -
                    Ui(3f)),
                new Vector2(
                    locationTextX +
                    locationAvailableWidth,
                    locationY +
                    ImGui.GetTextLineHeight() +
                    Ui(3f))))
        {
            ImGui.SetTooltip(
                WatchPartyLocationTooltip(
                    room));
        }

        // ---------------------------------------------------------
        // Visibility and media tags
        // ---------------------------------------------------------

        var tagsY =
           locationY +
           Ui(30f);

        var visibilityColor =
            WatchPartyVisibilityColor(
                room.Kind);

        var visibilityWidth =
              DrawWatchPartyTag(
                  drawList,
                  new Vector2(
                      origin.X +
                      Ui(10f),
                      tagsY),
                  WatchPartyVisibilityIcon(
                      room.Kind),
                  WatchPartyVisibilityText(
                      room.Kind),
                  visibilityColor);

        var categoryX =
            origin.X +
            Ui(10f) +
            visibilityWidth +
            Ui(7f);

        var categoryWidth =
            DrawWatchPartyTag(
                drawList,
                new Vector2(
                    categoryX,
                    tagsY),
                WatchPartyCategoryIcon(
                    room),
                WatchPartyCategoryText(
                    room),
                AccentHover);

        if (WatchPartyIsAdultOnly(
                room))
        {
            DrawWatchPartyAdultTag(
                drawList,
                new Vector2(
                    categoryX +
                    categoryWidth +
                    Ui(7f),
                    tagsY));
        }

        // ---------------------------------------------------------
        // Join and access controls
        // ---------------------------------------------------------

        var buttonHeight =
            Ui(30f);

        var accessButtonWidth =
            buttonHeight;

        var buttonGap =
            Ui(6f);

        var buttonsY =
            origin.Y +
            height -
            buttonHeight -
            Ui(8f);

        var joinMin =
            new Vector2(
                origin.X +
                Ui(8f),
                buttonsY);

        var accessMax =
            new Vector2(
                origin.X +
                width -
                Ui(8f),
                buttonsY +
                buttonHeight);

        var accessMin =
            new Vector2(
                accessMax.X -
                accessButtonWidth,
                buttonsY);

        var joinMax =
            new Vector2(
                accessMin.X -
                buttonGap,
                buttonsY +
                buttonHeight);

        var mouse =
            ImGui.GetMousePos();

        var joinHovered =
            mouse.X >= joinMin.X &&
            mouse.X <= joinMax.X &&
            mouse.Y >= joinMin.Y &&
            mouse.Y <= joinMax.Y;

        var accessHovered =
            mouse.X >= accessMin.X &&
            mouse.X <= accessMax.X &&
            mouse.Y >= accessMin.Y &&
            mouse.Y <= accessMax.Y;

        drawList.AddRectFilled(
            joinMin,
            joinMax,
            ImGui.GetColorU32(
                joinHovered
                    ? AccentHover
                    : Accent),
            Ui(6f));

        const string joinText =
            "Join Room";

        var joinTextSize =
            ImGui.CalcTextSize(
                joinText);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                joinMin.X +
                (joinMax.X -
                 joinMin.X -
                 joinTextSize.X) *
                0.5f,
                joinMin.Y +
                (buttonHeight -
                 joinTextSize.Y) *
                0.5f),
            ImGui.GetColorU32(
                Vector4.One),
            joinText);

        drawList.AddRectFilled(
            accessMin,
            accessMax,
            ImGui.GetColorU32(
                accessHovered
                    ? new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.20f)
                    : new Vector4(
                        0.025f,
                        0.03f,
                        0.055f,
                        1f)),
            Ui(6f));

        drawList.AddRect(
            accessMin,
            accessMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    accessHovered
                        ? 0.95f
                        : 0.58f)),
            Ui(6f));

        var accessGlyph =
            (room.Kind == RoomKind.Locked
                ? FontAwesomeIcon.Lock
                : FontAwesomeIcon.Unlock)
            .ToIconString();

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var accessGlyphSize =
                ImGui.CalcTextSize(
                    accessGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    accessMin.X +
                    (accessButtonWidth -
                     accessGlyphSize.X) *
                    0.5f,
                    accessMin.Y +
                    (buttonHeight -
                     accessGlyphSize.Y) *
                    0.5f),
                ImGui.GetColorU32(
                    room.Kind == RoomKind.Locked
                        ? visibilityColor
                        : AccentHover),
                accessGlyph);
        }

        if (joinHovered ||
            accessHovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);
        }

        if (accessHovered)
        {
            ImGui.SetTooltip(
                room.Kind == RoomKind.Locked
                    ? "Password required"
                    : "Open room");
        }

        if ((joinHovered ||
             accessHovered) &&
            onJoin is not null &&
            ImGui.IsMouseClicked(
                ImGuiMouseButton.Left))
        {
            onJoin();
        }

        //
        // DrawAvatarChip created an ImGui item after the original card button.
        // Restore the full card as the current layout item so SameLine and
        // wrapping use the complete card rectangle.
        //
        ImGui.SetCursorScreenPos(
            origin);

        ImGui.Dummy(
            size);
    }

    private float DrawWatchPartyTag(
       ImDrawListPtr drawList,
       Vector2 origin,
       FontAwesomeIcon icon,
       string text,
       Vector4 color)
    {
        var glyph =
            icon.ToIconString();

        const float iconScale =
            0.82f;

        Vector2 glyphSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            SetUiFontScale(
                iconScale);

            glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            SetUiFontScale(
                1f);
        }

        var textSize =
            ImGui.CalcTextSize(
                text);

        var height =
            Ui(22f);

        var width =
            Ui(7f) +
            glyphSize.X +
            Ui(5f) +
            textSize.X +
            Ui(8f);

        var max =
            origin +
            new Vector2(
                width,
                height);

        drawList.AddRectFilled(
            origin,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    0.09f)),
            Ui(5f));

        drawList.AddRect(
            origin,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    0.72f)),
            Ui(5f));

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            SetUiFontScale(
                iconScale);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    origin.X +
                    Ui(7f),
                    origin.Y +
                    (height -
                     glyphSize.Y) *
                    0.5f),
                ImGui.GetColorU32(
                    color),
                glyph);

            SetUiFontScale(
                1f);
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X +
                Ui(7f) +
                glyphSize.X +
                Ui(5f),
                origin.Y +
                (height -
                 textSize.Y) *
                0.5f),
            ImGui.GetColorU32(
                color),
            text);

        return width;
    }

    private float DrawWatchPartyAdultTag(
     ImDrawListPtr drawList,
     Vector2 origin)
    {
        const string text =
            "18+";

        var textSize =
            ImGui.CalcTextSize(
                text);

        var color =
            new Vector4(
                1.00f,
                0.28f,
                0.32f,
                1f);

        var height =
            Ui(22f);

        var width =
            textSize.X +
            Ui(14f);

        var max =
            origin +
            new Vector2(
                width,
                height);

        drawList.AddRectFilled(
            origin,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    0.12f)),
            Ui(5f));

        drawList.AddRect(
            origin,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    0.82f)),
            Ui(5f));

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X +
                (width -
                 textSize.X) *
                0.5f,
                origin.Y +
                (height -
                 textSize.Y) *
                0.5f),
            ImGui.GetColorU32(
                color),
            text);

        return width;
    }

    private void DrawCreateWatchPartyCard(
        float width,
        float height)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.InvisibleButton(
            "##createWatchParty",
            size);

        var hovered =
            ImGui.IsItemHovered();

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                hovered
                    ? CardBgHover
                    : new Vector4(
                        CardBg.X,
                        CardBg.Y,
                        CardBg.Z,
                        0.45f)),
            Ui(10f));

        DrawDashedRect(
            drawList,
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    hovered
                        ? 0.75f
                        : 0.40f)),
            Ui(10f));

        var centerX =
            origin.X +
            width * 0.5f;

        var iconCenter =
            new Vector2(
                centerX,
                origin.Y +
                Ui(44f));

        var circleRadius =
            Ui(16f);

        drawList.AddCircleFilled(
            iconCenter,
            circleRadius,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    hovered
                        ? 0.30f
                        : 0.20f)));

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var glyph =
                FontAwesomeIcon.Plus
                    .ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconCenter -
                glyphSize * 0.5f,
                ImGui.GetColorU32(
                    AccentHover),
                glyph);
        }

        const string title =
            "Watch With Friends";

        var titleSize =
            ImGui.CalcTextSize(
                title);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                centerX -
                titleSize.X * 0.5f,
                origin.Y +
                Ui(70f)),
            ImGui.GetColorU32(
                Vector4.One),
            title);

        const string subtitle1 =
            "Create or join a room to";

        const string subtitle2 =
            "watch together in Eorzea";

        var subtitle1Size =
            ImGui.CalcTextSize(
                subtitle1);

        var subtitle2Size =
            ImGui.CalcTextSize(
                subtitle2);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                centerX -
                subtitle1Size.X * 0.5f,
                origin.Y +
                Ui(94f)),
            ImGui.GetColorU32(
                MutedText),
            subtitle1);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                centerX -
                subtitle2Size.X * 0.5f,
                origin.Y +
                Ui(111f)),
            ImGui.GetColorU32(
                MutedText),
            subtitle2);

        // ---------------------------------------------------------
        // Actions
        // ---------------------------------------------------------

        var buttonGap =
            Ui(8f);

        var buttonHeight =
            Ui(28f);

        var horizontalPadding =
            Ui(12f);

        var buttonWidth =
            (width -
             horizontalPadding * 2f -
             buttonGap) *
            0.5f;

        var buttonsY =
            origin.Y +
            height -
            buttonHeight -
            Ui(10f);

        var newRoomMin =
            new Vector2(
                origin.X +
                horizontalPadding,
                buttonsY);

        var newRoomMax =
            new Vector2(
                newRoomMin.X +
                buttonWidth,
                buttonsY +
                buttonHeight);

        var joinRoomMin =
            new Vector2(
                newRoomMax.X +
                buttonGap,
                buttonsY);

        var joinRoomMax =
            new Vector2(
                joinRoomMin.X +
                buttonWidth,
                buttonsY +
                buttonHeight);

        var mousePos =
            ImGui.GetMousePos();

        var newRoomHovered =
            mousePos.X >= newRoomMin.X &&
            mousePos.X <= newRoomMax.X &&
            mousePos.Y >= newRoomMin.Y &&
            mousePos.Y <= newRoomMax.Y;

        var joinRoomHovered =
            mousePos.X >= joinRoomMin.X &&
            mousePos.X <= joinRoomMax.X &&
            mousePos.Y >= joinRoomMin.Y &&
            mousePos.Y <= joinRoomMax.Y;

        // ---------------------------------------------------------
        // New Room
        // ---------------------------------------------------------

        drawList.AddRectFilled(
            newRoomMin,
            newRoomMax,
            ImGui.GetColorU32(
                newRoomHovered
                    ? AccentHover
                    : Accent),
            Ui(6f));

        const string newRoomText =
            "New Room";

        var newRoomTextSize =
            ImGui.CalcTextSize(
                newRoomText);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                newRoomMin.X +
                (buttonWidth -
                 newRoomTextSize.X) *
                0.5f,
                newRoomMin.Y +
                (buttonHeight -
                 newRoomTextSize.Y) *
                0.5f),
            ImGui.GetColorU32(
                Vector4.One),
            newRoomText);

        // ---------------------------------------------------------
        // Join Room
        // ---------------------------------------------------------

        drawList.AddRectFilled(
            joinRoomMin,
            joinRoomMax,
            ImGui.GetColorU32(
                joinRoomHovered
                    ? CardBgHover
                    : CardBg),
            Ui(6f));

        drawList.AddRect(
            joinRoomMin,
            joinRoomMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    joinRoomHovered
                        ? 0.90f
                        : 0.55f)),
            Ui(6f));

        const string joinRoomText =
            "Join Room";

        var joinRoomTextSize =
            ImGui.CalcTextSize(
                joinRoomText);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                joinRoomMin.X +
                (buttonWidth -
                 joinRoomTextSize.X) *
                0.5f,
                joinRoomMin.Y +
                (buttonHeight -
                 joinRoomTextSize.Y) *
                0.5f),
            ImGui.GetColorU32(
                joinRoomHovered
                    ? Vector4.One
                    : MutedText),
            joinRoomText);

        if (newRoomHovered ||
            joinRoomHovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);
        }

        if (ImGui.IsMouseClicked(
                ImGuiMouseButton.Left))
        {
            if (newRoomHovered)
            {
                StartWatchParty(
                    goToPlayer: true);
            }
            else if (joinRoomHovered)
            {
                OpenWatchPartyPage();
            }
            else if (hovered)
            {
                OpenWatchPartyPage();
            }
        }
    }

    private static string FormatViewCount(
    long views)
    {
        if (views >= 1_000_000_000)
        {
            return $"{views / 1_000_000_000d:0.#}B views";
        }

        if (views >= 1_000_000)
        {
            return $"{views / 1_000_000d:0.#}M views";
        }

        if (views >= 1_000)
        {
            return $"{views / 1_000d:0.#}K views";
        }

        return $"{views:N0} views";
    }

    private static string FormatRelativeUploadDate(
    DateTimeOffset uploadDate)
    {
        var age =
            DateTimeOffset.UtcNow -
            uploadDate.ToUniversalTime();

        if (age.TotalDays < 1)
        {
            var hours =
                Math.Max(
                    1,
                    (int)age.TotalHours);

            return hours == 1
                ? "1 hour ago"
                : $"{hours} hours ago";
        }

        if (age.TotalDays < 30)
        {
            var days =
                Math.Max(
                    1,
                    (int)age.TotalDays);

            return days == 1
                ? "1 day ago"
                : $"{days} days ago";
        }

        if (age.TotalDays < 365)
        {
            var months =
                Math.Max(
                    1,
                    (int)(age.TotalDays / 30));

            return months == 1
                ? "1 month ago"
                : $"{months} months ago";
        }

        var years =
            Math.Max(
                1,
                (int)(age.TotalDays / 365));

        return years == 1
            ? "1 year ago"
            : $"{years} years ago";
    }

    private void DrawHomeHero(float maxHeroHeight = 220f)
    {
        var showArt = Plugin.Cfg.ShowHomeHeroImage;
        var avail = ImGui.GetContentRegionAvail().X;
        var gap = Ui(20f);
        var textWidth = showArt ? MathF.Min(avail * 0.48f, 420f) : avail;
        var artWidth = MathF.Max(avail - textWidth - gap, 220f);

        ImGui.BeginGroup();
        SetUiFontScale(1.75f);
        ImGui.TextUnformatted("Welcome to ");
        ImGui.SameLine(0, 0);
        ImGui.TextColored(Accent, "Alpha Channel");
        SetUiFontScale(1f);

        ImGui.Dummy(UiVec(0, 2));

        SetUiFontScale(1.15f);
        ImGui.TextColored(MutedText, "Cast. Watch. Together.");
        SetUiFontScale(1f);

        ImGui.Dummy(UiVec(0, 6));

        ImGui.PushTextWrapPos(ImGui.GetCursorPos().X + textWidth);
        ImGui.TextWrapped(
            "Bring your favourite videos into Eorzea. Create watch parties, " +
            "share screens, and enjoy moments together with friends wherever you are.");
        ImGui.PopTextWrapPos();

        ImGui.Dummy(UiVec(0, 18));

        var inviteHeight = 170f;
        var inviteWidth = ImGui.GetContentRegionAvail().X * 0.82f;

        var inviteOrigin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            inviteOrigin,
            inviteOrigin + new Vector2(inviteWidth, inviteHeight),
            ImGui.GetColorU32(new Vector4(CardBg.X, CardBg.Y, CardBg.Z, 0.45f)),
            14f);

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(16, 14)))
        using (var invite = ImRaii.Child(
    "##inviteFriends",
    new Vector2(inviteWidth, inviteHeight),
    false,
    ImGuiWindowFlags.NoBackground))
        {
            if (invite)
            {
                var imageSize = UiVec(56, 56);
                var imageOffset = UiVec(12, 12);

                var addFriendWrap = addFriendImage?.GetWrapOrDefault();

                if (addFriendWrap is not null)
                {
                    var imagePos = ImGui.GetCursorScreenPos() + imageOffset;

                    ImGui.GetWindowDrawList().AddImageRounded(
                        addFriendWrap.Handle,
                        imagePos,
                        imagePos + imageSize,
                        Vector2.Zero,
                        Vector2.One,
                        ImGui.GetColorU32(Vector4.One),
                        12f);
                }

                ImGui.Dummy(imageSize + imageOffset);
                ImGui.SameLine(0, 18);

                ImGui.BeginGroup();

                ImGui.Dummy(UiVec(0, 6));

                SetUiFontScale(1.3f);
                ImGui.TextUnformatted("Invite your friends to watch with you!");
                SetUiFontScale(1f);

                ImGui.TextColored(
    MutedText,
    "Add your friends to host watch parties, share virtual screens,\nand watch together in sync across Eorzea.");

                ImGui.Dummy(UiVec(0, 2));

                ImGui.SetNextItemWidth(inviteWidth - Ui(250));

                ImGui.InputTextWithHint(
                    "##friendName",
                    "Enter a friend's name...",
                    ref friendSearch,
                    64);

                ImGui.SameLine();

                using (ImRaii.PushColor(ImGuiCol.Button, Accent)
                           .Push(ImGuiCol.ButtonHovered, AccentHover)
                           .Push(ImGuiCol.ButtonActive, AccentActive)
                           .Push(ImGuiCol.Text, Vector4.One))
                {
                    using (ImRaii.PushColor(ImGuiCol.Button, Accent)
                               .Push(ImGuiCol.ButtonHovered, AccentHover)
                               .Push(ImGuiCol.ButtonActive, AccentActive)
                               .Push(ImGuiCol.Text, Vector4.One))
                    {
                        if (ImGui.Button("##addFriend", UiVec(120, 34)))
                        {
                            // Add friend action later
                        }

                        var buttonMin = ImGui.GetItemRectMin();
                        var buttonSize = ImGui.GetItemRectSize();

                        using (ImRaii.PushFont(UiBuilder.IconFont))
                        {
                            var icon = FontAwesomeIcon.UserPlus.ToIconString();
                            var iconSize = ImGui.CalcTextSize(icon);

                            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                                buttonMin + new Vector2(Ui(14), (buttonSize.Y - iconSize.Y) * 0.5f),
                                ImGui.GetColorU32(Vector4.One),
                                icon);
                        }

                        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
    buttonMin + UiVec(36, 7),
    ImGui.GetColorU32(Vector4.One),
    "Add Friend");

                        ImGui.Dummy(UiVec(0, 12));
                    }
                }

                ImGui.EndGroup();
            }
        }

        ImGui.EndGroup();
        var textHeight = ImGui.GetItemRectSize().Y;

        // Hero image is now drawn as a background layer.
    }

    private void DrawHomeHeroBackground(float height)
    {
        if (!Plugin.Cfg.ShowHomeHeroImage || homeHero is not { } texture)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();

        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();

        var width = MathF.Min(avail.X * 0.55f, 520f);
        var size = new Vector2(width, height);

        var position = origin + new Vector2(avail.X - width, 0);

        var (uv0, uv1) = CoverUvs(texture.Width, texture.Height, width, height);

        drawList.AddImageRounded(
            texture.Handle,
            position,
            position + size,
            uv0,
            uv1,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.45f)),
            14f);
    }
    private void DrawHomeHeroArt(float width, float height)
    {
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var size = new Vector2(width, height);

        if (homeHero is { } texture)
        {
            var (uv0, uv1) = CoverUvs(texture.Width, texture.Height, width, height);
            drawList.AddImageRounded(texture.Handle, origin, origin + size, uv0, uv1,
                ImGui.GetColorU32(Vector4.One), 14f);
            drawList.AddRect(origin, origin + size,
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.08f)), 14f, ImDrawFlags.None, 1f);
            ImGui.Dummy(size);
            return;
        }

        // Gradient fallback while the asset loads (or if it's missing).
        drawList.AddRectFilledMultiColor(origin, origin + size,
            ImGui.GetColorU32(new Vector4(0.12f, 0.08f, 0.22f, 1f)),
            ImGui.GetColorU32(new Vector4(0.25f, 0.10f, 0.28f, 1f)),
            ImGui.GetColorU32(new Vector4(0.08f, 0.14f, 0.32f, 1f)),
            ImGui.GetColorU32(new Vector4(0.05f, 0.08f, 0.18f, 1f)));
        drawList.AddRect(origin, origin + size, ImGui.GetColorU32(BorderSubtle), 14f);
        ImGui.Dummy(size);
    }

    // UV crop so the image fills the box (cover) without stretching.
    private static (Vector2 Uv0, Vector2 Uv1) CoverUvs(float texW, float texH, float boxW, float boxH)
    {
        if (texW <= 0 || texH <= 0 || boxW <= 0 || boxH <= 0)
        {
            return (Vector2.Zero, Vector2.One);
        }

        var texAspect = texW / texH;
        var boxAspect = boxW / boxH;
        if (texAspect > boxAspect)
        {
            var visible = boxAspect / texAspect;
            var pad = (1f - visible) * 0.5f;
            return (new Vector2(pad, 0f), new Vector2(1f - pad, 1f));
        }

        var visibleV = texAspect / boxAspect;
        var padV = (1f - visibleV) * 0.5f;
        return (new Vector2(0f, padV), new Vector2(1f, 1f - padV));
    }

    private void DrawHomeCapabilities()
    {
        var sectionTitle = "What do you want to do?";
        var titleSize = ImGui.CalcTextSize(sectionTitle);
        var lineY = ImGui.GetCursorScreenPos().Y + titleSize.Y * 0.5f;
        var availWidth = ImGui.GetContentRegionAvail().X;

        var drawList = ImGui.GetWindowDrawList();
        var lineColor = ImGui.GetColorU32(BorderSubtle);

        drawList.AddLine(
            new Vector2(ImGui.GetCursorScreenPos().X, lineY),
            new Vector2(
                ImGui.GetCursorScreenPos().X + (availWidth - titleSize.X) * 0.5f - Ui(12),
                lineY),
            lineColor,
            1f);

        drawList.AddLine(
            new Vector2(
                ImGui.GetCursorScreenPos().X + (availWidth + titleSize.X) * 0.5f + Ui(12),
                lineY),
            new Vector2(
                ImGui.GetCursorScreenPos().X + availWidth,
                lineY),
            lineColor,
            1f);

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() + (availWidth - titleSize.X) * 0.5f);

        ImGui.TextColored(Accent, sectionTitle);

        ImGui.SetCursorPosX(ImGui.GetStyle().WindowPadding.X);

        ImGui.Dummy(UiVec(0, 2));

        var avail = ImGui.GetContentRegionAvail().X;

        var gap = Ui(10f);
        var cardWidth = (avail - gap * 2) / 3f;

        var cardHeight = Ui(175f);
        var iconSize = Ui(72f);
        var titleY = Ui(36f);
        var bodyY = Ui(60f);
        var gapAfterTitle = Ui(6f);

        DrawCapabilityCard(
            cardWidth, cardHeight, iconSize, titleY, bodyY, gapAfterTitle,
            FontAwesomeIcon.SignInAlt,
            Hex(0xEF4444),
            "watch-videos.png",
"Watch Videos",
"Watch YouTube, Twitch, or any video link.",
"Start watching →",
() => currentPage = HomePage.Player);

        ImGui.SameLine(0, gap);

        DrawCapabilityCard(
            cardWidth, cardHeight, iconSize, titleY, bodyY, gapAfterTitle,
            FontAwesomeIcon.PlusSquare,
Hex(0xF59E0B),
"create-room.png",
"Create Room",
"Host your own room and invite friends.",
"Create your room →",
() => currentPage = HomePage.Player);

        ImGui.SameLine(0, gap);

        DrawCapabilityCard(
            cardWidth, cardHeight, iconSize, titleY, bodyY, gapAfterTitle,
            FontAwesomeIcon.SignInAlt,
Hex(0xEC4899),
"join-room.png",
"Join Room",
"Enter a friend's room and start watching.",
"Join a room →",
() => currentPage = HomePage.Player);

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 1);

        DrawCapabilityCard(
            cardWidth, cardHeight, iconSize, titleY, bodyY, gapAfterTitle,
            FontAwesomeIcon.Desktop,
Hex(0x8B5CF6),
"place-screen.png",
"Place a Screen",
"Move and resize your virtual screen.",
"Manage screen →",
() => currentPage = HomePage.Screen);

        ImGui.SameLine(0, gap);

        DrawCapabilityCard(
            cardWidth, cardHeight, iconSize, titleY, bodyY, gapAfterTitle,
            FontAwesomeIcon.UserFriends,
Hex(0x34D399),
"friends-list.png",
"Add Friends",
"Manage your friends and see who's online.",
"Friends List →",
() => currentPage = HomePage.Friends);

        ImGui.SameLine(0, gap);

        DrawCapabilityCard(
            cardWidth, cardHeight, iconSize, titleY, bodyY, gapAfterTitle,
            FontAwesomeIcon.Comment,
Hex(0x38BDF8),
"browse-apps.png",
"Alpha Chat",
"Open private messages and group chats with friends.",
"Open chat →",
() =>
{
    conversationsDirty = true;
    currentPage = HomePage.Messages;
});
    }

    // Fixed-size tile: background + hit target only claim layout; copy is DrawList-wrapped inside.
    private void DrawCapabilityCard(float width, float height, float iconSize, float titleY,
    float bodyY, float gapAfterTitle, FontAwesomeIcon icon, Vector4 color,
        string imageName, string title, string body, string actionText, Action onClick)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);

        if (ImGui.InvisibleButton($"##capHit{title}", size))
        {
            onClick();
        }

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBg), 14f);

        if (ImGui.IsItemHovered())
        {
            drawList.AddRectFilled(origin, origin + size,
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.04f)), 14f);
            drawList.AddRect(origin, origin + size,
                ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.55f)), 14f,
                ImDrawFlags.None, 1.5f);
        }

        var discOrigin = origin + UiVec(12f, 12f);


        var image = GetCapabilityImage(imageName);

        var imageWrap = image?.GetWrapOrDefault();

        if (imageWrap is not null)
        {
            var imageSize = 48f;

            drawList.AddImageRounded(
                imageWrap.Handle,
                discOrigin,
                discOrigin + new Vector2(imageSize, imageSize),
                Vector2.Zero,
                Vector2.One,
                ImGui.GetColorU32(Vector4.One),
                12f);
        }

        var wrapWidth = MathF.Max(40f, width - (12f + 48f + 20f));

        var lineH = ImGui.GetTextLineHeight();

        var textPos = origin + new Vector2(
            Ui(12f) + Ui(48f) + Ui(16f),
            Ui(18f));
        var titleBottom = DrawWrappedLines(drawList, textPos, wrapWidth, lineH, 2,
            ImGui.GetColorU32(Vector4.One), title);
        var bodyBottom = DrawWrappedLines(
            drawList,
            new Vector2(textPos.X, titleBottom + gapAfterTitle),
            wrapWidth,
            lineH,
            3,
            ImGui.GetColorU32(MutedText),
            body);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(origin.X + Ui(16), origin.Y + height - Ui(24)),
            ImGui.GetColorU32(color),
            actionText);
    }

    // Word-wrap into at most maxLines; returns Y just below the last drawn line.
    private static float DrawWrappedLines(ImDrawListPtr drawList, Vector2 pos, float wrapWidth,
        float lineHeight, int maxLines, uint color, string text)
    {
        var y = pos.Y;
        var linesDrawn = 0;
        var line = string.Empty;

        void Emit(string value)
        {
            if (linesDrawn >= maxLines || value.Length == 0)
            {
                return;
            }

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(pos.X, y), color, value);
            y += lineHeight;
            linesDrawn++;
        }

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (linesDrawn >= maxLines)
            {
                break;
            }

            var test = line.Length == 0 ? word : line + " " + word;
            if (ImGui.CalcTextSize(test).X <= wrapWidth)
            {
                line = test;
                continue;
            }

            if (line.Length == 0)
            {
                Emit(word);
                continue;
            }

            Emit(line);
            line = word;
        }

        Emit(line);
        return y;
    }

    private void DrawHomeHowItWorks()
    {
        ImGui.TextUnformatted("How it works");
        ImGui.Dummy(new Vector2(0, Ui(10f)));

        var avail = ImGui.GetContentRegionAvail().X;
        var gap = Ui(12f);
        var stepWidth = (avail - gap * 2) / 3f;

        DrawHowStep(stepWidth, 1, Accent, FontAwesomeIcon.UserPlus, "Invite Friends",
            "Add people, then host or join from Player.",
            () => currentPage = CurrentSession is null ? HomePage.Settings : HomePage.Friends);
        ImGui.SameLine(0, gap);
        DrawHowStep(stepWidth, 2, Hex(0xA78BFA), FontAwesomeIcon.Play, "Pick Something",
            "Paste a link or search YouTube / Twitch.",
            () =>
            {
                playerSourceTab = 0;
                currentPage = HomePage.Player;
            });
        ImGui.SameLine(0, gap);
        DrawHowStep(stepWidth, 3, Hex(0x34D399), FontAwesomeIcon.Heart, "Enjoy Together",
            "Everyone stays in sync on the screen.",
            () => currentPage = HomePage.Player);
    }

    private void DrawHowStep(float width, int number, Vector4 color, FontAwesomeIcon icon,
        string title, string body, Action onClick)
    {
        const float pad = 12f;
        const float badge = 24f;
        var badgeGap = Ui(10f);
        var titleGap = Ui(4f);

        // Full inner width for wrapped body — no side column stealing space.
        var wrapWidth = MathF.Max(40f, width - (pad * 2f));
        var titleWrap = MathF.Max(40f, wrapWidth - badge - badgeGap);
        var titleSize = ImGui.CalcTextSize(title, false, titleWrap);
        var bodySize = ImGui.CalcTextSize(body, false, wrapWidth);
        var headerH = MathF.Max(badge, titleSize.Y);
        var height = pad + headerH + titleGap + bodySize.Y + pad;

        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var size = new Vector2(width, height);

        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBg), 14f);

        var badgeCenter = origin + new Vector2(pad + badge * 0.5f, pad + headerH * 0.5f);
        drawList.AddCircleFilled(badgeCenter, badge * 0.5f, ImGui.GetColorU32(color));
        var num = number.ToString();
        var numSize = ImGui.CalcTextSize(num);
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), badgeCenter - numSize * 0.5f, ImGui.GetColorU32(Vector4.One), num);

        // Title to the right of the badge; body on the next row across the full card width.
        // PushTextWrapPos is window-local X (not screen).
        var titlePos = origin + new Vector2(pad + badge + badgeGap, pad + (headerH - titleSize.Y) * 0.5f);
        ImGui.SetCursorScreenPos(titlePos);
        ImGui.PushTextWrapPos(ImGui.GetCursorPos().X + titleWrap);
        ImGui.TextUnformatted(title);
        ImGui.PopTextWrapPos();

        var bodyPos = origin + new Vector2(pad, pad + headerH + titleGap);
        ImGui.SetCursorScreenPos(bodyPos);
        ImGui.PushTextWrapPos(ImGui.GetCursorPos().X + wrapWidth);
        ImGui.TextColored(MutedText, body);
        ImGui.PopTextWrapPos();

        // Soft icon accent in the top-right corner (doesn't fight title layout).
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                origin + new Vector2(width - pad - glyphSize.X, pad),
                ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.35f)),
                glyph);
        }

        ImGui.SetCursorScreenPos(origin);
        if (ImGui.InvisibleButton($"##howHit{number}", size))
        {
            onClick();
        }

        if (ImGui.IsItemHovered())
        {
            drawList.AddRect(origin, origin + size,
                ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.45f)), 14f,
                ImDrawFlags.None, 1.5f);
        }
    }

    private static void DrawAvatarStack(ParticipantInfo[] participants, int maxShown)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        const float radius = 12f;
        const float overlap = 16f;
        var shown = Math.Min(participants.Length, maxShown);
        for (var index = 0; index < shown; index++)
        {
            var center = origin + new Vector2(radius + index * overlap, radius);
            drawList.AddCircleFilled(center, radius + 1.5f, ImGui.GetColorU32(WindowBg));
            drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(AvatarPalette[index % AvatarPalette.Length]));
        }

        ImGui.Dummy(new Vector2(radius * 2 + Math.Max(0, shown - 1) * overlap, radius * 2));
        if (participants.Length > maxShown)
        {
            ImGui.SameLine();
            ImGui.TextColored(MutedText, $"+{participants.Length - maxShown}");
        }
    }

    private void DoJoin(
        string hostName, string? password = null, string? visibleHostName = null)
    {
        if (hostName.Length == 0)
        {
            return;
        }

        if (CurrentSession is null)
        {
            joinError = "Sign in to join a watch party.";
            Plugin.ChatGui.Print("[AlphaChannel] Sign in before joining a watch party.");
            return;
        }

        //
        // This is a new room, so its content may offer the TV prompt once.
        //
        ResetViewerTvSpawnPrompt();

        clearQueueWhenJoined =
            true;

        joinedHostDisplayName =
            string.IsNullOrWhiteSpace(visibleHostName)
                ? hostName.Trim()
                : visibleHostName.Trim();

        gameplayStreamOfferDismissed =
    false;

        _ = stream.JoinAsync(
            hostName.Trim(),
            string.IsNullOrWhiteSpace(password) ? null : password.Trim());

        if (!stream.IsConnected)
        {
            joinError = "Connecting to the relay…";
        }
    }

    private static string ActivityLabel(ActivityEventDto item) => item.Type switch
    {
        "StartedWatching" => $"{item.ActorDisplayName} started watching",
        "JoinedWatchAlong" => item.Metadata is { Length: > 0 }
            ? $"{item.ActorDisplayName} joined {item.Metadata}'s watch-along"
            : $"{item.ActorDisplayName} joined a watch-along",
        "FriendAccepted" => $"{item.ActorDisplayName} accepted a friend request",
        "VenueSaved" => item.Metadata is { Length: > 0 }
            ? $"{item.ActorDisplayName} saved a venue: {item.Metadata}"
            : $"{item.ActorDisplayName} saved a venue",
        "WentLive" => $"{item.ActorDisplayName} went live",
        _ => $"{item.ActorDisplayName}: {item.Type}",
    };
}
