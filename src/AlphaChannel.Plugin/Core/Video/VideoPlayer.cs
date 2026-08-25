namespace AlphaChannel.Plugin.Video;

internal enum VideoPlaybackState : byte
{
    Idle,
    Loading,
    Playing,
    Paused,
    Failed,
}

// Adapter over VideoEngine (the ported AlphaChannel engine, Voudi, GPL-3.0), keeping the public
// contract the rest of AetherStream (the queue, WatchAlongSession, the debug/screen windows)
// already depends on. The old hand-rolled libmpv p/invoke wrapper this replaced is gone; playback
// itself now lives on VideoEngine, shared with ScreenController so both the phone's UI and the
// in-world screen VFX are driven by the same single mpv instance.
internal sealed class VideoPlayer : IDisposable
{
    private readonly VideoEngine engine;

    public VideoPlayer(VideoEngine engine)
    {
        this.engine = engine;
    }

    public VideoPlaybackState State { get; private set; } = VideoPlaybackState.Idle;
    public string? LastError { get; private set; }
    public int PlaybackAttemptId { get; private set; }

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

    public bool UseFirefoxCookies
    {
        get => engine.UseFirefoxCookies;
        set => engine.UseFirefoxCookies = value;
    }

    public void ShowWaitingScreen()
    {
        engine.ShowWaitingScreen();
    }

    public void SetVolume(int volumePercent) => engine.SetVolume(volumePercent);

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

        if (engine.LastError is not { } error)
        {
            return;
        }

        State = VideoPlaybackState.Failed;
        LastError = error;

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
    public void Play(string url)
    {
        try
        {
            PlaybackAttemptId++;

            LastError = null;
            State = VideoPlaybackState.Loading;
            engine.PlayVideo(url);
            State = VideoPlaybackState.Playing;
        }
        catch (Exception exception)
        {
            State = VideoPlaybackState.Failed;
            LastError = exception.Message;
            AepLog.Warning($"[Video] Failed to start playback: {exception.Message}");
        }
    }

    public void Pause(bool pause)
    {
        engine.Pause(pause);
        State = pause ? VideoPlaybackState.Paused : VideoPlaybackState.Playing;
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
        State = VideoPlaybackState.Idle;
    }

    public void Dispose()
    {
        Stop();
    }
}
