using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Diagnostics;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly VideoUrlResolver searchResolver = new();
    private readonly TwitchChannelChecker twitchChecker = new();
    private string searchQuery = string.Empty;
    private string cookiesPathInput = Plugin.Cfg.YouTubeCookiesPath ?? string.Empty;
    private string? cookiesSearchError;

    // Written from RunSearchAsync's continuation, which resumes on an arbitrary thread pool thread
    // (not the main thread Draw() runs on) - same reasoning as Plugin.cs's pendingRemoteState.
    private volatile bool isSearching;
    private volatile List<VideoSearchEntry>? searchResults;

    // Home media-hub YouTube shelf.
    // Kept separate from Player search so Home doesn't overwrite the user's Browse results.
    private volatile bool isLoadingHomeYouTube;
    private volatile List<VideoSearchEntry>? homeYouTubeResults;
    private bool homeYouTubeRequested;
    private DateTime homeYouTubeCacheTime;
    private readonly Random trendingRandom = new();

    // Browse Videos full-page discovery
    private volatile bool isLoadingBrowseVideos;
    private volatile Dictionary<string, List<VideoSearchEntry>>? browseVideoResults;
    private DateTime browseVideoCacheTime;
    private bool browseVideoRequested;
    private CancellationTokenSource? browseVideosCts;

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
    private string dailymotionSearchQuery = string.Empty;
    private volatile bool isSearchingDailymotion;
    private volatile List<VideoSearchEntry>? dailymotionSearchResults;
    private volatile string? dailymotionSearchError;

    private string twitchChannelInput = string.Empty;
    private volatile bool isCheckingTwitch;
    private volatile TwitchStreamInfo? twitchResult;
    private volatile string? twitchError;

    private bool trendingDirty = true;
    private TwitchStreamDto[] trendingStreams = [];

    // Manual YouTube/Twitch panels — Player source tabs call these directly.
    private void DrawYouTubeSearch()
    {
        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Search YouTube");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 10f));

        // Search field
        ImGui.SetNextItemWidth(-66f);

        bool submitted;

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
            submitted = ImGui.InputTextWithHint(
                "##search",
                "Search YouTube...",
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
            clicked = ImGui.Button(
                FontAwesomeIcon.Search.ToIconString(),
                new Vector2(48f, 0f));
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
            ImGui.Dummy(new Vector2(0f, 6f));

            ImGui.TextColored(
                MutedText,
                "Searching...");
        }

        if (searchResults is not { } results ||
            results.Count == 0)
        {
            return;
        }

        ImGui.Dummy(new Vector2(0f, 16f));

        ImGui.TextColored(
      Accent,
      $"Results ({results.Count})");

        // Small explanation beside the result count.
        ImGui.SameLine(0f, 8f);

        ImGui.SetWindowFontScale(0.72f);

        ImGui.TextColored(
            MutedText,
            "Showing first 15 results");

        ImGui.SetWindowFontScale(1f);

        // Temporary queue confirmation on the right.
        if (ImGui.GetTime() < queueAddedFeedbackUntil)
        {
            const string feedbackText = "Video added to queue";

            var feedbackTextSize = ImGui.CalcTextSize(feedbackText);

            ImGui.SameLine(
                ImGui.GetContentRegionMax().X -
                feedbackTextSize.X -
                22f);

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    Good,
                    FontAwesomeIcon.Check.ToIconString());
            }

            ImGui.SameLine(0f, 6f);

            ImGui.TextColored(
                Good,
                feedbackText);
        }

        ImGui.Dummy(new Vector2(0f, 8f));

        // Only the search results scroll.
        // Keep the heading and search box fixed above.

        // Only the search results scroll.
        // Keep the heading and search box fixed above.
        var resultsHeight = MathF.Max(
            120f,
            ImGui.GetContentRegionAvail().Y - 8f);

        using var resultsChild = ImRaii.Child(
            "##youtubeResults",
            new Vector2(-1f, resultsHeight),
            false,
            ImGuiWindowFlags.None);

        if (!resultsChild)
        {
            return;
        }

        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];

            ImGui.PushID(index);

            const float rowHeight = 64f;

            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(0.045f, 0.06f, 0.10f, 1f)))
            using (var row = ImRaii.Child(
                $"##youtubeResult_{index}",
                new Vector2(-1f, rowHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (row)
                {
                    var rowOrigin = ImGui.GetCursorScreenPos();

                    // Thumbnail
                    var thumbnail = thumbnails.Get(
                        result.ThumbnailUrl);

                    var thumbWidth = 96f;
                    var thumbHeight = rowHeight;

                    if (thumbnail is not null)
                    {
                        ImGui.GetWindowDrawList().AddImageRounded(
                            thumbnail.Handle,
                            rowOrigin,
                            rowOrigin + new Vector2(
                                thumbWidth,
                                thumbHeight),
                            Vector2.Zero,
                            Vector2.One,
                            uint.MaxValue,
                            8f);
                    }

                    // Content starts to the right of thumbnail
                    var contentX =
                        rowOrigin.X +
                        thumbWidth +
                        12f;

                    var controlsWidth = 145f;

                    var textWidth =
                        ImGui.GetWindowWidth() -
                        thumbWidth -
                        controlsWidth -
                        28f;

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            rowOrigin.Y + 10f));

                    ImGui.PushTextWrapPos(
    contentX + textWidth);

                    ImGui.TextColored(
                        Vector4.One,
                        TruncateVideoTitle(result.Title));

                    ImGui.PopTextWrapPos();

                    var meta =
                        result.Duration is { } duration
                            ? $"{result.ChannelName}  •  {FormatTime((float)duration.TotalSeconds)}"
                            : result.ChannelName;

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            rowOrigin.Y + 36f));

                    ImGui.TextColored(
                        MutedText,
                        meta);

                    // Play button
                    var playSize =
                        new Vector2(68f, 26f);

                    var playPos =
                        new Vector2(
                            rowOrigin.X +
                            ImGui.GetWindowWidth() -
                            186f,
                            rowOrigin.Y +
                            rowHeight -
                            playSize.Y -
                            6f);

                    ImGui.SetCursorScreenPos(
                        playPos);

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
                        var buttonPos =
                            ImGui.GetCursorScreenPos();

                        if (ImGui.Button(
                            $"##play_{index}",
                            playSize))
                        {
                            queue.PlayNow(
                                new VideoQueueEntry(
                                    result.Url,
                                    result.Title,
                                    result.ChannelName,
                                    result.Duration,
                                    result.ThumbnailUrl));
                        }

                        DrawPlayerActionButtonContent(
                            buttonPos,
                            playSize,
                            FontAwesomeIcon.Play,
                            "Play",
                            Vector4.One);
                    }

                    // Add button
                    var addSize =
    new Vector2(62f, 26f);

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            playPos.X +
                            playSize.X +
                            8f,
                            playPos.Y));

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
                        var buttonPos =
                            ImGui.GetCursorScreenPos();

                        if (ImGui.Button(
                            $"##add_{index}",
                            addSize))
                        {
                            queue.Add(
                                new VideoQueueEntry(
                                    result.Url,
                                    result.Title,
                                    result.ChannelName,
                                    result.Duration,
                                    result.ThumbnailUrl));

                            queueAddedFeedbackUntil =
                                ImGui.GetTime() + 2.0;
                        }

                        ImGui.GetWindowDrawList().AddRect(
                            buttonPos,
                            buttonPos + addSize,
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
                            addSize,
                            FontAwesomeIcon.Plus,
                            "Add",
                            Vector4.One);
                    }
                }
            }

            ImGui.PopID();

            ImGui.Dummy(
                new Vector2(0f, 8f));
        }

        
    }

    private void DrawDailymotionSearch()
    {
        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Search Dailymotion");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 10f));

        ImGui.SetNextItemWidth(-66f);

        bool submitted;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
        {
            submitted = ImGui.InputTextWithHint(
                "##dailymotionSearch",
                "Search Dailymotion...",
                ref dailymotionSearchQuery,
                200,
                ImGuiInputTextFlags.EnterReturnsTrue);
        }

        ImGui.SameLine();

        var clicked = ImGui.Button(
            "Search##dailymotion",
            new Vector2(80, 0));

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


        ImGui.Dummy(new Vector2(0f, 16f));

        ImGui.TextColored(
            Accent,
            $"Results ({results.Count})");

        ImGui.SameLine(0f, 8f);

        ImGui.SetWindowFontScale(0.72f);

        ImGui.TextColored(
            MutedText,
            "Showing first 15 results");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 8f));
        using (var child = ImRaii.Child(
    "dailymotionResults",
    new Vector2(
        0,
        300),
    false))
        {
            if (child)
            {
                foreach (var result in results)
        {
            var index = results.IndexOf(result);

            ImGui.PushID($"dailymotion_{index}");

            const float rowHeight = 64f;

            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(0.045f, 0.06f, 0.10f, 1f)))
            using (var row = ImRaii.Child(
                $"##dailymotionResult_{index}",
                new Vector2(-1f, rowHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (row)
                {
                    var rowOrigin = ImGui.GetCursorScreenPos();

                    // Thumbnail
                    var thumbnail = thumbnails.Get(result.ThumbnailUrl);

                    const float thumbWidth = 96f;

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

                    // Text area
                    var contentX =
                        rowOrigin.X +
                        thumbWidth +
                        12f;

                    const float controlsWidth = 145f;

                    var textWidth =
                        ImGui.GetWindowWidth() -
                        thumbWidth -
                        controlsWidth -
                        28f;


                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            rowOrigin.Y + 10f));

                    ImGui.PushTextWrapPos(
    contentX + textWidth);

                    ImGui.TextColored(
                        Vector4.One,
                        TruncateVideoTitle(result.Title));

                    ImGui.PopTextWrapPos();


                    var meta =
                        result.Duration is { } duration
                            ? $"{result.ChannelName}  •  {FormatTime((float)duration.TotalSeconds)}"
                            : result.ChannelName;

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            rowOrigin.Y + 36f));

                    ImGui.TextColored(
                        MutedText,
                        meta);


                    // Play button
                    var playSize =
                      new Vector2(68f, 26f);

                    var playPos =
                        new Vector2(
                            rowOrigin.X +
                            ImGui.GetWindowWidth() -
                            174f,
                        rowOrigin.Y +
                        rowHeight -
                        playSize.Y -
                        6f);

                    ImGui.SetCursorScreenPos(playPos);

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
                        var buttonPos =
                            ImGui.GetCursorScreenPos();

                        if (ImGui.Button(
                            $"##dmPlay_{index}",
                            playSize))
                        {
                            queue.PlayNow(
                                new VideoQueueEntry(
                                    result.Url,
                                    result.Title,
                                    result.ChannelName,
                                    result.Duration,
                                    result.ThumbnailUrl));
                        }

                        DrawPlayerActionButtonContent(
                            buttonPos,
                            playSize,
                            FontAwesomeIcon.Play,
                            "Play",
                            Vector4.One);
                    }


                    // Add button
                    var addSize =
    new Vector2(62f, 26f);

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            playPos.X +
                            playSize.X +
                            8f,
                            playPos.Y));

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
                        var buttonPos =
                            ImGui.GetCursorScreenPos();

                        if (ImGui.Button(
                            $"##dmAdd_{index}",
                            addSize))
                        {
                            queue.Add(
                                new VideoQueueEntry(
                                    result.Url,
                                    result.Title,
                                    result.ChannelName,
                                    result.Duration,
                                    result.ThumbnailUrl));

                            queueAddedFeedbackUntil =
                                ImGui.GetTime() + 2.0;
                        }

                        ImGui.GetWindowDrawList().AddRect(
                            buttonPos,
                            buttonPos + addSize,
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
                            addSize,
                            FontAwesomeIcon.Plus,
                            "Add",
                            Vector4.One);
                    }
                }
            }

            ImGui.PopID();

            ImGui.Dummy(
                new Vector2(0f, 8f));
        }
            }
        }
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

        return topics;
    }

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

    private async Task LoadHomeYouTubeAsync(bool forceRefresh = false)
    {
        try
        {
            // Use cached results for 20 minutes unless manually refreshed.
            if (false)
            {
                return;
            }

            var topics = GetEnabledTrendingTopics();
            // Safety fallback - treat all topics as enabled.
            if (topics.Count < 3)
            {
                topics =
   [
       new("Gaming", ["trending gaming videos", "gaming news", "new game releases"]),
    new("MMORPG", ["MMORPG news", "new MMORPG releases", "MMORPG gameplay"]),
    new("Final Fantasy", ["Final Fantasy XIV", "FFXIV news", "FF14 gameplay"]),
    new("Anime", ["anime trailers", "anime trending", "anime news"]),
    new("Movies", ["movie trailers", "movie news", "best movies"]),
    new("TV Shows", ["new TV shows", "TV show trailers", "TV show news"]),
    new("Music", ["new music releases", "music trending", "latest songs"]),
    new("Memes", ["funny memes", "viral memes", "meme compilation"]),
    new("Wildlife", ["amazing wildlife documentary", "wildlife discoveries", "animal documentary"]),
    new("Architecture", ["amazing architecture", "modern architecture design", "unique buildings"]),
    new("Science", ["science discoveries", "latest science news", "amazing science"]),
    new("Space", ["space discoveries", "NASA news", "universe documentary"]),
    new("History", ["history documentary", "historical discoveries", "ancient history"]),
    new("Technology", ["latest technology news", "new technology", "future technology"]),
    new("Pets", ["cute pets", "funny animals", "adorable pets"]),
    new("Food", ["amazing food", "cooking videos", "food discoveries"]),
    new("Travel", ["beautiful places travel", "travel discoveries", "amazing destinations"]),
    new("Cars", ["car news", "supercars", "car reviews"]),
    new("Sports", ["sports highlights", "sports news", "best sports moments"])
   ];
            }


            var selectedTopics = topics
                .OrderBy(_ => trendingRandom.Next())
                .Take(3)
                .ToList();


            var searches = selectedTopics
     .Select(topic =>
         searchResolver.SearchWithMetadataAsync(
             topic.SearchQueries[
                 trendingRandom.Next(topic.SearchQueries.Length)],
             5,
             CancellationToken.None))
     .ToList();

            var searchResults = await Task
                .WhenAll(searches)
                .ConfigureAwait(false);

            var results = searchResults
                .SelectMany(x => x)
                .ToList();

            homeYouTubeResults = results
                .GroupBy(x => x.Url)
                .Select(x => x.First())
                .OrderByDescending(GetTrendingScore)
                .Take(5)
                .ToList();

            foreach (var video in homeYouTubeResults)
            {
                AepLog.Warning(
                    $"[TRENDING TEST] {video.Title} | {video.Url}");
            }

            homeYouTubeCacheTime = DateTime.UtcNow;
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Home] Failed to load YouTube shelf: {exception.Message}");

            homeYouTubeResults = [];
        }
        finally
        {
            isLoadingHomeYouTube = false;
        }
    }

    private async Task LoadBrowseVideosAsync(bool forceRefresh = false)
    {
        try
        {
            browseVideosCts?.Cancel();
            browseVideosCts = new CancellationTokenSource();
            // Reuse Browse results for 20 minutes unless manually refreshed.
            if (!forceRefresh &&
                browseVideoResults is { Count: > 0 } &&
                DateTime.UtcNow - browseVideoCacheTime < TimeSpan.FromMinutes(20))
            {
                return;
            }

            var topics = GetEnabledTrendingTopics();

            if (topics.Count == 0)
            {
                browseVideoResults = [];
                return;
            }

            // Pick up to 8 topics for the full Browse page.
            var selectedTopics = topics
                .OrderBy(_ => trendingRandom.Next())
                .ToList();

            // Start with an empty dictionary so rows can appear
            // progressively as each batch finishes.
            browseVideoResults =
                new Dictionary<string, List<VideoSearchEntry>>();

            // Load in batches of 3.
            const int batchSize = 3;

            for (var batchStart = 0;
                 batchStart < selectedTopics.Count;
                 batchStart += batchSize)
            {
                var batch = selectedTopics
                    .Skip(batchStart)
                    .Take(batchSize)
                    .ToList();

                var searches = batch
                    .Select(async topic =>
                    {
                        var query =
                            topic.SearchQueries[
                                trendingRandom.Next(
                                    topic.SearchQueries.Length)];

                        var results = await searchResolver
                            .SearchAsync(
                                query,
                                15,
                                browseVideosCts.Token)
                            .ConfigureAwait(false);

                        browseVideoResults[topic.Name] = results;

                        var ranked = results
      .GroupBy(x => x.Url)
      .Select(x => x.First())
      .Take(10)
      .ToList();

                        var enriched = await Task.WhenAll(
                            ranked.Select(
                                video =>
                                    searchResolver.EnrichSearchResultAsync(
                                        video,
                                        browseVideosCts.Token)));

                        ranked = enriched
                            .OrderByDescending(GetTrendingScore)
                            .ToList();

                        return new
                        {
                            topic.Name,
                            Results = ranked
                        };
                    })
                    .ToList();

                var loadedBatch = await Task
                    .WhenAll(searches)
                    .ConfigureAwait(false);

                // Create a NEW dictionary when publishing the batch.
                // This avoids modifying the dictionary that DrawVideoGrid()
                // may currently be enumerating on the UI thread.
                var updatedResults =
                    new Dictionary<string, List<VideoSearchEntry>>(
                        browseVideoResults);

                foreach (var loadedTopic in loadedBatch)
                {
                    if (loadedTopic.Results.Count == 0)
                    {
                        continue;
                    }

                    updatedResults[loadedTopic.Name] =
                        loadedTopic.Results;
                }

                browseVideoResults = updatedResults;
            }

            browseVideoCacheTime = DateTime.UtcNow;
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Browse Videos] Failed to load videos: {exception.Message}");

            browseVideoResults = [];
        }
        finally
        {
            isLoadingBrowseVideos = false;
        }
    }

    private async Task LoadFfxivYouTubeAsync()
    {
        try
        {
            ffxivYouTubeResults = await searchResolver
                .SearchLatestAggregatedAsync(
                    [
                        "ffxiv",
                    "ff14",
                    "final fantasy xiv"
                    ],
                    10,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Home] Failed to load FFXIV YouTube shelf: {exception.Message}");


            ffxivYouTubeResults = [];
        }
        finally
        {
            isLoadingFfxivYouTube = false;
        }
    }

    private async Task RunSearchAsync(string query)
    {
        searchResults = await searchResolver.SearchAsync(query, 15,CancellationToken.None).ConfigureAwait(false);
        isSearching = false;
    }

    private async Task RunDailymotionSearchAsync(string query)
    {
        try
        {
            using var http = new HttpClient();

            var encoded =
                Uri.EscapeDataString(query);

            var url =
     "https://api.dailymotion.com/videos" +
     $"?search={encoded}" +
     $"&limit=15" +
     "&fields=id,title,thumbnail_url,duration";

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

                    AepLog.Warning(
                        $"[Dailymotion] Queue URL: {watchUrl}");

                    results.Add(
                        new VideoSearchEntry(
                            title,
                            watchUrl,
                            "Dailymotion",
                            duration,
                            thumbnail));
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

    // Opt-in workaround for age-restricted videos, which yt-dlp otherwise refuses outright. Only
    // ever stores/uses a file path the player supplies themselves - see Configuration's own note
    // on why this isn't something the plugin generates or transmits.
    private void DrawCookiesSettings()
    {
        ImGui.TextWrapped("Age-restricted videos need a YouTube login (cookies.txt).");
        if (ImGui.Button("Open YouTube to sign in"))
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://www.youtube.com") { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                AepLog.Warning($"[YouTube] Failed to open browser: {exception.Message}");
            }
        }

        ImGui.Spacing();

        var useFirefox = Plugin.Cfg.UseFirefoxCookies;
        if (ImGui.Checkbox("Read cookies from Firefox automatically", ref useFirefox))
        {
            Plugin.Cfg.UseFirefoxCookies = useFirefox;
            Plugin.Cfg.Save();
            video.UseFirefoxCookies = useFirefox;
        }

        ImGui.TextDisabled("Best-effort - needs an actual logged-in Firefox session.");
        ImGui.TextDisabled("Falls back to the path below if it can't find one.");

        ImGui.Spacing();
        ImGui.SetNextItemWidth(-70f);
        ImGui.InputTextWithHint("##cookiesPath", "Path to cookies.txt", ref cookiesPathInput, 260);
        ImGui.SameLine();
        if (ImGui.Button("Save##cookies"))
        {
            var path = string.IsNullOrWhiteSpace(cookiesPathInput) ? null : cookiesPathInput.Trim();
            Plugin.Cfg.YouTubeCookiesPath = path;
            Plugin.Cfg.Save();
            video.CookiesPath = path;
        }

        if (ImGui.SmallButton("Find in Downloads"))
        {
            cookiesSearchError = null;
            var found = FindCookiesFileInDownloads();
            if (found is not null)
            {
                cookiesPathInput = found;
            }
            else
            {
                cookiesSearchError = "No cookies file found in Downloads - export one first (see above).";
            }
        }

        if (cookiesSearchError is { } searchError)
        {
            ImGui.TextColored(Danger, searchError);
        }

        if (!string.IsNullOrEmpty(Plugin.Cfg.YouTubeCookiesPath))
        {
            var exists = File.Exists(Plugin.Cfg.YouTubeCookiesPath);
            ImGui.TextColored(exists ? Good : Danger, exists ? "Cookies file found." : "File not found at that path.");
        }
    }

    // Browser cookie-export extensions default to saving into Downloads - this saves typing the
    // full path out by hand. Picks whichever matching file was modified most recently, in case
    // there are several from past exports.
    private static string? FindCookiesFileInDownloads()
    {
        try
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(downloads))
            {
                return null;
            }

            return Directory.GetFiles(downloads, "*cookies*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[YouTube] Failed to search Downloads for a cookies file: {exception.Message}");
            return null;
        }
    }

    // Real trending data via Twitch's own Helix API (server-side, see Server/Twitch), not scraping.
    private void DrawTwitchTrending()
    {
        ImGui.TextColored(
            Accent,
            "Trending on Twitch");

        ImGui.Dummy(new Vector2(0f, 8f));

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
                new Vector2(92f, 32f)))
            {
                trendingDirty = true;
            }
        }

        if (trendingStreams.Length == 0)
        {
            ImGui.Dummy(new Vector2(0f, 6f));

            ImGui.TextColored(
                MutedText,
                "Nothing trending right now.");

            return;
        }

        ImGui.Dummy(new Vector2(0f, 10f));

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

            const float rowHeight = 70f;

            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(0.045f, 0.06f, 0.10f, 1f)))
            using (var row = ImRaii.Child(
                $"##trending_{stream.ChannelName}",
                new Vector2(-6f, rowHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (row)
                {
                    var rowOrigin =
                        ImGui.GetCursorScreenPos();

                    const float thumbWidth = 105f;

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

                    const float controlsWidth = 120f;

                    var textWidth =
                        ImGui.GetWindowWidth() -
                        thumbWidth -
                        controlsWidth -
                        28f;

                    // Stream title
                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            rowOrigin.Y + 10f));

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
                            rowOrigin.Y + 40f));

                    ImGui.TextColored(
                        MutedText,
                        $"{stream.ChannelName}  •  " +
                        $"{stream.GameName}  •  " +
                        $"{stream.ViewerCount:N0} viewers");

                    // Play button
                    var playSize =
                        new Vector2(92f, 34f);

                    var playPos =
                        new Vector2(
                            rowOrigin.X +
                            ImGui.GetWindowWidth() -
                            104f,
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
                            queue.PlayNow(
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
                new Vector2(0f, 8f));
        }
    }

    // Not a real search - see TwitchChannelChecker's own comment on why. Just checks whether one
    // named channel is currently live.
    private void DrawTwitchCheck()
    {
        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Look up a Twitch channel");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 10f));

        // Channel input
        ImGui.SetNextItemWidth(-66f);

        bool submitted;

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
            submitted = ImGui.InputTextWithHint(
                "##twitchChannel",
                "Enter a Twitch channel name...",
                ref twitchChannelInput,
                64,
                ImGuiInputTextFlags.EnterReturnsTrue);
        }

        ImGui.SameLine(0f, 10f);

        // Check/search button
        bool clicked;

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
            clicked = ImGui.Button(
                FontAwesomeIcon.Search.ToIconString(),
                new Vector2(48f, 0f));
        }

        if ((submitted || clicked) &&
            twitchChannelInput.Length > 0 &&
            !isCheckingTwitch)
        {
            isCheckingTwitch = true;
            twitchResult = null;
            twitchError = null;

            _ = RunTwitchCheckAsync(
                twitchChannelInput.Trim());
        }

        ImGui.Dummy(new Vector2(0f, 5f));

        ImGui.SetWindowFontScale(0.82f);

ImGui.TextColored(
    MutedText,
    "Search a Twitch username to see if they're live and tune in.");

// Temporary queue confirmation on the right.
if (ImGui.GetTime() < queueAddedFeedbackUntil)
{
    const string feedbackText = "Video added to queue";

    var feedbackTextSize =
        ImGui.CalcTextSize(feedbackText);

    ImGui.SameLine(
        ImGui.GetContentRegionMax().X -
        feedbackTextSize.X -
        22f);

    using (ImRaii.PushFont(UiBuilder.IconFont))
    {
        ImGui.TextColored(
            Good,
            FontAwesomeIcon.Check.ToIconString());
    }

    ImGui.SameLine(0f, 6f);

    ImGui.TextColored(
        Good,
        feedbackText);
}

ImGui.SetWindowFontScale(1f);

        if (isCheckingTwitch)
        {
            ImGui.Dummy(new Vector2(0f, 8f));

            ImGui.TextColored(
                MutedText,
                "Checking...");
        }

        if (twitchError is { } error)
        {
            ImGui.Dummy(new Vector2(0f, 8f));

            ImGui.TextColored(
                Danger,
                error);
        }

        if (twitchResult is { } stream)
        {
            ImGui.Dummy(new Vector2(0f, 16f));

            const float rowHeight = 70f;

            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(0.045f, 0.06f, 0.10f, 1f)))
            using (var row = ImRaii.Child(
                "##twitchResult",
                new Vector2(-1f, rowHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (row)
                {
                    var rowOrigin =
                        ImGui.GetCursorScreenPos();

                    const float thumbWidth = 105f;
                    const float thumbHeight = rowHeight;

                    var thumbnail =
                        thumbnails.Get(stream.ThumbnailUrl);

                    if (thumbnail is not null)
                    {
                        ImGui.GetWindowDrawList().AddImageRounded(
                            thumbnail.Handle,
                            rowOrigin,
                            rowOrigin + new Vector2(
                                thumbWidth,
                                thumbHeight),
                            Vector2.Zero,
                            Vector2.One,
                            uint.MaxValue,
                            8f);
                    }

                    var contentX =
                        rowOrigin.X +
                        thumbWidth +
                        12f;

                    const float controlsWidth = 190f;

                    var textWidth =
                        ImGui.GetWindowWidth() -
                        thumbWidth -
                        controlsWidth -
                        28f;

                    // Title
                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            rowOrigin.Y + 11f));

                    ImGui.PushTextWrapPos(
                        contentX + textWidth);

                    ImGui.TextColored(
                        Vector4.One,
                        stream.Title);

                    ImGui.PopTextWrapPos();

                    // Channel metadata
                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            rowOrigin.Y + 41f));

                    ImGui.TextColored(
                        MutedText,
                        $"{stream.ChannelName}  •  Live now");

                    // Play
                    var playSize =
                        new Vector2(92f, 34f);

                    var playPos =
                        new Vector2(
                            rowOrigin.X +
                            ImGui.GetWindowWidth() -
                            174f,
                            rowOrigin.Y +
                            (rowHeight - playSize.Y) * 0.5f);

                    ImGui.SetCursorScreenPos(playPos);

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
                        var buttonPos =
                            ImGui.GetCursorScreenPos();

                        if (ImGui.Button(
                            "##twitchPlay",
                            playSize))
                        {
                            queue.PlayNow(
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

                    // Add
                    var addSize =
                        new Vector2(70f, 34f);

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            playPos.X +
                            playSize.X +
                            8f,
                            playPos.Y));

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
                        var buttonPos =
                            ImGui.GetCursorScreenPos();

                        if (ImGui.Button(
                            "##twitchAdd",
                            addSize))
                        {
                            queue.Add(
                                new VideoQueueEntry(
                                    stream.Url,
                                    stream.Title,
                                    stream.ChannelName,
                                    null,
                                    stream.ThumbnailUrl));

                            queueAddedFeedbackUntil =
                                ImGui.GetTime() + 2.0;
                        }

                        ImGui.GetWindowDrawList().AddRect(
                            buttonPos,
                            buttonPos + addSize,
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
                            addSize,
                            FontAwesomeIcon.Plus,
                            "Add",
                            Vector4.One);
                    }
                }
            }
        }

        ImGui.Dummy(new Vector2(0f, 18f));

        DrawTwitchTrending();
    }

    private async Task RunTwitchCheckAsync(string channelName)
    {
        var ytdlpPath = screenController.Engine.Resources.GetLocationYTDLP();
        if (ytdlpPath is null)
        {
            twitchError = "yt-dlp isn't downloaded yet - try again in a moment.";
            isCheckingTwitch = false;
            return;
        }

        var (stream, error) = await twitchChecker.CheckLiveAsync(ytdlpPath, channelName, CancellationToken.None)
            .ConfigureAwait(false);
        twitchResult = stream;
        twitchError = error;
        isCheckingTwitch = false;
    }
}
