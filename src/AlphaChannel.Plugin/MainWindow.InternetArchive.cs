using System.Collections.Concurrent;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Self-contained Internet Archive page. The only integration points outside this file are the
// HomePage enum/switch, the sidebar item, and lifetime cleanup in MainWindow.cs.
internal sealed partial class MainWindow
{
    private sealed record ArchiveCategory(string Key, string Label, string Query, string Sort);

    private static readonly ArchiveCategory[] ArchiveCategories =
    [
        new("films", "Feature Films", "collection:feature_films", "downloads desc"),
        new("animation", "Animation", "(subject:animation OR subject:cartoon)", "downloads desc"),
        new("documentaries", "Documentaries", "subject:documentary", "downloads desc"),
        new("television", "Vintage Television", "(subject:television OR collection:classic_tv)", "downloads desc"),
        new("history", "Historical Footage", "collection:prelinger", "downloads desc"),
    ];

    private static readonly TimeSpan InternetArchiveRefreshInterval = TimeSpan.FromHours(3);

    private readonly InternetArchiveClient internetArchive = new();
    private readonly InternetArchiveCacheStorage internetArchiveCacheStorage = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<InternetArchiveItem>> archiveRows =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InternetArchiveItem> archiveSearchCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> archiveCategoryLastPages =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> archiveCategoriesLoading =
        new(StringComparer.Ordinal);
    private readonly object internetArchiveCacheSync = new();
    private CancellationTokenSource? internetArchiveCts;
    private CancellationTokenSource? internetArchiveSearchCts;
    private CancellationTokenSource? internetArchiveLoadMoreCts;
    private bool internetArchiveStarted;
    private volatile bool internetArchiveLoading;
    private volatile bool internetArchiveSearchLoading;
    private volatile bool internetArchiveLoadMoreLoading;
    private string internetArchiveSearchText = string.Empty;
    private string? internetArchiveViewTitle;
    private string? internetArchiveViewCategoryKey;
    private IReadOnlyList<InternetArchiveItem> internetArchiveViewResults = Array.Empty<InternetArchiveItem>();
    private string? internetArchiveError;
    private string? internetArchiveLoadMoreMessage;
    private int internetArchiveSearchGeneration;
    private InternetArchiveItem? internetArchiveEpisodeItem;
    private DateTime internetArchiveLastRefreshUtc = DateTime.MinValue;

    private void DrawInternetArchivePage()
    {
        EnsureInternetArchiveStarted();

        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(9f, 9f));
        DrawInternetArchiveSearch();
        ImGui.Dummy(UiVec(0f, 2f));
        DrawInternetArchiveCategories();
        ImGui.Dummy(UiVec(0f, 8f));

        if (!string.IsNullOrWhiteSpace(internetArchiveError))
        {
            using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(Danger.X, Danger.Y, Danger.Z, 0.08f))
                .Push(ImGuiCol.Border, new Vector4(Danger.X, Danger.Y, Danger.Z, 0.52f)))
            using (var errorPanel = ImRaii.Child("##archiveError", UiVec(ImGui.GetContentRegionAvail().X, 58f), true,
                       ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (errorPanel)
                {
                    ImGui.TextColored(Danger, internetArchiveError);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Try again"))
                    {
                        internetArchiveError = null;
                        StartInternetArchiveHomeLoad(force: true);
                    }
                }
            }
            ImGui.Dummy(UiVec(0f, 4f));
        }

        if (internetArchiveViewTitle is not null)
        {
            DrawInternetArchiveResults();
            return;
        }

        DrawInternetArchiveShelf("Classic Animation", "animation");
        DrawInternetArchiveShelf("Feature Films", "films");
        DrawInternetArchiveShelf("Documentaries", "documentaries");
        DrawInternetArchiveShelf("Vintage Television", "television");
        DrawInternetArchiveShelf("Historical Footage", "history");
    }

    private void DrawInternetArchiveSearch()
    {
        var searchButtonWidth = Ui(112f);
        var refreshButtonWidth = Ui(112f);
        ImGui.SetNextItemWidth(MathF.Max(
            Ui(180f),
            ImGui.GetContentRegionAvail().X - searchButtonWidth - refreshButtonWidth - Ui(18f)));
        var submitted = ImGui.InputTextWithHint(
            "##internetArchiveSearch",
            "Search films, programmes, creators, or subjects...",
            ref internetArchiveSearchText,
            300,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine(0f, Ui(9f));
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(internetArchiveSearchText) || internetArchiveSearchLoading))
        {
            submitted |= GameLayoutButton(
                internetArchiveSearchLoading ? "Searching" : "Search",
                FontAwesomeIcon.Search,
                searchButtonWidth,
                true);
        }
        ImGui.SameLine(0f, Ui(9f));
        using (ImRaii.Disabled(internetArchiveLoading))
        {
            if (GameLayoutButton(
                    internetArchiveLoading ? "Refreshing" : "Refresh",
                    FontAwesomeIcon.Sync,
                    refreshButtonWidth))
            {
                internetArchiveError = null;
                StartInternetArchiveHomeLoad(force: true);
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(internetArchiveLastRefreshUtc == DateTime.MinValue
                ? "Fetch the latest Internet Archive results."
                : $"Last refreshed {FormatArchiveCacheAge(DateTime.UtcNow - internetArchiveLastRefreshUtc)} ago. Automatic refreshes run at most every three hours.");
        }

        if (submitted && !string.IsNullOrWhiteSpace(internetArchiveSearchText))
        {
            StartInternetArchiveSearch(
                $"Search results for “{internetArchiveSearchText.Trim()}”",
                InternetArchiveClient.BuildTextSearchQuery(internetArchiveSearchText),
                "downloads desc",
                internetArchiveSearchText);
        }
    }

    private void DrawInternetArchiveCategories()
    {
        DrawInternetArchiveCategoryChip("All", internetArchiveViewTitle is null, () =>
        {
            CancelInternetArchiveSearch();
            CancelInternetArchiveLoadMore();
            internetArchiveViewTitle = null;
            internetArchiveViewCategoryKey = null;
            internetArchiveViewResults = Array.Empty<InternetArchiveItem>();
        });

        foreach (var category in ArchiveCategories)
        {
            ImGui.SameLine(0f, Ui(7f));
            DrawInternetArchiveCategoryChip(category.Label, internetArchiveViewCategoryKey == category.Key, () =>
                ShowInternetArchiveCategory(category));
        }
    }

    private void DrawInternetArchiveCategoryChip(string label, bool selected, Action onClick)
    {
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(14f));
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(13f, 6f));
        using var colors = ImRaii.PushColor(ImGuiCol.Button, selected ? Accent : CardBg)
            .Push(ImGuiCol.ButtonHovered, selected ? AccentHover : CardBgHover)
            .Push(ImGuiCol.ButtonActive, AccentActive)
            .Push(ImGuiCol.Border, selected ? Accent : BorderSubtle);
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1f));
        if (ImGui.Button(label + "##archiveCategory"))
        {
            onClick();
        }
    }

    private void DrawInternetArchiveResults()
    {
        if (GameLayoutButton("Back to Archive", FontAwesomeIcon.ArrowLeft, Ui(160f)))
        {
            CancelInternetArchiveSearch();
            CancelInternetArchiveLoadMore();
            internetArchiveViewTitle = null;
            internetArchiveViewCategoryKey = null;
            internetArchiveViewResults = Array.Empty<InternetArchiveItem>();
            return;
        }

        ImGui.Dummy(UiVec(0f, 7f));
        SetUiFontScale(1.16f);
        ImGui.TextUnformatted(internetArchiveViewTitle ?? "Results");
        SetUiFontScale(1f);
        var resultsAreLoading = internetArchiveViewCategoryKey is { } loadingCategoryKey
            ? (archiveCategoriesLoading.TryGetValue(loadingCategoryKey, out var categoryLoads) && categoryLoads > 0) ||
              internetArchiveLoadMoreLoading
            : internetArchiveSearchLoading;
        if (resultsAreLoading)
        {
            ImGui.SameLine(0f, Ui(8f));
            DrawBrowseLoadingSpinner(Ui(7f));
        }
        ImGui.Dummy(UiVec(0f, 7f));

        var results = internetArchiveViewCategoryKey is { } categoryKey &&
                      archiveRows.TryGetValue(categoryKey, out var categoryResults)
            ? categoryResults
            : internetArchiveViewResults;
        if (internetArchiveSearchLoading)
        {
            ImGui.TextColored(
                MutedText,
                results.Count > 0
                    ? "Showing cached matches while additional live results are checked..."
                    : "Searching the Internet Archive and checking playable files...");
            if (results.Count == 0)
            {
                DrawInternetArchiveLoadingGrid();
                return;
            }
            ImGui.Dummy(UiVec(0f, 4f));
        }

        if (results.Count == 0)
        {
            GameLayoutPanel("##archiveNoResults", ImGui.GetContentRegionAvail().X, Ui(118f), () =>
            {
                GameLayoutHeading("No playable videos found", FontAwesomeIcon.Search);
                ImGui.TextColored(MutedText,
                    "Try a broader search. Audio, books, software, and items without a playable video file are excluded.");
            });
            return;
        }

        DrawInternetArchiveGrid(results);
        if (internetArchiveViewCategoryKey is { } loadMoreCategoryKey)
        {
            DrawInternetArchiveLoadMore(loadMoreCategoryKey);
        }
    }

    private void DrawInternetArchiveShelf(string title, string key)
    {
        ImGui.PushID("archiveShelf_" + key);
        SetUiFontScale(1.08f);
        ImGui.TextUnformatted(title);
        SetUiFontScale(1f);
        var category = ArchiveCategories.First(category => category.Key == key);
        const string seeAll = "See all  >";
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(seeAll).X);
        if (ImGui.Selectable(seeAll, false, ImGuiSelectableFlags.None, ImGui.CalcTextSize(seeAll)))
        {
            ShowInternetArchiveCategory(category);
        }
        ImGui.Dummy(UiVec(0f, 3f));

        if (archiveRows.TryGetValue(key, out var items) && items.Count > 0)
        {
            DrawInternetArchiveRow(items);
        }
        else
        {
            DrawInternetArchiveLoadingRow();
        }

        ImGui.Dummy(UiVec(0f, 15f));
        ImGui.PopID();
    }

    private void DrawInternetArchiveRow(IReadOnlyList<InternetArchiveItem> items)
    {
        var gap = Ui(10f);
        var count = Math.Clamp((int)((ImGui.GetContentRegionAvail().X + gap) / (Ui(182f) + gap)), 3, 6);
        var cardWidth = (ImGui.GetContentRegionAvail().X - gap * (count - 1)) / count;
        for (var index = 0; index < Math.Min(count, items.Count); index++)
        {
            if (index > 0) ImGui.SameLine(0f, gap);
            DrawInternetArchiveCard(items[index], cardWidth);
        }
    }

    private void DrawInternetArchiveGrid(IReadOnlyList<InternetArchiveItem> items)
    {
        var gap = Ui(10f);
        var columns = Math.Clamp((int)((ImGui.GetContentRegionAvail().X + gap) / (Ui(205f) + gap)), 3, 5);
        var width = (ImGui.GetContentRegionAvail().X - gap * (columns - 1)) / columns;
        var gridLeft = ImGui.GetCursorScreenPos().X;
        for (var index = 0; index < items.Count; index++)
        {
            if (index % columns != 0)
            {
                ImGui.SameLine(0f, gap);
            }
            else if (index > 0)
            {
                var nextRowY = ImGui.GetCursorScreenPos().Y + gap;
                ImGui.SetCursorScreenPos(new Vector2(gridLeft, nextRowY));
            }
            DrawInternetArchiveCard(items[index], width);
        }
        var gridBottom = ImGui.GetCursorScreenPos().Y;
        ImGui.SetCursorScreenPos(new Vector2(gridLeft, gridBottom));
    }

    private void DrawInternetArchiveLoadMore(string categoryKey)
    {
        ImGui.Dummy(UiVec(0f, 12f));
        using (ImRaii.Disabled(internetArchiveLoadMoreLoading || internetArchiveLoading))
        {
            if (GameLayoutButton(
                    internetArchiveLoadMoreLoading ? "Loading more..." : "Load More",
                    internetArchiveLoadMoreLoading ? FontAwesomeIcon.Spinner : FontAwesomeIcon.Plus,
                    ImGui.GetContentRegionAvail().X,
                    true))
            {
                StartInternetArchiveLoadMore(categoryKey);
            }
        }

        if (!string.IsNullOrWhiteSpace(internetArchiveLoadMoreMessage))
        {
            ImGui.Dummy(UiVec(0f, 4f));
            ImGui.TextColored(Danger, internetArchiveLoadMoreMessage);
        }
    }

    private void DrawInternetArchiveCard(InternetArchiveItem item, float width)
    {
        ImGui.PushID(item.Identifier);
        var height = Ui(223f);
        var origin = ImGui.GetCursorScreenPos();
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(9f));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using (var card = ImRaii.Child("##archiveCard", new Vector2(width, height), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (card)
            {
                var draw = ImGui.GetWindowDrawList();
                var imageHeight = Ui(109f);
                var imageOrigin = ImGui.GetCursorScreenPos();
                ImGui.InvisibleButton("##archiveThumb", new Vector2(width, imageHeight));
                var thumbnail = thumbnails.Get(item.ThumbnailUrl);
                if (thumbnail is not null)
                {
                    draw.AddImageRounded(thumbnail.Handle, imageOrigin, imageOrigin + new Vector2(width, imageHeight),
                        Vector2.Zero, Vector2.One, uint.MaxValue, Ui(8f));
                }
                else
                {
                    draw.AddRectFilled(imageOrigin, imageOrigin + new Vector2(width, imageHeight),
                        ImGui.GetColorU32(new Vector4(0.07f, 0.08f, 0.13f, 1f)), Ui(8f));
                    if (item.ThumbnailUrl is null)
                    {
                        DrawArchiveMissingThumbnail(imageOrigin, new Vector2(width, imageHeight), draw);
                    }
                }

                var playCenter = imageOrigin + new Vector2(width, imageHeight) * 0.5f;
                draw.AddCircleFilled(playCenter, Ui(19f), ImGui.GetColorU32(new Vector4(0.04f, 0.04f, 0.08f, 0.78f)), 30);
                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    var glyph = FontAwesomeIcon.Play.ToIconString();
                    var glyphSize = ImGui.CalcTextSize(glyph);
                    draw.AddText(playCenter - glyphSize * 0.5f + UiVec(1.5f, 0f), ImGui.GetColorU32(Vector4.One), glyph);
                }
                if (ImGui.IsItemClicked()) PlayInternetArchiveItem(item);

                ImGui.SetCursorPos(UiVec(9f, 117f));
                ImGui.TextUnformatted(TruncateToWidth(item.Title, width - Ui(18f)));
                ImGui.SetCursorPos(UiVec(9f, 140f));
                ImGui.TextColored(MutedText, TruncateToWidth(FormatArchiveDetails(item), width - Ui(18f)));
                ImGui.SetCursorPos(UiVec(8f, 178f));
                var buttonGap = Ui(7f);
                if (item.Episodes.Count > 1)
                {
                    if (GameLayoutButton(
                            $"View {item.Episodes.Count} Episodes",
                            FontAwesomeIcon.List,
                            width - Ui(16f),
                            true,
                            height: 33f))
                    {
                        OpenInternetArchiveEpisodes(item);
                    }
                }
                else
                {
                    var buttonWidth = (width - Ui(16f) - buttonGap) / 2f;
                    if (GameLayoutButton("Play", FontAwesomeIcon.Play, buttonWidth, true, height: 33f))
                        PlayInternetArchiveItem(item);
                    ImGui.SameLine(0f, buttonGap);
                    if (GameLayoutButton("Queue", FontAwesomeIcon.Plus, buttonWidth, height: 33f))
                        QueueInternetArchiveItem(item);
                }
            }
        }
        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height));
        ImGui.PopID();
    }

    private static void DrawArchiveMissingThumbnail(Vector2 origin, Vector2 size, ImDrawListPtr draw)
    {
        const string text = "NO THUMBNAIL AVAILABLE";
        var textSize = ImGui.CalcTextSize(text);
        draw.AddText(origin + (size - textSize) * 0.5f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.48f)), text);
    }

    private void DrawInternetArchiveLoadingRow()
    {
        var gap = Ui(10f);
        var count = Math.Clamp((int)((ImGui.GetContentRegionAvail().X + gap) / (Ui(182f) + gap)), 3, 6);
        var width = (ImGui.GetContentRegionAvail().X - gap * (count - 1)) / count;
        for (var index = 0; index < count; index++)
        {
            if (index > 0) ImGui.SameLine(0f, gap);
            DrawInternetArchiveLoadingCard(width, index);
        }
    }

    private void DrawInternetArchiveLoadingGrid()
    {
        ImGui.Dummy(UiVec(0f, 8f));
        DrawInternetArchiveLoadingRow();
    }

    private void DrawInternetArchiveLoadingCard(float width, int index)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, Ui(223f));
        ImGui.InvisibleButton("##archiveLoading" + index, size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBg), Ui(9f));
        draw.AddRectFilled(origin, origin + new Vector2(width, Ui(109f)), ImGui.GetColorU32(FrameBg), Ui(9f));
        draw.AddRectFilled(origin + UiVec(9f, 124f), origin + new Vector2(width * 0.78f, Ui(136f)),
            ImGui.GetColorU32(BorderSubtle), Ui(3f));
        draw.AddRectFilled(origin + UiVec(9f, 149f), origin + new Vector2(width * 0.58f, Ui(159f)),
            ImGui.GetColorU32(BorderSubtle), Ui(3f));
    }

    private void PlayInternetArchiveItem(InternetArchiveItem item)
    {
        if (item.Episodes.Count > 1)
        {
            OpenInternetArchiveEpisodes(item);
            return;
        }

        HandlePlayNow(CreateInternetArchiveQueueEntry(item, item.Episodes[0]));
    }

    private void QueueInternetArchiveItem(InternetArchiveItem item)
    {
        if (item.Episodes.Count > 1)
        {
            OpenInternetArchiveEpisodes(item);
            return;
        }

        HandleAddToQueue(CreateInternetArchiveQueueEntry(item, item.Episodes[0]));
    }

    private static VideoQueueEntry CreateInternetArchiveQueueEntry(
        InternetArchiveItem item,
        InternetArchiveEpisode episode) =>
        new(
            episode.VideoUrl,
            item.Episodes.Count > 1 ? $"{item.Title} — {episode.Title}" : item.Title,
            string.IsNullOrWhiteSpace(item.Creator) ? "Internet Archive" : item.Creator,
            episode.Duration,
            item.ThumbnailUrl);

    private void OpenInternetArchiveEpisodes(InternetArchiveItem item)
    {
        internetArchiveEpisodeItem = item;
        activeGameDialog = "Internet Archive episodes";
    }

    private void DrawInternetArchiveEpisodeDialog()
    {
        if (activeGameDialog != "Internet Archive episodes") return;
        var item = internetArchiveEpisodeItem;
        if (item is null || item.Episodes.Count == 0)
        {
            activeGameDialog = null;
            return;
        }

        if (!BeginGameDialog(
                "Internet Archive episodes",
                "Choose an Episode",
                FontAwesomeIcon.List,
                780f,
                680f))
        {
            return;
        }

        SetUiFontScale(1.12f);
        ImGui.TextUnformatted(item.Title);
        SetUiFontScale(1f);
        ImGui.TextColored(MutedText, $"{item.Episodes.Count} playable episodes found in this Archive item.");
        ImGui.Dummy(UiVec(0f, 6f));

        var bulkGap = Ui(8f);
        var bulkWidth = (ImGui.GetContentRegionAvail().X - bulkGap) / 2f;
        var bulkDisabled = ShouldUseViewerMediaActions || video.IsPlayingLocalVideo;
        using (ImRaii.Disabled(bulkDisabled))
        {
            if (GameLayoutButton("Play All", FontAwesomeIcon.Play, bulkWidth, true))
            {
                PlayAllInternetArchiveEpisodes(item);
                activeGameDialog = null;
            }
            ImGui.SameLine(0f, bulkGap);
            if (GameLayoutButton("Queue All", FontAwesomeIcon.Plus, bulkWidth))
            {
                foreach (var episode in item.Episodes)
                {
                    HandleAddToQueue(CreateInternetArchiveQueueEntry(item, episode));
                }
            }
        }
        if (bulkDisabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(video.IsPlayingLocalVideo
                ? "Stop the local video before adding Archive episodes."
                : "Bulk episode actions are available to the Watch Party host. You can still select one episode below.");
        }

        ImGui.Dummy(UiVec(0f, 5f));
        using (var episodeList = ImRaii.Child(
                   "##archiveEpisodeList",
                   new Vector2(
                       ImGui.GetContentRegionAvail().X,
                       MathF.Max(Ui(180f), ImGui.GetContentRegionAvail().Y - Ui(52f))),
                   false))
        {
            if (episodeList)
            {
                for (var index = 0; index < item.Episodes.Count; index++)
                {
                    var episode = item.Episodes[index];
                    ImGui.PushID(index);
                    using var rowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12f, 10f));
                    using var rowRounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(8f));
                    using var rowColors = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
                        .Push(ImGuiCol.Border, BorderSubtle);
                    using (var row = ImRaii.Child(
                               "##episodeRow",
                               new Vector2(ImGui.GetContentRegionAvail().X, Ui(64f)),
                               true,
                               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                    {
                        if (row)
                        {
                            var actionWidth = Ui(90f);
                            var actionGap = Ui(7f);
                            var detailWidth = MathF.Max(
                                Ui(120f),
                                ImGui.GetContentRegionAvail().X - actionWidth * 2f - actionGap - Ui(18f));
                            ImGui.BeginGroup();
                            ImGui.TextUnformatted(TruncateToWidth(episode.Title, detailWidth));
                            ImGui.TextColored(
                                MutedText,
                                episode.Duration is { } duration
                                    ? FormatArchiveDuration(duration)
                                    : $"Episode {index + 1}");
                            ImGui.EndGroup();
                            ImGui.SameLine();
                            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - actionWidth * 2f - actionGap);
                            if (GameLayoutButton("Play", FontAwesomeIcon.Play, actionWidth, true, height: 34f))
                            {
                                HandlePlayNow(CreateInternetArchiveQueueEntry(item, episode));
                                activeGameDialog = null;
                            }
                            ImGui.SameLine(0f, actionGap);
                            if (GameLayoutButton("Queue", FontAwesomeIcon.Plus, actionWidth, height: 34f))
                            {
                                HandleAddToQueue(CreateInternetArchiveQueueEntry(item, episode));
                            }
                        }
                    }
                    ImGui.PopID();
                    ImGui.Dummy(UiVec(0f, 6f));
                }
            }
        }

        ImGui.Dummy(UiVec(0f, 4f));
        if (GameLayoutButton("Close", FontAwesomeIcon.Times, ImGui.GetContentRegionAvail().X))
        {
            activeGameDialog = null;
            internetArchiveEpisodeItem = null;
        }
        EndGameDialog();
    }

    private void PlayAllInternetArchiveEpisodes(InternetArchiveItem item)
    {
        if (item.Episodes.Count == 0) return;
        HandlePlayNow(CreateInternetArchiveQueueEntry(item, item.Episodes[0]));
        foreach (var episode in item.Episodes.Skip(1))
        {
            HandleAddToQueue(CreateInternetArchiveQueueEntry(item, episode));
        }
    }

    private static string FormatArchiveDuration(TimeSpan duration) =>
        duration.TotalHours >= 1d
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";

    private static string FormatArchiveDetails(InternetArchiveItem item)
    {
        var creator = string.IsNullOrWhiteSpace(item.Creator) ? "Internet Archive" : item.Creator;
        var year = item.Year is null ? string.Empty : $"  •  {item.Year}";
        var views = item.Downloads <= 0 ? string.Empty : $"  •  {FormatArchiveDownloads(item.Downloads)} views";
        var episodes = item.Episodes.Count > 1 ? $"  •  {item.Episodes.Count} episodes" : string.Empty;
        return creator + year + episodes + views;
    }

    private static string FormatArchiveDownloads(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
        >= 1_000 => $"{value / 1_000d:0.#}K",
        _ => value.ToString(),
    };

    private static string NormalizeArchiveText(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private void EnsureInternetArchiveStarted()
    {
        if (internetArchiveStarted) return;
        internetArchiveStarted = true;
        var cached = internetArchiveCacheStorage.Load();
        internetArchiveLastRefreshUtc = cached.LastRefreshUtc;
        foreach (var (key, items) in cached.Categories)
        {
            if (items.Count > 0 && ArchiveCategories.Any(category => category.Key == key))
                archiveRows[key] = items;
        }
        foreach (var category in ArchiveCategories)
        {
            if (cached.CategoryLastPages.TryGetValue(category.Key, out var lastPage))
                archiveCategoryLastPages[category.Key] = Math.Max(1, lastPage);
            else if (archiveRows.ContainsKey(category.Key))
                archiveCategoryLastPages[category.Key] = 1;
        }
        foreach (var item in cached.SearchItems)
        {
            archiveSearchCache[item.Identifier] = item;
        }
        internetArchive.SeedResolvedItems(
            cached.Categories.Values.SelectMany(items => items).Concat(cached.SearchItems));
        internetArchive.SeedNoVideoItems(cached.NoVideoItems);
        StartInternetArchiveHomeLoad(force: false);
    }

    private void StartInternetArchiveHomeLoad(bool force)
    {
        if (internetArchiveLoading)
        {
            if (!force) return;
            internetArchiveCts?.Cancel();
        }
        if (!force && InternetArchiveCacheIsFreshAndComplete())
        {
            internetArchiveLoading = false;
            return;
        }
        internetArchiveCts?.Cancel();
        internetArchiveCts?.Dispose();
        internetArchiveCts = new CancellationTokenSource();
        internetArchiveLoading = true;
        _ = LoadInternetArchiveHomeAsync(internetArchiveCts.Token);
    }

    private async Task LoadInternetArchiveHomeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tasks = ArchiveCategories.Select(category => LoadInternetArchiveRowAsync(category, cancellationToken));
            var refreshed = await Task.WhenAll(tasks).ConfigureAwait(false);
            if (refreshed.Any(success => success))
            {
                internetArchiveLastRefreshUtc = DateTime.UtcNow;
                SaveInternetArchiveCache();
                internetArchiveError = null;
            }
            else if (!archiveRows.Values.Any(items => items.Count > 0))
            {
                internetArchiveError = "The Internet Archive could not be reached. Check your connection and try again.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[InternetArchive] Page load failed: {exception.Message}");
            internetArchiveError = "The Internet Archive could not be reached. Check your connection and try again.";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) internetArchiveLoading = false;
        }
    }

    private async Task<bool> LoadInternetArchiveRowAsync(
        ArchiveCategory category,
        CancellationToken cancellationToken)
    {
        archiveCategoriesLoading.AddOrUpdate(category.Key, 1, (_, count) => count + 1);
        try
        {
            var items = await internetArchive.SearchAsync(
                category.Query,
                category.Sort,
                12,
                cancellationToken,
                item => PublishInternetArchiveCategoryItem(category.Key, item, cancellationToken),
                priorityCandidateCount: 6)
                .ConfigureAwait(false);
            archiveCategoryLastPages.AddOrUpdate(category.Key, 1, (_, current) => Math.Max(current, 1));
            if (items.Count == 0)
            {
                SaveInternetArchiveCache();
                return false;
            }
            archiveRows.TryGetValue(category.Key, out var cached);
            archiveRows[category.Key] = MergeFreshArchiveItems(items, cached ?? Array.Empty<InternetArchiveItem>(), 200);
            SaveInternetArchiveCache();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[InternetArchive] {category.Label} load failed: {exception.Message}");
            return false;
        }
        finally
        {
            archiveCategoriesLoading.AddOrUpdate(category.Key, 0, (_, count) => Math.Max(0, count - 1));
        }
    }

    private void PublishInternetArchiveCategoryItem(
        string categoryKey,
        InternetArchiveItem item,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        archiveRows.AddOrUpdate(
            categoryKey,
            _ => new[] { item },
            (_, cached) => MergeFreshArchiveItems(new[] { item }, cached, 200));
    }

    private void StartInternetArchiveLoadMore(string categoryKey)
    {
        if (internetArchiveLoadMoreLoading) return;
        var category = ArchiveCategories.FirstOrDefault(candidate => candidate.Key == categoryKey);
        if (category is null) return;

        internetArchiveLoadMoreCts?.Cancel();
        internetArchiveLoadMoreCts?.Dispose();
        internetArchiveLoadMoreCts = new CancellationTokenSource();
        internetArchiveLoadMoreMessage = null;
        internetArchiveLoadMoreLoading = true;
        _ = LoadMoreInternetArchiveCategoryAsync(category, internetArchiveLoadMoreCts.Token);
    }

    private async Task LoadMoreInternetArchiveCategoryAsync(
        ArchiveCategory category,
        CancellationToken cancellationToken)
    {
        try
        {
            archiveRows.TryGetValue(category.Key, out var existingItems);
            var knownIdentifiers = new ConcurrentDictionary<string, byte>(
                (existingItems ?? Array.Empty<InternetArchiveItem>())
                .Select(item => new KeyValuePair<string, byte>(item.Identifier, 0)),
                StringComparer.OrdinalIgnoreCase);
            var lastPage = archiveCategoryLastPages.GetOrAdd(
                category.Key,
                existingItems is { Count: > 0 } ? 1 : 0);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = lastPage + 1;
                var addedCount = 0;
                void PublishNewItem(InternetArchiveItem item)
                {
                    if (cancellationToken.IsCancellationRequested ||
                        !knownIdentifiers.TryAdd(item.Identifier, 0))
                        return;
                    AppendInternetArchiveCategoryItem(category.Key, item);
                    Interlocked.Increment(ref addedCount);
                }

                var results = await internetArchive.SearchAsync(
                    category.Query,
                    category.Sort,
                    12,
                    cancellationToken,
                    PublishNewItem,
                    priorityCandidateCount: 6,
                    page: page).ConfigureAwait(false);

                foreach (var item in results) PublishNewItem(item);
                lastPage = page;
                archiveCategoryLastPages[category.Key] = lastPage;
                SaveInternetArchiveCache();
                if (addedCount > 0)
                {
                    internetArchiveLoadMoreMessage = null;
                    return;
                }
            }

            internetArchiveLoadMoreMessage = "Unable to load more videos";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[InternetArchive] Could not load more {category.Label}: {exception.Message}");
            internetArchiveLoadMoreMessage = "Unable to load more videos";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) internetArchiveLoadMoreLoading = false;
        }
    }

    private void AppendInternetArchiveCategoryItem(string categoryKey, InternetArchiveItem item)
    {
        archiveRows.AddOrUpdate(
            categoryKey,
            _ => new[] { item },
            (_, cached) => cached.Any(existing =>
                existing.Identifier.Equals(item.Identifier, StringComparison.OrdinalIgnoreCase))
                ? cached
                : cached.Append(item).Take(200).ToArray());
    }

    private void CancelInternetArchiveLoadMore()
    {
        internetArchiveLoadMoreCts?.Cancel();
        internetArchiveLoadMoreCts?.Dispose();
        internetArchiveLoadMoreCts = null;
        internetArchiveLoadMoreLoading = false;
        internetArchiveLoadMoreMessage = null;
    }

    private void ShowInternetArchiveCategory(ArchiveCategory category)
    {
        CancelInternetArchiveSearch();
        CancelInternetArchiveLoadMore();
        internetArchiveViewTitle = category.Label;
        internetArchiveViewCategoryKey = category.Key;
        internetArchiveViewResults = archiveRows.TryGetValue(category.Key, out var cached)
            ? cached
            : Array.Empty<InternetArchiveItem>();
        internetArchiveError = null;
        if (internetArchiveViewResults.Count == 0) StartInternetArchiveHomeLoad(force: false);
    }

    private void StartInternetArchiveSearch(
        string title,
        string query,
        string sort,
        string cacheSearchText)
    {
        CancelInternetArchiveLoadMore();
        internetArchiveSearchCts?.Cancel();
        internetArchiveSearchCts?.Dispose();
        internetArchiveSearchCts = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref internetArchiveSearchGeneration);
        internetArchiveViewTitle = title;
        internetArchiveViewCategoryKey = null;
        internetArchiveViewResults = SearchInternetArchiveCache(cacheSearchText, 40);
        internetArchiveSearchLoading = true;
        internetArchiveError = null;
        _ = LoadInternetArchiveSearchAsync(
            title,
            query,
            sort,
            internetArchiveViewResults,
            generation,
            internetArchiveSearchCts.Token);
    }

    private async Task LoadInternetArchiveSearchAsync(
        string title,
        string query,
        string sort,
        IReadOnlyList<InternetArchiveItem> cachedResults,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await internetArchive.SearchAsync(
                query,
                sort,
                12,
                cancellationToken,
                item => PublishInternetArchiveSearchItem(item, generation, cancellationToken),
                priorityCandidateCount: 12)
                .ConfigureAwait(false);
            if (generation != internetArchiveSearchGeneration || cancellationToken.IsCancellationRequested) return;
            foreach (var item in results)
            {
                archiveSearchCache[item.Identifier] = item;
            }
            internetArchiveViewResults = MergeCachedThenFreshArchiveItems(cachedResults, results, 60);
            internetArchiveViewTitle = title;
            SaveInternetArchiveCache();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation != internetArchiveSearchGeneration) return;
            AepLog.Warning($"[InternetArchive] Search failed: {exception.Message}");
            internetArchiveError = "That Internet Archive search failed. Please try again.";
        }
        finally
        {
            if (generation == internetArchiveSearchGeneration) internetArchiveSearchLoading = false;
        }
    }

    private void PublishInternetArchiveSearchItem(
        InternetArchiveItem item,
        int generation,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || generation != internetArchiveSearchGeneration) return;
        lock (internetArchiveCacheSync)
        {
            if (cancellationToken.IsCancellationRequested || generation != internetArchiveSearchGeneration) return;
            archiveSearchCache[item.Identifier] = item;
            internetArchiveViewResults = MergeCachedThenFreshArchiveItems(
                internetArchiveViewResults,
                new[] { item },
                60);
        }
    }

    private void CancelInternetArchiveSearch()
    {
        Interlocked.Increment(ref internetArchiveSearchGeneration);
        internetArchiveSearchCts?.Cancel();
        internetArchiveSearchCts?.Dispose();
        internetArchiveSearchCts = null;
        internetArchiveSearchLoading = false;
    }

    private bool InternetArchiveCacheIsFreshAndComplete()
    {
        if (internetArchiveLastRefreshUtc == DateTime.MinValue ||
            DateTime.UtcNow - internetArchiveLastRefreshUtc >= InternetArchiveRefreshInterval)
            return false;
        return ArchiveCategories.All(category =>
            archiveRows.TryGetValue(category.Key, out var items) && items.Count > 0);
    }

    private IReadOnlyList<InternetArchiveItem> SearchInternetArchiveCache(string searchText, int maximum)
    {
        var terms = searchText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return Array.Empty<InternetArchiveItem>();

        return archiveRows.Values
            .SelectMany(items => items)
            .Concat(archiveSearchCache.Values)
            .GroupBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(item =>
            {
                var searchable = string.Join(
                    ' ',
                    item.Identifier,
                    item.Title,
                    item.Creator,
                    item.Description,
                    string.Join(' ', item.Episodes.Select(episode => episode.Title)));
                return terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(item => item.Downloads)
            .Take(maximum)
            .ToArray();
    }

    private static IReadOnlyList<InternetArchiveItem> MergeFreshArchiveItems(
        IReadOnlyList<InternetArchiveItem> fresh,
        IReadOnlyList<InternetArchiveItem> cached,
        int maximum)
    {
        var result = new List<InternetArchiveItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in fresh.Concat(cached))
        {
            if (result.Count >= maximum) break;
            if (seen.Add(item.Identifier)) result.Add(item);
        }
        return result;
    }

    private static IReadOnlyList<InternetArchiveItem> MergeCachedThenFreshArchiveItems(
        IReadOnlyList<InternetArchiveItem> cached,
        IReadOnlyList<InternetArchiveItem> fresh,
        int maximum)
    {
        var freshByIdentifier = fresh.ToDictionary(item => item.Identifier, StringComparer.OrdinalIgnoreCase);
        var result = new List<InternetArchiveItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cachedItem in cached)
        {
            var item = freshByIdentifier.GetValueOrDefault(cachedItem.Identifier) ?? cachedItem;
            if (seen.Add(item.Identifier)) result.Add(item);
        }
        foreach (var item in fresh)
        {
            if (result.Count >= maximum) break;
            if (seen.Add(item.Identifier)) result.Add(item);
        }
        return result.Take(maximum).ToArray();
    }

    private void SaveInternetArchiveCache()
    {
        lock (internetArchiveCacheSync)
        {
            var categories = archiveRows.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Take(200).ToList(),
                StringComparer.Ordinal);
            var searchItems = archiveSearchCache.Values
                .OrderByDescending(item => item.PublicDate ?? DateTime.MinValue)
                .Take(300)
                .ToList();
            internetArchiveCacheStorage.Save(new(
                internetArchiveLastRefreshUtc,
                categories,
                searchItems,
                internetArchive.SnapshotNoVideoItems(),
                archiveCategoryLastPages.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal)));
        }
    }

    private static string FormatArchiveCacheAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalMinutes < 1d) return "less than a minute";
        if (age.TotalHours < 1d) return $"{Math.Max(1, (int)age.TotalMinutes)} minutes";
        if (age.TotalDays < 1d) return $"{Math.Max(1, (int)age.TotalHours)} hours";
        return $"{Math.Max(1, (int)age.TotalDays)} days";
    }

    private void DisposeInternetArchive()
    {
        internetArchiveCts?.Cancel();
        internetArchiveCts?.Dispose();
        internetArchiveCts = null;
        CancelInternetArchiveSearch();
        CancelInternetArchiveLoadMore();
        if (internetArchiveStarted) SaveInternetArchiveCache();
        internetArchive.Dispose();
    }
}
