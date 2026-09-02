using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Config;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Watch party lives on Player: host/join/roster + ephemeral room chat (stream.chat).
internal sealed partial class MainWindow
{
    private string partyJoinInput = string.Empty;

    private enum PartyChatItemKind
    {
        Message,
        MediaRequest,
        MediaQueued,
        MediaPlaying,
        Reaction
    }

    private sealed record PartyChatItem(
        Guid Id,
        PartyChatItemKind Kind,
        string Name,
        string Text,
        string Url = "",
        string Title = "",
        string Source = "",
        TimeSpan? Duration = null,
        string? ThumbnailUrl = null,
        int? QueuePosition = null,
        DateTime? ReceivedAt = null,
        string UserId = "");

    private readonly List<PartyChatItem>
        partyChatItems = [];

    private sealed record PartyAvatarInfo(
        string? AvatarIcon,
        string? AvatarColorHex,
        string? AvatarImageUrl,
        DateTime RefreshedAt);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        PartyAvatarInfo> partyAvatarCache = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        byte> partyAvatarLookupsInFlight = new();

    private readonly System.Collections.Concurrent.ConcurrentQueue<(
        Guid RequestId,
        VideoSearchEntry? Metadata)>
        pendingPartyMediaMetadata = new();

    private string partyChatInput = string.Empty;
    private bool partyChatStickToBottom = true;

    internal void AddPartyReactionToFeed(
      string userId,
      string displayName,
      string glyph)
    {
        if (stream.Mode is not
            (StreamMode.Hosting or StreamMode.Viewing))
        {
            return;
        }

        partyChatItems.Add(
     new PartyChatItem(
         Guid.NewGuid(),
         PartyChatItemKind.Reaction,
         string.IsNullOrWhiteSpace(displayName)
             ? "Someone"
             : displayName,
         glyph,
         UserId: userId));

        if (partyChatItems.Count > 200)
        {
            partyChatItems.RemoveRange(
                0,
                partyChatItems.Count - 200);
        }

        partyChatStickToBottom =
            true;
    }

    private enum PartyPanelTab
    {
        Watching,
        NowPlaying,
        Chat,
    }

    private PartyPanelTab partyPanelTab = PartyPanelTab.Watching;
    private bool gameplayStreamOfferDismissed;

    // Local UI state for the new Now Playing / TV dashboard.
    // Backend host-placement sync will be connected later.
    private bool partySyncTvPlacement = true;

    private bool ShouldShowBottomPlaybackBar =>
        currentPage is
            HomePage.Home or
            HomePage.Player or
            HomePage.VideoGrid;

    private void EnsurePartyAvatarLoaded(
     string userId,
     string displayName)
    {
        if (string.IsNullOrWhiteSpace(userId) ||
            CurrentSession is not { } session)
        {
            return;
        }

        // ---------------------------------------------------------
        // Keep a resolved server avatar for 60 seconds.
        //
        // Once stale, refresh it in the background while the old
        // avatar remains visible.
        // ---------------------------------------------------------

        if (partyAvatarCache.TryGetValue(
                userId,
                out var cachedAvatar) &&
            DateTime.UtcNow -
            cachedAvatar.RefreshedAt <
            TimeSpan.FromSeconds(60))
        {
            return;
        }

        // Only one lookup per participant at a time.
        if (!partyAvatarLookupsInFlight.TryAdd(
                userId,
                0))
        {
            return;
        }

        var token =
            session.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    // -------------------------------------------------
                    // PRIMARY:
                    // Fetch the participant's real server profile.
                    //
                    // This is the exact route verified by our temporary
                    // Watch Party self-test.
                    // -------------------------------------------------

                    AccountProfileDto? profile =
                        null;

                    try
                    {
                        profile =
                            await authClient.GetProfileAsync(
                                token,
                                userId);
                    }
                    catch (Exception exception)
                    {
                        // Profile access may legitimately be unavailable
                        // for a non-friend. Continue to the public search
                        // fallback instead of killing the avatar lookup.
                        AepLog.Info(
                            $"[WatchParty] Direct profile unavailable for {displayName}: {exception.Message}");
                    }

                    if (profile is not null)
                    {
                        partyAvatarCache[userId] =
                            new PartyAvatarInfo(
                                profile.AvatarIcon,
                                profile.AvatarColorHex,
                                profile.AvatarImageUrl,
                                DateTime.UtcNow);

                        return;
                    }

                    // -------------------------------------------------
                    // FALLBACK:
                    // Public/display-name account search.
                    //
                    // Never trust display name alone. The returned
                    // AccountId must exactly match the UserId carried
                    // by the Watch Party message/reaction.
                    // -------------------------------------------------

                    if (string.IsNullOrWhiteSpace(
                            displayName))
                    {
                        return;
                    }

                    var results =
                        await friendsClient.SearchAsync(
                            token,
                            displayName);

                    if (results is null)
                    {
                        return;
                    }

                    var match =
                        results.FirstOrDefault(
                            result =>
                                string.Equals(
                                    result.AccountId,
                                    userId,
                                    StringComparison.Ordinal));

                    if (match is null)
                    {
                        return;
                    }

                    partyAvatarCache[userId] =
                        new PartyAvatarInfo(
                            match.AvatarIcon,
                            match.AvatarColorHex,
                            match.AvatarImageUrl,
                            DateTime.UtcNow);
                }
                catch (Exception exception)
                {
                    AepLog.Warning(
                        $"[WatchParty] Avatar lookup failed for {displayName}: {exception.Message}");
                }
                finally
                {
                    partyAvatarLookupsInFlight.TryRemove(
                        userId,
                        out _);
                }
            });
    }

    private void DrainPartyChat()
    {
        // ---------------------------------------------------------
        // Apply completed local metadata lookups.
        //
        // The lookup itself runs asynchronously, but partyChatItems
        // is owned by the UI thread, so results are applied here.
        // ---------------------------------------------------------

        while (pendingPartyMediaMetadata.TryDequeue(
                   out var resolved))
        {
            if (resolved.Metadata is not { } metadata)
            {
                continue;
            }

            var index =
                partyChatItems.FindIndex(
                    item =>
                        item.Id == resolved.RequestId);

            if (index < 0)
            {
                continue;
            }

            var item =
                partyChatItems[index];

            partyChatItems[index] =
                item with
                {
                    Title =
                        string.IsNullOrWhiteSpace(
                            metadata.Title)
                            ? item.Url
                            : metadata.Title,

                    Source =
                        metadata.ChannelName ??
                        string.Empty,

                    Duration =
                        metadata.Duration,

                    ThumbnailUrl =
                        metadata.ThumbnailUrl
                };
        }

        // ---------------------------------------------------------
        // Incoming media requests.
        //
        // Network transport remains deliberately tiny:
        //
        //     request ID + original URL
        //
        // Metadata is resolved independently by each local client.
        // ---------------------------------------------------------

        while (stream.IncomingMediaRequests.TryDequeue(
                   out var request))
        {
            if (!Uri.TryCreate(
                    request.Url,
                    UriKind.Absolute,
                    out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp &&
                 uri.Scheme != Uri.UriSchemeHttps))
            {
                AepLog.Warning(
                    "[WatchParty] Ignored invalid media request URL.");

                continue;
            }

            partyChatItems.Add(
                new PartyChatItem(
                    request.RequestId,
                    PartyChatItemKind.MediaRequest,
                    request.DisplayName,
                    string.Empty,
                    request.Url,
                    request.Url,
                    string.Empty,
                    null,
                    null));

            partyChatStickToBottom =
                true;

            _ = ResolvePartyMediaMetadataAsync(
                request.RequestId,
                request.Url);
        }

        // ---------------------------------------------------------
        // Host request results.
        // ---------------------------------------------------------

        while (stream.IncomingMediaRequestResults.TryDequeue(
                   out var result))
        {
            var index =
                partyChatItems.FindIndex(
                    item =>
                        item.Id == result.RequestId &&
                        item.Kind == PartyChatItemKind.MediaRequest);

            if (index < 0)
            {
                continue;
            }

            var request =
                partyChatItems[index];

            partyChatItems[index] =
                request with
                {
                    Kind =
                        result.PlayNow
                            ? PartyChatItemKind.MediaPlaying
                            : PartyChatItemKind.MediaQueued,

                    QueuePosition =
                        result.PlayNow
                            ? null
                            : result.QueuePosition
                };

            partyChatStickToBottom =
                true;
        }

        // ---------------------------------------------------------
        // Normal party chat.
        // ---------------------------------------------------------

        while (stream.IncomingChat.TryDequeue(
                 out var message))
        {
            partyChatItems.Add(
                new PartyChatItem(
                    Guid.NewGuid(),
                    PartyChatItemKind.Message,
                    message.DisplayName,
                    message.Text,
                    ReceivedAt: DateTime.Now,
                    UserId: message.UserId));

            partyChatStickToBottom =
                true;
        }

        if (partyChatItems.Count > 200)
        {
            partyChatItems.RemoveRange(
                0,
                partyChatItems.Count - 200);
        }

        if (stream.Mode == StreamMode.None &&
            partyChatItems.Count > 0)
        {
            partyChatItems.Clear();
        }
    }

    private async Task ResolvePartyMediaMetadataAsync(
    Guid requestId,
    string url)
    {
        try
        {
            var metadata =
                await searchResolver
                    .GetVideoEntryAsync(
                        url,
                        CancellationToken.None)
                    .ConfigureAwait(false);

            pendingPartyMediaMetadata.Enqueue(
                (
                    requestId,
                    metadata
                ));
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[WatchParty] Could not resolve request metadata for {url}: " +
                $"{exception.Message}");

            pendingPartyMediaMetadata.Enqueue(
                (
                    requestId,
                    null
                ));
        }
    }

    private void DrawPartyPanel()
    {
        if (CurrentSession is null)
        {
            DrawLegacyPartyPanel();
            return;
        }

        if (stream.Mode is not (StreamMode.Hosting or StreamMode.Viewing))
        {
            DrawLegacyPartyPanel();
            return;
        }

        DrawPartyTabButtons();

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));

        switch (partyPanelTab)
        {
            case PartyPanelTab.Watching:
                DrawPartyWatchingTab();
                break;

            case PartyPanelTab.NowPlaying:
                DrawPartyNowPlayingTab();
                break;

            case PartyPanelTab.Chat:
                DrawPartyChatTab();
                break;
        }
    }

    private void DrawGameplayStreamOffer()
    {
        if (stream.Mode != StreamMode.Hosting)
        {
            return;
        }

        var engine =
            screenController.Engine;

        var gameplayActive =
            engine.IsPlayingSnes ||
            engine.IsPlayingGameBoy;

        var alreadyBroadcasting =
            engine.IsSnesBroadcasting ||
            engine.IsGameBoyBroadcasting;


        if (!gameplayActive ||
            alreadyBroadcasting ||
            gameplayStreamOfferDismissed)
        {
            return;
        }


        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f))
        using (var card =
            ImRaii.Child(
                "##gameplayStreamOffer",
                new Vector2(
                    -1f,
                    Ui(105f)),
                false))
        {
            if (!card)
            {
                return;
            }


            var drawList =
                ImGui.GetWindowDrawList();

            var min =
                ImGui.GetCursorScreenPos();

            var max =
                min +
                new Vector2(
                    ImGui.GetContentRegionAvail().X,
                    105f);


            drawList.AddRect(
                min,
                max,
                ImGui.GetColorU32(
                    new Vector4(
                        0.55f,
                        0.30f,
                        1f,
                        1f)),
                10f,
                ImDrawFlags.None,
                1.5f);


            using (ImRaii.PushColor(
                ImGuiCol.Text,
                Accent))
            {
                ImGui.Text(
                    "ⓘ   GAMEPLAY STREAM AVAILABLE");
            }


            ImGui.Spacing();


            ImGui.TextWrapped(
                "Games run locally unless you choose to stream them. Would you like to share your gameplay with this Watch Party?");


            ImGui.Dummy(
                new Vector2(
                    0f,
                    5f));


            if (ImGui.Button(
                    "Start Gameplay Stream"))
            {
                StartGameWatchPartyBroadcast();

                gameplayStreamOfferDismissed =
                    true;
            }


            ImGui.SameLine();


            if (ImGui.Button(
                    "Not Now"))
            {
                gameplayStreamOfferDismissed =
                    true;
            }
        }
    }

    private void DrawPartyHeaderCard()
    {
        var isHost =
            stream.Mode == StreamMode.Hosting;

        var hostName =
            isHost
                ? CurrentDisplayName ?? "You"
                : joinedHostDisplayName ?? "Host";

        // TEMP: room name is still visual-only until real room metadata exists.
        var roomName =
            $"{hostName}'s Watch Party";

        // TEMP: description backend field does not exist yet.
        const string roomDescription =
            "Just hanging out watching together.";

        var isPrivate =
            stream.IsPrivate;

        var cardHeight = Ui(150f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            14f))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.WindowPadding,
            UiVec(20f, 16f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.05f, 0.09f, 1f)))
        using (var card = ImRaii.Child(
            "##partyHeaderCard",
            new Vector2(-1f, cardHeight),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card)
            {
                return;
            }

            var cardPos =
                ImGui.GetWindowPos();

            var cardSize =
                ImGui.GetWindowSize();

            ImGui.GetWindowDrawList().AddRect(
                cardPos,
                cardPos + cardSize,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.40f)),
                14f,
                ImDrawFlags.RoundCornersAll,
                1.2f);

            //
            // Room title
            //
            ImGui.SetWindowFontScale(1.45f);

            ImGui.TextColored(
                Vector4.One,
                roomName);

            ImGui.SetWindowFontScale(1f);

            //
            // Leave button - always visible.
            //
            var leaveWidth = Ui(132f);

            ImGui.SetCursorPos(
                new Vector2(
                    ImGui.GetWindowWidth() - leaveWidth - Ui(18f),
                    Ui(14f)));

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.16f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(
                        Danger.X,
                        Danger.Y,
                        Danger.Z,
                        0.28f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(
                        Danger.X,
                        Danger.Y,
                        Danger.Z,
                        0.38f)))
            {
                if (ImGui.Button(
                    "Leave Watch Party",
                    new Vector2(leaveWidth, 32f)))
                {
                    LeaveStream();
                    partyChatItems.Clear();
                    return;
                }
            }

            //
            // Host identity row
            //
            ImGui.SetCursorPos(
                new Vector2(20f, 52f));

            if (isHost)
            {
                DrawAvatarChip(
                    CurrentSession.AvatarIcon,
                    CurrentSession.AvatarColorHex,
                    42,
                    CurrentSession.AvatarImageUrl);
            }
            else
            {
                // TEMP: the watch-party realtime roster currently does not
                // expose the host's Alpha Channel avatar.
                var avatarOrigin =
                    ImGui.GetCursorScreenPos();

                ImGui.GetWindowDrawList().AddCircleFilled(
                    avatarOrigin + new Vector2(21f, 21f),
                    21f,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.20f)));

                ImGui.SetCursorScreenPos(
                    avatarOrigin + new Vector2(12f, 11f));

                using (ImRaii.PushFont(
                    UiBuilder.IconFont))
                {
                    ImGui.TextColored(
                        Accent,
                        FontAwesomeIcon.User.ToIconString());
                }

                ImGui.SetCursorScreenPos(
                    avatarOrigin + new Vector2(42f, 0f));
            }

            ImGui.SameLine(0f, 10f);

            ImGui.BeginGroup();

            ImGui.TextColored(
                Vector4.One,
                $"Hosted by {hostName}");

            ImGui.SetWindowFontScale(0.88f);

            ImGui.TextColored(
                Good,
                $"● {stream.Roster.Length} watching");

            ImGui.SetWindowFontScale(1f);

            ImGui.EndGroup();

            //
            // Privacy
            //
            ImGui.SetCursorPos(
                new Vector2(
                    ImGui.GetWindowWidth() - 158f,
                    63f));

            if (isHost)
            {
                if (ImGui.Checkbox(
                    "Private party",
                    ref isPrivate))
                {
                    stream.IsPrivate =
                        isPrivate;
                }
            }
            else
            {
                ImGui.TextColored(
                    MutedText,
                    isPrivate
                        ? "Private party"
                        : "Public party");
            }

            //
            // Description
            //
            ImGui.SetCursorPos(
                new Vector2(20f, 112f));

            ImGui.TextColored(
                MutedText,
                roomDescription);

            if (isHost)
            {
                ImGui.SameLine(0f, 8f);

                using (ImRaii.PushFont(
                    UiBuilder.IconFont))
                {
                    ImGui.TextColored(
                        Accent,
                        FontAwesomeIcon.PencilAlt.ToIconString());
                }
            }
        }
    }

    private void DrawPartyTabButtons()
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        const float gap =
            8f;

        var tabWidth =
            (width -
             gap * 2f) /
            3f;

        // Host is separate from stream.Roster.
        var participantCount =
            stream.Roster.Length + 1;

        DrawPartyTabButton(
            PartyPanelTab.Watching,
            FontAwesomeIcon.UserFriends,
            $"Watch Party ({participantCount})",
            tabWidth);

        ImGui.SameLine(
            0f,
            gap);

        DrawPartyTabButton(
            PartyPanelTab.NowPlaying,
            FontAwesomeIcon.Tv,
            "Now Playing / TV",
            tabWidth);

        ImGui.SameLine(
            0f,
            gap);

        DrawPartyTabButton(
            PartyPanelTab.Chat,
            FontAwesomeIcon.Comments,
            "Party Chat & React",
            tabWidth);
    }

    private void DrawPartyTabButton(
     PartyPanelTab tab,
     FontAwesomeIcon icon,
     string label,
     float width)
    {
        var selected =
            partyPanelTab == tab;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                new Vector2(12f, 9f)))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            selected
                ? Accent
                : new Vector4(
                    0.045f,
                    0.05f,
                    0.085f,
                    1f))
            .Push(
                ImGuiCol.ButtonHovered,
                selected
                    ? AccentHover
                    : new Vector4(
                        0.075f,
                        0.065f,
                        0.13f,
                        1f))
            .Push(
                ImGuiCol.ButtonActive,
                selected
                    ? AccentActive
                    : new Vector4(
                        0.09f,
                        0.075f,
                        0.15f,
                        1f)))
        {
            //
            // Draw an invisible-label button first.
            //
            if (ImGui.Button(
                $"##partyTab_{tab}",
                new Vector2(width, 40f)))
            {
                partyPanelTab = tab;
            }

            var buttonMin =
                ImGui.GetItemRectMin();

            var buttonMax =
                ImGui.GetItemRectMax();

            var centerY =
                buttonMin.Y +
                (buttonMax.Y - buttonMin.Y) * 0.5f;

            var iconText =
                icon.ToIconString();

            Vector2 iconSize;

            //
            // Measure icon using the actual icon font.
            //
            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                iconSize =
                    ImGui.CalcTextSize(
                        iconText);
            }

            //
            // Measure label using the normal font.
            //
            Vector2 labelSize;

            using (ImRaii.PushFont(
                UiBuilder.DefaultFont))
            {
                labelSize =
                    ImGui.CalcTextSize(
                        label);
            }

            const float gap = 8f;

            var totalWidth =
                iconSize.X +
                gap +
                labelSize.X;

            var startX =
                buttonMin.X +
                ((buttonMax.X - buttonMin.X) - totalWidth) * 0.5f;

            //
            // Icon
            //
            ImGui.GetWindowDrawList().AddText(
                UiBuilder.IconFont,
                ImGui.GetFontSize(),
                new Vector2(
                    startX,
                    centerY - iconSize.Y * 0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                iconText);

            //
            // Normal label
            //
            ImGui.GetWindowDrawList().AddText(
                UiBuilder.DefaultFont,
                ImGui.GetFontSize(),
                new Vector2(
                    startX + iconSize.X + gap,
                    centerY - labelSize.Y * 0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                label);
        }
    }

    private static bool IsFfxivSoundMuted()
    {
        return Plugin.GameConfig.TryGet(
                   SystemConfigOption.IsSndMaster,
                   out uint muted)
               && muted != 0;
    }

    private static void SetFfxivSoundMuted(
        bool muted)
    {
        Plugin.GameConfig.Set(
            SystemConfigOption.IsSndMaster,
            muted ? 1u : 0u);
    }

    private void DrawPartyTvSpawnButton()
    {
        if (stream.Mode is not
            (StreamMode.Viewing or StreamMode.Hosting))
        {
            return;
        }

        var isHost =
            stream.Mode == StreamMode.Hosting;

        var tvSpawned =
            isHost
                ? screenController.Engine.IsActive
                : ViewerTvEnabled;

        var label =
            tvSpawned
                ? "Despawn TV"
                : "Spawn TV";

        var buttonColor =
            tvSpawned
                ? new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.62f)
                : Accent;

        var buttonHover =
            tvSpawned
                ? new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.78f)
                : AccentHover;

        var buttonActive =
            tvSpawned
                ? new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.90f)
                : AccentActive;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            9f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            buttonColor)
            .Push(
                ImGuiCol.ButtonHovered,
                buttonHover)
            .Push(
                ImGuiCol.ButtonActive,
                buttonActive))
        {
            if (!ImGui.Button(
                    label,
                    new Vector2(
                        170f,
                        40f)))
            {
                return;
            }

            // =====================================================
            // VIEWER
            // =====================================================

            if (!isHost)
            {
                if (ViewerTvEnabled)
                {
                    ViewerTvEnabled =
                        false;

                    video.Stop();
                }
                else
                {
                    ViewerTvEnabled =
                        true;

                    OnViewerTvSpawnRequested
                        ?.Invoke();
                }

                return;
            }

            // =====================================================
            // HOST
            // =====================================================

            var engine =
                screenController.Engine;

            if (!engine.IsActive)
            {
                engine.RespawnScreen();
                return;
            }

            // Don't let the host remove the TV while active
            // playback is running.
            if (queue.Current is not null)
            {
                var (_, _, isPaused) =
                    video.GetProgress();

                if (!isPaused)
                {
                    Plugin.ChatGui.Print(
                        "[Alpha Channel] Host can't despawn TV during playback. Pause playback first.");

                    return;
                }
            }

            engine.DespawnScreen();
        }
    }

    private void DrawPartyNowPlayingTab()
    {
        DrawGameplayStreamOffer();

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        var current =
            queue.Current;

        var (position, duration, isPaused) =
            video.GetProgress();

        var available =
            ImGui.GetContentRegionAvail();

        using (ImRaii.PushStyle(
            ImGuiStyleVar.WindowPadding,
            Vector2.Zero)
            .Push(
                ImGuiStyleVar.ItemSpacing,
                Vector2.Zero))
        using (var viewport =
            ImRaii.Child(
                "##partyNowPlayingViewport",
                available,
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!viewport)
            {
                return;
            }

            var width =
                ImGui.GetContentRegionAvail().X;

            var height =
                ImGui.GetContentRegionAvail().Y;

            var gap = Ui(12f);
            var footerHeight = Ui(50f);

            // =========================================================
            // ROW 1 — NOW PLAYING
            // =========================================================

            var heroHeight = Ui(225f);

            ImGui.SetCursorPos(
                Vector2.Zero);

            DrawPartyNowPlayingHero(
                current,
                position,
                duration,
                isPaused,
                width,
                heroHeight);

            // =========================================================
            // ROW 2 — UP NEXT / YOUR TV
            // =========================================================

            var secondRowHeight = Ui(156f);

            var secondRowY =
                heroHeight +
                gap;

            const float leftRatio = 0.52f;
            var leftWidth =
                (width - gap) *
                leftRatio;

            var rightWidth =
                width -
                leftWidth -
                gap;

            ImGui.SetCursorPos(
                new Vector2(
                    0f,
                    secondRowY));

            DrawPartyUpNextCard(
                leftWidth,
                secondRowHeight);

            ImGui.SetCursorPos(
                new Vector2(
                    leftWidth +
                    gap,
                    secondRowY));

            DrawPartyTvCard(
                current,
                isPaused,
                rightWidth,
                secondRowHeight);

            // =========================================================
            // ROW 3 — AUDIO / SCREEN PLACEMENT
            // =========================================================

            var thirdRowHeight = Ui(148f);

            var thirdRowY =
                secondRowY +
                secondRowHeight +
                gap;

            ImGui.SetCursorPos(
                new Vector2(
                    0f,
                    thirdRowY));

            DrawPartyAudioCard(
                leftWidth,
                thirdRowHeight);

            ImGui.SetCursorPos(
                new Vector2(
                    leftWidth +
                    gap,
                    thirdRowY));

            DrawPartyScreenPlacementCard(
                rightWidth,
                thirdRowHeight);

            // =========================================================
            // FIXED SESSION FOOTER
            //
            // Pinned to the bottom of this viewport. It no longer
            // contributes to page height or creates outer scrolling.
            // =========================================================
            ImGui.SetCursorPos(
                new Vector2(
                    0f,
                    MathF.Max(
                        0f,
                        height -
                        footerHeight)));

            DrawPartyNowPlayingFooter(
                width);
        }
    }

    private void DrawPartyNowPlayingHero(
        Video.VideoQueueEntry? current,
        float position,
        float duration,
        bool isPaused,
        float width,
        float height)
    {
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            12f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    18f,
                    15f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.04f,
                0.07f,
                1f)))
        using (var card =
            ImRaii.Child(
                "##partyNowPlayingHero",
                new Vector2(
                    width,
                    height),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card)
            {
                return;
            }

            var cardWidth =
                ImGui.GetContentRegionAvail().X;

            var syncWidth = Ui(220f);
            var dividerGap = Ui(20f);

            var mediaWidth =
                MathF.Max(
                    Ui(400f),
                    cardWidth -
                    syncWidth -
                    dividerGap);

            var cardScreenPos =
                ImGui.GetWindowPos();

            // =====================================================
            // LEFT — NOW PLAYING
            // =====================================================

            DrawSectionTitle(
                FontAwesomeIcon.PlayCircle,
                "Now Playing");

            ImGui.Dummy(
                new Vector2(
                    0f,
                    8f));

            if (current is null)
            {
                ImGui.SetWindowFontScale(
                    1.12f);

                ImGui.TextColored(
                    Vector4.One,
                    "Nothing is playing");

                ImGui.SetWindowFontScale(
                    1f);

                ImGui.Dummy(
                    new Vector2(
                        0f,
                        5f));

                ImGui.TextColored(
                    MutedText,
                    stream.Mode == StreamMode.Hosting
                        ? "Choose some media when you're ready."
                        : "Waiting for the host to start something.");
            }
            else
            {
                var origin =
                    ImGui.GetCursorScreenPos();

                var drawList =
                    ImGui.GetWindowDrawList();

                var thumbWidth = Ui(205f);
                var thumbHeight = Ui(115f);

                var thumbMin =
                    origin;

                var thumbMax =
                    origin +
                    new Vector2(
                        thumbWidth,
                        thumbHeight);

                drawList.AddRectFilled(
                    thumbMin,
                    thumbMax,
                    ImGui.GetColorU32(
                        new Vector4(
                            0.025f,
                            0.03f,
                            0.05f,
                            1f)),
                    9f);

                var thumbnail =
                    thumbnails.Get(
                        current.ThumbnailUrl);

                if (thumbnail is not null)
                {
                    drawList.AddImageRounded(
                        thumbnail.Handle,
                        thumbMin,
                        thumbMax,
                        Vector2.Zero,
                        Vector2.One,
                        uint.MaxValue,
                        9f);
                }
                else
                {
                    using (ImRaii.PushFont(
                        UiBuilder.IconFont))
                    {
                        var icon =
                            FontAwesomeIcon.Play
                                .ToIconString();

                        var iconSize =
                            ImGui.CalcTextSize(
                                icon);

                        drawList.AddText(
                            thumbMin +
                            (thumbMax -
                             thumbMin -
                             iconSize) *
                            0.5f,
                            ImGui.GetColorU32(
                                Accent),
                            icon);
                    }
                }

                var contentX =
                    origin.X +
                    thumbWidth +
                    18f;

                var contentWidth =
                    MathF.Max(
                        130f,
                        mediaWidth -
                        thumbWidth -
                        42f);

                // -------------------------------------------------
                // Title
                // -------------------------------------------------

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        contentX,
                        origin.Y + 2f));

                ImGui.SetWindowFontScale(
                    1.13f);

                ImGui.PushTextWrapPos(
                    ImGui.GetCursorPosX() +
                    contentWidth);

                ImGui.TextColored(
                    Vector4.One,
                    current.Title);

                ImGui.PopTextWrapPos();

                ImGui.SetWindowFontScale(
                    1f);

                // -------------------------------------------------
                // Source
                // -------------------------------------------------

                if (!string.IsNullOrWhiteSpace(
                        current.Source))
                {
                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            origin.Y + 54f));

                    ImGui.SetWindowFontScale(
                        0.82f);

                    ImGui.TextColored(
                        MutedText,
                        current.Source);

                    ImGui.SetWindowFontScale(
                        1f);
                }

                // -------------------------------------------------
                // Playback state / time
                // -------------------------------------------------

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        contentX,
                        origin.Y + 84f));

                using (ImRaii.PushFont(
                    UiBuilder.IconFont))
                {
                    ImGui.TextColored(
                        isPaused
                            ? Gold
                            : Good,
                        isPaused
                            ? FontAwesomeIcon.Pause
                                .ToIconString()
                            : FontAwesomeIcon.Play
                                .ToIconString());
                }

                ImGui.SameLine(
                    0f,
                    7f);

                ImGui.TextColored(
                    isPaused
                        ? Gold
                        : Good,
                    isPaused
                        ? "Paused"
                        : "Playing");

                ImGui.SameLine(
                    0f,
                    18f);

                ImGui.SetWindowFontScale(
                    0.80f);

                ImGui.TextColored(
                    MutedText,
                    duration > 0f
                        ? $"{FormatTime(position)} / {FormatTime(duration)}"
                        : "Live");

                ImGui.SetWindowFontScale(
                    1f);

                // =================================================
                // TRANSPORT / PROGRESS
                // =================================================

                var progressY =
                    origin.Y +
                    thumbHeight +
                    Ui(43f);

                var transportSize = Ui(30f);
                var transportGap = Ui(7f);

                var controlsWidth =
                    transportSize * 3f +
                    transportGap * 2f;

                var controlsX =
                    cardScreenPos.X +
                    mediaWidth -
                    controlsWidth -
                    18f;

                var controlsY =
                    progressY -
                    transportSize -
                    8f;

                var isHost =
                    stream.Mode ==
                    StreamMode.Hosting;

                // -------------------------------------------------
                // Play / Pause
                // -------------------------------------------------

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        controlsX,
                        controlsY));

                DrawPartyTransportPlayPauseButton(
                    "partyPlayPause",
                    transportSize,
                    !isHost,
                    isPaused);

                // PLACEHOLDER:
                // connect playback action later.

                // -------------------------------------------------
                // Restart
                // -------------------------------------------------

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        controlsX +
                        transportSize +
                        transportGap,
                        controlsY));

                DrawPartyTransportRestartButton(
                    "partyRestart",
                    transportSize,
                    !isHost,
                    "Restart video");

                // PLACEHOLDER:
                // restart current video later.

                // -------------------------------------------------
                // Next
                // -------------------------------------------------

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        controlsX +
                        (transportSize +
                         transportGap) *
                        2f,
                        controlsY));

                DrawPartyTransportNextButton(
                    "partyNext",
                    transportSize,
                    !isHost,
                    "Next video");

                // PLACEHOLDER:
                // queue advance later.

                // -------------------------------------------------
                // Progress
                // -------------------------------------------------

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        origin.X,
                        progressY));

                var progress =
                    duration > 0f
                        ? Math.Clamp(
                            position /
                            duration,
                            0f,
                            1f)
                        : 1f;

                ImGui.ProgressBar(
                    progress,
                    new Vector2(
                        mediaWidth -
                        18f,
                        6f),
                    string.Empty);
            }

            // =====================================================
            // DIVIDER
            // =====================================================

            var dividerX =
                cardScreenPos.X +
                mediaWidth;

            ImGui.GetWindowDrawList()
                .AddLine(
                    new Vector2(
                        dividerX,
                        cardScreenPos.Y +
                        17f),
                    new Vector2(
                        dividerX,
                        cardScreenPos.Y +
                        height -
                        17f),
                    ImGui.GetColorU32(
                        new Vector4(
                            1f,
                            1f,
                            1f,
                            0.08f)),
                    1f);

            // =====================================================
            // RIGHT — PLAYBACK SYNC
            // =====================================================

            ImGui.SetCursorScreenPos(
                new Vector2(
                    dividerX +
                    20f,
                    cardScreenPos.Y +
                    20f));

            ImGui.BeginGroup();

            ImGui.SetWindowFontScale(
                0.80f);

            ImGui.TextColored(
                Accent,
                "PLAYBACK SYNC");

            ImGui.SetWindowFontScale(
                1f);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    10f));

            ImGui.TextColored(
                Good,
                "●");

            ImGui.SameLine(
                0f,
                7f);

            ImGui.SetWindowFontScale(
                1.10f);

            ImGui.TextColored(
                Vector4.One,
                stream.Mode ==
                StreamMode.Hosting
                    ? "Hosting playback"
                    : "In sync");

            ImGui.SetWindowFontScale(
                1f);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    4f));

            ImGui.SetWindowFontScale(
                0.76f);

            ImGui.TextColored(
                MutedText,
                stream.Mode ==
                StreamMode.Hosting
                    ? "Everyone follows your playback."
                    : "You're synced with the host.");

            ImGui.SetWindowFontScale(
                1f);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    17f));

            using (ImRaii.Disabled(
                stream.Mode ==
                StreamMode.Hosting))
            {
                if (ImGui.Button(
                    "Re-sync##partyResync",
                    new Vector2(
                        124f,
                        34f)))
                {
                    // PLACEHOLDER:
                    // apply/request latest host timestamp.
                }
            }

            if (ImGui.IsItemHovered(
                    ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(
                    stream.Mode ==
                    StreamMode.Hosting
                        ? "The host is the playback source."
                        : "Video out of sync with host? Press here to attempt a re-sync");
            }

            ImGui.EndGroup();
        }
    }



    private void DrawPartyTransportPlayPauseButton(
     string id,
     float size,
     bool disabled,
     bool isPaused)
    {
        using (ImRaii.Disabled(
            disabled))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            size * 0.5f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            new Vector4(
                0.075f,
                0.085f,
                0.14f,
                1f))
            .Push(
                ImGuiCol.ButtonHovered,
                Accent)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            ImGui.Button(
                $"##{id}",
                new Vector2(
                    size,
                    size));
        }

        var min =
            ImGui.GetItemRectMin();

        var max =
            ImGui.GetItemRectMax();

        var center =
            min +
            (max - min) *
            0.5f;

        var drawList =
            ImGui.GetWindowDrawList();

        var color =
            ImGui.GetColorU32(
                disabled
                    ? MutedText
                    : Vector4.One);

        if (isPaused)
        {
            // Play triangle.
            drawList.AddTriangleFilled(
                new Vector2(
                    center.X - 4f,
                    center.Y - 6f),
                new Vector2(
                    center.X - 4f,
                    center.Y + 6f),
                new Vector2(
                    center.X + 6f,
                    center.Y),
                color);
        }
        else
        {
            // Pause bars.
            drawList.AddRectFilled(
                new Vector2(
                    center.X - 5f,
                    center.Y - 6f),
                new Vector2(
                    center.X - 1.5f,
                    center.Y + 6f),
                color,
                1f);

            drawList.AddRectFilled(
                new Vector2(
                    center.X + 1.5f,
                    center.Y - 6f),
                new Vector2(
                    center.X + 5f,
                    center.Y + 6f),
                color,
                1f);
        }

        if (ImGui.IsItemHovered(
                ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var tooltip =
                isPaused
                    ? "Play"
                    : "Pause";

            ImGui.SetTooltip(
                disabled
                    ? $"{tooltip} — controlled by the host"
                    : tooltip);
        }
    }

    private void DrawPartyTransportRestartButton(
        string id,
        float size,
        bool disabled,
        string tooltip)
    {
        using (ImRaii.Disabled(
            disabled))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            size * 0.5f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            new Vector4(
                0.075f,
                0.085f,
                0.14f,
                1f))
            .Push(
                ImGuiCol.ButtonHovered,
                Accent)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            ImGui.Button(
                $"##{id}",
                new Vector2(
                    size,
                    size));
        }

        var min =
            ImGui.GetItemRectMin();

        var max =
            ImGui.GetItemRectMax();

        var center =
            min +
            (max - min) *
            0.5f;

        var drawList =
            ImGui.GetWindowDrawList();

        var color =
            ImGui.GetColorU32(
                disabled
                    ? MutedText
                    : Vector4.One);

        // ---------------------------------------------------------
        // Restart icon:
        //
        // |◀
        //
        // Simple and unmistakable:
        // jump back to the beginning.
        // ---------------------------------------------------------

        drawList.AddLine(
            new Vector2(
                center.X - 5f,
                center.Y - 6f),
            new Vector2(
                center.X - 5f,
                center.Y + 6f),
            color,
            2f);

        drawList.AddTriangleFilled(
            new Vector2(
                center.X + 5f,
                center.Y - 6f),
            new Vector2(
                center.X + 5f,
                center.Y + 6f),
            new Vector2(
                center.X - 3f,
                center.Y),
            color);

        if (ImGui.IsItemHovered(
                ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                disabled
                    ? $"{tooltip} — controlled by the host"
                    : tooltip);
        }
    }

    private void DrawPartyTransportNextButton(
        string id,
        float size,
        bool disabled,
        string tooltip)
    {
        using (ImRaii.Disabled(
            disabled))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            size * 0.5f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            new Vector4(
                0.075f,
                0.085f,
                0.14f,
                1f))
            .Push(
                ImGuiCol.ButtonHovered,
                Accent)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            ImGui.Button(
                $"##{id}",
                new Vector2(
                    size,
                    size));
        }

        var min =
            ImGui.GetItemRectMin();

        var max =
            ImGui.GetItemRectMax();

        var drawList =
            ImGui.GetWindowDrawList();

        var center =
            min +
            (max - min) *
            0.5f;

        var color =
            ImGui.GetColorU32(
                disabled
                    ? MutedText
                    : Vector4.One);

        // Draw a proper "next track" icon manually:
        // triangle + vertical stop line.
        const float triangleHalfHeight = 5f;
        const float triangleWidth = 7f;

        var triangleCenter =
            new Vector2(
                center.X - 2f,
                center.Y);

        drawList.AddTriangleFilled(
            new Vector2(
                triangleCenter.X -
                triangleWidth * 0.5f,
                triangleCenter.Y -
                triangleHalfHeight),
            new Vector2(
                triangleCenter.X -
                triangleWidth * 0.5f,
                triangleCenter.Y +
                triangleHalfHeight),
            new Vector2(
                triangleCenter.X +
                triangleWidth * 0.5f,
                triangleCenter.Y),
            color);

        drawList.AddLine(
            new Vector2(
                center.X + 5f,
                center.Y - 5f),
            new Vector2(
                center.X + 5f,
                center.Y + 5f),
            color,
            2f);

        if (ImGui.IsItemHovered(
                ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                disabled
                    ? $"{tooltip} — controlled by the host"
                    : tooltip);
        }
    }

    private void DrawPartyUpNextCard(
        float width,
        float height)
    {
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            12f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    18f,
                    14f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.04f,
                0.07f,
                1f)))
        using (var card =
            ImRaii.Child(
                "##partyUpNextCard",
                new Vector2(
                    width,
                    height),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card)
            {
                return;
            }

            ImGui.TextColored(
                Accent,
                "UP NEXT");

            var queueCount =
                queue.Entries.Count;

            // =====================================================
            // Queue count badge
            // =====================================================

            var queueLabel =
                $"Queue ({queueCount})";

            var queueTextSize =
                ImGui.CalcTextSize(
                    queueLabel);

            var badgeSize =
                new Vector2(
                    queueTextSize.X +
                    18f,
                    25f);

            var badgeMin =
                ImGui.GetWindowPos() +
                new Vector2(
                    ImGui.GetWindowWidth() -
                    badgeSize.X -
                    16f,
                    10f);

            ImGui.GetWindowDrawList()
                .AddRectFilled(
                    badgeMin,
                    badgeMin +
                    badgeSize,
                    ImGui.GetColorU32(
                        new Vector4(
                            0.085f,
                            0.095f,
                            0.15f,
                            1f)),
                    12f);

            ImGui.GetWindowDrawList()
                .AddText(
                    badgeMin +
                    (badgeSize -
                     queueTextSize) *
                    0.5f,
                    ImGui.GetColorU32(
                        MutedText),
                    queueLabel);

            ImGui.SetCursorPos(
                new Vector2(
                    18f,
                    48f));

            if (queueCount == 0)
            {
                ImGui.TextColored(
                    Vector4.One,
                    "Nothing queued next");

                ImGui.Dummy(
                    new Vector2(
                        0f,
                        3f));

                ImGui.SetWindowFontScale(
                    0.76f);

                ImGui.TextColored(
                    MutedText,
                    "New party media will appear here.");

                ImGui.SetWindowFontScale(
                    1f);

                return;
            }

            // We deliberately expose only one queued item here.
            var next =
                queue.Entries[0];

            var origin =
                ImGui.GetCursorScreenPos();

            var drawList =
                ImGui.GetWindowDrawList();

            var thumbWidth = Ui(132f);
            var thumbHeight = Ui(76f);

            var thumbMin =
                origin;

            var thumbMax =
                origin +
                new Vector2(
                    thumbWidth,
                    thumbHeight);

            drawList.AddRectFilled(
                thumbMin,
                thumbMax,
                ImGui.GetColorU32(
                    new Vector4(
                        0.025f,
                        0.03f,
                        0.05f,
                        1f)),
                8f);

            var thumbnail =
                thumbnails.Get(
                    next.ThumbnailUrl);

            if (thumbnail is not null)
            {
                drawList.AddImageRounded(
                    thumbnail.Handle,
                    thumbMin,
                    thumbMax,
                    Vector2.Zero,
                    Vector2.One,
                    uint.MaxValue,
                    8f);
            }

            var contentX =
                origin.X +
                thumbWidth +
                14f;

            ImGui.SetCursorScreenPos(
                new Vector2(
                    contentX,
                    origin.Y));

            ImGui.SetWindowFontScale(
                1.00f);

            ImGui.PushTextWrapPos(
                ImGui.GetCursorPosX() +
                MathF.Max(
                    80f,
                    width -
                    thumbWidth -
                    70f));

            ImGui.TextColored(
                Vector4.One,
                next.Title);

            ImGui.PopTextWrapPos();

            ImGui.SetWindowFontScale(
                1f);

            ImGui.SetCursorScreenPos(
                new Vector2(
                    contentX,
                    origin.Y + 48f));

            ImGui.SetWindowFontScale(
                0.76f);

            var source =
                string.IsNullOrWhiteSpace(
                    next.Source)
                    ? "Media"
                    : next.Source;

            var metadata =
                next.Duration is { } nextDuration
                    ? $"{source}  •  {FormatTime((float)nextDuration.TotalSeconds)}"
                    : source;

            ImGui.TextColored(
                MutedText,
                metadata);

            ImGui.SetWindowFontScale(
                1f);
        }
    }

    private void DrawPartyTvCard(
        Video.VideoQueueEntry? current,
        bool isPaused,
        float width,
        float height)
    {
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            12f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    18f,
                    14f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.04f,
                0.07f,
                1f)))
        using (var card =
            ImRaii.Child(
                "##partyTvCard",
                new Vector2(
                    width,
                    height),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card)
            {
                return;
            }

            ImGui.TextColored(
                Accent,
                "YOUR TV");

            var tvSpawned =
                stream.Mode == StreamMode.Hosting
                    ? screenController.Engine.IsActive
                    : ViewerTvEnabled;

            var statusText =
                !tvSpawned
                    ? "TV not spawned"
                    : current is null
                        ? "TV is ready"
                        : isPaused
                            ? "TV is paused"
                            : "TV is playing";

            var windowOrigin =
                ImGui.GetWindowPos();

            var drawList =
                ImGui.GetWindowDrawList();

            // =====================================================
            // Larger centered TV icon
            // =====================================================

            var iconCenter =
                windowOrigin +
                new Vector2(
                    56f,
                    91f);

            const float circleRadius =
                31f;

            drawList.AddCircle(
                iconCenter,
                circleRadius,
                ImGui.GetColorU32(
                    tvSpawned
                        ? new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.80f)
                        : new Vector4(
                            1f,
                            1f,
                            1f,
                            0.12f)),
                0,
                2f);

            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                ImGui.SetWindowFontScale(
                    1.35f);

                var glyph =
                    FontAwesomeIcon.Tv
                        .ToIconString();

                var glyphSize =
                    ImGui.CalcTextSize(
                        glyph);

                drawList.AddText(
                    iconCenter -
                    glyphSize *
                    0.5f,
                    ImGui.GetColorU32(
                        tvSpawned
                            ? Accent
                            : MutedText),
                    glyph);

                ImGui.SetWindowFontScale(
                    1f);
            }

            // =====================================================
            // State
            // =====================================================

            ImGui.SetCursorPos(
                new Vector2(
                    104f,
                    58f));

            ImGui.TextColored(
                tvSpawned
                    ? Good
                    : MutedText,
                tvSpawned
                    ? "●"
                    : "○");

            ImGui.SameLine(
                0f,
                6f);

            ImGui.TextColored(
                Vector4.One,
                statusText);

            ImGui.SetCursorPos(
                new Vector2(
                    104f,
                    84f));

            ImGui.SetWindowFontScale(
                0.76f);

            ImGui.TextColored(
                MutedText,
                tvSpawned
                    ? "Your watch-party screen is active."
                    : "Spawn your TV to start watching.");

            ImGui.SetWindowFontScale(
                1f);

            // =====================================================
            // Primary TV action
            // =====================================================

            ImGui.SetCursorPos(
                new Vector2(
                    ImGui.GetWindowWidth() -
                    188f,
                    72f));

            DrawPartyTvSpawnButton();
        }
    }

    private void DrawPartyAudioCard(
        float width,
        float height)
    {
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            12f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    18f,
                    14f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.04f,
                0.07f,
                1f)))
        using (var card =
            ImRaii.Child(
                "##partyAudioCard",
                new Vector2(
                    width,
                    height),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card)
            {
                return;
            }

            ImGui.TextColored(
                Accent,
                "AUDIO");

            var volume =
                Plugin.Cfg.Volume;

            // =====================================================
            // Volume label + warning
            // =====================================================

            ImGui.SetCursorPos(
                new Vector2(
                    18f,
                    39f));

            ImGui.TextColored(
                Vector4.One,
                "TV Volume");

            if (volume > 100)
            {
                ImGui.SameLine(
                    0f,
                    10f);

                ImGui.SetWindowFontScale(
                    0.68f);

                ImGui.TextColored(
                    Gold,
                    "Volume levels higher than 100% may distort audio quality");

                ImGui.SetWindowFontScale(
                    1f);
            }

            var percent =
                $"{volume}%";

            var percentSize =
                ImGui.CalcTextSize(
                    percent);

            ImGui.SetCursorPos(
                new Vector2(
                    ImGui.GetWindowWidth() -
                    percentSize.X -
                    18f,
                    39f));

            ImGui.TextColored(
                volume > 100
                    ? Gold
                    : Vector4.One,
                percent);

            // =====================================================
            // Slider
            // =====================================================

            ImGui.SetCursorPos(
                new Vector2(
                    18f,
                    61f));

            ImGui.SetNextItemWidth(
                ImGui.GetWindowWidth() -
                36f);

            if (ImGui.SliderInt(
                "##partyAudioVolume",
                ref volume,
                0,
                130,
                ""))
            {
                Plugin.Cfg.Volume =
                    volume;

                video.SetVolume(
                    Plugin.Cfg.Muted
                        ? 0
                        : volume);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                Plugin.Cfg.Save();
            }

            // =====================================================
            // Mute controls
            //
            // More breathing room below the volume slider.
            // =====================================================

            ImGui.SetCursorPos(
                new Vector2(
                    18f,
                    103f));

            var muted =
                Plugin.Cfg.Muted;

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
            {
                if (ImGui.Button(
                    muted
                        ? "Unmute TV"
                        : "Mute TV",
                    new Vector2(
                        128f,
                        32f)))
                {
                    muted =
                        !muted;

                    Plugin.Cfg.Muted =
                        muted;

                    video.SetVolume(
                        muted
                            ? 0
                            : Plugin.Cfg.Volume);

                    Plugin.Cfg.Save();
                }

                ImGui.SameLine(
                    0f,
                    12f);

                var ffxivMuted =
                    IsFfxivSoundMuted();

                if (ImGui.Button(
                    ffxivMuted
                        ? "Restore FFXIV Sounds"
                        : "Mute FFXIV Sounds",
                    new Vector2(
                        170f,
                        32f)))
                {
                    SetFfxivSoundMuted(
                        !ffxivMuted);
                }
            }
        }
    }

    private void DrawPartyScreenPlacementCard(
        float width,
        float height)
    {
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            12f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    18f,
                    14f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.04f,
                0.07f,
                1f)))
        using (var card =
            ImRaii.Child(
                "##partyScreenPlacementCard",
                new Vector2(
                    width,
                    height),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card)
            {
                return;
            }

            ImGui.TextColored(
                Accent,
                "SCREEN PLACEMENT");

            var isHost =
                stream.Mode ==
                StreamMode.Hosting;

            var syncEnabled =
                isHost
                    ? true
                    : partySyncTvPlacement;

            // =====================================================
            // Placement toggle
            // =====================================================

            ImGui.SetCursorPos(
                new Vector2(
                    18f,
                    48f));

            DrawPartyPlacementToggle(
                ref syncEnabled,
                isHost);

            if (!isHost)
            {
                partySyncTvPlacement =
                    syncEnabled;
            }

            ImGui.SameLine(
                0f,
                10f);

            ImGui.SetCursorPosY(
                47f);

            ImGui.TextColored(
                Vector4.One,
                "Match TV position & size with host");

            ImGui.SetCursorPos(
                new Vector2(
                    18f,
                    78f));

            ImGui.SetWindowFontScale(
                0.76f);

            ImGui.TextColored(
                MutedText,
                isHost
                    ? "Disable to manually resize / position the TV"
                    : partySyncTvPlacement
                        ? "Disable to manually resize / position the TV"
                        : "Placement sync is disabled.");

            ImGui.SetWindowFontScale(
                1f);

            var canOpenSettings =
                isHost ||
                !partySyncTvPlacement;

            ImGui.SetCursorPos(
                new Vector2(
                    ImGui.GetWindowWidth() -
                    166f,
                    99f));

            using (ImRaii.Disabled(
                !canOpenSettings))
            {
                if (ImGui.Button(
                    "Screen Settings",
                    new Vector2(
                        148f,
                        34f)))
                {
                    // PLACEHOLDER:
                    // navigate directly to Screen settings.
                }
            }

            if (!canOpenSettings &&
                ImGui.IsItemHovered(
                    ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(
                    "Disable TV position sync to customize your screen.");
            }
        }
    }

    private void DrawPartyPlacementToggle(
        ref bool value,
        bool disabled)
    {
        var width = Ui(38f);
        var height = Ui(20f);

        var origin =
            ImGui.GetCursorScreenPos();

        using (ImRaii.Disabled(
            disabled))
        {
            if (ImGui.InvisibleButton(
                    "##partyPlacementToggle",
                    new Vector2(
                        width,
                        height)))
            {
                value =
                    !value;
            }
        }

        var drawList =
            ImGui.GetWindowDrawList();

        var fill =
            value
                ? new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    disabled
                        ? 0.45f
                        : 1f)
                : new Vector4(
                    0.10f,
                    0.11f,
                    0.17f,
                    1f);

        drawList.AddRectFilled(
            origin,
            origin +
            new Vector2(
                width,
                height),
            ImGui.GetColorU32(
                fill),
            height * 0.5f);

        var knobX =
            value
                ? origin.X +
                  width -
                  height * 0.5f
                : origin.X +
                  height * 0.5f;

        drawList.AddCircleFilled(
            new Vector2(
                knobX,
                origin.Y +
                height * 0.5f),
            7f,
            ImGui.GetColorU32(
                disabled
                    ? new Vector4(
                        0.75f,
                        0.75f,
                        0.78f,
                        1f)
                    : Vector4.One));
    }

    private void DrawPartyNowPlayingFooter(
        float width)
    {
        const float footerHeight =
            50f;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            12f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    18f,
                    9f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.04f,
                0.07f,
                1f)))
        using (var footer =
            ImRaii.Child(
                "##partyNowPlayingFooter",
                new Vector2(
                    width,
                    footerHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!footer)
            {
                return;
            }

            var isHost =
                stream.Mode ==
                StreamMode.Hosting;

            var hostName =
                isHost
                    ? CurrentDisplayName ??
                      "You"
                    : joinedHostDisplayName ??
                      "Host";

            ImGui.SetCursorPosY(
                16f);

            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    MutedText,
                    FontAwesomeIcon.UserFriends
                        .ToIconString());
            }

            ImGui.SameLine(
                0f,
                7f);

            ImGui.TextColored(
                MutedText,
                $"{stream.Roster.Length} watching");

            ImGui.SameLine(
                0f,
                12f);

            ImGui.TextColored(
                MutedText,
                "•");

            ImGui.SameLine(
                0f,
                12f);

            ImGui.TextColored(
                MutedText,
                $"Host: {hostName}");

            var actionLabel =
                isHost
                    ? "End Watch Party"
                    : "Leave Watch Party";

            const float actionWidth =
                154f;

            ImGui.SetCursorPos(
                new Vector2(
                    ImGui.GetWindowWidth() -
                    actionWidth -
                    12f,
                    8f));

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    isHost
                        ? 0.24f
                        : 0.12f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(
                        Danger.X,
                        Danger.Y,
                        Danger.Z,
                        0.34f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(
                        Danger.X,
                        Danger.Y,
                        Danger.Z,
                        0.46f)))
            {
                if (ImGui.Button(
                    actionLabel,
                    new Vector2(
                        actionWidth,
                        34f)))
                {
                    // Host confirmation can be added before shipping.
                    LeaveStream();

                    partyChatItems.Clear();
                }
            }
        }
    }

    private void DrawPartyChatTab()
    {
        DrainPartyChat();

        // =========================================================
        // Fixed Chat viewport
        //
        // The tab itself never scrolls. Only the chat feed inside
        // DrawPartyChatFeed() is allowed to scroll.
        // =========================================================

        var viewportSize =
            ImGui.GetContentRegionAvail();

        using (ImRaii.PushStyle(
            ImGuiStyleVar.WindowPadding,
            Vector2.Zero)
            .Push(
                ImGuiStyleVar.ItemSpacing,
                Vector2.Zero))
        using (var viewport =
            ImRaii.Child(
                "##partyChatViewport",
                viewportSize,
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!viewport)
            {
                return;
            }

            var dockHeight = Ui(68f);
            var gap = Ui(8f);

            var width =
                ImGui.GetContentRegionAvail().X;

            var height =
                ImGui.GetContentRegionAvail().Y;

            var feedHeight =
                MathF.Max(
                    180f,
                    height -
                    dockHeight -
                    gap);

            // =====================================================
            // Chat feed
            // =====================================================

            ImGui.SetCursorPos(
                Vector2.Zero);

            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                12f)
                .Push(
                    ImGuiStyleVar.WindowPadding,
                    new Vector2(
                        16f,
                        14f)))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    0.035f,
                    0.04f,
                    0.07f,
                    1f)))
            using (var chatPanel =
                ImRaii.Child(
                    "##partyChatPanel",
                    new Vector2(
                        width,
                        feedHeight),
                    false,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (chatPanel)
                {
                    DrawPartyChatFeed();
                }
            }

            // =====================================================
            // Composer — explicitly pinned to the bottom
            // =====================================================

            ImGui.SetCursorPos(
                new Vector2(
                    0f,
                    height -
                    dockHeight));

            DrawPartyChatComposer();
        }
    }

    private void DrawPartyWatchingTab()
    {
        // =========================================================
        // WATCH PARTY OVERVIEW
        //
        // Host is not part of stream.Roster, so every visible room
        // count on this page is:
        //
        //     host + viewers
        // =========================================================

        var participantCount =
            stream.Roster.Length + 1;

        DrawPartyHeaderCard();

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        // =========================================================
        // PARTICIPANTS PANEL
        // =========================================================

        var available =
            ImGui.GetContentRegionAvail();

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            12f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    18f,
                    16f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.04f,
                0.07f,
                1f)))
        using (var watchingPanel =
            ImRaii.Child(
                "##partyWatchingPanel",
                new Vector2(
                    -1f,
                    available.Y),
                false,
                ImGuiWindowFlags.None))
        {
            if (!watchingPanel)
            {
                return;
            }

            // -----------------------------------------------------
            // Single heading.
            // No duplicate DrawRoster title underneath.
            // -----------------------------------------------------

            DrawSectionTitle(
                FontAwesomeIcon.UserFriends,
                $"Watching ({participantCount})");

            ImGui.Dummy(
                new Vector2(
                    0f,
                    5f));

            ImGui.SetWindowFontScale(
                0.82f);

            ImGui.TextColored(
                MutedText,
                "Members currently in this Watch Party.");

            ImGui.SetWindowFontScale(
                1f);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    14f));

            // =====================================================
            // HOST
            // =====================================================

            var localIsHost =
                stream.Mode ==
                StreamMode.Hosting;

            var hostName =
                localIsHost
                    ? CurrentDisplayName ??
                      CurrentSession?.DisplayName ??
                      "You"
                    : joinedHostDisplayName ??
                      "Host";

            // When we're the host, use the signed-in session
            // immediately.
            //
            // When we're a viewer, StreamClient.HostId is the host's
            // real account ID after StreamJoined, so the normal
            // Watch Party server-avatar lookup can resolve it.
            var hostUserId =
                localIsHost
                    ? CurrentSession?.AccountId ??
                      string.Empty
                    : stream.HostId ??
                      string.Empty;

            ImGui.PushID(
                "##partyHostParticipant");

            DrawPartyMemberRow(
                userId: hostUserId,
                displayName: hostName,
                isHost: true,
                showManagementActions: false,
                onMakeHost: null);

            ImGui.PopID();

            // =====================================================
            // VIEWERS
            // =====================================================

            foreach (var participant in
                     stream.Roster)
            {
                ImGui.Dummy(
                    new Vector2(
                        0f,
                        7f));

                ImGui.PushID(
                    participant.UserId);

                DrawPartyMemberRow(
                    userId: participant.UserId,
                    displayName:
                        string.IsNullOrWhiteSpace(
                            participant.DisplayName)
                            ? "Viewer"
                            : participant.DisplayName,
                    isHost: false,
                    showManagementActions:
                        localIsHost,
                    onMakeHost:
                        () =>
                        {
                            _ =
                                stream.TransferHostAsync(
                                    participant.UserId);
                        });

                ImGui.PopID();
            }
        }
    }

    private void DrawPartyMemberRow(
        string userId,
        string displayName,
        bool isHost,
        bool showManagementActions,
        Action? onMakeHost)
    {
        var rowHeight = Ui(64f);
        var avatarSize = Ui(40f);
        var leftPadding = Ui(12f);
        var contentGap = Ui(12f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            9f))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.045f,
                0.055f,
                0.095f,
                1f)))
        using (var row =
            ImRaii.Child(
                "##partyMemberRow",
                new Vector2(
                    -1f,
                    rowHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!row)
            {
                return;
            }

            var origin =
                ImGui.GetCursorScreenPos();

            var rowWidth =
                ImGui.GetWindowWidth();

            var drawList =
                ImGui.GetWindowDrawList();

            // =====================================================
            // AVATAR
            // =====================================================

            var avatarMin =
                origin +
                new Vector2(
                    leftPadding,
                    (rowHeight -
                     avatarSize) *
                    0.5f);

            var drewAvatar =
                false;

            // -----------------------------------------------------
            // Local host:
            // don't round-trip to the server for our own row.
            // -----------------------------------------------------

            if (isHost &&
                stream.Mode ==
                StreamMode.Hosting &&
                CurrentSession is not null)
            {
                DrawAvatarAt(
                    avatarMin,
                    CurrentSession.AvatarIcon,
                    CurrentSession.AvatarColorHex,
                    avatarSize,
                    CurrentSession.AvatarImageUrl);

                drewAvatar =
                    true;
            }
            else
            {
                // -------------------------------------------------
                // Everyone else:
                // exact same server-avatar route used by Party Chat.
                // -------------------------------------------------

                if (!string.IsNullOrWhiteSpace(
                        userId))
                {
                    EnsurePartyAvatarLoaded(
                        userId,
                        displayName);

                    if (partyAvatarCache.TryGetValue(
                            userId,
                            out var avatar))
                    {
                        DrawAvatarAt(
                            avatarMin,
                            avatar.AvatarIcon,
                            avatar.AvatarColorHex,
                            avatarSize,
                            avatar.AvatarImageUrl);

                        drewAvatar =
                            true;
                    }
                }
            }

            // -----------------------------------------------------
            // Fallback initials while the network avatar is loading.
            // -----------------------------------------------------

            if (!drewAvatar)
            {
                var avatarCenter =
                    avatarMin +
                    new Vector2(
                        avatarSize * 0.5f,
                        avatarSize * 0.5f);

                drawList.AddCircleFilled(
                    avatarCenter,
                    avatarSize * 0.5f,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.18f)));

                drawList.AddCircle(
                    avatarCenter,
                    avatarSize * 0.5f,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.55f)),
                    0,
                    1.2f);

                var initial =
                    string.IsNullOrWhiteSpace(
                        displayName)
                        ? "?"
                        : displayName
                            .Trim()[0]
                            .ToString()
                            .ToUpperInvariant();

                var initialSize =
                    ImGui.CalcTextSize(
                        initial);

                drawList.AddText(
                    avatarCenter -
                    initialSize * 0.5f,
                    ImGui.GetColorU32(
                        Vector4.One),
                    initial);
            }

            // =====================================================
            // NAME
            // =====================================================

            var nameX =
                avatarMin.X +
                avatarSize +
                contentGap;

            var nameY =
                origin.Y +
                16f;

            ImGui.SetCursorScreenPos(
                new Vector2(
                    nameX,
                    nameY));

            ImGui.SetWindowFontScale(
                0.98f);

            ImGui.TextColored(
                isHost
                    ? AccentHover
                    : Vector4.One,
                displayName);

            ImGui.SetWindowFontScale(
                1f);

            // =====================================================
            // DEVELOPER BADGE
            //
            // Same visual language as the Host badge.
            // =====================================================

            if (UserRoles.IsDeveloper(displayName))
            {
                ImGui.SameLine(
                    0f,
                    8f);

                const string badgeText =
                    "Developer";

                var badgeTextSize =
                    ImGui.CalcTextSize(
                        badgeText);

                const float badgePadX =
                    7f;

                const float badgePadY =
                    3f;

                var badgeMin =
                    ImGui.GetCursorScreenPos();

                var badgeSize =
                    new Vector2(
                        badgeTextSize.X +
                        badgePadX * 2f,
                        badgeTextSize.Y +
                        badgePadY * 2f);

                drawList.AddRectFilled(
                    badgeMin,
                    badgeMin +
                    badgeSize,
                    ImGui.GetColorU32(
                        new Vector4(
                            0.55f,
                            0.30f,
                            1f,
                            0.28f)),
                    6f);

                drawList.AddRect(
                    badgeMin,
                    badgeMin +
                    badgeSize,
                    ImGui.GetColorU32(
                        new Vector4(
                            0.65f,
                            0.45f,
                            1f,
                            0.55f)),
                    6f);

                drawList.AddText(
                    badgeMin +
                    new Vector2(
                        badgePadX,
                        badgePadY - 1f),
                    ImGui.GetColorU32(
                        Vector4.One),
                    badgeText);

                ImGui.Dummy(
                    badgeSize);
            }

            // =====================================================
            // HOST BADGE
            //
            // Same visual language as the Party Chat Host badge.
            // =====================================================

            if (isHost)
            {
                ImGui.SameLine(
                    0f,
                    8f);

                const string badgeText =
                    "Host";

                var badgeTextSize =
                    ImGui.CalcTextSize(
                        badgeText);

                const float badgePadX =
                    7f;

                const float badgePadY =
                    3f;

                var badgeMin =
                    ImGui.GetCursorScreenPos();

                var badgeSize =
                    new Vector2(
                        badgeTextSize.X +
                        badgePadX * 2f,
                        badgeTextSize.Y +
                        badgePadY * 2f);

                drawList.AddRectFilled(
                    badgeMin,
                    badgeMin +
                    badgeSize,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.28f)),
                    6f);

                drawList.AddRect(
                    badgeMin,
                    badgeMin +
                    badgeSize,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.55f)),
                    6f);

                drawList.AddText(
                    badgeMin +
                    new Vector2(
                        badgePadX,
                        badgePadY - 1f),
                    ImGui.GetColorU32(
                        Vector4.One),
                    badgeText);

                ImGui.Dummy(
                    badgeSize);
            }

            // Small live status underneath.
            ImGui.SetCursorScreenPos(
                new Vector2(
                    nameX,
                    origin.Y +
                    39f));

            ImGui.SetWindowFontScale(
                0.73f);

            ImGui.TextColored(
                Good,
                "● In Watch Party");

            ImGui.SetWindowFontScale(
                1f);

            // =====================================================
            // HOST MANAGEMENT CONTROLS
            // =====================================================

            if (!showManagementActions ||
                isHost)
            {
                return;
            }

            const float gap =
                7f;

            const float makeHostWidth =
                100f;

            const float kickWidth =
                112f;

            const float banWidth =
                106f;

            const float buttonHeight =
                31f;

            const float rightPadding =
                12f;

            var actionsWidth =
                makeHostWidth +
                kickWidth +
                banWidth +
                gap * 2f;

            var actionX =
                origin.X +
                rowWidth -
                rightPadding -
                actionsWidth;

            var actionY =
                origin.Y +
                (rowHeight -
                 buttonHeight) *
                0.5f;

            // -----------------------------------------------------
            // Make Host
            // -----------------------------------------------------

            ImGui.SetCursorScreenPos(
                new Vector2(
                    actionX,
                    actionY));

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
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
                    AccentActive))
            {
                if (ImGui.Button(
                        "Make Host",
                        new Vector2(
                            makeHostWidth,
                            buttonHeight)))
                {
                    onMakeHost?.Invoke();
                }
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"Transfer Watch Party hosting to {displayName}.");
            }

            // -----------------------------------------------------
            // Kick from Room
            //
            // UI ready. No kick transport exists in the supplied
            // StreamClient/backend code yet.
            // -----------------------------------------------------

            ImGui.SetCursorScreenPos(
                new Vector2(
                    actionX +
                    makeHostWidth +
                    gap,
                    actionY));

            using (ImRaii.Disabled())
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.14f)))
            {
                ImGui.Button(
                    "Kick from Room",
                    new Vector2(
                        kickWidth,
                        buttonHeight));
            }

            if (ImGui.IsItemHovered(
                    ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(
                    "Room kick backend action is not connected yet.");
            }

            // -----------------------------------------------------
            // Kick and Ban
            //
            // Kept visually stronger/destructive, but disabled until
            // there is a real room-ban transport.
            // -----------------------------------------------------

            ImGui.SetCursorScreenPos(
                new Vector2(
                    actionX +
                    makeHostWidth +
                    gap +
                    kickWidth +
                    gap,
                    actionY));

            using (ImRaii.Disabled())
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.34f)))
            {
                ImGui.Button(
                    "Kick and Ban",
                    new Vector2(
                        banWidth,
                        buttonHeight));
            }

            if (ImGui.IsItemHovered(
                    ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(
                    "Room ban backend action is not connected yet.");
            }
        }
    }

    private void DrawPartyTabPlaceholder(
        string id,
        FontAwesomeIcon icon,
        string title,
        string description)
    {
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            12f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.04f,
                0.07f,
                1f)))
        using (var panel = ImRaii.Child(
            id,
            new Vector2(-1f, Ui(410f)),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!panel)
            {
                return;
            }

            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    Accent,
                    icon.ToIconString());
            }

            ImGui.SameLine(0f, 8f);

            ImGui.SetWindowFontScale(1.15f);

            ImGui.TextColored(
                Vector4.One,
                title);

            ImGui.SetWindowFontScale(1f);

            ImGui.Dummy(
                new Vector2(0f, 6f));

            ImGui.TextColored(
                MutedText,
                description);
        }
    }

    private void DrawLegacyPartyPanel()
    {
        if (CurrentSession is null)
        {
            ImGui.SetWindowFontScale(1.15f);

            ImGui.TextColored(
                Vector4.One,
                "Watch party");

            ImGui.SetWindowFontScale(1f);

            ImGui.Dummy(new Vector2(0f, 6f));

            ImGui.TextColored(
                MutedText,
                "Sign in to host or join a synced watch party.");

            ImGui.Dummy(new Vector2(0f, 12f));

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
                    "Open Settings",
                    new Vector2(120f, 34f)))
                {
                    currentPage = HomePage.Settings;
                }
            }

            return;
        }

        // ---------------------------------------------------------
        // Heading
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Watch party");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 4f));

        switch (stream.Mode)
        {
            // -----------------------------------------------------
            // Hosting
            // -----------------------------------------------------

            case StreamMode.Hosting:
                {
                    // Temporary visual-only room name.
                    var previewRoomName =
                        $"{CurrentDisplayName ?? "Your"}'s Watch Party";

                    var isPrivate =
                        stream.IsPrivate;

                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.ChildRounding,
                        8f))
                    using (ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        new Vector4(0.045f, 0.06f, 0.10f, 1f)))
                    using (var statusCard = ImRaii.Child(
                        "##partyHosting",
                        new Vector2(-1f, Ui(154f)),
                        false,
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse))
                    {
                        if (statusCard)
                        {
                            // HOSTING
                            ImGui.SetCursorPos(
                                new Vector2(14f, 12f));

                            ImGui.TextColored(
                                Good,
                                "HOSTING");

                            // Private party toggle in top-right.
                            ImGui.SetCursorPos(
                                new Vector2(
                                    ImGui.GetWindowWidth() - 140f,
                                    9f));

                            if (ImGui.Checkbox(
                                "Private party",
                                ref isPrivate))
                            {
                                stream.IsPrivate =
                                    isPrivate;
                            }

                            // Host
                            ImGui.SetCursorPos(
                                new Vector2(14f, 39f));

                            ImGui.TextColored(
                                Vector4.One,
                                $"Host: {CurrentDisplayName ?? "You"}");

                            // Status
                            ImGui.SetCursorPos(
                                new Vector2(14f, 63f));

                            ImGui.SetWindowFontScale(0.80f);

                            ImGui.TextColored(
                                MutedText,
                                $"{stream.Roster.Length} watching  •  Playback stays synced to you");

                            ImGui.SetWindowFontScale(1f);

                            // Room name label
                            ImGui.SetCursorPos(
                                new Vector2(14f, 91f));

                            ImGui.SetWindowFontScale(0.78f);

                            ImGui.TextColored(
                                MutedText,
                                "Room name");

                            ImGui.SetWindowFontScale(1f);

                            // Room name input
                            ImGui.SetCursorPos(
                                new Vector2(14f, 111f));

                            ImGui.SetNextItemWidth(
                                ImGui.GetWindowWidth() - 106f);

                            using (ImRaii.PushStyle(
                                ImGuiStyleVar.FrameRounding,
                                7f)
                                .Push(
                                    ImGuiStyleVar.FramePadding,
                                    new Vector2(12f, 7f)))
                            using (ImRaii.PushColor(
                                ImGuiCol.FrameBg,
                                new Vector4(0.055f, 0.07f, 0.115f, 1f))
                                .Push(
                                    ImGuiCol.FrameBgHovered,
                                    new Vector4(0.07f, 0.09f, 0.145f, 1f))
                                .Push(
                                    ImGuiCol.FrameBgActive,
                                    new Vector4(0.07f, 0.09f, 0.145f, 1f)))
                            {
                                ImGui.InputText(
                                    "##previewRoomName",
                                    ref previewRoomName,
                                    80);
                            }

                            ImGui.SameLine(0f, 8f);

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
                                ImGui.Button(
                                    "Save",
                                    new Vector2(64f, 32f));
                            }
                        }
                    }

                    ImGui.Dummy(
                        new Vector2(0f, 10f));

                    // Invite button directly below the card.
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
                            "Copy party invite",
                            new Vector2(150f, 32f)))
                        {
                            ImGui.SetClipboardText(
                                $"Come watch with me! Right-click my character and choose \"Join Stream\" " +
                                $"(or open AlphaChannel → Player and join \"{CurrentDisplayName}\").");
                        }
                    }

                    ImGui.Dummy(
                        new Vector2(0f, 14f));

                    DrawRoster(
                        $"Watching ({stream.Roster.Length})",
                        allowPromote: true);

                    break;
                }

            // -----------------------------------------------------
            // Viewing
            // -----------------------------------------------------

            case StreamMode.Viewing:
                {
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.ChildRounding,
                        8f))
                    using (ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        new Vector4(0.045f, 0.06f, 0.10f, 1f)))
                    using (var statusCard = ImRaii.Child(
                        "##partyViewing",
                        new Vector2(-1f, Ui(104f)),
                        false,
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse))
                    {
                        if (statusCard)
                        {
                            ImGui.SetCursorPos(
                                new Vector2(14f, 12f));

                            ImGui.TextColored(
                                Good,
                                "IN ROOM");

                            ImGui.SetCursorPos(
                                new Vector2(14f, 38f));

                            ImGui.TextColored(
                                Vector4.One,
                                joinedHostDisplayName is { } host
                                    ? $"{host}'s room"
                                    : "A friend's room");

                            ImGui.SetCursorPos(
                                new Vector2(14f, 64f));

                            ImGui.SetWindowFontScale(0.82f);

                            ImGui.TextColored(
                                MutedText,
                                $"{stream.Roster.Length} also here  •  Playback is synced to the host");

                            ImGui.SetWindowFontScale(1f);
                        }
                    }

                    ImGui.Dummy(new Vector2(0f, 14f));

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
                            "Leave room",
                            new Vector2(120f, 34f)))
                        {
                            LeaveStream();
                            partyChatItems.Clear();
                        }
                    }

                    ImGui.Dummy(new Vector2(0f, 20f));

                    DrawRoster(
                        $"Also here ({stream.Roster.Length})",
                        allowPromote: false);

                    break;
                }

            // -----------------------------------------------------
            // Not currently in a party
            // -----------------------------------------------------

            default:
                {
                    ImGui.SetWindowFontScale(0.88f);

                    ImGui.TextColored(
                        MutedText,
                        "Host automatically while playing, or join a friend's watch party.");

                    ImGui.SetWindowFontScale(1f);

                    ImGui.Dummy(new Vector2(0f, 14f));

                    ImGui.TextColored(
                        MutedText,
                        "Join a party");

                    ImGui.Dummy(new Vector2(0f, 4f));

                    ImGui.SetNextItemWidth(-118f);

                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        10f)
                        .Push(
                            ImGuiStyleVar.FramePadding,
                            new Vector2(14f, 8f)))
                    using (ImRaii.PushColor(
                        ImGuiCol.FrameBg,
                        new Vector4(0.055f, 0.07f, 0.115f, 1f))
                        .Push(
                            ImGuiCol.FrameBgHovered,
                            new Vector4(0.07f, 0.09f, 0.145f, 1f))
                        .Push(
                            ImGuiCol.FrameBgActive,
                            new Vector4(0.07f, 0.09f, 0.145f, 1f)))
                    {
                        if (playerFocusJoin)
                        {
                            ImGui.SetKeyboardFocusHere();
                            playerFocusJoin = false;
                        }

                        ImGui.InputTextWithHint(
                            "##hostName",
                            "Enter their AlphaChannel name",
                            ref joinHostNameInput,
                            32);
                    }
                }

                    ImGui.SameLine(0f, 10f);

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
                            "Join",
                            new Vector2(88f, 38f)))
                        {
                            DoJoin(joinHostNameInput, joinPasswordInput);
                        }
                    }

                    if (joinError is { } error)
                    {
                        ImGui.Dummy(new Vector2(0f, 8f));

                        ImGui.TextColored(
                            Danger,
                            error);
                    }

                    break;
                }
        }


    private void DrawPartySocialPanel()
    {
        DrainPartyChat();

        if (CurrentSession is null)
        {
            ImGui.TextColored(
                MutedText,
                "Sign in under Settings to use room chat and reactions.");

            return;
        }

        if (stream.Mode == StreamMode.None)
        {
            ImGui.SetWindowFontScale(1.15f);

            ImGui.TextColored(
                Vector4.One,
                "Chat");

            ImGui.SetWindowFontScale(1f);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    6f));

            ImGui.TextColored(
                MutedText,
                "Join or host a watch party to use chat and reactions.");

            return;
        }

        DrawPartyChatFeed();

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));

        DrawPartyChatComposer();

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));

        DrawReactions();
    }

    private void DrawPartyChatItem(
        PartyChatItem item)
    {
        switch (item.Kind)
        {
            case PartyChatItemKind.Message:
                {
                    var avatarSize = Ui(42f);
                    var contentGap = Ui(12f);
                    var bottomSpacing = Ui(14f);

                    var origin =
                        ImGui.GetCursorScreenPos();

                    var drawList =
                        ImGui.GetWindowDrawList();

                    var senderName =
                        string.IsNullOrWhiteSpace(
                            item.Name)
                            ? "Someone"
                            : item.Name;

                    var hostName =
                        stream.Mode == StreamMode.Hosting
                            ? CurrentDisplayName
                            : joinedHostDisplayName;

                    var isHostMessage =
                        !string.IsNullOrWhiteSpace(
                            hostName) &&
                        string.Equals(
                            senderName,
                            hostName,
                            StringComparison.OrdinalIgnoreCase);

                    // =========================================================
                    // Avatar
                    // =========================================================

                    // Request the participant's avatar from the server.
                    // This is non-blocking and becomes a cheap cache check
                    // after the first successful lookup.
                    if (!string.IsNullOrWhiteSpace(
                            item.UserId))
                    {
                        EnsurePartyAvatarLoaded(
                            item.UserId,
                            senderName);
                    }

                    var avatarMin =
                        origin;

                    var drewRealAvatar =
                        false;

                    // Cached Watch Party participant avatar.
                    if (!string.IsNullOrWhiteSpace(
                            item.UserId) &&
                        partyAvatarCache.TryGetValue(
                            item.UserId,
                            out var partyAvatar))
                    {
                        DrawAvatarAt(
                            avatarMin,
                            partyAvatar.AvatarIcon,
                            partyAvatar.AvatarColorHex,
                            avatarSize,
                            partyAvatar.AvatarImageUrl);

                        drewRealAvatar =
                            true;
                    }


                    // Initials while a remote avatar is unavailable.
                    if (!drewRealAvatar)
                    {
                        var avatarCenter =
                            avatarMin +
                            new Vector2(
                                avatarSize * 0.5f,
                                avatarSize * 0.5f);

                        drawList.AddCircleFilled(
                            avatarCenter,
                            avatarSize * 0.5f,
                            ImGui.GetColorU32(
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.18f)));

                        drawList.AddCircle(
                            avatarCenter,
                            avatarSize * 0.5f,
                            ImGui.GetColorU32(
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.55f)),
                            0,
                            1.2f);

                        var initial =
                            senderName
                                .Trim()[0]
                                .ToString()
                                .ToUpperInvariant();

                        ImGui.SetWindowFontScale(
                            0.95f);

                        var initialSize =
                            ImGui.CalcTextSize(
                                initial);

                        drawList.AddText(
                            avatarCenter -
                            initialSize * 0.5f,
                            ImGui.GetColorU32(
                                Vector4.One),
                            initial);

                        ImGui.SetWindowFontScale(
                            1f);
                    }

                    // =========================================================
                    // Sender row
                    // =========================================================

                    var contentX =
                        origin.X +
                        avatarSize +
                        contentGap;

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            origin.Y));

                    ImGui.SetWindowFontScale(
                        0.95f);

                    ImGui.TextColored(
                        AccentHover,
                        senderName);

                    ImGui.SetWindowFontScale(
                        1f);

                    // ---------------------------------------------------------
                    // Host badge
                    // ---------------------------------------------------------

                    if (isHostMessage)
                    {
                        ImGui.SameLine(
                            0f,
                            8f);

                        var badgeText =
                            "Host";

                        var badgeTextSize =
                            ImGui.CalcTextSize(
                                badgeText);

                        var badgeMin =
                            ImGui.GetCursorScreenPos();

                        const float badgePadX = 7f;
                        const float badgePadY = 3f;

                        var badgeSize =
                            new Vector2(
                                badgeTextSize.X +
                                badgePadX * 2f,
                                badgeTextSize.Y +
                                badgePadY * 2f);

                        drawList.AddRectFilled(
                            badgeMin,
                            badgeMin +
                            badgeSize,
                            ImGui.GetColorU32(
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.28f)),
                            6f);

                        drawList.AddRect(
                            badgeMin,
                            badgeMin +
                            badgeSize,
                            ImGui.GetColorU32(
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.55f)),
                            6f);

                        drawList.AddText(
                            badgeMin +
                            new Vector2(
                                badgePadX,
                                badgePadY - 1f),
                            ImGui.GetColorU32(
                                Vector4.One),
                            badgeText);

                        ImGui.Dummy(
                            badgeSize);
                    }

                    // ---------------------------------------------------------
                    // Developer badge
                    // ---------------------------------------------------------

                    if (UserRoles.IsDeveloper(senderName))
                    {
                        ImGui.SameLine(
                            0f,
                            8f);

                        const string badgeText =
                            "Developer";

                        var badgeTextSize =
                            ImGui.CalcTextSize(
                                badgeText);

                        const float badgePadX = 7f;
                        const float badgePadY = 3f;

                        var badgeMin =
                            ImGui.GetCursorScreenPos();

                        var badgeSize =
                            new Vector2(
                                badgeTextSize.X +
                                badgePadX * 2f,
                                badgeTextSize.Y +
                                badgePadY * 2f);

                        drawList.AddRectFilled(
                            badgeMin,
                            badgeMin +
                            badgeSize,
                            ImGui.GetColorU32(
                                new Vector4(
                                    0.55f,
                                    0.30f,
                                    1f,
                                    0.28f)),
                            6f);

                        drawList.AddRect(
                            badgeMin,
                            badgeMin +
                            badgeSize,
                            ImGui.GetColorU32(
                                new Vector4(
                                    0.65f,
                                    0.45f,
                                    1f,
                                    0.55f)),
                            6f);

                        drawList.AddText(
                            badgeMin +
                            new Vector2(
                                badgePadX,
                                badgePadY - 1f),
                            ImGui.GetColorU32(
                                Vector4.One),
                            badgeText);

                        ImGui.Dummy(
                            badgeSize);
                    }

                    // ---------------------------------------------------------
                    // Timestamp
                    // ---------------------------------------------------------

                    if (item.ReceivedAt is { } receivedAt)
                    {
                        ImGui.SameLine(
                            0f,
                            9f);

                        ImGui.SetWindowFontScale(
                            0.72f);

                        ImGui.TextColored(
                            MutedText,
                            receivedAt.ToString(
                                "t"));

                        ImGui.SetWindowFontScale(
                            1f);
                    }

                    // =========================================================
                    // Message body
                    // =========================================================

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            origin.Y + 24f));

                    ImGui.SetWindowFontScale(
                        0.96f);

                    var wrapRight =
                        ImGui.GetWindowWidth() -
                        20f;

                    ImGui.PushTextWrapPos(
                        wrapRight);

                    ImGui.TextColored(
                        Vector4.One,
                        item.Text);

                    ImGui.PopTextWrapPos();

                    ImGui.SetWindowFontScale(
                        1f);

                    // =========================================================
                    // Advance cursor below both avatar and wrapped text
                    // =========================================================

                    var contentEndY =
                        ImGui.GetCursorScreenPos().Y;

                    var minimumEndY =
                        origin.Y +
                        avatarSize;

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            origin.X,
                            MathF.Max(
                                contentEndY,
                                minimumEndY) +
                            bottomSpacing));

                    break;
                }

            case PartyChatItemKind.MediaRequest:
                {
                    ImGui.PushID(
                        item.Id.ToString());

                    var isHost =
                        stream.Mode == StreamMode.Hosting;

                    var thumbnailWidth = Ui(180f);
                    var thumbnailHeight = Ui(101f);
                    var cardHeight = Ui(146f);

                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.ChildRounding,
                        8f))
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.WindowPadding,
                        new Vector2(
                            14f,
                            12f)))
                    using (ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        new Vector4(
                            0.055f,
                            0.065f,
                            0.11f,
                            1f)))
                    using (var requestCard =
                        ImRaii.Child(
                            "##mediaRequest",
new Vector2(
    MathF.Max(
        1f,
        ImGui.GetContentRegionAvail().X -
        16f),
    cardHeight),
                            false,
                            ImGuiWindowFlags.NoScrollbar |
                            ImGuiWindowFlags.NoScrollWithMouse))
                    {
                        if (requestCard)
                        {
                            var origin =
                                ImGui.GetCursorScreenPos();

                            var drawList =
                                ImGui.GetWindowDrawList();

                            var cardMin =
                                ImGui.GetWindowPos();

                            var cardMax =
                                cardMin +
                                ImGui.GetWindowSize();

                            drawList.AddRectFilled(
                                new Vector2(
                                    cardMin.X,
                                    cardMin.Y),
                                new Vector2(
                                    cardMin.X + 3f,
                                    cardMax.Y),
                                ImGui.GetColorU32(
                                    Accent),
                                8f,
                                ImDrawFlags.RoundCornersLeft);

                            // -------------------------------------------------
                            // Compact request heading
                            // -------------------------------------------------

                            ImGui.SetCursorPosX(
                                ImGui.GetCursorPosX() + 7f);

                            ImGui.SetWindowFontScale(
                                0.88f);

                            ImGui.TextColored(
                                Accent,
                                "VIDEO REQUEST");

                            ImGui.SetWindowFontScale(
                                1f);

                            ImGui.SameLine(
                                0f,
                                8f);

                            ImGui.SetWindowFontScale(
                                0.88f);

                            ImGui.TextColored(
                                MutedText,
                                $"{item.Name} requested a video");

                            ImGui.SetWindowFontScale(
                                1f);

                            // -------------------------------------------------
                            // Media row
                            // -------------------------------------------------

                            var mediaY =
                                origin.Y + 32f;

                            var thumbnailMin =
                                new Vector2(
                                    origin.X + 7f,
                                    mediaY);

                            var thumbnailMax =
                                thumbnailMin +
                                new Vector2(
                                    thumbnailWidth,
                                    thumbnailHeight);

                            drawList.AddRectFilled(
                                thumbnailMin,
                                thumbnailMax,
                                ImGui.GetColorU32(
                                    new Vector4(
                                        0.025f,
                                        0.03f,
                                        0.05f,
                                        1f)),
                                6f);

                            var thumbnail =
                                thumbnails.Get(
                                    item.ThumbnailUrl);

                            if (thumbnail is not null)
                            {
                                drawList.AddImageRounded(
                                    thumbnail.Handle,
                                    thumbnailMin,
                                    thumbnailMax,
                                    Vector2.Zero,
                                    Vector2.One,
                                    uint.MaxValue,
                                    6f);
                            }
                            else
                            {
                                using (ImRaii.PushFont(
                                    UiBuilder.IconFont))
                                {
                                    var icon =
                                        FontAwesomeIcon.Play.ToIconString();

                                    var iconSize =
                                        ImGui.CalcTextSize(
                                            icon);

                                    drawList.AddText(
                                        thumbnailMin +
                                        (thumbnailMax - thumbnailMin) / 2f -
                                        iconSize / 2f,
                                        ImGui.GetColorU32(
                                            MutedText),
                                        icon);
                                }
                            }

                            // -------------------------------------------------
                            // Right-side host controls
                            // -------------------------------------------------

                            var controlSize = Ui(42f);
                            var controlGap = Ui(10f);

                            var controlsWidth =
                                isHost
                                    ? controlSize * 2f + controlGap
                                    : 0f;

                            var controlsX =
                                origin.X +
                                ImGui.GetWindowWidth() -
                                controlsWidth -
                                9f;

                            // -------------------------------------------------
                            // Title + metadata
                            // -------------------------------------------------

                            var contentX =
                                thumbnailMax.X + 10f;

                            var contentRight =
                                isHost
                                    ? controlsX - 10f
                                    : origin.X +
                                      ImGui.GetWindowWidth() -
                                      9f;

                            ImGui.SetCursorScreenPos(
                                new Vector2(
                                    contentX,
                                    mediaY + 1f));

                            ImGui.SetWindowFontScale(
                                1.00f);

                            ImGui.PushTextWrapPos(
                                contentRight);

                            ImGui.TextColored(
                                Vector4.One,
                                string.IsNullOrWhiteSpace(
                                    item.Title)
                                    ? "Video"
                                    : item.Title);

                            ImGui.PopTextWrapPos();

                            ImGui.SetWindowFontScale(
                                1f);

                            ImGui.SetCursorScreenPos(
                                new Vector2(
                                    contentX,
                                    mediaY + 58f));

                            ImGui.SetWindowFontScale(
                                0.82f);

                            var sourceText =
                                string.IsNullOrWhiteSpace(
                                    item.Source)
                                    ? "Media"
                                    : item.Source;

                            var metadataText =
                                item.Duration is { } duration
                                    ? $"{sourceText}  •  {FormatTime((float)duration.TotalSeconds)}"
                                    : sourceText;

                            ImGui.TextColored(
                                MutedText,
                                metadataText);

                            ImGui.SetWindowFontScale(
                                1f);

                            // -------------------------------------------------
                            // Host-only icon buttons
                            // -------------------------------------------------

                            if (isHost)
                            {
                                var controlsY =
                                    mediaY +
                                    (thumbnailHeight - controlSize) *
                                    0.5f;

                                // Add to queue
                                ImGui.SetCursorScreenPos(
                                    new Vector2(
                                        controlsX,
                                        controlsY));

                                using (ImRaii.PushStyle(
                                    ImGuiStyleVar.FrameRounding,
                                    7f))
                                using (ImRaii.PushColor(
                                    ImGuiCol.Button,
                                    new Vector4(
                                        0.075f,
                                        0.09f,
                                        0.15f,
                                        1f))
                                    .Push(
                                        ImGuiCol.ButtonHovered,
                                        new Vector4(
                                            0.10f,
                                            0.12f,
                                            0.19f,
                                            1f))
                                    .Push(
                                        ImGuiCol.ButtonActive,
                                        new Vector4(
                                            0.12f,
                                            0.14f,
                                            0.22f,
                                            1f)))
                                {
                                    var clicked =
                                        ImGui.Button(
                                            "##addToQueue",
                                            new Vector2(
                                                controlSize,
                                                controlSize));

                                    var buttonMin =
                                        ImGui.GetItemRectMin();

                                    var buttonMax =
                                        ImGui.GetItemRectMax();

                                    using (ImRaii.PushFont(
                                        UiBuilder.IconFont))
                                    {
                                        var icon =
                                            FontAwesomeIcon.Plus.ToIconString();

                                        var iconSize =
                                            ImGui.CalcTextSize(
                                                icon);

                                        drawList.AddText(
                                            buttonMin +
                                            (buttonMax -
                                             buttonMin -
                                             iconSize) *
                                            0.5f,
                                            ImGui.GetColorU32(
                                                Vector4.One),
                                            icon);
                                    }

                                    if (clicked)
                                    {
                                        queue.Add(
                                            new Video.VideoQueueEntry(
                                                item.Url,
                                                item.Title,
                                                item.Source,
                                                item.Duration,
                                                item.ThumbnailUrl));

                                        var queuePosition =
                                            queue.Entries.Count;

                                        _ = stream.SendMediaRequestResultAsync(
                                            item.Id,
                                            false,
                                            queuePosition);
                                    }
                                }

                                if (ImGui.IsItemHovered())
                                {
                                    ImGui.SetTooltip(
                                        "Add to queue");
                                }

                                // Play now
                                ImGui.SetCursorScreenPos(
                                    new Vector2(
                                        controlsX +
                                        controlSize +
                                        controlGap,
                                        controlsY));

                                var gameplayActive =
                                    screenController.Engine.IsPlayingSnes ||
                                    screenController.Engine.IsPlayingGameBoy;

                                using (ImRaii.Disabled(
                                    gameplayActive))
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
                                    var clicked =
                                        ImGui.Button(
                                            "##playNow",
                                            new Vector2(
                                                controlSize,
                                                controlSize));

                                    var buttonMin =
                                        ImGui.GetItemRectMin();

                                    var buttonMax =
                                        ImGui.GetItemRectMax();

                                    using (ImRaii.PushFont(
                                        UiBuilder.IconFont))
                                    {
                                        var icon =
                                            FontAwesomeIcon.Play.ToIconString();

                                        var iconSize =
                                            ImGui.CalcTextSize(
                                                icon);

                                        var iconPosition =
                                            buttonMin +
                                            (buttonMax -
                                             buttonMin -
                                             iconSize) *
                                            0.5f;

                                        // Visually centre the triangle.
                                        iconPosition.X +=
                                            1f;

                                        drawList.AddText(
                                            iconPosition,
                                            ImGui.GetColorU32(
                                                gameplayActive
                                                    ? MutedText
                                                    : Vector4.One),
                                            icon);
                                    }

                                    if (clicked)
                                    {
                                        queue.PlayNow(
                                            new Video.VideoQueueEntry(
                                                item.Url,
                                                item.Title,
                                                item.Source,
                                                item.Duration,
                                                item.ThumbnailUrl));

                                        _ = stream.SendMediaRequestResultAsync(
                                            item.Id,
                                            true,
                                            0);
                                    }
                                }

                                if (ImGui.IsItemHovered(
                                        ImGuiHoveredFlags.AllowWhenDisabled))
                                {
                                    ImGui.SetTooltip(
                                        gameplayActive
                                            ? "End gameplay to begin playback"
                                            : "Play now");
                                }
                            }
                        }
                    }

                    ImGui.PopID();

                    ImGui.Dummy(
                        new Vector2(
                            0f,
                            3f));

                    break;
                }

            case PartyChatItemKind.MediaQueued:
                {
                    var position =
                        item.QueuePosition?.ToString() ??
                        "?";

                    ImGui.TextColored(
                        Good,
                        "✓ Added to queue");

                    ImGui.SetWindowFontScale(
                        0.88f);

                    ImGui.TextColored(
                        Vector4.One,
                        $"Requested by {item.Name}");

                    ImGui.TextColored(
                        MutedText,
                        $"Queue position: {position}");

                    ImGui.TextColored(
                        MutedText,
                        string.IsNullOrWhiteSpace(
                            item.Title)
                            ? item.Url
                            : item.Title);

                    ImGui.SetWindowFontScale(
                        1f);

                    ImGui.Dummy(
                        new Vector2(
                            0f,
                            8f));

                    break;
                }

            case PartyChatItemKind.MediaPlaying:
                {
                    ImGui.TextColored(
                        Good,
                        "▶ Now playing");

                    ImGui.SetWindowFontScale(
                        0.88f);

                    ImGui.TextColored(
                        Vector4.One,
                        $"Requested by {item.Name}");

                    ImGui.TextColored(
                        MutedText,
                        string.IsNullOrWhiteSpace(
                            item.Title)
                            ? item.Url
                            : item.Title);

                    ImGui.SetWindowFontScale(
                        1f);

                    ImGui.Dummy(
                        new Vector2(
                            0f,
                            8f));

                    break;
                }
            case PartyChatItemKind.Reaction:
                {
                    var avatarSize = Ui(30f);
                    var rowHeight = Ui(40f);

                    var origin =
                        ImGui.GetCursorScreenPos();

                    var drawList =
                        ImGui.GetWindowDrawList();

                    var senderName =
                        string.IsNullOrWhiteSpace(
                            item.Name)
                            ? "Someone"
                            : item.Name;

                    var hostName =
                        stream.Mode == StreamMode.Hosting
                            ? CurrentDisplayName
                            : joinedHostDisplayName;

                    var isHostReaction =
                        !string.IsNullOrWhiteSpace(
                            hostName) &&
                        string.Equals(
                            senderName,
                            hostName,
                            StringComparison.OrdinalIgnoreCase);

                    // ---------------------------------------------------------
                    // Avatar
                    // ---------------------------------------------------------

                    // Use the same server-avatar path as normal chat messages.
                    if (!string.IsNullOrWhiteSpace(
                            item.UserId))
                    {
                        EnsurePartyAvatarLoaded(
                            item.UserId,
                            senderName);
                    }

                    var drewRealAvatar =
                        false;

                    // Cached Watch Party participant avatar.
                    if (!string.IsNullOrWhiteSpace(
                            item.UserId) &&
                        partyAvatarCache.TryGetValue(
                            item.UserId,
                            out var partyAvatar))
                    {
                        DrawAvatarAt(
                            origin,
                            partyAvatar.AvatarIcon,
                            partyAvatar.AvatarColorHex,
                            avatarSize,
                            partyAvatar.AvatarImageUrl);

                        drewRealAvatar =
                            true;
                    }


                    // Initials while a remote avatar is unavailable.
                    if (!drewRealAvatar)
                    {
                        var avatarCenter =
                            origin +
                            new Vector2(
                                avatarSize * 0.5f,
                                avatarSize * 0.5f);

                        drawList.AddCircleFilled(
                            avatarCenter,
                            avatarSize * 0.5f,
                            ImGui.GetColorU32(
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.16f)));

                        var initial =
                            senderName
                                .Trim()[0]
                                .ToString()
                                .ToUpperInvariant();

                        var initialSize =
                            ImGui.CalcTextSize(
                                initial);

                        drawList.AddText(
                            avatarCenter -
                            initialSize * 0.5f,
                            ImGui.GetColorU32(
                                Vector4.One),
                            initial);
                    }

                    // ---------------------------------------------------------
                    // Activity pill
                    // ---------------------------------------------------------

                    var pillMin =
                        origin +
                        new Vector2(
                            avatarSize + 8f,
                            2f);

                    var pillHeight =
                        30f;

                    var nameText =
                        $"{senderName} reacted with";

                    ImGui.SetWindowFontScale(
                        0.82f);

                    var nameSize =
                        ImGui.CalcTextSize(
                            nameText);

                    Vector2 reactionSize;

                    using (ImRaii.PushFont(
                        UiBuilder.IconFont))
                    {
                        reactionSize =
                            ImGui.CalcTextSize(
                                item.Text);
                    }

                    var pillWidth =
                        nameSize.X +
                        reactionSize.X +
                        34f;

                    drawList.AddRectFilled(
                        pillMin,
                        pillMin +
                        new Vector2(
                            pillWidth,
                            pillHeight),
                        ImGui.GetColorU32(
                            new Vector4(
                                0.065f,
                                0.075f,
                                0.12f,
                                1f)),
                        15f);

                    drawList.AddText(
                        pillMin +
                        new Vector2(
                            12f,
                            7f),
                        ImGui.GetColorU32(
                            MutedText),
                        nameText);

                    using (ImRaii.PushFont(
                        UiBuilder.IconFont))
                    {
                        drawList.AddText(
                            pillMin +
                            new Vector2(
                                18f + nameSize.X,
                                7f),
                            ImGui.GetColorU32(
                                AccentHover),
                            item.Text);
                    }

                    ImGui.SetWindowFontScale(
                        1f);

                    // Register the reaction row with ImGui's layout system.
                    //
                    // The reaction itself is drawn manually with the draw list, so simply
                    // moving the cursor does not reliably extend the scrollable content.
                    // A real Dummy item gives the chat child the correct content height,
                    // allowing its normal stick-to-bottom behaviour to work.
                    ImGui.SetCursorScreenPos(
                        origin);

                    ImGui.Dummy(
                        new Vector2(
                            0f,
                            rowHeight + 4f));

                    break;
                }
        }
    }

    private void DrawPartyChatFeed()
    {
        // ---------------------------------------------------------
        // Explicit inset inside the outer chat container.
        //
        // Borderless ImGui child windows don't reliably inherit
        // WindowPadding, so physically inset the scrollable feed.
        // ---------------------------------------------------------

        var insetX = Ui(18f);
        var insetY = Ui(14f);

        var available =
            ImGui.GetContentRegionAvail();

        ImGui.SetCursorPos(
            ImGui.GetCursorPos() +
            new Vector2(
                insetX,
                insetY));

        var feedSize =
            new Vector2(
                MathF.Max(
                    1f,
                    available.X -
                    insetX * 2f),
                MathF.Max(
                    1f,
                    available.Y -
                    insetY * 2f));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            8f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                Vector2.Zero))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.045f,
                0.06f,
                0.10f,
                1f)))
        using (var child =
            ImRaii.Child(
                "##partyChatLog",
                feedSize,
                false,
                ImGuiWindowFlags.None))
        {
            if (!child)
            {
                return;
            }

            if (partyChatItems.Count == 0)
            {
                ImGui.SetWindowFontScale(
                    0.88f);

                ImGui.TextColored(
                    MutedText,
                    "No messages yet.");

                ImGui.SetWindowFontScale(
                    1f);
            }
            else
            {
                const float chatContentInset = 16f;

                ImGui.Indent(
                    chatContentInset);

                foreach (var item in partyChatItems)
                {
                    // Reserve matching space on the right so full-width items,
                    // especially video-request cards, cannot touch the container edge.
                    ImGui.SetNextItemWidth(
                        MathF.Max(
                            1f,
                            ImGui.GetContentRegionAvail().X -
                            chatContentInset));

                    DrawPartyChatItem(
                        item);
                }

                ImGui.Unindent(
                    chatContentInset);
            }

            if (partyChatStickToBottom)
            {
                ImGui.SetScrollHereY(
                    1f);

                partyChatStickToBottom =
                    false;
            }
        }
    }

    private void DrawPartyChatComposer()
    {
        var dockHeight = Ui(68f);
        var padding = Ui(10f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            14f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    padding,
                    8f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.045f,
                0.050f,
                0.085f,
                1f)))
        using (var dock =
            ImRaii.Child(
                "##partyChatDock",
                new Vector2(
                    -1f,
                    dockHeight),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!dock)
            {
                return;
            }

            var availableWidth =
     ImGui.GetContentRegionAvail().X;

            // Save the dock content origin so the React section can be
            // positioned independently from the message controls.
            var dockContentStart =
                ImGui.GetCursorPos();

            var reactWidth = Ui(300f);
            var sendWidth = Ui(92f);
            var optionsWidth = Ui(40f);
            var dividerGap = Ui(14f);
            var inputHeight = Ui(40f);

            var chatWidth =
                MathF.Max(
                    300f,
                    availableWidth -
                    reactWidth -
                    dividerGap);

            // =====================================================
            // LEFT — message composer
            // =====================================================

            ImGui.SetCursorPosY(
                ImGui.GetCursorPosY() +
                6f);

            ImGui.BeginGroup();

            var inputWidth =
                chatWidth -
                sendWidth -
                optionsWidth -
                20f;

            ImGui.SetNextItemWidth(
                inputWidth);

            bool sent;

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                9f)
                .Push(
                    ImGuiStyleVar.FramePadding,
                    new Vector2(
                        14f,
                        10f)))
            using (ImRaii.PushColor(
                ImGuiCol.FrameBg,
                new Vector4(
                    0.060f,
                    0.070f,
                    0.115f,
                    1f))
                .Push(
                    ImGuiCol.FrameBgHovered,
                    new Vector4(
                        0.075f,
                        0.090f,
                        0.145f,
                        1f))
                .Push(
                    ImGuiCol.FrameBgActive,
                    new Vector4(
                        0.085f,
                        0.100f,
                        0.160f,
                        1f)))
            {
                sent =
                    ImGui.InputTextWithHint(
                        "##partyChatInput",
                        "Message the watch party...",
                        ref partyChatInput,
                        280,
                        ImGuiInputTextFlags.EnterReturnsTrue);
            }

            ImGui.SameLine(
                0f,
                10f);

            var hasMessage =
                partyChatInput.Trim().Length > 0;

            var sendClicked =
                false;

            using (ImRaii.Disabled(
       !hasMessage))
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                9f))
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
                sendClicked =
                    ImGui.Button(
                        "Send",
                        new Vector2(
                            sendWidth,
                            inputHeight));
            }

            // =====================================================
            // Chat options
            // =====================================================

            ImGui.SameLine(
                0f,
                10f);

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                9f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(
                    0.060f,
                    0.070f,
                    0.115f,
                    1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(
                        0.085f,
                        0.095f,
                        0.155f,
                        1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(
                        0.10f,
                        0.11f,
                        0.18f,
                        1f)))
            {
                if (ImGui.Button(
                        "...##partyChatOptions",
                        new Vector2(
                            optionsWidth,
                            inputHeight)))
                {
                    ImGui.OpenPopup(
                        "##partyChatOptionsPopup");
                }
            }

            ImGui.SetNextWindowSize(
                new Vector2(
                    300f,
                    0f),
                ImGuiCond.Appearing);

            using (ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    14f,
                    12f))
                .Push(
                    ImGuiStyleVar.PopupRounding,
                    10f))
            {
                if (ImGui.BeginPopup(
                        "##partyChatOptionsPopup"))
                {
                    ImGui.SetWindowFontScale(
                        0.90f);

                    ImGui.TextColored(
                        Vector4.One,
                        "Chat options");

                    ImGui.SetWindowFontScale(
                        1f);

                    ImGui.Dummy(
                        new Vector2(
                            0f,
                            5f));

                    // =====================================================
                    // Relay to FFXIV chat
                    // =====================================================

                    var relayChat =
                        Plugin.Cfg.RelayPartyChatToGameChat;

                    if (ImGui.Checkbox(
                            "Relay messages to FFXIV chat",
                            ref relayChat))
                    {
                        Plugin.Cfg.RelayPartyChatToGameChat =
                            relayChat;

                        Plugin.Cfg.Save();
                    }

                    ImGui.Dummy(
                        new Vector2(
                            0f,
                            3f));

                    ImGui.SetWindowFontScale(
                        0.76f);

                    ImGui.TextColored(
                        MutedText,
                        "Show Watch Party messages in your\nnormal FFXIV chatbox as well.");

                    ImGui.SetWindowFontScale(
                        1f);

                    // =====================================================
                    // Development sandbox
                    // =====================================================
                    //
                    // Host-only. This does not change the real StreamMode;
                    // it only routes media buttons through the viewer/request
                    // flow for testing.
                    // =====================================================

                    if (stream.Mode == StreamMode.Hosting)
                    {
                        ImGui.Dummy(
                            new Vector2(
                                0f,
                                8f));

                        ImGui.Separator();

                        ImGui.Dummy(
                            new Vector2(
                                0f,
                                7f));

                        ImGui.SetWindowFontScale(
                            0.78f);

                        ImGui.TextColored(
                            MutedText,
                            "TESTING");

                        ImGui.SetWindowFontScale(
                            1f);

                        ImGui.Dummy(
                            new Vector2(
                                0f,
                                4f));

                        ImGui.Checkbox(
                            "Sandbox mode: act as viewer",
                            ref sandboxActAsViewer);

                        if (sandboxActAsViewer)
                        {
                            ImGui.Dummy(
                                new Vector2(
                                    0f,
                                    2f));

                            ImGui.SetWindowFontScale(
                                0.76f);

                            ImGui.TextColored(
                                Gold,
                                "Media buttons behave like viewer requests.\nYou are still the real host.");

                            ImGui.SetWindowFontScale(
                                1f);
                        }
                    }

                    ImGui.EndPopup();
                }
            }

            if ((sendClicked || sent) &&
                hasMessage)
            {
                var text =
                    partyChatInput.Trim();

                partyChatInput =
                    string.Empty;

                _ = stream.SendChatAsync(
                    text);

                partyChatStickToBottom =
                    true;
            }

            ImGui.EndGroup();

            // =====================================================
            // DIVIDER
            // =====================================================

            var dividerX =
                dockContentStart.X +
                chatWidth +
                dividerGap * 0.5f;

            var dividerTop =
                dockContentStart.Y +
                1f;

            var windowPos =
                ImGui.GetWindowPos();

            ImGui.GetWindowDrawList()
                .AddLine(
                    windowPos +
                    new Vector2(
                        dividerX,
                        dividerTop),
                    windowPos +
                    new Vector2(
                        dividerX,
                        dividerTop + 44f),
                    ImGui.GetColorU32(
                        new Vector4(
                            1f,
                            1f,
                            1f,
                            0.13f)),
                    1f);

            // =====================================================
            // RIGHT — React Live
            //
            // Explicit positioning means this section no longer
            // depends on the cursor left behind by the composer.
            // =====================================================

            var reactX =
                dividerX +
                14f;

            var reactWidthAvailable =
                MathF.Max(
                    120f,
                    availableWidth -
                    (reactX - dockContentStart.X));

            // Position the React block directly inside the dock.
            var reactHeaderY =
                dockContentStart.Y -
                2f;

            // -----------------------------------------------------
            // Header
            // -----------------------------------------------------

            ImGui.SetCursorPos(
                new Vector2(
                    reactX,
                    reactHeaderY));

            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                ImGui.SetWindowFontScale(
                    0.82f);

                ImGui.TextColored(
                    Accent,
                    FontAwesomeIcon.Bolt.ToIconString());

                ImGui.SetWindowFontScale(
                    1f);
            }

            ImGui.SameLine(
                0f,
                5f);

            ImGui.SetWindowFontScale(
                0.68f);

            ImGui.TextColored(
                AccentHover,
                "REACT LIVE");

            ImGui.SetWindowFontScale(
                1f);

            // -----------------------------------------------------
            // Reaction buttons
            // -----------------------------------------------------

            ImGui.SetCursorPos(
                new Vector2(
                    reactX,
                    reactHeaderY + 15f));

            DrawCompactReactions(
                reactWidthAvailable);
        }
    }
}
