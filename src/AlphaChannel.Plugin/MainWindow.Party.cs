using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Config;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Org.BouncyCastle.Tls;

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

    private enum VideoRequestPermission
    {
        Allow,
        AutoAccept,
        NotAllowed,
    }

    private readonly Dictionary<string, VideoRequestPermission> videoRequestPermissions =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, Queue<DateTime>> videoRequestTimes =
        new(StringComparer.Ordinal);

    private const string VideoRequestsUnavailableMessage =
        "The host is not currently accepting video requests";

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
    private string? publishedNextQueueSignature;
    private VideoQueueEntry? viewerPartyNextQueueEntry;
    private int viewerPartyQueueCount;

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
    private bool browserStreamOfferDismissed;

    private bool partySyncTvPlacement = true;
    internal bool PartySyncTvPlacement => partySyncTvPlacement;

    private AudioVisualizerMode partyVisualizerMode =
     AudioVisualizerMode.ClassicBars;

    private AudioVisualizerTheme partyVisualizerTheme =
        AudioVisualizerTheme.AlphaPurple;

    private string? partyVisualizerMediaUrl;

    internal AudioVisualizerMode PartyVisualizerMode =>
        partyVisualizerMode;

    internal AudioVisualizerTheme PartyVisualizerTheme =>
        partyVisualizerTheme;

    private bool ShouldShowBottomPlaybackBar =>
        currentPage is
            HomePage.Home or
            HomePage.Player or
            HomePage.VideoGrid or
            HomePage.InternetArchive;

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
                        //
                        // Cache the missing result too. Home room cards
                        // render every frame, so an unavailable avatar
                        // must not cause continuous profile requests.
                        //
                        partyAvatarCache[userId] =
                            new PartyAvatarInfo(
                                null,
                                null,
                                null,
                                DateTime.UtcNow);

                        return;
                    }

                    var results =
                        await friendsClient.SearchAsync(
                            token,
                            displayName);

                    if (results is null)
                    {
                        partyAvatarCache[userId] =
                            new PartyAvatarInfo(
                                null,
                                null,
                                null,
                                DateTime.UtcNow);

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
                        partyAvatarCache[userId] =
                            new PartyAvatarInfo(
                                null,
                                null,
                                null,
                                DateTime.UtcNow);

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

    private void DrawPartyVisualizerSelector(
    Video.VideoQueueEntry current,
    Vector2 origin,
    float thumbnailWidth,
    float thumbnailHeight)
    {
        if (stream.Mode != StreamMode.Hosting ||
            !video.IsAudioOnly)
        {
            return;
        }

        var selection =
            AudioVisualizerSelection.Parse(
                current.Url);

        //
        // When genuinely changing media, adopt the visualizer and theme encoded
        // in the new URL. Fragment-only changes do not reset the local controls.
        //
        if (!string.Equals(
                partyVisualizerMediaUrl,
                selection.MediaUrl,
                StringComparison.Ordinal))
        {
            partyVisualizerMediaUrl =
                selection.MediaUrl;

            partyVisualizerMode =
                selection.Mode;

            partyVisualizerTheme =
                selection.Theme;
        }

        var dropdownGap =
            Ui(6f);

        var dropdownWidth =
            (thumbnailWidth - dropdownGap) /
            2f;

        var labelY =
            origin.Y +
            thumbnailHeight +
            Ui(24f);

        var dropdownY =
            labelY + Ui(20f);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   Ui(7f)))
        using (ImRaii.PushColor(
                   ImGuiCol.FrameBg,
                   new Vector4(
                       0.055f,
                       0.065f,
                       0.11f,
                       1f))
                   .Push(
                       ImGuiCol.FrameBgHovered,
                       new Vector4(
                           0.075f,
                           0.085f,
                           0.14f,
                           1f))
                   .Push(
                       ImGuiCol.FrameBgActive,
                       new Vector4(
                           0.09f,
                           0.075f,
                           0.16f,
                           1f))
                   .Push(
                       ImGuiCol.Header,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.72f))
                   .Push(
                       ImGuiCol.HeaderHovered,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.88f)))
        {
            //
            // Visualizer dropdown.
            //
            ImGui.SetCursorScreenPos(
                new Vector2(
                    origin.X,
                    labelY));

            SetUiFontScale(0.76f);
            ImGui.TextColored(MutedText, "Visualizer");
            SetUiFontScale(1f);

            ImGui.SetCursorScreenPos(
                new Vector2(
                    origin.X,
                    dropdownY));

            ImGui.SetNextItemWidth(
                dropdownWidth);

            var visualizerPreview =
                AudioVisualizerSelection.GetDisplayName(
                    partyVisualizerMode);

            if (ImGui.BeginCombo(
                    "##partyVisualizer",
                    visualizerPreview))
            {
                DrawPartyVisualizerOption(
                    current,
                    AudioVisualizerMode.ClassicBars);

                DrawPartyVisualizerOption(
                    current,
                    AudioVisualizerMode.FfmpegSpectrum);

                DrawPartyVisualizerOption(
                    current,
                    AudioVisualizerMode.MirrorSpectrum);

                DrawPartyVisualizerOption(
    current,
    AudioVisualizerMode.Waterfall);

                DrawPartyVisualizerOption(
    current,
    AudioVisualizerMode.NeonWave);

                DrawPartyVisualizerOption(
    current,
    AudioVisualizerMode.OrbitalScope);

                ImGui.EndCombo();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Choose the audio visualizer used by everyone in the Watch Party.");
            }

            //
            // Theme dropdown.
            //
            ImGui.SetCursorScreenPos(
                new Vector2(
                    origin.X +
                    dropdownWidth +
                    dropdownGap,
                    labelY));

            SetUiFontScale(0.76f);
            ImGui.TextColored(MutedText, "Colour");
            SetUiFontScale(1f);

            ImGui.SetCursorScreenPos(
                new Vector2(
                    origin.X +
                    dropdownWidth +
                    dropdownGap,
                    dropdownY));

            ImGui.SetNextItemWidth(
                dropdownWidth);

            var themeDisabled =
                partyVisualizerMode ==
                AudioVisualizerMode.ClassicBars;

            using (ImRaii.Disabled(
                       themeDisabled))
            {
                var themePreview =
                    AudioVisualizerSelection.GetThemeDisplayName(
                        partyVisualizerTheme);

                if (ImGui.BeginCombo(
                        "##partyVisualizerTheme",
                        themePreview))
                {
                    DrawPartyVisualizerThemeOption(
                        current,
                        AudioVisualizerTheme.AlphaPurple);

                    DrawPartyVisualizerThemeOption(
                        current,
                        AudioVisualizerTheme.ElectricBlue);

                    DrawPartyVisualizerThemeOption(
                        current,
                        AudioVisualizerTheme.NeonCyan);

                    DrawPartyVisualizerThemeOption(
                        current,
                        AudioVisualizerTheme.Emerald);

                    DrawPartyVisualizerThemeOption(
                        current,
                        AudioVisualizerTheme.HotPink);

                    DrawPartyVisualizerThemeOption(
                        current,
                        AudioVisualizerTheme.SunsetOrange);

                    DrawPartyVisualizerThemeOption(
                        current,
                        AudioVisualizerTheme.Crimson);

                    DrawPartyVisualizerThemeOption(
                        current,
                        AudioVisualizerTheme.White);

                    ImGui.Separator();

                    DrawPartyVisualizerThemeOption(
                        current,
                        AudioVisualizerTheme.PurplePink);

                    DrawPartyVisualizerThemeOption(
                        current,
                        AudioVisualizerTheme.BlueCyan);

                    DrawPartyVisualizerThemeOption(
                        current,
                        AudioVisualizerTheme.Sunset);

                    DrawPartyVisualizerThemeOption(
                        current,
                        AudioVisualizerTheme.Rainbow);

                    ImGui.EndCombo();
                }
            }

            if (ImGui.IsItemHovered(
                    ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(
                    themeDisabled
                        ? "Classic Bars is a less CPU-intensive visualizer which only monitors the audio volume and does not provide colour selection."
                        : "Choose the visualizer colour or gradient used by everyone in the Watch Party.");
            }
        }
    }

    private void DrawPartyVisualizerOption(
    Video.VideoQueueEntry current,
    AudioVisualizerMode mode)
    {
        var selected =
            partyVisualizerMode ==
            mode;

        var label =
            AudioVisualizerSelection.GetDisplayName(
                mode);

        if (ImGui.Selectable(
                label,
                selected))
        {
            partyVisualizerMode =
                mode;

            var transmittedUrl =
                AudioVisualizerSelection.AddToUrl(
                    current.Url,
                    mode,
                    partyVisualizerTheme);

            //
            // VideoPlayer recognizes this as a presentation-only fragment
            // change and does not restart the audio.
            //
            video.Play(
                transmittedUrl);

            AepLog.Info(
                $"[WatchParty] Host selected visualizer: {label}");
        }

        if (selected)
        {
            ImGui.SetItemDefaultFocus();
        }
    }

    private void DrawPartyVisualizerThemeOption(
    Video.VideoQueueEntry current,
    AudioVisualizerTheme theme)
    {
        var selected =
            partyVisualizerTheme ==
            theme;

        var label =
            AudioVisualizerSelection.GetThemeDisplayName(
                theme);

        if (ImGui.Selectable(
                label,
                selected))
        {
            partyVisualizerTheme =
                theme;

            var transmittedUrl =
                AudioVisualizerSelection.AddToUrl(
                    current.Url,
                    partyVisualizerMode,
                    theme);

            //
            // The normalized media URL remains unchanged, so only the FFmpeg
            // visualizer graph is rebuilt.
            //
            video.Play(
                transmittedUrl);

            AepLog.Info(
                $"[WatchParty] Host selected visualizer theme: {label}");
        }

        if (selected)
        {
            ImGui.SetItemDefaultFocus();
        }
    }

    private void DrainPartyChat()
    {
        PublishPartyNextQueueIfChanged();

        while (stream.IncomingNextQueueSnapshots.TryDequeue(out var nextSnapshot))
        {
            viewerPartyQueueCount = Math.Max(0, nextSnapshot.Count);
            viewerPartyNextQueueEntry = nextSnapshot.Count > 0 &&
                !string.IsNullOrWhiteSpace(nextSnapshot.Title)
                ? new VideoQueueEntry(
                    string.Empty,
                    nextSnapshot.Title,
                    nextSnapshot.Source ?? string.Empty,
                    nextSnapshot.DurationSeconds is { } seconds
                        ? TimeSpan.FromSeconds(Math.Max(0d, seconds))
                        : null,
                    nextSnapshot.ThumbnailUrl)
                : null;
        }

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

        while (stream.PendingHostMediaRequests.TryDequeue(out var pendingRequest))
        {
            if (stream.Mode != StreamMode.Hosting)
            {
                continue;
            }

            var permission = videoRequestPermissions.TryGetValue(pendingRequest.UserId, out var savedPermission)
                ? savedPermission
                : VideoRequestPermission.Allow;

            if (CurrentMediaDisallowsVideoRequests() || permission == VideoRequestPermission.NotAllowed)
            {
                _ = stream.SendMediaRequestDeniedAsync(
                    pendingRequest.UserId,
                    VideoRequestsUnavailableMessage);
                continue;
            }

            var now = DateTime.UtcNow;
            if (!videoRequestTimes.TryGetValue(pendingRequest.UserId, out var recentRequests))
            {
                recentRequests = new Queue<DateTime>();
                videoRequestTimes[pendingRequest.UserId] = recentRequests;
            }

            while (recentRequests.Count > 0 && now - recentRequests.Peek() >= TimeSpan.FromSeconds(60))
            {
                recentRequests.Dequeue();
            }

            if (recentRequests.Count >= 3)
            {
                _ = stream.SendMediaRequestDeniedAsync(
                    pendingRequest.UserId,
                    "You can send up to 3 video requests every 60 seconds.");
                continue;
            }

            recentRequests.Enqueue(now);

            _ = stream.PublishMediaRequestAsync(
                pendingRequest.UserId,
                pendingRequest.DisplayName,
                pendingRequest.RequestId);

            if (permission == VideoRequestPermission.AutoAccept)
            {
                queue.Add(new VideoQueueEntry(
                    pendingRequest.Url,
                    pendingRequest.Url,
                    string.Empty,
                    null,
                    null));

                _ = stream.SendMediaRequestResultAsync(
                    pendingRequest.RequestId,
                    false,
                    queue.Entries.Count);
            }
        }

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
                    null,
                    UserId: request.UserId));

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

        if (stream.Mode == StreamMode.None)
        {
            videoRequestPermissions.Clear();
            videoRequestTimes.Clear();
            publishedNextQueueSignature = null;
            viewerPartyNextQueueEntry = null;
            viewerPartyQueueCount = 0;
        }
    }

    private void PublishPartyNextQueueIfChanged()
    {
        if (stream.Mode != StreamMode.Hosting)
        {
            publishedNextQueueSignature = null;
            return;
        }

        var next = queue.Entries.FirstOrDefault();
        var rosterSignature = string.Join(',', stream.Roster.Select(member => member.UserId));
        var signature = string.Join('|',
            queue.Entries.Count,
            next?.Id.ToString() ?? string.Empty,
            next?.Title ?? string.Empty,
            next?.Source ?? string.Empty,
            next?.Duration?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            next?.ThumbnailUrl ?? string.Empty,
            rosterSignature);

        if (string.Equals(signature, publishedNextQueueSignature, StringComparison.Ordinal))
        {
            return;
        }

        publishedNextQueueSignature = signature;
        _ = stream.SendNextQueueSnapshotAsync(new PartyNextQueueSnapshot(
            queue.Entries.Count,
            next?.Title,
            next?.Source,
            next?.Duration?.TotalSeconds,
            next?.ThumbnailUrl));
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
            UiVec(0f, 10f));

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

    private void DrawExclusivePlaybackStreamOffer()
    {
        if (stream.Mode != StreamMode.Hosting)
        {
            return;
        }

        var engine =
            screenController.Engine;

        var gameplayActive =
            engine.IsPlayingGame;

        var browserActive =
            engine.IsPlayingBrowser;

        var gameplayAlreadyBroadcasting =
            gameBroadcastArmed ||
            engine.IsSnesBroadcasting ||
            engine.IsGameBoyBroadcasting ||
            engine.IsNesBroadcasting ||
            engine.IsGameBoyAdvanceBroadcasting ||
            engine.IsMasterSystemBroadcasting ||
            engine.IsGameGearBroadcasting;

        var browserAlreadyBroadcasting =
            browserBroadcastArmed ||
            engine.IsBrowserBroadcasting;

        var offeringBrowser =
            browserActive &&
            !browserAlreadyBroadcasting;

        var offeringGameplay =
            gameplayActive &&
            !gameplayAlreadyBroadcasting;


        if ((!offeringGameplay ||
             gameplayStreamOfferDismissed) &&
            (!offeringBrowser ||
             browserStreamOfferDismissed))
        {
            return;
        }

        var isBrowserOffer =
            offeringBrowser &&
            !browserStreamOfferDismissed;

        var exclusiveOfferLocked =
            !HasConfirmedPatreonAccess();


        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f))
        using (var card =
            ImRaii.Child(
                "##exclusivePlaybackStreamOffer",
                new Vector2(
                    -1f,
                    exclusiveOfferLocked
                        ? Ui(140f)
                        : Ui(105f)),
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
                    exclusiveOfferLocked
                        ? Ui(140f)
                        : Ui(105f));


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
                    isBrowserOffer
                        ? "ⓘ   BROWSER STREAM AVAILABLE"
                        : "ⓘ   GAMEPLAY STREAM AVAILABLE");
            }


            ImGui.Spacing();


            ImGui.TextWrapped(
                isBrowserOffer
                    ? exclusiveOfferLocked
                        ? "Browser broadcasting is a Patreon feature. Your browser will continue running locally while broadcast access remains locked."
                        : "The browser runs locally unless you choose to stream it. Would you like to share your browser with this Watch Party?"
                    : exclusiveOfferLocked
                        ? "Gameplay broadcasting is a Patreon feature. Your game will continue playing locally while broadcast access remains locked."
                        : "Games run locally unless you choose to stream them. Would you like to share your gameplay with this Watch Party?");


            ImGui.Dummy(
                UiVec(0f, 5f));


            if (exclusiveOfferLocked)
            {
                using (ImRaii.PushColor(ImGuiCol.Button, PatreonOrange)
                           .Push(ImGuiCol.ButtonHovered, PatreonOrangeHover)
                           .Push(ImGuiCol.ButtonActive, new Vector4(0.92f, 0.42f, 0.08f, 1f)))
                {
                    if (ImGui.Button("Unlock with Patreon"))
                    {
                        patreonPopupOpen = true;
                    }
                }

                ImGui.SameLine();

                if (ImGui.Button("Refresh access"))
                {
                    if (isBrowserOffer)
                        RefreshBrowserPatreonAccess();
                    else
                        RefreshGamePatreonAccess();
                }

                ImGui.SameLine();

                if (ImGui.Button("Not Now"))
                {
                    if (isBrowserOffer)
                        browserStreamOfferDismissed = true;
                    else
                        gameplayStreamOfferDismissed = true;
                }

                var accessMessage = isBrowserOffer
                    ? browserPatreonAccessMessage
                    : gamePatreonAccessMessage;
                if (accessMessage is not null)
                {
                    ImGui.TextColored(
                        Danger,
                        accessMessage);
                }
            }
            else
            {
                if (ImGui.Button(
                        isBrowserOffer
                            ? "Start Browser Stream"
                            : "Start Gameplay Stream"))
                {
                    if (isBrowserOffer)
                    {
                        StartBrowserWatchPartyBroadcast();
                        browserStreamOfferDismissed =
                            true;
                    }
                    else
                    {
                        StartGameWatchPartyBroadcast();
                        gameplayStreamOfferDismissed =
                            true;
                    }
                }

                ImGui.SameLine();

                if (ImGui.Button(
                        "Not Now"))
                {
                    if (isBrowserOffer)
                    {
                        browserStreamOfferDismissed =
                            true;
                    }
                    else
                    {
                        gameplayStreamOfferDismissed =
                            true;
                    }
                }
            }
        }
    }

    private void DrawPartyHeaderCard()
    {
        var isHost =
            stream.Mode ==
            StreamMode.Hosting;

        var hostName =
            isHost
                ? CurrentDisplayName ??
                  CurrentSession?.DisplayName ??
                  "You"
                : joinedHostDisplayName ??
                  "Host";

        ReadActivePartyRoomMetadata(
                   out var visibleDescription,
                   out var categoryIndex,
                   out var adultOnly,
                   out var serverName);

        var cardHeight =
            partyRoomEditing &&
            isHost
                ? Ui(440f)
                : Ui(300f);

        using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                Ui(14f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.WindowPadding,
                   UiVec(
                       20f,
                       0f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       0.035f,
                       0.040f,
                       0.070f,
                       1f)))
        using (var card =
               ImRaii.Child(
                   "##partyRoomDetailsCard",
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

            var cardPos =
                ImGui.GetWindowPos();

            var cardSize =
                ImGui.GetWindowSize();

            var cardMax =
                cardPos +
                cardSize;

            var drawList =
                ImGui.GetWindowDrawList();

            drawList.AddRect(
                cardPos,
                cardMax,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.42f)),
                Ui(14f),
                ImDrawFlags.RoundCornersAll,
                Ui(1.2f));

            //
            // Card heading.
            //
            ImGui.SetCursorScreenPos(
                cardPos +
                UiVec(
                    20f,
                    17f));

            SetUiFontScale(
                1.18f);

            ImGui.TextColored(
                Vector4.One,
                partyRoomEditing
                    ? "Edit room details"
                    : "Room details");

            SetUiFontScale(
                1f);

            if (partyRoomEditing &&
                isHost)
            {
                DrawPartyRoomEditForm(
                    cardPos,
                    cardSize);

                return;
            }

            DrawPartyRoomView(
                drawList,
                cardPos,
                cardSize,
                hostName,
                visibleDescription,
                categoryIndex,
                adultOnly,
                serverName,
                isHost);
        }
    }

    private void DrawPartyRoomView(
        ImDrawListPtr drawList,
        Vector2 cardPos,
        Vector2 cardSize,
        string hostName,
        string visibleDescription,
        int categoryIndex,
        bool adultOnly,
        string serverName,
        bool isHost)
    {
        var rightPadding =
            Ui(20f);

        var leaveWidth =
            Ui(154f);

        var editWidth =
            Ui(155f);

        var buttonHeight =
            Ui(36f);

        var buttonY =
            cardPos.Y +
            Ui(14f);

        //
        // Leave button.
        //
        ImGui.SetCursorScreenPos(
            new Vector2(
                cardPos.X +
                cardSize.X -
                rightPadding -
                leaveWidth,
                buttonY));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   Ui(7f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       Danger.X,
                       Danger.Y,
                       Danger.Z,
                       0.16f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   new Vector4(
                       Danger.X,
                       Danger.Y,
                       Danger.Z,
                       0.28f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   new Vector4(
                       Danger.X,
                       Danger.Y,
                       Danger.Z,
                       0.38f)))
        {
            if (ImGui.Button(
                    "Leave Watch Party",
                    new Vector2(
                        leaveWidth,
                        buttonHeight)))
            {
                RequestLeaveWatchParty();
                partyChatItems.Clear();
                return;
            }
        }

        //
        // Host-only edit button.
        //
        if (isHost)
        {
            ImGui.SetCursorScreenPos(
                new Vector2(
                    cardPos.X +
                    cardSize.X -
                    rightPadding -
                    leaveWidth -
                    Ui(10f) -
                    editWidth,
                    buttonY));

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       Ui(7f)))
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
                           0.24f)))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.34f)))
            using (ImRaii.PushColor(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.65f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameBorderSize,
                       Ui(1f)))
            {
                if (ImGui.Button(
                        "Edit room details",
                        new Vector2(
                            editWidth,
                            buttonHeight)))
                {
                    BeginPartyRoomEditing();
                }
            }
        }

        //
        // Host avatar.
        //
        var avatarOrigin =
            cardPos +
            UiVec(
                20f,
                54f);

        var avatarSize =
            Ui(58f);

        DrawPartyRoomHostAvatar(
            drawList,
            avatarOrigin,
            avatarSize,
            hostName);

        //
        // Room name and hosting state.
        //
        var identityX =
            avatarOrigin.X +
            avatarSize +
            Ui(14f);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                identityX,
                avatarOrigin.Y +
                Ui(2f)),
            ImGui.GetColorU32(
                Vector4.One),
            $"{hostName}'s Watch Party");

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                identityX,
                avatarOrigin.Y +
                Ui(25f)),
            ImGui.GetColorU32(
                AccentHover),
            isHost
                ? "● HOSTING"
                : $"Hosted by {hostName}");

        //
        // Room tags.
        //
        //
        // Leave a clear gap beneath the HOSTING line before the
        // room badges begin.
        //
        var tagsY =
            avatarOrigin.Y +
            Ui(49f);

        var tagX =
            identityX;

        tagX +=
            DrawWatchPartyTag(
                drawList,
                new Vector2(
                    tagX,
                    tagsY),
                WatchPartyVisibilityIcon(
                    stream.RoomKind),
                WatchPartyVisibilityText(
                    stream.RoomKind),
                WatchPartyVisibilityColor(
                    stream.RoomKind)) +
            Ui(7f);

        tagX +=
            DrawWatchPartyTag(
                drawList,
                new Vector2(
                    tagX,
                    tagsY),
                PartyRoomCategoryIcon(
                    categoryIndex),
                PartyRoomCategoryName(
                    categoryIndex)
                    .ToUpperInvariant(),
                AccentHover) +
            Ui(7f);

        if (adultOnly)
        {
            DrawWatchPartyAdultTag(
                drawList,
                new Vector2(
                    tagX,
                    tagsY));
        }

        //
        // Divider below the complete identity area.
        //
        var dividerY =
            cardPos.Y +
            Ui(134f);

        drawList.AddLine(
            new Vector2(
                cardPos.X +
                Ui(20f),
                dividerY),
            new Vector2(
                cardPos.X +
                cardSize.X -
                Ui(20f),
                dividerY),
            ImGui.GetColorU32(
                BorderSubtle),
            Ui(1f));

        //
        // Fixed three-column detail grid.
        //
        var contentLeft =
            cardPos.X +
            Ui(20f);

        var contentWidth =
            cardSize.X -
            Ui(40f);

        var columnGap =
            Ui(18f);

        var columnWidth =
            (
                contentWidth -
                columnGap * 2f
            ) /
            3f;

        var firstRowY =
            dividerY +
            Ui(16f);

        var secondRowY =
            firstRowY +
            Ui(72f);

        DrawPartyRoomDetailCell(
            "description",
            FontAwesomeIcon.CommentAlt,
            "Description",
            string.IsNullOrWhiteSpace(
                visibleDescription)
                ? "No description set"
                : visibleDescription,
            new Vector2(
                contentLeft,
                firstRowY),
            columnWidth);

        DrawPartyRoomDetailCell(
            "location",
            FontAwesomeIcon.MapMarkerAlt,
            "Location",
            string.IsNullOrWhiteSpace(
                stream.RoomLocation)
                ? "Location not specified"
                : stream.RoomLocation!,
            new Vector2(
                contentLeft +
                columnWidth +
                columnGap,
                firstRowY),
            columnWidth);

        DrawPartyRoomDetailCell(
            "server",
            FontAwesomeIcon.Server,
            "Server",
            string.IsNullOrWhiteSpace(
                serverName)
                ? "Server not available"
                : serverName,
            new Vector2(
                contentLeft +
                (
                    columnWidth +
                    columnGap
                ) *
                Ui(2f),
                firstRowY),
            columnWidth);

        DrawPartyRoomDetailCell(
            "category",
            PartyRoomCategoryIcon(
                categoryIndex),
            "Category",
            PartyRoomCategoryName(
                categoryIndex),
            new Vector2(
                contentLeft,
                secondRowY),
            columnWidth);

        DrawPartyRoomDetailCell(
            "type",
            WatchPartyVisibilityIcon(
                stream.RoomKind),
            "Type",
            PartyRoomTypeName(
                stream.RoomKind),
            new Vector2(
                contentLeft +
                columnWidth +
                columnGap,
                secondRowY),
            columnWidth);

        DrawPartyRoomDetailCell(
            "rating",
            adultOnly
                ? FontAwesomeIcon.ExclamationTriangle
                : FontAwesomeIcon.Users,
            "Rating",
            adultOnly
                ? "18+ adult-only"
                : "Everyone",
            new Vector2(
                contentLeft +
                (
                    columnWidth +
                    columnGap
                ) *
                Ui(2f),
                secondRowY),
            columnWidth);
    }

    private void DrawPartyRoomEditForm(
        Vector2 cardPos,
        Vector2 cardSize)
    {
        ImGui.SetCursorScreenPos(
            cardPos +
            UiVec(
                20f,
                53f));

        ImGui.TextColored(
            MutedText,
            "Update the current room without ending the Watch Party.");

        var formOrigin =
            cardPos +
            UiVec(
                20f,
                80f);

        var formWidth =
            cardSize.X -
            Ui(40f);

        ImGui.SetCursorScreenPos(
            formOrigin);

        //
        // Give the editable controls a visible border against the
        // dark Room Details panel.
        //
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.32f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   Ui(1f)))
        {
            DrawCreateRoomFields(
                formWidth);
        }

        //
        // Dedicated settings row below Category and Type.
        //
        var settingsY =
            cardPos.Y +
            cardSize.Y -
            Ui(100f);

        var settingsLabelY =
            settingsY +
            Ui(4f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                cardPos.X +
                Ui(20f),
                settingsLabelY));

        DrawWatchPartyAdultToggle();

        if (createRoomKindIndex == 1)
        {
            ImGui.SetCursorScreenPos(
                new Vector2(
                    cardPos.X +
                    Ui(104f),
                    settingsY));

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       Ui(7f)))
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
                           0.25f)))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.36f)))
            using (ImRaii.PushColor(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.50f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameBorderSize,
                       Ui(1f)))
            {
                if (ImGui.Button(
                        "Set new password",
                        new Vector2(
                            Ui(142f),
                            Ui(30f))))
                {
                    createRoomPassword =
                        string.Empty;

                    createLockedRoomPasswordError =
                        null;

                    createLockedRoomPasswordForRoomEdit =
                        true;

                    createLockedRoomPasswordPopupRequested =
                        true;
                }
            }
        }

        var serverText =
            "Server: information coming soon";

        var serverTextSize =
            ImGui.CalcTextSize(
                serverText);

        ImGui.SetCursorScreenPos(
            new Vector2(
                cardPos.X +
                cardSize.X -
                Ui(20f) -
                serverTextSize.X,
                settingsLabelY));

        ImGui.TextColored(
            MutedText,
            serverText);

        //
        // Footer controls.
        //
        var buttonWidth =
            Ui(140f);

        var buttonGap =
            Ui(10f);

        var buttonY =
            cardPos.Y +
            cardSize.Y -
            Ui(48f);

        var firstButtonX =
            cardPos.X +
            cardSize.X -
            Ui(20f) -
            buttonWidth * 2f -
            buttonGap;

        ImGui.SetCursorScreenPos(
            new Vector2(
                firstButtonX,
                buttonY));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   Ui(7f)))
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
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.55f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   Ui(1f)))
        {
            if (ImGui.Button(
                    "Cancel",
                    new Vector2(
                        buttonWidth,
                        Ui(36f))))
            {
                partyRoomEditing =
                    false;
            }
        }

        ImGui.SameLine(
            0f,
            buttonGap);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   Ui(7f)))
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
                    "Save changes",
                    new Vector2(
                        buttonWidth,
                        Ui(36f))))
            {
                RequestSavePartyRoomDetails();
            }
        }
    }

    private void DrawPartyRoomHostAvatar(
        ImDrawListPtr drawList,
        Vector2 avatarOrigin,
        float avatarSize,
        string hostName)
    {
        var drewAvatar =
            false;

        if (stream.Mode ==
                StreamMode.Hosting &&
            CurrentSession is not null)
        {
            DrawAvatarAt(
                avatarOrigin,
                CurrentSession.AvatarIcon,
                CurrentSession.AvatarColorHex,
                avatarSize,
                CurrentSession.AvatarImageUrl);

            drewAvatar =
                true;
        }
        else if (!string.IsNullOrWhiteSpace(
                     stream.HostId))
        {
            EnsurePartyAvatarLoaded(
                stream.HostId,
                hostName);

            if (partyAvatarCache.TryGetValue(
                    stream.HostId,
                    out var avatar))
            {
                DrawAvatarAt(
                    avatarOrigin,
                    avatar.AvatarIcon,
                    avatar.AvatarColorHex,
                    avatarSize,
                    avatar.AvatarImageUrl);

                drewAvatar =
                    true;
            }
        }

        if (drewAvatar)
        {
            return;
        }

        var center =
            avatarOrigin +
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
                    0.20f)));

        drawList.AddCircle(
            center,
            avatarSize * 0.5f,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.60f)),
            24,
            Ui(1f));

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var glyph =
                FontAwesomeIcon.User
                    .ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                center -
                glyphSize *
                0.5f,
                ImGui.GetColorU32(
                    Accent),
                glyph);
        }
    }

    private void DrawPartyRoomDetailCell(
        string id,
        FontAwesomeIcon icon,
        string label,
        string value,
        Vector2 origin,
        float width)
    {
        ImGui.PushID(
            id);

        var iconColor =
            label == "Rating" &&
            value.StartsWith(
                "18+",
                StringComparison.Ordinal)
                ? Danger
                : AccentHover;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            ImGui.SetCursorScreenPos(
                new Vector2(
                    origin.X,
                    origin.Y +
                    Ui(8f)));

            ImGui.TextColored(
                iconColor,
                icon.ToIconString());
        }

        var textX =
            origin.X +
            Ui(29f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                textX,
                origin.Y));

        SetUiFontScale(
            0.80f);

        ImGui.TextColored(
            MutedText,
            label);

        SetUiFontScale(
            1f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                textX,
                origin.Y +
                Ui(21f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            Math.Max(
                Ui(50f),
                width -
                Ui(29f)));

        ImGui.TextColored(
            Vector4.One,
            value);

        ImGui.PopTextWrapPos();

        if (ImGui.IsItemHovered() &&
            ImGui.CalcTextSize(
                value).X >
            width -
            Ui(29f))
        {
            ImGui.SetTooltip(
                value);
        }

        ImGui.PopID();
    }

    private void BeginPartyRoomEditing()
    {
        if (stream.Mode !=
            StreamMode.Hosting)
        {
            return;
        }

        ReadActivePartyRoomMetadata(
            out var visibleDescription,
            out var categoryIndex,
            out var adultOnly,
            out _);

        createRoomDescription =
            visibleDescription;

        createRoomLocation =
            stream.RoomLocation ??
            string.Empty;

        createRoomCategoryIndex =
            categoryIndex;

        createRoomAdultOnly =
            adultOnly;

        createRoomKindIndex =
            stream.RoomKind switch
            {
                RoomKind.Locked =>
                    1,

                RoomKind.Venue =>
                    2,

                _ =>
                    0,
            };

        createRoomPassword =
            stream.RoomPassword ??
            string.Empty;

        partyRoomEditing =
            true;
    }

    private void RequestSavePartyRoomDetails()
    {
        if (stream.Mode !=
            StreamMode.Hosting)
        {
            partyRoomEditing =
                false;

            return;
        }

        if (createRoomKindIndex == 1 &&
            string.IsNullOrWhiteSpace(
                createRoomPassword))
        {
            createLockedRoomPasswordError =
                null;

            createLockedRoomPasswordForRoomEdit =
                true;

            createLockedRoomPasswordPopupRequested =
                true;

            return;
        }

        SavePartyRoomDetails();
    }

    private void SavePartyRoomDetails()
    {
        if (stream.Mode !=
            StreamMode.Hosting)
        {
            partyRoomEditing =
                false;

            return;
        }

        ApplyCreateRoomToStream();

        _ =
            stream.PublishRoomDetailsAsync();

        partyRoomEditing =
            false;

        Plugin.ChatGui.Print(
            "[AlphaChannel] Watch Party room details updated.");
    }

    private void ReadActivePartyRoomMetadata(
       out string visibleDescription,
       out int categoryIndex,
       out bool adultOnly,
       out string serverName)
    {
        var rawDescription =
            stream.RoomDescription ??
            string.Empty;

        visibleDescription =
            rawDescription.Trim();

        categoryIndex =
            0;

        adultOnly =
            false;

        serverName =
            string.Empty;

        var tokenStart =
            rawDescription.IndexOf(
                "<#",
                StringComparison.Ordinal);

        if (tokenStart < 0)
        {
            return;
        }

        var tokenEnd =
            rawDescription.IndexOf(
                "#>",
                tokenStart + 2,
                StringComparison.Ordinal);

        if (tokenEnd < 0)
        {
            return;
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

        if (tokenParts.Length < 2)
        {
            return;
        }

        var matchedCategory =
            Array.FindIndex(
                WatchPartyCategoryOptions,
                option =>
                    string.Equals(
                        option,
                        tokenParts[0],
                        StringComparison.OrdinalIgnoreCase));

        if (matchedCategory >= 0)
        {
            categoryIndex =
                matchedCategory;
        }

        adultOnly =
            tokenParts[1] == "1";

        if (tokenParts.Length >= 3)
        {
            serverName =
                tokenParts[2].Trim();
        }

        visibleDescription =
            (
                rawDescription[..tokenStart] +
                rawDescription[(tokenEnd + 2)..]
            ).Trim();
    }

    internal void RefreshHostedRoomWorldMetadata()
    {
        if (stream.Mode !=
            StreamMode.Hosting)
        {
            return;
        }

        var currentWorld =
            CurrentWorldName?
                .Trim();

        if (string.IsNullOrWhiteSpace(
                currentWorld))
        {
            return;
        }

        ReadActivePartyRoomMetadata(
            out var visibleDescription,
            out var categoryIndex,
            out var adultOnly,
            out var publishedWorld);

        if (string.Equals(
                currentWorld,
                publishedWorld,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var category =
            WatchPartyCategoryOptions[
                Math.Clamp(
                    categoryIndex,
                    0,
                    WatchPartyCategoryOptions.Length - 1)];

        var rating =
            adultOnly
                ? 1
                : 0;

        var metadata =
            $"<#{category}#{rating}#{currentWorld}#>";

        stream.RoomDescription =
            string.IsNullOrWhiteSpace(
                visibleDescription)
                ? metadata
                : $"{metadata} {visibleDescription.Trim()}";

        _ =
            stream.PublishRoomDetailsAsync();
    }

    private static string PartyRoomCategoryName(
        int categoryIndex)
    {
        return WatchPartyCategoryOptions[
            Math.Clamp(
                categoryIndex,
                0,
                WatchPartyCategoryOptions.Length - 1)];
    }

    private static FontAwesomeIcon PartyRoomCategoryIcon(
        int categoryIndex)
    {
        return categoryIndex switch
        {
            0 =>
                FontAwesomeIcon.PlayCircle,

            1 =>
                FontAwesomeIcon.Film,

            2 =>
                FontAwesomeIcon.Tv,

            3 =>
                FontAwesomeIcon.CommentDots,

            4 =>
                FontAwesomeIcon.Smile,

            5 =>
                FontAwesomeIcon.BroadcastTower,

            6 =>
                FontAwesomeIcon.Gamepad,

            7 =>
                FontAwesomeIcon.Headphones,

            8 =>
                FontAwesomeIcon.Music,

            9 =>
                FontAwesomeIcon.Images,

            10 =>
                FontAwesomeIcon.Bullhorn,

            _ =>
                FontAwesomeIcon.Video,
        };
    }

    private static string PartyRoomTypeName(
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
                UiVec(12f, 9f)))
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
                new Vector2(width, Ui(40f))))
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
                        Ui(210f),
                        Ui(42f))))
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
                    DespawnViewerTv(
                        stopPlayback:
                            !(IsMiniPlayerOpen?.Invoke() ?? false));
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
        DrawExclusivePlaybackStreamOffer();

        ImGui.Dummy(
            UiVec(0f, 12f));

        var current =
       queue.Current;

        var roomState =
            stream.CurrentRoomState;

        //
        // Gameplay and local-video broadcasts do not belong to the saved
        // video queue. Build a temporary display entry from stream.state so
        // the Watch Party page can still describe what is being shared.
        //

        if (roomState is
            {
                Url: { Length: > 0 } roomUrl,
                MediaTitle: { Length: > 0 } roomTitle
            } &&
       (
           current is null ||
           !AudioVisualizerSelection.IsSameMedia(
               current.Url,
               roomUrl)
       ))
        {
            var source =
                roomTitle.StartsWith(
                    "Local Video",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Local Video"
                    : roomTitle.StartsWith(
                        "Playing:",
                        StringComparison.OrdinalIgnoreCase) ||
                      roomTitle.EndsWith(
                          "Gameplay",
                          StringComparison.OrdinalIgnoreCase)
                        ? "Gameplay"
                        : string.Equals(
                            roomTitle,
                            "Live Streaming",
                            StringComparison.OrdinalIgnoreCase)
                            ? "Live Stream"
                            : "Broadcast";

            current =
                new Video.VideoQueueEntry(
                    roomUrl,
                    roomTitle,
                    source,
                    null,
                    roomState.MediaThumbnailUrl);
        }

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

            var heroHeight = Ui(330f);

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
            // ROW 2 — UNIFIED SCREEN CONTROLLER
            // =========================================================

            var controllerY =
                heroHeight +
                gap;

            var footerY =
                MathF.Max(
                    controllerY + Ui(174f) + gap,
                    height - footerHeight);

            var controllerHeight = Ui(190f);

            ImGui.SetCursorPos(
                new Vector2(
                    0f,
                    controllerY));

            DrawPartyScreenControllerCard(
                current,
                isPaused,
                width,
                controllerHeight);

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
                        footerY)));

            DrawPartyNowPlayingFooter(
                width);
        }
    }

    private void DrawPartyDjMutedNotice(
        Video.VideoQueueEntry current,
        Vector2 origin,
        float thumbnailHeight)
    {
        if (!djAutoMuteNoticeVisible ||
            stream.Mode != StreamMode.Hosting)
        {
            return;
        }

        //
        // Hide a stale notice if playback has moved on from the DJ stream.
        //
        if (string.IsNullOrWhiteSpace(
                djAutoMutedStreamUrl) ||
            !AudioVisualizerSelection.IsSameMedia(
                current.Url,
                djAutoMutedStreamUrl))
        {
            djAutoMuteNoticeVisible =
                false;

            djAutoMutedStreamUrl =
                null;

            return;
        }

        //
        // If the host unmuted through the regular Audio card, the notice
        // is no longer needed.
        //
        if (!Plugin.Cfg.Muted)
        {
            djAutoMuteNoticeVisible =
                false;

            djAutoMutedStreamUrl =
                null;

            return;
        }

        var noticePosition =
            new Vector2(
                origin.X,
                origin.Y +
                thumbnailHeight +
                Ui(
                    video.IsAudioOnly
                        ? 78f
                        : 10f));

        ImGui.SetCursorScreenPos(
            noticePosition);

        SetUiFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "Host TV is muted when playing a DJ stream, click");

        ImGui.SameLine(
            0f,
            Ui(4f));

        var linkOrigin =
            ImGui.GetCursorScreenPos();

        const string linkText =
            "here";

        var linkSize =
            ImGui.CalcTextSize(
                linkText);

        if (ImGui.InvisibleButton(
                "##unmuteDjHostTv",
                linkSize))
        {
            Plugin.Cfg.Muted =
                false;

            video.SetOutputMuted(
                false);

            video.SetVolume(
                Plugin.Cfg.Volume);

            Plugin.Cfg.Save();

            djAutoMuteNoticeVisible =
                false;

            djAutoMutedStreamUrl =
                null;
        }

        var hovered =
            ImGui.IsItemHovered();

        ImGui.GetWindowDrawList()
            .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                linkOrigin,
                ImGui.GetColorU32(
                    hovered
                        ? AccentHover
                        : Accent),
                linkText);

        if (hovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);

            ImGui.GetWindowDrawList()
                .AddLine(
                    new Vector2(
                        linkOrigin.X,
                        linkOrigin.Y +
                        linkSize.Y),
                    new Vector2(
                        linkOrigin.X +
                        linkSize.X,
                        linkOrigin.Y +
                        linkSize.Y),
                    ImGui.GetColorU32(
                        AccentHover),
                    Ui(1f));
        }

        ImGui.SameLine(
            0f,
            0f);

        ImGui.TextColored(
            MutedText,
            " to unmute.");

        SetUiFontScale(
            1f);
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
                UiVec(18f, 15f)))
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

            SetUiFontScale(1.10f);

            DrawSectionTitle(
                FontAwesomeIcon.PlayCircle,
                "Now Playing");

            SetUiFontScale(1f);

            ImGui.Dummy(
                UiVec(0f, 8f));

            if (current is null)
            {
                SetUiFontScale(
                    1.12f);

                ImGui.TextColored(
                    Vector4.One,
                    "Nothing is playing");

                SetUiFontScale(
                    1f);

                ImGui.Dummy(
                    UiVec(0f, 5f));

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

                var thumbWidth = Ui(245f);
                var thumbHeight = Ui(145f);

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
                    var isGameplay =
                        string.Equals(
                            current.Source,
                            "Gameplay",
                            StringComparison.OrdinalIgnoreCase);

                    var isLocalVideo =
                        string.Equals(
                            current.Source,
                            "Local Video",
                            StringComparison.OrdinalIgnoreCase);

                    var isLiveStream =
                        string.Equals(
                            current.Source,
                            "Live Stream",
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            current.Source,
                            "Broadcast",
                            StringComparison.OrdinalIgnoreCase);

                    var fallbackColor =
                        isGameplay
                            ? new Vector4(
                                0.20f,
                                0.42f,
                                0.72f,
                                1f)
                            : isLocalVideo
                                ? new Vector4(
                                    0.55f,
                                    0.25f,
                                    0.68f,
                                    1f)
                                : isLiveStream
                                    ? new Vector4(
                                        0.72f,
                                        0.20f,
                                        0.30f,
                                        1f)
                                    : new Vector4(
                                        Accent.X,
                                        Accent.Y,
                                        Accent.Z,
                                        1f);

                    drawList.AddRectFilled(
                        thumbMin,
                        thumbMax,
                        ImGui.GetColorU32(
                            new Vector4(
                                fallbackColor.X,
                                fallbackColor.Y,
                                fallbackColor.Z,
                                0.25f)),
                        9f);

                    drawList.AddRect(
                        thumbMin,
                        thumbMax,
                        ImGui.GetColorU32(
                            new Vector4(
                                fallbackColor.X,
                                fallbackColor.Y,
                                fallbackColor.Z,
                                0.70f)),
                        9f,
                        ImDrawFlags.None,
                        Ui(1f));

                    var fallbackIcon =
                        isGameplay
                            ? FontAwesomeIcon.Gamepad
                            : isLocalVideo
                                ? FontAwesomeIcon.Film
                                : isLiveStream
                                    ? FontAwesomeIcon.BroadcastTower
                                    : FontAwesomeIcon.Play;

                    using (ImRaii.PushFont(
                               UiBuilder.IconFont))
                    {
                        SetUiFontScale(
                            2.2f);

                        var icon =
                            fallbackIcon
                                .ToIconString();

                        var iconSize =
                            ImGui.CalcTextSize(
                                icon);

                        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                            thumbMin +
                            (thumbMax -
                             thumbMin -
                             iconSize) *
                            0.5f,
                            ImGui.GetColorU32(
                                fallbackColor),
                            icon);

                        SetUiFontScale(
                            1f);
                    }
                }

                var contextDividerY =
                    cardScreenPos.Y +
                    height -
                    Ui(96f);

                var contextAnchorHeight =
                    contextDividerY -
                    origin.Y -
                    Ui(6f);

                DrawPartyVisualizerSelector(
                    current,
                    origin,
                    MathF.Min(
                        Ui(430f),
                        mediaWidth - Ui(155f)),
                    contextAnchorHeight);

                if (!video.IsAudioOnly)
                {
                    DrawPartyInlineUpNext(
                        origin,
                        thumbWidth,
                        contextAnchorHeight,
                        MathF.Max(
                            Ui(250f),
                            mediaWidth - Ui(165f)));
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

                DrawPartyMediaBadge(
                    current,
                    new Vector2(
                        contentX,
                        origin.Y));

                // -------------------------------------------------
                // Title
                // -------------------------------------------------

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        contentX,
                        origin.Y + Ui(30f)));

                SetUiFontScale(
                    1.28f);

                ImGui.TextColored(
         Vector4.One,
         FitPartyTextToWidth(
             PrepareTwitchDisplayText(
                 current.Title),
             contentWidth));

                SetUiFontScale(
                    1f);

                if (!IsPartyLiveMedia(current) &&
                    duration > 0f)
                {
                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            origin.Y + Ui(124f)));

                    ImGui.ProgressBar(
                        Math.Clamp(
                            position / duration,
                            0f,
                            1f),
                        new Vector2(
                            contentWidth,
                            Ui(7f)),
                        string.Empty);
                }

                // -------------------------------------------------
                // Source
                // -------------------------------------------------

                if (!string.IsNullOrWhiteSpace(
                        current.Source))
                {
                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            origin.Y + Ui(68f)));

                    SetUiFontScale(
                        0.82f);

                    ImGui.TextColored(
                        MutedText,
                        current.Source);

                    SetUiFontScale(
                        1f);
                }

                // -------------------------------------------------
                // Playback state / time
                // -------------------------------------------------

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        contentX,
                        origin.Y + Ui(94f)));

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

                SetUiFontScale(
                    0.80f);

                ImGui.TextColored(
                    MutedText,
                    !IsPartyLiveMedia(current) &&
                    duration > 0f
                        ? $"{FormatTime(position)} / {FormatTime(duration)}"
                        : "LIVE");

                SetUiFontScale(
                    1f);

                DrawPartyDjMutedNotice(
    current,
    origin,
    thumbHeight);

                ImGui.GetWindowDrawList()
                    .AddLine(
                        new Vector2(
                            origin.X,
                            contextDividerY),
                        new Vector2(
                            cardScreenPos.X +
                            mediaWidth -
                            Ui(18f),
                            contextDividerY),
                        ImGui.GetColorU32(
                            new Vector4(
                                1f,
                                1f,
                                1f,
                                0.09f)),
                        Ui(1f));

                // =================================================
                // TRANSPORT / PROGRESS
                // =================================================

                var transportSize = Ui(42f);
                var transportGap = Ui(9f);

                var controlsWidth =
                    transportSize * 3f +
                    transportGap * 2f;

                var controlsX =
                    cardScreenPos.X +
                    mediaWidth -
                    controlsWidth -
                    18f;

                var controlsY =
                    contextDividerY +
                    Ui(18f);

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
                        Ui(2f),
                        controlsY));

                DrawPartyTransportNextButton(
                    "partyNext",
                    transportSize,
                    !isHost,
                    "Next video");

                // PLACEHOLDER:
                // queue advance later.

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
                        Ui(17f)),
                    new Vector2(
                        dividerX,
                        cardScreenPos.Y +
                        height -
                        Ui(17f)),
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
                    Ui(20f),
                    cardScreenPos.Y +
                    Ui(20f)));

            ImGui.BeginGroup();

            SetUiFontScale(
                0.80f);

            ImGui.TextColored(
                Accent,
                "PLAYBACK SYNC");

            SetUiFontScale(
                1f);

            ImGui.Dummy(
                UiVec(0f, 10f));

            ImGui.TextColored(
                Good,
                "●");

            ImGui.SameLine(
                0f,
                7f);

            SetUiFontScale(
                1.10f);

            ImGui.TextColored(
                Vector4.One,
                stream.Mode ==
                StreamMode.Hosting
                    ? "Hosting playback"
                    : "In sync");

            SetUiFontScale(
                1f);

            ImGui.Dummy(
                UiVec(0f, 4f));

            SetUiFontScale(
                0.76f);

            ImGui.TextColored(
                MutedText,
                stream.Mode ==
                StreamMode.Hosting
                    ? "Everyone follows your playback."
                    : "You're synced with the host.");

            SetUiFontScale(
                1f);

            ImGui.Dummy(
                UiVec(0f, 17f));

            using (ImRaii.Disabled(
                stream.Mode ==
                StreamMode.Hosting))
            {
                if (ImGui.Button(
                    "Re-sync##partyResync",
                    new Vector2(
                        Ui(170f),
                        Ui(38f))))
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



    private static bool IsPartyLiveMedia(
        Video.VideoQueueEntry current)
    {
        if (current.IsTransient)
        {
            return true;
        }

        return current.Source.Equals(
                   "Radio",
                   StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals(
                   "Broadcast",
                   StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals(
                   "Live Stream",
                   StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals(
                   "Gameplay",
                   StringComparison.OrdinalIgnoreCase) ||
               current.Title.Contains(
                   "Live DJ",
                   StringComparison.OrdinalIgnoreCase);
    }

    private void DrawPartyMediaBadge(
        Video.VideoQueueEntry current,
        Vector2 thumbnailOrigin)
    {
        var label =
            current.Source.Equals(
                "YouTube",
                StringComparison.OrdinalIgnoreCase)
                ? "YOUTUBE"
                : current.Source.Equals(
                    "Radio",
                    StringComparison.OrdinalIgnoreCase)
                    ? "RADIO"
                    : current.Title.Contains(
                        "DJ",
                        StringComparison.OrdinalIgnoreCase)
                        ? "LIVE DJ"
                        : IsPartyLiveMedia(current)
                            ? "LIVE"
                            : string.IsNullOrWhiteSpace(current.Source)
                                ? "MEDIA"
                                : current.Source.ToUpperInvariant();

        var textSize =
            ImGui.CalcTextSize(label);

        var badgeSize =
            new Vector2(
                textSize.X + Ui(14f),
                textSize.Y + Ui(7f));

        var badgeMin =
            thumbnailOrigin;

        var isYouTube =
            label == "YOUTUBE";

        var fill =
            isYouTube
                ? new Vector4(
                    0.82f,
                    0.08f,
                    0.12f,
                    0.94f)
                : IsPartyLiveMedia(current)
                    ? new Vector4(
                        0.62f,
                        0.10f,
                        0.20f,
                        0.94f)
                    : new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.90f);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            badgeMin,
            badgeMin + badgeSize,
            ImGui.GetColorU32(fill),
            Ui(5f));

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            badgeMin +
            (badgeSize - textSize) * 0.5f,
            ImGui.GetColorU32(Vector4.One),
            label);
    }

    private void DrawPartyInlineUpNext(
        Vector2 mediaOrigin,
        float thumbnailWidth,
        float thumbnailHeight,
        float availableWidth)
    {
        var origin =
            new Vector2(
                mediaOrigin.X,
                mediaOrigin.Y +
                thumbnailHeight +
                Ui(25f));

        ImGui.SetCursorScreenPos(origin);

        SetUiFontScale(1.00f);

        ImGui.TextColored(
            Accent,
            "UP NEXT");

        SetUiFontScale(1f);

        var viewing = stream.Mode == StreamMode.Viewing;
        var queueCount = viewing ? viewerPartyQueueCount : queue.Entries.Count;

        var queueLabel =
            $"Queue ({queueCount})";

        var queueLabelSize =
            ImGui.CalcTextSize(queueLabel);

        var badgeMin =
            new Vector2(
                origin.X +
                availableWidth -
                queueLabelSize.X -
                Ui(18f),
                origin.Y - Ui(3f));

        ImGui.GetWindowDrawList()
            .AddRectFilled(
                badgeMin,
                badgeMin +
                new Vector2(
                    queueLabelSize.X + Ui(14f),
                    queueLabelSize.Y + Ui(6f)),
                ImGui.GetColorU32(
                    new Vector4(
                        0.085f,
                        0.095f,
                        0.15f,
                        1f)),
                Ui(10f));

        ImGui.GetWindowDrawList()
            .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                badgeMin +
                new Vector2(
                    Ui(7f),
                    Ui(3f)),
                ImGui.GetColorU32(MutedText),
                queueLabel);

        if (queueCount == 0)
        {
            ImGui.SetCursorScreenPos(
                origin +
                new Vector2(
                    0f,
                    Ui(28f)));

            ImGui.TextColored(
                MutedText,
                "Nothing queued next");

            return;
        }

        var next = viewing ? viewerPartyNextQueueEntry : queue.Entries[0];
        if (next is null)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(0f, Ui(28f)));
            ImGui.TextColored(MutedText, "Nothing queued next");
            return;
        }

        var itemOrigin =
            origin +
            new Vector2(
                Ui(126f),
                0f);

        var itemThumbWidth = Ui(105f);
        var itemThumbHeight = Ui(56f);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            itemOrigin,
            itemOrigin +
            new Vector2(
                itemThumbWidth,
                itemThumbHeight),
            ImGui.GetColorU32(
                new Vector4(
                    0.025f,
                    0.03f,
                    0.05f,
                    1f)),
            Ui(6f));

        var thumbnail =
            thumbnails.Get(next.ThumbnailUrl);

        if (thumbnail is not null)
        {
            drawList.AddImageRounded(
                thumbnail.Handle,
                itemOrigin,
                itemOrigin +
                new Vector2(
                    itemThumbWidth,
                    itemThumbHeight),
                Vector2.Zero,
                Vector2.One,
                uint.MaxValue,
                Ui(6f));
        }

        var contentX =
            itemOrigin.X +
            itemThumbWidth +
            Ui(10f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                contentX,
                itemOrigin.Y));

        ImGui.TextColored(
            Vector4.One,
            FitPartyTextToWidth(
                PrepareTwitchDisplayText(
                    next.Title),
                MathF.Max(
                    Ui(90f),
                    badgeMin.X -
                    contentX -
                    Ui(12f))));

        ImGui.SetCursorScreenPos(
            new Vector2(
                contentX,
                itemOrigin.Y +
                Ui(31f)));

        SetUiFontScale(0.72f);

        ImGui.TextColored(
            MutedText,
            next.Duration is { } nextDuration
                ? $"{next.Source}  •  {FormatTime((float)nextDuration.TotalSeconds)}"
                : next.Source);

        SetUiFontScale(1f);
    }

    private static string FitPartyTextToWidth(
        string text,
        float maximumWidth)
    {
        if (string.IsNullOrEmpty(text) ||
            ImGui.CalcTextSize(text).X <= maximumWidth)
        {
            return text;
        }

        const string ellipsis = "…";

        var low = 0;
        var high = text.Length;

        while (low < high)
        {
            var middle =
                (low + high + 1) / 2;

            var candidate =
                text[..middle].TrimEnd() +
                ellipsis;

            if (ImGui.CalcTextSize(candidate).X <= maximumWidth)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return text[..low].TrimEnd() + ellipsis;
    }

    private void DrawPartyScreenControllerCard(
        Video.VideoQueueEntry? current,
        bool isPaused,
        float width,
        float height)
    {
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   Ui(12f))
               .Push(
                   ImGuiStyleVar.WindowPadding,
                   new Vector2(
                       Ui(18f),
                       Ui(14f))))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       0.035f,
                       0.04f,
                       0.07f,
                       1f)))
        using (var card =
               ImRaii.Child(
                   "##partyScreenControllerCard",
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

            SetUiFontScale(1.10f);

            DrawSectionTitle(
                FontAwesomeIcon.Tv,
                "Screen Controller");

            SetUiFontScale(1f);

            var contentWidth =
                ImGui.GetContentRegionAvail().X;

            var gap = Ui(28f);
            var leftWidth =
                contentWidth * 0.38f;

            var rightWidth =
                contentWidth - leftWidth - gap;

            var windowOrigin =
                ImGui.GetWindowPos();

            var contentOrigin =
                ImGui.GetWindowContentRegionMin() +
                windowOrigin;

            var sectionY =
                contentOrigin.Y + Ui(37f);

            var leftX =
                contentOrigin.X;

            var rightX =
                leftX + leftWidth + gap;

            var drawList =
                ImGui.GetWindowDrawList();

            drawList.AddLine(
                new Vector2(
                    rightX - gap * 0.5f,
                    sectionY),
                new Vector2(
                    rightX - gap * 0.5f,
                    windowOrigin.Y + height - Ui(16f)),
                ImGui.GetColorU32(
                    new Vector4(
                        1f,
                        1f,
                        1f,
                        0.07f)));

            DrawPartyScreenControllerTv(
                current,
                isPaused,
                leftX,
                sectionY,
                leftWidth);

            DrawPartyScreenControllerAudio(
                rightX,
                sectionY,
                rightWidth);

            DrawPartyScreenControllerPlacement(
                rightX,
                sectionY + Ui(104f),
                rightWidth);
        }
    }

    private void DrawPartyScreenControllerTv(
        Video.VideoQueueEntry? current,
        bool isPaused,
        float x,
        float y,
        float width)
    {
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

        var iconCenter =
            new Vector2(
                x + Ui(55f),
                y + Ui(54f));

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddCircle(
            iconCenter,
            Ui(37f),
            ImGui.GetColorU32(
                tvSpawned
                    ? Accent
                    : MutedText),
            0,
            Ui(2f));

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            SetUiFontScale(1.45f);

            var icon =
                FontAwesomeIcon.Tv.ToIconString();

            var iconSize =
                ImGui.CalcTextSize(icon);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconCenter - iconSize * 0.5f,
                ImGui.GetColorU32(
                    tvSpawned
                        ? Accent
                        : MutedText),
                icon);

            SetUiFontScale(1f);
        }

        var detailsX =
            x + Ui(120f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                detailsX,
                y + Ui(18f)));

        ImGui.TextColored(
            tvSpawned
                ? Good
                : MutedText,
            tvSpawned
                ? "●"
                : "○");

        ImGui.SameLine(0f, Ui(7f));

        ImGui.TextColored(
            Vector4.One,
            statusText);

        ImGui.SetCursorScreenPos(
            new Vector2(
                detailsX,
                y + Ui(45f)));

        SetUiFontScale(0.75f);

        ImGui.TextColored(
            MutedText,
            tvSpawned
                ? "Your watch-party screen is active."
                : "Spawn your TV to start watching.");

        SetUiFontScale(1f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                detailsX,
                y + Ui(78f)));

        DrawPartyTvSpawnButton();
    }

    private void DrawPartyScreenControllerAudio(
        float x,
        float y,
        float width)
    {
        var volume =
            Plugin.Cfg.Volume;

        ImGui.SetCursorScreenPos(
            new Vector2(
                x,
                y + Ui(2f)));

        ImGui.TextColored(
            Vector4.One,
            "TV Volume");

        var percent =
            $"{volume}%";

        var percentSize =
            ImGui.CalcTextSize(percent);

        ImGui.SetCursorScreenPos(
            new Vector2(
                x + width - percentSize.X,
                y + Ui(2f)));

        ImGui.TextColored(
            volume > 100
                ? Gold
                : Vector4.One,
            percent);

        ImGui.SetCursorScreenPos(
            new Vector2(
                x,
                y + Ui(28f)));

        ImGui.SetNextItemWidth(width);

        if (ImGui.SliderInt(
                "##partyControllerVolume",
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

        ImGui.SetCursorScreenPos(
            new Vector2(
                x,
                y + Ui(67f)));

        var muted =
            Plugin.Cfg.Muted;

        var buttonGap = Ui(8f);
        var buttonWidth =
            MathF.Min(
                Ui(155f),
                (width - buttonGap) * 0.44f);

        var ffxivButtonWidth =
            MathF.Min(
                Ui(190f),
                width - buttonWidth - buttonGap);

        if (ImGui.Button(
                muted
                    ? "Unmute TV"
                    : "Mute TV",
                new Vector2(
                    buttonWidth,
                    Ui(32f))))
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

        ImGui.SameLine(0f, buttonGap);

        var ffxivMuted =
            IsFfxivSoundMuted();

        if (ImGui.Button(
                ffxivMuted
                    ? "Restore FFXIV Sounds"
                    : "Mute FFXIV Sounds",
                new Vector2(
                    ffxivButtonWidth,
                    Ui(32f))))
        {
            SetFfxivSoundMuted(
                !ffxivMuted);
        }
    }

    private void DrawPartyScreenControllerPlacement(
        float x,
        float y,
        float width)
    {
        var isHost =
            stream.Mode == StreamMode.Hosting;

        var syncEnabled =
            isHost || partySyncTvPlacement;

        ImGui.SetCursorScreenPos(
            new Vector2(
                x,
                y + Ui(6f)));

        var wasSyncEnabled = partySyncTvPlacement;

        DrawPartyPlacementToggle(
            ref syncEnabled,
            isHost);

        if (!isHost)
        {
            partySyncTvPlacement =
                syncEnabled;

            if (wasSyncEnabled && !partySyncTvPlacement)
            {
                ApplySavedViewerScreenPlacement();
            }
        }

        ImGui.SameLine(0f, Ui(9f));

        ImGui.TextColored(
            Vector4.One,
            "Match TV position & size with host");

        ImGui.SetCursorScreenPos(
            new Vector2(
                x,
                y + Ui(35f)));

        SetUiFontScale(0.75f);

        ImGui.PushTextWrapPos(
            x + width - Ui(180f));

        ImGui.TextColored(
            MutedText,
            isHost
                ? "Disable to adjust tv position and size manually"
                : partySyncTvPlacement
                    ? "Following the host's TV position and size."
                    : "Placement sync is disabled.");

        ImGui.PopTextWrapPos();

        SetUiFontScale(1f);

        var canOpenSettings =
            isHost ||
            !partySyncTvPlacement;

        ImGui.SetCursorScreenPos(
            new Vector2(
                x + width - Ui(164f),
                y));

        using (ImRaii.Disabled(
                   !canOpenSettings))
        {
            if (ImGui.Button(
                    "Screen Settings",
                    new Vector2(
                        Ui(164f),
                        Ui(32f))))
            {
                currentPage = HomePage.Screen;
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
                    center.X - Ui(4f),
                    center.Y - Ui(6f)),
                new Vector2(
                    center.X - Ui(4f),
                    center.Y + Ui(6f)),
                new Vector2(
                    center.X + Ui(6f),
                    center.Y),
                color);
        }
        else
        {
            // Pause bars.
            drawList.AddRectFilled(
                new Vector2(
                    center.X - Ui(5f),
                    center.Y - Ui(6f)),
                new Vector2(
                    center.X - Ui(1.5f),
                    center.Y + Ui(6f)),
                color,
                1f);

            drawList.AddRectFilled(
                new Vector2(
                    center.X + Ui(1.5f),
                    center.Y - Ui(6f)),
                new Vector2(
                    center.X + Ui(5f),
                    center.Y + Ui(6f)),
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
                center.X - Ui(5f),
                center.Y - Ui(6f)),
            new Vector2(
                center.X - Ui(5f),
                center.Y + Ui(6f)),
            color,
            2f);

        drawList.AddTriangleFilled(
            new Vector2(
                center.X + Ui(5f),
                center.Y - Ui(6f)),
            new Vector2(
                center.X + Ui(5f),
                center.Y + Ui(6f)),
            new Vector2(
                center.X - Ui(3f),
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
        var triangleHalfHeight = Ui(5f);
        var triangleWidth = Ui(7f);

        var triangleCenter =
            new Vector2(
                center.X - Ui(2f),
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
                center.X + Ui(5f),
                center.Y - Ui(5f)),
            new Vector2(
                center.X + Ui(5f),
                center.Y + Ui(5f)),
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
                UiVec(18f, 14f)))
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
                    Ui(18f),
                    Ui(25f));

            var badgeMin =
                ImGui.GetWindowPos() +
                new Vector2(
                    ImGui.GetWindowWidth() -
                    badgeSize.X -
                    Ui(16f),
                    Ui(10f));

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
                .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    badgeMin +
                    (badgeSize -
                     queueTextSize) *
                    0.5f,
                    ImGui.GetColorU32(
                        MutedText),
                    queueLabel);

            ImGui.SetCursorPos(
                UiVec(18f, 48f));

            if (queueCount == 0)
            {
                ImGui.TextColored(
                    Vector4.One,
                    "Nothing queued next");

                ImGui.Dummy(
                    UiVec(0f, 3f));

                SetUiFontScale(
                    0.76f);

                ImGui.TextColored(
                    MutedText,
                    "New party media will appear here.");

                SetUiFontScale(
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

            SetUiFontScale(
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
                PrepareTwitchDisplayText(
                    next.Title));

            ImGui.PopTextWrapPos();

            SetUiFontScale(
                1f);

            ImGui.SetCursorScreenPos(
                new Vector2(
                    contentX,
                    origin.Y + Ui(48f)));

            SetUiFontScale(
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

            SetUiFontScale(
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
                UiVec(18f, 14f)))
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
                UiVec(56f, 91f);

            var circleRadius = Ui(31f);

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
                SetUiFontScale(
                    1.35f);

                var glyph =
                    FontAwesomeIcon.Tv
                        .ToIconString();

                var glyphSize =
                    ImGui.CalcTextSize(
                        glyph);

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    iconCenter -
                    glyphSize *
                    0.5f,
                    ImGui.GetColorU32(
                        tvSpawned
                            ? Accent
                            : MutedText),
                    glyph);

                SetUiFontScale(
                    1f);
            }

            // =====================================================
            // State
            // =====================================================

            ImGui.SetCursorPos(
                UiVec(104f, 58f));

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
                UiVec(104f, 84f));

            SetUiFontScale(
                0.76f);

            ImGui.TextColored(
                MutedText,
                tvSpawned
                    ? "Your watch-party screen is active."
                    : "Spawn your TV to start watching.");

            SetUiFontScale(
                1f);

            // =====================================================
            // Primary TV action
            // =====================================================

            ImGui.SetCursorPos(
                new Vector2(
                    ImGui.GetWindowWidth() -
                    Ui(188f),
                    Ui(72f)));

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
                UiVec(18f, 14f)))
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
                UiVec(18f, 39f));

            ImGui.TextColored(
                Vector4.One,
                "TV Volume");

            if (volume > 100)
            {
                ImGui.SameLine(
                    0f,
                    10f);

                SetUiFontScale(
                    0.68f);

                ImGui.TextColored(
                    Gold,
                    "Volume levels higher than 100% may distort audio quality");

                SetUiFontScale(
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
                    Ui(18f),
                    Ui(39f)));

            ImGui.TextColored(
                volume > 100
                    ? Gold
                    : Vector4.One,
                percent);

            // =====================================================
            // Slider
            // =====================================================

            ImGui.SetCursorPos(
                UiVec(18f, 61f));

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
                UiVec(18f, 103f));

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
                    UiVec(128f, 32f)))
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
                    UiVec(170f, 32f)))
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
                UiVec(18f, 14f)))
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
                UiVec(18f, 48f));

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
                UiVec(18f, 78f));

            SetUiFontScale(
                0.76f);

            ImGui.TextColored(
                MutedText,
                isHost
                    ? "Disable to manually resize / position the TV"
                    : partySyncTvPlacement
                        ? "Disable to manually resize / position the TV"
                        : "Placement sync is disabled.");

            SetUiFontScale(
                1f);

            var canOpenSettings =
                isHost ||
                !partySyncTvPlacement;

            ImGui.SetCursorPos(
                new Vector2(
                    ImGui.GetWindowWidth() -
                    Ui(166f),
                    Ui(99f)));

            using (ImRaii.Disabled(
                !canOpenSettings))
            {
                if (ImGui.Button(
                    "Screen Settings",
                    UiVec(148f, 34f)))
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

        if (disabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Can not be changed as host");
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
        var footerHeight = Ui(50f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            12f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(18f, 9f)))
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

            var actionWidth = Ui(154f);

            ImGui.SetCursorPos(
                new Vector2(
                    ImGui.GetWindowWidth() -
                    actionWidth -
                    Ui(12f),
                    Ui(8f)));

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
                        Ui(34f))))
                {
                    // Host confirmation can be added before shipping.
                    RequestLeaveWatchParty();

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
                    UiVec(16f, 14f)))
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

    private bool CurrentMediaDisallowsVideoRequests()
    {
        var engine = screenController.Engine;
        if (engine.IsPlayingGame ||
            gameBroadcastArmed ||
            localVideoBroadcastArmed ||
            djBroadcastingToWatchParty)
        {
            return true;
        }

        var current = queue.Current;
        if (current is null)
        {
            return false;
        }

        if (ImageMediaSelection.IsImageMediaUrl(current.Url))
        {
            return true;
        }

        return current.Source.Equals("DJ Live", StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals("Radio", StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals("Live Stream", StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals("Broadcast", StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals("Local Video", StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals("Gameplay", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySavedViewerScreenPlacement()
    {
        screenController.Engine.SetScreenTransform(
            Plugin.Cfg.ScreenPosition,
            Plugin.Cfg.ScreenYaw,
            Plugin.Cfg.DisableFixedScreenScaleRatio,
            Plugin.Cfg.ScreenWidthScale,
            Plugin.Cfg.ScreenHeightScale);
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
            UiVec(0f, 12f));

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
                UiVec(18f, 16f)))
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
               "Room members");

            var countText =
                $"{participantCount} " +
                (
                    participantCount == 1
                        ? "person"
                        : "people"
                );

            var countSize =
                ImGui.CalcTextSize(
                    countText);

            ImGui.SetCursorPos(
                new Vector2(
                    ImGui.GetWindowWidth() -
                    Ui(18f) -
                    countSize.X,
                    Ui(17f)));

            ImGui.TextColored(
                MutedText,
                countText);

            //
            // Restore an explicit left-side cursor after drawing the
            // independently positioned count.
            //
            ImGui.SetCursorPos(
                UiVec(
                    18f,
                    45f));

            SetUiFontScale(
                0.82f);

            ImGui.TextColored(
                MutedText,
                "People currently in this Watch Party.");

            SetUiFontScale(
                1f);

            ImGui.SetCursorPos(
                UiVec(
                    18f,
                    72f));

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
                    UiVec(0f, 7f));

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

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
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

            SetUiFontScale(
                0.98f);

            ImGui.TextColored(
                isHost
                    ? AccentHover
                    : Vector4.One,
                displayName);

            SetUiFontScale(
                1f);

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

                var badgePadX = Ui(7f);

                var badgePadY = Ui(3f);

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

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
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
                    Ui(39f)));

            SetUiFontScale(
                0.73f);

            ImGui.TextColored(
                Good,
                "● In Watch Party");

            SetUiFontScale(
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

            var makeHostWidth = Ui(100f);

            var kickWidth = Ui(112f);

            var banWidth = Ui(106f);

            var buttonHeight = Ui(31f);

            var rightPadding = Ui(12f);

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

            var requestsDisabled = CurrentMediaDisallowsVideoRequests();
            var permission = videoRequestPermissions.TryGetValue(userId, out var storedPermission)
                ? storedPermission
                : VideoRequestPermission.Allow;
            var permissionLabel = permission switch
            {
                VideoRequestPermission.AutoAccept => "Auto-accept",
                VideoRequestPermission.NotAllowed => "Not allowed",
                _ => "Allow",
            };

            var requestLabelWidth = Ui(98f);
            var requestComboWidth = Ui(112f);
            var requestGap = Ui(8f);
            var requestX = actionX - requestLabelWidth - requestComboWidth - requestGap;

            ImGui.SetCursorScreenPos(new Vector2(requestX, actionY + Ui(7f)));
            ImGui.TextUnformatted("Video Requests:");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Set video request permissions");
            }

            ImGui.SetCursorScreenPos(new Vector2(requestX + requestLabelWidth, actionY));
            ImGui.SetNextItemWidth(requestComboWidth);
            using (ImRaii.Disabled(requestsDisabled))
            {
                if (ImGui.BeginCombo("##videoRequestPermission", permissionLabel))
                {
                    foreach (var option in Enum.GetValues<VideoRequestPermission>())
                    {
                        var optionLabel = option switch
                        {
                            VideoRequestPermission.AutoAccept => "Auto-accept",
                            VideoRequestPermission.NotAllowed => "Not allowed",
                            _ => "Allow",
                        };

                        if (ImGui.Selectable(optionLabel, option == permission))
                        {
                            videoRequestPermissions[userId] = option;
                        }
                    }

                    ImGui.EndCombo();
                }
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(requestsDisabled
                    ? "Current media type does not allow video requests"
                    : "Set video request permissions");
            }

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
                        "Make host",
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
                    "Kick",
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
                    "Ban",
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
                UiVec(20f, 18f)))
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

            SetUiFontScale(1.15f);

            ImGui.TextColored(
                Vector4.One,
                title);

            SetUiFontScale(1f);

            ImGui.Dummy(
                UiVec(0f, 6f));

            ImGui.TextColored(
                MutedText,
                description);
        }
    }

    private void DrawLegacyPartyPanel()
    {
        if (CurrentSession is null)
        {
            SetUiFontScale(1.15f);

            ImGui.TextColored(
                Vector4.One,
                "Watch party");

            SetUiFontScale(1f);

            ImGui.Dummy(UiVec(0f, 6f));

            ImGui.TextColored(
                MutedText,
                "Sign in to host or join a synced watch party.");

            ImGui.Dummy(UiVec(0f, 12f));

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
                    UiVec(120f, 34f)))
                {
                    currentPage = HomePage.Settings;
                }
            }

            return;
        }

        // ---------------------------------------------------------
        // Heading
        // ---------------------------------------------------------

        SetUiFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Watch party");

        SetUiFontScale(1f);

        ImGui.Dummy(UiVec(0f, 4f));

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
                                UiVec(14f, 12f));

                            ImGui.TextColored(
                                Good,
                                "HOSTING");

                            // Private party toggle in top-right.
                            ImGui.SetCursorPos(
                                new Vector2(
                                    ImGui.GetWindowWidth() - Ui(140f),
                                    Ui(9f)));

                            if (ImGui.Checkbox(
                                "Private party",
                                ref isPrivate))
                            {
                                stream.IsPrivate =
                                    isPrivate;
                            }

                            // Host
                            ImGui.SetCursorPos(
                                UiVec(14f, 39f));

                            ImGui.TextColored(
                                Vector4.One,
                                $"Host: {CurrentDisplayName ?? "You"}");

                            // Status
                            ImGui.SetCursorPos(
                                UiVec(14f, 63f));

                            SetUiFontScale(0.80f);

                            ImGui.TextColored(
                                MutedText,
                                $"{stream.Roster.Length} watching  •  Playback stays synced to you");

                            SetUiFontScale(1f);

                            // Room name label
                            ImGui.SetCursorPos(
                                UiVec(14f, 91f));

                            SetUiFontScale(0.78f);

                            ImGui.TextColored(
                                MutedText,
                                "Room name");

                            SetUiFontScale(1f);

                            // Room name input
                            ImGui.SetCursorPos(
                                UiVec(14f, 111f));

                            ImGui.SetNextItemWidth(
                                ImGui.GetWindowWidth() - 106f);

                            using (ImRaii.PushStyle(
                                ImGuiStyleVar.FrameRounding,
                                7f)
                                .Push(
                                    ImGuiStyleVar.FramePadding,
                                    UiVec(12f, 7f)))
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
                                    UiVec(64f, 32f));
                            }
                        }
                    }

                    ImGui.Dummy(
                        UiVec(0f, 10f));

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
                            UiVec(150f, 32f)))
                        {
                            ImGui.SetClipboardText(
                                $"Come watch with me! Right-click my character and choose \"Join Stream\" " +
                                $"(or open AlphaChannel → Player and join \"{CurrentDisplayName}\").");
                        }
                    }

                    ImGui.Dummy(
                        UiVec(0f, 14f));

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
                                UiVec(14f, 12f));

                            ImGui.TextColored(
                                Good,
                                "IN ROOM");

                            ImGui.SetCursorPos(
                                UiVec(14f, 38f));

                            ImGui.TextColored(
                                Vector4.One,
                                joinedHostDisplayName is { } host
                                    ? $"{host}'s room"
                                    : "A friend's room");

                            ImGui.SetCursorPos(
                                UiVec(14f, 64f));

                            SetUiFontScale(0.82f);

                            ImGui.TextColored(
                                MutedText,
                                $"{stream.Roster.Length} also here  •  Playback is synced to the host");

                            SetUiFontScale(1f);
                        }
                    }

                    ImGui.Dummy(UiVec(0f, 14f));

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
                            UiVec(120f, 34f)))
                        {
                            RequestLeaveWatchParty();
                            partyChatItems.Clear();
                        }
                    }

                    ImGui.Dummy(UiVec(0f, 20f));

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
                    SetUiFontScale(0.88f);

                    ImGui.TextColored(
                        MutedText,
                        "Host automatically while playing, or join a friend's watch party.");

                    SetUiFontScale(1f);

                    ImGui.Dummy(UiVec(0f, 14f));

                    ImGui.TextColored(
                        MutedText,
                        "Join a party");

                    ImGui.Dummy(UiVec(0f, 4f));

                    ImGui.SetNextItemWidth(-118f);

                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        10f)
                        .Push(
                            ImGuiStyleVar.FramePadding,
                            UiVec(14f, 8f)))
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
                        UiVec(88f, 38f)))
                    {
                        DoJoin(joinHostNameInput, joinPasswordInput);
                    }
                }

                if (joinError is { } error)
                {
                    ImGui.Dummy(UiVec(0f, 8f));

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
            SetUiFontScale(1.15f);

            ImGui.TextColored(
                Vector4.One,
                "Chat");

            SetUiFontScale(1f);

            ImGui.Dummy(
                UiVec(0f, 6f));

            ImGui.TextColored(
                MutedText,
                "Join or host a watch party to use chat and reactions.");

            return;
        }

        DrawPartyChatFeed();

        ImGui.Dummy(
            UiVec(0f, 8f));

        DrawPartyChatComposer();

        ImGui.Dummy(
            UiVec(0f, 10f));

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

                        SetUiFontScale(
                            0.95f);

                        var initialSize =
                            ImGui.CalcTextSize(
                                initial);

                        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                            avatarCenter -
                            initialSize * 0.5f,
                            ImGui.GetColorU32(
                                Vector4.One),
                            initial);

                        SetUiFontScale(
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

                    SetUiFontScale(
                        0.95f);

                    ImGui.TextColored(
                        AccentHover,
                        senderName);

                    SetUiFontScale(
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

                        var badgePadX = Ui(7f);
                        var badgePadY = Ui(3f);

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

                        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
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

                        SetUiFontScale(
                            0.72f);

                        ImGui.TextColored(
                            MutedText,
                            receivedAt.ToString(
                                "t"));

                        SetUiFontScale(
                            1f);
                    }

                    // =========================================================
                    // Message body
                    // =========================================================

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            origin.Y + Ui(24f)));

                    SetUiFontScale(
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

                    SetUiFontScale(
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
                        UiVec(14f, 12f)))
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
        Ui(16f)),
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
                                    cardMin.X + Ui(3f),
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

                            SetUiFontScale(
                                0.88f);

                            ImGui.TextColored(
                                Accent,
                                "VIDEO REQUEST");

                            SetUiFontScale(
                                1f);

                            ImGui.SameLine(
                                0f,
                                8f);

                            SetUiFontScale(
                                0.88f);

                            ImGui.TextColored(
                                MutedText,
                                $"{item.Name} requested a video");

                            SetUiFontScale(
                                1f);

                            // -------------------------------------------------
                            // Media row
                            // -------------------------------------------------

                            var mediaY =
                                origin.Y + 32f;

                            var thumbnailMin =
                                new Vector2(
                                    origin.X + Ui(7f),
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

                                    drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
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

                            SetUiFontScale(
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

                            SetUiFontScale(
                                1f);

                            ImGui.SetCursorScreenPos(
                                new Vector2(
                                    contentX,
                                    mediaY + Ui(58f)));

                            SetUiFontScale(
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

                            SetUiFontScale(
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

                                        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
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

                                var gameplayActive = screenController.Engine.IsPlayingGame;

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

                                        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
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
                        UiVec(0f, 3f));

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

                    SetUiFontScale(
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

                    SetUiFontScale(
                        1f);

                    ImGui.Dummy(
                        UiVec(0f, 8f));

                    break;
                }

            case PartyChatItemKind.MediaPlaying:
                {
                    ImGui.TextColored(
                        Good,
                        "▶ Now playing");

                    SetUiFontScale(
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

                    SetUiFontScale(
                        1f);

                    ImGui.Dummy(
                        UiVec(0f, 8f));

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

                        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
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
                            avatarSize + Ui(8f),
                            Ui(2f));

                    var pillHeight =
                        30f;

                    var nameText =
                        $"{senderName} reacted with";

                    SetUiFontScale(
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

                    drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                        pillMin +
                        UiVec(12f, 7f),
                        ImGui.GetColorU32(
                            MutedText),
                        nameText);

                    using (ImRaii.PushFont(
                        UiBuilder.IconFont))
                    {
                        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                            pillMin +
                            new Vector2(
                                Ui(18f) + nameSize.X,
                                Ui(7f)),
                            ImGui.GetColorU32(
                                AccentHover),
                            item.Text);
                    }

                    SetUiFontScale(
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
                            rowHeight + Ui(4f)));

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
                SetUiFontScale(
                    0.88f);

                ImGui.TextColored(
                    MutedText,
                    "No messages yet.");

                SetUiFontScale(
                    1f);
            }
            else
            {
                var chatContentInset = Ui(16f);

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
                    Ui(8f))))
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
                    UiVec(14f, 10f)))
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
                UiVec(300f, 0f),
                ImGuiCond.Appearing);

            using (ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(14f, 12f))
                .Push(
                    ImGuiStyleVar.PopupRounding,
                    10f))
            {
                if (ImGui.BeginPopup(
                        "##partyChatOptionsPopup"))
                {
                    SetUiFontScale(
                        0.90f);

                    ImGui.TextColored(
                        Vector4.One,
                        "Chat options");

                    SetUiFontScale(
                        1f);

                    ImGui.Dummy(
                        UiVec(0f, 5f));

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
                        UiVec(0f, 3f));

                    SetUiFontScale(
                        0.76f);

                    ImGui.TextColored(
                        MutedText,
                        "Show Watch Party messages in your\nnormal FFXIV chatbox as well.");

                    SetUiFontScale(
                        1f);

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
                        dividerTop + Ui(44f)),
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
                SetUiFontScale(
                    0.82f);

                ImGui.TextColored(
                    Accent,
                    FontAwesomeIcon.Bolt.ToIconString());

                SetUiFontScale(
                    1f);
            }

            ImGui.SameLine(
                0f,
                5f);

            SetUiFontScale(
                0.68f);

            ImGui.TextColored(
                AccentHover,
                "REACT LIVE");

            SetUiFontScale(
                1f);

            // -----------------------------------------------------
            // Reaction buttons
            // -----------------------------------------------------

            ImGui.SetCursorPos(
                new Vector2(
                    reactX,
                    reactHeaderY + Ui(15f)));

            DrawCompactReactions(
                reactWidthAvailable);
        }
    }
}
