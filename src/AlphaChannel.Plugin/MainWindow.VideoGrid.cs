using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? browseVideoTopicFilter;
    private int browseVideoSortMode;

    // ---------------------------------------------------------
    // Browse Videos main tabs
    // 0 = Topics
    // 1 = Subscriptions
    // 2 = Favourite Videos
    // ---------------------------------------------------------

    private int browseVideoSectionTab;

    // ---------------------------------------------------------
    // Favourite Videos tab
    // ---------------------------------------------------------

    private List<VideoSearchEntry>? favouriteVideoResults;
    private bool isLoadingFavouriteVideos;
    private string lastFavouriteVideoSignature = string.Empty;

    // ---------------------------------------------------------
    // Subscriptions tab
    // ---------------------------------------------------------

    private List<VideoSearchEntry>? subscriptionVideoResults;
    private bool isLoadingSubscriptionVideos;
    private string lastSubscriptionSignature = string.Empty;

    private void DrawBrowseVideoTabs()
    {
        ImGui.TextColored(
            MutedText,
            "BROWSE VIDEOS");

        ImGui.Dummy(
            new Vector2(
                0f,
                6f));

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float gap = 8f;
        const float buttonHeight = 38f;
        const int tabCount = 3;

        var buttonWidth =
            (availableWidth -
             (gap * (tabCount - 1))) /
            tabCount;

        var buttonSize =
            new Vector2(
                buttonWidth,
                buttonHeight);

        DrawBrowseVideoTab(
            FontAwesomeIcon.ThLarge,
            "Topics",
            0,
            buttonSize);

        ImGui.SameLine(
            0f,
            gap);

        DrawBrowseVideoTab(
            FontAwesomeIcon.Rss,
            "Subscriptions",
            1,
            buttonSize);

        ImGui.SameLine(
            0f,
            gap);

        DrawBrowseVideoTab(
            FontAwesomeIcon.Heart,
            "Favourite Videos",
            2,
            buttonSize);
    }

    private void DrawBrowseVideoTab(
        FontAwesomeIcon icon,
        string label,
        int tab,
        Vector2 size)
    {
        var selected =
            browseVideoSectionTab == tab;

        var buttonPos =
            ImGui.GetCursorScreenPos();

        var buttonBg =
            selected
                ? new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.10f)
                : new Vector4(
                    0.045f,
                    0.06f,
                    0.10f,
                    1f);

        var hoverBg =
            selected
                ? new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.16f)
                : new Vector4(
                    0.07f,
                    0.09f,
                    0.14f,
                    1f);

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
                $"##browseVideoSection_{tab}",
                size))
            {
                browseVideoSectionTab =
                    tab;
            }
        }

        var drawList =
            ImGui.GetWindowDrawList();

        // Thin border matching the Player source tabs.
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
            selected
                ? 1.5f
                : 1f);

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

        var textSize =
            ImGui.CalcTextSize(
                label);

        const float iconGap = 8f;

        var totalWidth =
            iconSize.X +
            iconGap +
            textSize.X;

        var textStart =
            new Vector2(
                buttonPos.X +
                (size.X - totalWidth) * 0.5f,
                buttonPos.Y +
                (size.Y - textSize.Y) * 0.5f);

        var color =
            selected
                ? AccentHover
                : MutedText;

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            drawList.AddText(
                textStart,
                ImGui.GetColorU32(
                    color),
                iconText);
        }

        drawList.AddText(
            textStart +
            new Vector2(
                iconSize.X +
                iconGap,
                0f),
            ImGui.GetColorU32(
                color),
            label);
    }

    private async Task LoadFavouriteVideosAsync()
    {
        try
        {
            isLoadingFavouriteVideos = true;

            var ids =
                Plugin.Cfg.FavouriteYouTubeVideoIds
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (ids.Count == 0)
            {
                favouriteVideoResults = [];
                lastFavouriteVideoSignature = string.Empty;
                return;
            }

            var tasks =
                ids.Select(
                    id =>
                        searchResolver.GetVideoEntryAsync(
                            $"https://www.youtube.com/watch?v={id}",
                            CancellationToken.None))
                    .ToArray();

            var results =
                await Task.WhenAll(tasks)
                    .ConfigureAwait(false);

            favouriteVideoResults =
                results
                    .Where(x => x is not null)
                    .Select(x => x!)
                    .ToList();

            lastFavouriteVideoSignature =
                string.Join(
                    "|",
                    ids.OrderBy(
                        x => x,
                        StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Favourites] Failed to load favourite videos: " +
                $"{exception.Message}");

            favouriteVideoResults = [];
        }
        finally
        {
            isLoadingFavouriteVideos = false;
        }
    }

    private async Task LoadSubscriptionVideosAsync()
    {
        try
        {
            isLoadingSubscriptionVideos = true;

            var channelIds =
                Plugin.Cfg.SubscribedYouTubeChannelIds
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (channelIds.Count == 0)
            {
                subscriptionVideoResults = [];
                lastSubscriptionSignature =
                    string.Empty;

                return;
            }

            var loaded =
                new List<VideoSearchEntry>();

            // Start conservatively: five recent uploads per channel.
            // Sequential loading avoids hammering YouTube when somebody
            // eventually has a large subscription collection.
            foreach (var channelId in channelIds)
            {
                var channelVideos =
                    await searchResolver
                        .GetChannelUploadsAsync(
                            channelId,
                            5,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                loaded.AddRange(
                    channelVideos);
            }

            subscriptionVideoResults =
                loaded
                    .GroupBy(
                        video => video.Url,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderByDescending(
                        video =>
                            video.UploadDate ??
                            DateTimeOffset.MinValue)
                    .Take(40)
                    .ToList();

            lastSubscriptionSignature =
                string.Join(
                    "|",
                    channelIds.OrderBy(
                        id => id,
                        StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Subscriptions] Failed to load subscription feed: " +
                $"{exception.Message}");

            subscriptionVideoResults = [];
        }
        finally
        {
            isLoadingSubscriptionVideos = false;
        }
    }

    private void DrawBrowseSubscriptionsSection()
    {
        var subscribedIds =
            Plugin.Cfg.SubscribedYouTubeChannelIds;

        var subscriptionSignature =
            string.Join(
                "|",
                subscribedIds
                    .OrderBy(
                        id => id,
                        StringComparer.OrdinalIgnoreCase));

        // Reload whenever the actual subscription IDs change.
        if (!isLoadingSubscriptionVideos &&
            (
                subscriptionVideoResults is null ||
                !string.Equals(
                    subscriptionSignature,
                    lastSubscriptionSignature,
                    StringComparison.Ordinal)
            ))
        {
            _ = LoadSubscriptionVideosAsync();
        }

        // ---------------------------------------------------------
        // Heading
        // ---------------------------------------------------------

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            ImGui.TextColored(
                AccentHover,
                FontAwesomeIcon.Rss.ToIconString());
        }

        ImGui.SameLine(
            0f,
            8f);

        ImGui.SetWindowFontScale(
            1.08f);

        ImGui.TextColored(
            Vector4.One,
            "Subscriptions");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        // ---------------------------------------------------------
        // Empty state
        // ---------------------------------------------------------

        if (subscribedIds.Count == 0)
        {
            ImGui.TextColored(
                MutedText,
                "You haven't subscribed to any channels yet.");

            ImGui.Dummy(
                new Vector2(
                    0f,
                    4f));

            ImGui.TextColored(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.65f),
                "Use the + button beside a creator to subscribe.");

            return;
        }

        // ---------------------------------------------------------
        // Loading
        // ---------------------------------------------------------

        if (isLoadingSubscriptionVideos &&
            subscriptionVideoResults is null)
        {
            ImGui.TextColored(
                MutedText,
                "Loading subscription videos...");

            return;
        }

        if (subscriptionVideoResults is not
            { Count: > 0 } results)
        {
            ImGui.TextColored(
                MutedText,
                "No subscription videos could be loaded.");

            return;
        }

        // ---------------------------------------------------------
        // Hide videos from channels that were unsubscribed while
        // this page is being displayed.
        // ---------------------------------------------------------

        var currentSubscriptions =
            Plugin.Cfg.SubscribedYouTubeChannelIds
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);

        var visibleResults =
            results
                .Where(
                    result =>
                        result.ChannelId is not null &&
                        currentSubscriptions.Contains(
                            result.ChannelId))
                .ToList();

        if (visibleResults.Count == 0)
        {
            return;
        }

        // ---------------------------------------------------------
        // Grid
        // ---------------------------------------------------------

        const int columns = 5;
        const float gap = 12f;
        const float rowGap = 16f;
        const float cardHeight = 224f;

        var contentWidth =
            ImGui.GetContentRegionAvail().X;

        var cardWidth =
            (contentWidth -
             gap * (columns - 1)) /
            columns;

        for (var index = 0;
             index < visibleResults.Count;
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
                $"subscriptionVideo_{index}");

            DrawHomeYouTubeCard(
                visibleResults[index],
                cardWidth,
                cardHeight);

            ImGui.PopID();
        }
    }

    private void DrawBrowseFavouriteVideosSection()
    {
        var favouriteIds =
            Plugin.Cfg.FavouriteYouTubeVideoIds;

        // ---------------------------------------------------------
        // Reload if favourites changed
        // ---------------------------------------------------------

        var favouriteSignature =
        string.Join(
            "|",
            favouriteIds
                .OrderBy(
                    x => x,
                    StringComparer.OrdinalIgnoreCase));

        if (!isLoadingFavouriteVideos &&
            (
                favouriteVideoResults is null ||
                !string.Equals(
                    favouriteSignature,
                    lastFavouriteVideoSignature,
                    StringComparison.Ordinal)
            ))
        {
            _ = LoadFavouriteVideosAsync();
        }

        // ---------------------------------------------------------
        // Heading
        // ---------------------------------------------------------

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            ImGui.TextColored(
                AccentHover,
                FontAwesomeIcon.Heart.ToIconString());
        }

        ImGui.SameLine(
            0f,
            8f);

        ImGui.SetWindowFontScale(
            1.08f);

        ImGui.TextColored(
            Vector4.One,
            "Favourite Videos");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        // ---------------------------------------------------------
        // Empty state
        // ---------------------------------------------------------

        if (favouriteIds.Count == 0)
        {
            ImGui.TextColored(
                MutedText,
                "You haven't favourited any videos yet.");

            ImGui.Dummy(
                new Vector2(
                    0f,
                    4f));

            ImGui.TextColored(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.65f),
                "Use the heart button on a video to add it here.");

            return;
        }

        // ---------------------------------------------------------
        // Loading
        // ---------------------------------------------------------

        if (isLoadingFavouriteVideos &&
            favouriteVideoResults is null)
        {
            ImGui.TextColored(
                MutedText,
                "Loading favourite videos...");

            return;
        }

        if (favouriteVideoResults is not
            { Count: > 0 } results)
        {
            ImGui.TextColored(
                MutedText,
                "Favourite videos could not be loaded.");

            return;
        }

        // ---------------------------------------------------------
        // Only show videos that are STILL favourited.
        //
        // This means clicking the heart from this page removes
        // the card immediately without needing a refresh.
        // ---------------------------------------------------------

        var currentFavouriteIds =
            Plugin.Cfg.FavouriteYouTubeVideoIds
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);

        var visibleResults =
            results
                .Where(
                    result =>
                    {
                        var id =
                            GetYouTubeVideoId(
                                result.Url);

                        return id is not null &&
                               currentFavouriteIds.Contains(id);
                    })
                .ToList();

        if (visibleResults.Count == 0)
        {
            return;
        }

        // ---------------------------------------------------------
        // Grid
        // ---------------------------------------------------------

        const int columns = 5;
        const float gap = 10f;
        const float rowGap = 16f;
        const float cardHeight = 224f;

        var contentWidth =
            ImGui.GetContentRegionAvail().X;

        var cardWidth =
            (contentWidth -
             gap * (columns - 1)) /
            columns;

        for (var index = 0;
             index < visibleResults.Count;
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
                $"favouriteVideo_{index}");

            DrawHomeYouTubeCard(
                visibleResults[index],
                cardWidth,
                cardHeight);

            ImGui.PopID();
        }
    }

    private void DrawVideoGrid()
    {
        DrawBrowseVideoTabs();

        ImGui.Dummy(
            new Vector2(
                0f,
                18f));


        // Only the Topics tab needs the existing Browse Videos
        // discovery data for now.
        if (browseVideoSectionTab == 0 &&
            !browseVideoRequested)
        {
            browseVideoRequested = true;
            isLoadingBrowseVideos = true;

            _ = LoadBrowseVideosAsync();
        }

        // ---------------------------------------------------------
        // Browse Videos section routing
        // ---------------------------------------------------------

        switch (browseVideoSectionTab)
        {
            case 1:
                DrawBrowseSubscriptionsSection();
                return;

            case 2:
                DrawBrowseFavouriteVideosSection();
                return;
        }


        // ---------------------------------------------------------
        // Topic filters
        // ---------------------------------------------------------

        ImGui.TextColored(
            Accent,
            "Topics");

        ImGui.SameLine(0f, 10f);

        // All
        var allSelected =
            browseVideoTopicFilter is null;

        using (ImRaii.PushColor(
            ImGuiCol.Button,
            allSelected ? Accent : CardBg)
            .Push(
                ImGuiCol.ButtonHovered,
                allSelected ? AccentHover : CardBgHover)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            if (ImGui.Button("All"))
            {
                browseVideoTopicFilter = null;
            }
        }


        // Loaded topic filters
        if (browseVideoResults is { Count: > 0 })
        {
            foreach (var topicName in browseVideoResults.Keys)
            {
                ImGui.SameLine(0f, 8f);

                var selected =
                    string.Equals(
                        browseVideoTopicFilter,
                        topicName,
                        StringComparison.Ordinal);

                using (ImRaii.PushColor(
                    ImGuiCol.Button,
                    selected ? Accent : CardBg)
                    .Push(
                        ImGuiCol.ButtonHovered,
                        selected ? AccentHover : CardBgHover)
                    .Push(
                        ImGuiCol.ButtonActive,
                        AccentActive))
                {
                    if (ImGui.Button(
                        $"{topicName}##browseFilter_{topicName}"))
                    {
                        browseVideoTopicFilter = topicName;
                    }
                }
            }
        }

        // ---------------------------------------------------------
        // Sort controls
        // ---------------------------------------------------------

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));

        ImGui.TextColored(
            MutedText,
            "Sort:");

        ImGui.SameLine(0f, 10f);

        var trendingSelected =
            browseVideoSortMode == 0;

        using (ImRaii.PushColor(
            ImGuiCol.Button,
            trendingSelected ? Accent : CardBg)
            .Push(
                ImGuiCol.ButtonHovered,
                trendingSelected ? AccentHover : CardBgHover)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            if (ImGui.Button("Trending"))
            {
                browseVideoSortMode = 0;
            }
        }

        ImGui.SameLine(0f, 8f);

        var newestSelected =
            browseVideoSortMode == 1;

        using (ImRaii.PushColor(
            ImGuiCol.Button,
            newestSelected ? Accent : CardBg)
            .Push(
                ImGuiCol.ButtonHovered,
                newestSelected ? AccentHover : CardBgHover)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            if (ImGui.Button("Newest"))
            {
                browseVideoSortMode = 1;
            }
        }

        ImGui.SameLine(0f, 8f);

        var viewedSelected =
            browseVideoSortMode == 2;

        using (ImRaii.PushColor(
            ImGuiCol.Button,
            viewedSelected ? Accent : CardBg)
            .Push(
                ImGuiCol.ButtonHovered,
                viewedSelected ? AccentHover : CardBgHover)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            if (ImGui.Button("Most Viewed"))
            {
                browseVideoSortMode = 2;
            }
        }

        // ---------------------------------------------------------
        // Refresh icon — far right, no background
        // ---------------------------------------------------------

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            ImGui.GetWindowContentRegionMax().X - 20f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                AccentHover,
                FontAwesomeIcon.Sync.ToIconString());
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);

            ImGui.SetTooltip(
                "Refresh Browse Videos");
        }

        if (ImGui.IsItemClicked())
        {
            browseVideoTopicFilter = null;
            browseVideoResults = null;
            isLoadingBrowseVideos = true;

            _ = LoadBrowseVideosAsync(true);
        }

        ImGui.Dummy(
            new Vector2(0f, 15f));

        // Scrollable content area
        using var child = ImRaii.Child(
            "##browseVideoContent",
            new Vector2(
                0f,
                -1f),
            false);

        if (!child)
        {
            return;
        }

        if (isLoadingBrowseVideos &&
       (browseVideoResults is null ||
        browseVideoResults.Count == 0))
        {
            ImGui.TextColored(
                MutedText,
                "Loading videos...");

            return;
        }

        if (browseVideoResults is null ||
            browseVideoResults.Count == 0)
        {
            ImGui.TextColored(
                MutedText,
                "No videos loaded.");

            return;
        }

        if (isLoadingBrowseVideos)
        {
            ImGui.TextColored(
                MutedText,
                $"Loading more topics... {browseVideoResults!.Count}/8");

            ImGui.Dummy(
                new Vector2(
                    0f,
                    10f));
        }

        const int columns = 5;
        const float gap = 12f;
        const float rowGap = 16f;
        const float cardHeight = 224f;

        var contentWidth =
            ImGui.GetContentRegionAvail().X;

        var cardWidth =
            (contentWidth - gap * (columns - 1)) /
            columns;

        var topicIndex = 0;

        var visibleTopics =
            browseVideoTopicFilter is null
                ? browseVideoResults
                : browseVideoResults
                    .Where(x =>
                        string.Equals(
                            x.Key,
                            browseVideoTopicFilter,
                            StringComparison.Ordinal))
                    .ToDictionary(
                        x => x.Key,
                        x => x.Value);

        foreach (var topic in visibleTopics)
        {
            if (topicIndex > 0)
            {
                ImGui.Dummy(
                    new Vector2(
                        0f,
                        rowGap));
            }

            // Topic heading
            var topicIcon =
                topic.Key switch
                {
                    "Gaming" => FontAwesomeIcon.Gamepad,
                    "MMORPG" => FontAwesomeIcon.Users,
                    "Final Fantasy" => FontAwesomeIcon.Magic,
                    "Anime" => FontAwesomeIcon.Star,
                    "Movies" => FontAwesomeIcon.Film,
                    "TV Shows" => FontAwesomeIcon.Tv,
                    "Music" => FontAwesomeIcon.Music,
                    "Memes" => FontAwesomeIcon.Grin,
                    "Wildlife" => FontAwesomeIcon.Paw,
                    "Architecture" => FontAwesomeIcon.Building,
                    "Science" => FontAwesomeIcon.Flask,
                    "Space" => FontAwesomeIcon.Rocket,
                    "History" => FontAwesomeIcon.Landmark,
                    "Technology" => FontAwesomeIcon.Microchip,
                    "Pets" => FontAwesomeIcon.Paw,
                    "Food" => FontAwesomeIcon.Utensils,
                    "Travel" => FontAwesomeIcon.Plane,
                    "Cars" => FontAwesomeIcon.Car,
                    "Sports" => FontAwesomeIcon.Futbol,
                    _ => FontAwesomeIcon.PlayCircle
                };

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    AccentHover,
                    topicIcon.ToIconString());
            }

            ImGui.SameLine(0f, 8f);

            ImGui.SetWindowFontScale(1.08f);

            ImGui.TextColored(
                Vector4.One,
                topic.Key);

            ImGui.SetWindowFontScale(1f);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    8f));
            var videos =
    browseVideoSortMode switch
    {
        1 => topic.Value
            .OrderByDescending(
                x => x.UploadDate ?? DateTime.MinValue)
            .ToList(),

        2 => topic.Value
            .OrderByDescending(
                x => x.ViewCount ?? 0)
            .ToList(),

        _ => topic.Value
            .OrderByDescending(GetTrendingScore)
            .ToList()
    };

            // All topics = 5 videos per topic.
            // Single-topic filter = up to 15 videos.
            var maxVideos =
                browseVideoTopicFilter is null
                    ? 5
                    : 15;

            var visibleCount =
                Math.Min(
                    maxVideos,
                    videos.Count);

            const float videoRowGap = 16f;

            for (var index = 0;
                 index < visibleCount;
                 index++)
            {
                if (index > 0)
                {
                    if (index % columns == 0)
                    {
                        // Start a new row after every 5 cards.
                        ImGui.Dummy(
                            new Vector2(
                                0f,
                                videoRowGap));
                    }
                    else
                    {
                        ImGui.SameLine(
                            0f,
                            gap);
                    }
                }

                ImGui.PushID(
                    $"browse_{topicIndex}_{index}");

                DrawHomeYouTubeCard(
                    videos[index],
                    cardWidth,
                    cardHeight);

                ImGui.PopID();
            }

            topicIndex++;
        }
    }
}