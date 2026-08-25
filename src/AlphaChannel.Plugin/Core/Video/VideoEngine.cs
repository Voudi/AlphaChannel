using System.Text.RegularExpressions;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace AlphaChannel.Plugin.Video;

// Ported from AlphaChannel's Core (Voudi, GPL-3.0), with the companion/minion tracking removed -
// see port/alphachannel-engine Stage 4. Screen mounting itself is ported again from AlphaChannel's
// later revamp (tag v1.1.20260725.1088, "Revamp screen to not use VFX" / "Removed need for
// carbuncle, added screen positions in settings"): the VFX/Penumbra/actor-attach approach
// (chara/monster/m7002/.../aetherstreamscreen_{session}.avfx cast on the local player) is gone
// entirely, replaced by ScreenPainter drawing a textured quad directly at an absolute world
// position/yaw/scale - independent of any game object, so it no longer rides along on the player's
// own body.
//
// AlphaChannel port note: the SNES9x emulator path (Snes9xRenderer, InputManager,
// WndProcKeyUpReader) was cut here rather than ported - Aetherphone's own review found it had no
// UI entry point wired to it at all, and this project's UI doesn't add one either. mpv-based video
// playback is unaffected.
internal sealed class VideoEngine : IDisposable
{
    internal const int ScreenWidth = 1920;
    internal const int ScreenHeight = 1080;

    //Default placement for a freshly (re)spawned screen: 2 units in front of the local player, facing
    //back towards them. Matches AlphaChannel's SpawnScreenInFrontOfLocalPlayer 1:1.
    private const float DefaultScreenSpawnDistance = 2.0f;
    private const float DefaultScreenHeightOffset = 1.0f;

    //Aetherphone addition on top of the upstream port - upstream's Scale field had no bounds at all.
    internal const float MinScreenScale = 0.1f;
    internal const float MaxScreenScale = 8.0f;

    //Aetherphone addition - how far the Casting tab's X/Y/Z sliders reach out from ScreenSpawnAnchor in
    //either direction. Position itself is unbounded (it's a world coordinate), but a slider needs a
    //visible min/max to be a slider at all, so it's capped relative to wherever the screen was last
    //placed on purpose (spawn/recenter/preset apply) rather than relative to some arbitrary world origin.
    internal const float ScreenPositionSliderRange = 10f;

    private readonly ScreenPainter _screenPainter;
    private readonly List<ScreenPositionPreset> _screenPresets = [];

    internal Vector3 ScreenPosition { get; private set; }
    internal float ScreenYaw { get; private set; }
    internal float ScreenScale { get; private set; } = 1.0f;

    //Center the Casting tab's position sliders around - updated only on a deliberate re-placement
    //(spawn/recenter/preset apply), never while just dragging the sliders themselves, so the window
    //doesn't shrink out from under the slider mid-drag.
    internal Vector3 ScreenSpawnAnchor { get; private set; }

    private MpvRenderer? _mpvRenderer;
    private readonly Texture2D _screenTexture;
    private readonly ShaderResourceView _previewShaderResourceView;
    private static readonly Texture2DDescription ScreenTextureDescription = new()
    {
        Width = ScreenWidth,
        Height = ScreenHeight,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        CpuAccessFlags = CpuAccessFlags.None,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        OptionFlags = ResourceOptionFlags.None,
    };
    private CancellationTokenSource _renderCancellation = new();

    private DateTime _lastLoadYT = DateTime.MinValue;
    private static readonly Regex YtRegex = new(@"^\w+://[^/]*youtube\.\w+/|^\w+://youtu\.be/", RegexOptions.Compiled);
    private static bool IsYTURL(string url) => YtRegex.IsMatch(url);

    private bool _isActive; // whether the screen should currently be drawing for the local player
    private volatile bool _stopRequested;
    private bool _lastIdle = true;
    private int _pendingVolume = 60;

    private volatile bool _rendererFailed;

    // Read fresh at Play() time by MpvRenderer.Initialize so a settings change takes effect on
    // the next video, not the current one - matching how the old VideoPlayer read these.
    internal bool HardwareDecoding { get; set; }
    internal int MaxQualityHeight { get; set; } = 720;
    internal bool AllowInsecureDirectUrls { get; set; }

    // Path to a Netscape-format cookies.txt the player exported from their own logged-in browser
    // session - lets yt-dlp play age-restricted videos it would otherwise refuse. Read fresh at
    // mpv init time like the other options above, so a settings change applies on the next video.
    internal string? CookiesPath { get; set; }

    // Alternative to CookiesPath - read cookies directly from a local Firefox profile instead of a
    // manually exported file. Takes priority over CookiesPath when both are set.
    internal bool UseFirefoxCookies { get; set; }

    internal Resources Resources { get; }

    internal VideoEngine()
    {
        Resources = new Resources();
        Resources.NativeLoader.Register(Resources);
        MpvRenderer.Setup(Resources);
        DxHandler.Initialise(Plugin.PluginInterface);

        _screenTexture =
            new Texture2D(
                DxHandler.Device,
                ScreenTextureDescription);

        _previewShaderResourceView =
            new ShaderResourceView(
                DxHandler.Device,
                _screenTexture);

        _screenPainter =
            new ScreenPainter();

        _screenPresets.AddRange(Plugin.Cfg.ScreenPresets);
    }

    internal bool IsActive => _isActive;

    internal nint PreviewTextureHandle =>
    _previewShaderResourceView.NativePointer;

    // Set only from PlayVideo's background task on a genuine init/decode failure (e.g. mpv/yt-dlp
    // never downloaded, so mpv_create() throws DllNotFoundException) - VideoPlayer polls this from
    // GetProgress() to flip its own State/LastError, since PlayVideo itself returns long before
    // the failure is known and its caller's try/catch never sees it.
    internal string? LastError { get; private set; }

    internal void StopVideo()
    {
        _stopRequested = true;

        _isActive = false;
        _rendererFailed = false;

        _mpvRenderer?.Stop();

        var renderer = _mpvRenderer;
        _mpvRenderer = null;

        if (renderer is not null)
        {
            try
            {
                Task.Delay(1000).ContinueWith(_ =>
                {
                    try
                    {
                        if (ReferenceEquals(_mpvRenderer, renderer))
                        {
                            _mpvRenderer = null;
                        }

                        renderer.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Already cleaned up by the render loop.
                    }
                    catch (Exception exception)
                    {
                        AepLog.Warning(
                            $"[MPV] Failed delayed renderer dispose after video end: {exception.Message}");
                    }
                });
            }
            catch (Exception exception)
            {
                AepLog.Warning(
                    $"[MPV] Failed to schedule renderer cleanup: {exception.Message}");
            }
        }
        // MpvRenderer.Dispose() cancels the token it was given.
        // Every fresh renderer therefore needs a fresh token source.
        _renderCancellation.Dispose();
        _renderCancellation =
            new CancellationTokenSource();
    }

    private void ResetFailedRenderer()
    {
        var renderer = _mpvRenderer;

        _mpvRenderer = null;
        _rendererFailed = false;
        _isActive = false;

        _screenPainter.SetLoading(false);
        _screenPainter.SetTarget(null);

        if (renderer is not null)
        {
            try
            {
                renderer.Dispose();
            }
            catch (Exception exception)
            {
                AepLog.Warning(
                    $"[MPV] Failed renderer cleanup: {exception.Message}");
            }
        }

        _renderCancellation.Dispose();
        _renderCancellation =
            new CancellationTokenSource();
    }

    internal void ShowWaitingScreen()
    {
        if (_isActive)
        {
            return;
        }

        if (Plugin.ObjectTable.LocalPlayer is null)
        {
            return;
        }

        AssignScreenForSession(_screenTexture);

        _screenPainter.SetLoading(true);

        _isActive = true;

        _screenPainter.SetTransform(
            ScreenPosition,
            ScreenYaw,
            ScreenScale);
    }

    internal void PlayVideo(string url, int playbackPosition = 0, bool isPlaying = true)
    {
        // Never reuse an MPV instance that has already suffered a playback failure.
        if (_rendererFailed)
        {
            AepLog.Warning(
                "[MPV] Resetting failed renderer before loading next video.");

            ResetFailedRenderer();
        }

        if (_mpvRenderer != null &&
            _mpvRenderer.GetCurrentUrl() == url &&
            !_mpvRenderer.IsIdle())
        {
            return;
        }

        LastError = null;
        _stopRequested = false;

        AssignScreenForSession(_screenTexture);

        _screenPainter.SetLoading(true);

        Task.Run(async () =>
        {
            if (IsYTURL(url))
            {
                TimeSpan elapsed = DateTime.Now - _lastLoadYT;
                if (elapsed.TotalSeconds < 7)
                {
                    int sleepTime = Math.Min(Math.Max((int)(7000 - elapsed.TotalMilliseconds), 0), 7000); //Add some sleep time to avoid hitting rate limits
                    Thread.Sleep(sleepTime);
                }

                _lastLoadYT = DateTime.Now;
            }

            try
            {
                if (_mpvRenderer != null)
                {


                    _mpvRenderer.Play(
                        url,
                        playbackPosition,
                        isPlaying);

                    _isActive = true;

                    _screenPainter.SetTransform(
                        ScreenPosition,
                        ScreenYaw,
                        ScreenScale);

                    return;
                }

                _mpvRenderer = new MpvRenderer();

                _mpvRenderer.OnError =
     message =>
     {
         LastError = message;
         _rendererFailed = true;

         _isActive = false;

         _screenPainter.SetLoading(false);
         _screenPainter.SetTarget(null);

         AepLog.Warning(
             $"[MPV] Playback failed; renderer marked for reset: {message}");
     };
                _mpvRenderer.OnFrameRendered =
    () => _screenPainter.SetLoading(false);



                _mpvRenderer.Initialize(
                    ScreenWidth,
                    ScreenHeight,
                    _screenTexture,
                    _renderCancellation,
                    HardwareDecoding,
                    MaxQualityHeight,
                    AllowInsecureDirectUrls,
                    _pendingVolume,
                    CookiesPath,
                    UseFirefoxCookies);

                _mpvRenderer.Play(
                    url,
                    playbackPosition,
                    isPlaying);

                _isActive = true;

                _screenPainter.SetTransform(
                    ScreenPosition,
                    ScreenYaw,
                    ScreenScale);
                while (!_stopRequested && _mpvRenderer.RenderFrame())
                {
                }

                AepLog.Debug("[MPV] Video ended, cleaning up renderer");

                _isActive = false;

                _screenPainter.SetLoading(false);
                _screenPainter.SetTarget(null);

                var renderer = _mpvRenderer;
                _mpvRenderer = null;

                if (renderer is not null)
                {
                    try
                    {
                        renderer.Dispose();
                    }
                    catch (Exception exception)
                    {
                        AepLog.Warning(
                            $"[MPV] Failed to dispose renderer after video end: {exception.Message}");
                    }
                }

                _renderCancellation.Dispose();
                _renderCancellation = new CancellationTokenSource();
            }
            catch (Exception e)
            {
                AepLog.Error($"[MPV] Generic error: {e.Message} {e.StackTrace}");
                LastError = e.Message;

                _isActive = false;
                _screenPainter.SetLoading(false);
                _screenPainter.SetTarget(null);
            }
        });
    }

    internal void Pause(bool pause)
    {
        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            _mpvRenderer?.Pause(pause);
        }
    }

    internal bool GetIdle()
    {
        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            return _mpvRenderer?.IsEofReached() ?? true;
        }

        return true;
    }

    internal bool GetPaused()
    {
        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            return _mpvRenderer?.GetPaused() ?? false;
        }

        return false;
    }

    internal double[] GetInfo()
    {
        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            return _mpvRenderer?.GetProperties() ?? [0, 0, 0, 0, 0];
        }

        return [0, 0, 0, 0, 0];
    }

    internal void Seek(int seconds)
    {
        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            _mpvRenderer?.Seek(seconds);
        }
    }

    internal void SetVolume(int vol)
    {
        _pendingVolume = Math.Clamp(vol, 0, 100);
        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            _mpvRenderer?.SetVolume(vol);
        }
    }

    internal byte[]? TryGetFrame(out int width, out int height)
    {
        if (_mpvRenderer is null)
        {
            width = ScreenWidth;
            height = ScreenHeight;
            return null;
        }

        return _mpvRenderer.TryGetFrame(out width, out height);
    }

    internal string GetMediaTitle()
    {
        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            return _mpvRenderer?.GetMediaTitle() ?? string.Empty;
        }

        return string.Empty;
    }

    internal string? GetCurrentUrl() => _mpvRenderer?.GetCurrentUrl();

    internal bool ValidateURL(string inputUrl, out Uri? url)
    {
        string formattedUrl = inputUrl;

        if (!formattedUrl.StartsWith("http://", StringComparison.Ordinal) && !formattedUrl.StartsWith("https://", StringComparison.Ordinal))
        {
            formattedUrl = "https://" + formattedUrl;
        }

        return Uri.TryCreate(formattedUrl, UriKind.Absolute, out url)
            && (url?.Scheme == Uri.UriSchemeHttp || url?.Scheme == Uri.UriSchemeHttps)
            && url.Host.Contains('.') && !url.Host.EndsWith('.')
            && Uri.CheckHostName(url.Host) == UriHostNameType.Dns;
    }

    internal void OnFrameworkUpdate()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer is not null && _isActive)
        {
            bool idle = GetIdle();
            _lastIdle = idle;
        }
        else
        {
            _lastIdle = true;
        }
    }

    //Places the screen 2 units in front of (and slightly above) the local player, facing the way
    //they're facing. Called when a genuinely new session starts (see AssignScreenForSession), and
    //re-callable any time via RecenterScreen() as a one-tap "lost track of it" reset.
    private void SpawnScreenInFrontOfLocalPlayer()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer is null)
        {
            return;
        }

        float yaw = localPlayer.Rotation;
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));

        var position = localPlayer.Position + forward * DefaultScreenSpawnDistance + new Vector3(0, DefaultScreenHeightOffset, 0);
        ScreenSpawnAnchor = position;
        SetScreenTransform(position, yaw + MathF.PI, 1.0f); //Face back towards the player, not away from them.
    }

    //One-tap reset for when the screen has drifted out of view/reach - re-spawns it in front of the
    //player exactly like a fresh session would, without touching playback.
    internal void RecenterScreen() => SpawnScreenInFrontOfLocalPlayer();

    //Live, unsaved position/yaw/scale edit from the Casting tab - only meaningful while the screen is
    //active. Scale is clamped to [MinScreenScale, MaxScreenScale] here rather than at each call site,
    //so drag/slider widgets in the UI can't push it out of range through fast mouse movement.
    internal void SetScreenTransform(Vector3 position, float yaw, float scale)
    {
        ScreenPosition = position;
        ScreenYaw = yaw;
        ScreenScale = Math.Clamp(scale, MinScreenScale, MaxScreenScale);

        if (_isActive)
        {
            _screenPainter.SetTransform(ScreenPosition, ScreenYaw, ScreenScale);
        }
    }

    internal List<ScreenPositionPreset> GetScreenPresets() => [.. _screenPresets];

    internal void SaveScreenPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _screenPresets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _screenPresets.Add(new ScreenPositionPreset
        {
            Name = name, X = ScreenPosition.X, Y = ScreenPosition.Y, Z = ScreenPosition.Z, Yaw = ScreenYaw,
            Scale = ScreenScale,
        });

        Plugin.Cfg.ScreenPresets = _screenPresets;
        Plugin.Cfg.Save();
    }

    internal void RemoveScreenPreset(string name)
    {
        _screenPresets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Plugin.Cfg.ScreenPresets = _screenPresets;
        Plugin.Cfg.Save();
    }

    internal void ApplyScreenPreset(ScreenPositionPreset preset)
    {
        var position = new Vector3(preset.X, preset.Y, preset.Z);
        ScreenSpawnAnchor = position; //Re-center the position sliders on the spot just jumped to.
        SetScreenTransform(position, preset.Yaw, preset.Scale);
    }

    // Applied when watching someone else's AetherStream over StreamClient and their host client
    // publishes a screen transform (see StreamClient.PublishStateAsync/StreamControl's
    // ScreenX/Y/Z/Yaw/Scale). There is no shared/networked 3D object - this just makes the local
    // ScreenPainter draw at the same coordinates the host is using, same as any other placement.
    internal void ApplyRemoteScreenTransform(Vector3 position, float yaw, float scale)
    {
        ScreenSpawnAnchor = position; //Re-center the position sliders on the host's spot too.
        SetScreenTransform(position, yaw, scale);
    }

    //Called whenever the queue advances or a watch-along viewer's remote state changes, so the
    //in-world screen's own "now playing" banner tracks the same title everyone sees.
    internal void SetOverlayTitle(string title, string source) => _screenPainter.SetTitle(title, source);

    //Called every tick from Plugin.cs with the current active reaction particles - see
    //ScreenPainter.SetReactions for the render side.
    internal void SetReactions(IReadOnlyList<ReactionParticle> reactions) => _screenPainter.SetReactions(reactions);

    //Hands the painter its texture and, if this is a genuinely new session (the screen was idle),
    //spawns it 2 units in front of the local player. Continuing/switching content on an
    //already-active screen must not reset a position the user placed by hand.
    private void AssignScreenForSession(Texture2D screenTexture)
    {
        bool isNewSession = !_isActive;
        _screenPainter.SetTarget(screenTexture);

        if (isNewSession)
        {
            SpawnScreenInFrontOfLocalPlayer();
        }
    }

    public void Dispose()
    {
        _mpvRenderer?.Dispose();
        _screenPainter.Dispose();
        _previewShaderResourceView.Dispose();
        _screenTexture.Dispose();
        Resources.Dispose();
    }
}
