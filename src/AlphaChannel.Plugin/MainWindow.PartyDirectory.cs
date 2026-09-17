using System.Collections.Concurrent;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const int PartyDirectoryInitialVisibleCount =
        12;

    private const int PartyDirectoryLoadMoreCount =
        12;

    private static readonly TimeSpan PartyDirectoryRefreshInterval =
        TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PartyDirectoryMetadataLifetime =
        TimeSpan.FromMinutes(5);

    private const string AlphaChannelDjStreamPrefix =
    "http://alphachannel.duckdns.org:8000/radio/";

    private sealed record PartyDirectoryMediaInfo(
        string? Title,
        string? ThumbnailUrl,
        DateTime RefreshedAt);

    private readonly ConcurrentDictionary<
        string,
        PartyDirectoryMediaInfo> partyDirectoryMediaCache =
            new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<
        string,
        byte> partyDirectoryMediaRequestsInFlight =
            new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<RoomKind> partyDirectoryRoomKinds =
        new(Enum.GetValues<RoomKind>());

    private readonly HashSet<WatchPartyCategory> partyDirectoryCategories =
        new(
            Enum.GetValues<WatchPartyCategory>()
                .Where(
                    category =>
                        category !=
                        WatchPartyCategory.Unknown));

    private RoomDirectoryDto[] partyDirectoryRooms =
        [];

    private string partyDirectorySearch =
        string.Empty;

    private volatile bool partyDirectoryLoading;

    private bool partyDirectoryRequested;

    private DateTime partyDirectoryRefreshedAt =
        DateTime.MinValue;

    private int partyDirectoryRatingFilter;
    private string? partyDirectoryServer;

    private int partyDirectoryVisibleCount =
     PartyDirectoryInitialVisibleCount;

    //
    // The selected locked room is retained while its password prompt
    // is open. Locked-room joins are only submitted after the player
    // confirms a non-empty password.
    //
    private RoomDirectoryDto? partyDirectoryPasswordRoom;

    private string partyDirectoryPassword =
        string.Empty;

    private string? partyDirectoryPasswordError;

    private bool partyDirectoryPasswordPopupRequested;

    private void DrawPartyDirectoryPage()
    {
        RefreshPartyDirectoryIfNeeded();

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   Ui(8f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FramePadding,
                   UiVec(10f, 8f)))
        {
            DrawPartyDirectorySearch();

            ImGui.Dummy(
                UiVec(
                    0f,
                    8f));

            DrawPartyDirectoryFilters();

            ImGui.Dummy(
                UiVec(
                    0f,
                    10f));

            DrawPartyDirectoryResults();
        }
    }

    private void RefreshPartyDirectoryIfNeeded(
        bool force = false)
    {
        if (CurrentSession is not { } session)
        {
            partyDirectoryRooms =
                [];

            partyDirectoryLoading =
                false;

            return;
        }

        var expired =
            DateTime.UtcNow -
            partyDirectoryRefreshedAt >=
            PartyDirectoryRefreshInterval;

        if (!force &&
            partyDirectoryRequested &&
            !expired)
        {
            return;
        }

        if (partyDirectoryLoading)
        {
            return;
        }

        partyDirectoryRequested =
            true;

        partyDirectoryLoading =
            true;

        var token =
            session.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var rooms =
                        await roomsClient
                            .ListAsync(token)
                            .ConfigureAwait(false);

                    partyDirectoryRooms =
                        rooms;

                    partyDirectoryRefreshedAt =
                        DateTime.UtcNow;

                    partyDirectoryVisibleCount =
                        Math.Max(
                            partyDirectoryVisibleCount,
                            PartyDirectoryInitialVisibleCount);
                }
                catch (Exception exception)
                {
                    AepLog.Warning(
                        $"[PartyDirectory] Refresh failed: {exception.Message}");
                }
                finally
                {
                    partyDirectoryLoading =
                        false;
                }
            });
    }

    private void DrawPartyDirectorySearch()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var gap =
            Ui(10f);

        var refreshSize =
            ImGui.GetFrameHeight();

        var searchWidth =
            Math.Max(
                Ui(120f),
                availableWidth -
                refreshSize -
                gap);

        using (ImRaii.PushColor(
                   ImGuiCol.FrameBg,
                   new Vector4(
                       0.035f,
                       0.045f,
                       0.075f,
                       0.98f)))
        using (ImRaii.PushColor(
                   ImGuiCol.FrameBgHovered,
                   new Vector4(
                       0.065f,
                       0.060f,
                       0.115f,
                       0.98f)))
        using (ImRaii.PushColor(
                   ImGuiCol.FrameBgActive,
                   new Vector4(
                       0.080f,
                       0.065f,
                       0.140f,
                       1f)))
        {
            ImGui.SetNextItemWidth(
                searchWidth);

            if (ImGui.InputTextWithHint(
                    "##partyDirectorySearch",
                    "Search hosts, rooms, locations, categories or content",
                    ref partyDirectorySearch,
                    120))
            {
                partyDirectoryVisibleCount =
                    PartyDirectoryInitialVisibleCount;
            }
        }

        ImGui.SameLine(
            0f,
            gap);

        var refreshClicked =
            false;

        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.12f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.28f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.42f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.58f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   Ui(1f)))
        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            refreshClicked =
                ImGui.Button(
                    FontAwesomeIcon
                        .SyncAlt
                        .ToIconString() +
                    "##refreshPartyDirectory",
                    new Vector2(
                        refreshSize,
                        refreshSize));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Refresh Watch Parties");
        }

        if (refreshClicked)
        {
            RefreshPartyDirectoryIfNeeded(
                force: true);
        }
    }

    private void DrawPartyDirectoryFilters()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var gap =
            Ui(8f);

        var clearWidth =
            Ui(112f);

        var filterWidth =
            Math.Max(
                Ui(112f),
                (
                    availableWidth -
                    clearWidth -
                    gap * 4f
                ) /
                4f);

        DrawPartyDirectoryRoomTypeFilter(
            filterWidth);

        ImGui.SameLine(
            0f,
            gap);

        DrawPartyDirectoryCategoryFilter(
            filterWidth);

        ImGui.SameLine(
            0f,
            gap);

        DrawPartyDirectoryRatingFilter(
            filterWidth);

        ImGui.SameLine(
            0f,
            gap);

        DrawPartyDirectoryServerPlaceholder(
            filterWidth);

        ImGui.SameLine(
            0f,
            gap);

        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       0.045f,
                       0.050f,
                       0.085f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.20f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.42f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   Ui(1f)))
        {
            if (ImGui.Button(
                    "Clear filters",
                    new Vector2(
                        clearWidth,
                        0f)))
            {
                ResetPartyDirectoryFilters();
            }
        }
    }

    private void DrawPartyDirectoryRoomTypeFilter(
        float width)
    {
        var preview =
            partyDirectoryRoomKinds.Count switch
            {
                3 =>
                    "Room type: All",

                0 =>
                    "Room type: None",

                1 =>
                    $"Room type: {PartyDirectoryRoomKindText(
                        partyDirectoryRoomKinds.First())}",

                _ =>
                    $"Room type: {partyDirectoryRoomKinds.Count} selected",
            };

        ImGui.SetNextItemWidth(
            width);

        if (!ImGui.BeginCombo(
                "##partyDirectoryRoomTypes",
                preview))
        {
            return;
        }

        foreach (var kind in
                 Enum.GetValues<RoomKind>())
        {
            var selected =
                partyDirectoryRoomKinds.Contains(
                    kind);

            if (ImGui.Checkbox(
                    $"{PartyDirectoryRoomKindText(kind)}##directoryKind_{kind}",
                    ref selected))
            {
                if (selected)
                {
                    partyDirectoryRoomKinds.Add(
                        kind);
                }
                else
                {
                    partyDirectoryRoomKinds.Remove(
                        kind);
                }

                partyDirectoryVisibleCount =
                    PartyDirectoryInitialVisibleCount;
            }
        }

        ImGui.EndCombo();
    }

    private void DrawPartyDirectoryCategoryFilter(
        float width)
    {
        var availableCategoryCount =
            Enum.GetValues<WatchPartyCategory>()
                .Count(
                    category =>
                        category !=
                        WatchPartyCategory.Unknown);

        var preview =
            partyDirectoryCategories.Count switch
            {
                0 =>
                    "Categories: None",

                var count when
                    count ==
                    availableCategoryCount =>
                    "Categories: All",

                1 =>
                    $"Category: {PartyDirectoryCategoryName(
                        partyDirectoryCategories.First())}",

                _ =>
                    $"Categories: {partyDirectoryCategories.Count} selected",
            };

        ImGui.SetNextItemWidth(
            width);

        if (!ImGui.BeginCombo(
                "##partyDirectoryCategories",
                preview))
        {
            return;
        }

        foreach (var category in
                 Enum.GetValues<WatchPartyCategory>())
        {
            if (category ==
                WatchPartyCategory.Unknown)
            {
                continue;
            }

            var selected =
                partyDirectoryCategories.Contains(
                    category);

            if (ImGui.Checkbox(
                    $"{PartyDirectoryCategoryName(category)}##directoryCategory_{category}",
                    ref selected))
            {
                if (selected)
                {
                    partyDirectoryCategories.Add(
                        category);
                }
                else
                {
                    partyDirectoryCategories.Remove(
                        category);
                }

                partyDirectoryVisibleCount =
                    PartyDirectoryInitialVisibleCount;
            }
        }

        ImGui.EndCombo();
    }

    private void DrawPartyDirectoryRatingFilter(
        float width)
    {
        var preview =
            partyDirectoryRatingFilter switch
            {
                1 =>
                    "Rating: Not 18+",

                2 =>
                    "Rating: 18+ only",

                _ =>
                    "Rating: All",
            };

        ImGui.SetNextItemWidth(
            width);

        if (!ImGui.BeginCombo(
                "##partyDirectoryRating",
                preview))
        {
            return;
        }

        if (ImGui.Selectable(
                "All",
                partyDirectoryRatingFilter == 0))
        {
            partyDirectoryRatingFilter =
          0;

            partyDirectoryServer =
                null;

            partyDirectoryVisibleCount =
                PartyDirectoryInitialVisibleCount;
        }

        if (ImGui.Selectable(
                "Not 18+",
                partyDirectoryRatingFilter == 1))
        {
            partyDirectoryRatingFilter =
                1;

            partyDirectoryVisibleCount =
                PartyDirectoryInitialVisibleCount;
        }

        if (ImGui.Selectable(
                "18+ only",
                partyDirectoryRatingFilter == 2))
        {
            partyDirectoryRatingFilter =
                2;

            partyDirectoryVisibleCount =
                PartyDirectoryInitialVisibleCount;
        }

        ImGui.EndCombo();
    }

    private void DrawPartyDirectoryServerPlaceholder(
    float width)
    {
        var servers =
            partyDirectoryRooms
                .Select(
                    WatchPartyServer)
                .Where(
                    server =>
                        !string.IsNullOrWhiteSpace(
                            server))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    server =>
                        server,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        ImGui.SetNextItemWidth(
            width);

        var preview =
            string.IsNullOrWhiteSpace(
                partyDirectoryServer)
                ? "Server: All servers"
                : $"Server: {partyDirectoryServer}";

        if (!ImGui.BeginCombo(
                "##partyDirectoryServer",
                preview))
        {
            return;
        }

        var allSelected =
            string.IsNullOrWhiteSpace(
                partyDirectoryServer);

        if (ImGui.Selectable(
                "All servers",
                allSelected))
        {
            partyDirectoryServer =
                null;

            partyDirectoryVisibleCount =
                PartyDirectoryInitialVisibleCount;
        }

        foreach (var server in servers)
        {
            var selected =
                string.Equals(
                    partyDirectoryServer,
                    server,
                    StringComparison.OrdinalIgnoreCase);

            if (ImGui.Selectable(
                    server,
                    selected))
            {
                partyDirectoryServer =
                    server;

                partyDirectoryVisibleCount =
                    PartyDirectoryInitialVisibleCount;
            }
        }

        ImGui.EndCombo();
    }
    private void ResetPartyDirectoryFilters()
    {
        partyDirectorySearch =
            string.Empty;

        partyDirectoryRoomKinds.Clear();

        foreach (var kind in
                 Enum.GetValues<RoomKind>())
        {
            partyDirectoryRoomKinds.Add(
                kind);
        }

        partyDirectoryCategories.Clear();

        foreach (var category in
                 Enum.GetValues<WatchPartyCategory>())
        {
            if (category !=
                WatchPartyCategory.Unknown)
            {
                partyDirectoryCategories.Add(
                    category);
            }
        }

        partyDirectoryRatingFilter =
            0;

        partyDirectoryVisibleCount =
            PartyDirectoryInitialVisibleCount;
    }

    private void DrawPartyDirectoryResults()
    {
        var filteredRooms =
            GetFilteredPartyDirectoryRooms();

        var totalCount =
            filteredRooms.Length;

        var displayedCount =
            Math.Min(
                totalCount,
                partyDirectoryVisibleCount);

        ImGui.TextColored(
            Vector4.One,
            $"{totalCount} Watch Parties");

        var sortText =
            "Sorted by most viewers";

        var sortWidth =
            ImGui.CalcTextSize(
                sortText).X;

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            Math.Max(
                ImGui.GetCursorPosX(),
                ImGui.GetWindowWidth() -
                sortWidth -
                Ui(18f)));

        ImGui.TextColored(
            MutedText,
            sortText);

        ImGui.Dummy(
            UiVec(
                0f,
                6f));

        var resultsHeight =
            Math.Max(
                Ui(120f),
                ImGui.GetContentRegionAvail().Y);

        using (var results =
               ImRaii.Child(
                   "##partyDirectoryResults",
                   new Vector2(
                       0f,
                       resultsHeight),
                   false,
                   ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            if (!results)
            {
                return;
            }

            if (CurrentSession is null)
            {
                DrawPartyDirectoryEmptyState(
                    FontAwesomeIcon.UserLock,
                    "Sign in to browse Watch Parties",
                    "The public directory is available after signing in.");

                return;
            }

            if (partyDirectoryLoading &&
                partyDirectoryRooms.Length == 0)
            {
                DrawPartyDirectoryEmptyState(
                    FontAwesomeIcon.SyncAlt,
                    "Loading Watch Parties",
                    "Fetching the latest public rooms and venues.");

                return;
            }

            if (totalCount == 0)
            {
                DrawPartyDirectoryEmptyState(
                    FontAwesomeIcon.Search,
                    "No Watch Parties found",
                    "Try clearing or changing your search filters.");

                return;
            }

            var rowHeight =
                Ui(190f);

            var rowGap =
                Ui(10f);

            for (var index = 0;
                 index < displayedCount;
                 index++)
            {
                var room =
                    filteredRooms[index];

                ImGui.PushID(
                    $"partyDirectoryRoom_{room.HostAccountId}_{index}");

                DrawPartyDirectoryRoom(
                    room,
                    ImGui.GetContentRegionAvail().X,
                    rowHeight);

                ImGui.PopID();

                if (index <
                    displayedCount - 1)
                {
                    ImGui.Dummy(
                        UiVec(
                            0f,
                            rowGap));
                }
            }

            ImGui.Dummy(
                UiVec(
                    0f,
                    12f));

            var showingText =
                $"Showing {displayedCount} of {totalCount} rooms";

            var showingWidth =
                ImGui.CalcTextSize(
                    showingText).X;

            ImGui.SetCursorPosX(
                Math.Max(
                    ImGui.GetCursorPosX(),
                    (
                        ImGui.GetWindowWidth() -
                        showingWidth
                    ) *
                    0.5f));

            ImGui.TextColored(
                MutedText,
                showingText);

            if (displayedCount <
                totalCount)
            {
                ImGui.Dummy(
                    UiVec(
                        0f,
                        5f));

                var loadMoreWidth =
                    Ui(150f);

                ImGui.SetCursorPosX(
                    Math.Max(
                        ImGui.GetCursorPosX(),
                        (
                            ImGui.GetWindowWidth() -
                            loadMoreWidth
                        ) *
                        0.5f));

                using (ImRaii.PushColor(
                           ImGuiCol.Button,
                           new Vector4(
                               Accent.X,
                               Accent.Y,
                               Accent.Z,
                               0.12f)))
                using (ImRaii.PushColor(
                           ImGuiCol.ButtonHovered,
                           new Vector4(
                               Accent.X,
                               Accent.Y,
                               Accent.Z,
                               0.28f)))
                using (ImRaii.PushColor(
                           ImGuiCol.Border,
                           new Vector4(
                               Accent.X,
                               Accent.Y,
                               Accent.Z,
                               0.58f)))
                using (ImRaii.PushStyle(
                           ImGuiStyleVar.FrameBorderSize,
                           Ui(1f)))
                {
                    if (ImGui.Button(
                            "Load more",
                            new Vector2(
                                loadMoreWidth,
                                0f)))
                    {
                        partyDirectoryVisibleCount +=
                            PartyDirectoryLoadMoreCount;
                    }
                }
            }

            ImGui.Dummy(
                UiVec(
                    0f,
                    10f));
        }
    }

    private RoomDirectoryDto[] GetFilteredPartyDirectoryRooms()
    {
        var query =
            partyDirectorySearch.Trim();

        return partyDirectoryRooms
            .Where(
                room =>
                    partyDirectoryRoomKinds.Contains(
                        room.Kind))
            .Where(
                room =>
                {
                    var category =
                        GetWatchPartyCategory(
                            room);

                    return category !=
                               WatchPartyCategory.Unknown &&
                           partyDirectoryCategories.Contains(
                               category);
                })
            .Where(
                room =>
                {
                    var adultOnly =
                        WatchPartyIsAdultOnly(
                            room);

                    return partyDirectoryRatingFilter switch
                    {
                        1 =>
                            !adultOnly,

                        2 =>
                            adultOnly,

                        _ =>
                            true,
                    };
                })
            .Where(
                room =>
                    string.IsNullOrWhiteSpace(
                        partyDirectoryServer) ||
                    string.Equals(
                        WatchPartyServer(
                            room),
                        partyDirectoryServer,
                        StringComparison.OrdinalIgnoreCase))
            .Where(
                room =>
                    string.IsNullOrWhiteSpace(
                        query) ||
                    PartyDirectoryMatchesSearch(
                        room,
                        query))
            .OrderByDescending(
                room =>
                    room.ViewerCount + 1)
            .ThenBy(
                room =>
                    room.HasMedia
                        ? 0
                        : 1)
            .ThenBy(
                room =>
                    room.Kind ==
                    RoomKind.Locked
                        ? 1
                        : 0)
            .ThenBy(
                room =>
                    room.HostDisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool PartyDirectoryMatchesSearch(
        RoomDirectoryDto room,
        string query)
    {
        TryReadWatchPartyMetadata(
              room,
              out var category,
              out _,
              out var serverName,
              out var visibleDescription);

        var media =
            GetPartyDirectoryMedia(
                room);

        return PartyDirectoryContains(
                    room.HostDisplayName,
                    query) ||
                PartyDirectoryContains(
                    visibleDescription,
                    query) ||
                PartyDirectoryContains(
                    room.Location,
                    query) ||
                PartyDirectoryContains(
                    serverName,
                    query) ||
                PartyDirectoryContains(
                    PartyDirectoryCategoryName(
                        category),
                    query) ||
                PartyDirectoryContains(
                    PartyDirectoryRoomKindText(
                        room.Kind),
                    query) ||
                PartyDirectoryContains(
                    media.Title,
                    query);
    }

    private static bool PartyDirectoryContains(
        string? value,
        string query)
    {
        return !string.IsNullOrWhiteSpace(
                   value) &&
               value.Contains(
                   query,
                   StringComparison.OrdinalIgnoreCase);
    }

    private void DrawPartyDirectoryRoom(
        RoomDirectoryDto room,
        float width,
        float height)
    {
        EnsurePartyDirectoryMediaLoaded(
            room);

        var media =
            GetPartyDirectoryMedia(
                room);

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

        var cardHovered =
            ImGui.IsMouseHoveringRect(
                origin,
                cardMax);

        drawList.AddRectFilled(
            origin,
            cardMax,
            ImGui.GetColorU32(
                cardHovered
                    ? new Vector4(
                        0.075f,
                        0.060f,
                        0.130f,
                        0.98f)
                    : new Vector4(
                        0.040f,
                        0.050f,
                        0.085f,
                        0.98f)),
            Ui(11f));

        drawList.AddRect(
            origin,
            cardMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    cardHovered
                        ? 0.62f
                        : 0.34f)),
            Ui(11f),
            ImDrawFlags.None,
            Ui(1f));

        var padding =
            Ui(12f);

        var thumbnailWidth =
            Math.Clamp(
                width * 0.27f,
                Ui(190f),
                Ui(275f));

        var thumbnailMin =
            origin +
            new Vector2(
                padding,
                padding);

        var thumbnailSize =
            new Vector2(
                thumbnailWidth,
                height -
                padding * 2f);

        var thumbnailMax =
            thumbnailMin +
            thumbnailSize;

        DrawPartyDirectoryThumbnail(
            drawList,
            room,
            media,
            thumbnailMin,
            thumbnailSize);

        DrawPartyDirectoryPlaybackBadge(
            drawList,
            room,
            thumbnailMin +
            UiVec(
                8f,
                8f));

        DrawPartyDirectoryViewerBadge(
            drawList,
            room.ViewerCount + 1,
            new Vector2(
                thumbnailMax.X -
                Ui(8f),
                thumbnailMin.Y +
                Ui(8f)));

        DrawPartyDirectoryCategoryBubble(
            drawList,
            room,
            new Vector2(
                thumbnailMin.X +
                thumbnailSize.X *
                0.5f,
                thumbnailMax.Y -
                Ui(2f)));

        var actionWidth =
            Ui(160f);

        var lockButtonSize =
            Ui(42f);

        var actionGap =
            Ui(8f);

        var hasLockButton =
            room.Kind ==
            RoomKind.Locked;

        var actionTotalWidth =
            actionWidth +
            (
                hasLockButton
                    ? lockButtonSize +
                      actionGap
                    : 0f
            );

        var actionStartX =
            cardMax.X -
            padding -
            actionTotalWidth;

        var contentX =
            thumbnailMax.X +
            Ui(20f);

        var contentRight =
            actionStartX -
            Ui(18f);

        var contentWidth =
            Math.Max(
                Ui(120f),
                contentRight -
                contentX);

        var hostName =
            string.IsNullOrWhiteSpace(
                room.HostDisplayName)
                ? "Unknown Host"
                : room.HostDisplayName;

        DrawPartyDirectoryHost(
            drawList,
            room,
            hostName,
            new Vector2(
                contentX,
                origin.Y +
                padding),
            Ui(34f));

        var titleY =
            origin.Y +
            Ui(56f);

        //
        // Never reveal or infer the current media title for a locked
        // room. Its directory title remains fixed whether it currently
        // has media or is waiting for content.
        //
        var roomTitle =
            room.Kind ==
            RoomKind.Locked
                ? "Locked Room - Content Hidden"
                : media.Title;

        var displayTitle =
            PartyDirectoryTrimToWidth(
                roomTitle,
                contentWidth);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                contentX,
                titleY),
            ImGui.GetColorU32(
                Vector4.One),
            displayTitle);

        var description =
            WatchPartyDescription(
                room);

        var descriptionY =
            titleY +
            Ui(28f);

        var displayDescription =
            PartyDirectoryTrimToWidth(
                description,
                contentWidth);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                contentX,
                descriptionY),
            ImGui.GetColorU32(
                MutedText),
            displayDescription);

        if (ImGui.IsMouseHoveringRect(
                new Vector2(
                    contentX,
                    descriptionY),
                new Vector2(
                    contentX +
                    contentWidth,
                    descriptionY +
                    ImGui.GetTextLineHeight())))
        {
            ImGui.SetTooltip(
                description);
        }

        var location =
            WatchPartyLocation(
                room);

        var locationY =
            descriptionY +
            Ui(27f);

        var locationGlyph =
            FontAwesomeIcon
                .MapMarkerAlt
                .ToIconString();

        Vector2 locationGlyphSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            locationGlyphSize =
                ImGui.CalcTextSize(
                    locationGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    contentX,
                    locationY),
                ImGui.GetColorU32(
                    AccentHover),
                locationGlyph);
        }

        var locationTextX =
            contentX +
            locationGlyphSize.X +
            Ui(7f);

        var locationTextWidth =
            Math.Max(
                Ui(60f),
                contentRight -
                locationTextX);

        var displayLocation =
            PartyDirectoryTrimToWidth(
                location,
                locationTextWidth);

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

        if (ImGui.IsMouseHoveringRect(
                new Vector2(
                    locationTextX,
                    locationY),
                new Vector2(
                    locationTextX +
                    locationTextWidth,
                    locationY +
                    ImGui.GetTextLineHeight())))
        {
            ImGui.SetTooltip(
                location);
        }

        var tagsY =
            origin.Y +
            height -
            padding -
            Ui(25f);

        var tagX =
            contentX;

        tagX +=
            DrawWatchPartyTag(
                drawList,
                new Vector2(
                    tagX,
                    tagsY),
                WatchPartyVisibilityIcon(
                    room.Kind),
                WatchPartyVisibilityText(
                    room.Kind),
                WatchPartyVisibilityColor(
                    room.Kind)) +
            Ui(7f);

        tagX +=
       DrawWatchPartyTag(
           drawList,
           new Vector2(
               tagX,
               tagsY),
           WatchPartyCategoryIcon(
               room),
           WatchPartyCategoryText(
               room),
           AccentHover) +
       Ui(7f);

        var serverName =
            WatchPartyServer(
                room);

        if (!string.IsNullOrWhiteSpace(
                serverName))
        {
            tagX +=
                DrawWatchPartyTag(
                    drawList,
                    new Vector2(
                        tagX,
                        tagsY),
                    FontAwesomeIcon.Server,
                    serverName.ToUpperInvariant(),
                    new Vector4(
                        0.35f,
                        0.65f,
                        1f,
                        1f)) +
                Ui(7f);
        }

        tagX +=
            DrawWatchPartyTag(
                drawList,
                new Vector2(
                    tagX,
                    tagsY),
                FontAwesomeIcon.Eye,
                $"{room.ViewerCount + 1} total viewers",
                MutedText) +
            Ui(7f);

        if (WatchPartyIsAdultOnly(
                room))
        {
            DrawWatchPartyTag(
                drawList,
                new Vector2(
                    tagX,
                    tagsY),
                FontAwesomeIcon.ExclamationTriangle,
                "18+",
                new Vector4(
                    1.00f,
                    0.28f,
                    0.32f,
                    1f));
        }

        var buttonY =
            origin.Y +
            (
                height -
                Ui(42f)
            ) *
            0.5f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                actionStartX,
                buttonY));

        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   Accent))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   AccentHover))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   AccentActive))
        {
            if (ImGui.Button(
                    "Join Room",
                    new Vector2(
                        actionWidth,
                        Ui(42f))))
            {
                JoinPartyDirectoryRoom(
                    room);
            }
        }

        if (hasLockButton)
        {
            ImGui.SameLine(
                0f,
                actionGap);

            var lockClicked =
                false;

            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.14f)))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonHovered,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.30f)))
            using (ImRaii.PushColor(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.62f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameBorderSize,
                       Ui(1f)))
            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                lockClicked =
                    ImGui.Button(
                        FontAwesomeIcon
                            .Lock
                            .ToIconString() +
                        "##directoryLockedRoom",
                        new Vector2(
                            lockButtonSize,
                            Ui(42f)));
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Password required");
            }

            if (lockClicked)
            {
                JoinPartyDirectoryRoom(
                    room);
            }
        }

        ImGui.SetCursorScreenPos(
            origin);

        ImGui.Dummy(
            size);
    }

    private void DrawPartyDirectoryThumbnail(
      ImDrawListPtr drawList,
      RoomDirectoryDto room,
      PartyDirectoryMediaInfo media,
      Vector2 origin,
      Vector2 size)
    {
        IDalamudTextureWrap? image =
            null;

        //
        // Locked rooms deliberately do not receive their real media
        // URL or thumbnail. The normal fallback artwork is retained
        // underneath the locked-room treatment.
        //
        if (room.Kind !=
                RoomKind.Locked &&
            !string.IsNullOrWhiteSpace(
                media.ThumbnailUrl))
        {
            image =
                thumbnails.Get(
                    media.ThumbnailUrl);
        }

        image ??=
            homeHero;

        if (image is not null)
        {
            var (uv0, uv1) =
                CoverUvs(
                    image.Width,
                    image.Height,
                    size.X,
                    size.Y);

            drawList.AddImageRounded(
                image.Handle,
                origin,
                origin + size,
                uv0,
                uv1,
                uint.MaxValue,
                Ui(8f));
        }
        else
        {
            drawList.AddRectFilled(
                origin,
                origin + size,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.16f)),
                Ui(8f));
        }

        //
        // General image treatment.
        //
        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    0.005f,
                    0.008f,
                    0.020f,
                    0.14f)),
            Ui(8f));

        if (room.Kind ==
            RoomKind.Locked)
        {
            //
            // Place a neutral semi-transparent grey layer over the
            // entire thumbnail so it cannot be mistaken for visible
            // room content.
            //
            drawList.AddRectFilled(
                origin,
                origin + size,
                ImGui.GetColorU32(
                    new Vector4(
                        0.18f,
                        0.19f,
                        0.22f,
                        0.76f)),
                Ui(8f));

            var lockCenter =
                origin +
                size *
                0.5f;

            var lockCircleRadius =
                Ui(25f);

            drawList.AddCircleFilled(
                lockCenter,
                lockCircleRadius,
                ImGui.GetColorU32(
                    new Vector4(
                        0.045f,
                        0.050f,
                        0.070f,
                        0.92f)),
                32);

            drawList.AddCircle(
                lockCenter,
                lockCircleRadius,
                ImGui.GetColorU32(
                    new Vector4(
                        0.82f,
                        0.84f,
                        0.90f,
                        0.72f)),
                32,
                Ui(1.5f));

            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                var lockGlyph =
                    FontAwesomeIcon.Lock
                        .ToIconString();

                var lockGlyphSize =
                    ImGui.CalcTextSize(
                        lockGlyph);

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    lockCenter -
                    lockGlyphSize *
                    0.5f,
                    ImGui.GetColorU32(
                        new Vector4(
                            0.94f,
                            0.95f,
                            0.98f,
                            1f)),
                    lockGlyph);
            }
        }

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    room.Kind ==
                            RoomKind.Locked
                        ? 0.44f
                        : 0.28f)),
            Ui(8f),
            ImDrawFlags.None,
            Ui(1f));
    }

    private void DrawPartyDirectoryPlaybackBadge(
        ImDrawListPtr drawList,
        RoomDirectoryDto room,
        Vector2 origin)
    {
        var text =
            WatchPartyPlaybackStateText(
                room);

        var color =
            WatchPartyPlaybackStateColor(
                room);

        var textSize =
            ImGui.CalcTextSize(
                text);

        var size =
            new Vector2(
                textSize.X +
                Ui(22f),
                Ui(23f));

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    0.015f,
                    0.020f,
                    0.045f,
                    0.94f)),
            Ui(5f));

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                color),
            Ui(5f),
            ImDrawFlags.None,
            Ui(1f));

        drawList.AddCircleFilled(
            new Vector2(
                origin.X +
                Ui(8f),
                origin.Y +
                size.Y *
                0.5f),
            Ui(2.6f),
            ImGui.GetColorU32(
                color),
            16);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X +
                Ui(14f),
                origin.Y +
                (
                    size.Y -
                    textSize.Y
                ) *
                0.5f),
            ImGui.GetColorU32(
                color),
            text);
    }

    private void DrawPartyDirectoryViewerBadge(
        ImDrawListPtr drawList,
        int totalViewers,
        Vector2 topRight)
    {
        var countText =
            totalViewers.ToString();

        var eye =
            FontAwesomeIcon.Eye
                .ToIconString();

        Vector2 eyeSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            eyeSize =
                ImGui.CalcTextSize(
                    eye);
        }

        var countSize =
            ImGui.CalcTextSize(
                countText);

        var badgeSize =
            new Vector2(
                eyeSize.X +
                countSize.X +
                Ui(20f),
                Ui(23f));

        var badgeMin =
            new Vector2(
                topRight.X -
                badgeSize.X,
                topRight.Y);

        drawList.AddRectFilled(
            badgeMin,
            badgeMin +
            badgeSize,
            ImGui.GetColorU32(
                new Vector4(
                    0.015f,
                    0.020f,
                    0.045f,
                    0.94f)),
            Ui(5f));

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    badgeMin.X +
                    Ui(7f),
                    badgeMin.Y +
                    (
                        badgeSize.Y -
                        eyeSize.Y
                    ) *
                    0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                eye);
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                badgeMin.X +
                Ui(11f) +
                eyeSize.X,
                badgeMin.Y +
                (
                    badgeSize.Y -
                    countSize.Y
                ) *
                0.5f),
            ImGui.GetColorU32(
                Vector4.One),
            countText);
    }

    private void DrawPartyDirectoryCategoryBubble(
        ImDrawListPtr drawList,
        RoomDirectoryDto room,
        Vector2 center)
    {
        var radius =
            Ui(22f);

        drawList.AddCircleFilled(
            center,
            radius +
            Ui(3f),
            ImGui.GetColorU32(
                new Vector4(
                    0.010f,
                    0.015f,
                    0.035f,
                    0.98f)),
            32);

        drawList.AddCircleFilled(
            center,
            radius,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.30f)),
            32);

        drawList.AddCircle(
            center,
            radius,
            ImGui.GetColorU32(
                AccentHover),
            32,
            Ui(1f));

        var glyph =
            WatchPartyCategoryIcon(
                room)
                .ToIconString();

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                center -
                glyphSize *
                0.5f,
                ImGui.GetColorU32(
                    Vector4.One),
                glyph);
        }
    }

    private void DrawPartyDirectoryHost(
        ImDrawListPtr drawList,
        RoomDirectoryDto room,
        string hostName,
        Vector2 origin,
        float avatarSize)
    {
        if (!string.IsNullOrWhiteSpace(
                room.HostAccountId))
        {
            EnsurePartyAvatarLoaded(
                room.HostAccountId,
                hostName);
        }

        PartyAvatarInfo? avatar =
            null;

        if (!string.IsNullOrWhiteSpace(
                room.HostAccountId) &&
            partyAvatarCache.TryGetValue(
                room.HostAccountId,
                out var cachedAvatar))
        {
            avatar =
                cachedAvatar;
        }

        var hasAvatar =
            avatar is not null &&
            (
                !string.IsNullOrWhiteSpace(
                    avatar.AvatarIcon) ||
                !string.IsNullOrWhiteSpace(
                    avatar.AvatarImageUrl)
            );

        if (hasAvatar &&
            avatar is not null)
        {
            DrawAvatarAt(
                origin,
                avatar.AvatarIcon,
                avatar.AvatarColorHex,
                avatarSize,
                avatar.AvatarImageUrl);
        }
        else
        {
            var center =
                origin +
                new Vector2(
                    avatarSize * 0.5f,
                    avatarSize * 0.5f);

            drawList.AddCircleFilled(
                center,
                avatarSize * 0.5f,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.22f)),
                32);

            drawList.AddCircle(
                center,
                avatarSize * 0.5f,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.62f)),
                32,
                Ui(1f));

            var glyph =
                FontAwesomeIcon.User
                    .ToIconString();

            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                var glyphSize =
                    ImGui.CalcTextSize(
                        glyph);

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    center -
                    glyphSize *
                    0.5f,
                    ImGui.GetColorU32(
                        AccentHover),
                    glyph);
            }
        }

        var hostText =
            $"Hosted by: {hostName}";

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X +
                avatarSize +
                Ui(9f),
                origin.Y +
                (
                    avatarSize -
                    ImGui.GetTextLineHeight()
                ) *
                0.5f),
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.98f)),
            hostText);
    }

    private void DrawPartyDirectoryEmptyState(
        FontAwesomeIcon icon,
        string title,
        string description)
    {
        var available =
            ImGui.GetContentRegionAvail();

        var origin =
            ImGui.GetCursorScreenPos();

        var center =
            origin +
            new Vector2(
                available.X *
                0.5f,
                Math.Min(
                    available.Y *
                    0.42f,
                    Ui(180f)));

        var glyph =
            icon.ToIconString();

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            ImGui.SetCursorScreenPos(
                new Vector2(
                    center.X -
                    glyphSize.X *
                    0.5f,
                    center.Y -
                    Ui(35f)));

            ImGui.TextColored(
                AccentHover,
                glyph);
        }

        var titleSize =
            ImGui.CalcTextSize(
                title);

        ImGui.SetCursorScreenPos(
            new Vector2(
                center.X -
                titleSize.X *
                0.5f,
                center.Y));

        ImGui.TextColored(
            Vector4.One,
            title);

        var descriptionSize =
            ImGui.CalcTextSize(
                description);

        ImGui.SetCursorScreenPos(
            new Vector2(
                center.X -
                descriptionSize.X *
                0.5f,
                center.Y +
                Ui(28f)));

        ImGui.TextColored(
            MutedText,
            description);
    }

    private static bool IsAlphaChannelDjStream(
    RoomDirectoryDto room)
    {
        return !string.IsNullOrWhiteSpace(
                   room.Url) &&
               room.Url.Trim()
                   .StartsWith(
                       AlphaChannelDjStreamPrefix,
                       StringComparison.OrdinalIgnoreCase);
    }

    private void EnsurePartyDirectoryMediaLoaded(
        RoomDirectoryDto room)
    {
        if (!room.HasMedia ||
      string.IsNullOrWhiteSpace(
          room.Url) ||
      IsAlphaChannelDjStream(
          room))
        {
            return;
        }

        //
        // If the server eventually supplies metadata, there is no
        // reason to perform an additional local lookup.
        //
        if (!string.IsNullOrWhiteSpace(
                room.MediaTitle) ||
            !string.IsNullOrWhiteSpace(
                room.MediaThumbnailUrl))
        {
            return;
        }

        var url =
            room.Url.Trim();

        if (partyDirectoryMediaCache.TryGetValue(
                url,
                out var cached) &&
            DateTime.UtcNow -
            cached.RefreshedAt <
            PartyDirectoryMetadataLifetime)
        {
            return;
        }

        if (!partyDirectoryMediaRequestsInFlight.TryAdd(
                url,
                0))
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var metadata =
                        await searchResolver
                            .GetVideoEntryAsync(
                                url,
                                CancellationToken.None)
                            .ConfigureAwait(false);

                    partyDirectoryMediaCache[url] =
                        new PartyDirectoryMediaInfo(
                            metadata?.Title,
                            metadata?.ThumbnailUrl,
                            DateTime.UtcNow);
                }
                catch (Exception exception)
                {
                    AepLog.Warning(
                        $"[PartyDirectory] Could not resolve media metadata for {url}: " +
                        exception.Message);

                    partyDirectoryMediaCache[url] =
                        new PartyDirectoryMediaInfo(
                            null,
                            null,
                            DateTime.UtcNow);
                }
                finally
                {
                    partyDirectoryMediaRequestsInFlight.TryRemove(
                        url,
                        out _);
                }
            });
    }

    private PartyDirectoryMediaInfo GetPartyDirectoryMedia(
       RoomDirectoryDto room)
    {
        //
        // AlphaChannel Icecast mounts are always presented as a live DJ
        // stream rather than exposing or trying to resolve their URL.
        //
        // Locked rooms retain their privacy-safe title.
        //
        if (room.Kind != RoomKind.Locked &&
            IsAlphaChannelDjStream(
                room))
        {
            return new PartyDirectoryMediaInfo(
                "Live DJ Stream",
                null,
                DateTime.UtcNow);
        }

        if (!string.IsNullOrWhiteSpace(
                room.MediaTitle) ||
            !string.IsNullOrWhiteSpace(
                room.MediaThumbnailUrl))
        {
            return new PartyDirectoryMediaInfo(
                string.IsNullOrWhiteSpace(
                    room.MediaTitle)
                    ? PartyDirectoryFallbackMediaTitle(
                        room)
                    : room.MediaTitle,
                room.MediaThumbnailUrl,
                DateTime.UtcNow);
        }

        if (!string.IsNullOrWhiteSpace(
                room.Url) &&
            partyDirectoryMediaCache.TryGetValue(
                room.Url,
                out var cached))
        {
            return cached with
            {
                Title =
                    string.IsNullOrWhiteSpace(
                        cached.Title)
                        ? PartyDirectoryFallbackMediaTitle(
                            room)
                        : cached.Title,
            };
        }

        return new PartyDirectoryMediaInfo(
            PartyDirectoryFallbackMediaTitle(
                room),
            null,
            DateTime.UtcNow);
    }

    private static string PartyDirectoryFallbackMediaTitle(
       RoomDirectoryDto room)
    {
        //
        // Locked rooms always use one neutral title. This avoids
        // revealing whether the host currently has content loaded.
        //
        if (room.Kind ==
            RoomKind.Locked)
        {
            return "Locked Room - Content Hidden";
        }

        if (!room.HasMedia)
        {
            return "Waiting for content";
        }

        return "Join party to see playing content";
    }

    private void JoinPartyDirectoryRoom(
       RoomDirectoryDto room)
    {
        if (room.Kind !=
            RoomKind.Locked)
        {
            currentPage =
                HomePage.WatchAlong;

            JoinHomeWatchParty(
                room);

            return;
        }

        //
        // Locked rooms must collect their password before navigating
        // away from the directory or submitting the join request.
        //
        partyDirectoryPasswordRoom =
            room;

        partyDirectoryPassword =
            string.Empty;

        partyDirectoryPasswordError =
            null;

        partyDirectoryPasswordPopupRequested =
            true;
    }

    private void DrawPartyDirectoryPasswordPopup()
    {
        if (!partyDirectoryPasswordPopupRequested)
        {
            return;
        }

        var room =
            partyDirectoryPasswordRoom;

        if (room is null)
        {
            partyDirectoryPasswordPopupRequested =
                false;

            partyDirectoryPasswordError =
                null;

            partyDirectoryPassword =
                string.Empty;

            return;
        }

        var popupWidth = Ui(470f);

        var popupHeight = Ui(285f);

        //
        // Use the same Alpha Channel overlay treatment as the locked
        // room creation, username and controller configuration prompts.
        //
        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            new Vector2(
                parentPos.X +
                (parentSize.X -
                 popupWidth) *
                0.5f,

                parentPos.Y +
                (parentSize.Y -
                 popupHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!ImGui.Begin(
                "##partyDirectoryPasswordOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        //
        // Darken the Alpha Channel window behind the prompt.
        //
        drawList.AddRectFilled(
            parentPos,
            parentPos +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.48f)));

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        drawList.AddRectFilled(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    0.055f,
                    0.065f,
                    0.11f,
                    1f)),
            10f);

        drawList.AddRect(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.45f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        const float padding =
            20f;

        var contentWidth =
            popupWidth -
            padding *
            2f;

        //
        // Lock icon.
        //
        var iconCenter =
            popupPos +
            new Vector2(
                padding +
                Ui(18f),
                Ui(31f));

        drawList.AddCircleFilled(
            iconCenter,
            18f,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.24f)),
            24);

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var lockGlyph =
                FontAwesomeIcon.Lock
                    .ToIconString();

            var lockGlyphSize =
                ImGui.CalcTextSize(
                    lockGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconCenter -
                lockGlyphSize *
                0.5f,
                ImGui.GetColorU32(
                    Accent),
                lockGlyph);
        }

        //
        // Heading.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        SetUiFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Join locked room");

        SetUiFontScale(
            1f);

        var hostName =
            string.IsNullOrWhiteSpace(
                room.HostDisplayName)
                ? "this host"
                : room.HostDisplayName;

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(63f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        ImGui.TextColored(
            MutedText,
            $"Enter the password for {hostName}'s room to join the Watch Party.");

        ImGui.PopTextWrapPos();

        //
        // Password field.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(113f)));

        ImGui.TextColored(
            MutedText,
            "Room password");

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(137f)));

        ImGui.SetNextItemWidth(
            contentWidth);

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        var submitRequested =
            false;

        using (ImRaii.PushColor(
                   ImGuiCol.FrameBg,
                   new Vector4(
                       0.025f,
                       0.03f,
                       0.055f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.75f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   5f))
        {
            submitRequested =
                ImGui.InputTextWithHint(
                    "##partyDirectoryRoomPassword",
                    "Enter the room password",
                    ref partyDirectoryPassword,
                    64,
                    ImGuiInputTextFlags.Password |
                    ImGuiInputTextFlags.EnterReturnsTrue);
        }

        if (partyDirectoryPasswordError is { } passwordError)
        {
            ImGui.SetCursorScreenPos(
                popupPos +
                new Vector2(
                    padding,
                    Ui(176f)));

            ImGui.TextColored(
                Danger,
                passwordError);
        }

        //
        // Footer.
        //
        var dividerY =
            popupMax.Y -
            65f;

        drawList.AddLine(
            new Vector2(
                popupPos.X +
                padding,
                dividerY),
            new Vector2(
                popupMax.X -
                padding,
                dividerY),
            ImGui.GetColorU32(
                BorderSubtle),
            1f);

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        var cancelRequested =
            false;

        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       0.08f,
                       0.08f,
                       0.14f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   new Vector4(
                       0.14f,
                       0.11f,
                       0.22f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   new Vector4(
                       0.18f,
                       0.13f,
                       0.28f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.55f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f))
        {
            cancelRequested =
                ImGui.Button(
                    "Cancel",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        ImGui.SameLine(
            0f,
            buttonGap);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   Accent))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   AccentHover))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   AccentActive))
        {
            if (ImGui.Button(
                    "Join Room",
                    new Vector2(
                        buttonWidth,
                        Ui(36f))))
            {
                submitRequested =
                    true;
            }
        }

        var joinConfirmed =
            false;

        if (cancelRequested)
        {
            partyDirectoryPasswordPopupRequested =
                false;

            partyDirectoryPasswordRoom =
                null;

            partyDirectoryPassword =
                string.Empty;

            partyDirectoryPasswordError =
                null;
        }
        else if (submitRequested)
        {
            partyDirectoryPassword =
                partyDirectoryPassword.Trim();

            if (string.IsNullOrWhiteSpace(
                    partyDirectoryPassword))
            {
                partyDirectoryPasswordError =
                    "Please enter the room password.";
            }
            else
            {
                partyDirectoryPasswordPopupRequested =
                    false;

                partyDirectoryPasswordError =
                    null;

                joinConfirmed =
                    true;
            }
        }

        ImGui.End();

        //
        // Submit the existing join flow only after closing the popup's
        // ImGui window.
        //
        if (joinConfirmed)
        {
            joinHostNameInput =
                room.HostDisplayName;

            joinPasswordInput =
                partyDirectoryPassword;

            currentPage =
                HomePage.WatchAlong;

            partyDirectoryPasswordRoom =
                null;

            partyDirectoryPassword =
                string.Empty;

            DoJoin(
                joinHostNameInput,
                joinPasswordInput);
        }
    }

    private static string PartyDirectoryRoomKindText(
        RoomKind kind)
    {
        return kind switch
        {
            RoomKind.Locked =>
                "Locked",

            RoomKind.Venue =>
                "Venue",

            _ =>
                "Public",
        };
    }

    private static string PartyDirectoryCategoryName(
        WatchPartyCategory category)
    {
        return category switch
        {
            WatchPartyCategory.YouTube =>
                "YouTube",

            WatchPartyCategory.Movies =>
                "Movies",

            WatchPartyCategory.Tv =>
                "TV",

            WatchPartyCategory.Twitch =>
                "Twitch",

            WatchPartyCategory.Cartoons =>
                "Cartoons",

            WatchPartyCategory.LiveStream =>
                "Live Stream",

            WatchPartyCategory.Gaming =>
                "Gaming",

            WatchPartyCategory.Dj =>
                "DJ",

            WatchPartyCategory.Music =>
                "Music",

            WatchPartyCategory.Images =>
                "Images",

            WatchPartyCategory.Promotional =>
                "Promotional",

            _ =>
                "Media",
        };
    }

    private static string PartyDirectoryTrimToWidth(
        string? text,
        float width)
    {
        if (string.IsNullOrWhiteSpace(
                text))
        {
            return string.Empty;
        }

        var result =
            text.Trim();

        if (ImGui.CalcTextSize(
                result).X <=
            width)
        {
            return result;
        }

        while (result.Length > 1 &&
               ImGui.CalcTextSize(
                   result + "…").X >
               width)
        {
            result =
                result[..^1];
        }

        return result.TrimEnd() +
               "…";
    }
}
