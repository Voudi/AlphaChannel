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
    private int homeVideoColumnCount = 5;

    private const int FavouritePageSize = 20;
    private int favouriteVideosVisibleCount = FavouritePageSize;


    private readonly HashSet<string> temporaryYouTubeSubscriptions =
        new(StringComparer.OrdinalIgnoreCase);

    private Vector2 homeSearchInputPos;
    private ISharedImmediateTexture? addFriendImage;
    private readonly Dictionary<string, ISharedImmediateTexture?> capabilityImages = new();

    // Constant spacing on right side
    private const float HomeContentRightInset = 18f;



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
        const float rowHeight = 34f;

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
                    : 44f));

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
                        : 44f),
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
                8f,
                subtitle is null
                    ? 9f
                    : 13f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                iconPos,
                ImGui.GetColorU32(
                    hovered
                        ? AccentHover
                        : Accent),
                icon.ToIconString());
        }

        drawList.AddText(
            origin +
            new Vector2(
                31f,
                subtitle is null
                    ? 8f
                    : 6f),
            ImGui.GetColorU32(Vector4.One),
            title);

        if (subtitle is not null)
        {
            var displaySubtitle =
                subtitle.Length > 62
                    ? subtitle[..59] + "..."
                    : subtitle;

            drawList.AddText(
                origin +
                new Vector2(
                    31f,
                    24f),
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
        const float contentPaddingLeft = 24f;

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() + contentPaddingLeft);

        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing,
            new Vector2(
                ImGui.GetStyle().ItemSpacing.X,
                ImGui.GetStyle().ItemSpacing.Y));

        var searchWidth =
    ImGui.GetContentRegionAvail().X < 600f
        ? 330f
        : 430f;

        if (!homeYouTubeRequested)
        {
            homeYouTubeRequested = true;
            isLoadingHomeYouTube = true;

            _ = LoadHomeYouTubeAsync();
        }

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
                    searchMax.Y + 6f);


            var drawList =
                ImGui.GetForegroundDrawList();

            var popupSize =
                new Vector2(
                    searchWidth,
                    looksLikeUrl ? 108f : 142f);

            drawList.AddRectFilled(
                popupPos,
                popupPos + popupSize,
                ImGui.GetColorU32(CardBg),
                10f);

            var textPos = popupPos + new Vector2(16f, 14f);

            string suggestionText;

            if (looksLikeUrl)
            {
                suggestionText = "▶  Play video";
            }
            else
            {
                suggestionText = $"▶  Search \"{trimmedSearch}\" on YouTube";
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
        $"▶  Search \"{trimmedSearch}\" on YouTube",
        $"▶  Search \"{trimmedSearch}\" on Dailymotion",
        $"▶  Find Twitch channel \"{trimmedSearch}\""
                };

            for (int i = 0; i < rows.Length; i++)
            {
                var rowMin =
                    popupPos +
                    new Vector2(
                        8f,
                        8f + i * (rowHeight + rowSpacing));

                var rowMax =
                    rowMin +
                    new Vector2(
                        searchWidth - 16f,
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

                drawList.AddText(
                    rowMin + new Vector2(12f, 8f),
                    ImGui.GetColorU32(Vector4.One),
                    rows[i]);

                if (looksLikeUrl)
                {
                    var displayUrl =
                        trimmedSearch.Length > 62
                            ? trimmedSearch[..59] + "..."
                            : trimmedSearch;

                    drawList.AddText(
                        rowMin + new Vector2(12f, 24f),
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
                                // YouTube
                                OpenPlayerSearch(
                                    1,
                                    trimmedSearch);
                                break;

                            case 1:
                                // Dailymotion
                                OpenPlayerSearch(
                                    3,
                                    trimmedSearch);
                                break;

                            case 2:
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
                }
            }

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                !searchActive)
            {
                homeSearchPopupOpen = false;
            }
        }



        ImGui.Dummy(new Vector2(0f, -6f));


        // ---------------------------------------------------------
        // Header row
        // ---------------------------------------------------------

        var contentWidth = ImGui.GetContentRegionAvail().X;
        var startX = ImGui.GetCursorPosX();
        var headerY = ImGui.GetCursorPosY();

        var showWelcome = contentWidth >= 900f;
        var showWatchers = contentWidth >= 500f;


        // ---------------------------------------------------------
        // Left: Welcome text
        // ---------------------------------------------------------
        if (showWelcome)
        {
            ImGui.SetCursorPos(
            new Vector2(
                startX + 25f,
                headerY + 6f));

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    Accent,
                    FontAwesomeIcon.Users.ToIconString());
            }

            ImGui.SameLine(0, 8);

            ImGui.SetWindowFontScale(1.08f);

            ImGui.TextColored(
                MutedText,
                "Welcome to ");

            ImGui.SameLine(0, 0);

            ImGui.TextColored(
                Accent,
                "Alpha Channel");

            ImGui.SetWindowFontScale(1f);
        }


        // ---------------------------------------------------------
        // Centre: Search bar
        // ---------------------------------------------------------

        //
        // At full width, keep the search bar centered.
        //
        // When "Welcome to Alpha Channel" disappears, move the
        // search bar left into the space that Welcome was using.
        //

        var searchX =
            showWelcome
                ? startX +
                  (contentWidth - searchWidth) *
                  0.5f
                : startX + 25f;


        ImGui.SetCursorPos(
            new Vector2(
                searchX,
                headerY));


        ImGui.SetNextItemWidth(
            searchWidth);



        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            18f))
        {
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FramePadding,
                new Vector2(36f, 9f)))
            {
                homeSearchInputPos =
                    ImGui.GetCursorScreenPos();

                ImGui.InputTextWithHint(
                    "##homeSearch",
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
            new Vector2(2f, 2f);

        var iconAreaMax =
            homeSearchInputPos +
            new Vector2(
                35f,
                searchHeight - 2f);

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
            new Vector2(14f, 7f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            searchDrawList.AddText(
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

            const float avatarSize = 38f;
            const float profileWidth = 185f;


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
                    profileY + 17f));


            //
            // Friends are deliberately the quieter secondary status.
            //

            ImGui.SetWindowFontScale(
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
                profileY + 35f));


            //
            // Watcher activity is the strongest secondary status in this
            // profile block, so give it a little more size and live color.
            //

            ImGui.SetWindowFontScale(
                1.02f);


            ImGui.TextColored(
                Good,
                watchersText);


            ImGui.SetWindowFontScale(
                1f);
        }

        ImGui.SetWindowFontScale(1f);
        ImGui.Dummy(new Vector2(0f, 2f));

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

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));

        DrawHomeYouTubeShelf();

        ImGui.Dummy(
            new Vector2(
                0f,
                2f));

        DrawWatchPartiesShelf();

        if (Plugin.Cfg.ShowFfxivYouTubeSection)
        {
            ImGui.Dummy(new Vector2(0f, 24f));
            DrawFfxivYouTubeShelf();
        }

        ImGui.Dummy(new Vector2(0f, 24f));

        DrawRecentlyWatchedShelf();
        ImGui.PopStyleVar();
    }

    private int GetHomeVideoColumnCount(float windowWidth)
    {
        switch (homeVideoColumnCount)
        {
            case 5:
                if (windowWidth < 780f)
                    homeVideoColumnCount = 4;
                break;

            case 4:
                if (windowWidth >= 900f)
                    homeVideoColumnCount = 5;
                else if (windowWidth < 560f)
                    homeVideoColumnCount = 3;
                break;

            case 3:
                if (windowWidth >= 720f)
                    homeVideoColumnCount = 4;
                break;
        }

        return homeVideoColumnCount;
    }

    private void DrawHomeYouTubeShelf()
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        var headingPos = ImGui.GetCursorScreenPos();

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                new Vector4(
                    1f,
                    0.25f,
                    0.35f,
                    1f),
                FontAwesomeIcon.Fire.ToIconString());
        }

        ImGui.SameLine(0f, 8f);

        ImGui.Text("Trending on YouTube");

        ImGui.SameLine(0f, 8f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                MutedText,
                FontAwesomeIcon.InfoCircle.ToIconString());
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Adjust your trending video topics in settings.");
        }



        // Refresh icon right side
        ImGui.SameLine();

        ImGui.SetCursorPosX(
    ImGui.GetWindowContentRegionMax().X -
    HomeContentRightInset -
    22f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var refreshHovered =
                ImGui.IsMouseHoveringRect(
                    ImGui.GetCursorScreenPos(),
                    ImGui.GetCursorScreenPos() +
                    ImGui.CalcTextSize(
                        FontAwesomeIcon.Sync.ToIconString()));

            ImGui.TextColored(
                refreshHovered
                    ? AccentHover
                    : MutedText,
                FontAwesomeIcon.Sync.ToIconString());
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Refresh trending videos");

            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);
        }

        if (ImGui.IsItemClicked())
        {
            homeYouTubeResults = null;
            isLoadingHomeYouTube = true;

            _ = LoadHomeYouTubeAsync(true);
        }

        // ---------------------------------------------------------
        // Loading / unavailable state
        // ---------------------------------------------------------

        if (homeYouTubeResults is not { Count: > 0 } results)
        {
            if (isLoadingHomeYouTube)
            {
                DrawMediaHubLoadingCards(
                    224f);
            }
            else
            {
                DrawMediaHubShelfCards(
                    224f);
            }

            return;
        }

        // ---------------------------------------------------------
        // Real results
        // ---------------------------------------------------------

        var windowWidth = ImGui.GetWindowSize().X;

        var cardCount =
      GetHomeVideoColumnCount(windowWidth);

        const float gap = 12f;
        const float cardHeight = 224f;

        var visibleCount =
            Math.Min(cardCount, results.Count);

        var cardWidth =
            (width - gap * (cardCount - 1)) /
            cardCount;

        for (var index = 0;
             index < visibleCount;
             index++)
        {
            if (index > 0)
            {
                ImGui.SameLine(0f, gap);
            }

            ImGui.PushID($"homeYoutube_{index}");

            DrawHomeYouTubeCard(
                results[index],
                cardWidth,
                cardHeight);

            ImGui.PopID();
        }
    }

    private void DrawHomeShelfHeading(
     FontAwesomeIcon icon,
     string title,
     Vector4 iconColor,
     bool showSeeAll = true,
     bool addBottomSpacing = true)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        const float iconGap = 8f;

        Vector2 iconSize;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph =
                icon.ToIconString();

            iconSize =
                ImGui.CalcTextSize(glyph);

            drawList.AddText(
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

        ImGui.SetWindowFontScale(1.08f);

        ImGui.TextUnformatted(title);

        ImGui.SetWindowFontScale(1f);

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

            const float chevronGap = 7f;

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
                    origin.Y + ImGui.GetTextLineHeight() + 6f);

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

            drawList.AddText(
                new Vector2(
                    seeAllX,
                    origin.Y + 3f),
                ImGui.GetColorU32(seeAllColor),
                seeAll);

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                drawList.AddText(
                    new Vector2(
                        seeAllX +
                        seeAllSize.X +
                        chevronGap,
                        origin.Y + 2f),
                    ImGui.GetColorU32(seeAllColor),
                    FontAwesomeIcon.ChevronRight.ToIconString());
            }
        }
        if (addBottomSpacing)
        {
            ImGui.Dummy(
                new Vector2(
                    0f,
                    5f));
        }
    }


    private void DrawMediaHubShelfCards(
      float cardHeight)
    {
        var width = ImGui.GetContentRegionAvail().X;

        var itemCount =
            GetHomeVideoColumnCount(ImGui.GetWindowSize().X);

        const float gap = 10f;

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

        const float gap = 10f;

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

        const float thumbnailHeight = 116f;

        // Thumbnail skeleton.
        drawList.AddRectFilled(
            origin,
            origin + new Vector2(
                width,
                thumbnailHeight),
            ImGui.GetColorU32(CardBg),
            9f);

        // ---------------------------------------------------------
        // Animated spinner
        // ---------------------------------------------------------

        var center =
            origin +
            new Vector2(
                width * 0.5f,
                thumbnailHeight * 0.5f);

        const float radius = 14f;

        var rotation =
            (float)ImGui.GetTime() * 4.5f;


        // Soft purple glow behind spinner.
        drawList.AddCircle(
            center,
            radius,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.12f)),
            48,
            5f);

        // Dim full ring behind the active arc.
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
            2.5f);

        // Rotating 270-degree accent arc.
        drawList.PathArcTo(
            center,
            radius,
            rotation,
            rotation + MathF.PI * 1.5f,
            24);

        drawList.PathStroke(
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.95f)),
            ImDrawFlags.None,
            3.2f);

        // Skeleton title lines beneath the thumbnail.
        drawList.AddRectFilled(
            origin + new Vector2(
                2f,
                thumbnailHeight + 10f),
            origin + new Vector2(
                width * 0.78f,
                thumbnailHeight + 14f),
            ImGui.GetColorU32(
                new Vector4(
                    1f,
                    1f,
                    1f,
                    0.18f)),
            2f);

        drawList.AddRectFilled(
            origin + new Vector2(
                2f,
                thumbnailHeight + 25f),
            origin + new Vector2(
                width * 0.58f,
                thumbnailHeight + 29f),
            ImGui.GetColorU32(
                new Vector4(
                    1f,
                    1f,
                    1f,
                    0.10f)),
            2f);

        drawList.AddRectFilled(
            origin + new Vector2(
                2f,
                thumbnailHeight + 44f),
            origin + new Vector2(
                width * 0.40f,
                thumbnailHeight + 47f),
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.14f)),
            2f);
    }

    private static string? GetYouTubeVideoId(
    string url)
    {
        var videoId =
            VideoId.TryParse(url);

        return videoId?.Value;
    }

    private void DrawHomeYouTubeCard(
        VideoSearchEntry result,
        float width,
        float height)
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

        const float thumbnailHeight = 116f;

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
                thumbnailHeight - 22f),
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
                new Vector2(
                    7f,
                    6f);

            var badgeMax =
                badgeMin +
                new Vector2(
                    badgeSize.X + 10f,
                    badgeSize.Y + 5f);

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

            drawList.AddText(
                badgeMin +
                new Vector2(
                    5f,
                    2f),
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
                new Vector2(
                    7f,
                    6f);

            var badgeMax =
                badgeMin +
                new Vector2(
                    dateSize.X + 10f,
                    dateSize.Y + 5f);

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

            drawList.AddText(
                badgeMin +
                new Vector2(
                    5f,
                    2f),
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
                    10f,
                    origin.Y +
                    thumbnailHeight -
                    20f);

            var badgeMax =
                new Vector2(
                    origin.X +
                    width -
                    4f,
                    origin.Y +
                    thumbnailHeight -
                    4f);

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

            drawList.AddText(
                new Vector2(
                    badgeMin.X + 3f,
                    badgeMin.Y +
                    ((badgeMax.Y - badgeMin.Y) - durationSize.Y) * 0.5f),
                ImGui.GetColorU32(Vector4.One),
                durationText);
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

                const float progressHeight =
                    3f;

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
     origin.Y + thumbnailHeight + 10f;


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

        var channelY =
     titleY +
     (lineHeight * 2f) +
     5f;

        var userIcon =
            FontAwesomeIcon.User.ToIconString();

        float iconWidth;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconWidth =
                ImGui.CalcTextSize(userIcon).X;

            drawList.AddText(
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

        const float subscribeButtonSize = 24f;

        var subscribeMin =
            new Vector2(
                origin.X + width - subscribeButtonSize - 2f,
                channelY - 2f);

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

        drawList.AddText(
            new Vector2(
                textX + iconWidth + 5f,
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
        textX + iconWidth + 5f,
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

            drawList.AddText(
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

        if (result.ViewCount is { } views)
        {
            drawList.AddText(
                new Vector2(
                    textX,
                    channelY +
                    lineHeight +
                    3f),
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

        if (hovered)
        {
            // Darken the thumbnail so the controls stand out.
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

            const float favouriteButtonSize = 27f;

            var favouriteMin =
                new Vector2(
                    origin.X + width - favouriteButtonSize - 7f,
                    origin.Y + 7f);

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

                drawList.AddText(
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

            const float buttonGap = 6f;
            const float buttonHeight = 28f;

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
                    origin.X + 8f,
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
                drawList.AddText(
                    new Vector2(
                        playStartX,
                        playMin.Y +
                        (buttonHeight - playGlyphSize.Y) * 0.5f),
                    ImGui.GetColorU32(Vector4.One),
                    FontAwesomeIcon.Play.ToIconString());
            }

            drawList.AddText(
                new Vector2(
                    playStartX +
                    playGlyphSize.X +
                    6f,
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
                drawList.AddText(
                    new Vector2(
                        queueStartX,
                        queueMin.Y +
                        (buttonHeight - queueGlyphSize.Y) * 0.5f),
                    ImGui.GetColorU32(Vector4.One),
                    FontAwesomeIcon.Plus.ToIconString());
            }

            drawList.AddText(
                new Vector2(
                    queueStartX +
                    queueGlyphSize.X +
                    6f,
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
                        if (isFavourite)
                        {
                            Plugin.Cfg.FavouriteYouTubeVideoIds.RemoveAll(
                                id => string.Equals(
                                    id,
                                    favouriteVideoId,
                                    StringComparison.OrdinalIgnoreCase));
                        }
                        else
                        {
                            Plugin.Cfg.FavouriteYouTubeVideoIds.RemoveAll(
                                id => string.Equals(
                                    id,
                                    favouriteVideoId,
                                    StringComparison.OrdinalIgnoreCase));

                            Plugin.Cfg.FavouriteYouTubeVideoIds.Insert(
                                0,
                                favouriteVideoId);
                        }

                        Plugin.Cfg.Save();
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

            if (mouse.Y < actionAreaTop ||
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
        const float height = 260f;
        const float rounding = 14f;

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

        const float dotGap = 15f;
        const float dotRadius = 3.5f;
        const float activePillWidth = 13f;

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
                new Vector2(7f, 7f);

            var hitMax =
                dotCenter +
                new Vector2(7f, 7f);

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
                        dotY - 2.5f),
                    new Vector2(
                        x + activePillWidth * 0.5f,
                        dotY + 2.5f),
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
        drawList.AddText(
    new Vector2(
        textX,
        origin.Y + 35f),
    WithAlpha(
        AccentHover,
        contentAlpha),
    "FEATURED");
        // ---------------------------------------------------------
        // Eyebrow
        // ---------------------------------------------------------

        drawList.AddText(
            new Vector2(
                textX,
               origin.Y + 55f),
            WithAlpha(
                AccentHover,
                contentAlpha),
            viewText);

        // ---------------------------------------------------------
        // Title
        // ---------------------------------------------------------

        var savedCursor =
            ImGui.GetCursorScreenPos();

        ImGui.SetWindowFontScale(1.55f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                textX,
               origin.Y + 95f));

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

        ImGui.SetWindowFontScale(1f);

        // ---------------------------------------------------------
        // Channel / category
        // ---------------------------------------------------------

        ImGui.SetCursorScreenPos(
            new Vector2(
                textX,
                origin.Y + 165f));

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
            new Vector2(
                118f,
                36f);

        var togetherMin =
            new Vector2(
                watchMax.X + 8f,
                buttonY);

        var togetherMax =
            togetherMin +
            new Vector2(
                142f,
                36f);

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
            drawList.AddText(
                watchMin +
                new Vector2(
                    15f,
                    10f),
                WithAlpha(
                    Vector4.One,
                    contentAlpha),
                FontAwesomeIcon.Play
                    .ToIconString());
        }

        drawList.AddText(
            watchMin +
            new Vector2(
                38f,
                9f),
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
            drawList.AddText(
                togetherMin +
                new Vector2(
                    14f,
                    10f),
                WithAlpha(
                    Vector4.One,
                    contentAlpha),
                FontAwesomeIcon.UserFriends
                    .ToIconString());
        }

        drawList.AddText(
            togetherMin +
            new Vector2(
                38f,
                9f),
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

        ImGui.SetWindowFontScale(1.08f);
        ImGui.TextUnformatted(title);
        ImGui.SetWindowFontScale(1f);

        var seeAll = "See all  >";
        var seeAllWidth = ImGui.CalcTextSize(seeAll).X;

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            ImGui.GetWindowContentRegionMax().X -
            seeAllWidth);

        ImGui.TextColored(
            AccentHover,
            seeAll);

        ImGui.Dummy(new Vector2(0f, 5f));

        // ---------------------------------------------------------
        // Placeholder cards
        // ---------------------------------------------------------

        const float gap = 10f;

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

    private static void DrawMediaHubPlaceholderCard(
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
            origin + new Vector2(10f, thumbnailHeight + 9f),
            origin + new Vector2(
                width * 0.72f,
                thumbnailHeight + 13f),
            ImGui.GetColorU32(
                new Vector4(1f, 1f, 1f, 0.34f)),
            2f);

        if (height >= 90f)
        {
            drawList.AddRectFilled(
                origin + new Vector2(10f, thumbnailHeight + 21f),
                origin + new Vector2(
                    width * 0.48f,
                    thumbnailHeight + 24f),
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

        const float gap = 10f;
        const float rowGap = 14f;
        const float cardHeight = 174f;

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
                    hideTextSize.Y) + 4f);

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

        ImGui.Dummy(
            new Vector2(0f, 5f));

        // ---------------------------------------------------------
        // Results
        // ---------------------------------------------------------

        var cardWidth =
            (width - gap * (columns - 1)) /
            columns;

        if (ffxivYouTubeResults is not { Count: > 0 } results)
        {
            for (var index = 0;
                 index < 10;
                 index++)
            {
                if (index > 0)
                {
                    if (index % columns == 0)
                    {
                        ImGui.Dummy(
                            new Vector2(
                                0f,
                                rowGap));
                    }
                    else
                    {
                        ImGui.SameLine(
                            0f,
                            gap);
                    }
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
                10,
                results.Count);

        for (var index = 0;
             index < visibleCount;
             index++)
        {
            if (index > 0)
            {
                if (index % columns == 0)
                {
                    ImGui.Dummy(
                        new Vector2(0f, rowGap));
                }
                else
                {
                    ImGui.SameLine(
                        0f,
                        gap);
                }
            }

            ImGui.PushID(
                $"ffxivYoutube_{index}");

            DrawHomeYouTubeCard(
                results[index],
                cardWidth,
                cardHeight);

            ImGui.PopID();
        }
    }

    private void DrawRecentlyWatchedShelf()
    {
        DrawHomeShelfHeading(
            FontAwesomeIcon.History,
            "Recently Watched",
            Accent,
            false);

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
            drawList.AddText(
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
            Plugin.Cfg.RecentlyWatchedVideos;

        if (videos is not { Count: > 0 })
        {
            DrawMediaHubShelfCards(
                224f);

            return;
        }


        var cardCount =
            GetHomeVideoColumnCount(
                ImGui.GetWindowSize().X);

        const float gap = 12f;
        const float cardHeight = 190f;


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


            const float thumbnailHeight = 116f;


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


                const float progressHeight = 3f;


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
                    2f,
                    thumbnailHeight + 10f),
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

        const float thumbnailHeight = 92f;

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

        const float progressHeight = 3f;

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
                new Vector2(
                    7f,
                    6f);

            var badgeMax =
                badgeMin +
                new Vector2(
                    badgeSize.X + 10f,
                    badgeSize.Y + 5f);

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

            drawList.AddText(
                badgeMin +
                new Vector2(
                    5f,
                    2f),
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

            const float buttonHeight =
                28f;

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
                    8f);

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

            drawList.AddText(
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

        drawList.AddText(
            origin +
            new Vector2(
                2f,
                thumbnailHeight + 8f),
            ImGui.GetColorU32(
                Vector4.One),
            displayTitle);
    }

    private void DrawWatchPartiesShelf()
    {
        const float gap = 10f;
        const float cardHeight = 238f;
        const int cardCount = 4;

        var width =
            ImGui.GetContentRegionAvail().X;

        DrawHomeShelfHeading(
            FontAwesomeIcon.Users,
            "Watch Parties [IGNORE SECTION - UNDER CONSTRUCTION]",
            AccentHover);

        var cardWidth =
            (width - gap * (cardCount - 1)) /
            cardCount;

        DrawWatchPartyCard(
            "Limsa Lounge",
            "LOTR Movie Marathon!",
            "WarriorOfMight",
            "Plot 5 Ward 6 — The Goblet",
            "7 watching",
                    null,
            FontAwesomeIcon.Play,
            true,
            false,
            cardWidth,
            cardHeight);

        ImGui.SameLine(0f, gap);

        DrawWatchPartyCard(
            "Lala Theatre",
            "Watching: Endwalker Trailer",
            "Y'shtola",
            "Shirogane — Empyreum Apartments",
            "2 watching",
                    "roombg1.png",
            FontAwesomeIcon.Video,
            false,
            false,
            cardWidth,
            cardHeight);

        ImGui.SameLine(0f, gap);

        DrawWatchPartyCard(
            "Chocobo Club",
            "anime nightt booooiis",
            "Alphinaud",
            "Central Shroud",
            "1 watching",
                    "roombg2.png",
            FontAwesomeIcon.Film,
            false,
            true,
            cardWidth,
            cardHeight);

        ImGui.SameLine(0f, gap);

        DrawCreateWatchPartyCard(
            cardWidth,
            cardHeight);
    }

    const float thumbHeight = 92f;
    private void DrawWatchPartyCard(
     string title,
     string contentText,
     string hostName,
     string locationText,
     string watcherText,
     string? imageName,
     FontAwesomeIcon categoryIcon,
     bool featured,
     bool locked,
     float width,
     float height)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(width, height);

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.InvisibleButton(
            $"##watchParty_{title}",
            size);

        var hovered =
            ImGui.IsItemHovered();

        // ---------------------------------------------------------
        // Card background
        // ---------------------------------------------------------

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                hovered
                    ? CardBgHover
                    : CardBg),
            10f);

        // ---------------------------------------------------------
        // Artwork
        // ---------------------------------------------------------

        IDalamudTextureWrap? imageWrap = null;

        if (imageName is null)
        {
            if (homeHero is { } hero)
            {
                imageWrap = hero;
            }
        }
        else
        {
            imageWrap =
                GetCapabilityImage(imageName)?
                    .GetWrapOrDefault();
        }

        if (imageWrap is not null)
        {
            var (uv0, uv1) =
                CoverUvs(
                    imageWrap.Width,
                    imageWrap.Height,
                    width,
                    thumbHeight);

            drawList.AddImageRounded(
                imageWrap.Handle,
                origin,
                origin +
                new Vector2(
                    width,
                    thumbHeight),
                uv0,
                uv1,
                uint.MaxValue,
                10f,
                ImDrawFlags.RoundCornersTop);

            drawList.AddRectFilledMultiColor(
     origin,
     origin + size,
     ImGui.GetColorU32(
         new Vector4(0.01f, 0.01f, 0.02f, 0.95f)),
     ImGui.GetColorU32(
         new Vector4(0.01f, 0.01f, 0.02f, 0.05f)),
     ImGui.GetColorU32(
         new Vector4(0.01f, 0.01f, 0.02f, 0.05f)),
     ImGui.GetColorU32(
         new Vector4(0.01f, 0.01f, 0.02f, 0.70f)));

            drawList.AddRectFilled(
                origin,
                origin +
                new Vector2(
                    width,
                    thumbHeight),
                ImGui.GetColorU32(
                    new Vector4(
                        0f,
                        0f,
                        0f,
                        hovered ? 0.08f : 0.15f)),
                10f,
                ImDrawFlags.RoundCornersTop);
        }
        else
        {
            drawList.AddRectFilled(
                origin,
                origin +
                new Vector2(
                    width,
                    thumbHeight),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.08f)),
                10f,
                ImDrawFlags.RoundCornersTop);
        }

        // ---------------------------------------------------------
        // Watcher badge on thumbnail
        // ---------------------------------------------------------

        var watcherSize =
            ImGui.CalcTextSize(watcherText);

        var watcherMin =
            origin +
            new Vector2(
                8f,
                8f);

        var watcherMax =
            watcherMin +
            new Vector2(
                watcherSize.X + 12f,
                watcherSize.Y + 6f);

        drawList.AddRectFilled(
            watcherMin,
            watcherMax,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.65f)),
            5f);

        drawList.AddText(
            watcherMin +
            new Vector2(
                6f,
                3f),
            ImGui.GetColorU32(Vector4.One),
            watcherText);

        // ---------------------------------------------------------
        // Featured badge
        // ---------------------------------------------------------

        if (featured)
        {
            const string badgeText =
                "FEATURED";

            var badgeSize =
                ImGui.CalcTextSize(badgeText);

            var badgeMin =
    origin +
    new Vector2(
        width - badgeSize.X - 17f,
        6f);

            var badgeMax =
                badgeMin +
                new Vector2(
                    badgeSize.X + 10f,
                    badgeSize.Y + 5f);

            drawList.AddRectFilled(
                badgeMin,
                badgeMax,
                ImGui.GetColorU32(Accent),
                5f);

            drawList.AddText(
                badgeMin +
                new Vector2(5f, 2f),
                ImGui.GetColorU32(Vector4.One),
                badgeText);
        }

        // ---------------------------------------------------------
        // Participant avatars
        // ---------------------------------------------------------

        var avatarY =
            origin.Y + thumbHeight - 28f;

 

        const int maxVisibleAvatars = 4;

        var participantCount =
            locked
                ? 1
                : featured
                    ? 7
                    : 2;

        var visibleAvatars =
            Math.Min(participantCount, maxVisibleAvatars);

        var extraAvatars =
            participantCount - visibleAvatars;

        const float avatarSize = 22f;
        const float avatarOverlap = 16f;

        var stackWidth =
            avatarSize +
            (visibleAvatars - 1) * avatarOverlap +
            (extraAvatars > 0 ? avatarSize : 0f);

        var avatarX =
            origin.X + width - stackWidth - 10f;


        // For these placeholder rooms, reuse the current user's actual
        // profile avatar. Real room participants can replace these later.
        var avatarIcon =
            CurrentSession?.AvatarIcon;

        var avatarColor =
            CurrentSession?.AvatarColorHex ??
            "#9966FA";

        var avatarImage =
            CurrentSession?.AvatarImageUrl;



        for (var i = 0; i < visibleAvatars; i++)
        {
            var avatarPos =
                new Vector2(
                    avatarX + i * avatarOverlap,
                    avatarY);

            // Small dark rim around each overlapping portrait.
            drawList.AddCircleFilled(
                avatarPos +
                new Vector2(
                    avatarSize * 0.5f,
                    avatarSize * 0.5f),
                avatarSize * 0.5f + 1.5f,
                ImGui.GetColorU32(CardBg));

            ImGui.SetCursorScreenPos(
                avatarPos);

            ImGui.PushID(
                $"roomAvatar_{title}_{i}");

            DrawAvatarChip(
                avatarIcon,
                avatarColor,
                avatarSize,
                avatarImage);

            ImGui.PopID();
        }

        if (extraAvatars > 0)
        {
            var plusX =
                avatarX +
                visibleAvatars * avatarOverlap;

            var plusPos =
                new Vector2(
                    plusX,
                    avatarY);

            drawList.AddCircleFilled(
                plusPos +
                new Vector2(
                    avatarSize * 0.5f,
                    avatarSize * 0.5f),
                avatarSize * 0.5f,
                ImGui.GetColorU32(CardBgHover));

            var plusText =
                $"+{extraAvatars}";

            var textSize =
                ImGui.CalcTextSize(plusText);

            drawList.AddText(
                plusPos +
                new Vector2(
                    (avatarSize - textSize.X) * 0.5f,
                    (avatarSize - textSize.Y) * 0.5f),
                ImGui.GetColorU32(Vector4.One),
                plusText);
        }



        // ---------------------------------------------------------
        // Room title + category icon
        // ---------------------------------------------------------

        var titleY =
            origin.Y +
            thumbHeight +
            8f;

        float categoryWidth;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            categoryWidth =
                ImGui.CalcTextSize(
                    categoryIcon.ToIconString()).X;

            drawList.AddText(
                new Vector2(
                    origin.X + 10f,
                    titleY),
                ImGui.GetColorU32(Accent),
                categoryIcon.ToIconString());
        }

        drawList.AddText(
            new Vector2(
                origin.X + 10f + categoryWidth + 6f,
                titleY),
            ImGui.GetColorU32(Vector4.One),
            title);

        // ---------------------------------------------------------
        // Current content pill
        // ---------------------------------------------------------

        const float contentPillHeight = 20f;

        var contentTextSize =
            ImGui.CalcTextSize(contentText);

        var contentPillMin =
            new Vector2(
                origin.X + 10f,
                titleY + 22f);

        var contentPillMax =
            contentPillMin +
            new Vector2(
                MathF.Min(contentTextSize.X + 16f, width - 20f),
                contentPillHeight);

        drawList.AddRectFilled(
            contentPillMin,
            contentPillMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.12f)),
            6f);

        drawList.AddText(
            contentPillMin +
            new Vector2(
                8f,
                3f),
            ImGui.GetColorU32(
                new Vector4(
                    0.82f,
                    0.78f,
                    0.95f,
                    1f)),
            contentText);

        // ---------------------------------------------------------
        // Host + watcher metadata
        // ---------------------------------------------------------

        var hostY =
            origin.Y +
            thumbHeight +
            62f;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                new Vector2(
                    origin.X + 10f,
                    hostY),
                ImGui.GetColorU32(new Vector4(1f, 0.78f, 0.25f, 1f)),
                FontAwesomeIcon.Crown.ToIconString());
        }

        drawList.AddText(
            new Vector2(
                origin.X + 29f,
                hostY),
            ImGui.GetColorU32(MutedText),
            "Hosted by ");

        var hostedByWidth = ImGui.CalcTextSize("Hosted by ").X;

        drawList.AddText(
            new Vector2(
                origin.X + 29f + hostedByWidth,
                hostY),
            ImGui.GetColorU32(new Vector4(0.55f, 0.35f, 1.0f, 1.0f)),
            hostName);

        var metaY =
            origin.Y +
            thumbHeight +
            102f;

        // Location row
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                new Vector2(
                    origin.X + 10f,
                    metaY - 20f),
                ImGui.GetColorU32(Accent),
                FontAwesomeIcon.MapMarkerAlt.ToIconString());
        }

        drawList.AddText(
            new Vector2(
                origin.X + 29f,
                metaY - 14f),
            ImGui.GetColorU32(
                new Vector4(
                    0.72f,
                    0.68f,
                    0.95f,
                    0.85f)),
            locationText);


       

  

        // ---------------------------------------------------------
        // Join button
        // ---------------------------------------------------------

        const float buttonHeight = 24f;
        const float statusWidth = 32f;
        const float buttonGap = 6f;

        var joinMin =
            new Vector2(
                origin.X + 8f,
                origin.Y + height - buttonHeight - 8f);

        var joinMax =
            new Vector2(
                origin.X +
                width -
                statusWidth -
                buttonGap -
                8f,
                joinMin.Y + buttonHeight);

        var statusMin =
            new Vector2(
                joinMax.X + buttonGap,
                joinMin.Y);

        var statusMax =
            new Vector2(
                origin.X + width - 8f,
                joinMin.Y + buttonHeight);

        var mouse =
            ImGui.GetMousePos();

        var joinHovered =
            !locked &&
            mouse.X >= joinMin.X &&
            mouse.X <= joinMax.X &&
            mouse.Y >= joinMin.Y &&
            mouse.Y <= joinMax.Y;

        var statusHovered =
            mouse.X >= statusMin.X &&
            mouse.X <= statusMax.X &&
            mouse.Y >= statusMin.Y &&
            mouse.Y <= statusMax.Y;

        drawList.AddRectFilled(
            joinMin,
            joinMax,
            ImGui.GetColorU32(
                locked
                    ? new Vector4(
                        CardBgHover.X,
                        CardBgHover.Y,
                        CardBgHover.Z,
                        0.55f)
                    : joinHovered
                        ? AccentHover
                        : featured
                            ? Accent
                            : new Vector4(
                                CardBgHover.X,
                                CardBgHover.Y,
                                CardBgHover.Z,
                                0.92f)),
            5f);

        if (!featured && !locked)
        {
            drawList.AddRect(
                joinMin,
                joinMax,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.24f)),
                5f);
        }

        const string joinText =
            "Join Room";

        var joinTextSize =
            ImGui.CalcTextSize(joinText);

        drawList.AddText(
            new Vector2(
                joinMin.X +
                (joinMax.X -
                 joinMin.X -
                 joinTextSize.X) * 0.5f,
                joinMin.Y +
                (buttonHeight -
                 joinTextSize.Y) * 0.5f),
ImGui.GetColorU32(
    locked
        ? new Vector4(
            MutedText.X,
            MutedText.Y,
            MutedText.Z,
            0.55f)
        : Vector4.One),
            joinText);

        // ---------------------------------------------------------
        // Room visibility status
        // ---------------------------------------------------------

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var statusIcon =
                locked
                    ? FontAwesomeIcon.Lock
                    : FontAwesomeIcon.LockOpen;

            var glyph =
                statusIcon.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(glyph);

            drawList.AddText(
                new Vector2(
                    statusMin.X +
                    (statusWidth - glyphSize.X) * 0.5f,
                    statusMin.Y +
                    (buttonHeight - glyphSize.Y) * 0.5f),
                ImGui.GetColorU32(
                    locked
                        ? MutedText
                        : new Vector4(
                            0.65f,
                            0.75f,
                            0.90f,
                            1f)),
                glyph);
        }

        // ---------------------------------------------------------
        // Hover border
        // ---------------------------------------------------------

        if (hovered)
        {
            drawList.AddRect(
                origin,
                origin + size,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.45f)),
                10f,
                ImDrawFlags.None,
                1f);
        }

        // ---------------------------------------------------------
        // Restore the card as the active ImGui layout item
        // ---------------------------------------------------------
        //
        // DrawAvatarChip() creates its own ImGui items. Without this,
        // ImGui.SameLine() thinks the last item was the final tiny
        // avatar instead of this entire room card, which causes the
        // following cards to staircase diagonally.
        //
        // Re-reserve the exact card rectangle so the next SameLine()
        // positions itself from the full card again.
        ImGui.SetCursorScreenPos(origin);

        ImGui.Dummy(size);

        // The room cards are still mock/demo data, so don't
        // actually attempt to join anything yet.
    }

    private void DrawCreateWatchPartyCard(
     float width,
     float height)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(width, height);

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
            10f);

        DrawDashedRect(
            drawList,
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    hovered ? 0.75f : 0.40f)),
            10f);

        var centerX =
            origin.X + width * 0.5f;

        var iconCenter =
            new Vector2(
                centerX,
                origin.Y + 44f);

        const float circleRadius = 17f;

        drawList.AddCircleFilled(
            iconCenter,
            circleRadius,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    hovered ? 0.30f : 0.20f)));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph =
                FontAwesomeIcon.Plus.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(glyph);

            drawList.AddText(
                new Vector2(
                    iconCenter.X -
                    glyphSize.X * 0.5f,
                    iconCenter.Y -
                    glyphSize.Y * 0.5f),
                ImGui.GetColorU32(AccentHover),
                glyph);
        }

        const string title =
            "Watch With Friends";

        var titleSize =
            ImGui.CalcTextSize(title);

        drawList.AddText(
            new Vector2(
                centerX - titleSize.X * 0.5f,
                origin.Y + 72f),
            ImGui.GetColorU32(Vector4.One),
            title);

        const string subtitle1 =
            "Create or join a room to";

        const string subtitle2 =
            "watch videos together in Eorzea";



        var subtitle1Size =
            ImGui.CalcTextSize(subtitle1);

        var subtitle2Size =
            ImGui.CalcTextSize(subtitle2);

        drawList.AddText(
            new Vector2(
                centerX -
                subtitle1Size.X * 0.5f,
                origin.Y + 96f),
            ImGui.GetColorU32(MutedText),
            subtitle1);

        drawList.AddText(
            new Vector2(
                centerX -
                subtitle2Size.X * 0.5f,
                origin.Y + 113f),
            ImGui.GetColorU32(MutedText),
            subtitle2);

        // ---------------------------------------------------------
        // Placeholder actions
        // ---------------------------------------------------------

        const float buttonGap = 8f;
        const float buttonHeight = 28f;
        const float horizontalPadding = 12f;

        var buttonWidth =
            (width - horizontalPadding * 2f - buttonGap) * 0.5f;

        var buttonsY =
            origin.Y + height - buttonHeight - 10f;

        var newRoomMin =
            new Vector2(
                origin.X + horizontalPadding,
                buttonsY);

        var newRoomMax =
            new Vector2(
                newRoomMin.X + buttonWidth,
                buttonsY + buttonHeight);

        var joinRoomMin =
            new Vector2(
                newRoomMax.X + buttonGap,
                buttonsY);

        var joinRoomMax =
            new Vector2(
                joinRoomMin.X + buttonWidth,
                buttonsY + buttonHeight);


        // ---------------------------------------------------------
        // New Room button
        // ---------------------------------------------------------

        var mousePos =
            ImGui.GetMousePos();

        var newRoomHovered =
            mousePos.X >= newRoomMin.X &&
            mousePos.X <= newRoomMax.X &&
            mousePos.Y >= newRoomMin.Y &&
            mousePos.Y <= newRoomMax.Y;

        drawList.AddRectFilled(
            newRoomMin,
            newRoomMax,
            ImGui.GetColorU32(
                newRoomHovered
                    ? AccentHover
                    : Accent),
            6f);

        const string newRoomText = "New Room";

        var newRoomTextSize =
            ImGui.CalcTextSize(newRoomText);

        drawList.AddText(
            new Vector2(
                newRoomMin.X +
                    (buttonWidth - newRoomTextSize.X) * 0.5f,
                newRoomMin.Y +
                    (buttonHeight - newRoomTextSize.Y) * 0.5f),
            ImGui.GetColorU32(Vector4.One),
            newRoomText);


        // ---------------------------------------------------------
        // Join Room button
        // ---------------------------------------------------------

        var joinRoomHovered =
            mousePos.X >= joinRoomMin.X &&
            mousePos.X <= joinRoomMax.X &&
            mousePos.Y >= joinRoomMin.Y &&
            mousePos.Y <= joinRoomMax.Y;

        drawList.AddRectFilled(
            joinRoomMin,
            joinRoomMax,
            ImGui.GetColorU32(
                joinRoomHovered
                    ? CardBgHover
                    : CardBg),
            6f);

        drawList.AddRect(
            joinRoomMin,
            joinRoomMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    joinRoomHovered ? 0.9f : 0.55f)),
            6f);

        const string joinRoomText = "Join Room";

        var joinRoomTextSize =
            ImGui.CalcTextSize(joinRoomText);

        drawList.AddText(
            new Vector2(
                joinRoomMin.X +
                    (buttonWidth - joinRoomTextSize.X) * 0.5f,
                joinRoomMin.Y +
                    (buttonHeight - joinRoomTextSize.Y) * 0.5f),
            ImGui.GetColorU32(
                joinRoomHovered
                    ? Vector4.One
                    : MutedText),
            joinRoomText);

        if (newRoomHovered || joinRoomHovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);
        }

        if (hovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);
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
        const float gap = 20f;
        var textWidth = showArt ? MathF.Min(avail * 0.48f, 420f) : avail;
        var artWidth = MathF.Max(avail - textWidth - gap, 220f);

        ImGui.BeginGroup();
        ImGui.SetWindowFontScale(1.75f);
        ImGui.TextUnformatted("Welcome to ");
        ImGui.SameLine(0, 0);
        ImGui.TextColored(Accent, "Alpha Channel");
        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0, 2));

        ImGui.SetWindowFontScale(1.15f);
        ImGui.TextColored(MutedText, "Cast. Watch. Together.");
        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0, 6));

        ImGui.PushTextWrapPos(ImGui.GetCursorPos().X + textWidth);
        ImGui.TextWrapped(
            "Bring your favourite videos into Eorzea. Create watch parties, " +
            "share screens, and enjoy moments together with friends wherever you are.");
        ImGui.PopTextWrapPos();

        ImGui.Dummy(new Vector2(0, 18));

        var inviteHeight = 170f;
        var inviteWidth = ImGui.GetContentRegionAvail().X * 0.82f;

        var inviteOrigin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            inviteOrigin,
            inviteOrigin + new Vector2(inviteWidth, inviteHeight),
            ImGui.GetColorU32(new Vector4(CardBg.X, CardBg.Y, CardBg.Z, 0.45f)),
            14f);

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16, 14)))
        using (var invite = ImRaii.Child(
    "##inviteFriends",
    new Vector2(inviteWidth, inviteHeight),
    false,
    ImGuiWindowFlags.NoBackground))
        {
            if (invite)
            {
                var imageSize = new Vector2(56, 56);
                var imageOffset = new Vector2(12, 12);

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

                ImGui.Dummy(new Vector2(0, 6));

                ImGui.SetWindowFontScale(1.3f);
                ImGui.TextUnformatted("Invite your friends to watch with you!");
                ImGui.SetWindowFontScale(1f);

                ImGui.TextColored(
    MutedText,
    "Add your friends to host watch parties, share virtual screens,\nand watch together in sync across Eorzea.");

                ImGui.Dummy(new Vector2(0, 2));

                ImGui.SetNextItemWidth(inviteWidth - 250);

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
                        if (ImGui.Button("##addFriend", new Vector2(120, 34)))
                        {
                            // Add friend action later
                        }

                        var buttonMin = ImGui.GetItemRectMin();
                        var buttonSize = ImGui.GetItemRectSize();

                        using (ImRaii.PushFont(UiBuilder.IconFont))
                        {
                            var icon = FontAwesomeIcon.UserPlus.ToIconString();
                            var iconSize = ImGui.CalcTextSize(icon);

                            ImGui.GetWindowDrawList().AddText(
                                buttonMin + new Vector2(14, (buttonSize.Y - iconSize.Y) * 0.5f),
                                ImGui.GetColorU32(Vector4.One),
                                icon);
                        }

                        ImGui.GetWindowDrawList().AddText(
    buttonMin + new Vector2(36, 7),
    ImGui.GetColorU32(Vector4.One),
    "Add Friend");

                        ImGui.Dummy(new Vector2(0, 12));
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
                ImGui.GetCursorScreenPos().X + (availWidth - titleSize.X) * 0.5f - 12,
                lineY),
            lineColor,
            1f);

        drawList.AddLine(
            new Vector2(
                ImGui.GetCursorScreenPos().X + (availWidth + titleSize.X) * 0.5f + 12,
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

        ImGui.Dummy(new Vector2(0, 2));

        var avail = ImGui.GetContentRegionAvail().X;

        const float gap = 10f;
        var cardWidth = (avail - gap * 2) / 3f;

        const float cardHeight = 175f;
        const float iconSize = 72f;
        const float titleY = 36f;
        const float bodyY = 60f;
        const float gapAfterTitle = 6f;

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
            FontAwesomeIcon.ThLarge,
Hex(0x38BDF8),
"browse-apps.png",
"Browse Apps",
"Open chat, Hub, Tweeter, and more.",
"App Store →",
() => currentPage = HomePage.Apps);
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

        var discOrigin = origin + new Vector2(
            12f,
            12f);


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
            12f + 48f + 16f,
            18f);
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

        drawList.AddText(
            new Vector2(origin.X + 16, origin.Y + height - 24),
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

            drawList.AddText(new Vector2(pos.X, y), color, value);
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
        ImGui.Dummy(new Vector2(0, 10));

        var avail = ImGui.GetContentRegionAvail().X;
        const float gap = 12f;
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
        const float badgeGap = 10f;
        const float titleGap = 4f;

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
        drawList.AddText(badgeCenter - numSize * 0.5f, ImGui.GetColorU32(Vector4.One), num);

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
            drawList.AddText(
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

    private void DoJoin(string hostName)
    {
        if (hostName.Length == 0)
        {
            return;
        }

        var engine =
            screenController.Engine;

        if (engine.IsPlayingSnes ||
            engine.IsPlayingGameBoy)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] End gameplay before joining a Watch Party.");

            joinError =
                "End gameplay before joining a Watch Party.";

            return;
        }

        queue.Clear();

        joinedHostDisplayName =
            hostName.Trim();

        gameplayStreamOfferDismissed =
    false;

        _ = stream.JoinAsync(
            hostName.Trim());
    }

    private static string ActivityLabel(ActivityEventDto item) => item.Type switch
    {
        "StartedWatching" => $"{item.ActorDisplayName} started watching",
        "JoinedWatchAlong" => item.Metadata is { Length: > 0 }
            ? $"{item.ActorDisplayName} joined {item.Metadata}'s watch-along"
            : $"{item.ActorDisplayName} joined a watch-along",
        "FriendAccepted" => $"{item.ActorDisplayName} accepted a friend request",
        "PostLiked" => $"{item.ActorDisplayName} liked your post",
        "PostReplied" => $"{item.ActorDisplayName} replied to your post",
        "Mentioned" => $"{item.ActorDisplayName} mentioned you",
        "VenueSaved" => item.Metadata is { Length: > 0 }
            ? $"{item.ActorDisplayName} saved a venue: {item.Metadata}"
            : $"{item.ActorDisplayName} saved a venue",
        "WentLive" => $"{item.ActorDisplayName} went live",
        _ => $"{item.ActorDisplayName}: {item.Type}",
    };
}
