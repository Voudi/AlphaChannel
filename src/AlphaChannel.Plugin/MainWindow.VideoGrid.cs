using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? browseVideoTopicFilter;
    private int browseVideoSortMode;

    private readonly HashSet<string> browseSelectedTopics =
    new(
        StringComparer.Ordinal);

    private int browseVideosPerTopic =
        5;

    // ---------------------------------------------------------
    // Browse Videos main tabs
    // 0 = Topics
    // 1 = Subscriptions
    // 2 = Favourite Videos
    // ---------------------------------------------------------

    private int browseVideoSectionTab;
    private int previousBrowseVideoSectionTab;

    // ---------------------------------------------------------
    // Favourite Videos tab
    // ---------------------------------------------------------

    private static readonly TimeSpan FavouriteVideoCacheDuration =
        TimeSpan.FromHours(3);

    private volatile List<VideoSearchEntry>?
        favouriteVideoResults;

    private volatile bool isLoadingFavouriteVideos;

    private string lastFavouriteVideoSignature =
        string.Empty;

    private DateTime favouriteVideoCacheUpdatedUtc;

    private int favouriteVideoSortMode =
    3;

    // ---------------------------------------------------------
    // Subscriptions tab
    // ---------------------------------------------------------

    private static readonly TimeSpan SubscriptionVideoCacheDuration =
        TimeSpan.FromHours(3);

    private volatile Dictionary<string, List<VideoSearchEntry>>?
        subscriptionVideoResults;

    private readonly HashSet<string> selectedSubscriptionChannels =
        new(StringComparer.OrdinalIgnoreCase);

    private int subscriptionVideoSortMode;

    private int subscriptionVideosPerChannel =
        5;

    private volatile bool isLoadingSubscriptionVideos;

    private string lastSubscriptionSignature =
        string.Empty;

    private DateTime subscriptionVideoCacheUpdatedUtc;

    private int subscriptionVideoExpectedChannelCount;

    private CancellationTokenSource? subscriptionVideosCts;

    private int subscriptionVideoLoadGeneration;

    private void DrawBrowseVideoTabs()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float gap =
            8f;

        var buttonHeight =
            Ui(44f);

        const int tabCount =
            3;

        var buttonWidth =
            (availableWidth -
             gap *
             (tabCount - 1)) /
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

        var iconGap = Ui(8f);

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
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                textStart,
                ImGui.GetColorU32(
                    color),
                iconText);
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            textStart +
            new Vector2(
                iconSize.X +
                iconGap,
                0f),
            ImGui.GetColorU32(
                color),
            label);
    }

    private static string GetFavouriteVideoSignature(
    IEnumerable<string> videoIds)
    {
        //
        // Do not sort this signature. Favourite list order represents the
        // order in which videos were most recently favourited.
        //
        return string.Join(
            "\u001F",
            videoIds
                .Where(
                    id =>
                        !string.IsNullOrWhiteSpace(
                            id))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase));
    }

    private void RestorePersistedFavouriteVideoCache()
    {
        if (favouriteVideoResults is not null)
        {
            return;
        }

        if (Plugin.Cfg.FavouriteVideoCache.Count == 0)
        {
            return;
        }

        try
        {
            favouriteVideoResults =
                Plugin.Cfg.FavouriteVideoCache
                    .Where(
                        video =>
                            !string.IsNullOrWhiteSpace(
                                video.Url))
                    .Select(
                        video =>
                            new VideoSearchEntry(
                                video.Title,
                                video.Url,
                                video.ChannelName,
                                video.DurationSeconds is { } seconds
                                    ? TimeSpan.FromSeconds(
                                        seconds)
                                    : null,
                                video.ThumbnailUrl,
                                video.ViewCount,
                                video.UploadDate,
                                video.ChannelId))
                    .ToList();

            favouriteVideoCacheUpdatedUtc =
                Plugin.Cfg.FavouriteVideoCacheUpdatedUtc;

            lastFavouriteVideoSignature =
                Plugin.Cfg.FavouriteVideoCacheSignature ??
                string.Empty;
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Favourites] Failed to restore cached videos: " +
                $"{exception.Message}");

            favouriteVideoResults =
                null;

            Plugin.Cfg.FavouriteVideoCache.Clear();

            Plugin.Cfg.FavouriteVideoCacheUpdatedUtc =
                default;

            Plugin.Cfg.FavouriteVideoCacheSignature =
                null;

            Plugin.Cfg.Save();
        }
    }

    private void PersistFavouriteVideoCache()
    {
        Plugin.Cfg.FavouriteVideoCache =
            favouriteVideoResults?
                .Select(
                    video =>
                        new CachedBrowseVideoRecord
                        {
                            Title =
                                video.Title,

                            Url =
                                video.Url,

                            ChannelName =
                                video.ChannelName,

                            DurationSeconds =
                                video.Duration?.TotalSeconds,

                            ThumbnailUrl =
                                video.ThumbnailUrl,

                            ViewCount =
                                video.ViewCount,

                            UploadDate =
                                video.UploadDate,

                            ChannelId =
                                video.ChannelId
                        })
                .ToList() ??
            [];

        Plugin.Cfg.FavouriteVideoCacheUpdatedUtc =
            favouriteVideoCacheUpdatedUtc;

        Plugin.Cfg.FavouriteVideoCacheSignature =
            lastFavouriteVideoSignature;

        Plugin.Cfg.Save();
    }

    private async Task LoadFavouriteVideosAsync(
        bool forceRefresh = false)
    {
        if (isLoadingFavouriteVideos)
        {
            return;
        }

        RestorePersistedFavouriteVideoCache();

        var ids =
            Plugin.Cfg.FavouriteYouTubeVideoIds
                .Where(
                    id =>
                        !string.IsNullOrWhiteSpace(
                            id))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        var signature =
            GetFavouriteVideoSignature(
                ids);

        var cacheIsFresh =
            !forceRefresh &&
            favouriteVideoResults is not null &&
            string.Equals(
                lastFavouriteVideoSignature,
                signature,
                StringComparison.Ordinal) &&
            DateTime.UtcNow -
            favouriteVideoCacheUpdatedUtc <
            FavouriteVideoCacheDuration;

        if (cacheIsFresh)
        {
            return;
        }

        isLoadingFavouriteVideos =
            true;

        try
        {
            if (ids.Count == 0)
            {
                favouriteVideoResults =
                    [];

                lastFavouriteVideoSignature =
                    string.Empty;

                favouriteVideoCacheUpdatedUtc =
                    DateTime.UtcNow;

                PersistFavouriteVideoCache();

                return;
            }

            //
            // Retain every still-favourited cached entry. A favourite-list
            // change must never cause those videos to be resolved again.
            //
            var cachedById =
                new Dictionary<string, VideoSearchEntry>(
                    StringComparer.OrdinalIgnoreCase);

            if (favouriteVideoResults is not null)
            {
                foreach (var cachedVideo in
                         favouriteVideoResults)
                {
                    var cachedId =
                        GetYouTubeVideoId(
                            cachedVideo.Url);

                    if (cachedId is not null &&
                        ids.Contains(
                            cachedId,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        cachedById.TryAdd(
                            cachedId,
                            cachedVideo);
                    }
                }
            }

            //
            // Only IDs absent from the persisted Favourite cache are sent
            // through the resolver. Newly favourited cards will normally
            // already exist in the resolver's shared metadata cache too.
            //
            var missingIds =
                ids
                    .Where(
                        id =>
                            !cachedById.ContainsKey(
                                id))
                    .ToList();

            if (missingIds.Count > 0)
            {
                var missingTasks =
                    missingIds
                        .Select(
                            id =>
                                searchResolver.GetVideoEntryAsync(
                                    $"https://www.youtube.com/watch?v={id}",
                                    CancellationToken.None))
                        .ToArray();

                var loadedMissing =
                    await Task.WhenAll(
                            missingTasks)
                        .ConfigureAwait(false);

                foreach (var loadedVideo in
                         loadedMissing)
                {
                    if (loadedVideo is null)
                    {
                        continue;
                    }

                    var loadedId =
                        GetYouTubeVideoId(
                            loadedVideo.Url);

                    if (loadedId is not null)
                    {
                        cachedById[loadedId] =
                            loadedVideo;
                    }
                }
            }

            //
            // Rebuild using FavouriteYouTubeVideoIds order. This directly
            // supplies the "Recently Favourited" sort order.
            //
            favouriteVideoResults =
                ids
                    .Where(
                        cachedById.ContainsKey)
                    .Select(
                        id =>
                            cachedById[id])
                    .ToList();

            lastFavouriteVideoSignature =
                signature;

            favouriteVideoCacheUpdatedUtc =
                DateTime.UtcNow;

            favouriteVideosVisibleCount =
                FavouritePageSize;

            PersistFavouriteVideoCache();
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Favourites] Failed to load favourite videos: " +
                $"{exception.Message}");

            favouriteVideoResults ??=
                [];
        }
        finally
        {
            isLoadingFavouriteVideos =
                false;
        }
    }

    private void UpdateFavouriteVideoCacheAfterToggle(
    VideoSearchEntry video,
    bool isNowFavourite)
    {
        RestorePersistedFavouriteVideoCache();

        var videoId =
            GetYouTubeVideoId(
                video.Url);

        if (videoId is null)
        {
            return;
        }

        var updated =
            favouriteVideoResults?
                .Where(
                    existing =>
                        !string.Equals(
                            GetYouTubeVideoId(
                                existing.Url),
                            videoId,
                            StringComparison.OrdinalIgnoreCase))
                .ToList() ??
            [];

        if (isNowFavourite)
        {
            //
            // The card itself already contains the metadata, so insert it
            // directly without making any YouTube request.
            //
            updated.Insert(
                0,
                video);
        }

        var favouriteIds =
            Plugin.Cfg.FavouriteYouTubeVideoIds
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        var cachedById =
            updated
                .Select(
                    cached =>
                        (
                            Id:
                                GetYouTubeVideoId(
                                    cached.Url),
                            Video:
                                cached
                        ))
                .Where(
                    item =>
                        item.Id is not null)
                .ToDictionary(
                    item =>
                        item.Id!,
                    item =>
                        item.Video,
                    StringComparer.OrdinalIgnoreCase);

        favouriteVideoResults =
            favouriteIds
                .Where(
                    cachedById.ContainsKey)
                .Select(
                    id =>
                        cachedById[id])
                .ToList();

        lastFavouriteVideoSignature =
            GetFavouriteVideoSignature(
                favouriteIds);

        favouriteVideoCacheUpdatedUtc =
            DateTime.UtcNow;

        PersistFavouriteVideoCache();
    }

    private static string GetSubscriptionSignature(
      IEnumerable<string> channelIds)
    {
        return string.Join(
            "\u001F",
            channelIds
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    id =>
                        id,
                    StringComparer.OrdinalIgnoreCase));
    }

    private void RestorePersistedSubscriptionVideoCache()
    {
        if (subscriptionVideoResults is not null)
        {
            return;
        }

        var persisted =
            Plugin.Cfg.SubscriptionVideoChannelCache;

        if (persisted.Count == 0 ||
            Plugin.Cfg.SubscriptionVideoCacheUpdatedUtc ==
            default ||
            string.IsNullOrWhiteSpace(
                Plugin.Cfg.SubscriptionVideoCacheSignature))
        {
            return;
        }

        try
        {
            var restored =
                new Dictionary<string, List<VideoSearchEntry>>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var channel in persisted)
            {
                var videos =
                    channel.Value
                        .Where(
                            video =>
                                !string.IsNullOrWhiteSpace(
                                    video.Url))
                        .Select(
                            video =>
                                new VideoSearchEntry(
                                    video.Title,
                                    video.Url,
                                    video.ChannelName,
                                    video.DurationSeconds is { } seconds
                                        ? TimeSpan.FromSeconds(
                                            seconds)
                                        : null,
                                    video.ThumbnailUrl,
                                    video.ViewCount,
                                    video.UploadDate,
                                    video.ChannelId))
                        .ToList();

                if (videos.Count > 0)
                {
                    restored[channel.Key] =
                        videos;
                }
            }

            if (restored.Count == 0)
            {
                return;
            }

            subscriptionVideoResults =
                restored;

            subscriptionVideoCacheUpdatedUtc =
                Plugin.Cfg.SubscriptionVideoCacheUpdatedUtc;

            lastSubscriptionSignature =
                Plugin.Cfg.SubscriptionVideoCacheSignature!;
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Subscriptions] Failed to restore cached videos: " +
                $"{exception.Message}");

            Plugin.Cfg.SubscriptionVideoChannelCache.Clear();

            Plugin.Cfg.SubscriptionVideoCacheUpdatedUtc =
                default;

            Plugin.Cfg.SubscriptionVideoCacheSignature =
                null;

            Plugin.Cfg.Save();
        }
    }

    private void PersistSubscriptionVideoCache()
    {
        if (subscriptionVideoResults is not { Count: > 0 } ||
            string.IsNullOrWhiteSpace(
                lastSubscriptionSignature) ||
            subscriptionVideoCacheUpdatedUtc ==
            default)
        {
            return;
        }

        Plugin.Cfg.SubscriptionVideoChannelCache =
            subscriptionVideoResults.ToDictionary(
                channel =>
                    channel.Key,
                channel =>
                    channel.Value
                        .Select(
                            video =>
                                new CachedBrowseVideoRecord
                                {
                                    Title =
                                        video.Title,

                                    Url =
                                        video.Url,

                                    ChannelName =
                                        video.ChannelName,

                                    DurationSeconds =
                                        video.Duration?.TotalSeconds,

                                    ThumbnailUrl =
                                        video.ThumbnailUrl,

                                    ViewCount =
                                        video.ViewCount,

                                    UploadDate =
                                        video.UploadDate,

                                    ChannelId =
                                        video.ChannelId
                                })
                        .ToList(),
                StringComparer.OrdinalIgnoreCase);

        Plugin.Cfg.SubscriptionVideoCacheUpdatedUtc =
            subscriptionVideoCacheUpdatedUtc;

        Plugin.Cfg.SubscriptionVideoCacheSignature =
            lastSubscriptionSignature;

        Plugin.Cfg.Save();
    }

    private void ClearPersistedSubscriptionVideoCache()
    {
        Plugin.Cfg.SubscriptionVideoChannelCache.Clear();

        Plugin.Cfg.SubscriptionVideoCacheUpdatedUtc =
            default;

        Plugin.Cfg.SubscriptionVideoCacheSignature =
            null;

        Plugin.Cfg.Save();
    }

    private async Task LoadSubscriptionVideosAsync(
        bool forceRefresh = false)
    {
        RestorePersistedSubscriptionVideoCache();

        var channelIds =
            Plugin.Cfg.SubscribedYouTubeChannelIds
                .Where(
                    id =>
                        !string.IsNullOrWhiteSpace(
                            id))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        var signature =
            GetSubscriptionSignature(
                channelIds);

        var cacheIsUsable =
            !forceRefresh &&
            subscriptionVideoResults is { Count: > 0 } &&
            string.Equals(
                lastSubscriptionSignature,
                signature,
                StringComparison.Ordinal) &&
            DateTime.UtcNow -
            subscriptionVideoCacheUpdatedUtc <
            SubscriptionVideoCacheDuration;

        if (cacheIsUsable)
        {
            subscriptionVideoExpectedChannelCount =
                channelIds.Count;

            isLoadingSubscriptionVideos =
                false;

            return;
        }

        subscriptionVideosCts?.Cancel();
        subscriptionVideosCts?.Dispose();

        var cancellation =
            new CancellationTokenSource();

        subscriptionVideosCts =
            cancellation;

        var loadGeneration =
            Interlocked.Increment(
                ref subscriptionVideoLoadGeneration);

        var previousSignature =
            lastSubscriptionSignature;

        lastSubscriptionSignature =
            signature;

        subscriptionVideoExpectedChannelCount =
            channelIds.Count;

        isLoadingSubscriptionVideos =
            true;

        try
        {
            if (channelIds.Count == 0)
            {
                subscriptionVideoResults =
                    [];

                subscriptionVideoCacheUpdatedUtc =
                    DateTime.UtcNow;

                selectedSubscriptionChannels.Clear();

                ClearPersistedSubscriptionVideoCache();

                return;
            }

            var previousResultsMatch =
                subscriptionVideoResults is { Count: > 0 } &&
                string.Equals(
                    previousSignature,
                    signature,
                    StringComparison.Ordinal);

            var workingResults =
                previousResultsMatch
                    ? new Dictionary<string, List<VideoSearchEntry>>(
                        subscriptionVideoResults!,
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, List<VideoSearchEntry>>(
                        StringComparer.OrdinalIgnoreCase);

            subscriptionVideoResults =
                new Dictionary<string, List<VideoSearchEntry>>(
                    workingResults,
                    StringComparer.OrdinalIgnoreCase);

            const int batchSize =
                3;

            const int resultsPerChannel =
                15;

            for (var batchStart = 0;
                 batchStart < channelIds.Count;
                 batchStart += batchSize)
            {
                cancellation.Token
                    .ThrowIfCancellationRequested();

                var batch =
                    channelIds
                        .Skip(
                            batchStart)
                        .Take(
                            batchSize)
                        .ToList();

                var pendingChannels =
                    batch
                        .Select(
                            async channelId =>
                            {
                                //
                                // Channel enumeration is the fresh discovery
                                // request. Metadata enrichment uses the shared
                                // resolver cache whenever possible.
                                //
                                var uploads =
                                    await searchResolver
                                        .GetChannelUploadsAsync(
                                            channelId,
                                            resultsPerChannel,
                                            cancellation.Token)
                                        .ConfigureAwait(false);

                                var uniqueUploads =
                                    uploads
                                        .GroupBy(
                                            video =>
                                                video.Url,
                                            StringComparer.OrdinalIgnoreCase)
                                        .Select(
                                            group =>
                                                group.First())
                                        .Take(
                                            resultsPerChannel)
                                        .ToList();

                                var enriched =
                                    await Task.WhenAll(
                                            uniqueUploads.Select(
                                                video =>
                                                    searchResolver
                                                        .EnrichSearchResultAsync(
                                                            video,
                                                            cancellation.Token)))
                                        .ConfigureAwait(false);

                                return
                                    (
                                        ChannelId: channelId,
                                        Videos:
                                            enriched
                                                .OrderByDescending(
                                                    video =>
                                                        video.UploadDate ??
                                                        DateTimeOffset.MinValue)
                                                .Take(
                                                    resultsPerChannel)
                                                .ToList()
                                    );
                            })
                        .ToArray();

                var loadedBatch =
                    await Task.WhenAll(
                            pendingChannels)
                        .ConfigureAwait(false);

                cancellation.Token
                    .ThrowIfCancellationRequested();

                if (loadGeneration !=
                    subscriptionVideoLoadGeneration)
                {
                    return;
                }

                var updatedResults =
                    new Dictionary<string, List<VideoSearchEntry>>(
                        workingResults,
                        StringComparer.OrdinalIgnoreCase);

                foreach (var loadedChannel in
                         loadedBatch)
                {
                    updatedResults.Remove(
                        loadedChannel.ChannelId);

                    if (loadedChannel.Videos.Count >
                        0)
                    {
                        updatedResults[
                            loadedChannel.ChannelId] =
                            loadedChannel.Videos;
                    }
                }

                workingResults =
                    updatedResults;

                subscriptionVideoResults =
                    new Dictionary<string, List<VideoSearchEntry>>(
                        workingResults,
                        StringComparer.OrdinalIgnoreCase);
            }

            if (loadGeneration ==
                subscriptionVideoLoadGeneration)
            {
                subscriptionVideoCacheUpdatedUtc =
                    DateTime.UtcNow;

                PersistSubscriptionVideoCache();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal when subscriptions change or a manual refresh replaces it.
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Subscriptions] Failed to load subscription feed: " +
                $"{exception.Message}");

            if (loadGeneration ==
                    subscriptionVideoLoadGeneration &&
                subscriptionVideoResults is null)
            {
                subscriptionVideoResults =
                    [];
            }
        }
        finally
        {
            if (loadGeneration ==
                subscriptionVideoLoadGeneration)
            {
                isLoadingSubscriptionVideos =
                    false;
            }
        }
    }

    private void EnsureSubscriptionVideosLoaded()
    {
        if (isLoadingSubscriptionVideos)
        {
            return;
        }

        RestorePersistedSubscriptionVideoCache();

        var subscribedIds =
            Plugin.Cfg.SubscribedYouTubeChannelIds
                .Where(
                    id =>
                        !string.IsNullOrWhiteSpace(
                            id))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (subscribedIds.Count == 0)
        {
            subscriptionVideoResults =
                [];

            selectedSubscriptionChannels.Clear();

            if (Plugin.Cfg.SubscriptionVideoChannelCache.Count >
                0)
            {
                ClearPersistedSubscriptionVideoCache();
            }

            return;
        }

        var signature =
            GetSubscriptionSignature(
                subscribedIds);

        var cacheMissing =
            subscriptionVideoResults is null ||
            subscriptionVideoResults.Count == 0;

        var cacheExpired =
            !cacheMissing &&
            DateTime.UtcNow -
            subscriptionVideoCacheUpdatedUtc >=
            SubscriptionVideoCacheDuration;

        var subscriptionsChanged =
            !string.Equals(
                signature,
                lastSubscriptionSignature,
                StringComparison.Ordinal);

        if (!cacheMissing &&
            !cacheExpired &&
            !subscriptionsChanged)
        {
            return;
        }

        _ =
            LoadSubscriptionVideosAsync(
                forceRefresh:
                    cacheExpired ||
                    subscriptionsChanged);
    }

    private void DrawBrowseSubscriptionsSection()
    {
        EnsureSubscriptionVideosLoaded();

        var subscribedIds =
            Plugin.Cfg.SubscribedYouTubeChannelIds
                .Where(
                    id =>
                        !string.IsNullOrWhiteSpace(
                            id))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (subscribedIds.Count == 0)
        {
            DrawBrowseVideoEmptyState(
                "No subscriptions yet",
                "Use the + button beside a YouTube creator to subscribe.");

            return;
        }

        DrawSubscriptionFilterPanel();

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(7f)));

        using var content =
            ImRaii.Child(
                "##subscriptionVideoContent",
                new Vector2(
                    0f,
                    -1f),
                false);

        if (!content)
        {
            return;
        }

        if (isLoadingSubscriptionVideos &&
            (subscriptionVideoResults is null ||
             subscriptionVideoResults.Count == 0))
        {
            DrawBrowseVideoSkeletonGrid(
                "Subscription videos",
                "Loading subscriptions");

            return;
        }

        if (subscriptionVideoResults is not
            { Count: > 0 } results)
        {
            DrawBrowseVideoEmptyState(
                "No subscription videos found",
                "Try refreshing the page or subscribing to another creator.");

            return;
        }

        var currentSubscriptions =
            subscribedIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);

        selectedSubscriptionChannels.RemoveWhere(
            channelId =>
                !currentSubscriptions.Contains(
                    channelId));

        var selectedChannels =
            results
                .Where(
                    pair =>
                        currentSubscriptions.Contains(
                            pair.Key) &&
                        (selectedSubscriptionChannels.Count == 0 ||
                         selectedSubscriptionChannels.Contains(
                             pair.Key)))
                .ToList();

        var mixedVideos =
            selectedChannels
                .SelectMany(
                    pair =>
                    {
                        var sortedChannelVideos =
                            subscriptionVideoSortMode switch
                            {
                                1 =>
                                    pair.Value
                                        .OrderByDescending(
                                            video =>
                                                video.UploadDate ??
                                                DateTimeOffset.MinValue),

                                2 =>
                                    pair.Value
                                        .OrderByDescending(
                                            video =>
                                                video.ViewCount ??
                                                0),

                                _ =>
                                    pair.Value
                                        .OrderByDescending(
                                            GetTrendingScore)
                            };

                        return sortedChannelVideos
                            .Take(
                                subscriptionVideosPerChannel);
                    })
                .GroupBy(
                    video =>
                        video.Url,
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    group =>
                        group.First())
                .ToList();

        mixedVideos =
            subscriptionVideoSortMode switch
            {
                1 =>
                    mixedVideos
                        .OrderByDescending(
                            video =>
                                video.UploadDate ??
                                DateTimeOffset.MinValue)
                        .ToList(),

                2 =>
                    mixedVideos
                        .OrderByDescending(
                            video =>
                                video.ViewCount ??
                                0)
                        .ToList(),

                _ =>
                    mixedVideos
                        .OrderByDescending(
                            GetTrendingScore)
                        .ToList()
            };

        SetUiFontScale(
            1.16f);

        ImGui.TextColored(
            Vector4.One,
            "Subscription videos");

        SetUiFontScale(
            1f);

        ImGui.SameLine(
            0f,
            Ui(14f));

        ImGui.TextColored(
            MutedText,
            $"{mixedVideos.Count} videos from " +
            $"{selectedChannels.Count} " +
            $"{(selectedChannels.Count == 1
                ? "subscription"
                : "subscriptions")}");

        if (isLoadingSubscriptionVideos)
        {
            ImGui.SameLine(
                0f,
                Ui(9f));

            DrawBrowseLoadingSpinner(
                Ui(7f));

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"Refreshing subscriptions " +
                    $"({results.Count}/" +
                    $"{subscriptionVideoExpectedChannelCount} loaded)");
            }
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(7f)));

        if (mixedVideos.Count == 0)
        {
            DrawBrowseVideoEmptyState(
                "No matching videos",
                "Try selecting more subscriptions or increasing the videos-per-channel setting.");

            return;
        }

        var contentWidth =
            ImGui.GetContentRegionAvail().X;

        var columns =
            contentWidth >=
            Ui(930f)
                ? 5
                : contentWidth >=
                  Ui(720f)
                    ? 4
                    : 3;

        var cardGap =
            Ui(12f);

        var rowGap =
            cardGap;

        var cardWidth =
            (contentWidth -
             cardGap *
             (columns - 1)) /
            columns;

        var cardHeight =
            Ui(266f);

        var gridStartX =
            ImGui.GetCursorPosX();

        var gridStartY =
            ImGui.GetCursorPosY();

        for (var index = 0;
             index < mixedVideos.Count;
             index++)
        {
            if (index > 0)
            {
                if (index %
                    columns ==
                    0)
                {
                    var rowIndex =
                        index /
                        columns;

                    ImGui.SetCursorPos(
                        new Vector2(
                            gridStartX,
                            gridStartY +
                            rowIndex *
                            (cardHeight +
                             rowGap)));
                }
                else
                {
                    ImGui.SameLine(
                        0f,
                        cardGap);
                }
            }

            ImGui.PushID(
                $"subscriptionVideo_{index}");

            DrawBrowseVideoCardSurface(
     mixedVideos[index],
     null,
     cardWidth,
     cardHeight,
     showFavouriteAction: true);

            ImGui.PopID();
        }
    }

    private void DrawSubscriptionFilterPanel()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var compact =
            availableWidth <
            Ui(1100f);

        var panelHeight =
            compact
                ? Ui(112f)
                : Ui(56f);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                Ui(10f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(
                    12f,
                    9f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    0.035f,
                    0.045f,
                    0.075f,
                    0.82f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.20f)))
        using (
            var panel =
                ImRaii.Child(
                    "##subscriptionFilterPanel",
                    new Vector2(
                        -1f,
                        panelHeight),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!panel)
            {
                return;
            }

            var contentRight =
                ImGui.GetWindowContentRegionMax().X;

            var refreshWidth =
                Ui(116f);

            if (compact)
            {
                DrawSubscriptionChannelSelector(
                    Ui(220f));

                var sortWidth =
                    GetBrowseSortSelectorWidth();

                ImGui.SameLine();

                ImGui.SetCursorPosX(
                    MathF.Max(
                        ImGui.GetCursorPosX(),
                        contentRight -
                        sortWidth));

                DrawSubscriptionSortSelector();

                ImGui.Dummy(
                    new Vector2(
                        0f,
                        Ui(7f)));

                DrawSubscriptionVideoCountSelector();

                ImGui.SameLine();

                ImGui.SetCursorPosX(
                    contentRight -
                    refreshWidth);

                DrawSubscriptionRefreshButton(
                    refreshWidth);
            }
            else
            {
                DrawSubscriptionChannelSelector(
                    Ui(190f));

                ImGui.SameLine(
                    0f,
                    Ui(28f));

                DrawSubscriptionSortSelector();

                ImGui.SameLine(
                    0f,
                    Ui(20f));

                DrawSubscriptionVideoCountSelector();

                ImGui.SameLine();

                ImGui.SetCursorPosX(
                    contentRight -
                    refreshWidth);

                DrawSubscriptionRefreshButton(
                    refreshWidth);
            }
        }
    }

    private void DrawSubscriptionChannelSelector(
        float selectorWidth)
    {
        ImGui.AlignTextToFramePadding();

        ImGui.TextColored(
            MutedText,
            "Subscriptions");

        ImGui.SameLine(
            0f,
            Ui(9f));

        var summary =
            selectedSubscriptionChannels.Count switch
            {
                0 =>
                    "All subscriptions",

                1 =>
                    GetSubscriptionDisplayName(
                        selectedSubscriptionChannels.First()),

                _ =>
                    $"{selectedSubscriptionChannels.Count} selected"
            };

        ImGui.SetNextItemWidth(
            selectorWidth);

        var comboHovered =
            false;

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                Ui(8f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.FrameBg,
                new Vector4(
                    0.055f,
                    0.07f,
                    0.115f,
                    1f))
                .Push(
                    ImGuiCol.FrameBgHovered,
                    CardBgHover)
                .Push(
                    ImGuiCol.FrameBgActive,
                    CardBgHover))
        {
            var comboOpen =
                ImGui.BeginCombo(
                    "##subscriptionMultiSelect",
                    summary);

            comboHovered =
                ImGui.IsItemHovered();

            if (comboOpen)
            {
                var allSubscriptions =
                    selectedSubscriptionChannels.Count ==
                    0;

                using (
                    ImRaii.Disabled(
                        allSubscriptions))
                {
                    if (ImGui.Checkbox(
                            "All subscriptions",
                            ref allSubscriptions))
                    {
                        selectedSubscriptionChannels.Clear();
                    }
                }

                ImGui.Separator();

                foreach (var channelId in
                         Plugin.Cfg.SubscribedYouTubeChannelIds
                             .Distinct(
                                 StringComparer.OrdinalIgnoreCase)
                             .OrderBy(
                                 GetSubscriptionDisplayName,
                                 StringComparer.OrdinalIgnoreCase))
                {
                    var selected =
                        selectedSubscriptionChannels.Contains(
                            channelId);

                    var channelName =
                        GetSubscriptionDisplayName(
                            channelId);

                    if (ImGui.Checkbox(
                            $"{channelName}##subscription_{channelId}",
                            ref selected))
                    {
                        if (selected)
                        {
                            selectedSubscriptionChannels.Add(
                                channelId);
                        }
                        else
                        {
                            selectedSubscriptionChannels.Remove(
                                channelId);
                        }
                    }
                }

                ImGui.EndCombo();
            }
        }

        if (comboHovered)
        {
            ImGui.SetTooltip(
                "Choose which subscribed channels appear in this feed.");
        }
    }

    private string GetSubscriptionDisplayName(
        string channelId)
    {
        if (Plugin.Cfg.SubscribedYouTubeChannelNames.TryGetValue(
                channelId,
                out var savedName) &&
            !string.IsNullOrWhiteSpace(
                savedName))
        {
            return savedName;
        }

        if (subscriptionVideoResults is { } results &&
            results.TryGetValue(
                channelId,
                out var channelVideos))
        {
            var discoveredName =
                channelVideos
                    .Select(
                        video =>
                            video.ChannelName)
                    .FirstOrDefault(
                        name =>
                            !string.IsNullOrWhiteSpace(
                                name));

            if (!string.IsNullOrWhiteSpace(
                    discoveredName))
            {
                return discoveredName;
            }
        }

        return channelId;
    }

    private void DrawSubscriptionSortSelector()
    {
        ImGui.AlignTextToFramePadding();

        ImGui.TextColored(
            MutedText,
            "Sort by");

        ImGui.SameLine(
            0f,
            Ui(9f));

        DrawBrowseFilterPill(
            "Trending",
            "subscriptionSortTrending",
            subscriptionVideoSortMode == 0,
            () =>
                subscriptionVideoSortMode =
                    0);

        ImGui.SameLine(
            0f,
            Ui(5f));

        DrawBrowseFilterPill(
            "Newest",
            "subscriptionSortNewest",
            subscriptionVideoSortMode == 1,
            () =>
                subscriptionVideoSortMode =
                    1);

        ImGui.SameLine(
            0f,
            Ui(5f));

        DrawBrowseFilterPill(
            "Most Viewed",
            "subscriptionSortMostViewed",
            subscriptionVideoSortMode == 2,
            () =>
                subscriptionVideoSortMode =
                    2);
    }

    private void DrawSubscriptionVideoCountSelector()
    {
        ImGui.AlignTextToFramePadding();

        ImGui.TextColored(
            MutedText,
            "Videos per channel");

        ImGui.SameLine(
            0f,
            Ui(9f));

        DrawSubscriptionCountSegment(
            5,
            true,
            false);

        ImGui.SameLine(
            0f,
            0f);

        DrawSubscriptionCountSegment(
            10,
            false,
            false);

        ImGui.SameLine(
            0f,
            0f);

        DrawSubscriptionCountSegment(
            15,
            false,
            true);
    }

    private void DrawSubscriptionCountSegment(
        int count,
        bool first,
        bool last)
    {
        var selected =
            subscriptionVideosPerChannel ==
            count;

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                first ||
                last
                    ? Ui(8f)
                    : 0f))
        using (
            ImRaii.PushColor(
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
                        : CardBgHover)
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentActive))
        {
            if (ImGui.Button(
                    $"{count}##subscriptionCount_{count}",
                    new Vector2(
                        Ui(50f),
                        Ui(32f))))
            {
                subscriptionVideosPerChannel =
                    count;
            }
        }
    }

    private void DrawSubscriptionRefreshButton(
        float width)
    {
        DrawDjActionButton(
            "##refreshSubscriptionVideos",
            FontAwesomeIcon.Sync,
            isLoadingSubscriptionVideos
                ? "Refreshing"
                : "Refresh",
            new Vector2(
                width,
                Ui(34f)),
            isLoadingSubscriptionVideos,
            () =>
            {
                isLoadingSubscriptionVideos =
                    true;

                _ =
                    LoadSubscriptionVideosAsync(
                        forceRefresh: true);
            });
    }

    private void DrawBrowseFavouriteVideosSection()
    {
        var favouriteIds =
            Plugin.Cfg.FavouriteYouTubeVideoIds
                .Where(
                    id =>
                        !string.IsNullOrWhiteSpace(
                            id))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        var favouriteSignature =
            GetFavouriteVideoSignature(
                favouriteIds);

        RestorePersistedFavouriteVideoCache();

        var cacheExpired =
            favouriteVideoResults is not null &&
            DateTime.UtcNow -
            favouriteVideoCacheUpdatedUtc >=
            FavouriteVideoCacheDuration;

        var favouritesChanged =
            !string.Equals(
                favouriteSignature,
                lastFavouriteVideoSignature,
                StringComparison.Ordinal);

        if (!isLoadingFavouriteVideos &&
            (favouriteVideoResults is null ||
             cacheExpired ||
             favouritesChanged))
        {
            _ =
                LoadFavouriteVideosAsync(
                    forceRefresh:
                        cacheExpired ||
                        favouritesChanged);
        }

        if (favouriteIds.Count == 0)
        {
            DrawBrowseVideoEmptyState(
                "No favourite videos yet",
                "Use the heart button on a YouTube video to save it here.");

            return;
        }

        DrawFavouriteFilterPanel();

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(7f)));

        using var content =
            ImRaii.Child(
                "##favouriteVideoContent",
                new Vector2(
                    0f,
                    -1f),
                false);

        if (!content)
        {
            return;
        }

        if (isLoadingFavouriteVideos &&
            (favouriteVideoResults is null ||
             favouriteVideoResults.Count == 0))
        {
            DrawBrowseVideoSkeletonGrid(
                "Favourite videos",
                "Loading favourites");

            return;
        }

        if (favouriteVideoResults is not
            { Count: > 0 } results)
        {
            DrawBrowseVideoEmptyState(
                "Favourite videos could not be loaded",
                "Try refreshing the page.");

            return;
        }

        var favouriteOrder =
            favouriteIds
                .Select(
                    (id, index) =>
                        (
                            Id: id,
                            Index: index
                        ))
                .ToDictionary(
                    item =>
                        item.Id,
                    item =>
                        item.Index,
                    StringComparer.OrdinalIgnoreCase);

        var visibleResults =
            results
                .Where(
                    result =>
                    {
                        var videoId =
                            GetYouTubeVideoId(
                                result.Url);

                        return videoId is not null &&
                               favouriteOrder.ContainsKey(
                                   videoId);
                    })
                .ToList();

        visibleResults =
            favouriteVideoSortMode switch
            {
                0 =>
                    visibleResults
                        .OrderByDescending(
                            GetTrendingScore)
                        .ToList(),

                1 =>
                    visibleResults
                        .OrderByDescending(
                            video =>
                                video.UploadDate ??
                                DateTimeOffset.MinValue)
                        .ToList(),

                2 =>
                    visibleResults
                        .OrderByDescending(
                            video =>
                                video.ViewCount ??
                                0)
                        .ToList(),

                _ =>
                    visibleResults
                        .OrderBy(
                            video =>
                            {
                                var videoId =
                                    GetYouTubeVideoId(
                                        video.Url);

                                return videoId is not null &&
                                       favouriteOrder.TryGetValue(
                                           videoId,
                                           out var index)
                                    ? index
                                    : int.MaxValue;
                            })
                        .ToList()
            };

        SetUiFontScale(
            1.16f);

        ImGui.TextColored(
            Vector4.One,
            "Favourite videos");

        SetUiFontScale(
            1f);

        ImGui.SameLine(
            0f,
            Ui(14f));

        ImGui.TextColored(
            MutedText,
            $"{visibleResults.Count} " +
            $"{(visibleResults.Count == 1
                ? "video"
                : "videos")}");

        if (isLoadingFavouriteVideos)
        {
            ImGui.SameLine(
                0f,
                Ui(9f));

            DrawBrowseLoadingSpinner(
                Ui(7f));

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Checking the favourites cache");
            }
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(7f)));

        if (visibleResults.Count == 0)
        {
            DrawBrowseVideoEmptyState(
                "No favourite videos are available",
                "The missing videos can be retried using Refresh.");

            return;
        }

        var contentWidth =
            ImGui.GetContentRegionAvail().X;

        var columns =
            contentWidth >=
            Ui(930f)
                ? 5
                : contentWidth >=
                  Ui(720f)
                    ? 4
                    : 3;

        var cardGap =
            Ui(12f);

        var rowGap =
            cardGap;

        var cardWidth =
            (contentWidth -
             cardGap *
             (columns - 1)) /
            columns;

        var cardHeight =
            Ui(266f);

        var gridStartX =
            ImGui.GetCursorPosX();

        var gridStartY =
            ImGui.GetCursorPosY();

        for (var index = 0;
             index < visibleResults.Count;
             index++)
        {
            if (index > 0)
            {
                if (index %
                    columns ==
                    0)
                {
                    var rowIndex =
                        index /
                        columns;

                    ImGui.SetCursorPos(
                        new Vector2(
                            gridStartX,
                            gridStartY +
                            rowIndex *
                            (cardHeight +
                             rowGap)));
                }
                else
                {
                    ImGui.SameLine(
                        0f,
                        cardGap);
                }
            }

            ImGui.PushID(
                $"favouriteVideo_{index}");

            DrawBrowseVideoCardSurface(
                visibleResults[index],
                null,
                cardWidth,
                cardHeight,
                showFavouriteAction: true);

            ImGui.PopID();
        }
    }

    private void DrawFavouriteFilterPanel()
    {
        var panelHeight =
            Ui(56f);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                Ui(10f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(
                    12f,
                    9f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    0.035f,
                    0.045f,
                    0.075f,
                    0.82f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.20f)))
        using (
            var panel =
                ImRaii.Child(
                    "##favouriteFilterPanel",
                    new Vector2(
                        -1f,
                        panelHeight),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!panel)
            {
                return;
            }

            DrawFavouriteSortSelector();

            var refreshWidth =
                Ui(116f);

            ImGui.SameLine();

            ImGui.SetCursorPosX(
                ImGui.GetWindowContentRegionMax().X -
                refreshWidth);

            DrawFavouriteRefreshButton(
                refreshWidth);
        }
    }

    private void DrawFavouriteSortSelector()
    {
        ImGui.AlignTextToFramePadding();

        ImGui.TextColored(
            MutedText,
            "Sort by");

        ImGui.SameLine(
            0f,
            Ui(9f));

        DrawBrowseFilterPill(
            "Trending",
            "favouriteSortTrending",
            favouriteVideoSortMode == 0,
            () =>
                favouriteVideoSortMode =
                    0);

        ImGui.SameLine(
            0f,
            Ui(5f));

        DrawBrowseFilterPill(
            "Newest",
            "favouriteSortNewest",
            favouriteVideoSortMode == 1,
            () =>
                favouriteVideoSortMode =
                    1);

        ImGui.SameLine(
            0f,
            Ui(5f));

        DrawBrowseFilterPill(
            "Most Viewed",
            "favouriteSortMostViewed",
            favouriteVideoSortMode == 2,
            () =>
                favouriteVideoSortMode =
                    2);

        ImGui.SameLine(
            0f,
            Ui(5f));

        DrawBrowseFilterPill(
            "Recently Favourited",
            "favouriteSortRecentlyAdded",
            favouriteVideoSortMode == 3,
            () =>
                favouriteVideoSortMode =
                    3);
    }

    private void DrawFavouriteRefreshButton(
     float width)
    {
        DrawDjActionButton(
            "##refreshFavouriteVideos",
            FontAwesomeIcon.Sync,
            isLoadingFavouriteVideos
                ? "Checking"
                : "Refresh",
            new Vector2(
                width,
                Ui(34f)),
            isLoadingFavouriteVideos,
            () =>
            {
                //
                // LoadFavouriteVideosAsync sets the loading flag itself.
                // Setting it here would make its duplicate-load guard return
                // immediately and leave the UI permanently loading.
                //
                _ =
                    LoadFavouriteVideosAsync(
                        forceRefresh: true);
            });
    }

    private void DrawVideoGrid()
    {
        if (viewedChannelId is null)
        {
            DrawBrowseVideoTabs();

            ImGui.Dummy(
                new Vector2(
                    0f,
                    Ui(18f)));
        }
        else
        {
            ImGui.Dummy(
                new Vector2(
                    0f,
                    Ui(8f)));
        }

        var enabledBrowseTopics =
    GetEnabledTrendingTopics();

        var currentBrowseTopicSignature =
            GetBrowseTopicSignature(
                enabledBrowseTopics);

        var browseTopicsChanged =
            browseVideoRequested &&
            !isLoadingBrowseVideos &&
            !string.Equals(
                browseVideoTopicSignature,
                currentBrowseTopicSignature,
                StringComparison.Ordinal);

        if (browseVideoSectionTab == 0 &&
            enabledBrowseTopics.Count > 0 &&
            (!browseVideoRequested ||
             browseTopicsChanged))
        {
            browseVideoRequested =
                true;

            isLoadingBrowseVideos =
                true;

            _ =
                LoadBrowseVideosAsync(
                    forceRefresh:
                        browseTopicsChanged);
        }

        if (viewedChannelId is not null)
        {
            DrawYouTubeChannelPage();

            return;
        }

        switch (browseVideoSectionTab)
        {
            case 1:
                DrawBrowseSubscriptionsSection();

                return;

            case 2:
                DrawBrowseFavouriteVideosSection();

                return;
        }

        if (GetSubscribedTopicCount() == 0)
        {
            browseVideosCts?.Cancel();

            browseVideoRequested =
                false;

            isLoadingBrowseVideos =
                false;

            browseVideoResults =
                null;

            DrawBrowseVideoEmptyState(
                "No subscribed topics yet",
                "Choose your topics during setup or from Settings " +
                "before opening topic recommendations.");

            return;
        }

        DrawBrowseTopicFilterPanel();

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(7f)));

        using var content =
            ImRaii.Child(
                "##browseVideoContent",
                new Vector2(
                    0f,
                    -1f),
                false);

        if (!content)
        {
            return;
        }

        if (isLoadingBrowseVideos &&
      (browseVideoResults is null ||
       browseVideoResults.Count == 0))
        {
            DrawBrowseVideoSkeletonGrid();

            return;
        }

        if (browseVideoResults is null ||
            browseVideoResults.Count == 0)
        {
            DrawBrowseVideoEmptyState(
                "No videos found",
                "Try refreshing the page or selecting different topics.");

            return;
        }

        //
        // Remove selections that are no longer present in the loaded
        // topic collection.
        //

        browseSelectedTopics.RemoveWhere(
            topic =>
                !browseVideoResults.ContainsKey(
                    topic));

        var selectedTopicResults =
            browseVideoResults
                .Where(
                    pair =>
                        browseSelectedTopics.Count ==
                        0 ||
                        browseSelectedTopics.Contains(
                            pair.Key))
                .ToList();

        //
        // Sort each topic before applying the per-topic limit. The
        // resulting candidates are then combined, deduplicated and
        // sorted globally.
        //

        var mixedVideos =
            selectedTopicResults
                .SelectMany(
                    pair =>
                    {
                        var sortedTopicVideos =
                            browseVideoSortMode switch
                            {
                                1 =>
                                    pair.Value
                                        .OrderByDescending(
                                            video =>
                                                video.UploadDate ??
                                                DateTime.MinValue),

                                2 =>
                                    pair.Value
                                        .OrderByDescending(
                                            video =>
                                                video.ViewCount ??
                                                0),

                                _ =>
                                    pair.Value
                                        .OrderByDescending(
                                            GetTrendingScore)
                            };

                        return sortedTopicVideos
                            .Take(
                                browseVideosPerTopic)
                            .Select(
                                video =>
                                    (
                                        Video: video,
                                        Topic: pair.Key
                                    ));
                    })
                .GroupBy(
                    item =>
                        item.Video.Url,
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    group =>
                        group.First())
                .ToList();

        mixedVideos =
            browseVideoSortMode switch
            {
                1 =>
                    mixedVideos
                        .OrderByDescending(
                            item =>
                                item.Video.UploadDate ??
                                DateTime.MinValue)
                        .ToList(),

                2 =>
                    mixedVideos
                        .OrderByDescending(
                            item =>
                                item.Video.ViewCount ??
                                0)
                        .ToList(),

                _ =>
                    mixedVideos
                        .OrderByDescending(
                            item =>
                                GetTrendingScore(
                                    item.Video))
                        .ToList()
            };

      

        SetUiFontScale(
            1.16f);

        ImGui.TextColored(
            Vector4.One,
            "Recommended videos");

        SetUiFontScale(
            1f);

        ImGui.SameLine(
            0f,
            Ui(14f));

        var displayedTopicCount =
            selectedTopicResults.Count;

        ImGui.TextColored(
     MutedText,
     $"{mixedVideos.Count} videos from " +
     $"{displayedTopicCount} " +
     $"{(displayedTopicCount == 1 ? "topic" : "topics")}");

        if (isLoadingBrowseVideos)
        {
            ImGui.SameLine(
                0f,
                Ui(9f));

            DrawBrowseLoadingSpinner(
                Ui(7f));

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"Loading more videos " +
                    $"({browseVideoResults.Count}/" +
                    $"{browseVideoExpectedTopicCount} topics)");
            }
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(7f)));

        if (mixedVideos.Count == 0)
        {
            DrawBrowseVideoEmptyState(
                "No matching videos",
                "Try selecting more topics or increasing the videos-per-topic setting.");

            return;
        }

        var contentWidth =
            ImGui.GetContentRegionAvail().X;

        var columns =
            contentWidth >=
            Ui(930f)
                ? 5
                : contentWidth >=
                  Ui(720f)
                    ? 4
                    : 3;

        var cardGap =
            Ui(12f);

        var rowGap =
            cardGap;

        var cardWidth =
            (contentWidth -
             cardGap *
             (columns - 1)) /
            columns;

        var cardHeight =
            Ui(266f);

        var gridStartX =
    ImGui.GetCursorPosX();

        var gridStartY =
            ImGui.GetCursorPosY();

        for (var index = 0;
      index < mixedVideos.Count;
      index++)
        {
            if (index > 0)
            {
                if (index %
                    columns ==
                    0)
                {
                    var rowIndex =
                        index /
                        columns;

                    ImGui.SetCursorPos(
                        new Vector2(
                            gridStartX,
                            gridStartY +
                            rowIndex *
                            (cardHeight +
                             rowGap)));
                }
                else
                {
                    ImGui.SameLine(
                        0f,
                        cardGap);
                }
            }

            var item =
                mixedVideos[index];

            ImGui.PushID(
                $"browseMixed_{index}");

            DrawBrowseVideoCardSurface(
     item.Video,
     item.Topic,
     cardWidth,
     cardHeight,
     showFavouriteAction: true);

            ImGui.PopID();
        }
    }

    private void DrawBrowseVideoSkeletonGrid(
    string heading = "Recommended videos",
    string loadingLabel = "Loading recommendations")
    {
        SetUiFontScale(
            1.16f);

        ImGui.TextColored(
            Vector4.One,
            heading);

        SetUiFontScale(
            1f);

        ImGui.SameLine(
            0f,
            Ui(14f));

        ImGui.TextColored(
            MutedText,
            loadingLabel);

        ImGui.SameLine(
            0f,
            Ui(9f));

        DrawBrowseLoadingSpinner(
            Ui(7f));

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(7f)));

        var contentWidth =
            ImGui.GetContentRegionAvail().X;

        var columns =
            contentWidth >=
            Ui(930f)
                ? 5
                : contentWidth >=
                  Ui(720f)
                    ? 4
                    : 3;

        var cardGap =
            Ui(12f);

        var rowGap =
            cardGap;

        var cardWidth =
            (contentWidth -
             cardGap *
             (columns - 1)) /
            columns;

        var cardHeight =
            Ui(266f);

        var thumbnailHeight =
            cardWidth *
            9f /
            16f;

        var pulse =
            0.5f +
            0.5f *
            MathF.Sin(
                (float)ImGui.GetTime() *
                3.2f);

        var baseAlpha =
            0.10f +
            pulse *
            0.055f;

        var highlightAlpha =
            0.14f +
            pulse *
            0.07f;

        var drawList =
            ImGui.GetWindowDrawList();

        var cardCount =
            columns *
            2;

        var gridStartX =
    ImGui.GetCursorPosX();

        var gridStartY =
            ImGui.GetCursorPosY();

        for (var index = 0;
      index < cardCount;
      index++)
        {
            if (index > 0)
            {
                if (index %
                    columns ==
                    0)
                {
                    var rowIndex =
                        index /
                        columns;

                    ImGui.SetCursorPos(
                        new Vector2(
                            gridStartX,
                            gridStartY +
                            rowIndex *
                            (cardHeight +
                             rowGap)));
                }
                else
                {
                    ImGui.SameLine(
                        0f,
                        cardGap);
                }
            }

            var origin =
                ImGui.GetCursorScreenPos();

            var bottomRight =
                origin +
                new Vector2(
                    cardWidth,
                    cardHeight);

            drawList.AddRectFilled(
                origin,
                bottomRight,
                ImGui.GetColorU32(
                    new Vector4(
                        0.055f,
                        0.07f,
                        0.115f,
                        0.72f)),
                Ui(9f));

            drawList.AddRect(
                origin,
                bottomRight,
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.13f)),
                Ui(9f),
                ImDrawFlags.None,
                Ui(1f));

            var inset =
                Ui(7f);

            var thumbnailStart =
                origin +
                new Vector2(
                    inset,
                    inset);

            var thumbnailEnd =
                new Vector2(
                    origin.X +
                    cardWidth -
                    inset,
                    thumbnailStart.Y +
                    thumbnailHeight -
                    inset);

            drawList.AddRectFilled(
                thumbnailStart,
                thumbnailEnd,
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        highlightAlpha)),
                Ui(7f));

            var textStartX =
                origin.X +
                Ui(10f);

            var firstLineY =
                thumbnailEnd.Y +
                Ui(17f);

            drawList.AddRectFilled(
                new Vector2(
                    textStartX,
                    firstLineY),
                new Vector2(
                    origin.X +
                    cardWidth *
                    0.83f,
                    firstLineY +
                    Ui(10f)),
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        highlightAlpha)),
                Ui(4f));

            drawList.AddRectFilled(
                new Vector2(
                    textStartX,
                    firstLineY +
                    Ui(18f)),
                new Vector2(
                    origin.X +
                    cardWidth *
                    0.64f,
                    firstLineY +
                    Ui(27f)),
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        baseAlpha)),
                Ui(4f));

            drawList.AddRectFilled(
                new Vector2(
                    textStartX,
                    firstLineY +
                    Ui(49f)),
                new Vector2(
                    origin.X +
                    cardWidth *
                    0.48f,
                    firstLineY +
                    Ui(58f)),
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        baseAlpha)),
                Ui(4f));

            drawList.AddRectFilled(
                new Vector2(
                    textStartX,
                    bottomRight.Y -
                    Ui(43f)),
                new Vector2(
                    bottomRight.X -
                    Ui(10f),
                    bottomRight.Y -
                    Ui(9f)),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        baseAlpha)),
                Ui(7f));

            ImGui.Dummy(
                new Vector2(
                    cardWidth,
                    cardHeight));
        }
    }

    private void DrawBrowseLoadingSpinner(
        float radius)
    {
        var size =
            radius *
            2f +
            Ui(2f);

        var origin =
            ImGui.GetCursorScreenPos();

        var center =
            origin +
            new Vector2(
                size * 0.5f,
                size * 0.5f);

        var rotation =
            (float)ImGui.GetTime() *
            4.5f;

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddCircle(
            center,
            radius,
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.20f)),
            24,
            Ui(2f));

        drawList.PathArcTo(
            center,
            radius,
            rotation,
            rotation +
            MathF.PI *
            1.45f,
            18);

        drawList.PathStroke(
            ImGui.GetColorU32(
                AccentHover),
            ImDrawFlags.None,
            Ui(2.2f));

        ImGui.Dummy(
            new Vector2(
                size,
                size));
    }

    private void DrawBrowseTopicFilterPanel()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var compact =
            availableWidth <
            Ui(1060f);

        var panelHeight =
            compact
                ? Ui(112f)
                : Ui(56f);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                Ui(10f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(
                    12f,
                    9f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    0.035f,
                    0.045f,
                    0.075f,
                    0.82f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.20f)))
        using (
            var panel =
                ImRaii.Child(
                    "##browseTopicFilterPanel",
                    new Vector2(
                        -1f,
                        panelHeight),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!panel)
            {
                return;
            }

            var contentRight =
                ImGui.GetWindowContentRegionMax().X;

            var refreshWidth =
                Ui(116f);

            if (compact)
            {
                //
                // First row: topics left, sorting right.
                //
                DrawBrowseTopicSelector(
                    Ui(220f));

                var sortWidth =
                    GetBrowseSortSelectorWidth();

                ImGui.SameLine();

                ImGui.SetCursorPosX(
                    MathF.Max(
                        ImGui.GetCursorPosX(),
                        contentRight -
                        sortWidth));

                DrawBrowseSortSelector();

                //
                // Second row: result count left, refresh right.
                //
                ImGui.Dummy(
                    new Vector2(
                        0f,
                        Ui(7f)));

                DrawBrowseVideoCountSelector();

                ImGui.SameLine();

                ImGui.SetCursorPosX(
                    contentRight -
                    refreshWidth);

                DrawBrowseRefreshButton(
                    refreshWidth);
            }
            else
            {
                DrawBrowseTopicSelector(
                    Ui(190f));

                ImGui.SameLine(
                    0f,
                    Ui(28f));

                DrawBrowseSortSelector();

                ImGui.SameLine(
                    0f,
                    Ui(20f));

                DrawBrowseVideoCountSelector();

                ImGui.SameLine();

                ImGui.SetCursorPosX(
                    contentRight -
                    refreshWidth);

                DrawBrowseRefreshButton(
                    refreshWidth);
            }
        }
    }

    private float GetBrowseSortSelectorWidth()
    {
        return
            ImGui.CalcTextSize(
                "Sort by").X +
            Ui(9f) +
            ImGui.CalcTextSize(
                "Trending").X +
            Ui(24f) +
            Ui(5f) +
            ImGui.CalcTextSize(
                "Newest").X +
            Ui(24f) +
            Ui(5f) +
            ImGui.CalcTextSize(
                "Most Viewed").X +
            Ui(24f);
    }

    private void DrawBrowseTopicSelector(
    float selectorWidth)
    {
        ImGui.AlignTextToFramePadding();

        ImGui.TextColored(
            MutedText,
            "Topics");

        ImGui.SameLine(
            0f,
            Ui(9f));

        var summary =
            browseSelectedTopics.Count switch
            {
                0 =>
                    "All topics",

                1 =>
                    browseSelectedTopics.First(),

                _ =>
                    $"{browseSelectedTopics.Count} topics selected"
            };

        ImGui.SetNextItemWidth(
            selectorWidth);

        var comboOpen =
            false;

        var comboHovered =
            false;

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                Ui(8f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.FrameBg,
                new Vector4(
                    0.055f,
                    0.07f,
                    0.115f,
                    1f))
                .Push(
                    ImGuiCol.FrameBgHovered,
                    CardBgHover)
                .Push(
                    ImGuiCol.FrameBgActive,
                    CardBgHover))
        {
            comboOpen =
                ImGui.BeginCombo(
                    "##browseTopicMultiSelect",
                    summary);

            comboHovered =
                ImGui.IsItemHovered();

            if (comboOpen)
            {
                var allTopics =
                    browseSelectedTopics.Count ==
                    0;

                using (
                    ImRaii.Disabled(
                        allTopics))
                {
                    if (ImGui.Checkbox(
                            "All topics",
                            ref allTopics))
                    {
                        browseSelectedTopics.Clear();
                    }
                }

                ImGui.Separator();

                if (browseVideoResults is { Count: > 0 })
                {
                    foreach (var topicName in
                             browseVideoResults.Keys
                                 .OrderBy(
                                     name =>
                                         name,
                                     StringComparer.OrdinalIgnoreCase))
                    {
                        var selected =
                            browseSelectedTopics.Contains(
                                topicName);

                        if (ImGui.Checkbox(
                                $"{topicName}##browseMulti_{topicName}",
                                ref selected))
                        {
                            if (selected)
                            {
                                browseSelectedTopics.Add(
                                    topicName);
                            }
                            else
                            {
                                browseSelectedTopics.Remove(
                                    topicName);
                            }
                        }
                    }
                }

                ImGui.EndCombo();
            }
        }

        if (comboHovered)
        {
            ImGui.SetTooltip(
                "Available topics can be adjusted in Settings.");
        }
    }

    private void DrawBrowseSortSelector()
    {
        ImGui.AlignTextToFramePadding();

        ImGui.TextColored(
            MutedText,
            "Sort by");

        ImGui.SameLine(
            0f,
            Ui(9f));

        DrawBrowseFilterPill(
            "Trending",
            "sortTrending",
            browseVideoSortMode == 0,
            () =>
                browseVideoSortMode =
                    0);

        ImGui.SameLine(
            0f,
            Ui(5f));

        DrawBrowseFilterPill(
            "Newest",
            "sortNewest",
            browseVideoSortMode == 1,
            () =>
                browseVideoSortMode =
                    1);

        ImGui.SameLine(
            0f,
            Ui(5f));

        DrawBrowseFilterPill(
            "Most Viewed",
            "sortMostViewed",
            browseVideoSortMode == 2,
            () =>
                browseVideoSortMode =
                    2);
    }

    private void DrawBrowseVideoCountSelector()
    {
        ImGui.AlignTextToFramePadding();

        ImGui.TextColored(
            MutedText,
            "Videos per topic");

        ImGui.SameLine(
            0f,
            Ui(9f));

        DrawBrowseCountSegment(
            5,
            true,
            false);

        ImGui.SameLine(
            0f,
            0f);

        DrawBrowseCountSegment(
            10,
            false,
            false);

        ImGui.SameLine(
            0f,
            0f);

        DrawBrowseCountSegment(
            15,
            false,
            true);
    }

    private void DrawBrowseCountSegment(
        int count,
        bool first,
        bool last)
    {
        var selected =
            browseVideosPerTopic ==
            count;

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                first ||
                last
                    ? Ui(8f)
                    : 0f))
        using (
            ImRaii.PushColor(
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
                        : CardBgHover)
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentActive))
        {
            if (ImGui.Button(
                    $"{count}##browseCount_{count}",
                    new Vector2(
                        Ui(50f),
                        Ui(32f))))
            {
                browseVideosPerTopic =
                    count;
            }
        }
    }

    private void DrawBrowseRefreshButton(
        float width)
    {
        DrawDjActionButton(
            "##refreshBrowseVideos",
            FontAwesomeIcon.Sync,
            isLoadingBrowseVideos
                ? "Refreshing"
                : "Refresh",
            new Vector2(
                width,
                Ui(34f)),
            isLoadingBrowseVideos,
            () =>
            {
                isLoadingBrowseVideos =
                    true;

                _ =
                    LoadBrowseVideosAsync(
                        true);
            });
    }

    private void DrawBrowseFilterPill(
    string label,
    string id,
    bool selected,
    Action action)
    {
        var size =
            new Vector2(
                ImGui.CalcTextSize(
                    label).X +
                Ui(24f),
                Ui(32f));

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                Ui(9f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.Button,
                selected
                    ? Accent
                    : CardBg)
                .Push(
                    ImGuiCol.ButtonHovered,
                    selected
                        ? AccentHover
                        : CardBgHover)
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentActive))
        {
            if (ImGui.Button(
                    $"{label}##{id}",
                    size))
            {
                action();
            }
        }
    }

    private void DrawBrowseTopicSectionHeading(
        string topicName,
        int videoCount,
        bool showViewAll)
    {
        var headingOrigin =
            ImGui.GetCursorScreenPos();

        var topicIcon =
            GetBrowseTopicIcon(
                topicName);

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            SetUiFontScale(
                1.18f);

            ImGui.TextColored(
                AccentHover,
                topicIcon.ToIconString());

            SetUiFontScale(
                1f);
        }

        ImGui.SameLine(
            0f,
            Ui(9f));

        SetUiFontScale(
            1.10f);

        ImGui.TextColored(
            Vector4.One,
            topicName);

        SetUiFontScale(
            1f);

        ImGui.SameLine(
            0f,
            Ui(14f));

        ImGui.TextColored(
            MutedText,
            $"{videoCount} videos");

        if (showViewAll)
        {
            var buttonWidth =
                Ui(106f);

            ImGui.SetCursorScreenPos(
                new Vector2(
                    headingOrigin.X +
                    ImGui.GetContentRegionAvail().X +
                    ImGui.GetCursorPosX() -
                    buttonWidth,

                    headingOrigin.Y -
                    Ui(4f)));

            DrawDjActionButton(
                $"##viewAll_{topicName}",
                FontAwesomeIcon.ArrowRight,
                "View all",
                new Vector2(
                    buttonWidth,
                    Ui(34f)),
                false,
                () =>
                    browseVideoTopicFilter =
                        topicName);
        }

        ImGui.SetCursorScreenPos(
            headingOrigin +
            new Vector2(
                0f,
                Ui(36f)));
    }

    private static FontAwesomeIcon GetBrowseTopicIcon(
        string topicName)
    {
        return topicName switch
        {
            "Gaming" =>
                FontAwesomeIcon.Gamepad,

            "MMORPG" =>
                FontAwesomeIcon.Users,

            "Final Fantasy" =>
                FontAwesomeIcon.Magic,

            "Anime" =>
                FontAwesomeIcon.Star,

            "Movies" =>
                FontAwesomeIcon.Film,

            "TV Shows" =>
                FontAwesomeIcon.Tv,

            "Music" =>
                FontAwesomeIcon.Music,

            "Memes" =>
                FontAwesomeIcon.Grin,

            "Wildlife" or
            "Pets" =>
                FontAwesomeIcon.Paw,

            "Architecture" =>
                FontAwesomeIcon.Building,

            "Science" =>
                FontAwesomeIcon.Flask,

            "Space" =>
                FontAwesomeIcon.Rocket,

            "History" =>
                FontAwesomeIcon.Landmark,

            "Technology" =>
                FontAwesomeIcon.Microchip,

            "Food" =>
                FontAwesomeIcon.Utensils,

            "Travel" =>
                FontAwesomeIcon.Plane,

            "Cars" =>
                FontAwesomeIcon.Car,

            "Sports" =>
                FontAwesomeIcon.Futbol,

            _ =>
                FontAwesomeIcon.PlayCircle
        };
    }

    private void DrawBrowseVideoCardSurface(
    VideoSearchEntry videoResult,
    string? topic,
    float width,
    float height,
    bool showFavouriteAction = false)
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
            browseLayout: true,
            browseTopic: topic,
            showBrowseFavouriteAction:
                showFavouriteAction);

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
    }

    private void DrawBrowseVideoEmptyState(
        string title,
        string description)
    {
        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(24f)));

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                Ui(10f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(
                    20f,
                    18f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    CardBg.X,
                    CardBg.Y,
                    CardBg.Z,
                    0.70f)))
        using (
            var emptyState =
                ImRaii.Child(
                    "##browseVideosEmptyState",
                    new Vector2(
                        -1f,
                        Ui(112f)),
                    false,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!emptyState)
            {
                return;
            }

            SetUiFontScale(
                1.08f);

            ImGui.TextColored(
                Vector4.One,
                title);

            SetUiFontScale(
                1f);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    Ui(6f)));

            ImGui.TextColored(
                MutedText,
                description);
        }
    }
}