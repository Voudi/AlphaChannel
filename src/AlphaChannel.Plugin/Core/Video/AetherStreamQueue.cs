namespace AlphaChannel.Plugin.Video;

// Title/Source/Duration/ThumbnailUrl start out as just the raw URL and get filled in
// asynchronously - see AetherStreamQueue's enrichment step. Mutable (not a record) precisely so
// that fill-in can update the same instance already sitting in the queue/UI rather than needing
// every caller to track down and replace it after a reorder. Reference equality (the class
// default) is also the correct behavior for List.Remove here, unlike a record's value equality,
// which could match the wrong entry if two queued items happen to share every field.
internal sealed class VideoQueueEntry
{
    public Guid Id { get; } =
        Guid.NewGuid();

    public string Url { get; }

    public string Title { get; set; }

    public string Source { get; set; }

    public TimeSpan? Duration { get; set; }

    public string? ThumbnailUrl { get; set; }

    public double ResumePositionSeconds { get; set; }

    public bool IsTransient { get; }

    public VideoQueueEntry(
     string url,
     string title,
     string source,
     TimeSpan? duration,
     string? thumbnailUrl,
     double resumePositionSeconds = 0d,
     bool isTransient = false)
    {
        Url =
            url;

        Title =
            title;

        Source =
            source;

        Duration =
            duration;

        ThumbnailUrl =
            thumbnailUrl;

        ResumePositionSeconds =
            Math.Max(
                0d,
                resumePositionSeconds);

        IsTransient =
            isTransient;
    }
}

// Manages Alpha Channel's playback queue independently of VideoPlayer. One item plays at a time;
// the next is pushed when VideoPlayer.IsIdle() reports the current one has naturally ended.
// Stage 0 established that end-of-playback is only observable by polling mpv's idle-active
// property (docs/video-pipeline.md in the AlphaChannel repo, §"when is state published" for the
// equivalent finding on the reference implementation) - there's no event to subscribe to, so
// this polls, but on a tick-throttled interval, never every frame.
internal sealed class AetherStreamQueue : IDisposable
{
    private const int PollEveryTicks = 30; // roughly twice a second at 60fps, not per-frame

    private readonly VideoPlayer video;
    private readonly VideoUrlResolver metadataResolver = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<VideoQueueEntry> entries = new();
    private int tickCounter;
    private int progressSaveCounter;

    private bool wasIdle =
        true;

    private bool autoAdvanceArmed;

    private double? pendingResumePositionSeconds;

    private bool pendingStartUnpause;

    // The saved queue profile that supplied Current. This remains unchanged
    // when the user activates another queue while the video continues.
    private SavedQueueProfile? currentOwnerProfile;

    // Restores the upcoming list (not whatever was actively Current - that doesn't survive a
    // reload anyway, since the mpv session behind it is gone) from Configuration, so a relog or
    // plugin reload mid watch-party doesn't wipe out what was queued up.
    public AetherStreamQueue(VideoPlayer video)
    {
        this.video = video;
        video.ExternalPlaybackTakingOver += OnExternalPlaybackTakingOver;

        LoadEntries(Plugin.Cfg.VideoQueue);
    }

    public AetherStreamQueue(VideoPlayer video, IEnumerable<VideoQueueRecord> records)
    {
        this.video = video;
        video.ExternalPlaybackTakingOver += OnExternalPlaybackTakingOver;

        LoadEntries(records);
    }

    private void OnExternalPlaybackTakingOver()
    {
        if (video.HasVideoPlaybackSession)
        {
            SaveCurrentProgress();
        }

        Current = null;
        currentOwnerProfile = null;
        pendingResumePositionSeconds = null;
        pendingStartUnpause = false;
        autoAdvanceArmed = false;
        wasIdle = true;

        Persist();
    }

    private void LoadEntries(
    IEnumerable<VideoQueueRecord> records)
    {
        foreach (var record in records)
        {
            entries.Add(
     new VideoQueueEntry(
         record.Url,
         record.Title,
         record.Source,
         record.DurationSeconds is { } seconds
             ? TimeSpan.FromSeconds(
                 seconds)
             : null,
         record.ThumbnailUrl,
         record.ResumePositionSeconds));
        }
    }

    /// <summary>
    /// Switches the upcoming queue without touching the item currently
    /// playing. Normal Next and auto-advance behaviour immediately begin
    /// using these replacement entries.
    /// </summary>
    public void ReplaceUpcomingEntries(
        IEnumerable<VideoQueueRecord> records)
    {
        entries.Clear();

        LoadEntries(records);

        //
        // If the user switched back to the queue that owns the currently
        // playing video, replace the deserialized copy with the real Current
        // instance. This prevents the playing item appearing twice or being
        // treated as an upcoming video.
        //
        if (Current is { } current &&
            ReferenceEquals(
                GetActiveProfile(),
                currentOwnerProfile))
        {
            var restoredCopyIndex =
                FindMatchingRecordIndex(
                    entries,
                    current);

            if (restoredCopyIndex >= 0)
            {
                entries.RemoveAt(
                    restoredCopyIndex);
            }

            entries.Insert(
                0,
                current);
        }

        //
        // Current is deliberately preserved when another queue is activated.
        //
        Persist();
    }

    /// <summary>
    /// Retained for call sites that intentionally need to discard current
    /// queue ownership as well as replacing upcoming entries.
    /// </summary>
    public void ReplaceEntries(
        IEnumerable<VideoQueueRecord> records)
    {
        entries.Clear();

        LoadEntries(records);

        Current = null;

        Persist();
    }

    public IReadOnlyList<VideoQueueEntry> Entries => entries;
    public VideoQueueEntry? Current { get; private set; }

    // Builds a display-only entry for a URL, enriched the same way a real queue entry would be,
    // without touching entries or Current - used by WatchAlongSession to show what a viewer is
    // watching. Current specifically must stay untouched here: OnFrameworkUpdate's Viewing branch
    // treats any non-null Current as "the user queued something locally, leave the stream", so
    // populating it from the sync path itself would immediately self-trigger a Leave().
    public VideoQueueEntry CreateDisplayEntry(string url)
    {
        var entry = new VideoQueueEntry(url, url, string.Empty, null, null);
        EnrichIfYouTube(entry);
        return entry;
    }

    public void Add(VideoQueueEntry entry)
    {
        entries.Add(entry);
        EnrichIfYouTube(entry);
        Persist();
    }

    public void PlayNow(
     VideoQueueEntry entry)
    {
        if (VideoPlaybackIsBlockedByExclusiveMedia())
        {
            return;
        }

        //
        // Explicitly playing another item finishes ownership of the old
        // queue item, even if its queue is no longer active.
        //
        if (Current is { } current &&
            !ReferenceEquals(
                current,
                entry))
        {
            RemoveFromOwningQueue(
                current);

            Current =
                null;

            currentOwnerProfile =
                null;
        }

        entries.Remove(
            entry);

        entries.Insert(
            0,
            entry);

        EnrichIfYouTube(
            entry);

        StartFirstEntry();
    }

    public void PlayTransient(
    VideoQueueEntry entry)
    {
        if (VideoPlaybackIsBlockedByExclusiveMedia())
        {
            return;
        }

        //
        // A transient live stream may be Current so the playback UI and
        // Watch Party synchronization can see it, but it must never become
        // part of the saved or resumable queue.
        //
        if (Current is { } current &&
            !ReferenceEquals(
                current,
                entry))
        {
            //
            // Match PlayNow's replacement behaviour for an existing normal
            // queue item.
            //
            RemoveFromOwningQueue(
                current);

            Current =
                null;

            currentOwnerProfile =
                null;
        }

        //
        // Defensively remove the supplied object if it was previously added
        // through another route.
        //
        entries.Remove(
            entry);

        Current =
            entry;

        currentOwnerProfile =
            null;

        pendingResumePositionSeconds =
            null;

        pendingStartUnpause =
            true;

        autoAdvanceArmed =
            false;

        EnrichIfYouTube(
            entry);

        video.Play(
            entry.Url);

        video.SetOverlayTitle(
            entry.Title,
            entry.Source);

        //
        // Persist only the normal upcoming entries. The transient Current
        // entry is deliberately not in entries.
        //
        Persist();
    }

    public void PlayNext(
        VideoQueueEntry entry)
    {
        entries.Remove(
            entry);

        var insertAt =
            Current is not null &&
            entries.Contains(
                Current)
                ? Math.Min(
                    1,
                    entries.Count)
                : 0;

        entries.Insert(
            insertAt,
            entry);

        EnrichIfYouTube(
            entry);

        Persist();
    }

    public void Remove(
        VideoQueueEntry entry)
    {
        var removingCurrent =
            ReferenceEquals(
                Current,
                entry);

        entries.Remove(
            entry);

        if (removingCurrent)
        {
            Current =
                null;

            pendingResumePositionSeconds =
                null;

            pendingStartUnpause =
    false;

            video.Stop();
        }

        Persist();
    }

    public void Clear()
    {
        entries.Clear();

        Current =
            null;

        pendingResumePositionSeconds =
            null;

        pendingStartUnpause =
    false;

        autoAdvanceArmed =
            false;

        video.Stop();

        Persist();
    }

    public void StopPlayback()
    {
        //
        // Capture the timestamp before stopping MPV. SaveCurrentProgress
        // writes it back to the queue that originally supplied Current,
        // even when another queue is now active.
        //
        SaveCurrentProgress();

        Current =
            null;

        currentOwnerProfile =
            null;

        pendingResumePositionSeconds =
            null;

        pendingStartUnpause =
            false;

        autoAdvanceArmed =
            false;

        video.Stop();

        Persist();
    }

    public void SaveCurrentProgress()
    {
        if (Current is not { } current ||
            current.IsTransient)
        {
            return;
        }

        var (position, duration, _) =
            video.GetProgress();

        if (position >= 0f)
        {
            current.ResumePositionSeconds =
                position;
        }

        if (duration > 0f)
        {
            current.Duration =
                TimeSpan.FromSeconds(
                    duration);
        }

        //
        // When Current still belongs to the active in-memory list, normal
        // persistence updates it. Otherwise write it back to its original
        // saved queue profile.
        //
        if (entries.Contains(
                current))
        {
            Persist();
            return;
        }

        UpdateOwningQueueRecord(
            current);

        Plugin.Cfg.Save();
    }

    public void Reorder(
        int fromIndex,
        int toIndex)
    {
        if (fromIndex < 0 ||
            fromIndex >= entries.Count ||
            toIndex < 0 ||
            toIndex >= entries.Count ||
            fromIndex == toIndex)
        {
            return;
        }

        //
        // The playing entry remains fixed at the front until it finishes
        // or the player explicitly skips it.
        //

        if (ReferenceEquals(
                entries[fromIndex],
                Current) ||
            ReferenceEquals(
                entries[toIndex],
                Current))
        {
            return;
        }

        var item =
            entries[fromIndex];

        entries.RemoveAt(
            fromIndex);

        entries.Insert(
            toIndex,
            item);

        Persist();
    }

    public List<VideoQueueRecord> ExportEntries()
    {
        var records =
            new List<VideoQueueRecord>(
                entries.Count);

                foreach (var entry in entries)
                {
                    if (entry.IsTransient)
                    {
                        continue;
                    }

                    records.Add(
                        new VideoQueueRecord
                {
                    Url =
                        entry.Url,

                    Title =
                        entry.Title,

                    Source =
                        entry.Source,

                    DurationSeconds =
                        entry.Duration?.TotalSeconds,

                    ThumbnailUrl =
                        entry.ThumbnailUrl,

                    ResumePositionSeconds =
                        entry.ResumePositionSeconds,
                });
        }

        return records;
    }

    private void Persist()
    {
        var records =
            ExportEntries();

        //
        // Continue updating the legacy single-queue field during the
        // saved-profile migration period.
        //

        Plugin.Cfg.VideoQueue =
            new List<VideoQueueRecord>(
                records);

        var activeSlot =
            Plugin.Cfg.ActiveQueueSlot;

        if (activeSlot >= 0 &&
            activeSlot <
            Plugin.Cfg.SavedQueueProfiles.Count)
        {
            var activeProfile =
                Plugin.Cfg
                    .SavedQueueProfiles[activeSlot];

            //
            // Videos can be added from Home, Browse Videos, links, and
            // other pages without visiting the Queue tab first.
            //

            if (activeProfile is null &&
                records.Count > 0)
            {
                activeProfile =
                    new SavedQueueProfile
                    {
                        Name =
                            "My Queue",

                        Icon =
                            "Tv",

                        Entries =
                            new List<VideoQueueRecord>()
                    };

                Plugin.Cfg
                    .SavedQueueProfiles[activeSlot] =
                    activeProfile;
            }

            if (activeProfile is not null)
            {
                activeProfile.Entries =
                    new List<VideoQueueRecord>(
                        records);
            }
        }

        Plugin.Cfg.Save();
    }

    public void Advance(
     bool showSnesWarning = true)
    {
        if (VideoPlaybackIsBlockedByExclusiveMedia(showSnesWarning))
        {
            return;
        }

        //
        // If something is playing, Advance is the Next command. Remove the
        // item from the queue that supplied it, which may no longer be active.
        //
        // With no Current item, Advance is Resume Queue and retains the first
        // entry so its saved timestamp can be restored.
        //
        if (Current is { } current)
        {
            RemoveFromOwningQueue(
                current);

            Current =
                null;

            currentOwnerProfile =
                null;

            pendingResumePositionSeconds =
                null;

            pendingStartUnpause =
                false;
        }

        StartFirstEntry();
    }

    private bool VideoPlaybackIsBlockedByExclusiveMedia(
        bool showMessage = true)
    {
        if (video.IsPlayingGame || video.IsPlayingBrowser)
        {
            if (showMessage)
            {
                Plugin.ChatGui.Print(
                    "[AlphaChannel] Stop the current game or browser before using video playback.");
            }

            return true;
        }

        if (video.IsPlayingLocalVideo)
        {
            if (showMessage)
            {
                Plugin.ChatGui.Print(
                    "[AlphaChannel] Stop the local video before using other media playback.");
            }

            return true;
        }

        return false;
    }

    private void StartFirstEntry()
    {
        if (entries.Count == 0)
        {
            Current =
    null;

            currentOwnerProfile =
                null;

            pendingResumePositionSeconds =
                null;

            pendingStartUnpause =
                false;

            video.SetOverlayTitle(
                string.Empty,
                string.Empty);

            Persist();

            return;
        }

        //
        // Keep the entry at index zero while it is playing.
        //

        Current =
Current =
    entries[0];

// Capture ownership before another queue can become active.
currentOwnerProfile =
    GetActiveProfile();

autoAdvanceArmed =
    false;

        EnrichIfYouTube(
            Current);

        //
        // Remember the resume position before starting MPV. Play() returns
        // before the file has necessarily finished loading, so seeking and
        // unpausing are completed later by OnFrameworkUpdate.
        //

        pendingResumePositionSeconds =
            Current.ResumePositionSeconds > 0.5d
                ? Current.ResumePositionSeconds
                : null;

        pendingStartUnpause =
            true;

        video.Play(
            Current.Url);

        video.SetOverlayTitle(
            Current.Title,
            Current.Source);

        Persist();
    }

    private void CompleteCurrentEntry()
    {
        if (Current is not { } completed)
        {
            return;
        }

        //
        // Remove the completed video from the queue that started it rather
        // than whichever queue happens to be active now.
        //
        RemoveFromOwningQueue(
            completed);

        Current =
            null;

        currentOwnerProfile =
            null;

        pendingResumePositionSeconds =
            null;

        pendingStartUnpause =
            false;

        autoAdvanceArmed =
            false;

        Persist();

        if (Plugin.Cfg.AutoPlayNextQueueVideo)
        {
            StartFirstEntry();
        }
    }

    private SavedQueueProfile? GetActiveProfile()
    {
        var activeSlot =
            Plugin.Cfg.ActiveQueueSlot;

        return activeSlot >= 0 &&
               activeSlot <
               Plugin.Cfg.SavedQueueProfiles.Count
            ? Plugin.Cfg.SavedQueueProfiles[
                activeSlot]
            : null;
    }

    private static int FindMatchingRecordIndex(
        IReadOnlyList<VideoQueueEntry> candidates,
        VideoQueueEntry target)
    {
        for (var index = 0;
             index < candidates.Count;
             index++)
        {
            if (string.Equals(
                    candidates[index].Url,
                    target.Url,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindMatchingRecordIndex(
        IReadOnlyList<VideoQueueRecord> candidates,
        VideoQueueEntry target)
    {
        for (var index = 0;
             index < candidates.Count;
             index++)
        {
            if (string.Equals(
                    candidates[index].Url,
                    target.Url,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static VideoQueueRecord ToRecord(
        VideoQueueEntry entry) =>
        new()
        {
            Url =
                entry.Url,

            Title =
                entry.Title,

            Source =
                entry.Source,

            DurationSeconds =
                entry.Duration?.TotalSeconds,

            ThumbnailUrl =
                entry.ThumbnailUrl,

            ResumePositionSeconds =
                entry.ResumePositionSeconds,
        };

    private void UpdateOwningQueueRecord(
        VideoQueueEntry current)
    {
        if (currentOwnerProfile is null)
        {
            return;
        }

        var recordIndex =
            FindMatchingRecordIndex(
                currentOwnerProfile.Entries,
                current);

        if (recordIndex < 0)
        {
            return;
        }

        currentOwnerProfile.Entries[
            recordIndex] =
            ToRecord(
                current);
    }

    private void RemoveFromOwningQueue(
        VideoQueueEntry current)
    {
        //
        // If its original queue is active, remove the actual in-memory entry.
        //
        if (ReferenceEquals(
                GetActiveProfile(),
                currentOwnerProfile))
        {
            entries.Remove(
                current);

            return;
        }

        //
        // Otherwise remove the serialized entry from the inactive profile.
        //
        if (currentOwnerProfile is null)
        {
            return;
        }

        var recordIndex =
            FindMatchingRecordIndex(
                currentOwnerProfile.Entries,
                current);

        if (recordIndex >= 0)
        {
            currentOwnerProfile.Entries.RemoveAt(
                recordIndex);

            Plugin.Cfg.Save();
        }
    }

    private void EnrichIfYouTube(VideoQueueEntry entry)
    {
        if (!VideoUrlResolver.IsYouTubeUrl(entry.Url))
        {
            return;
        }

        _ = EnrichAsync(entry);
    }

    private async Task EnrichAsync(VideoQueueEntry entry)
    {
        VideoMetadata? metadata;
        try
        {
            metadata = await metadataResolver.ResolveMetadataAsync(entry.Url, lifetime.Token)
            .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }

        if (metadata is null)
        {
            return;
        }

        entry.Title = metadata.Title;
        entry.Source = metadata.Source;
        entry.Duration = metadata.Duration;
        entry.ThumbnailUrl = metadata.ThumbnailUrl;
        // Enrichment runs for every queued/display entry, not just the one actually playing (Add,
        // PlayNext, CreateDisplayEntry all trigger it too) - only push to the in-world banner when
        // this is the entry the screen is actually showing right now.
        if (ReferenceEquals(entry, Current))
        {
            video.SetOverlayTitle(entry.Title, entry.Source);
        }

        Persist(); // Keep the saved copy in sync so a reload doesn't fall back to a bare URL title.
    }

    public void Dispose()
    {
        video.ExternalPlaybackTakingOver -= OnExternalPlaybackTakingOver;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    public void OnFrameworkUpdate()
    {
        tickCounter++;

        if (tickCounter <
            PollEveryTicks)
        {
            return;
        }

        tickCounter =
            0;

        var idle =
            video.IsIdle();

        var (position, duration, paused) =
            video.GetProgress();

        //
        // Play() returns before MPV has necessarily loaded the media.
        // A positive duration tells us a normal queued video is ready to
        // accept its restored seek and unpause commands.
        //
        // Do not clear either pending value until that point.
        //

        var mediaReady =
            Current is not null &&
            !idle &&
            duration > 0f &&
            video.State is
                VideoPlaybackState.Playing or
                VideoPlaybackState.Paused;

        if (mediaReady &&
       pendingResumePositionSeconds is { } resumePosition)
        {
            var maximumResumePosition =
                Math.Max(
                    0f,
                    duration - 1f);

            var targetPosition =
                Math.Clamp(
                    (float)resumePosition,
                    0f,
                    maximumResumePosition);

            //
            // MPV can accept a seek command before the newly loaded file is
            // actually ready and silently leave playback at zero. Keep issuing
            // the seek until the reported position confirms it was applied.
            //
            if (MathF.Abs(
                    position -
                    targetPosition) >
                1.5f)
            {
                video.Seek(
                    targetPosition);
            }
            else
            {
                pendingResumePositionSeconds =
                    null;
            }
        }

        if (mediaReady &&
            pendingResumePositionSeconds is null &&
            pendingStartUnpause)
        {
            //
            // Likewise, do not consider the automatic unpause complete until
            // MPV reports that playback is genuinely unpaused.
            //
            if (paused)
            {
                video.Pause(
                    false);
            }
            else
            {
                pendingStartUnpause =
                    false;
            }
        }

        if (video.State ==
                VideoPlaybackState.Playing &&
            !idle &&
            !pendingStartUnpause)
        {
            autoAdvanceArmed =
                true;
        }

        //
        // Save approximately every five seconds. Do not save the initial
        // zero position while a restored seek is still waiting for MPV,
        // because that would overwrite the timestamp being restored.
        //

        if (Current is not null &&
            pendingResumePositionSeconds is null &&
            !pendingStartUnpause &&
            video.State is
                VideoPlaybackState.Playing or
                VideoPlaybackState.Paused)
        {
            progressSaveCounter++;

            if (progressSaveCounter >=
                10)
            {
                progressSaveCounter =
                    0;

                SaveCurrentProgress();
            }
        }
        else
        {
            progressSaveCounter =
                0;
        }

        if (video.State ==
            VideoPlaybackState.Failed)
        {
            autoAdvanceArmed =
                false;

            pendingStartUnpause =
                false;
        }
        else if (idle &&
                 !wasIdle &&
                 autoAdvanceArmed &&
                 Current is not null)
        {
            CompleteCurrentEntry();
        }

        wasIdle =
            idle;
    }
}
