namespace AlphaChannel.Plugin.Video;

internal enum VideoPlaybackState : byte
{
    Idle,
    Loading,
    Playing,
    Paused,
    Failed,
}

// Adapter over VideoEngine (the ported AlphaChannel engine, Voudi, GPL-3.0).
// Exposes playback controls and state to AlphaChannel's UI and queue.
// Shares the engine owned by ScreenController, keeping playback controls
// and the in-world screen driven by the same playback engine.
internal sealed class VideoPlayer : IDisposable
{
    private readonly VideoEngine engine;

    private string? currentMediaUrl;
    private DateTime? idleScreensaverEligibleSinceUtc;
    private string? idleScreensaverStatus;
    private int idleScreensaverPlaybackAttemptId = -1;
    private int waitingScreenVersion;
    private int idleScreensaverWaitingScreenVersion = -1;

    private AudioVisualizerMode currentVisualizerMode =
        AudioVisualizerMode.ClassicBars;

    private AudioVisualizerTheme currentVisualizerTheme =
    AudioVisualizerTheme.AlphaPurple;

    public VideoPlayer(VideoEngine engine)
    {
        this.engine = engine;
        engine.ExternalPlaybackTakingOver += OnExternalPlaybackTakingOver;
    }

    internal event Action? ExternalPlaybackTakingOver;

    public VideoPlaybackState State { get; private set; } = VideoPlaybackState.Idle;
    public string? LastError { get; private set; }
    public int PlaybackAttemptId { get; private set; }

    public bool IsPlayingSnes =>
        engine.IsPlayingSnes;

    public bool IsPlayingGame =>
        engine.IsPlayingGame;

    public bool IsPlayingBrowser =>
        engine.IsPlayingBrowser;

    public bool IsPlayingLocalVideo =>
        engine.IsPlayingLocalVideo;

    public bool IsPlayingImage =>
    engine.IsPlayingImage;

    internal bool HasVideoPlaybackSession =>
        engine.HasVideoPlaybackSession;

    internal BroadcastDiagnosticsSnapshot BroadcastDiagnostics =>
        engine.BroadcastDiagnostics;

    public bool IsAudioOnly =>
    engine.IsAudioOnly;

    public bool HardwareDecoding
    {
        get => engine.HardwareDecoding;
        set => engine.HardwareDecoding = value;
    }

    public bool AllowInsecureDirectUrls
    {
        get => engine.AllowInsecureDirectUrls;
        set => engine.AllowInsecureDirectUrls = value;
    }

    public int MaxQualityHeight
    {
        get => engine.MaxQualityHeight;
        set => engine.MaxQualityHeight = value;
    }

    public string? CookiesPath
    {
        get => engine.CookiesPath;
        set => engine.CookiesPath = value;
    }

    public void ShowWaitingScreen()
    {
        waitingScreenVersion++;
        engine.ShowWaitingScreen();
    }

    public void UpdateIdleScreensaver()
    {
        string? status = null;
        if (engine.IsActive)
        {
            status = engine.IsShowingWaitingScreen
                ? "Waiting for content"
                : State == VideoPlaybackState.Paused
                    ? "Playback paused"
                    : State is VideoPlaybackState.Idle or VideoPlaybackState.Failed
                        ? "Nothing playing"
                        : null;
        }

        var activityChanged = idleScreensaverPlaybackAttemptId != PlaybackAttemptId ||
                              idleScreensaverWaitingScreenVersion != waitingScreenVersion ||
                              !string.Equals(idleScreensaverStatus, status, StringComparison.Ordinal);
        if (activityChanged)
        {
            idleScreensaverPlaybackAttemptId = PlaybackAttemptId;
            idleScreensaverWaitingScreenVersion = waitingScreenVersion;
            idleScreensaverStatus = status;
            idleScreensaverEligibleSinceUtc = status is null ? null : DateTime.UtcNow;
            engine.SetIdleScreensaver(null);
        }

        if (status is null)
        {
            idleScreensaverEligibleSinceUtc = null;
            engine.SetIdleScreensaver(null);
            return;
        }

        idleScreensaverEligibleSinceUtc ??= DateTime.UtcNow;
        if (DateTime.UtcNow - idleScreensaverEligibleSinceUtc.Value >= TimeSpan.FromMinutes(10))
            engine.SetIdleScreensaver(status);
    }

    public void SetVolume(int volumePercent) => engine.SetVolume(volumePercent);

    public void SetOutputMuted(bool muted) => engine.SetOutputMuted(muted);

    public void SetOverlayTitle(string title, string source) => engine.SetOverlayTitle(title, source);

    public void SetReactions(IReadOnlyList<ReactionParticle> reactions) => engine.SetReactions(reactions);

    // True once mpv has nothing left to play (natural end, with keep-open=yes so it doesn't
    // reset position) or before anything has ever been loaded. Callers polling this for
    // auto-advance should throttle - see AetherStreamQueue, which does not poll every frame.
    public bool IsIdle()
    {
        CheckForPlaybackFailure();

        if (State == VideoPlaybackState.Failed)
        {
            return true;
        }

        return engine.GetIdle();
    }

    private void CheckForPlaybackFailure()
    {
        if (State == VideoPlaybackState.Idle ||
            State == VideoPlaybackState.Failed)
        {
            return;
        }


        //
        // VideoEngine may currently be handling the first MPV failure
        // by running WebMediaUrlResolver and preparing a second attempt.
        //
        // During that period the original MPV failure is NOT the final
        // playback result.
        //
        // Do not call StopVideo() here, because that would kill the
        // resolver while it is still working.
        //

        if (engine.WebResolverFallbackRunning)
        {
            State =
                VideoPlaybackState.Loading;

            return;
        }


        //
        // No final playback error yet.
        //

        if (engine.LastError is not { } error)
        {
            return;
        }


        //
        // Both chances are now finished and playback genuinely failed.
        //

        State =
            VideoPlaybackState.Failed;


        LastError =
            error;


        AepLog.Warning(
            $"[Video] Playback failed; resetting player: {error}");


        try
        {
            engine.StopVideo();
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Video] Failed to reset player after playback error: {exception.Message}");
        }
    }

    // AlphaChannel's engine resolves both YouTube and generic page URLs itself, via mpv's bundled
    // ytdl_hook + yt-dlp (see MpvRenderer's "ytdl"/"ytdl-format" options) - unlike the old
    // VideoPlayer this replaces, there's no separate YoutubeExplode pre-resolution step needed
    // here; that resolver (VideoUrlResolver) is kept only for AetherStreamQueue's metadata
    // enrichment (title/duration/thumbnail), not for the playback URL itself.

    private static bool IsAlphaChannelLiveHls(
    string url)
    {
        return Uri.TryCreate(
                   url,
                   UriKind.Absolute,
                   out var uri) &&
               uri.Port == 8888 &&
               uri.AbsolutePath.StartsWith(
                   "/live/",
                   StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.EndsWith(
                   "/index.m3u8",
                   StringComparison.OrdinalIgnoreCase);
    }

    public void Play(
    string url)
    {
        if (engine.IsPlayingGame || engine.IsPlayingBrowser)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the current game or browser before using video playback.");

            return;
        }

        if (engine.IsPlayingLocalVideo)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the local video before using other media playback.");

            return;
        }

        //
        // Image descriptors must be handled before AudioVisualizerSelection,
        // because both features store synchronization metadata in URL
        // fragments. Image URLs never reach mpv or yt-dlp.
        //
        if (ImageMediaSelection.TryParse(
                url,
                out var imageSelection) &&
            imageSelection is not null)
        {
            var sameImageMedia =
                !string.IsNullOrWhiteSpace(
                    currentMediaUrl) &&
                string.Equals(
                    currentMediaUrl,
                    url,
                    StringComparison.Ordinal) &&
                engine.IsPlayingImage &&
                State is
                    VideoPlaybackState.Loading or
                    VideoPlaybackState.Playing or
                    VideoPlaybackState.Paused;

            if (sameImageMedia)
            {
                return;
            }

            try
            {
                PlaybackAttemptId++;

                currentMediaUrl =
                    url;

                currentVisualizerMode =
                    AudioVisualizerMode.ClassicBars;

                currentVisualizerTheme =
                    AudioVisualizerTheme.AlphaPurple;

                LastError =
                    null;

                State =
                    VideoPlaybackState.Loading;

                engine.PlayImage(
                    imageSelection);

                if (engine.LastError is { } immediateError)
                {
                    State =
                        VideoPlaybackState.Failed;

                    LastError =
                        immediateError;

                    return;
                }

                State =
                    VideoPlaybackState.Playing;
            }
            catch (Exception exception)
            {
                currentMediaUrl =
                    null;

                State =
                    VideoPlaybackState.Failed;

                LastError =
                    exception.Message;

                AepLog.Warning(
                    $"[Image] Failed to start playback: {exception.Message}");
            }

            return;
        }

        //
        // A malformed AlphaChannel image descriptor must not fall through to
        // mpv as an ordinary web/video URL.
        //
        if (url.Contains(
                "#acmedia=",
                StringComparison.OrdinalIgnoreCase))
        {
            currentMediaUrl =
                null;

            State =
                VideoPlaybackState.Failed;

            LastError =
                "The shared image or slideshow information is invalid.";

            return;
        }

        var expectedAudioOnly =
            AudioVisualizerSelection
                .HasAudioVisualizerMetadata(
                    url);

        var selection =
            AudioVisualizerSelection.Parse(
                url);

        var sameMedia =
            !string.IsNullOrWhiteSpace(
                currentMediaUrl) &&
            string.Equals(
                currentMediaUrl,
                selection.MediaUrl,
                StringComparison.Ordinal) &&
            // A game/browser can stop MPV directly while this facade still
            // remembers the last URL. Only suppress a duplicate Play request
            // when the engine still owns a live or pending MPV session.
            engine.HasVideoPlaybackSession &&
            State is
                VideoPlaybackState.Loading or
                VideoPlaybackState.Playing or
                VideoPlaybackState.Paused;

        //
        // Always record/apply the visualizer selection first. During Loading,
        // VideoEngine remembers it and applies it once audio-only playback is
        // confirmed.
        //
        var visualizerChanged =
            currentVisualizerMode != selection.Mode ||
            currentVisualizerTheme != selection.Theme;

        currentVisualizerMode =
            selection.Mode;
        currentVisualizerTheme =
    selection.Theme;

        engine.SetAudioVisualizerMode(
            selection.Mode,
            selection.Theme);

        //
        // A fragment-only change is presentation state, not new media.
        // Do not call PlayVideo because that would reload the Icecast stream.
        //
        if (sameMedia)
        {
            if (visualizerChanged)
            {
                AepLog.Info(
                    $"[AudioVisualizer] Applied URL selection: {AudioVisualizerSelection.GetDisplayName(selection.Mode)}.");
            }

            return;
        }

        try
        {
            PlaybackAttemptId++;

            currentMediaUrl =
                selection.MediaUrl;

            LastError =
                null;

            State =
                VideoPlaybackState.Loading;

            //
            // Only the normalized media URL is passed to MPV. The fragment is
            // AlphaChannel synchronization metadata and never reaches Icecast.
            //
            engine.PlayVideo(
     selection.MediaUrl,
     allowWebResolverFallback:
         !IsAlphaChannelLiveHls(
             selection.MediaUrl),
     expectedAudioOnly:
         expectedAudioOnly);

            State =
                VideoPlaybackState.Playing;
        }
        catch (Exception exception)
        {
            currentMediaUrl =
                null;

            State =
                VideoPlaybackState.Failed;

            LastError =
                exception.Message;

            AepLog.Warning(
                $"[Video] Failed to start playback: {exception.Message}");
        }
    }

    public bool PlayLocalVideo(
        string path)
    {
        if (engine.IsPlayingGame || engine.IsPlayingBrowser)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the current game or browser before playing a local video.");

            return false;
        }

        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path))
        {
            LastError =
                "The selected local video file could not be found.";

            State =
                VideoPlaybackState.Failed;

            return false;
        }

        try
        {
            PlaybackAttemptId++;

            LastError =
                null;

            State =
                VideoPlaybackState.Loading;

            engine.PlayVideo(
                path,
                allowWebResolverFallback: false,
                isLocalVideo: true);

            if (!engine.IsPlayingLocalVideo)
            {
                State =
                    VideoPlaybackState.Failed;

                LastError ??=
                    engine.LastError ??
                    "Local video playback could not be started.";

                return false;
            }

            State =
                VideoPlaybackState.Playing;

            return true;
        }
        catch (Exception exception)
        {
            State =
                VideoPlaybackState.Failed;

            LastError =
                exception.Message;

            AepLog.Warning(
                $"[LocalVideo] Failed to start playback: {exception.Message}");

            return false;
        }
    }

    public void Pause(
      bool pause)
    {
        //
        // A remote Watch Party state can call Pause every framework update.
        // Check the renderer first and preserve Failed/Idle so the same MPV
        // failure is not reset to Playing and reported again every frame.
        //

        CheckForPlaybackFailure();

        if (State == VideoPlaybackState.Failed ||
            State == VideoPlaybackState.Idle)
        {
            return;
        }

        engine.Pause(
            pause);

        State =
            pause
                ? VideoPlaybackState.Paused
                : VideoPlaybackState.Playing;
    }

    public void Seek(float seconds) => engine.Seek((int)MathF.Round(seconds));

    public (float Position, float Duration, bool Paused) GetProgress()
    {
        CheckForPlaybackFailure();

        var info = engine.GetInfo();
        return ((float)info[0], (float)info[1], engine.GetPaused());
    }

    // Separate from GetProgress() rather than folded into its tuple - existing call sites
    // deconstruct that tuple positionally and would silently break if it grew.
    public (int Width, int Height) GetResolution()
    {
        var info = engine.GetInfo();
        return ((int)info[3], (int)info[4]);
    }

    public byte[]? TryGetFrame(out int width, out int height) => engine.TryGetFrame(out width, out height);

    public void Stop()
    {
        engine.StopVideo();

        currentMediaUrl =
            null;

        currentVisualizerMode =
            AudioVisualizerMode.ClassicBars;

        currentVisualizerTheme =
    AudioVisualizerTheme.AlphaPurple;

        State =
            VideoPlaybackState.Idle;
    }

    private void OnExternalPlaybackTakingOver()
    {
        // Queue listeners save the current resume point before this facade
        // forgets the MPV session.
        ExternalPlaybackTakingOver?.Invoke();

        currentMediaUrl = null;
        currentVisualizerMode = AudioVisualizerMode.ClassicBars;
        currentVisualizerTheme = AudioVisualizerTheme.AlphaPurple;
        LastError = null;
        State = VideoPlaybackState.Idle;
        PlaybackAttemptId++;
        idleScreensaverEligibleSinceUtc = null;
        engine.SetIdleScreensaver(null);
    }

    public void Dispose()
    {
        engine.ExternalPlaybackTakingOver -= OnExternalPlaybackTakingOver;
        Stop();
    }
}
