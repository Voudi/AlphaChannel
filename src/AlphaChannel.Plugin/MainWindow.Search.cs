using System.Collections.Concurrent;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Text.Json;


namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly VideoUrlResolver searchResolver = new();
    private readonly TwitchChannelChecker twitchChecker = new();
    private string searchQuery = string.Empty;

    // Written from RunSearchAsync's continuation, which resumes on an arbitrary thread pool thread
    // (not the main thread Draw() runs on) - same reasoning as Plugin.cs's pendingRemoteState.
    private enum VideoSearchOrder
    {
        Relevance,
        Shortest,
        Longest
    }

    private enum VideoDurationFilter
    {
        Any,
        UnderFiveMinutes,
        FiveToTwentyMinutes,
        OverTwentyMinutes
    }

    private volatile bool isSearching;

    private volatile List<VideoSearchEntry>? searchResults;

    private VideoSearchOrder youtubeSearchOrder =
        VideoSearchOrder.Relevance;

    private VideoDurationFilter youtubeDurationFilter =
        VideoDurationFilter.Any;

    // ---------------------------------------------------------
    // Shared Topics cache / Home YouTube shelf
    // ---------------------------------------------------------
    //
    // Home never performs its own YouTube searches. It selects up to five
    // enabled topics and builds its shelf entirely from the shared Browse
    // Topics cache.
    //
    private volatile bool isLoadingHomeYouTube;

    private volatile List<VideoSearchEntry>?
        homeYouTubeResults;

    private readonly Random trendingRandom =
        new();

    private readonly HashSet<string>
        homeYouTubeSelectedTopics =
            new(StringComparer.OrdinalIgnoreCase);

    //
    // Ensures startup cache preparation runs once per plugin load.
    //
    private bool topicVideoStartupRequested;

    //
    // Debounces topic changes made through Settings so several nearby changes
    // result in one cache refresh.
    //
    private int topicSettingsRefreshGeneration;

    // Browse Videos full-page discovery
    private static readonly TimeSpan BrowseVideoCacheDuration =
        TimeSpan.FromHours(3);

    private volatile bool isLoadingBrowseVideos;

    private volatile Dictionary<string, List<VideoSearchEntry>>?
        browseVideoResults;

    private DateTime browseVideoCacheTime;

    //
    // Identifies the exact subscribed-topic collection used to build the
    // current Browse cache. Changing topic settings therefore invalidates
    // the cache immediately, even when it is less than three hours old.
    //
    private string? browseVideoTopicSignature;

    private int browseVideoExpectedTopicCount;

    private bool browseVideoRequested;

    private CancellationTokenSource? browseVideosCts;

    //
    // Prevents an older cancelled request from publishing over a newer
    // refresh if cancellation and completion happen at nearly the same time.
    //
    private int browseVideoLoadGeneration;

    // Youtube Trending
    // s
    private sealed record TrendingTopic(
        string Name,
        string[] SearchQueries);

    // FFXIV-specific discovery shelf.
    private volatile bool isLoadingFfxivYouTube;
    private volatile List<VideoSearchEntry>? ffxivYouTubeResults;
    private bool ffxivYouTubeRequested;

    // Dailymotion search (kept separate from YouTube)
    private string dailymotionSearchQuery =
        string.Empty;

    private volatile bool isSearchingDailymotion;

    private volatile List<VideoSearchEntry>?
        dailymotionSearchResults;

    private volatile string?
        dailymotionSearchError;

    private VideoSearchOrder dailymotionSearchOrder =
        VideoSearchOrder.Relevance;

    private VideoDurationFilter dailymotionDurationFilter =
        VideoDurationFilter.Any;

    private string twitchChannelInput =
        string.Empty;

    private volatile bool isCheckingTwitch;

    private volatile TwitchStreamInfo?
        twitchResult;

    private volatile string?
        twitchError;

    private volatile bool twitchResultIsOffline;

    private volatile string?
        twitchCheckedChannelName;

    private bool trendingDirty =
        true;

    private TwitchStreamDto[] trendingStreams =
        [];

    private const int MaximumFavouriteTwitchChannels =
        20;

    private static readonly TimeSpan TwitchFavouriteCacheDuration =
        TimeSpan.FromMinutes(15);

    private sealed record TwitchFavouriteStatus(
        string ChannelName,
        TwitchStreamInfo? Stream,
        bool IsOffline,
        string? Error,
        DateTime CheckedAtUtc);

    private readonly ConcurrentDictionary<string, TwitchFavouriteStatus>
        twitchFavouriteStatuses =
            new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim twitchFavouriteCheckGate =
        new(
            2,
            2);

    private volatile bool isRefreshingTwitchFavourites;

    private DateTime? twitchFavouritesLastRefreshUtc;

    private static List<VideoSearchEntry> FilterAndOrderSearchResults(
    IReadOnlyList<VideoSearchEntry> results,
    VideoSearchOrder order,
    VideoDurationFilter durationFilter)
    {
        IEnumerable<VideoSearchEntry> filtered =
            results;

        filtered =
            durationFilter switch
            {
                VideoDurationFilter.UnderFiveMinutes =>
                    filtered.Where(
                        result =>
                            result.Duration is { } duration &&
                            duration <
                            TimeSpan.FromMinutes(5)),

                VideoDurationFilter.FiveToTwentyMinutes =>
                    filtered.Where(
                        result =>
                            result.Duration is { } duration &&
                            duration >=
                            TimeSpan.FromMinutes(5) &&
                            duration <=
                            TimeSpan.FromMinutes(20)),

                VideoDurationFilter.OverTwentyMinutes =>
                    filtered.Where(
                        result =>
                            result.Duration is { } duration &&
                            duration >
                            TimeSpan.FromMinutes(20)),

                _ =>
                    filtered
            };

        filtered =
            order switch
            {
                VideoSearchOrder.Shortest =>
                    filtered
                        .OrderBy(
                            result =>
                                result.Duration ??
                                TimeSpan.MaxValue),

                VideoSearchOrder.Longest =>
                    filtered
                        .OrderByDescending(
                            result =>
                                result.Duration ??
                                TimeSpan.Zero),

                _ =>
                    filtered
            };

        return filtered.ToList();
    }

    private static string GetVideoSearchOrderName(
        VideoSearchOrder order)
    {
        return order switch
        {
            VideoSearchOrder.Shortest =>
                "Shortest",

            VideoSearchOrder.Longest =>
                "Longest",

            _ =>
                "Relevance"
        };
    }

    private static string GetVideoDurationFilterName(
        VideoDurationFilter filter)
    {
        return filter switch
        {
            VideoDurationFilter.UnderFiveMinutes =>
                "Under 5 min",

            VideoDurationFilter.FiveToTwentyMinutes =>
                "5–20 min",

            VideoDurationFilter.OverTwentyMinutes =>
                "Over 20 min",

            _ =>
                "Any"
        };
    }

    private void DrawVideoSearchFilters(
    string id,
    ref VideoSearchOrder order,
    ref VideoDurationFilter durationFilter)
    {
        var orderWidth = Ui(170f);

        var durationWidth = Ui(160f);

        const float gap =
            10f;

        var controlsWidth =
            orderWidth +
            durationWidth +
            gap;

        var rightEdge =
            ImGui.GetWindowPos().X +
            ImGui.GetContentRegionMax().X;

        //
        // Keep both controls on the same row as the result count.
        //

        ImGui.SameLine();

        var currentPosition =
            ImGui.GetCursorScreenPos();

        ImGui.SetCursorScreenPos(
            new Vector2(
                rightEdge -
                controlsWidth,
                currentPosition.Y));

        ImGui.SetNextItemWidth(
            orderWidth);

        if (ImGui.BeginCombo(
                $"##searchOrder_{id}",
                $"Order: {GetVideoSearchOrderName(order)}"))
        {
            foreach (var option in new[]
                     {
                     VideoSearchOrder.Relevance,
                     VideoSearchOrder.Shortest,
                     VideoSearchOrder.Longest
                 })
            {
                var selected =
                    order == option;

                if (ImGui.Selectable(
                        GetVideoSearchOrderName(option),
                        selected))
                {
                    order =
                        option;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine(
            0f,
            gap);

        ImGui.SetNextItemWidth(
            durationWidth);

        if (ImGui.BeginCombo(
                $"##searchDuration_{id}",
                $"Duration: {GetVideoDurationFilterName(durationFilter)}"))
        {
            foreach (var option in new[]
                     {
                     VideoDurationFilter.Any,
                     VideoDurationFilter.UnderFiveMinutes,
                     VideoDurationFilter.FiveToTwentyMinutes,
                     VideoDurationFilter.OverTwentyMinutes
                 })
            {
                var selected =
                    durationFilter == option;

                if (ImGui.Selectable(
                        GetVideoDurationFilterName(option),
                        selected))
                {
                    durationFilter =
                        option;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }
    }

    private void DrawVideoSearchResultsGrid(
    string id,
    IReadOnlyList<VideoSearchEntry> results)
    {
        var resultsHeight =
            MathF.Max(
                180f,
                ImGui.GetContentRegionAvail().Y -
                8f);

        using var resultsChild =
            ImRaii.Child(
                $"##videoSearchResults_{id}",
                new Vector2(
                    -1f,
                    resultsHeight),
                false,
                ImGuiWindowFlags.None);

        if (!resultsChild)
        {
            return;
        }

        var columnGap = Ui(10f);

        var rowGap = Ui(10f);

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var cardWidth =
            (availableWidth -
             columnGap) /
            2f;

        for (var index = 0;
             index < results.Count;
             index++)
        {
            if ((index % 2) != 0)
            {
                ImGui.SameLine(
                    0f,
                    columnGap);
            }

            DrawVideoSearchResultCard(
                id,
                index,
                results[index],
                new Vector2(
                    cardWidth,
                    Ui(112f)));

            var completedRow =
                (index % 2) != 0;

            var finalCard =
                index ==
                results.Count - 1;

            if (completedRow ||
                finalCard)
            {
                ImGui.Dummy(
                    new Vector2(
                        0f,
                        rowGap));
            }
        }
    }

    private void DrawVideoSearchResultCard(
        string sourceId,
        int index,
        VideoSearchEntry result,
        Vector2 size)
    {
        ImGui.PushID(
            $"{sourceId}_{index}");

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
        using (var card = ImRaii.Child(
                   "##videoSearchCard",
                   size,
                   false,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (card)
            {
                var origin =
                    ImGui.GetCursorScreenPos();

                var drawList =
                    ImGui.GetWindowDrawList();

                var innerPadding = Ui(8f);

                var thumbnailHeight =
                    size.Y -
                    (innerPadding * 2f);

                var thumbnailWidth =
                    thumbnailHeight *
                    (16f / 9f);

                var thumbnailMinimum =
                    new Vector2(
                        origin.X +
                        innerPadding,
                        origin.Y +
                        innerPadding);

                var thumbnailMaximum =
                    thumbnailMinimum +
                    new Vector2(
                        thumbnailWidth,
                        thumbnailHeight);

                //
                // Draw a quiet placeholder while the thumbnail downloads.
                //

                drawList.AddRectFilled(
                    thumbnailMinimum,
                    thumbnailMaximum,
                    ImGui.GetColorU32(
                        new Vector4(
                            0.025f,
                            0.035f,
                            0.065f,
                            1f)),
                    7f);

                var thumbnail =
                    thumbnails.Get(
                        result.ThumbnailUrl);

                if (thumbnail is not null)
                {
                    drawList.AddImageRounded(
                        thumbnail.Handle,
                        thumbnailMinimum,
                        thumbnailMaximum,
                        Vector2.Zero,
                        Vector2.One,
                        uint.MaxValue,
                        7f);
                }

                var contentX =
                    thumbnailMaximum.X +
                    12f;

                var contentRight =
                    origin.X +
                    size.X -
                    10f;

                //
                // Title
                //

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        contentX,
                        origin.Y + Ui(9f)));

                ImGui.PushTextWrapPos(
                    contentRight);

                ImGui.TextColored(
                    Vector4.One,
                    TruncateVideoTitle(
                        result.Title));

                ImGui.PopTextWrapPos();

                //
                // Creator and duration
                //

                var metadataParts =
                    new List<string> { result.ChannelName };

                if (result.Duration is { } duration)
                {
                    metadataParts.Add(
                        FormatTime((float)duration.TotalSeconds));
                }

                if (result.ViewCount is { } views)
                {
                    metadataParts.Add(
                        FormatViewCount(views));
                }

                var metadata =
                    string.Join("  •  ", metadataParts);

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        contentX,
                        origin.Y + Ui(46f)));

                SetUiFontScale(
                    0.84f);

                ImGui.TextColored(
                    MutedText,
                    metadata);

                SetUiFontScale(
                    1f);

                //
                // Actions
                //

                var playSize =
                    UiVec(68f, 26f);

                var addSize =
                    UiVec(62f, 26f);

                var buttonGap = Ui(8f);

                var actionsWidth =
                    playSize.X +
                    addSize.X +
                    buttonGap;

                var actionsX =
                    MathF.Max(
                        contentX,
                        contentRight -
                        actionsWidth);

                var actionsY =
                    origin.Y +
                    size.Y -
                    playSize.Y -
                    8f;

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        actionsX,
                        actionsY));

                using (ImRaii.PushStyle(
                           ImGuiStyleVar.FrameRounding,
                           6f))
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
                    var buttonPosition =
                        ImGui.GetCursorScreenPos();

                    if (ImGui.Button(
                            "##play",
                            playSize))
                    {
                        HandlePlayNow(
                            new VideoQueueEntry(
                                result.Url,
                                result.Title,
                                result.ChannelName,
                                result.Duration,
                                result.ThumbnailUrl));
                    }

                    DrawPlayerActionButtonContent(
                        buttonPosition,
                        playSize,
                        FontAwesomeIcon.Play,
                        "Play",
                        Vector4.One);
                }

                ImGui.SameLine(
                    0f,
                    buttonGap);

                using (ImRaii.PushStyle(
                           ImGuiStyleVar.FrameRounding,
                           6f))
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
                    var buttonPosition =
                        ImGui.GetCursorScreenPos();

                    if (ImGui.Button(
                            "##add",
                            addSize))
                    {
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

                    drawList.AddRect(
                        buttonPosition,
                        buttonPosition +
                        addSize,
                        ImGui.GetColorU32(
                            new Vector4(
                                MutedText.X,
                                MutedText.Y,
                                MutedText.Z,
                                0.16f)),
                        6f,
                        ImDrawFlags.None,
                        1f);

                    DrawPlayerActionButtonContent(
                        buttonPosition,
                        addSize,
                        FontAwesomeIcon.Plus,
                        "Add",
                        Vector4.One);
                }
            }
        }

        ImGui.PopID();
    }

    // Manual YouTube/Twitch panels — Player source tabs call these directly.
    private void DrawYouTubeSearch()
    {
        SetUiFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Search YouTube");

        SetUiFontScale(1f);

        ImGui.Dummy(UiVec(0f, 10f));


        // Search field
        ImGui.SetNextItemWidth(-Ui(66f));

        bool submitted;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                UiVec(14f, 10f)))
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
            submitted = ImGui.InputTextWithHint(
                "##search",
                "Enter a YouTube URL or search term...",
                ref searchQuery,
                200,
                ImGuiInputTextFlags.EnterReturnsTrue);
        }

        ImGui.SameLine(0f, 10f);

        // Search icon button
        bool clicked;

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
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            clicked = ImGui.Button(
                FontAwesomeIcon.Search.ToIconString(),
                UiVec(48f, 0f));
        }

        if ((submitted || clicked) &&
            searchQuery.Length > 0 &&
            !isSearching)
        {
            isSearching = true;
            _ = RunSearchAsync(searchQuery);
        }

        if (isSearching)
        {
            ImGui.Dummy(UiVec(0f, 6f));

            ImGui.TextColored(
                MutedText,
                "Searching...");
        }

        if (searchResults is not { } results ||
            results.Count == 0)
        {
            return;
        }

        var displayedResults =
            FilterAndOrderSearchResults(
                results,
                youtubeSearchOrder,
                youtubeDurationFilter);

        ImGui.Dummy(
            UiVec(0f, 16f));

        ImGui.TextColored(
      Accent,
      $"Results ({displayedResults.Count})");

        DrawVideoSearchFilters(
            "youtube",
            ref youtubeSearchOrder,
            ref youtubeDurationFilter);

        ImGui.Dummy(
            UiVec(0f, 8f));

        ImGui.Dummy(UiVec(0f, 8f));

        if (displayedResults.Count == 0)
        {
            ImGui.Dummy(
                UiVec(0f, 20f));

            ImGui.TextColored(
                MutedText,
                "No videos match the selected filters.");

            return;
        }

        DrawVideoSearchResultsGrid(
            "youtube",
            displayedResults);
    }

    private void DrawDailymotionSearch()
    {
        SetUiFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Search Dailymotion");

        SetUiFontScale(1f);

        ImGui.Dummy(UiVec(0f, 10f));

        ImGui.SetNextItemWidth(
     -66f);

        bool submitted;

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
            submitted =
                ImGui.InputTextWithHint(
                    "##dailymotionSearch",
                    "Enter a Dailymotion URL or search term...",
                    ref dailymotionSearchQuery,
                    200,
                    ImGuiInputTextFlags.EnterReturnsTrue);
        }

        ImGui.SameLine(
            0f,
            10f);

        bool clicked;

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
            clicked =
                ImGui.Button(
                    FontAwesomeIcon.Search.ToIconString(),
                    UiVec(48f, 0f));
        }

        if ((submitted || clicked) &&
            !string.IsNullOrWhiteSpace(dailymotionSearchQuery) &&
            !isSearchingDailymotion)
        {
            isSearchingDailymotion = true;
            dailymotionSearchError = null;

            _ = RunDailymotionSearchAsync(
                dailymotionSearchQuery.Trim());
        }


        if (isSearchingDailymotion)
        {
            ImGui.TextColored(
                MutedText,
                "Searching...");
        }


        if (dailymotionSearchError is { } error)
        {
            ImGui.TextColored(
                Danger,
                error);
        }


        if (dailymotionSearchResults is not { } results)
        {
            return;
        }

        var displayedResults =
            FilterAndOrderSearchResults(
                results,
                dailymotionSearchOrder,
                dailymotionDurationFilter);

        ImGui.Dummy(
            UiVec(0f, 16f));

        ImGui.TextColored(
     Accent,
     $"Results ({displayedResults.Count})");

        DrawVideoSearchFilters(
            "dailymotion",
            ref dailymotionSearchOrder,
            ref dailymotionDurationFilter);

        ImGui.Dummy(
            UiVec(0f, 8f));

        ImGui.Dummy(UiVec(0f, 8f));
        if (displayedResults.Count == 0)
        {
            ImGui.Dummy(
                UiVec(0f, 20f));

            ImGui.TextColored(
                MutedText,
                "No videos match the selected filters.");

            return;
        }

        DrawVideoSearchResultsGrid(
            "dailymotion",
            displayedResults);
    }

    private List<TrendingTopic> GetEnabledTrendingTopics()
    {
        var topics = new List<TrendingTopic>();

        // Entertainment
        if (Plugin.Cfg.TrendingGaming)
        {
            topics.Add(new(
                "Gaming",
                [
                    "trending gaming videos",
                "gaming news",
                "new game releases"
                ]));
        }

        if (Plugin.Cfg.TrendingMMORPG)
        {
            topics.Add(new(
                "MMORPG",
                [
                    "MMORPG news",
                "new MMORPG releases",
                "MMORPG gameplay"
                ]));
        }

        if (Plugin.Cfg.TrendingFinalFantasy)
        {
            topics.Add(new(
                "Final Fantasy",
                [
                    "Final Fantasy XIV",
                "FFXIV news",
                "FF14 gameplay"
                ]));
        }

        if (Plugin.Cfg.TrendingAnime)
        {
            topics.Add(new(
                "Anime",
                [
                    "anime trailers",
                "anime trending",
                "anime news"
                ]));
        }

        if (Plugin.Cfg.TrendingMovies)
        {
            topics.Add(new(
                "Movies",
                [
                    "movie trailers",
                "movie news",
                "best movies"
                ]));
        }

        if (Plugin.Cfg.TrendingTvShows)
        {
            topics.Add(new(
                "TV Shows",
                [
                    "new TV shows",
                "TV show trailers",
                "TV show news"
                ]));
        }

        if (Plugin.Cfg.TrendingMusic)
        {
            topics.Add(new(
                "Music",
                [
                    "new music releases",
                "music trending",
                "latest songs"
                ]));
        }

        if (Plugin.Cfg.TrendingMemes)
        {
            topics.Add(new(
                "Memes",
                [
                    "funny memes",
                "viral memes",
                "meme compilation"
                ]));
        }

        // World & Knowledge
        if (Plugin.Cfg.TrendingWildlife)
        {
            topics.Add(new(
                "Wildlife",
                [
                    "amazing wildlife documentary",
                "wildlife discoveries",
                "animal documentary"
                ]));
        }

        if (Plugin.Cfg.TrendingArchitecture)
        {
            topics.Add(new(
                "Architecture",
                [
                    "amazing architecture",
                "modern architecture design",
                "unique buildings"
                ]));
        }

        if (Plugin.Cfg.TrendingScience)
        {
            topics.Add(new(
                "Science",
                [
                    "science discoveries",
                "latest science news",
                "amazing science"
                ]));
        }

        if (Plugin.Cfg.TrendingSpace)
        {
            topics.Add(new(
                "Space",
                [
                    "space discoveries",
                "NASA news",
                "universe documentary"
                ]));
        }

        if (Plugin.Cfg.TrendingHistory)
        {
            topics.Add(new(
                "History",
                [
                    "history documentary",
                "historical discoveries",
                "ancient history"
                ]));
        }

        if (Plugin.Cfg.TrendingTechnology)
        {
            topics.Add(new(
                "Technology",
                [
                    "latest technology news",
                "new technology",
                "future technology"
                ]));
        }

        // Lifestyle
        if (Plugin.Cfg.TrendingPets)
        {
            topics.Add(new(
                "Pets",
                [
                    "cute pets",
                "funny animals",
                "adorable pets"
                ]));
        }

        if (Plugin.Cfg.TrendingFood)
        {
            topics.Add(new(
                "Food",
                [
                    "amazing food",
                "cooking videos",
                "food discoveries"
                ]));
        }

        if (Plugin.Cfg.TrendingTravel)
        {
            topics.Add(new(
                "Travel",
                [
                    "beautiful places travel",
                "travel discoveries",
                "amazing destinations"
                ]));
        }

        if (Plugin.Cfg.TrendingCars)
        {
            topics.Add(new(
                "Cars",
                [
                    "car news",
                "supercars",
                "car reviews"
                ]));
        }

        if (Plugin.Cfg.TrendingSports)
        {
            topics.Add(new(
                "Sports",
                [
                    "sports highlights",
                "sports news",
                "best sports moments"
                ]));
        }
        if (Plugin.Cfg.TrendingCartoons)
        {
            topics.Add(new(
                "Cartoons",
                [
                    "new cartoons",
            "animated shows",
            "cartoon clips"
                ]));
        }

        if (Plugin.Cfg.TrendingHorror)
        {
            topics.Add(new(
                "Horror",
                [
                    "horror movies",
            "scary videos",
            "horror stories"
                ]));
        }

        if (Plugin.Cfg.TrendingSciFi)
        {
            topics.Add(new(
                "Sci-Fi",
                [
                    "science fiction movies",
            "sci-fi news",
            "science fiction videos"
                ]));
        }

        if (Plugin.Cfg.TrendingComedy)
        {
            topics.Add(new(
                "Comedy",
                [
                    "comedy videos",
            "funny sketches",
            "stand up comedy"
                ]));
        }

        if (Plugin.Cfg.TrendingMinecraft)
        {
            topics.Add(new(
                "Minecraft",
                [
                    "Minecraft videos",
            "Minecraft builds",
            "Minecraft gameplay"
                ]));
        }

        if (Plugin.Cfg.TrendingArtsAndCrafts)
        {
            topics.Add(new(
                "Arts & Crafts",
                [
                    "arts and crafts",
            "creative craft ideas",
            "art projects"
                ]));
        }

        if (Plugin.Cfg.TrendingCosplaying)
        {
            topics.Add(new(
                "Cosplaying",
                [
                    "cosplay tutorials",
            "cosplay showcases",
            "cosplay conventions"
                ]));
        }

        if (Plugin.Cfg.TrendingDiy)
        {
            topics.Add(new(
                "DIY",
                [
                    "DIY projects",
            "DIY tutorials",
            "creative DIY ideas"
                ]));
        }

        if (Plugin.Cfg.TrendingUrbanExploration)
        {
            topics.Add(new(
                "Urban Exploration",
                [
                    "urban exploration",
            "abandoned places exploration",
            "exploring abandoned buildings"
                ]));
        }

        if (Plugin.Cfg.TrendingFashion)
        {
            topics.Add(new(
                "Fashion",
                [
                    "fashion trends",
            "fashion inspiration",
            "style ideas"
                ]));
        }

        if (Plugin.Cfg.TrendingDisney)
        {
            topics.Add(new(
                "Disney",
                [
                    "Disney news",
            "Disney movies",
            "Disney parks"
                ]));
        }

        if (Plugin.Cfg.TrendingFantasy)
        {
            topics.Add(new(
                "Fantasy",
                [
                    "fantasy movies",
            "fantasy worlds",
            "fantasy stories"
                ]));
        }

        return topics;
    }

    private int GetSubscribedTopicCount() =>
    GetEnabledTrendingTopics().Count;

    private static double GetTrendingScore(VideoSearchEntry video)
    {
        var views = video.ViewCount ?? 0;

        var viewScore = Math.Log10(
            Math.Max(views, 1));

        var ageBonus = 0d;

        if (video.UploadDate is { } uploadDate)
        {
            var ageDays =
                Math.Max(
                    0,
                    (DateTime.UtcNow - uploadDate).TotalDays);

            // Strong boost for recent uploads.
            // Falls off over roughly a month.
            ageBonus =
                Math.Max(
                    0,
                    30 - ageDays) / 30.0 * 3.0;
        }

        return viewScore + ageBonus;
    }

    private async Task LoadFeaturedSlidesAsync()
    {
        try
        {
            var tasks =
                FeaturedSlides
                    .Select(
                        slide =>
                            searchResolver.GetVideoEntryAsync(
                                slide.Url,
                                CancellationToken.None))
                    .ToArray();

            var results =
                await Task.WhenAll(tasks)
                    .ConfigureAwait(false);

            var loaded =
                new VideoSearchEntry?[
                    FeaturedSlides.Length];

            for (var i = 0;
                 i < results.Length;
                 i++)
            {
                loaded[i] =
                    results[i];
            }

            featuredSlideResults =
                loaded;
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Home] Failed to load featured videos: " +
                $"{exception.Message}");
        }
    }

    private void SelectNewHomeYouTubeTopics(
        IEnumerable<string>? availableTopics = null)
    {
        var availableTopicSet =
            availableTopics?.ToHashSet(
                StringComparer.OrdinalIgnoreCase);

        var enabledTopics =
            GetEnabledTrendingTopics()
                .Where(
                    topic =>
                        availableTopicSet is null ||
                        availableTopicSet.Contains(
                            topic.Name))
                .Select(
                    topic =>
                        topic.Name)
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    _ =>
                        trendingRandom.Next())
                .Take(
                    5)
                .ToList();

        homeYouTubeSelectedTopics.Clear();

        foreach (var topic in enabledTopics)
        {
            homeYouTubeSelectedTopics.Add(
                topic);
        }
    }

    private void RebuildHomeYouTubeFromBrowseCache(
        bool chooseNewTopics,
        bool selectOnlyCachedTopics = false)
    {
        RestorePersistedBrowseVideoCache();

        var cache =
            browseVideoResults;

        if (chooseNewTopics ||
            homeYouTubeSelectedTopics.Count == 0)
        {
            SelectNewHomeYouTubeTopics(
                selectOnlyCachedTopics
                    ? cache?.Keys
                    : null);
        }

        if (cache is not { Count: > 0 } ||
            homeYouTubeSelectedTopics.Count == 0)
        {
            homeYouTubeResults =
                [];

            isLoadingHomeYouTube =
                isLoadingBrowseVideos;

            return;
        }

        var candidates =
            homeYouTubeSelectedTopics
                .Where(
                    topic =>
                        cache.ContainsKey(
                            topic))
                .SelectMany(
                    topic =>
                        cache[topic])
                .Where(
                    video =>
                        !string.IsNullOrWhiteSpace(
                            video.Url))
                .GroupBy(
                    video =>
                        GetYouTubeVideoId(
                            video.Url) ??
                        video.Url,
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    group =>
                        group.First())
                .OrderByDescending(
                    GetTrendingScore)
                .Take(
                    5)
                .ToList();

        homeYouTubeResults =
            candidates;

        isLoadingHomeYouTube =
            false;
    }

    private async Task PrepareTopicVideosForLaunchAsync(
        bool forceRefresh)
    {
        try
        {
            RestorePersistedBrowseVideoCache();

            //
            // Immediately populate Home from the previous cache if possible.
            // A stale cache remains useful while its replacement downloads.
            //
            RebuildHomeYouTubeFromBrowseCache(
                chooseNewTopics: true);

            if (homeYouTubeResults is not
                { Count: > 0 })
            {
                isLoadingHomeYouTube =
                    true;
            }

            await LoadBrowseVideosAsync(
                    forceRefresh)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Topic Videos] Startup preparation failed: " +
                $"{exception.Message}");

            isLoadingHomeYouTube =
                false;
        }
    }

    private void EnsureTopicVideoStartup()
    {
        if (topicVideoStartupRequested)
        {
            return;
        }

        topicVideoStartupRequested =
            true;

        _ =
            PrepareTopicVideosForLaunchAsync(
                forceRefresh: false);
    }

    private void RefreshHomeYouTubeFromCache()
    {
        //
        // This is deliberately a cache-only operation. The Home refresh
        // button must never perform a YouTube request or alter cache age.
        //
        RebuildHomeYouTubeFromBrowseCache(
            chooseNewTopics: true);
    }

    private void NotifyTopicSettingsChanged()
    {
        //
        // Give the Home shelf a new selection immediately from whatever
        // matching cached results are currently available.
        //
        RebuildHomeYouTubeFromBrowseCache(
            chooseNewTopics: true);

        var refreshGeneration =
            Interlocked.Increment(
                ref topicSettingsRefreshGeneration);

        _ =
            RefreshTopicVideosAfterSettingsChangeAsync(
                refreshGeneration);
    }

    private async Task RefreshTopicVideosAfterSettingsChangeAsync(
        int refreshGeneration)
    {
        try
        {
            //
            // A short debounce prevents several quickly changed checkboxes
            // from starting and cancelling several complete YouTube pulls.
            //
            await Task.Delay(
                    TimeSpan.FromMilliseconds(
                        500))
                .ConfigureAwait(false);

            if (refreshGeneration !=
                topicSettingsRefreshGeneration)
            {
                return;
            }

            browseVideoRequested =
                true;

            await LoadBrowseVideosAsync(
                    forceRefresh: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Topic Videos] Settings refresh failed: " +
                $"{exception.Message}");
        }
    }

    private static string GetBrowseTopicSignature(
    IEnumerable<TrendingTopic> topics)
    {
        return string.Join(
            "\u001F",
            topics
                .Select(
                    topic =>
                        topic.Name)
                .OrderBy(
                    name =>
                        name,
                    StringComparer.OrdinalIgnoreCase));
    }

    private void RestorePersistedBrowseVideoCache()
    {
        if (browseVideoResults is not null)
        {
            return;
        }

        var persisted =
            Plugin.Cfg.BrowseVideoTopicCache;

        if (persisted.Count == 0 ||
            Plugin.Cfg.BrowseVideoCacheUpdatedUtc ==
            default ||
            string.IsNullOrWhiteSpace(
                Plugin.Cfg.BrowseVideoCacheTopicSignature))
        {
            return;
        }

        try
        {
            var restored =
                new Dictionary<string, List<VideoSearchEntry>>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var topic in persisted)
            {
                var videos =
                    topic.Value
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
                    restored[topic.Key] =
                        videos;
                }
            }

            if (restored.Count == 0)
            {
                return;
            }

            browseVideoResults =
                restored;

            browseVideoCacheTime =
                Plugin.Cfg.BrowseVideoCacheUpdatedUtc;

            browseVideoTopicSignature =
                Plugin.Cfg.BrowseVideoCacheTopicSignature;
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Browse Videos] Failed to restore cached videos: " +
                $"{exception.Message}");

            Plugin.Cfg.BrowseVideoTopicCache.Clear();

            Plugin.Cfg.BrowseVideoCacheUpdatedUtc =
                default;

            Plugin.Cfg.BrowseVideoCacheTopicSignature =
                null;

            Plugin.Cfg.Save();
        }
    }

    private void PersistBrowseVideoCache()
    {
        if (browseVideoResults is not { Count: > 0 } ||
            string.IsNullOrWhiteSpace(
                browseVideoTopicSignature) ||
            browseVideoCacheTime ==
            default)
        {
            return;
        }

        Plugin.Cfg.BrowseVideoTopicCache =
            browseVideoResults.ToDictionary(
                topic =>
                    topic.Key,
                topic =>
                    topic.Value
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

        Plugin.Cfg.BrowseVideoCacheUpdatedUtc =
            browseVideoCacheTime;

        Plugin.Cfg.BrowseVideoCacheTopicSignature =
            browseVideoTopicSignature;

        Plugin.Cfg.Save();
    }

    private void ClearPersistedBrowseVideoCache()
    {
        Plugin.Cfg.BrowseVideoTopicCache.Clear();

        Plugin.Cfg.BrowseVideoCacheUpdatedUtc =
            default;

        Plugin.Cfg.BrowseVideoCacheTopicSignature =
            null;

        Plugin.Cfg.Save();
    }

    private async Task LoadBrowseVideosAsync(
        bool forceRefresh = false)
    {
        RestorePersistedBrowseVideoCache();

        var topics =
            GetEnabledTrendingTopics();

        var topicSignature =
            GetBrowseTopicSignature(
                topics);

        var cacheIsUsable =
            !forceRefresh &&
            browseVideoResults is { Count: > 0 } &&
            string.Equals(
                browseVideoTopicSignature,
                topicSignature,
                StringComparison.Ordinal) &&
            DateTime.UtcNow -
            browseVideoCacheTime <
            BrowseVideoCacheDuration;

        if (cacheIsUsable)
        {
            browseVideoExpectedTopicCount =
                topics.Count;

            isLoadingBrowseVideos =
                false;

            RebuildHomeYouTubeFromBrowseCache(
                chooseNewTopics:
                    homeYouTubeSelectedTopics.Count == 0);

            return;
        }

        browseVideosCts?.Cancel();
        browseVideosCts?.Dispose();

        var cancellation =
            new CancellationTokenSource();

        browseVideosCts =
            cancellation;

        var loadGeneration =
            Interlocked.Increment(
                ref browseVideoLoadGeneration);

        isLoadingBrowseVideos =
            true;

        browseVideoExpectedTopicCount =
            topics.Count;

        var previousTopicSignature =
            browseVideoTopicSignature;

        browseVideoTopicSignature =
            topicSignature;

        try
        {
            if (topics.Count == 0)
            {
                browseVideoResults =
                    [];

                browseVideoCacheTime =
                    DateTime.UtcNow;

                ClearPersistedBrowseVideoCache();

                return;
            }

            var previousResultsMatchTopics =
                browseVideoResults is { Count: > 0 } &&
                string.Equals(
                    previousTopicSignature,
                    topicSignature,
                    StringComparison.Ordinal);

            var workingResults =
                previousResultsMatchTopics
                    ? new Dictionary<string, List<VideoSearchEntry>>(
                        browseVideoResults!,
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, List<VideoSearchEntry>>(
                        StringComparer.OrdinalIgnoreCase);

            browseVideoResults =
                new Dictionary<string, List<VideoSearchEntry>>(
                    workingResults,
                    StringComparer.OrdinalIgnoreCase);

            var selectedTopics =
                topics.ToList();

            const int batchSize =
                3;

            var initialHomeTopicTarget =
                Math.Min(
                    batchSize,
                    selectedTopics.Count);

            var initialHomePublished =
                previousResultsMatchTopics ||
                homeYouTubeResults is { Count: > 0 };

            const int resultsPerTopic =
                15;

            for (var batchStart = 0;
                 batchStart < selectedTopics.Count;
                 batchStart += batchSize)
            {
                cancellation.Token
                    .ThrowIfCancellationRequested();

                var batch =
                    selectedTopics
                        .Skip(
                            batchStart)
                        .Take(
                            batchSize)
                        .ToList();

                var pendingSearches =
                    batch
                        .Select(
                            topic =>
                                (
                                    Topic: topic,
                                    Query:
                                        topic.SearchQueries[
                                            trendingRandom.Next(
                                                topic.SearchQueries.Length)]
                                ))
                        .Select(
                            async request =>
                            {
                                //
                                // SearchAsync always performs a fresh discovery
                                // request. Metadata enrichment remains cache-aware.
                                //
                                var searchResults =
                                    await searchResolver
                                        .SearchAsync(
                                            request.Query,
                                            resultsPerTopic,
                                            cancellation.Token)
                                        .ConfigureAwait(false);

                                var candidates =
                                    searchResults
                                        .GroupBy(
                                            result =>
                                                result.Url,
                                            StringComparer.OrdinalIgnoreCase)
                                        .Select(
                                            group =>
                                                group.First())
                                        .Take(
                                            resultsPerTopic)
                                        .ToList();

                                var enriched =
                                    await Task.WhenAll(
                                            candidates.Select(
                                                video =>
                                                    searchResolver
                                                        .EnrichSearchResultAsync(
                                                            video,
                                                            cancellation.Token)))
                                        .ConfigureAwait(false);

                                return
                                    (
                                        request.Topic.Name,
                                        Results:
                                            enriched
                                                .OrderByDescending(
                                                    GetTrendingScore)
                                                .Take(
                                                    resultsPerTopic)
                                                .ToList()
                                    );
                            })
                        .ToArray();

                var loadedBatch =
                    await Task.WhenAll(
                            pendingSearches)
                        .ConfigureAwait(false);

                cancellation.Token
                    .ThrowIfCancellationRequested();

                if (loadGeneration !=
                    browseVideoLoadGeneration)
                {
                    return;
                }

                var updatedResults =
                    new Dictionary<string, List<VideoSearchEntry>>(
                        workingResults,
                        StringComparer.OrdinalIgnoreCase);

                foreach (var loadedTopic in
                         loadedBatch)
                {
                    updatedResults.Remove(
                        loadedTopic.Name);

                    if (loadedTopic.Results.Count >
                        0)
                    {
                        updatedResults[
                            loadedTopic.Name] =
                            loadedTopic.Results;
                    }
                }

                workingResults =
                    updatedResults;

                browseVideoResults =
                    new Dictionary<string, List<VideoSearchEntry>>(
                        workingResults,
                        StringComparer.OrdinalIgnoreCase);

                // A new installation has no persisted Topics cache. Publish
                // Home as soon as three topic feeds have usable results rather
                // than waiting for every subscribed topic to finish. Restrict
                // the random Home selection to completed topics for this first
                // publication; the final rebuild below uses the full cache.
                if (!initialHomePublished &&
                    workingResults.Count >=
                    initialHomeTopicTarget)
                {
                    RebuildHomeYouTubeFromBrowseCache(
                        chooseNewTopics: true,
                        selectOnlyCachedTopics: true);

                    initialHomePublished =
                        homeYouTubeResults is { Count: > 0 };
                }
            }

            if (loadGeneration ==
        browseVideoLoadGeneration)
            {
                browseVideoCacheTime =
                    DateTime.UtcNow;

                PersistBrowseVideoCache();

                //
                // Every successful shared-cache rebuild gives Home a new selection.
                //
                RebuildHomeYouTubeFromBrowseCache(
                    chooseNewTopics: true);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal when a newer refresh or topic change replaces this load.
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Browse Videos] Failed to load videos: " +
                $"{exception.Message}");

            if (loadGeneration ==
                    browseVideoLoadGeneration &&
                browseVideoResults is null)
            {
                browseVideoResults =
                    [];
            }
        }
        finally
        {
            if (loadGeneration ==
                browseVideoLoadGeneration)
            {
                isLoadingBrowseVideos =
                    false;

                if (homeYouTubeResults is not
                    { Count: > 0 })
                {
                    RebuildHomeYouTubeFromBrowseCache(
                        chooseNewTopics:
                            homeYouTubeSelectedTopics.Count == 0);
                }

                isLoadingHomeYouTube =
                    false;
            }
        }
    }

    private async Task LoadFfxivYouTubeAsync()
    {
        try
        {
            // Restore before the first await so saved cards appear immediately,
            // including while an expired cache is being refreshed.
            try
            {
                ffxivYouTubeResults = Plugin.Cfg.FfxivVideoCache?
                    .Where(video => video is not null && !string.IsNullOrWhiteSpace(video.Url))
                    .Take(10)
                    .Select(video => new VideoSearchEntry(
                        video.Title,
                        video.Url,
                        video.ChannelName,
                        video.DurationSeconds is { } seconds
                            ? TimeSpan.FromSeconds(seconds)
                            : null,
                        video.ThumbnailUrl,
                        video.ViewCount,
                        video.UploadDate,
                        video.ChannelId))
                    .ToList();
            }
            catch (Exception exception)
            {
                AepLog.Warning($"[Home] Failed to restore FFXIV video cache: {exception.Message}");
            }

            var cacheAge = DateTime.UtcNow - Plugin.Cfg.FfxivVideoCacheUpdatedUtc;
            if (ffxivYouTubeResults is { Count: > 0 } &&
                Plugin.Cfg.FfxivVideoCacheUpdatedUtc != default &&
                cacheAge >= TimeSpan.Zero &&
                cacheAge < BrowseVideoCacheDuration)
            {
                return;
            }

            var refreshed = await searchResolver
                .SearchLatestAggregatedAsync(
                    [
                        "ffxiv",
                    "ff14",
                    "final fantasy xiv"
                    ],
                    10,
                    CancellationToken.None)
                .ConfigureAwait(false);

            // Search failures can return an empty list rather than throw.
            // Keep the previous cards and timestamp so a later load can retry.
            if (refreshed.Count == 0)
            {
                return;
            }

            ffxivYouTubeResults = refreshed;
            Plugin.Cfg.FfxivVideoCache = refreshed
                .Select(video => new CachedBrowseVideoRecord
                {
                    Title = video.Title,
                    Url = video.Url,
                    ChannelName = video.ChannelName,
                    DurationSeconds = video.Duration?.TotalSeconds,
                    ThumbnailUrl = video.ThumbnailUrl,
                    ViewCount = video.ViewCount,
                    UploadDate = video.UploadDate,
                    ChannelId = video.ChannelId
                })
                .ToList();
            Plugin.Cfg.FfxivVideoCacheUpdatedUtc = DateTime.UtcNow;
            Plugin.Cfg.Save();
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Home] Failed to load FFXIV YouTube shelf: {exception.Message}");


        }
        finally
        {
            isLoadingFfxivYouTube = false;
        }
    }

    private async Task RunSearchAsync(
        string query)
    {
        searchResults =
            await searchResolver.SearchAsync(
                    query,
                    50,
                    CancellationToken.None)
                .ConfigureAwait(false);

        isSearching =
            false;
    }

    private async Task RunDailymotionSearchAsync(string query)
    {
        try
        {
            using var http =
                Net.PluginHttpClients.CreateMetadataClient();

            var encoded =
                Uri.EscapeDataString(query);

            var url =
     "https://api.dailymotion.com/videos" +
     $"?search={encoded}" +
     $"&limit=50" +
     "&fields=id,title,thumbnail_url,duration,views_total";

            var json =
                await http.GetStringAsync(url);

            using var document =
                JsonDocument.Parse(json);

            var results =
                new List<VideoSearchEntry>();

            if (document.RootElement.TryGetProperty(
                    "list",
                    out var list))
            {
                foreach (var video in list.EnumerateArray())
                {
                    var id =
                        video.GetProperty("id")
                            .GetString();

                    var title =
                        video.GetProperty("title")
                            .GetString();

                    if (string.IsNullOrWhiteSpace(id) ||
                        string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    var watchUrl = $"https://www.dailymotion.com/video/{id}";

                    var thumbnail =
                        video.TryGetProperty("thumbnail_url", out var thumbnailValue)
                            ? thumbnailValue.GetString()
                            : null;

                    AepLog.Warning(
    $"[Dailymotion] Thumb: {thumbnail}");

                    TimeSpan? duration =
                        video.TryGetProperty("duration", out var durationValue) &&
                        durationValue.TryGetDouble(out var seconds)
                            ? TimeSpan.FromSeconds(seconds)
                            : null;

                    long? viewCount =
                        video.TryGetProperty("views_total", out var viewsValue) &&
                        viewsValue.TryGetInt64(out var views)
                            ? views
                            : null;

                    AepLog.Warning(
                        $"[Dailymotion] Queue URL: {watchUrl}");

                    results.Add(
                        new VideoSearchEntry(
                            title,
                            watchUrl,
                            "Dailymotion",
                            duration,
                            thumbnail,
                            viewCount));
                }
            }

            dailymotionSearchResults = results;
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Dailymotion] Search failed: {exception.Message}");

            dailymotionSearchError =
                "Couldn't search Dailymotion.";
        }
        finally
        {
            isSearchingDailymotion = false;
        }
    }

    // Real trending data via Twitch's own Helix API (server-side, see Server/Twitch), not scraping.
    private void DrawTwitchTrending()
    {
        ImGui.TextColored(
            Accent,
            "Trending on Twitch");

        ImGui.Dummy(UiVec(0f, 8f));

        if (CurrentSession is not { } session)
        {
            ImGui.TextColored(
                MutedText,
                "Sign in to see trending streams.");

            return;
        }

        if (trendingDirty)
        {
            trendingDirty = false;

            var token = session.Token;

            _ = Task.Run(
                async () =>
                    trendingStreams =
                        await twitchClient.GetTrendingAsync(token));
        }

        // Refresh button
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
            if (ImGui.Button(
                "Refresh",
                UiVec(92f, 32f)))
            {
                trendingDirty = true;
            }
        }

        if (trendingStreams.Length == 0)
        {
            ImGui.Dummy(UiVec(0f, 6f));

            ImGui.TextColored(
                MutedText,
                "Nothing trending right now.");

            return;
        }

        ImGui.Dummy(UiVec(0f, 10f));

        // Only the trending list scrolls.
        var trendingHeight = MathF.Max(
            120f,
            ImGui.GetContentRegionAvail().Y - 8f);

        using var trendingChild = ImRaii.Child(
            "##twitchTrendingResults",
            new Vector2(-1f, trendingHeight),
            false,
            ImGuiWindowFlags.None);

        if (!trendingChild)
        {
            return;
        }

        foreach (var stream in trendingStreams)
        {
            ImGui.PushID(stream.ChannelName);

            var rowHeight = Ui(70f);

            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(0.045f, 0.06f, 0.10f, 1f)))
            using (var row = ImRaii.Child(
                $"##trending_{stream.ChannelName}",
                new Vector2(Ui(-6f), rowHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (row)
                {
                    var rowOrigin =
                        ImGui.GetCursorScreenPos();

                    var thumbWidth = Ui(105f);

                    var thumbnail =
                        thumbnails.Get(stream.ThumbnailUrl);

                    if (thumbnail is not null)
                    {
                        ImGui.GetWindowDrawList().AddImageRounded(
                            thumbnail.Handle,
                            rowOrigin,
                            rowOrigin + new Vector2(
                                thumbWidth,
                                rowHeight),
                            Vector2.Zero,
                            Vector2.One,
                            uint.MaxValue,
                            8f);
                    }

                    var contentX =
                        rowOrigin.X +
                        thumbWidth +
                        12f;

                    var controlsWidth = Ui(120f);

                    var textWidth =
                        ImGui.GetWindowWidth() -
                        thumbWidth -
                        controlsWidth -
                        28f;

                    // Stream title
                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            rowOrigin.Y + Ui(10f)));

                    ImGui.PushTextWrapPos(
                        contentX + textWidth);

                    ImGui.TextColored(
                        Vector4.One,
                        stream.Title);

                    ImGui.PopTextWrapPos();

                    // Stream metadata
                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            rowOrigin.Y + Ui(40f)));

                    ImGui.TextColored(
                        MutedText,
                        $"{stream.ChannelName}  •  " +
                        $"{stream.GameName}  •  " +
                        $"{stream.ViewerCount:N0} viewers");

                    // Play button
                    var playSize =
                        UiVec(92f, 34f);

                    var playPos =
                        new Vector2(
                            rowOrigin.X +
                            ImGui.GetWindowWidth() -
                            Ui(104f),
                            rowOrigin.Y +
                            (rowHeight - playSize.Y) * 0.5f);

                    ImGui.SetCursorScreenPos(
                        playPos);

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
                            $"##trendingPlay_{stream.ChannelName}",
                            playSize))
                        {
                            HandlePlayNow(
                                new VideoQueueEntry(
                                    stream.Url,
                                    stream.Title,
                                    stream.ChannelName,
                                    null,
                                    stream.ThumbnailUrl));
                        }

                        DrawPlayerActionButtonContent(
                            buttonPos,
                            playSize,
                            FontAwesomeIcon.Play,
                            "Play",
                            Vector4.One);
                    }
                }
            }

            ImGui.PopID();

            ImGui.Dummy(
                UiVec(0f, 8f));
        }
    }

    private IReadOnlyList<string> GetFavouriteTwitchChannels()
    {
        Plugin.Cfg.FavouriteTwitchChannels ??=
            [];

        return Plugin.Cfg.FavouriteTwitchChannels
            .Where(
                channel =>
                    !string.IsNullOrWhiteSpace(
                        channel))
            .Distinct(
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(
                channel =>
                    channel,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool IsFavouriteTwitchChannel(
        string? channelName)
    {
        if (!TwitchChannelChecker.TryNormalizeChannelName(
                channelName,
                out var normalizedChannel,
                out _))
        {
            return false;
        }

        return Plugin.Cfg.FavouriteTwitchChannels.Any(
            savedChannel =>
                string.Equals(
                    savedChannel,
                    normalizedChannel,
                    StringComparison.OrdinalIgnoreCase));
    }

    private bool TryAddFavouriteTwitchChannel(
        string? channelName,
        out string? error)
    {
        error =
            null;

        if (!TwitchChannelChecker.TryNormalizeChannelName(
                channelName,
                out var normalizedChannel,
                out var normalizationError))
        {
            error =
                normalizationError;

            return false;
        }

        Plugin.Cfg.FavouriteTwitchChannels ??=
            [];

        if (Plugin.Cfg.FavouriteTwitchChannels.Any(
                savedChannel =>
                    string.Equals(
                        savedChannel,
                        normalizedChannel,
                        StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (Plugin.Cfg.FavouriteTwitchChannels.Count >=
            MaximumFavouriteTwitchChannels)
        {
            error =
                $"You can save up to " +
                $"{MaximumFavouriteTwitchChannels} Twitch channels.";

            return false;
        }

        Plugin.Cfg.FavouriteTwitchChannels.Add(
            normalizedChannel);

        Plugin.Cfg.FavouriteTwitchChannels =
            Plugin.Cfg.FavouriteTwitchChannels
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    savedChannel =>
                        savedChannel,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        Plugin.Cfg.Save();

        return true;
    }

    private void RemoveFavouriteTwitchChannel(
        string? channelName)
    {
        if (!TwitchChannelChecker.TryNormalizeChannelName(
                channelName,
                out var normalizedChannel,
                out _))
        {
            return;
        }

        Plugin.Cfg.FavouriteTwitchChannels ??=
            [];

        Plugin.Cfg.FavouriteTwitchChannels.RemoveAll(
            savedChannel =>
                string.Equals(
                    savedChannel,
                    normalizedChannel,
                    StringComparison.OrdinalIgnoreCase));

        Plugin.Cfg.Save();

        twitchFavouriteStatuses.TryRemove(
            normalizedChannel,
            out _);
    }

    private void ToggleFavouriteTwitchChannel(
        string? channelName)
    {
        if (IsFavouriteTwitchChannel(
                channelName))
        {
            RemoveFavouriteTwitchChannel(
                channelName);

            return;
        }

        if (!TryAddFavouriteTwitchChannel(
                channelName,
                out var error))
        {
            twitchError =
                error;

            return;
        }

        RefreshTwitchFavouritesIfStale(
            forceRefresh: true);
    }

    private void RefreshTwitchFavouritesIfStale(
        bool forceRefresh = false)
    {
        if (isRefreshingTwitchFavourites)
        {
            return;
        }

        var favourites =
            GetFavouriteTwitchChannels();

        if (favourites.Count == 0)
        {
            return;
        }

        var now =
            DateTime.UtcNow;

        var hasStaleChannel =
            favourites.Any(
                channel =>
                    !twitchFavouriteStatuses.TryGetValue(
                        channel,
                        out var status) ||
                    now -
                    status.CheckedAtUtc >=
                    TwitchFavouriteCacheDuration);

        if (!forceRefresh &&
            !hasStaleChannel)
        {
            return;
        }

        _ = RefreshTwitchFavouritesAsync(
            forceRefresh);
    }

    private async Task RefreshTwitchFavouritesAsync(
        bool forceRefresh)
    {
        if (isRefreshingTwitchFavourites)
        {
            return;
        }

        isRefreshingTwitchFavourites =
            true;

        try
        {
            var ytdlpPath =
                screenController.Engine.Resources
                    .GetLocationYTDLP();

            if (string.IsNullOrWhiteSpace(
                    ytdlpPath))
            {
                twitchError =
                    "yt-dlp isn't downloaded yet - try again in a moment.";

                return;
            }

            var favourites =
                GetFavouriteTwitchChannels()
                    .ToArray();

            var refreshStartedAtUtc =
                DateTime.UtcNow;

            var checks =
                favourites.Select(
                    async channel =>
                    {
                        if (!forceRefresh &&
                            twitchFavouriteStatuses.TryGetValue(
                                channel,
                                out var existing) &&
                            refreshStartedAtUtc -
                            existing.CheckedAtUtc <
                            TwitchFavouriteCacheDuration)
                        {
                            return;
                        }

                        await twitchFavouriteCheckGate
                            .WaitAsync()
                            .ConfigureAwait(false);

                        try
                        {
                            var (stream, error, isOffline) =
        await twitchChecker.CheckLiveAsync(
                ytdlpPath,
                channel,
                CancellationToken.None)
            .ConfigureAwait(false);

                            twitchFavouriteStatuses[channel] =
                                new TwitchFavouriteStatus(
                                    channel,
                                    stream,
                                    isOffline,
                                    error,
                                    DateTime.UtcNow);
                        }
                        catch (Exception exception)
                        {
                            AepLog.Warning(
                                $"[Twitch] Favourite check failed for " +
                                $"{channel}: {exception.Message}");

                            twitchFavouriteStatuses[channel] =
     new TwitchFavouriteStatus(
         channel,
         null,
         false,
         "The channel status could not be checked.",
         DateTime.UtcNow);
                        }
                        finally
                        {
                            twitchFavouriteCheckGate.Release();
                        }
                    });

            await Task.WhenAll(
                    checks)
                .ConfigureAwait(false);

            twitchFavouritesLastRefreshUtc =
                DateTime.UtcNow;
        }
        finally
        {
            isRefreshingTwitchFavourites =
                false;
        }
    }

    // Checks whether one named Twitch channel is currently live.
    private void DrawTwitchCheck()
    {
        RefreshTwitchFavouritesIfStale();

        SetUiFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Find a Twitch channel");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 3f));

        SetUiFontScale(
            0.84f);

        ImGui.TextColored(
            MutedText,
            "Enter a channel name or paste a Twitch URL to see if they're live.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 12f));

        ImGui.SetNextItemWidth(
            -66f);

        bool submitted;

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
            submitted =
                ImGui.InputTextWithHint(
                    "##twitchChannel",
                    "Enter a Twitch channel name or URL",
                    ref twitchChannelInput,
                    64,
                    ImGuiInputTextFlags.EnterReturnsTrue);
        }

        ImGui.SameLine(
            0f,
            10f);

        bool clicked;

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
            clicked =
                ImGui.Button(
                    FontAwesomeIcon.Search.ToIconString(),
                    UiVec(48f, 0f));
        }

        if ((submitted || clicked) &&
            twitchChannelInput.Length > 0 &&
            !isCheckingTwitch)
        {
            isCheckingTwitch =
                true;

            twitchResult =
                null;

            twitchError =
                null;

            twitchResultIsOffline =
                false;

            twitchCheckedChannelName =
                null;

            _ =
                RunTwitchCheckAsync(
                    twitchChannelInput.Trim());
        }

        if (isCheckingTwitch)
        {
            ImGui.Dummy(
                UiVec(0f, 12f));

            ImGui.TextColored(
                MutedText,
                "Checking channel...");
        }

        if (twitchError is { } error)
        {
            ImGui.Dummy(
                UiVec(0f, 12f));

            ImGui.TextColored(
                Danger,
                error);
        }

        if (!isCheckingTwitch &&
            (twitchResult is not null ||
             (twitchResultIsOffline &&
              twitchCheckedChannelName is not null)))
        {
            ImGui.Dummy(
                UiVec(0f, 14f));

            DrawTwitchSearchedChannelCard();
        }

        if (ImGui.GetTime() <
            queueAddedFeedbackUntil)
        {
            ImGui.Dummy(
                UiVec(0f, 6f));

            SetUiFontScale(
                0.82f);

            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    Good,
                    FontAwesomeIcon.Check.ToIconString());
            }

            ImGui.SameLine(
                0f,
                6f);

            ImGui.TextColored(
                Good,
                "Video added to queue");

            SetUiFontScale(
                1f);
        }

        ImGui.Dummy(
            UiVec(0f, 22f));

        DrawTwitchFavourites();
    }

    private void DrawTwitchSearchedChannelCard()
    {
        var stream =
            twitchResult;

        var channelName =
            stream?.ChannelName ??
            twitchCheckedChannelName;

        if (string.IsNullOrWhiteSpace(
                channelName))
        {
            return;
        }

        var isLive =
            stream is not null;

        var favourite =
            IsFavouriteTwitchChannel(
                channelName);

        var cardHeight =
            Ui(92f);

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
                   "##twitchSearchedChannelCard",
                   new Vector2(
                       -1f,
                       cardHeight),
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

            var drawList =
                ImGui.GetWindowDrawList();

            drawList.AddRect(
                origin,
                origin +
                new Vector2(
                    cardWidth,
                    cardHeight),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.20f)),
                9f,
                ImDrawFlags.None,
                1f);

            var imageWidth =
                Ui(128f);

            var imageInset =
                Ui(7f);

            if (isLive &&
                stream is not null)
            {
                var thumbnail =
                    thumbnails.Get(
                        stream.ThumbnailUrl);

                if (thumbnail is not null)
                {
                    drawList.AddImageRounded(
                        thumbnail.Handle,
                        origin +
                        new Vector2(
                            imageInset,
                            imageInset),
                        origin +
                        new Vector2(
                            imageWidth,
                            cardHeight -
                            imageInset),
                        Vector2.Zero,
                        Vector2.One,
                        uint.MaxValue,
                        7f);
                }
                else
                {
                    DrawTwitchPlaceholder(
                        origin +
                        new Vector2(
                            imageInset,
                            imageInset),
                        new Vector2(
                            imageWidth -
                            imageInset,
                            cardHeight -
                            (imageInset * 2f)));
                }
            }
            else
            {
                DrawTwitchPlaceholder(
                    origin +
                    new Vector2(
                        imageInset,
                        imageInset),
                    new Vector2(
                        imageWidth -
                        imageInset,
                        cardHeight -
                        (imageInset * 2f)));
            }

            var contentX =
                origin.X +
                imageWidth +
                Ui(13f);

            var starSize =
                new Vector2(
                    Ui(34f),
                    Ui(34f));

            var addSize =
                new Vector2(
                    Ui(68f),
                    Ui(34f));

            var playSize =
                new Vector2(
                    Ui(84f),
                    Ui(34f));

            var rightPadding =
                Ui(10f);

            var controlsWidth =
                starSize.X;

            if (isLive)
            {
                controlsWidth +=
                    Ui(8f) +
                    addSize.X +
                    Ui(8f) +
                    playSize.X;
            }

            var textWidth =
                MathF.Max(
                    Ui(80f),
                    cardWidth -
                    (contentX - origin.X) -
                    controlsWidth -
                    rightPadding -
                    Ui(18f));

            ImGui.SetCursorScreenPos(
                new Vector2(
                    contentX,
                    origin.Y +
                    Ui(14f)));

            ImGui.TextColored(
                Vector4.One,
                FitTwitchText(
                    channelName,
                    textWidth));

            ImGui.SameLine(
                0f,
                Ui(8f));

            ImGui.TextColored(
                isLive
                    ? Good
                    : MutedText,
                isLive
                    ? "LIVE"
                    : "OFFLINE");

            ImGui.SetCursorScreenPos(
                new Vector2(
                    contentX,
                    origin.Y +
                    Ui(46f)));

            var description =
                isLive &&
                stream is not null
                    ? stream.Title
                    : "This channel is not live right now.";

            ImGui.TextColored(
                MutedText,
                FitTwitchText(
                    PrepareTwitchDisplayText(
                        description),
                    textWidth));

            var controlsX =
                origin.X +
                cardWidth -
                controlsWidth -
                rightPadding;

            var controlsY =
                origin.Y +
                ((cardHeight -
                  starSize.Y) *
                 0.5f);

            if (isLive &&
                stream is not null)
            {
                ImGui.SetCursorScreenPos(
                    new Vector2(
                        controlsX,
                        controlsY));

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
                    if (ImGui.Button(
                            "Play##searchedTwitchPlay",
                            playSize))
                    {
                        HandlePlayNow(
                            new VideoQueueEntry(
                                stream.Url,
                                stream.Title,
                                stream.ChannelName,
                                null,
                                stream.ThumbnailUrl));
                    }
                }

                controlsX +=
                    playSize.X +
                    Ui(8f);

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        controlsX,
                        controlsY));

                using (ImRaii.PushStyle(
                           ImGuiStyleVar.FrameRounding,
                           7f))
                {
                    if (ImGui.Button(
                            "Add##searchedTwitchAdd",
                            addSize))
                    {
                        HandleAddToQueue(
                            new VideoQueueEntry(
                                stream.Url,
                                stream.Title,
                                stream.ChannelName,
                                null,
                                stream.ThumbnailUrl));

                        if (!ShouldUseViewerMediaActions)
                        {
                            queueAddedFeedbackUntil =
                                ImGui.GetTime() +
                                2.0;
                        }
                    }
                }

                controlsX +=
                    addSize.X +
                    Ui(8f);
            }

            ImGui.SetCursorScreenPos(
                new Vector2(
                    controlsX,
                    controlsY));

            DrawTwitchFavouriteButton(
                "searchedTwitchFavourite",
                channelName,
                favourite,
                starSize);
        }
    }

    private void DrawTwitchFavourites()
    {
        var favourites =
            GetFavouriteTwitchChannels();

        SetUiFontScale(
            1.08f);

        ImGui.TextColored(
            Vector4.One,
            $"Favourite channels ({favourites.Count})");

        SetUiFontScale(
            1f);

        var refreshWidth =
            Ui(92f);

        ImGui.SameLine(
            ImGui.GetContentRegionMax().X -
            refreshWidth);

        using (ImRaii.Disabled(
                   isRefreshingTwitchFavourites))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   7f))
        {
            if (ImGui.Button(
                    isRefreshingTwitchFavourites
                        ? "Checking...##refreshTwitchFavourites"
                        : "Refresh##refreshTwitchFavourites",
                    new Vector2(
                        refreshWidth,
                        Ui(30f))))
            {
                RefreshTwitchFavouritesIfStale(
                    forceRefresh: true);
            }
        }

        ImGui.Dummy(
            UiVec(0f, 3f));

        SetUiFontScale(
            0.78f);

        ImGui.TextColored(
            MutedText,
            isRefreshingTwitchFavourites
                ? "Checking saved channel statuses..."
                : twitchFavouritesLastRefreshUtc is { } refreshedAt
                    ? $"Last checked {FormatTwitchFavouriteCheckAge(refreshedAt)}"
                    : "Statuses update automatically every 15 minutes.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 10f));

        if (favourites.Count == 0)
        {
            DrawEmptyTwitchFavourites();

            return;
        }

        var orderedFavourites =
            favourites
                .OrderByDescending(
                    channel =>
                        twitchFavouriteStatuses.TryGetValue(
                            channel,
                            out var status) &&
                        status.Stream is not null)
                .ThenBy(
                    channel =>
                        channel,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var gap =
            Ui(10f);

        var cardWidth =
            MathF.Max(
                Ui(260f),
                (availableWidth - gap) /
                2f);

        for (var index = 0;
             index < orderedFavourites.Count;
             index++)
        {
            var channel =
                orderedFavourites[index];

            twitchFavouriteStatuses.TryGetValue(
                channel,
                out var status);

            if (index % 2 == 1)
            {
                ImGui.SameLine(
                    0f,
                    gap);
            }

            if (DrawTwitchFavouriteCard(
                    channel,
                    status,
                    cardWidth))
            {
                break;
            }
        }
    }

    private bool DrawTwitchFavouriteCard(
        string channel,
        TwitchFavouriteStatus? status,
        float width)
    {
        var stream =
            status?.Stream;

        var isLive =
            stream is not null;

        var isOffline =
            status?.IsOffline ==
            true;

        var hasError =
            !string.IsNullOrWhiteSpace(
                status?.Error) &&
            !isOffline;

        var cardHeight =
            Ui(104f);

        var removed =
            false;

        ImGui.PushID(
            channel);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   9f))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   isLive
                       ? new Vector4(
                           0.05f,
                           0.065f,
                           0.105f,
                           1f)
                       : new Vector4(
                           0.038f,
                           0.048f,
                           0.075f,
                           1f)))
        using (var card = ImRaii.Child(
                   "##favouriteTwitchCard",
                   new Vector2(
                       width,
                       cardHeight),
                   false,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (card)
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
                        cardHeight),
                    ImGui.GetColorU32(
                        new Vector4(
                            isLive
                                ? Good.X
                                : MutedText.X,
                            isLive
                                ? Good.Y
                                : MutedText.Y,
                            isLive
                                ? Good.Z
                                : MutedText.Z,
                            isLive
                                ? 0.22f
                                : 0.12f)),
                    9f,
                    ImDrawFlags.None,
                    1f);

                var imageInset =
                    Ui(7f);

                var imageWidth =
                    Ui(116f);

                var imageHeight =
                    cardHeight -
                    (imageInset * 2f);

                if (isLive &&
                    stream is not null)
                {
                    var thumbnail =
                        thumbnails.Get(
                            stream.ThumbnailUrl);

                    if (thumbnail is not null)
                    {
                        drawList.AddImageRounded(
                            thumbnail.Handle,
                            origin +
                            new Vector2(
                                imageInset,
                                imageInset),
                            origin +
                            new Vector2(
                                imageWidth,
                                cardHeight -
                                imageInset),
                            Vector2.Zero,
                            Vector2.One,
                            uint.MaxValue,
                            7f);
                    }
                    else
                    {
                        DrawTwitchPlaceholder(
                            origin +
                            new Vector2(
                                imageInset,
                                imageInset),
                            new Vector2(
                                imageWidth -
                                imageInset,
                                imageHeight));
                    }
                }
                else
                {
                    DrawTwitchPlaceholder(
                        origin +
                        new Vector2(
                            imageInset,
                            imageInset),
                        new Vector2(
                            imageWidth -
                            imageInset,
                            imageHeight));
                }

                var contentX =
                    origin.X +
                    imageWidth +
                    Ui(11f);

                var starSize =
                    new Vector2(
                        Ui(30f),
                        Ui(30f));

                var rightPadding =
                    Ui(8f);

                var actionReserve =
                    isLive
                        ? Ui(76f)
                        : 0f;

                var textWidth =
                    MathF.Max(
                        Ui(70f),
                        cardWidth -
                        (contentX - origin.X) -
                        starSize.X -
                        rightPadding -
                        Ui(14f) -
                        actionReserve);

                var displayedChannel =
                    stream?.ChannelName ??
                    channel;

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        contentX,
                        origin.Y +
                        Ui(11f)));

                ImGui.TextColored(
       Vector4.One,
       FitTwitchText(
           displayedChannel,
           textWidth));

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        contentX,
                        origin.Y +
                        Ui(35f)));

                ImGui.TextColored(
                    isLive
                        ? Good
                        : hasError
                            ? Danger
                            : MutedText,
                    isLive
                        ? "LIVE NOW"
                        : isOffline
                            ? "OFFLINE"
                            : hasError
                                ? "STATUS UNAVAILABLE"
                                : isRefreshingTwitchFavourites
                                    ? "CHECKING..."
                                    : "NOT CHECKED");

                var detailText =
                    isLive &&
                    stream is not null
                        ? stream.Title
                        : hasError
                            ? status!.Error!
                            : isOffline
                                ? "This channel is not live."
                                : "Waiting for a status check.";

                SetUiFontScale(
                    0.78f);

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        contentX,
                        origin.Y +
                        Ui(61f)));

                ImGui.TextColored(
                    MutedText,
                    FitTwitchText(
                        PrepareTwitchDisplayText(
                            detailText),
                        textWidth));

                if (status is not null)
                {
                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            origin.Y +
                            Ui(81f)));

                    ImGui.TextColored(
                        MutedText,
                        $"Checked " +
                        $"{FormatTwitchFavouriteCheckAge(status.CheckedAtUtc)}");
                }

                SetUiFontScale(
                    1f);

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        origin.X +
                        cardWidth -
                        starSize.X -
                        rightPadding,
                        origin.Y +
                        Ui(8f)));

                if (DrawTwitchFavouriteButton(
                        "favouriteCard",
                        channel,
                        true,
                        starSize))
                {
                    removed =
                        true;
                }

                if (isLive &&
                    stream is not null)
                {
                    var playSize =
                        new Vector2(
                            Ui(68f),
                            Ui(28f));

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            origin.X +
                            cardWidth -
                            playSize.X -
                            rightPadding,
                            origin.Y +
                            cardHeight -
                            playSize.Y -
                            Ui(8f)));

                    using (ImRaii.PushStyle(
                               ImGuiStyleVar.FrameRounding,
                               6f))
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
                                "Play##favouritePlay",
                                playSize))
                        {
                            HandlePlayNow(
                                new VideoQueueEntry(
                                    stream.Url,
                                    stream.Title,
                                    stream.ChannelName,
                                    null,
                                    stream.ThumbnailUrl));
                        }
                    }
                }
            }
        }

        ImGui.PopID();

        return removed;
    }

    private bool DrawTwitchFavouriteButton(
    string id,
    string channelName,
    bool favourite,
    Vector2 size)
    {
        var clicked =
            false;

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   7f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   favourite
                       ? new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.28f)
                       : new Vector4(
                           0.055f,
                           0.07f,
                           0.115f,
                           1f))
               .Push(
                   ImGuiCol.ButtonHovered,
                   favourite
                       ? new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.44f)
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
                    $"##{id}",
                    size))
            {
                ToggleFavouriteTwitchChannel(
                    channelName);

                clicked =
                    true;
            }
        }

        var buttonMin =
            ImGui.GetItemRectMin();

        var buttonMax =
            ImGui.GetItemRectMax();

        var star =
            FontAwesomeIcon.Star.ToIconString();

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var starSize =
                ImGui.CalcTextSize(
                    star);

            var starPosition =
                new Vector2(
                    buttonMin.X +
                    ((buttonMax.X -
                      buttonMin.X -
                      starSize.X) *
                     0.5f),

                    buttonMin.Y +
                    ((buttonMax.Y -
                      buttonMin.Y -
                      starSize.Y) *
                     0.5f));

            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                starPosition,
                ImGui.GetColorU32(
                    favourite
                        ? Vector4.One
                        : MutedText),
                star);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                favourite
                    ? "Remove from favourites"
                    : "Add to favourites");
        }

        return clicked;
    }

    private void DrawTwitchPlaceholder(
        Vector2 position,
        Vector2 size)
    {
        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            position,
            position + size,
            ImGui.GetColorU32(
                new Vector4(
                    0.025f,
                    0.035f,
                    0.06f,
                    1f)),
            7f);

        var icon =
            FontAwesomeIcon.Tv.ToIconString();

        Vector2 iconSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            iconSize =
                ImGui.CalcTextSize(
                    icon);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                position +
                new Vector2(
                    (size.X -
                     iconSize.X) *
                    0.5f,
                    (size.Y -
                     iconSize.Y) *
                    0.5f),
                ImGui.GetColorU32(
                    MutedText),
                icon);
        }
    }

    private void DrawEmptyTwitchFavourites()
    {
        var height =
            Ui(92f);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   9f))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       0.038f,
                       0.048f,
                       0.075f,
                       1f)))
        using (var empty = ImRaii.Child(
                   "##emptyTwitchFavourites",
                   new Vector2(
                       -1f,
                       height),
                   false,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!empty)
            {
                return;
            }

            var origin =
                ImGui.GetCursorScreenPos();

            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                ImGui.SetCursorScreenPos(
                    new Vector2(
                        origin.X +
                        Ui(18f),
                        origin.Y +
                        Ui(30f)));

                ImGui.TextColored(
                    Accent,
                    FontAwesomeIcon.Star.ToIconString());
            }

            ImGui.SetCursorScreenPos(
                new Vector2(
                    origin.X +
                    Ui(58f),
                    origin.Y +
                    Ui(19f)));

            ImGui.TextColored(
                Vector4.One,
                "No favourite channels yet");

            SetUiFontScale(
                0.82f);

            ImGui.SetCursorScreenPos(
                new Vector2(
                    origin.X +
                    Ui(58f),
                    origin.Y +
                    Ui(49f)));

            ImGui.TextColored(
                MutedText,
                "Find a Twitch channel above and use the star to save it here.");

            SetUiFontScale(
                1f);
        }
    }

    private static string FitTwitchText(
        string? text,
        float maximumWidth)
    {
        if (string.IsNullOrWhiteSpace(
                text))
        {
            return string.Empty;
        }

        var value =
            text.Trim();

        if (maximumWidth <= 0f ||
            ImGui.CalcTextSize(
                value).X <=
            maximumWidth)
        {
            return value;
        }

        const string suffix =
            "...";

        var low =
            0;

        var high =
            value.Length;

        while (low < high)
        {
            var middle =
                (low +
                 high +
                 1) /
                2;

            var candidate =
                value[..middle] +
                suffix;

            if (ImGui.CalcTextSize(
                    candidate).X <=
                maximumWidth)
            {
                low =
                    middle;
            }
            else
            {
                high =
                    middle -
                    1;
            }
        }

        return value[..low] +
               suffix;
    }

    private static string PrepareTwitchDisplayText(
    string? text)
    {
        if (string.IsNullOrWhiteSpace(
                text))
        {
            return string.Empty;
        }

        var output =
            new System.Text.StringBuilder(
                text.Length);

        var replacedSymbol =
            false;

        for (var index = 0;
             index < text.Length;
             index++)
        {
            var character =
                text[index];

            var isSurrogatePair =
                char.IsHighSurrogate(
                    character) &&
                index + 1 <
                text.Length &&
                char.IsLowSurrogate(
                    text[index + 1]);

            var isUnsupportedSymbol =
                isSurrogatePair ||
                character is >= '\u2600' and <= '\u27BF';

            if (isUnsupportedSymbol)
            {
                if (!replacedSymbol &&
                    output.Length > 0 &&
                    output[^1] != ' ')
                {
                    output.Append(
                        " • ");
                }

                replacedSymbol =
                    true;

                if (isSurrogatePair)
                {
                    index++;
                }

                continue;
            }

            // Remove emoji presentation selectors and joiners.
            if (character is '\uFE0E' or
                '\uFE0F' or
                '\u200D')
            {
                continue;
            }

            output.Append(
                character);

            if (!char.IsWhiteSpace(
                    character))
            {
                replacedSymbol =
                    false;
            }
        }

        return output
            .ToString()
            .Trim()
            .Trim('•')
            .Trim();
    }

    private static string FormatTwitchFavouriteCheckAge(
        DateTime checkedAtUtc)
    {
        var age =
            DateTime.UtcNow -
            checkedAtUtc;

        if (age <
            TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age <
            TimeSpan.FromHours(1))
        {
            var minutes =
                Math.Max(
                    1,
                    (int)age.TotalMinutes);

            return
                $"{minutes} min ago";
        }

        var hours =
            Math.Max(
                1,
                (int)age.TotalHours);

        return
            $"{hours} hr ago";
    }

    private async Task RunTwitchCheckAsync(
    string input)
    {
        if (!TwitchChannelChecker.TryNormalizeChannelName(
                input,
                out var channelName,
                out var normalizationError))
        {
            twitchResult =
                null;

            twitchError =
                normalizationError;

            twitchResultIsOffline =
                false;

            twitchCheckedChannelName =
                null;

            isCheckingTwitch =
                false;

            return;
        }

        twitchChannelInput =
            channelName;

        twitchCheckedChannelName =
            channelName;

        var ytdlpPath =
            screenController.Engine.Resources
                .GetLocationYTDLP();

        if (ytdlpPath is null)
        {
            twitchError =
                "yt-dlp isn't downloaded yet - try again in a moment.";

            twitchResultIsOffline =
                false;

            isCheckingTwitch =
                false;

            return;
        }

        var (stream, error, isOffline) =
            await twitchChecker.CheckLiveAsync(
                    ytdlpPath,
                    channelName,
                    CancellationToken.None)
                .ConfigureAwait(false);

        twitchResult =
            stream;

        twitchResultIsOffline =
            isOffline;

        //
        // Offline is a valid result and will receive its own result card.
        // Only genuine failures should render as red errors.
        //

        twitchError =
            isOffline
                ? null
                : error;

        isCheckingTwitch =
            false;
    }
}
