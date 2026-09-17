using System.Text.Json;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum UnifiedSearchProvider
    {
        YouTube,
        Dailymotion,
        InternetArchive,
    }

    private sealed record UnifiedSearchResult(
        UnifiedSearchProvider Provider,
        VideoSearchEntry? Video,
        InternetArchiveItem? Archive)
    {
        public string Title => Video?.Title ?? Archive?.Title ?? string.Empty;
        public string Creator => Video?.ChannelName ?? Archive?.Creator ?? string.Empty;
        public string? ThumbnailUrl => Video?.ThumbnailUrl ?? Archive?.ThumbnailUrl;
        public TimeSpan? Duration => Video?.Duration ?? Archive?.Duration;
        public string ExactKey => Provider == UnifiedSearchProvider.InternetArchive
            ? Archive?.Identifier ?? string.Empty
            : Video?.Url ?? string.Empty;
    }

    private readonly object unifiedSearchSync = new();
    private CancellationTokenSource? unifiedSearchCts;
    private string unifiedSearchQuery = string.Empty;
    private string unifiedSearchCompletedQuery = string.Empty;
    private volatile IReadOnlyList<VideoSearchEntry> unifiedYouTubeResults = Array.Empty<VideoSearchEntry>();
    private volatile IReadOnlyList<VideoSearchEntry> unifiedDailymotionResults = Array.Empty<VideoSearchEntry>();
    private volatile IReadOnlyList<InternetArchiveItem> unifiedArchiveResults = Array.Empty<InternetArchiveItem>();
    private volatile bool unifiedYouTubeSearching;
    private volatile bool unifiedDailymotionSearching;
    private volatile bool unifiedArchiveSearching;
    private volatile string? unifiedYouTubeError;
    private volatile string? unifiedDailymotionError;
    private volatile string? unifiedArchiveError;
    private int unifiedSearchGeneration;
    private bool unifiedShowYouTube = true;
    private bool unifiedShowDailymotion = true;
    private bool unifiedShowArchive = true;
    private VideoSearchOrder unifiedSearchOrder = VideoSearchOrder.Relevance;
    private VideoDurationFilter unifiedDurationFilter = VideoDurationFilter.Any;

    private void OpenUnifiedVideoSearch(string query)
    {
        currentPage = HomePage.UnifiedSearch;
        unifiedSearchQuery = query.Trim();
        StartUnifiedSearch();
    }

    private void DrawUnifiedSearchPage()
    {
        var searchWidth = Ui(112f);
        ImGui.SetNextItemWidth(MathF.Max(Ui(180f), ImGui.GetContentRegionAvail().X - searchWidth - Ui(10f)));
        var submitted = ImGui.InputTextWithHint(
            "##unifiedVideoSearch",
            "Search videos across all providers...",
            ref unifiedSearchQuery,
            300,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine(0f, Ui(10f));
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(unifiedSearchQuery)))
        {
            submitted |= GameLayoutButton(
                "Search",
                FontAwesomeIcon.Search,
                searchWidth,
                true,
                height: 40f);
        }
        if (submitted && !string.IsNullOrWhiteSpace(unifiedSearchQuery)) StartUnifiedSearch();

        ImGui.Dummy(UiVec(0f, 10f));
        DrawUnifiedProviderFilters();
        ImGui.Dummy(UiVec(0f, 9f));
        DrawUnifiedProviderStatuses();
        ImGui.Dummy(UiVec(0f, 18f));

        var results = BuildUnifiedSearchResults();
        SetUiFontScale(1.25f);
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(unifiedSearchCompletedQuery)
            ? "Search results"
            : $"Results for “{unifiedSearchCompletedQuery}”");
        SetUiFontScale(1f);
        ImGui.TextColored(MutedText, $"{results.Count} results so far");
        ImGui.Dummy(UiVec(0f, 10f));

        if (results.Count == 0)
        {
            ImGui.TextColored(MutedText, IsUnifiedSearchRunning
                ? "Searching providers for playable videos..."
                : "Enter a search above to find videos.");
            return;
        }

        DrawUnifiedResultsGrid(results);
    }

    private bool IsUnifiedSearchRunning =>
        unifiedYouTubeSearching || unifiedDailymotionSearching || unifiedArchiveSearching;

    private void DrawUnifiedProviderFilters()
    {
        var all = unifiedShowYouTube && unifiedShowDailymotion && unifiedShowArchive;
        if (DrawUnifiedFilterChip("All sources", all, FontAwesomeIcon.Globe, Accent))
            unifiedShowYouTube = unifiedShowDailymotion = unifiedShowArchive = true;
        ImGui.SameLine(0f, Ui(7f));
        if (DrawUnifiedFilterChip("YouTube", unifiedShowYouTube && !all, FontAwesomeIcon.Play, Hex(0xFF0033)))
            unifiedShowYouTube = !unifiedShowYouTube;
        ImGui.SameLine(0f, Ui(7f));
        if (DrawUnifiedFilterChip("Dailymotion", unifiedShowDailymotion && !all, FontAwesomeIcon.Video, Hex(0x168AFF)))
            unifiedShowDailymotion = !unifiedShowDailymotion;
        ImGui.SameLine(0f, Ui(7f));
        if (DrawUnifiedFilterChip("Internet Archive", unifiedShowArchive && !all, FontAwesomeIcon.Film, Hex(0xEAB308)))
            unifiedShowArchive = !unifiedShowArchive;

        DrawVideoSearchFilters("unified", ref unifiedSearchOrder, ref unifiedDurationFilter);
    }

    private bool DrawUnifiedFilterChip(string label, bool selected, FontAwesomeIcon icon, Vector4 color)
    {
        var iconText = icon.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            iconSize = ImGui.CalcTextSize(iconText);
        var labelSize = ImGui.CalcTextSize(label);
        var size = new Vector2(iconSize.X + labelSize.X + Ui(31f), Ui(34f));
        var origin = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##unifiedProvider_{label}", size);
        var hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(selected ? (hovered ? AccentHover : Accent) : (hovered ? CardBgHover : CardBg)),
            Ui(9f));
        draw.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(selected ? AccentHover : BorderSubtle),
            Ui(9f));
        draw.AddText(
            UiBuilder.IconFont,
            ImGui.GetFontSize(),
            origin + new Vector2(Ui(11f), (size.Y - iconSize.Y) * 0.5f),
            ImGui.GetColorU32(selected ? Vector4.One : color),
            iconText);
        draw.AddText(
            ImGui.GetFont(),
            ImGui.GetFontSize(),
            origin + new Vector2(Ui(20f) + iconSize.X, (size.Y - labelSize.Y) * 0.5f),
            ImGui.GetColorU32(Vector4.One),
            label);
        return clicked;
    }

    private void DrawUnifiedProviderStatuses()
    {
        var gap = Ui(10f);
        var width = (ImGui.GetContentRegionAvail().X - gap * 2f) / 3f;
        DrawUnifiedProviderStatus("YouTube", unifiedYouTubeSearching, unifiedYouTubeResults.Count, unifiedYouTubeError, Hex(0xFF0033), width);
        ImGui.SameLine(0f, gap);
        DrawUnifiedProviderStatus("Dailymotion", unifiedDailymotionSearching, unifiedDailymotionResults.Count, unifiedDailymotionError, Hex(0x168AFF), width);
        ImGui.SameLine(0f, gap);
        DrawUnifiedProviderStatus("Internet Archive", unifiedArchiveSearching, unifiedArchiveResults.Count, unifiedArchiveError, Hex(0xEAB308), width);
    }

    private void DrawUnifiedProviderStatus(string provider, bool searching, int count, string? error, Vector4 color, float width)
    {
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(8f));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var panel = ImRaii.Child($"##unifiedStatus_{provider}", new Vector2(width, Ui(82f)), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!panel) return;
        ImGui.TextColored(color, "●");
        ImGui.SameLine(0f, Ui(7f));
        ImGui.TextUnformatted(provider);
        ImGui.Dummy(UiVec(0f, 3f));
        var status = error is not null ? "Unavailable" : searching ? "Searching..." : "Complete";
        if (searching)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(AccentHover, FontAwesomeIcon.Spinner.ToIconString());
            }
            ImGui.SameLine(0f, Ui(6f));
        }
        ImGui.TextColored(error is not null ? Danger : searching ? AccentHover : Good, status);
        ImGui.SameLine(0f, Ui(10f));
        ImGui.TextColored(MutedText, searching ? $"{count} found" : $"{count} results");
    }

    private List<UnifiedSearchResult> BuildUnifiedSearchResults()
    {
        IEnumerable<UnifiedSearchResult> youtube = unifiedShowYouTube
            ? unifiedYouTubeResults
                .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => new UnifiedSearchResult(UnifiedSearchProvider.YouTube, group.First(), null))
            : [];
        IEnumerable<UnifiedSearchResult> dailymotion = unifiedShowDailymotion
            ? unifiedDailymotionResults
                .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => new UnifiedSearchResult(UnifiedSearchProvider.Dailymotion, group.First(), null))
            : [];
        IEnumerable<UnifiedSearchResult> archive = unifiedShowArchive
            ? unifiedArchiveResults
                .GroupBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase)
                .Select(group => new UnifiedSearchResult(UnifiedSearchProvider.InternetArchive, null, group.First()))
            : [];

        var providerLists = new[] { youtube.ToList(), dailymotion.ToList(), archive.ToList() };
        var merged = new List<UnifiedSearchResult>();
        for (var index = 0; providerLists.Any(list => index < list.Count); index++)
            foreach (var list in providerLists)
                if (index < list.Count) merged.Add(list[index]);

        merged = unifiedDurationFilter switch
        {
            VideoDurationFilter.UnderFiveMinutes => merged.Where(item => item.Duration is { } value && value < TimeSpan.FromMinutes(5)).ToList(),
            VideoDurationFilter.FiveToTwentyMinutes => merged.Where(item => item.Duration is { } value && value >= TimeSpan.FromMinutes(5) && value <= TimeSpan.FromMinutes(20)).ToList(),
            VideoDurationFilter.OverTwentyMinutes => merged.Where(item => item.Duration is { } value && value > TimeSpan.FromMinutes(20)).ToList(),
            _ => merged,
        };
        return unifiedSearchOrder switch
        {
            VideoSearchOrder.Shortest => merged.OrderBy(item => item.Duration ?? TimeSpan.MaxValue).ToList(),
            VideoSearchOrder.Longest => merged.OrderByDescending(item => item.Duration ?? TimeSpan.Zero).ToList(),
            _ => merged,
        };
    }

    private void DrawUnifiedResultsGrid(IReadOnlyList<UnifiedSearchResult> results)
    {
        using var child = ImRaii.Child("##unifiedResults", new Vector2(-1f, MathF.Max(Ui(180f), ImGui.GetContentRegionAvail().Y)), false);
        if (!child) return;

        // Calculate after entering the scrolling child so the vertical scrollbar is already
        // removed from the available width. Calculating outside made the final card overrun it.
        var available = ImGui.GetContentRegionAvail().X;
        var gap = Ui(10f);
        var columns = Math.Clamp((int)((available + gap) / (Ui(205f) + gap)), 3, 5);
        var width = (available - gap * (columns - 1)) / columns;
        for (var index = 0; index < results.Count; index++)
        {
            if (index % columns != 0) ImGui.SameLine(0f, gap);
            DrawUnifiedResultCard(results[index], width, index);
            if (index % columns == columns - 1) ImGui.Dummy(UiVec(0f, gap));
        }
    }

    private void DrawUnifiedResultCard(UnifiedSearchResult result, float width, int index)
    {
        ImGui.PushID($"unified_{result.Provider}_{result.ExactKey}_{index}");
        var height = Ui(250f);
        var imageHeight = Ui(128f);
        var origin = ImGui.GetCursorScreenPos();
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(9f));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using (var card = ImRaii.Child("##card", new Vector2(width, height), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (card)
            {
                var draw = ImGui.GetWindowDrawList();
                var imageOrigin = ImGui.GetCursorScreenPos();
                ImGui.InvisibleButton("##thumb", new Vector2(width, imageHeight));
                draw.AddRectFilled(imageOrigin, imageOrigin + new Vector2(width, imageHeight), ImGui.GetColorU32(FrameBg), Ui(8f));
                var thumbnail = thumbnails.Get(result.ThumbnailUrl);
                if (thumbnail is not null)
                    draw.AddImageRounded(thumbnail.Handle, imageOrigin, imageOrigin + new Vector2(width, imageHeight), Vector2.Zero, Vector2.One, uint.MaxValue, Ui(8f));
                else if (result.ThumbnailUrl is null)
                    DrawArchiveMissingThumbnail(imageOrigin, new Vector2(width, imageHeight), draw);

                var (providerName, providerColor) = result.Provider switch
                {
                    UnifiedSearchProvider.YouTube => ("YouTube", Hex(0xFF0033)),
                    UnifiedSearchProvider.Dailymotion => ("Dailymotion", Hex(0x168AFF)),
                    _ => ("Internet Archive", Hex(0xEAB308)),
                };
                var badgeSize = ImGui.CalcTextSize(providerName) + UiVec(18f, 10f);
                draw.AddRectFilled(imageOrigin + UiVec(7f, 7f), imageOrigin + UiVec(7f, 7f) + badgeSize,
                    ImGui.GetColorU32(new Vector4(0.02f, 0.025f, 0.05f, 0.92f)), Ui(5f));
                draw.AddText(imageOrigin + UiVec(16f, 12f), ImGui.GetColorU32(providerColor), providerName);
                if (result.Video?.ViewCount is { } views)
                {
                    var viewText = FormatViewCount(views).Replace(" views", string.Empty, StringComparison.Ordinal);
                    var eyeText = FontAwesomeIcon.Eye.ToIconString();
                    Vector2 eyeSize;
                    using (ImRaii.PushFont(UiBuilder.IconFont))
                        eyeSize = ImGui.CalcTextSize(eyeText);
                    var viewTextSize = ImGui.CalcTextSize(viewText);
                    var viewBadgeSize = new Vector2(
                        eyeSize.X + viewTextSize.X + Ui(19f),
                        MathF.Max(eyeSize.Y, viewTextSize.Y) + Ui(8f));
                    var viewBadgeOrigin = imageOrigin + new Vector2(Ui(6f), imageHeight - viewBadgeSize.Y - Ui(5f));
                    draw.AddRectFilled(
                        viewBadgeOrigin,
                        viewBadgeOrigin + viewBadgeSize,
                        ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.78f)),
                        Ui(4f));
                    draw.AddText(
                        UiBuilder.IconFont,
                        ImGui.GetFontSize(),
                        viewBadgeOrigin + new Vector2(Ui(6f), (viewBadgeSize.Y - eyeSize.Y) * 0.5f),
                        uint.MaxValue,
                        eyeText);
                    draw.AddText(
                        ImGui.GetFont(),
                        ImGui.GetFontSize(),
                        viewBadgeOrigin + new Vector2(Ui(12f) + eyeSize.X, (viewBadgeSize.Y - viewTextSize.Y) * 0.5f),
                        uint.MaxValue,
                        viewText);
                }
                if (result.Duration is { } duration)
                {
                    var durationText = FormatArchiveDuration(duration);
                    var durationSize = ImGui.CalcTextSize(durationText);
                    draw.AddRectFilled(imageOrigin + new Vector2(width - durationSize.X - Ui(13f), imageHeight - durationSize.Y - Ui(9f)),
                        imageOrigin + new Vector2(width - Ui(5f), imageHeight - Ui(4f)), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.78f)), Ui(3f));
                    draw.AddText(imageOrigin + new Vector2(width - durationSize.X - Ui(9f), imageHeight - durationSize.Y - Ui(7f)), uint.MaxValue, durationText);
                }
                if (ImGui.IsItemClicked()) PlayUnifiedResult(result);

                ImGui.SetCursorPos(UiVec(9f, 137f));
                ImGui.TextUnformatted(TruncateToWidth(result.Title, width - Ui(18f)));
                ImGui.SetCursorPos(UiVec(9f, 161f));
                ImGui.TextColored(MutedText, TruncateToWidth(GetUnifiedMetadata(result), width - Ui(18f)));
                ImGui.SetCursorPos(UiVec(8f, 204f));
                if (result.Archive is { Episodes.Count: > 1 } archive)
                {
                    if (GameLayoutButton($"View {archive.Episodes.Count} Episodes", FontAwesomeIcon.List, width - Ui(16f), true, height: 34f))
                        OpenInternetArchiveEpisodes(archive);
                }
                else
                {
                    var buttonGap = Ui(7f);
                    var buttonWidth = (width - Ui(16f) - buttonGap) / 2f;
                    if (GameLayoutButton("Play", FontAwesomeIcon.Play, buttonWidth, true, height: 34f)) PlayUnifiedResult(result);
                    ImGui.SameLine(0f, buttonGap);
                    if (GameLayoutButton("Queue", FontAwesomeIcon.Plus, buttonWidth, height: 34f)) QueueUnifiedResult(result);
                }
            }
        }
        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height));
        ImGui.PopID();
    }

    private static string GetUnifiedMetadata(UnifiedSearchResult result)
    {
        if (result.Archive is { } archive) return FormatArchiveDetails(archive);
        var creator = string.IsNullOrWhiteSpace(result.Creator)
            ? result.Provider.ToString()
            : result.Creator;
        return creator;
    }

    private void PlayUnifiedResult(UnifiedSearchResult result)
    {
        if (result.Archive is { } archive) PlayInternetArchiveItem(archive);
        else if (result.Video is { } videoResult)
            HandlePlayNow(new VideoQueueEntry(videoResult.Url, videoResult.Title, videoResult.ChannelName, videoResult.Duration, videoResult.ThumbnailUrl));
    }

    private void QueueUnifiedResult(UnifiedSearchResult result)
    {
        if (result.Archive is { } archive) QueueInternetArchiveItem(archive);
        else if (result.Video is { } videoResult)
            HandleAddToQueue(new VideoQueueEntry(videoResult.Url, videoResult.Title, videoResult.ChannelName, videoResult.Duration, videoResult.ThumbnailUrl));
    }

    private void StartUnifiedSearch()
    {
        var query = unifiedSearchQuery.Trim();
        if (query.Length == 0) return;
        EnsureInternetArchiveStarted();
        unifiedSearchCts?.Cancel();
        unifiedSearchCts?.Dispose();
        unifiedSearchCts = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref unifiedSearchGeneration);
        var token = unifiedSearchCts.Token;
        unifiedSearchCompletedQuery = query;
        unifiedYouTubeResults = SearchUnifiedYouTubeCache(query, 50);
        unifiedDailymotionResults = Array.Empty<VideoSearchEntry>();
        unifiedArchiveResults = SearchInternetArchiveCache(query, 40);
        unifiedYouTubeError = unifiedDailymotionError = unifiedArchiveError = null;
        unifiedYouTubeSearching = unifiedDailymotionSearching = unifiedArchiveSearching = true;
        _ = SearchUnifiedYouTubeAsync(query, generation, token);
        _ = SearchUnifiedDailymotionAsync(query, generation, token);
        _ = SearchUnifiedArchiveAsync(query, generation, token);
    }

    private async Task SearchUnifiedYouTubeAsync(string query, int generation, CancellationToken token)
    {
        try
        {
            var cached = unifiedYouTubeResults;
            var results = await searchResolver.SearchAsync(query, 50, token).ConfigureAwait(false);
            if (generation == unifiedSearchGeneration && !token.IsCancellationRequested)
            {
                unifiedYouTubeResults = cached.Concat(results)
                    .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Take(75)
                    .ToArray();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            AepLog.Warning($"[UnifiedSearch] YouTube failed: {exception.Message}");
            if (generation == unifiedSearchGeneration) unifiedYouTubeError = "Search failed";
        }
        finally { if (generation == unifiedSearchGeneration) unifiedYouTubeSearching = false; }
    }

    private IReadOnlyList<VideoSearchEntry> SearchUnifiedYouTubeCache(string query, int maximum)
    {
        RestorePersistedBrowseVideoCache();
        var terms = query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return Array.Empty<VideoSearchEntry>();

        try
        {
            var cached = Enumerable.Empty<VideoSearchEntry>();
            if (browseVideoResults is { } topics) cached = cached.Concat(topics.Values.SelectMany(items => items));
            if (subscriptionVideoResults is { } subscriptions) cached = cached.Concat(subscriptions.Values.SelectMany(items => items));
            if (homeYouTubeResults is { } home) cached = cached.Concat(home);
            if (ffxivYouTubeResults is { } ffxiv) cached = cached.Concat(ffxiv);
            if (favouriteVideoResults is { } favourites) cached = cached.Concat(favourites);

            return cached
                .Where(item => terms.All(term =>
                    item.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    item.ChannelName.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(maximum)
                .ToArray();
        }
        catch (InvalidOperationException)
        {
            // A discovery feed may publish a replacement cache while this snapshot is being made.
            // Live provider results still complete the search, so retrying on the UI thread is unnecessary.
            return Array.Empty<VideoSearchEntry>();
        }
    }

    private async Task SearchUnifiedDailymotionAsync(string query, int generation, CancellationToken token)
    {
        try
        {
            using var http = Net.PluginHttpClients.CreateMetadataClient();
            var url = "https://api.dailymotion.com/videos" +
                      $"?search={Uri.EscapeDataString(query)}&limit=50&fields=id,title,owner.screenname,thumbnail_url,duration,views_total";
            var json = await http.GetStringAsync(url, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var results = new List<VideoSearchEntry>();
            if (document.RootElement.TryGetProperty("list", out var list))
            {
                foreach (var video in list.EnumerateArray())
                {
                    var id = video.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                    var title = video.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
                    var creator = video.TryGetProperty("owner.screenname", out var creatorValue) ? creatorValue.GetString() : null;
                    var thumbnail = video.TryGetProperty("thumbnail_url", out var thumbnailValue) ? thumbnailValue.GetString() : null;
                    TimeSpan? duration = video.TryGetProperty("duration", out var durationValue) && durationValue.TryGetDouble(out var seconds)
                        ? TimeSpan.FromSeconds(seconds) : null;
                    long? viewCount = video.TryGetProperty("views_total", out var viewsValue) && viewsValue.TryGetInt64(out var views)
                        ? views : null;
                    results.Add(new VideoSearchEntry(
                        title,
                        $"https://www.dailymotion.com/video/{id}",
                        creator ?? "Dailymotion",
                        duration,
                        thumbnail,
                        viewCount));
                }
            }
            if (generation == unifiedSearchGeneration && !token.IsCancellationRequested) unifiedDailymotionResults = results;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            AepLog.Warning($"[UnifiedSearch] Dailymotion failed: {exception.Message}");
            if (generation == unifiedSearchGeneration) unifiedDailymotionError = "Search failed";
        }
        finally { if (generation == unifiedSearchGeneration) unifiedDailymotionSearching = false; }
    }

    private async Task SearchUnifiedArchiveAsync(string query, int generation, CancellationToken token)
    {
        try
        {
            var cached = unifiedArchiveResults;
            var results = await internetArchive.SearchAsync(
                InternetArchiveClient.BuildTextSearchQuery(query),
                "downloads desc",
                24,
                token,
                item => PublishUnifiedArchiveResult(item, generation, token),
                priorityCandidateCount: 12).ConfigureAwait(false);
            if (generation != unifiedSearchGeneration || token.IsCancellationRequested) return;
            foreach (var item in results) archiveSearchCache[item.Identifier] = item;
            unifiedArchiveResults = cached.Concat(results)
                .GroupBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToArray();
            SaveInternetArchiveCache();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            AepLog.Warning($"[UnifiedSearch] Internet Archive failed: {exception.Message}");
            if (generation == unifiedSearchGeneration) unifiedArchiveError = "Search failed";
        }
        finally { if (generation == unifiedSearchGeneration) unifiedArchiveSearching = false; }
    }

    private void PublishUnifiedArchiveResult(InternetArchiveItem item, int generation, CancellationToken token)
    {
        if (token.IsCancellationRequested || generation != unifiedSearchGeneration) return;
        lock (unifiedSearchSync)
        {
            if (token.IsCancellationRequested || generation != unifiedSearchGeneration) return;
            archiveSearchCache[item.Identifier] = item;
            unifiedArchiveResults = unifiedArchiveResults.Append(item)
                .GroupBy(result => result.Identifier, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToArray();
        }
    }
}
