using System.Text.RegularExpressions;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using System.Runtime.InteropServices;
namespace AlphaChannel.Plugin.Video;

// Ported from AlphaChannel's Core (Voudi, GPL-3.0), with the companion/minion tracking removed -
// see port/alphachannel-engine Stage 4. Screen mounting itself is ported again from AlphaChannel's
// later revamp (tag v1.1.20260725.1088, "Revamp screen to not use VFX" / "Removed need for
// carbuncle, added screen positions in settings"): the VFX/Penumbra/actor-attach approach
// (chara/monster/m7002/.../aetherstreamscreen_{session}.avfx cast on the local player) is gone
// entirely, replaced by ScreenPainter drawing a textured quad directly at an absolute world
// position/yaw/scale - independent of any game object, so it no longer rides along on the player's
// own body.
internal sealed class VideoEngine : IDisposable
{
    // Temporary YouTube account-session test switch. Set this to false to
    // restore the normal Android -> PO-token fallback policy. This is kept in
    // one place so the test can be reverted without changing saved settings.
    private const bool ForceAuthenticatedPoTokenYouTubePlaybackForTesting = false;

    internal const int ScreenWidth = 1920;
    internal const int ScreenHeight = 1080;

    //Default placement for a freshly (re)spawned screen: 2 units in front of the local player, facing
    //back towards them. Matches AlphaChannel's SpawnScreenInFrontOfLocalPlayer 1:1.
    private const float DefaultScreenSpawnDistance = 2.0f;
    private const float DefaultScreenHeightOffset = 1.0f;

    // Allowed screen scale range.
    internal const float MinScreenScale = 0.1f;
    internal const float MaxScreenScale = 8.0f;

    // Temporarily disabled until independent width/height values can be
    // carried reliably through every deployed Watch Party relay.
    internal const bool IndependentScreenScalingEnabled = false;

    // Position sliders extend this far in either direction from ScreenSpawnAnchor.
    // The range is relative to the last spawn, recenter, or applied preset;
    // it does not impose bounds on the screen's world coordinates.
    internal const float ScreenPositionSliderRange = 10f;

    private readonly ScreenPainter _screenPainter;
    private readonly List<ScreenPositionPreset> _screenPresets = [];

    internal Vector3 ScreenPosition { get; private set; }

    internal float ScreenYaw { get; private set; }

    //
    // ScreenScale remains as the compatibility value used by older code and
    // older Watch Party clients.
    //
    internal float ScreenScale { get; private set; } = 1.0f;

    internal bool DisableFixedScreenScaleRatio { get; private set; }

    internal float ScreenWidthScale { get; private set; } = 1.0f;

    internal float ScreenHeightScale { get; private set; } = 1.0f;

    //Center the Casting tab's position sliders around - updated only on a deliberate re-placement
    //(spawn/recenter/preset apply), never while just dragging the sliders themselves, so the window
    //doesn't shrink out from under the slider mid-drag.
    internal Vector3 ScreenSpawnAnchor { get; private set; }

    private MpvRenderer? _mpvRenderer;

    private readonly ImageRenderer _imageRenderer =
    new();

    private static readonly TimeSpan ImageFadeDuration =
    TimeSpan.FromSeconds(
        0.75);

    private DateTime _imageClockStartedUtc =
    DateTime.MinValue;

    private double _imageElapsedBeforeClockStart;

    private bool _imagePlaybackPaused;

    private CancellationTokenSource? _imagePlaybackCancellation;

    private Task? _imagePlaybackTask;

    private bool _isPlayingImage;

    private ImageMediaSelection? _currentImageSelection;

    private AudioVisualizerMode _audioVisualizerMode =
    AudioVisualizerMode.ClassicBars;

    private AudioVisualizerTheme _audioVisualizerTheme =
    AudioVisualizerTheme.AlphaPurple;

    private Snes9xRenderer? _snesRenderer;
    private bool _isPlayingSnes;

    private GambatteRenderer? _gameBoyRenderer;
    private bool _isPlayingGameBoy;

    private GambatteRenderer? _nesRenderer;
    private bool _isPlayingNes;

    private GambatteRenderer? _gameBoyAdvanceRenderer;
    private bool _isPlayingGameBoyAdvance;

    private GambatteRenderer? _masterSystemRenderer;
    private bool _isPlayingMasterSystem;

    private GambatteRenderer? _gameGearRenderer;
    private bool _isPlayingGameGear;

    // Alpha Channel embedded-browser integration.
    private BrowserRenderer? _browserRenderer;
    private bool _isPlayingBrowser;

    private bool _isPlayingLocalVideo;

    private readonly LocalVideoBroadcastEncoder
        _localVideoBroadcastEncoder =
            new();

    private readonly Texture2D _screenTexture;
    private readonly Texture2D _imageTransitionTexture;
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
    private readonly object _renderCancellationLock = new();

    private DateTime _lastLoadYT = DateTime.MinValue;
    private static readonly Regex YtRegex = new(@"^\w+://[^/]*youtube\.\w+/|^\w+://youtu\.be/", RegexOptions.Compiled);
    private static bool IsYTURL(string url) => YtRegex.IsMatch(url);

    private bool _isActive; // whether the screen should currently be drawing for the local player
    private volatile bool _stopRequested;
    private bool _lastIdle = true;
    private int _pendingVolume = 60;
    private volatile bool _pendingOutputMuted;

    private volatile bool _rendererFailed;
    private BroadcastDiagnosticsSnapshot _lastBroadcastDiagnostics = BroadcastDiagnosticsSnapshot.Idle;
    private DateTime _lastHandledBroadcastFailureUtc = DateTime.MinValue;
    private Task? _renderTask;
    private int _playbackGeneration;
    private volatile bool _disposing;
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    private volatile bool _webResolverFallbackRunning;
    private Task? _webResolverFallbackTask;

    internal bool WebResolverFallbackRunning =>
        _webResolverFallbackRunning;

    private DateTime _lastAudioLevelUpdate =
    DateTime.MinValue;

    // Read fresh at Play() time by MpvRenderer.Initialize so a settings change takes effect on
    // the next video, not the current one - matching how the old VideoPlayer read these.
    internal bool HardwareDecoding { get; set; }

    internal int MaxQualityHeight { get; set; } = 1080;

    internal bool AllowInsecureDirectUrls { get; set; }

    // Filtered Netscape cookie jar created only after the player explicitly
    // connects the account in Alpha Channel's embedded browser. It is never
    // selected for the initial request; only the authenticated PO-token retry
    // reads it.
    internal string? CookiesPath { get; set; }

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

        _imageTransitionTexture =
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
    internal event Action? ExternalPlaybackTakingOver;
    internal bool HasVideoPlaybackSession =>
        _mpvRenderer is not null ||
        (_renderTask is { IsCompleted: false } && !_stopRequested);
    internal bool IsShowingWaitingScreen => _screenPainter.IsLoading;
    internal void SetIdleScreensaver(string? status) => _screenPainter.SetScreensaver(status);
    internal bool IsPlayingSnes => _isPlayingSnes;
    internal bool IsPlayingGameBoy => _isPlayingGameBoy;
    internal bool IsPlayingNes => _isPlayingNes;
    internal bool IsPlayingGameBoyAdvance => _isPlayingGameBoyAdvance;
    internal bool IsPlayingMasterSystem => _isPlayingMasterSystem;
    internal bool IsPlayingGameGear => _isPlayingGameGear;
    internal bool IsPlayingGame => _isPlayingSnes || _isPlayingGameBoy || _isPlayingNes || _isPlayingGameBoyAdvance || _isPlayingMasterSystem || _isPlayingGameGear;
    internal bool IsPlayingBrowser => _isPlayingBrowser;
    internal bool IsBrowserBroadcasting => _browserRenderer?.IsBroadcasting == true;
    internal BrowserRenderer? Browser => _browserRenderer;
    internal bool IsPlayingLocalVideo => _isPlayingLocalVideo;


    internal bool IsPlayingImage =>
    _isPlayingImage;

    internal ImageMediaSelection? CurrentImageSelection =>
        _currentImageSelection;

    internal bool IsLocalVideoBroadcasting =>
    _localVideoBroadcastEncoder.IsRunning;

    internal string? LocalVideoBroadcastError =>
        _localVideoBroadcastEncoder.LastError;

    internal BroadcastDiagnosticsSnapshot BroadcastDiagnostics
    {
        get
        {
            var snapshots = new[]
            {
                _browserRenderer?.BroadcastDiagnostics,
                _snesRenderer?.BroadcastDiagnostics,
                _gameBoyRenderer?.BroadcastDiagnostics,
                _nesRenderer?.BroadcastDiagnostics,
                _gameBoyAdvanceRenderer?.BroadcastDiagnostics,
                _masterSystemRenderer?.BroadcastDiagnostics,
                _gameGearRenderer?.BroadcastDiagnostics,
                _localVideoBroadcastEncoder.Diagnostics,
            };

            return snapshots.FirstOrDefault(snapshot => snapshot?.Active == true)
                   ?? snapshots.FirstOrDefault(snapshot => snapshot?.Health == BroadcastHealth.Failed)
                   ?? (_lastBroadcastDiagnostics.Health == BroadcastHealth.Failed
                       ? _lastBroadcastDiagnostics
                       : null)
                   ?? BroadcastDiagnosticsSnapshot.Idle;
        }
    }

    internal bool IsSnesBroadcasting =>
                _snesRenderer?.IsBroadcasting ==
        true;

    internal bool IsGameBoyBroadcasting =>
        _gameBoyRenderer?.IsBroadcasting ==
        true;

    internal bool IsNesBroadcasting =>
        _nesRenderer?.IsBroadcasting == true;

    internal bool IsGameBoyAdvanceBroadcasting =>
        _gameBoyAdvanceRenderer?.IsBroadcasting == true;

    internal bool IsMasterSystemBroadcasting =>
        _masterSystemRenderer?.IsBroadcasting == true;

    internal bool IsGameGearBroadcasting =>
        _gameGearRenderer?.IsBroadcasting == true;

    internal bool SnesControlsEnabled
    {
        get;
        private set;
    } = true;

    internal bool GameBoyControlsEnabled
    {
        get;
        private set;
    } = true;

    internal bool BrowserControlsEnabled
    {
        get;
        private set;
    }

    internal bool BlockAllFfxivKeyboardInput
    {
        get;
        private set;
    }

    private bool _forceFfxivResetChordWasDown;

    internal bool GameBoyCrtFilterEnabled
    {
        get;
        private set;
    }

    internal bool NesCrtFilterEnabled { get; private set; }
    internal bool GameBoyAdvanceCrtFilterEnabled { get; private set; }
    internal bool MasterSystemCrtFilterEnabled { get; private set; }
    internal bool GameGearCrtFilterEnabled { get; private set; }

    internal bool SnesCrtFilterEnabled
    {
        get;
        private set;
    }

    internal void SetBlockAllFfxivKeyboardInput(
    bool enabled)
    {
        BlockAllFfxivKeyboardInput =
            enabled;

        if (!enabled)
        {
            _forceFfxivResetChordWasDown =
                false;
        }
    }

    internal void SetSnesCrtFilterEnabled(
    bool enabled)
    {
        SnesCrtFilterEnabled =
            enabled;

        _snesRenderer?.SetCrtFilterEnabled(
            enabled);
    }

    internal void SetGameBoyCrtFilterEnabled(
    bool enabled)
    {
        GameBoyCrtFilterEnabled =
            enabled;

        _gameBoyRenderer?
            .SetCrtFilterEnabled(
                enabled);
    }

    internal void SetBrowserControlsEnabled(bool enabled)
    {
        BrowserControlsEnabled = enabled && _isPlayingBrowser && _browserRenderer is not null;
        _forceFfxivResetChordWasDown = false;
        _browserRenderer?.Focus(BrowserControlsEnabled);
    }

    internal void SetNesCrtFilterEnabled(bool enabled)
    {
        NesCrtFilterEnabled = enabled;
        _nesRenderer?.SetCrtFilterEnabled(enabled);
    }

    internal void SetGameBoyAdvanceCrtFilterEnabled(bool enabled)
    {
        GameBoyAdvanceCrtFilterEnabled = enabled;
        _gameBoyAdvanceRenderer?.SetCrtFilterEnabled(enabled);
    }

    internal void SetMasterSystemCrtFilterEnabled(bool enabled)
    {
        MasterSystemCrtFilterEnabled = enabled;
        _masterSystemRenderer?.SetCrtFilterEnabled(enabled);
    }

    internal void SetGameGearCrtFilterEnabled(bool enabled)
    {
        GameGearCrtFilterEnabled = enabled;
        _gameGearRenderer?.SetCrtFilterEnabled(enabled);
    }

    internal void SetSnesControlsEnabled(bool enabled)
    {
        if (SnesControlsEnabled == enabled)
        {
            return;
        }

        SnesControlsEnabled = enabled;

        // Make absolutely sure no SNES button remains "held"
        // when control is handed back to FFXIV.
        if (!enabled && _snesRenderer is not null)
        {
            foreach (Snes9xInput input in Enum.GetValues<Snes9xInput>())
            {
                _snesRenderer.SetButton(
                    0,
                    (int)input,
                    false);
            }
        }
    }

    internal void SetGameBoyControlsEnabled(
    bool enabled)
    {
        if (GameBoyControlsEnabled ==
            enabled)
        {
            return;
        }

        GameBoyControlsEnabled =
            enabled;

        //
        // Release every Game Boy button when input is
        // handed back to FFXIV.
        //

        if (!enabled &&
            (_gameBoyRenderer is not null || _nesRenderer is not null || _gameBoyAdvanceRenderer is not null || _masterSystemRenderer is not null || _gameGearRenderer is not null))
        {
            foreach (GambatteInput input in
                     Enum.GetValues<GambatteInput>())
            {
                (_isPlayingNes ? _nesRenderer : _isPlayingGameBoyAdvance ? _gameBoyAdvanceRenderer : _isPlayingMasterSystem ? _masterSystemRenderer : _isPlayingGameGear ? _gameGearRenderer : _gameBoyRenderer)?.SetButton(
                    (int)input,
                    false);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(
      int virtualKey);


    private static bool IsSnesKeyHeld(
        VirtualKey key)
    {
        return (
            GetAsyncKeyState(
                (int)key) &
            0x8000) != 0;
    }


    //
    // =============================================================
    // Emulator keyboard ownership
    // =============================================================
    //

    private bool TryForceFfxivControl()
    {
        var chordDown =
            IsSnesKeyHeld(
                VirtualKey.CONTROL) &&
            IsSnesKeyHeld(
                VirtualKey.F12);


        //
        // Trigger only once per press instead of once every frame
        // while Ctrl + F12 is being held.
        //

        var triggered =
            chordDown &&
            !_forceFfxivResetChordWasDown;

        _forceFfxivResetChordWasDown =
            chordDown;


        if (!triggered)
        {
            return false;
        }


        if (_isPlayingSnes)
        {
            SetSnesControlsEnabled(
                false);
        }

        if (_isPlayingGameBoy)
        {
            SetGameBoyControlsEnabled(
                false);
        }

        if (_isPlayingBrowser)
        {
            SetBrowserControlsEnabled(false);
        }


        Plugin.ChatGui.Print(
            "[AlphaChannel] Media controls released. Keyboard control returned to FFXIV.");


        return true;
    }


    private static bool IsMouseVirtualKey(
        VirtualKey key)
    {
        return key is
            VirtualKey.LBUTTON or
            VirtualKey.RBUTTON or
            VirtualKey.MBUTTON or
            VirtualKey.XBUTTON1 or
            VirtualKey.XBUTTON2;
    }


    private static void SuppressAllFfxivKeyboardInput()
    {
        //
        // Clear every keyboard key FFXIV considers valid, but leave
        // mouse buttons untouched. Emulator input still works because
        // it is read directly through GetAsyncKeyState before this.
        //

        foreach (var key in
                 Plugin.KeyState
                     .GetValidVirtualKeys())
        {
            if (IsMouseVirtualKey(
                    key))
            {
                continue;
            }

            if (Plugin.KeyState[key])
            {
                Plugin.KeyState[key] =
                    false;
            }
        }
    }
    
    private void UpdateSnesInput()
    {
        if (!_isPlayingSnes || _snesRenderer is null)
        {
            return;
        }

        if (!SnesControlsEnabled)
        {
            _forceFfxivResetChordWasDown =
                false;

            return;
        }


        //
        // Ctrl + F12 always wins over emulator ownership.
        //

        if (BlockAllFfxivKeyboardInput &&
            TryForceFfxivControl())
        {
            return;
        }


        bool Pad(GamepadButtons button) =>
                    Plugin.GamepadState.Raw(button) != 0;

        //
        // Read keyboard directly from Windows.
        //
        // This is deliberately separate from Dalamud KeyState because
        // KeyState is cleared below so FFXIV does not react to SNES keys.
        //

        var cfg =
    Plugin.Cfg;

        var keyUp =
            (VirtualKey)cfg.SnesKeyUp;

        var keyDown =
            (VirtualKey)cfg.SnesKeyDown;

        var keyLeft =
            (VirtualKey)cfg.SnesKeyLeft;

        var keyRight =
            (VirtualKey)cfg.SnesKeyRight;

        var keyA =
            (VirtualKey)cfg.SnesKeyA;

        var keyB =
            (VirtualKey)cfg.SnesKeyB;

        var keyX =
            (VirtualKey)cfg.SnesKeyX;

        var keyY =
            (VirtualKey)cfg.SnesKeyY;

        var keyL =
            (VirtualKey)cfg.SnesKeyL;

        var keyR =
            (VirtualKey)cfg.SnesKeyR;

        var keyStart =
            (VirtualKey)cfg.SnesKeyStart;

        var keySelect =
            (VirtualKey)cfg.SnesKeySelect;


        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.UP,
            IsSnesKeyHeld(keyUp) ||
            Pad((GamepadButtons)cfg.GamepadUp));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.DOWN,
            IsSnesKeyHeld(keyDown) ||
            Pad((GamepadButtons)cfg.GamepadDown));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.LEFT,
            IsSnesKeyHeld(keyLeft) ||
            Pad((GamepadButtons)cfg.GamepadLeft));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.RIGHT,
            IsSnesKeyHeld(keyRight) ||
            Pad((GamepadButtons)cfg.GamepadRight));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.B,
            IsSnesKeyHeld(keyB) ||
            Pad((GamepadButtons)cfg.GamepadB));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.A,
            IsSnesKeyHeld(keyA) ||
            Pad((GamepadButtons)cfg.GamepadA));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.Y,
            IsSnesKeyHeld(keyY) ||
            Pad((GamepadButtons)cfg.GamepadY));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.X,
            IsSnesKeyHeld(keyX) ||
            Pad((GamepadButtons)cfg.GamepadX));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.L,
            IsSnesKeyHeld(keyL) ||
            Pad((GamepadButtons)cfg.GamepadL));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.R,
            IsSnesKeyHeld(keyR) ||
            Pad((GamepadButtons)cfg.GamepadR));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.START,
            IsSnesKeyHeld(keyStart) ||
            Pad((GamepadButtons)cfg.GamepadStart));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.SELECT,
            IsSnesKeyHeld(keySelect) ||
            Pad((GamepadButtons)cfg.GamepadSelect));


        //
        // Remove those keyboard presses from Dalamud's game-facing
        // key state so FFXIV does not react to them.
        //

        VirtualKey[] snesKeys =
 [
     keyUp,
    keyDown,
    keyLeft,
    keyRight,

    keyA,
    keyB,
    keyX,
    keyY,

    keyL,
    keyR,

    keyStart,
    keySelect
 ];


        if (BlockAllFfxivKeyboardInput)
        {
            SuppressAllFfxivKeyboardInput();
        }
        else
        {
            //
            // Normal mode:
            // only consume keys actually assigned to SNES.
            //

            foreach (var key in
                     snesKeys)
            {
                if (Plugin.KeyState[key])
                {
                    Plugin.KeyState[key] =
                        false;
                }
            }
        }
    }

    private void UpdateGameBoyInput()
    {
        var renderer = _isPlayingNes ? _nesRenderer : _isPlayingGameBoyAdvance ? _gameBoyAdvanceRenderer : _isPlayingMasterSystem ? _masterSystemRenderer : _isPlayingGameGear ? _gameGearRenderer : _gameBoyRenderer;
        if ((!_isPlayingGameBoy && !_isPlayingNes && !_isPlayingGameBoyAdvance && !_isPlayingMasterSystem && !_isPlayingGameGear) || renderer is null)
        {
            return;
        }

        if (!GameBoyControlsEnabled)
        {
            _forceFfxivResetChordWasDown =
                false;

            return;
        }


        //
        // Ctrl + F12 always returns keyboard ownership to FFXIV.
        //

        if (BlockAllFfxivKeyboardInput &&
            TryForceFfxivControl())
        {
            return;
        }


        bool Pad(
                    GamepadButtons button) =>
            Plugin.GamepadState.Raw(
                button) != 0;


        //
        // For the first Game Boy pass, reuse the matching
        // SNES keyboard bindings.
        //
        // Game Boy only needs:
        // D-pad, A, B, Start and Select.
        //

        var cfg =
            Plugin.Cfg;

        var keyUp =
            (VirtualKey)cfg.SnesKeyUp;

        var keyDown =
            (VirtualKey)cfg.SnesKeyDown;

        var keyLeft =
            (VirtualKey)cfg.SnesKeyLeft;

        var keyRight =
            (VirtualKey)cfg.SnesKeyRight;

        var keyA =
            (VirtualKey)cfg.SnesKeyA;

        var keyB =
            (VirtualKey)cfg.SnesKeyB;

        var keyL =
            (VirtualKey)cfg.SnesKeyL;

        var keyR =
            (VirtualKey)cfg.SnesKeyR;

        var keyStart =
            (VirtualKey)cfg.SnesKeyStart;

        var keySelect =
            (VirtualKey)cfg.SnesKeySelect;


        //
        // D-pad
        //

        renderer.SetButton(
            (int)GambatteInput.Up,
            IsSnesKeyHeld(
                keyUp) ||
            Pad(
                (GamepadButtons)cfg.GamepadUp));

        renderer.SetButton(
            (int)GambatteInput.Down,
            IsSnesKeyHeld(
                keyDown) ||
            Pad(
                (GamepadButtons)cfg.GamepadDown));

        renderer.SetButton(
            (int)GambatteInput.Left,
            IsSnesKeyHeld(
                keyLeft) ||
            Pad(
                (GamepadButtons)cfg.GamepadLeft));

        renderer.SetButton(
            (int)GambatteInput.Right,
            IsSnesKeyHeld(
                keyRight) ||
            Pad(
                (GamepadButtons)cfg.GamepadRight));


        //
        // Face buttons
        //

        renderer.SetButton(
            (int)GambatteInput.B,
            IsSnesKeyHeld(
                keyB) ||
            Pad(
                (GamepadButtons)cfg.GamepadB));

        renderer.SetButton(
            (int)GambatteInput.A,
            IsSnesKeyHeld(
                keyA) ||
            Pad(
                (GamepadButtons)cfg.GamepadA));

        if (_isPlayingGameBoyAdvance)
        {
            renderer.SetButton((int)GambatteInput.L,
                IsSnesKeyHeld(keyL) || Pad((GamepadButtons)cfg.GamepadL));
            renderer.SetButton((int)GambatteInput.R,
                IsSnesKeyHeld(keyR) || Pad((GamepadButtons)cfg.GamepadR));
        }


        //
        // Start / Select
        //

        renderer.SetButton(
            (int)GambatteInput.Start,
            IsSnesKeyHeld(
                keyStart) ||
            Pad(
                (GamepadButtons)cfg.GamepadStart));

        renderer.SetButton(
            (int)GambatteInput.Select,
            IsSnesKeyHeld(
                keySelect) ||
            Pad(
                (GamepadButtons)cfg.GamepadSelect));


        //
        // Prevent FFXIV from receiving the Game Boy
        // keyboard controls while emulator input is active.
        //

        var gameBoyKeys = new List<VirtualKey>
 {
     keyUp,
    keyDown,
    keyLeft,
    keyRight,
    keyA,
    keyB,
    keyStart,
    keySelect
 };

        if (_isPlayingGameBoyAdvance)
        {
            gameBoyKeys.Add(keyL);
            gameBoyKeys.Add(keyR);
        }


        if (BlockAllFfxivKeyboardInput)
        {
            SuppressAllFfxivKeyboardInput();
        }
        else
        {
            //
            // Normal mode:
            // only consume Game Boy-bound keyboard keys.
            //

            foreach (var key in
                     gameBoyKeys)
            {
                if (Plugin.KeyState[key])
                {
                    Plugin.KeyState[key] =
                        false;
                }
            }
        }
    }

    internal bool IsAudioOnly { get; private set; }

    // Hides the world-space TV without stopping or disposing playback.
    // Used when a paused host temporarily despawns their screen.
    internal void DespawnScreen()
    {
        if (!_isActive)
        {
            return;
        }

        _isActive = false;

        _screenPainter.SetLoading(false);
        _screenPainter.SetTarget(null);
    }

    // Restores a previously-despawned TV without restarting playback
    // or changing its saved world position.
    internal void RespawnScreen()
    {
        if (_isActive)
        {
            return;
        }

        if (Plugin.ObjectTable.LocalPlayer is null)
        {
            return;
        }

        _screenPainter.SetTarget(_screenTexture);

        _isActive = true;

        _screenPainter.SetTransform(
            ScreenPosition,
            ScreenYaw,
            ScreenWidthScale,
            ScreenHeightScale);
    }

    internal nint PreviewTextureHandle =>
    _previewShaderResourceView.NativePointer;

    // Set only from PlayVideo's background task on a genuine init/decode failure (e.g. mpv/yt-dlp
    // never downloaded, so mpv_create() throws DllNotFoundException) - VideoPlayer polls this from
    // GetProgress() to flip its own State/LastError, since PlayVideo itself returns long before
    // the failure is known and its caller's try/catch never sees it.
    internal string? LastError { get; private set; }

    internal void PlayImage(
    ImageMediaSelection selection)
    {
        if (_disposing)
        {
            return;
        }

        if (selection.ImageUrls.Count == 0)
        {
            LastError =
                "The image selection contains no images.";

            return;
        }

        //
        // Slideshow scheduling will be added after still-image playback has
        // been verified. For now load the first image from either descriptor.
        //
        var imageUrl =
            selection.PrimaryImageUrl;

        //
        // Stop whichever renderer currently owns the shared TV texture.
        // StopVideo also invalidates delayed mpv work before we assign image
        // ownership to the same texture.
        //
        StopVideo();

        LastError =
            null;

        IsAudioOnly =
            false;

        _screenPainter.SetAudioOnly(
            false);

        _screenPainter.SetAudioLevel(
            0f);

        _currentImageSelection =
            selection;

        _isPlayingImage =
            true;

        _imageElapsedBeforeClockStart =
    0d;

        _imageClockStartedUtc =
            DateTime.UtcNow;

        _imagePlaybackPaused =
            false;

        _imagePlaybackCancellation =
            new CancellationTokenSource();

        var cancellation =
            _imagePlaybackCancellation;

        var imageGeneration =
            _playbackGeneration;

        AssignScreenForSession(
            _screenTexture);

        _screenPainter.SetLoading(
            true);

        _screenPainter.SetTransform(
            ScreenPosition,
            ScreenYaw,
            ScreenWidthScale,
            ScreenHeightScale);

        _isActive =
            true;

        _imagePlaybackTask =
            selection.Mode ==
                ImageMediaMode.Slideshow
                ? RunSlideshowAsync(
                    selection,
                    imageGeneration,
                    cancellation)
                : LoadStillImageAsync(
                    imageUrl,
                    imageGeneration,
                    cancellation);
    }

    private async Task LoadStillImageAsync(
        string imageUrl,
        int imageGeneration,
        CancellationTokenSource cancellation)
    {
        var result =
            await _imageRenderer
                .LoadAsync(
                    imageUrl,
                    _screenTexture,
                    ScreenWidth,
                    ScreenHeight,
                    cancellation.Token)
                .ConfigureAwait(false);

        //
        // Ignore completion from an image replaced by newer media.
        //
        if (cancellation.IsCancellationRequested ||
            !ReferenceEquals(
                _imagePlaybackCancellation,
                cancellation) ||
            imageGeneration !=
                _playbackGeneration)
        {
            return;
        }

        if (!result.Success)
        {
            LastError =
                result.Error ??
                "The image could not be displayed.";

            _isPlayingImage =
                false;

            _currentImageSelection =
                null;

            _isActive =
                false;

            _screenPainter.SetLoading(
                false);

            _screenPainter.SetTarget(
                null);

            AepLog.Warning(
                $"[Image] Playback failed: {LastError}");

            return;
        }

        _screenPainter.SetLoading(
            false);

        _isActive =
            true;

        AepLog.Info(
            "[Image] Still image is ready.");
    }

    private async Task RunSlideshowAsync(
    ImageMediaSelection selection,
    int imageGeneration,
    CancellationTokenSource cancellation)
    {
        var imageCount =
            selection.ImageUrls.Count;

        if (imageCount == 0)
        {
            FailImagePlayback(
                "The slideshow contains no images.",
                imageGeneration,
                cancellation);

            return;
        }

        var lastDisplayedIndex =
     -1;

        var hasDisplayedImage =
            false;

        //
        // Before the first successful image, try each URL in sequence instead
        // of immediately failing the complete slideshow.
        //
        var initialCandidateIndex =
            0;

        var initialFailureCount =
            0;

        var preloadStarted =
    false;

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (!IsCurrentImageRequest(
                        imageGeneration,
                        cancellation))
                {
                    return;
                }

                var elapsedSeconds =
                    GetImageElapsedSeconds();

                var absoluteIndex =
                    (long)Math.Floor(
                        elapsedSeconds /
                        selection.SecondsPerImage);

                if (!selection.Loop &&
                    absoluteIndex >=
                        imageCount)
                {
                    CompleteNonLoopingSlideshow(
                        imageGeneration,
                        cancellation);

                    return;
                }

                var selectedIndex =
      !hasDisplayedImage
          ? initialCandidateIndex
          : selection.Loop
              ? (int)(
                  absoluteIndex %
                  imageCount)
              : (int)Math.Min(
                  absoluteIndex,
                  imageCount - 1);

                if (selectedIndex !=
                    lastDisplayedIndex)
                {
                    var useFade =
    hasDisplayedImage &&
    selection.Transition ==
        ImageTransition.Fade;

                    var result =
                        await DisplaySlideshowImageAsync(
                                selection.ImageUrls[
                                    selectedIndex],
                                useFade,
                                cancellation.Token)
                            .ConfigureAwait(false);

                    if (!IsCurrentImageRequest(
                            imageGeneration,
                            cancellation))
                    {
                        return;
                    }

                    if (result.Success)
                    {
                        lastDisplayedIndex =
                            selectedIndex;

                        hasDisplayedImage =
                            true;

                        _screenPainter.SetLoading(
      false);

                        _isActive =
                            true;

                        AepLog.Debug(
                            $"[Image] Displaying slideshow image " +
                            $"{selectedIndex + 1}/{imageCount}.");

                        //
                        // Once something is visible, prepare every other slide in the
                        // background. Preloading only fills ImageRenderer's bounded pixel
                        // cache and never uploads over the image currently on the TV.
                        //
                        if (!preloadStarted)
                        {
                            preloadStarted =
                                true;

                            _ = PreloadRemainingSlideshowImagesAsync(
                                selection,
                                selectedIndex,
                                imageGeneration,
                                cancellation);
                        }
                    }
                    else if (!hasDisplayedImage)
                    {
                        initialFailureCount++;

                        AepLog.Warning(
                            $"[Image] Slideshow image " +
                            $"{selectedIndex + 1} could not be loaded: " +
                            $"{result.Error}");

                        if (initialFailureCount >=
                            imageCount)
                        {
                            FailImagePlayback(
                                "None of the slideshow images could be displayed.",
                                imageGeneration,
                                cancellation);

                            return;
                        }

                        //
                        // Try the next image immediately. Do not wait for the slideshow
                        // timer because there is still nothing visible on the TV.
                        //
                        initialCandidateIndex =
                            (selectedIndex + 1) %
                            imageCount;

                        continue;
                    }
                    else
                    {
                        //
                        // Once at least one image is visible, retain it when a later image
                        // fails. Mark the failed position as handled for this cycle so the
                        // scheduler can continue to the following slide.
                        //
                        lastDisplayedIndex =
                            selectedIndex;

                        AepLog.Warning(
                            $"[Image] Slideshow image " +
                            $"{selectedIndex + 1} was skipped: " +
                            $"{result.Error}");
                    }
                }

                await Task.Delay(
                        100,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced media and normal shutdown both arrive here.
        }
    }

    private async Task<ImageRenderResult> DisplaySlideshowImageAsync(
    string imageUrl,
    bool useFade,
    CancellationToken cancellationToken)
    {
        //
        // The first image and Instant transitions write directly to the
        // primary screen texture.
        //
        if (!useFade)
        {
            _screenPainter.ClearImageTransition();

            return await _imageRenderer
                .LoadAsync(
                    imageUrl,
                    _screenTexture,
                    ScreenWidth,
                    ScreenHeight,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        //
        // Prepare the incoming image on the secondary texture. This does not
        // disturb the image currently visible on the primary screen texture.
        //
        var transitionResult =
            await _imageRenderer
                .LoadAsync(
                    imageUrl,
                    _imageTransitionTexture,
                    ScreenWidth,
                    ScreenHeight,
                    cancellationToken)
                .ConfigureAwait(false);

        if (!transitionResult.Success)
        {
            return transitionResult;
        }

        cancellationToken.ThrowIfCancellationRequested();

        _screenPainter.SetImageTransitionTarget(
            _imageTransitionTexture);

        var transitionStartedUtc =
            DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var elapsed =
                DateTime.UtcNow -
                transitionStartedUtc;

            var progress =
                Math.Clamp(
                    elapsed.TotalSeconds /
                    ImageFadeDuration.TotalSeconds,
                    0d,
                    1d);

            //
            // Smoothstep prevents a visibly abrupt start or end.
            //
            var easedProgress =
                progress *
                progress *
                (3d -
                 2d *
                 progress);

            _screenPainter.SetImageTransitionBlend(
                (float)easedProgress);

            if (progress >=
                1d)
            {
                break;
            }

            await Task.Delay(
                    16,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        //
        // The secondary image is fully visible now. Commit the same cached
        // pixels to the primary texture while the blend remains at 1, then
        // remove the temporary transition target. This avoids a visible flash
        // back to the preceding image.
        //
        var commitResult =
            await _imageRenderer
                .LoadAsync(
                    imageUrl,
                    _screenTexture,
                    ScreenWidth,
                    ScreenHeight,
                    cancellationToken)
                .ConfigureAwait(false);

        if (!commitResult.Success)
        {
            _screenPainter.ClearImageTransition();
            return commitResult;
        }

        _screenPainter.ClearImageTransition();

        return ImageRenderResult.Completed();
    }

    private async Task PreloadRemainingSlideshowImagesAsync(
    ImageMediaSelection selection,
    int displayedIndex,
    int imageGeneration,
    CancellationTokenSource cancellation)
    {
        var imageCount =
            selection.ImageUrls.Count;

        for (var offset = 1;
             offset < imageCount;
             offset++)
        {
            if (!IsCurrentImageRequest(
                    imageGeneration,
                    cancellation))
            {
                return;
            }

            var imageIndex =
                (displayedIndex + offset) %
                imageCount;

            var result =
                await _imageRenderer
                    .PreloadAsync(
                        selection.ImageUrls[
                            imageIndex],
                        ScreenWidth,
                        ScreenHeight,
                        cancellation.Token)
                    .ConfigureAwait(false);

            if (!IsCurrentImageRequest(
                    imageGeneration,
                    cancellation))
            {
                return;
            }

            if (!result.Success)
            {
                //
                // A failed preload is not a playback failure. The normal
                // scheduler will retry or skip it when its turn arrives.
                //
                AepLog.Debug(
                    $"[Image] Could not preload slideshow image " +
                    $"{imageIndex + 1}: {result.Error}");
            }
        }
    }

    private bool IsCurrentImageRequest(
        int imageGeneration,
        CancellationTokenSource cancellation)
    {
        return !cancellation.IsCancellationRequested &&
               ReferenceEquals(
                   _imagePlaybackCancellation,
                   cancellation) &&
               imageGeneration ==
                   _playbackGeneration;
    }

    private void FailImagePlayback(
        string error,
        int imageGeneration,
        CancellationTokenSource cancellation)
    {
        if (!IsCurrentImageRequest(
                imageGeneration,
                cancellation))
        {
            return;
        }

        LastError =
            error;

        _isPlayingImage =
            false;

        _currentImageSelection =
            null;

        _isActive =
            false;

        _screenPainter.SetLoading(
            false);

        _screenPainter.SetTarget(
            null);

        AepLog.Warning(
            $"[Image] Playback failed: {error}");
    }

    private void CompleteNonLoopingSlideshow(
        int imageGeneration,
        CancellationTokenSource cancellation)
    {
        if (!IsCurrentImageRequest(
                imageGeneration,
                cancellation))
        {
            return;
        }

        _isPlayingImage =
            false;

        _currentImageSelection =
            null;

        _isActive =
            false;

        _screenPainter.SetLoading(
            false);

        _screenPainter.SetTarget(
            null);

        AepLog.Debug(
            "[Image] Non-looping slideshow completed.");
    }

    private double GetImageElapsedSeconds()
    {
        if (!_isPlayingImage)
        {
            return 0d;
        }

        if (_imagePlaybackPaused)
        {
            return Math.Max(
                0d,
                _imageElapsedBeforeClockStart);
        }

        return Math.Max(
            0d,
            _imageElapsedBeforeClockStart +
            (DateTime.UtcNow -
             _imageClockStartedUtc).TotalSeconds);
    }

    private void StopImagePlayback(
        bool waitForCompletion = false)
    {

        _screenPainter.ClearImageTransition();

        var cancellation =
            _imagePlaybackCancellation;

        var playbackTask =
            _imagePlaybackTask;

        _imagePlaybackCancellation =
            null;

        if (cancellation is not null)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // An earlier stop already completed cancellation.
            }

            if (waitForCompletion &&
                playbackTask is { IsCompleted: false })
            {
                try
                {
                    if (!playbackTask.Wait(TimeSpan.FromSeconds(3)))
                    {
                        AepLog.Warning(
                            "[Image] Playback worker did not stop within 3 seconds.");
                    }
                }
                catch (AggregateException exception)
                    when (exception.InnerExceptions.All(
                        inner => inner is OperationCanceledException))
                {
                    // Normal during shutdown.
                }
            }

            cancellation.Dispose();
        }

        _imagePlaybackTask =
            null;

        _currentImageSelection =
            null;

        _isPlayingImage =
            false;
    }

    internal void StopVideo()
    {
        _pendingOutputMuted = false;

        StopImagePlayback();
        StopLocalVideoBroadcast();
        if (_isPlayingSnes)
        {
            AepLog.Debug(
                "[SNES9X] Stopping game.");

            // Release every SNES button and immediately return
            // keyboard/gamepad control to FFXIV.
            SetSnesControlsEnabled(false);

            _isPlayingSnes = false;
            _isActive = false;
            IsAudioOnly = false;

            try
            {
                _snesRenderer?.Unload();
            }
            catch (Exception exception)
            {
                AepLog.Warning(
                    $"[SNES9X] Failed to unload game: {exception.Message}");
            }

            _screenPainter.SetAudioOnly(false);
            _screenPainter.SetAudioLevel(0f);
            _screenPainter.SetLoading(false);
            _screenPainter.SetTarget(null);

            return;
        }

        if (_isPlayingGameBoy)
        {
            AepLog.Debug(
                "[GAMBATTE] Stopping game.");

            // Release Game Boy buttons and immediately
            // return keyboard/controller input to FFXIV.
            SetGameBoyControlsEnabled(
                false);

            _isPlayingGameBoy = false;
            _isActive = false;
            IsAudioOnly = false;

            try
            {
                _gameBoyRenderer?.Unload();
            }
            catch (Exception exception)
            {
                AepLog.Warning(
                    $"[GAMBATTE] Failed to unload game: {exception.Message}");
            }

            _screenPainter.SetAudioOnly(false);
            _screenPainter.SetAudioLevel(0f);
            _screenPainter.SetLoading(false);
            _screenPainter.SetTarget(null);

            return;
        }

        if (_isPlayingMasterSystem)
        {
            AepLog.Debug("[GEARSYSTEM] Stopping game.");
            SetGameBoyControlsEnabled(false);
            _isPlayingMasterSystem = false;
            _isActive = false;
            IsAudioOnly = false;
            try { _masterSystemRenderer?.Unload(); }
            catch (Exception exception)
            {
                AepLog.Warning($"[GEARSYSTEM] Failed to unload game: {exception.Message}");
            }
            _screenPainter.SetAudioOnly(false);
            _screenPainter.SetAudioLevel(0f);
            _screenPainter.SetLoading(false);
            _screenPainter.SetTarget(null);
            return;
        }

        if (_isPlayingGameGear)
        {
            AepLog.Debug("[GEARSYSTEM] Stopping Game Gear game.");
            SetGameBoyControlsEnabled(false);
            _isPlayingGameGear = false;
            _isActive = false;
            IsAudioOnly = false;
            try { _gameGearRenderer?.Unload(); }
            catch (Exception exception)
            {
                AepLog.Warning($"[GEARSYSTEM] Failed to unload Game Gear game: {exception.Message}");
            }
            _screenPainter.SetAudioOnly(false);
            _screenPainter.SetAudioLevel(0f);
            _screenPainter.SetLoading(false);
            _screenPainter.SetTarget(null);
            return;
        }

        if (_isPlayingBrowser)
        {
            AepLog.Debug("[BROWSER] Stopping embedded browser.");
            SetBrowserControlsEnabled(false);
            _isPlayingBrowser = false;
            _isActive = false;
            IsAudioOnly = false;
            try { _browserRenderer?.Dispose(); }
            catch (Exception exception) { AepLog.Warning($"[BROWSER] Failed to stop: {exception.Message}"); }
            _browserRenderer = null;
            _screenPainter.SetAudioOnly(false);
            _screenPainter.SetAudioLevel(0f);
            _screenPainter.SetLoading(false);
            _screenPainter.SetTarget(null);
            return;
        }

        if (_isPlayingNes)
        {
            AepLog.Debug("[NESTOPIA] Stopping game.");
            SetGameBoyControlsEnabled(false);
            _isPlayingNes = false;
            _isActive = false;
            IsAudioOnly = false;
            try { _nesRenderer?.Unload(); }
            catch (Exception exception)
            {
                AepLog.Warning($"[NESTOPIA] Failed to unload game: {exception.Message}");
            }
            _screenPainter.SetAudioOnly(false);
            _screenPainter.SetAudioLevel(0f);
            _screenPainter.SetLoading(false);
            _screenPainter.SetTarget(null);
            return;
        }

        if (_isPlayingGameBoyAdvance)
        {
            AepLog.Debug("[MGBA] Stopping game.");
            SetGameBoyControlsEnabled(false);
            _isPlayingGameBoyAdvance = false;
            _isActive = false;
            IsAudioOnly = false;
            try { _gameBoyAdvanceRenderer?.Unload(); }
            catch (Exception exception)
            {
                AepLog.Warning($"[MGBA] Failed to unload game: {exception.Message}");
            }
            _screenPainter.SetAudioOnly(false);
            _screenPainter.SetAudioLevel(0f);
            _screenPainter.SetLoading(false);
            _screenPainter.SetTarget(null);
            return;
        }

        // Invalidate any currently-running MPV render task before
        // another playback mode is allowed to take over the screen.
        _playbackGeneration++;

        _stopRequested = true;

        _isActive = false;
        _rendererFailed = false;
        _isPlayingLocalVideo = false;
        IsAudioOnly = false;

        _screenPainter.SetAudioOnly(false);
        _screenPainter.SetAudioLevel(0f);
        _screenPainter.SetLoading(false);


        //
        // Explicit Stop means the TV must disappear immediately.
        //
        // Do not wait for the MPV render task / delayed renderer cleanup
        // to eventually clear the screen. Local Video uses StopVideo()
        // as its deliberate "exit this exclusive mode" action.
        //

        _screenPainter.SetTarget(null);


        var renderCancellation = ReplaceRenderCancellation();

        try
        {
            renderCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Its playback task already completed cleanup.
        }

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
                    finally
                    {
                        renderCancellation.Dispose();
                    }
                });
            }
            catch (Exception exception)
            {
                AepLog.Warning(
                    $"[MPV] Failed to schedule renderer cleanup: {exception.Message}");
            }
        }
        else
        {
            renderCancellation.Dispose();
        }

    }

    private void StopVideoForExternalPlayback()
    {
        // Let the VideoPlayer/queue capture the current position and release
        // their now-playing state while MPV can still report its progress.
        // The emulator/browser then receives exclusive ownership of the TV.
        try
        {
            ExternalPlaybackTakingOver?.Invoke();
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Video] Failed to clear playback UI before changing media mode: {exception.Message}");
        }

        StopVideo();
    }

    private void ResetFailedRenderer()
    {
        var renderCancellation = ReplaceRenderCancellation();

        try
        {
            renderCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Its playback task already completed cleanup.
        }

        var renderer = _mpvRenderer;

        _mpvRenderer = null;
        _rendererFailed = false;
        _webResolverFallbackRunning = false;
        _isActive = false;
        IsAudioOnly = false;

        if (_isPlayingLocalVideo)
        {
            _isPlayingLocalVideo = false;
            StopLocalVideoBroadcast();
        }

        _screenPainter.SetAudioOnly(false);
        _screenPainter.SetAudioLevel(0f);
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

        renderCancellation.Dispose();
    }

    private CancellationTokenSource GetRenderCancellation()
    {
        lock (_renderCancellationLock)
        {
            return _renderCancellation;
        }
    }

    private CancellationTokenSource ReplaceRenderCancellation()
    {
        lock (_renderCancellationLock)
        {
            var previous = _renderCancellation;
            _renderCancellation = new CancellationTokenSource();
            return previous;
        }
    }

    private void CompleteRenderCancellation(CancellationTokenSource cancellation)
    {
        lock (_renderCancellationLock)
        {
            if (ReferenceEquals(_renderCancellation, cancellation))
            {
                _renderCancellation = new CancellationTokenSource();
            }
        }

        cancellation.Dispose();
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
            ScreenWidthScale,
            ScreenHeightScale);
    }

    internal bool PlaySnes(
        string romPath)
    {
        if (_isPlayingLocalVideo)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the local video before starting a SNES game.");

            return false;
        }

        if (_disposing)
        {
            return false;
        }

        LastError = null;

        if (string.IsNullOrWhiteSpace(
                romPath) ||
            !File.Exists(
                romPath))
        {
            LastError =
                "SNES ROM file was not found.";

            AepLog.Warning(
                $"[SNES9X] ROM not found: {romPath}");

            return false;
        }

        var extension =
            Path.GetExtension(
                romPath);

        if (!extension.Equals(
                ".sfc",
                StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(
                ".smc",
                StringComparison.OrdinalIgnoreCase))
        {
            LastError =
                "Please select an .sfc or .smc SNES ROM.";

            return false;
        }

        var corePath =
            Resources.GetLocationSNES9X();

        if (string.IsNullOrWhiteSpace(
                corePath) ||
            !File.Exists(
                corePath))
        {
            LastError =
                "Snes9x is still being installed. Try again in a few seconds.";

            AepLog.Warning(
                "[SNES9X] Play requested but core is not installed.");

            return false;
        }

        // Stop whatever was previously using the TV.
        StopVideoForExternalPlayback();

        try
        {
            _snesRenderer ??=
                new Snes9xRenderer(
                    corePath,
                    Resources.RomsDirectory);

            _snesRenderer.SetCrtFilterEnabled(
                SnesCrtFilterEnabled);

            IsAudioOnly = false;

            _screenPainter.SetAudioOnly(
                false);

            AssignScreenForSession(
                _screenTexture);

            _screenPainter.SetLoading(
                true);

            AepLog.Info(
                $"[SNES9X] Loading ROM: {romPath}");

            bool loaded =
                _snesRenderer.Load(
                    _screenTexture,
                    romPath);

            if (!loaded)
            {
                LastError =
                    "Snes9x failed to load the ROM.";

                _screenPainter.SetLoading(
                    false);

                _screenPainter.SetTarget(
                    null);

                return false;
            }

            _snesRenderer.SetVolume(
                _pendingVolume);

            _isPlayingSnes = true;
            _isActive = true;

            // A newly-started game begins in SNES-control mode.
            SetSnesControlsEnabled(true);

            _screenPainter.SetLoading(
                false);

            _screenPainter.SetTransform(
                ScreenPosition,
                ScreenYaw,
                ScreenWidthScale,
                ScreenHeightScale);

            _screenPainter.SetTitle(
                Path.GetFileNameWithoutExtension(
                    romPath),
                "Super Nintendo");

            AepLog.Info(
                "[SNES9X] Game started.");

            return true;
        }
        catch (Exception exception)
        {
            _isPlayingSnes = false;
            _isActive = false;

            try { _snesRenderer?.Dispose(); }
            catch (Exception cleanupException)
            {
                AepLog.Warning($"[SNES9X] Failed startup cleanup: {cleanupException.Message}");
            }
            _snesRenderer = null;

            LastError =
                exception.Message;

            _screenPainter.SetLoading(
                false);

            _screenPainter.SetTarget(
                null);

            AepLog.Error(
         $"[SNES9X] Failed to start game: {exception}");

            return false;
        }
    }

    // =============================================================
    // Local video broadcasting
    // =============================================================

    internal bool StartLocalVideoBroadcast(
        string sourcePath,
        string publishUrl,
        double positionSeconds)
    {
        LastError =
            null;

        if (!_isPlayingLocalVideo)
        {
            LastError =
                "Start the local video before broadcasting it.";

            return false;
        }

        var ffmpegPath =
            Resources.GetLocationFFmpeg();

        var started =
            _localVideoBroadcastEncoder.Start(
                ffmpegPath,
                sourcePath,
                publishUrl,
                positionSeconds);

        if (!started)
        {
            LastError =
                _localVideoBroadcastEncoder.LastError ??
                "The local video broadcast could not be started.";

            return false;
        }

        return true;
    }

    internal void StopLocalVideoBroadcast()
    {
        _localVideoBroadcastEncoder.Stop();
    }


    // =============================================================
    // SNES broadcasting
    // =============================================================

    internal bool StartSnesBroadcast(
        string publishUrl)
    {
        LastError =
            null;

        if (!_isPlayingSnes ||
            _snesRenderer is null)
        {
            LastError =
                "Start an SNES game before broadcasting.";

            AepLog.Warning(
                "[GAME-BROADCAST] Broadcast requested while no SNES game is running.");

            return false;
        }

        if (_snesRenderer.IsBroadcasting)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(
                publishUrl))
        {
            LastError =
                "The broadcast publish URL was empty.";

            AepLog.Warning(
                "[GAME-BROADCAST] SNES broadcast requested with an empty publish URL.");

            return false;
        }

        var ffmpegPath =
            Resources.GetLocationFFmpeg();

        if (string.IsNullOrWhiteSpace(
                ffmpegPath) ||
            !File.Exists(
                ffmpegPath))
        {
            LastError =
                "FFmpeg is not installed yet. Try again in a few seconds.";

            AepLog.Warning(
                "[GAME-BROADCAST] FFmpeg is not installed.");

            return false;
        }

        //
        // IMPORTANT:
        //
        // Do not log publishUrl here.
        //
        // The final RTMP URL contains the account's private
        // stream secret.
        //

        AepLog.Info(
            "[GAME-BROADCAST] Starting SNES broadcast.");

        var started =
            _snesRenderer.StartBroadcast(
                ffmpegPath,
                publishUrl);

        if (!started)
        {
            LastError =
                "FFmpeg failed to start the SNES broadcast.";

            AepLog.Error(
                "[GAME-BROADCAST] SNES broadcast failed to start.");

            return false;
        }

        AepLog.Info(
            "[GAME-BROADCAST] SNES broadcast started.");

        return true;
    }


    internal void StopSnesBroadcast()
    {
        if (_snesRenderer is null)
        {
            return;
        }

        if (!_snesRenderer.IsBroadcasting)
        {
            return;
        }

        AepLog.Info(
            "[GAME-BROADCAST] Stopping SNES broadcast.");

        _snesRenderer.StopBroadcast();

        AepLog.Info(
            "[GAME-BROADCAST] SNES broadcast stopped.");
    }


    // =============================================================
    // Game Boy / Game Boy Color
    // =============================================================

    internal bool StartGameBoyBroadcast(
            string publishUrl)
    {
        LastError =
            null;

        if (!_isPlayingGameBoy ||
            _gameBoyRenderer is null)
        {
            LastError =
                "Start a Game Boy game before broadcasting.";

            AepLog.Warning(
                "[GAME-BROADCAST] Broadcast requested while no Game Boy game is running.");

            return false;
        }

        if (_gameBoyRenderer.IsBroadcasting)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(
                publishUrl))
        {
            LastError =
                "The broadcast publish URL was empty.";

            AepLog.Warning(
                "[GAME-BROADCAST] Broadcast requested with an empty publish URL.");

            return false;
        }

        var ffmpegPath =
            Resources.GetLocationFFmpeg();

        if (string.IsNullOrWhiteSpace(
                ffmpegPath) ||
            !File.Exists(
                ffmpegPath))
        {
            LastError =
                "FFmpeg is not installed yet. Try again in a few seconds.";

            AepLog.Warning(
                "[GAME-BROADCAST] FFmpeg is not installed.");

            return false;
        }

        //
        // IMPORTANT:
        //
        // Do not log publishUrl here.
        //
        // The final RTMP URL contains the account's private
        // stream secret.
        //

        AepLog.Info(
            "[GAME-BROADCAST] Starting Game Boy broadcast.");

        var started =
            _gameBoyRenderer.StartBroadcast(
                ffmpegPath,
                publishUrl);

        if (!started)
        {
            LastError =
                "FFmpeg failed to start the Game Boy broadcast.";

            AepLog.Error(
                "[GAME-BROADCAST] Game Boy broadcast failed to start.");

            return false;
        }

        AepLog.Info(
            "[GAME-BROADCAST] Game Boy broadcast started.");

        return true;
    }


    internal void StopGameBoyBroadcast()
    {
        if (_gameBoyRenderer is null)
        {
            return;
        }

        if (!_gameBoyRenderer.IsBroadcasting)
        {
            return;
        }

        AepLog.Info(
            "[GAME-BROADCAST] Stopping Game Boy broadcast.");

        _gameBoyRenderer.StopBroadcast();

        AepLog.Info(
            "[GAME-BROADCAST] Game Boy broadcast stopped.");
    }

    internal bool StartNesBroadcast(string publishUrl)
    {
        LastError = null;
        if (!_isPlayingNes || _nesRenderer is null)
        {
            LastError = "Start an NES game before broadcasting.";
            return false;
        }
        if (_nesRenderer.IsBroadcasting) return true;
        if (string.IsNullOrWhiteSpace(publishUrl))
        {
            LastError = "The broadcast publish URL was empty.";
            return false;
        }
        var ffmpegPath = Resources.GetLocationFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            LastError = "FFmpeg is not installed yet. Try again in a few seconds.";
            return false;
        }
        AepLog.Info("[GAME-BROADCAST] Starting NES broadcast.");
        if (!_nesRenderer.StartBroadcast(ffmpegPath, publishUrl))
        {
            LastError = "FFmpeg failed to start the NES broadcast.";
            return false;
        }
        return true;
    }

    internal void StopNesBroadcast()
    {
        if (_nesRenderer?.IsBroadcasting == true) _nesRenderer.StopBroadcast();
    }

    internal bool PlayNes(string romPath)
    {
        if (_isPlayingLocalVideo)
        {
            Plugin.ChatGui.Print("[AlphaChannel] Stop the local video before starting an NES game.");
            return false;
        }
        if (_disposing) return false;
        LastError = null;
        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
        {
            LastError = "NES ROM file was not found.";
            return false;
        }
        if (!Path.GetExtension(romPath).Equals(".nes", StringComparison.OrdinalIgnoreCase))
        {
            LastError = "Please select an .nes NES ROM.";
            return false;
        }
        var corePath = Resources.GetLocationNestopia();
        if (string.IsNullOrWhiteSpace(corePath) || !File.Exists(corePath))
        {
            LastError = "Nestopia is still being installed. Try again in a few seconds.";
            return false;
        }

        StopVideoForExternalPlayback();
        try
        {
            // GambatteRenderer hosts the shared software-libretro frontend;
            // the Nestopia profile selects NES content and XRGB8888 conversion.
            _gameBoyRenderer?.Dispose();
            _gameBoyRenderer = null;
            _gameBoyAdvanceRenderer?.Dispose();
            _gameBoyAdvanceRenderer = null;
            _masterSystemRenderer?.Dispose();
            _masterSystemRenderer = null;
            _gameGearRenderer?.Dispose();
            _gameGearRenderer = null;
            _nesRenderer ??= new GambatteRenderer(corePath, Resources.RomsDirectory, nestopia: true);
            _nesRenderer.SetCrtFilterEnabled(NesCrtFilterEnabled);
            IsAudioOnly = false;
            _screenPainter.SetAudioOnly(false);
            AssignScreenForSession(_screenTexture);
            _screenPainter.SetLoading(true);
            if (!_nesRenderer.Load(_screenTexture, romPath))
            {
                LastError = "Nestopia failed to load the ROM.";
                _screenPainter.SetLoading(false);
                _screenPainter.SetTarget(null);
                return false;
            }
            _nesRenderer.SetVolume(_pendingVolume);
            _isPlayingNes = true;
            _isActive = true;
            SetGameBoyControlsEnabled(true);
            _screenPainter.SetLoading(false);
            _screenPainter.SetTransform(ScreenPosition, ScreenYaw, ScreenWidthScale, ScreenHeightScale);
            _screenPainter.SetTitle(Path.GetFileNameWithoutExtension(romPath), "Nintendo Entertainment System");
            AepLog.Info("[NESTOPIA] Game started.");
            return true;
        }
        catch (Exception exception)
        {
            _isPlayingNes = false;
            _isActive = false;
            try { _nesRenderer?.Dispose(); }
            catch (Exception cleanupException) { AepLog.Warning($"[NESTOPIA] Failed startup cleanup: {cleanupException.Message}"); }
            _nesRenderer = null;
            LastError = exception.Message;
            _screenPainter.SetLoading(false);
            _screenPainter.SetTarget(null);
            AepLog.Error($"[NESTOPIA] Failed to start game: {exception}");
            return false;
        }
    }

    internal bool StartMasterSystemBroadcast(string publishUrl)
    {
        LastError = null;
        if (!_isPlayingMasterSystem || _masterSystemRenderer is null)
        {
            LastError = "Start a Master System or SG-1000 game before broadcasting.";
            return false;
        }
        if (_masterSystemRenderer.IsBroadcasting) return true;
        if (string.IsNullOrWhiteSpace(publishUrl))
        {
            LastError = "The broadcast publish URL was empty.";
            return false;
        }
        var ffmpegPath = Resources.GetLocationFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            LastError = "FFmpeg is not installed yet. Try again in a few seconds.";
            return false;
        }
        AepLog.Info("[GAME-BROADCAST] Starting Master System / SG-1000 broadcast.");
        if (!_masterSystemRenderer.StartBroadcast(ffmpegPath, publishUrl))
        {
            LastError = "FFmpeg failed to start the Master System / SG-1000 broadcast.";
            return false;
        }
        return true;
    }

    internal void StopMasterSystemBroadcast()
    {
        if (_masterSystemRenderer?.IsBroadcasting == true) _masterSystemRenderer.StopBroadcast();
    }

    internal bool PlayMasterSystem(string romPath)
    {
        if (_isPlayingLocalVideo)
        {
            Plugin.ChatGui.Print("[AlphaChannel] Stop the local video before starting a Master System or SG-1000 game.");
            return false;
        }
        if (_disposing) return false;
        LastError = null;
        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
        {
            LastError = "Master System / SG-1000 ROM file was not found.";
            return false;
        }
        var extension = Path.GetExtension(romPath);
        if (!extension.Equals(".sms", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".sg", StringComparison.OrdinalIgnoreCase))
        {
            LastError = "Please select a .sms Master System or .sg SG-1000 ROM.";
            return false;
        }
        var corePath = Resources.GetLocationGearsystem();
        if (string.IsNullOrWhiteSpace(corePath) || !File.Exists(corePath))
        {
            LastError = "Gearsystem is still being installed. Try again in a few seconds.";
            return false;
        }

        StopVideoForExternalPlayback();
        try
        {
            _gameBoyRenderer?.Dispose();
            _gameBoyRenderer = null;
            _nesRenderer?.Dispose();
            _nesRenderer = null;
            _gameBoyAdvanceRenderer?.Dispose();
            _gameBoyAdvanceRenderer = null;
            _gameGearRenderer?.Dispose();
            _gameGearRenderer = null;
            _masterSystemRenderer ??= new GambatteRenderer(corePath, Resources.RomsDirectory, gearsystem: true);
            _masterSystemRenderer.SetCrtFilterEnabled(MasterSystemCrtFilterEnabled);
            IsAudioOnly = false;
            _screenPainter.SetAudioOnly(false);
            AssignScreenForSession(_screenTexture);
            _screenPainter.SetLoading(true);
            if (!_masterSystemRenderer.Load(_screenTexture, romPath))
            {
                LastError = "Gearsystem failed to load the ROM.";
                _screenPainter.SetLoading(false);
                _screenPainter.SetTarget(null);
                return false;
            }
            _masterSystemRenderer.SetVolume(_pendingVolume);
            _isPlayingMasterSystem = true;
            _isActive = true;
            SetGameBoyControlsEnabled(true);
            _screenPainter.SetLoading(false);
            _screenPainter.SetTransform(ScreenPosition, ScreenYaw, ScreenWidthScale, ScreenHeightScale);
            _screenPainter.SetTitle(Path.GetFileNameWithoutExtension(romPath),
                extension.Equals(".sg", StringComparison.OrdinalIgnoreCase) ? "SG-1000" : "Sega Master System");
            AepLog.Info("[GEARSYSTEM] Game started.");
            return true;
        }
        catch (Exception exception)
        {
            _isPlayingMasterSystem = false;
            _isActive = false;
            try { _masterSystemRenderer?.Dispose(); }
            catch (Exception cleanupException) { AepLog.Warning($"[GEARSYSTEM] Failed startup cleanup: {cleanupException.Message}"); }
            _masterSystemRenderer = null;
            LastError = exception.Message;
            _screenPainter.SetLoading(false);
            _screenPainter.SetTarget(null);
            AepLog.Error($"[GEARSYSTEM] Failed to start game: {exception}");
            return false;
        }
    }

    internal bool StartGameGearBroadcast(string publishUrl)
    {
        LastError = null;
        if (!_isPlayingGameGear || _gameGearRenderer is null)
        {
            LastError = "Start a Game Gear game before broadcasting.";
            return false;
        }
        if (_gameGearRenderer.IsBroadcasting) return true;
        if (string.IsNullOrWhiteSpace(publishUrl))
        {
            LastError = "The broadcast publish URL was empty.";
            return false;
        }
        var ffmpegPath = Resources.GetLocationFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            LastError = "FFmpeg is not installed yet. Try again in a few seconds.";
            return false;
        }
        AepLog.Info("[GAME-BROADCAST] Starting Game Gear broadcast.");
        if (!_gameGearRenderer.StartBroadcast(ffmpegPath, publishUrl))
        {
            LastError = "FFmpeg failed to start the Game Gear broadcast.";
            return false;
        }
        return true;
    }

    internal void StopGameGearBroadcast()
    {
        if (_gameGearRenderer?.IsBroadcasting == true) _gameGearRenderer.StopBroadcast();
    }

    internal bool PlayGameGear(string romPath)
    {
        if (_isPlayingLocalVideo)
        {
            Plugin.ChatGui.Print("[AlphaChannel] Stop the local video before starting a Game Gear game.");
            return false;
        }
        if (_disposing) return false;
        LastError = null;
        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
        {
            LastError = "Game Gear ROM file was not found.";
            return false;
        }
        var extension = Path.GetExtension(romPath);
        if (!extension.Equals(".gg", StringComparison.OrdinalIgnoreCase))
        {
            LastError = "Please select a .gg Game Gear ROM.";
            return false;
        }
        var corePath = Resources.GetLocationGearsystem();
        if (string.IsNullOrWhiteSpace(corePath) || !File.Exists(corePath))
        {
            LastError = "Gearsystem is still being installed. Try again in a few seconds.";
            return false;
        }

        StopVideoForExternalPlayback();
        try
        {
            _gameBoyRenderer?.Dispose();
            _gameBoyRenderer = null;
            _nesRenderer?.Dispose();
            _nesRenderer = null;
            _gameBoyAdvanceRenderer?.Dispose();
            _gameBoyAdvanceRenderer = null;
            _masterSystemRenderer?.Dispose();
            _masterSystemRenderer = null;
            _gameGearRenderer ??= new GambatteRenderer(corePath, Resources.RomsDirectory, gearsystem: true, gameGear: true);
            _gameGearRenderer.SetCrtFilterEnabled(GameGearCrtFilterEnabled);
            IsAudioOnly = false;
            _screenPainter.SetAudioOnly(false);
            AssignScreenForSession(_screenTexture);
            _screenPainter.SetLoading(true);
            if (!_gameGearRenderer.Load(_screenTexture, romPath))
            {
                LastError = "Gearsystem failed to load the Game Gear ROM.";
                _screenPainter.SetLoading(false);
                _screenPainter.SetTarget(null);
                return false;
            }
            _gameGearRenderer.SetVolume(_pendingVolume);
            _isPlayingGameGear = true;
            _isActive = true;
            SetGameBoyControlsEnabled(true);
            _screenPainter.SetLoading(false);
            _screenPainter.SetTransform(ScreenPosition, ScreenYaw, ScreenWidthScale, ScreenHeightScale);
            _screenPainter.SetTitle(Path.GetFileNameWithoutExtension(romPath), "Game Gear");
            AepLog.Info("[GEARSYSTEM] Game Gear game started.");
            return true;
        }
        catch (Exception exception)
        {
            _isPlayingGameGear = false;
            _isActive = false;
            try { _gameGearRenderer?.Dispose(); }
            catch (Exception cleanupException) { AepLog.Warning($"[GEARSYSTEM] Failed Game Gear startup cleanup: {cleanupException.Message}"); }
            _gameGearRenderer = null;
            LastError = exception.Message;
            _screenPainter.SetLoading(false);
            _screenPainter.SetTarget(null);
            AepLog.Error($"[GEARSYSTEM] Failed to start Game Gear game: {exception}");
            return false;
        }
    }

    internal bool StartGameBoyAdvanceBroadcast(string publishUrl)
    {
        LastError = null;
        if (!_isPlayingGameBoyAdvance || _gameBoyAdvanceRenderer is null)
        {
            LastError = "Start a Game Boy Advance game before broadcasting.";
            return false;
        }
        if (_gameBoyAdvanceRenderer.IsBroadcasting) return true;
        if (string.IsNullOrWhiteSpace(publishUrl))
        {
            LastError = "The broadcast publish URL was empty.";
            return false;
        }
        var ffmpegPath = Resources.GetLocationFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            LastError = "FFmpeg is not installed yet. Try again in a few seconds.";
            return false;
        }
        AepLog.Info("[GAME-BROADCAST] Starting Game Boy Advance broadcast.");
        if (!_gameBoyAdvanceRenderer.StartBroadcast(ffmpegPath, publishUrl))
        {
            LastError = "FFmpeg failed to start the Game Boy Advance broadcast.";
            return false;
        }
        return true;
    }

    internal void StopGameBoyAdvanceBroadcast()
    {
        if (_gameBoyAdvanceRenderer?.IsBroadcasting == true) _gameBoyAdvanceRenderer.StopBroadcast();
    }

    internal bool PlayGameBoyAdvance(string romPath)
    {
        if (_isPlayingLocalVideo)
        {
            Plugin.ChatGui.Print("[AlphaChannel] Stop the local video before starting a Game Boy Advance game.");
            return false;
        }
        if (_disposing) return false;
        LastError = null;
        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
        {
            LastError = "Game Boy Advance ROM file was not found.";
            return false;
        }
        if (!Path.GetExtension(romPath).Equals(".gba", StringComparison.OrdinalIgnoreCase))
        {
            LastError = "Please select a .gba Game Boy Advance ROM.";
            return false;
        }
        var corePath = Resources.GetLocationMgba();
        if (string.IsNullOrWhiteSpace(corePath) || !File.Exists(corePath))
        {
            LastError = "mGBA is still being installed. Try again in a few seconds.";
            return false;
        }

        StopVideoForExternalPlayback();
        try
        {
            _gameBoyRenderer?.Dispose();
            _gameBoyRenderer = null;
            _nesRenderer?.Dispose();
            _nesRenderer = null;
            _masterSystemRenderer?.Dispose();
            _masterSystemRenderer = null;
            _gameGearRenderer?.Dispose();
            _gameGearRenderer = null;
            _gameBoyAdvanceRenderer ??= new GambatteRenderer(corePath, Resources.RomsDirectory, mgba: true);
            _gameBoyAdvanceRenderer.SetCrtFilterEnabled(GameBoyAdvanceCrtFilterEnabled);
            IsAudioOnly = false;
            _screenPainter.SetAudioOnly(false);
            AssignScreenForSession(_screenTexture);
            _screenPainter.SetLoading(true);
            if (!_gameBoyAdvanceRenderer.Load(_screenTexture, romPath))
            {
                LastError = "mGBA failed to load the ROM.";
                _screenPainter.SetLoading(false);
                _screenPainter.SetTarget(null);
                return false;
            }
            _gameBoyAdvanceRenderer.SetVolume(_pendingVolume);
            _isPlayingGameBoyAdvance = true;
            _isActive = true;
            SetGameBoyControlsEnabled(true);
            _screenPainter.SetLoading(false);
            _screenPainter.SetTransform(ScreenPosition, ScreenYaw, ScreenWidthScale, ScreenHeightScale);
            _screenPainter.SetTitle(Path.GetFileNameWithoutExtension(romPath), "Game Boy Advance");
            AepLog.Info("[MGBA] Game started.");
            return true;
        }
        catch (Exception exception)
        {
            _isPlayingGameBoyAdvance = false;
            _isActive = false;
            try { _gameBoyAdvanceRenderer?.Dispose(); }
            catch (Exception cleanupException) { AepLog.Warning($"[MGBA] Failed startup cleanup: {cleanupException.Message}"); }
            _gameBoyAdvanceRenderer = null;
            LastError = exception.Message;
            _screenPainter.SetLoading(false);
            _screenPainter.SetTarget(null);
            AepLog.Error($"[MGBA] Failed to start game: {exception}");
            return false;
        }
    }

    internal bool PlayBrowser(string initialUrl)
    {
        if (_disposing) return false;
        LastError = null;
        StopVideoForExternalPlayback();
        try
        {
            var cache = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Browser", "Cache");
            _browserRenderer = CreateBrowserRenderer(cache);
            _browserRenderer.SetVolume(_pendingVolume);
            if (!string.IsNullOrWhiteSpace(initialUrl)) _browserRenderer.Navigate(initialUrl);
            _isPlayingBrowser = true;
            BrowserControlsEnabled = false;
            IsAudioOnly = false;
            _screenPainter.SetAudioOnly(false);
            AssignScreenForSession(_screenTexture);
            _isActive = true;
            _screenPainter.SetLoading(false);
            _screenPainter.SetTransform(ScreenPosition, ScreenYaw, ScreenWidthScale, ScreenHeightScale);
            _screenPainter.SetTitle("Browser", "Web Browser");
            AepLog.Info("[BROWSER] Browser started on the local TV.");
            return true;
        }
        catch (Exception exception)
        {
            _browserRenderer?.Dispose();
            _browserRenderer = null;
            _isPlayingBrowser = false;
            _isActive = false;
            LastError = exception.Message;
            AepLog.Error($"[BROWSER] Failed to start: {exception}");
            return false;
        }
    }

    private BrowserRenderer CreateBrowserRenderer(string cache) => new(_screenTexture, cache);

    internal bool StartBrowserBroadcast(string publishUrl)
    {
        if (!_isPlayingBrowser || _browserRenderer is null)
        {
            LastError = "Open the browser before broadcasting.";
            return false;
        }
        var ffmpeg = Resources.GetLocationFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            LastError = "FFmpeg is not installed yet. Try again in a few seconds.";
            return false;
        }
        return _browserRenderer.StartBroadcast(ffmpeg, publishUrl);
    }

    internal void StopBrowserBroadcast() => _browserRenderer?.StopBroadcast();


    internal bool PlayGameBoy(
        string romPath)
    {
        if (_isPlayingLocalVideo)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the local video before starting a Game Boy game.");

            return false;
        }

        if (_disposing)
        {
            return false;
        }

        LastError =
            null;

        if (string.IsNullOrWhiteSpace(
                romPath) ||
            !File.Exists(
                romPath))
        {
            LastError =
                "Game Boy ROM file was not found.";

            AepLog.Warning(
                $"[GAMBATTE] ROM not found: {romPath}");

            return false;
        }

        var extension =
            Path.GetExtension(
                romPath);

        if (!extension.Equals(
                ".gb",
                StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(
                ".gbc",
                StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(
                ".dmg",
                StringComparison.OrdinalIgnoreCase))
        {
            LastError =
                "Please select a .gb, .gbc or .dmg Game Boy ROM.";

            return false;
        }

        var corePath =
            Resources.GetLocationGambatte();

        if (string.IsNullOrWhiteSpace(
                corePath) ||
            !File.Exists(
                corePath))
        {
            LastError =
                "Gambatte is still being installed. Try again in a few seconds.";

            AepLog.Warning(
                "[GAMBATTE] Play requested but core is not installed.");

            return false;
        }

        //
        // Stop whatever currently owns the TV before Gambatte takes it.
        //

        StopVideoForExternalPlayback();

        try
        {
            _nesRenderer?.Dispose();
            _nesRenderer = null;
            _gameBoyAdvanceRenderer?.Dispose();
            _gameBoyAdvanceRenderer = null;
            _masterSystemRenderer?.Dispose();
            _masterSystemRenderer = null;
            _gameGearRenderer?.Dispose();
            _gameGearRenderer = null;
            _gameBoyRenderer ??=
                new GambatteRenderer(
                    corePath,
                    Resources.RomsDirectory);

            IsAudioOnly =
                false;

            _screenPainter.SetAudioOnly(
                false);

            AssignScreenForSession(
                _screenTexture);

            _screenPainter.SetLoading(
                true);

            AepLog.Info(
                $"[GAMBATTE] Loading ROM: {romPath}");

            var loaded =
                _gameBoyRenderer.Load(
                    _screenTexture,
                    romPath);

            if (!loaded)
            {
                LastError =
                    "Gambatte failed to load the ROM.";

                _screenPainter.SetLoading(
                    false);

                _screenPainter.SetTarget(
                    null);

                return false;
            }

            _gameBoyRenderer.SetVolume(
                _pendingVolume);

            _gameBoyRenderer.SetCrtFilterEnabled(
                GameBoyCrtFilterEnabled);

            _isPlayingGameBoy =
                true;

            _isActive =
                true;

            // A newly-started game begins with input routed
            // to the Game Boy emulator.
            SetGameBoyControlsEnabled(
                true);

            _screenPainter.SetLoading(
                false);

            _screenPainter.SetTransform(
                ScreenPosition,
                ScreenYaw,
                ScreenWidthScale,
                ScreenHeightScale);

            _screenPainter.SetTitle(
                Path.GetFileNameWithoutExtension(
                    romPath),
                extension.Equals(
                    ".gbc",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Game Boy Color"
                    : "Game Boy");

            AepLog.Info(
                "[GAMBATTE] Game started.");

            return true;
        }
        catch (Exception exception)
        {
            _isPlayingGameBoy =
                false;

            _isActive =
                false;

            try { _gameBoyRenderer?.Dispose(); }
            catch (Exception cleanupException)
            {
                AepLog.Warning($"[GAMBATTE] Failed startup cleanup: {cleanupException.Message}");
            }
            _gameBoyRenderer = null;

            LastError =
                exception.Message;

            _screenPainter.SetLoading(
                false);

            _screenPainter.SetTarget(
                null);

            AepLog.Error(
                $"[GAMBATTE] Failed to start game: {exception}");

            return false;
        }
    }


    internal void PlayVideo(
    string url,
    int playbackPosition = 0,
    bool isPlaying = true,
    bool allowWebResolverFallback = true,
    bool isLocalVideo = false,
    bool expectedAudioOnly = false)
    {
        if (_isPlayingImage)
        {
            StopVideo();
        }
        //
        // Local Video is an exclusive TV mode, just like SNES.
        //
        // Normal URL/queue/watch-party playback must not replace it.
        // The user must explicitly stop Local Video first.
        //

        if (_isPlayingLocalVideo &&
            !isLocalVideo)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the local video before using other media playback.");

            return;
        }


        if (IsPlayingGame || IsPlayingBrowser)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the current game or browser to begin playback.");

            return;
        }


        if (_disposing)
        {
            return;
        }

        // A local-output-only mute is used while the host monitors a DJ stream.
        // Do not carry it into a different piece of media.
        if (_mpvRenderer?.GetCurrentUrl() != url)
        {
            _pendingOutputMuted = false;
            _mpvRenderer?.SetMuted(false);
        }


        //
        // Local files are handed directly to MPV.
        //
        // They never use WebMediaUrlResolver / yt-dlp fallback.
        //

        if (isLocalVideo)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !File.Exists(url))
            {
                LastError =
                    "The selected local video file could not be found.";

                AepLog.Warning(
                    $"[LocalVideo] File not found: {url}");

                return;
            }

            allowWebResolverFallback =
                false;

            _isPlayingLocalVideo =
                true;

            AepLog.Info(
                $"[LocalVideo] Starting local playback: {Path.GetFileName(url)}");
        }


        //
        // Never reuse a renderer that genuinely failed.
        //

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


        LastError =
            null;


        _stopRequested =
            false;


        IsAudioOnly =
            false;


        var playbackGeneration =
            ++_playbackGeneration;

        var youtubeRoute =
            IsYTURL(
                url)
                ? ForceAuthenticatedPoTokenYouTubePlaybackForTesting
                    ? YouTubePlaybackRoute.PoToken
                    : Resources
                        .YouTubePolicy
                        .SelectRoute()
                : YouTubePlaybackRoute.Android;

        var useForcedYouTubeCookies =
            ForceAuthenticatedPoTokenYouTubePlaybackForTesting &&
            IsYTURL(url);

        if (useForcedYouTubeCookies)
        {
            AepLog.Info(
                "[YouTube/Test] Forcing authenticated PO-token playback for this video.");

            if (string.IsNullOrWhiteSpace(CookiesPath) ||
                !YouTubeEmbeddedBrowserSession.LooksUsable(CookiesPath))
            {
                AepLog.Warning(
                    "[YouTube/Test] Forced authenticated playback is enabled, " +
                    "but no connected embedded-browser cookie session is available.");
            }
        }


        //
        // Has this attempt actually begun useful playback?
        //
        // Video:
        //     first rendered frame
        //
        // Audio-only:
        //     FILE_LOADED + audio track and no video track
        //
        // We deliberately do NOT treat FILE_LOADED alone as success.
        //

        var playbackStarted =
            0;


        //
        // Prevent multiple MPV events from launching multiple resolver
        // attempts for the same URL.
        //

        var fallbackStarted =
            0;

        var authenticatedYouTubeRetryStarted =
            useForcedYouTubeCookies
                ? 1
                : 0;

        //
        // MPV's final END_FILE error is generic. Preserve a recognisable
        // yt-dlp bot-check warning so the playback policy can make the
        // correct fallback decision.
        //
        string? youtubeVerificationError =
            null;

        string? youtubeAuthenticationError =
            null;

        var youtubePolicySuccessReported =
    0;

        void ReportYouTubeRouteSucceeded()
        {
            //
            // Normal Android and PO-token playback don't need a state
            // transition here. Only a deliberate post-timeout Android
            // probe can return the policy to normal mode.
            //
            if (youtubeRoute !=
                    YouTubePlaybackRoute.AndroidProbe ||
                Volatile.Read(
                    ref fallbackStarted) != 0 ||
                Volatile.Read(
                    ref authenticatedYouTubeRetryStarted) != 0)
            {
                return;
            }

            //
            // Frame callbacks run repeatedly. Report success only once
            // for this playback request.
            //
            if (Interlocked.Exchange(
                    ref youtubePolicySuccessReported,
                    1) != 0)
            {
                return;
            }

            Resources
                .YouTubePolicy
                .ReportPlaybackSucceeded(
                    youtubeRoute);
        }


        void StartWebResolverFallback(
      string failureMessage)
        {
            //
            // The resolved retry does not get another resolver attempt.
            //
            // fallbackStarted is scoped to this PlayVideo() call, so this
            // guarantees at most one automatic fallback for the requested
            // URL.
            //

            if (!allowWebResolverFallback)
            {
                AepLog.Warning(
                    "[WebResolver] Resolved retry failed. No further resolver attempt will be made.");

                return;
            }


            //
            // MPV can be reused across several videos.
            //
            // Do NOT use playbackStarted as a resolver gate here. Previous
            // playback/frame events on the reused renderer can leave that
            // state looking "started" while the newly requested URL has
            // actually failed before producing anything.
            //
            // fallbackStarted is the correct per-request guard.
            //

            if (Interlocked.Exchange(
                    ref fallbackStarted,
                    1) != 0)
            {
                return;
            }


            AepLog.Info(
                $"[WebResolver] First playback attempt failed for: {url}");


            AepLog.Info(
                "[WebResolver] Starting automatic second-chance resolver.");


            _webResolverFallbackTask =
                Task.Run(
                    async () =>
                    {
                        await TryWebResolverFallbackAsync(
                                url,
                                playbackPosition,
                                isPlaying,
                                playbackGeneration,
                                failureMessage,
                                useForcedYouTubeCookies ||
                                Volatile.Read(
                                    ref authenticatedYouTubeRetryStarted) != 0)
                            .ConfigureAwait(false);
                    },
                    _lifetimeCancellation.Token);
        }


        bool StartAuthenticatedYouTubeRetry(
            MpvRenderer renderer,
            string failureMessage)
        {
            if (!IsYTURL(url) ||
                !YouTubePlaybackPolicy.IsAccountRequiredError(failureMessage) ||
                string.IsNullOrWhiteSpace(CookiesPath) ||
                !YouTubeEmbeddedBrowserSession.LooksUsable(CookiesPath) ||
                !YouTubePoTokenSupport.IsAvailable(Resources) ||
                Interlocked.CompareExchange(
                    ref authenticatedYouTubeRetryStarted,
                    1,
                    0) != 0)
            {
                return false;
            }

            AepLog.Info(
                "[YouTube/Account] Account-required video detected. " +
                "Preparing one authenticated PO-token retry.");

            _webResolverFallbackRunning =
                true;

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        var providerReady =
                            await Resources
                                .YouTubePolicy
                                .EnsureProviderReadyAsync()
                                .ConfigureAwait(false);

                        if (!providerReady ||
                            playbackGeneration != _playbackGeneration ||
                            _disposing)
                        {
                            if (!providerReady)
                            {
                                AepLog.Warning(
                                    "[YouTube/Account] The PO-token provider is unavailable; " +
                                    "the browser account will not be sent through Android playback.");
                            }

                            StartWebResolverFallback(failureMessage);
                            return;
                        }

                        renderer.SetYouTubeCookiesPath(CookiesPath);
                        renderer.Play(
                            url,
                            playbackPosition,
                            isPlaying,
                            useYouTubePoTokens: true,
                            useYouTubeCookies: true,
                            preparedVisualizerMode:
                                _audioVisualizerMode != AudioVisualizerMode.ClassicBars
                                    ? _audioVisualizerMode
                                    : null,
                            preparedVisualizerTheme: _audioVisualizerTheme,
                            expectedAudioOnly: expectedAudioOnly);

                        AepLog.Info(
                            "[YouTube/Account] Authenticated PO-token retry started.");
                    }
                    catch (Exception exception)
                    {
                        AepLog.Warning(
                            "[YouTube/Account] Could not start authenticated retry: " +
                            exception.Message);
                        StartWebResolverFallback(failureMessage);
                    }
                },
                _lifetimeCancellation.Token);

            return true;
        }

        AssignScreenForSession(
            _screenTexture);


        _screenPainter.SetLoading(
            true);

        // Make the TV and its loading screen visible before any delayed
        // YouTube work begins. PO-token provider startup can take several
        // seconds on its first use; keeping _isActive false until after that
        // wait made the application appear to have ignored the Play action.
        _isActive =
            true;

        _screenPainter.SetAudioOnly(
            false);

        _screenPainter.SetTransform(
            ScreenPosition,
            ScreenYaw,
            ScreenWidthScale,
            ScreenHeightScale);

        // This playback attempt owns this source for its entire native MPV
        // lifetime. A later StopVideo replaces the engine's current source,
        // so delayed cleanup from an earlier video cannot cancel or dispose
        // the next video's renderer.
        var renderCancellation =
            GetRenderCancellation();

        _renderTask =
            Task.Run(
                async () =>
                {
                    if (IsYTURL(
                            url))
                    {
                        TimeSpan elapsed =
                            DateTime.Now -
                            _lastLoadYT;


                        if (elapsed.TotalSeconds <
                            7)
                        {
                            int sleepTime =
                                Math.Min(
                                    Math.Max(
                                        (int)(
                                            7000 -
                                            elapsed.TotalMilliseconds),
                                        0),
                                    7000);


                            Thread.Sleep(
                                sleepTime);
                        }


                        _lastLoadYT =
                         DateTime.Now;
                    }

                    //
                    // A newer PlayVideo call may have replaced this request
                    // while this YouTube task was waiting for the load
                    // throttle. Never allow the delayed task to load its URL
                    // into the newer playback session.
                    //
                    if (playbackGeneration !=
       _playbackGeneration)
                    {
                        AepLog.Debug(
                            $"[MPV] Abandoning stale delayed playback start. " +
                            $"Generation={playbackGeneration}, " +
                            $"Current={_playbackGeneration}.");

                        return;
                    }

                    var useYouTubePoTokens =
                        youtubeRoute ==
                        YouTubePlaybackRoute.PoToken;

                    //
                    // Start the local provider only when compatibility mode
                    // actually needs it. If the HTTP provider cannot start,
                    // yt-dlp can still use the configured one-shot script
                    // provider.
                    //
                    if (useYouTubePoTokens)
                    {
                        var providerReady =
                            await Resources
                                .YouTubePolicy
                                .EnsureProviderReadyAsync()
                                .ConfigureAwait(false);

                        if (!providerReady)
                        {
                            AepLog.Warning(
                                "[YouTube/Policy] Local PO-token HTTP provider " +
                                "is unavailable. The script provider will be used.");
                        }

                        //
                        // Starting the provider takes time. Ensure this request
                        // was not replaced while we were waiting.
                        //
                        if (playbackGeneration !=
                            _playbackGeneration)
                        {
                            AepLog.Debug(
                                "[YouTube/Policy] Ignoring stale playback " +
                                "request after starting the PO-token provider.");

                            return;
                        }
                    }

                    try
                    {
                        //
                        // =================================================
                        // Renderer callbacks for this playback generation
                        // =================================================
                        //

                        void ConfigureRendererCallbacks(
                            MpvRenderer renderer)
                        {
                            //
                            // =========================================================
                            // MPV DIAGNOSTIC LOGGING
                            // =========================================================
                            //
                            // MPV emits many warnings during otherwise healthy playback:existingRenderer.Play(
                            //
                            // - A/V desynchronisation
                            // - temporary buffering / slow decode
                            // - ytdl informational errors
                            // - driver / timing warnings
                            //
                            // These are NOT terminal playback failures.
                            //
                            // IMPORTANT:
                            //
                            // Do NOT set LastError here.
                            //
                            // LastError is consumed by VideoPlayer as a genuine playback
                            // failure and causes StopVideo() to be called.
                            //
                            // Actual terminal playback failures are handled separately by
                            // OnPlaybackFailed when MPV sends END_FILE with reason=ERROR.
                            //

                            renderer.OnError =
                                message =>
                                {
                                    if (playbackGeneration !=
                                        _playbackGeneration)
                                    {
                                        return;
                                    }


                                    if (string.IsNullOrWhiteSpace(
                                            message))
                                    {
                                        return;
                                    }

                                    if (IsYTURL(url) &&
    YouTubePlaybackPolicy.IsBotCheckError(
        message))
                                    {
                                        youtubeVerificationError =
                                            message;

                                        AepLog.Warning(
                                            "[YouTube/Policy] Recognised a YouTube verification warning.");
                                    }

                                    if (IsYTURL(url) &&
                                        YouTubePlaybackPolicy.IsAccountRequiredError(
                                            message))
                                    {
                                        youtubeAuthenticationError =
                                            message;

                                        AepLog.Warning(
                                            "[YouTube/Account] Recognised an account-required warning.");
                                    }


                                    AepLog.Warning(
                                        $"[MPV] Playback warning: {message}");


                                };


                            //
                            // Definitive MPV terminal failure.
                            //
                            // MpvRenderer fires this from END_FILE when
                            // reason == MPV_END_FILE_REASON_ERROR.
                            //

                            renderer.OnPlaybackFailed =
    message =>
    {
        if (playbackGeneration !=
            _playbackGeneration)
        {
            return;
        }


        AepLog.Warning(
            $"[MPV] Playback genuinely failed: {message}");


        //
        // =========================================================
        // FIRST FAILURE: KEEP THIS MPV RENDERER ALIVE
        // =========================================================
        //
        // The generic web resolver gets one second chance.
        //
        // IMPORTANT:
        //
        // Do NOT Stop(), Dispose(), ResetFailedRenderer(), or otherwise
        // destroy this MpvRenderer here.
        //
        // Its RenderFrame() call may simply remain blocked waiting for
        // another frame. That is intentional.
        //
        // If the resolver succeeds we will issue another loadfile to
        // THIS SAME mpv instance. The existing render loop will then
        // wake naturally when the resolved media produces frames.
        //
        // This avoids destroying/recreating the D3D/mpv render context
        // during the retry.
        //

        if (allowWebResolverFallback &&
            Volatile.Read(
                ref fallbackStarted) == 0)
        {
            _webResolverFallbackRunning =
                true;


            _rendererFailed =
                false;


            LastError =
                null;


            _isActive =
                true;


            IsAudioOnly =
                false;


            _screenPainter.SetAudioOnly(
                false);


            _screenPainter.SetLoading(
                true);


            AepLog.Info(
                "[WebResolver] Keeping existing MPV renderer alive for automatic retry.");


            var effectiveFailure =
                !string.IsNullOrWhiteSpace(youtubeAuthenticationError)
                    ? youtubeAuthenticationError
                    : !string.IsNullOrWhiteSpace(youtubeVerificationError)
                        ? youtubeVerificationError
                        : message;

            if (StartAuthenticatedYouTubeRetry(
                    renderer,
                    effectiveFailure))
            {
                return;
            }

            //
            // A probe that failed for an ordinary reason must not be
            // treated as proof that YouTube verification is still active.
            //
            // A recognised bot check is handled by the fallback method,
            // which re-enters compatibility mode and extends the timer.
            //
            if (youtubeRoute ==
                    YouTubePlaybackRoute.AndroidProbe &&
                !YouTubePlaybackPolicy.IsBotCheckError(
                    effectiveFailure))
            {
                Resources
                    .YouTubePolicy
                    .ReportProbeInconclusive();
            }

            StartWebResolverFallback(
                effectiveFailure);


            return;
        }


        //
        // =========================================================
        // SECOND FAILURE: FINAL
        // =========================================================
        //
        // The resolver chance has already been consumed, so this is
        // now a genuine final playback failure.
        //

        _webResolverFallbackRunning =
            false;


        _rendererFailed =
            true;


        LastError =
            string.IsNullOrWhiteSpace(
                LastError)
                ? message
                : LastError;


        _isActive =
            false;


        IsAudioOnly =
            false;


        _screenPainter.SetAudioOnly(
            false);


        _screenPainter.SetLoading(
            false);


        _screenPainter.SetTarget(
            null);


        AepLog.Warning(
            $"[MPV] Resolved retry also failed; playback is now final: {message}");


        //
        // Final failure only: wake the render loop so its normal
        // cleanup can run.
        //

        try
        {
            renderer.Stop();
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[MPV] Failed to stop renderer after final playback failure: {exception.Message}");
        }
    };


                            //
                            // FILE_LOADED tells us enough to detect an
                            // audio-only stream, but video isn't considered
                            // successfully playing until a frame is rendered.
                            //

                            renderer.OnMediaLoaded =
     () =>
     {
         if (playbackGeneration !=
             _playbackGeneration)
         {
             return;
         }


         var currentRenderer =
             _mpvRenderer;


         if (currentRenderer is null)
         {
             return;
         }


         bool hasAudio =
             currentRenderer
                 .HasAudioTrack();


         bool hasVideo =
             currentRenderer
                 .HasVideoTrack();


         //
         // An FFmpeg visualizer deliberately generates a video track for an
         // otherwise audio-only stream. Treat that generated output as
         // audio-only media rather than mistaking it for ordinary video and
         // clearing the visualizer graph.
         //
         bool audioOnly =
    expectedAudioOnly ||
    hasAudio &&
    (!hasVideo ||
     currentRenderer.AudioSpectrumEnabled);


         IsAudioOnly =
      audioOnly;

         if (audioOnly)
         {
             if (currentRenderer.AudioSpectrumEnabled)
             {
                 //
                 // The FFmpeg graph was installed before loadfile. Its
                 // generated frames should be displayed through the normal
                 // video texture rather than the Classic Bars shader.
                 //
                 _screenPainter.SetAudioOnly(
                     false);
             }
             else
             {
                 //
                 // Classic Bars does not generate video frames, so enable
                 // ScreenPainter's lightweight audio-only shader. This also
                 // provides the fallback if mpv rejected a prepared graph.
                 //
                 SetAudioVisualizerMode(
                     _audioVisualizerMode,
                     _audioVisualizerTheme);
             }
         }
         else
         {
             currentRenderer.SetAudioSpectrumEnabled(
                 false);

             _screenPainter.SetAudioOnly(
                 false);
         }


         if (audioOnly)
         {
             Interlocked.Exchange(
                 ref playbackStarted,
                 1);

             _screenPainter
                 .SetLoading(
                     false);

             ReportYouTubeRouteSucceeded();
         }


         //
         // =========================================================
         // RESOLVER RETRY SUCCESS
         // =========================================================
         //
         // TryWebResolverFallbackAsync() deliberately leaves
         // _webResolverFallbackRunning=true after sending the
         // resolved URL to the existing MPV renderer.
         //
         // FILE_LOADED is MPV confirming that the resolved second
         // attempt actually loaded.
         //
         // At this point VideoPlayer can safely resume treating any
         // future MPV failure as a real/final playback failure.
         //

         if (_webResolverFallbackRunning)
         {
             AepLog.Info(
                 "[WebResolver] Resolved retry reached FILE_LOADED successfully.");


             _webResolverFallbackRunning =
                 false;


             LastError =
                 null;
         }
     };

                            //
                            // First actual video frame = successful video
                            // playback.
                            //

                            renderer.OnFrameRendered =
                                () =>
                                {
                                    if (playbackGeneration !=
                                        _playbackGeneration)
                                    {
                                        return;
                                    }


                                    Interlocked.Exchange(
    ref playbackStarted,
    1);

                                    _screenPainter
                                        .SetLoading(
                                            false);

                                    ReportYouTubeRouteSucceeded();
                                };
                        }


                        //
                        // =================================================
                        // Existing renderer
                        // =================================================
                        //

                        var existingRenderer =
                   _mpvRenderer;

                        if (existingRenderer is not null)
                        {
                            if (playbackGeneration !=
                                _playbackGeneration)
                            {
                                AepLog.Debug(
                                    "[MPV] Ignoring stale request before existing-renderer reuse.");

                                return;
                            }

                            ConfigureRendererCallbacks(
                                existingRenderer);

                            existingRenderer.SetMuted(
                                _pendingOutputMuted);

                            if (useForcedYouTubeCookies)
                            {
                                existingRenderer.SetYouTubeCookiesPath(
                                    CookiesPath);
                            }

                            existingRenderer.Play(
       url,
       playbackPosition,
       isPlaying,
       useYouTubePoTokens,
       useYouTubeCookies: useForcedYouTubeCookies,
       preparedVisualizerMode:
           _audioVisualizerMode !=
               AudioVisualizerMode.ClassicBars
               ? _audioVisualizerMode
               : null,
       preparedVisualizerTheme:
           _audioVisualizerTheme,
       expectedAudioOnly:
           expectedAudioOnly);

                            _isActive =
                                true;

                            _screenPainter.SetTransform(
                                ScreenPosition,
                                ScreenYaw,
                                ScreenWidthScale,
                                ScreenHeightScale);

                            return;
                        }


                        //
                        // =================================================
                        // New renderer
                        // =================================================
                        //

                        AepLog.Info(
         $"[MPV] Creating renderer for: {url}");


                        var ownedRenderer =
                           new MpvRenderer();

                        //
                        // Check again after construction. Another PlayVideo
                        // request may have become current while this task was
                        // being scheduled.
                        //
                        if (playbackGeneration !=
                            _playbackGeneration)
                        {
                            ownedRenderer.Dispose();

                            AepLog.Debug(
                                "[MPV] Disposed stale renderer before initialization.");

                            return;
                        }

                        _mpvRenderer =
                            ownedRenderer;

                        AepLog.Info(
                            "[MPV] Renderer object created.");

                        ConfigureRendererCallbacks(
                            ownedRenderer);

                        AepLog.Info(
                            "[MPV] Renderer callbacks configured.");

                        AepLog.Info(
                            $"[MPV] Initializing renderer. " +
                            $"Generation={playbackGeneration}, " +
                            $"CancellationRequested={renderCancellation.IsCancellationRequested}");

                        ownedRenderer.Initialize(
                            ScreenWidth,
                            ScreenHeight,
                            _screenTexture,
                            renderCancellation.Token,
                            HardwareDecoding,
                            MaxQualityHeight,
                            AllowInsecureDirectUrls,
                            _pendingVolume,
                            CookiesPath);

                        ownedRenderer.SetMuted(
                            _pendingOutputMuted);

                        if (playbackGeneration !=
                            _playbackGeneration)
                        {
                            ownedRenderer.Stop();
                            ownedRenderer.Dispose();

                            if (ReferenceEquals(
                                    _mpvRenderer,
                                    ownedRenderer))
                            {
                                _mpvRenderer =
                                    null;
                            }

                            AepLog.Debug(
                                "[MPV] Disposed stale renderer after initialization.");

                            return;
                        }

                        AepLog.Info(
                            "[MPV] Renderer initialized successfully.");

                        AepLog.Info(
                            $"[MPV] Sending Play command for: {url}");

                        ownedRenderer.Play(
     url,
     playbackPosition,
     isPlaying,
     useYouTubePoTokens,
     useYouTubeCookies: useForcedYouTubeCookies,
     preparedVisualizerMode:
         _audioVisualizerMode !=
             AudioVisualizerMode.ClassicBars
             ? _audioVisualizerMode
             : null,
     preparedVisualizerTheme:
         _audioVisualizerTheme,
     expectedAudioOnly:
         expectedAudioOnly);

                        AepLog.Info(
                            "[MPV] Play command returned successfully.");

                        _isActive =
                            true;

                        _screenPainter.SetTransform(
                            ScreenPosition,
                            ScreenYaw,
                            ScreenWidthScale,
                            ScreenHeightScale);

                        AepLog.Info(
                            $"[MPV] Entering render loop for generation {playbackGeneration}.");

                        //
                        // This task renders only the renderer it created.
                        //
                        // The same renderer is deliberately reused when the
                        // user changes from one video to another. A new URL
                        // changes playbackGeneration and replaces the renderer
                        // callbacks, but this existing render loop must remain
                        // alive to display frames from the new video.
                        //
                        while (!_stopRequested &&
                               ownedRenderer.RenderFrame())
                        {
                        }

                        AepLog.Info(
                            $"[MPV] Render loop exited for generation {playbackGeneration}.");

                        if (playbackGeneration !=
       _playbackGeneration)
                        {
                            AepLog.Debug(
                                "[MPV] Stale render loop exited after playback ownership changed.");

                            return;
                        }

                        _isActive =
                            false;

                        //
                        // A naturally completed local file must release exclusive ownership.
                        //
                        // Previously this flag was only cleared by StopVideo(). That left the
                        // completed local file blocking every later playback attempt until the
                        // plugin was restarted.
                        //

                        if (isLocalVideo)
                        {
                            _isPlayingLocalVideo =
                                false;

                            StopLocalVideoBroadcast();

                            AepLog.Info(
                                "[LocalVideo] Local playback ended and released TV ownership.");
                        }

                        _screenPainter.SetLoading(
                            false);

                        _screenPainter.SetTarget(
                            null);

                        if (ReferenceEquals(
                                _mpvRenderer,
                                ownedRenderer))
                        {
                            _mpvRenderer =
                                null;
                        }

                        try
                        {
                            ownedRenderer.Dispose();
                        }
                        catch (Exception exception)
                        {
                            AepLog.Warning(
                                $"[MPV] Failed to dispose renderer after video end: {exception.Message}");
                        }

                        CompleteRenderCancellation(
                            renderCancellation);
                    }
                    catch (Exception exception)
                    {
                        //
                        // Ignore exceptions from a playback generation that
                        // has already been replaced.
                        //

                        if (playbackGeneration !=
                            _playbackGeneration)
                        {
                            AepLog.Debug(
                                $"[MPV] Ignoring stale renderer exception after playback changed: {exception.Message}");

                            return;
                        }


                        AepLog.Error(
                            $"[MPV] Generic error: " +
                            $"{exception.Message} " +
                            $"{exception.StackTrace}");


                        LastError =
                            exception.Message;


                        _rendererFailed =
                            true;


                        _isActive =
                            false;


                        IsAudioOnly =
                            false;


                        _screenPainter
                            .SetAudioOnly(
                                false);


                        _screenPainter
                            .SetLoading(
                                false);


                        _screenPainter
                            .SetTarget(
                                null);


                        //
                        // Initialization-level exceptions happen outside the
                        // MPV END_FILE event system, so they also get one
                        // resolver attempt.
                        //

                        StartWebResolverFallback(
                            exception.Message);
                    }
                });
    }


    private async Task TryWebResolverFallbackAsync(
    string originalUrl,
    int playbackPosition,
    bool isPlaying,
    int failedPlaybackGeneration,
    string originalFailure,
    bool useAuthenticatedYouTubeSession)
    {
        if (_disposing ||
            failedPlaybackGeneration !=
            _playbackGeneration)
        {
            return;
        }


        _webResolverFallbackRunning =
            true;


        //
        // The original MPV failure is not final while the resolver is
        // working.
        //

        LastError =
            null;


        try
        {
            AepLog.Info(
      $"[WebResolver] Resolving fallback URL: {originalUrl}");

            var usePoTokens =
                false;

            if (IsYTURL(
                    originalUrl))
            {
                if (useAuthenticatedYouTubeSession &&
                    !string.IsNullOrWhiteSpace(CookiesPath) &&
                    YouTubeEmbeddedBrowserSession.LooksUsable(CookiesPath) &&
                    YouTubePoTokenSupport.IsAvailable(Resources))
                {
                    await Resources
                        .YouTubePolicy
                        .EnsureProviderReadyAsync()
                        .ConfigureAwait(false);

                    usePoTokens =
                        true;
                }
                else
                if (YouTubePlaybackPolicy.IsBotCheckError(
                        originalFailure))
                {
                    //
                    // Android encountered a recognisable verification
                    // restriction. Enable compatibility mode and wait
                    // for the local provider before retrying.
                    //
                    await Resources
                        .YouTubePolicy
                        .EnterCompatibilityModeAsync()
                        .ConfigureAwait(false);

                    usePoTokens =
                        true;
                }
                else if (Resources
                    .YouTubePolicy
                    .CompatibilityModeActive)
                {
                    //
                    // A previous verification failure already enabled
                    // compatibility mode.
                    //
                    await Resources
                        .YouTubePolicy
                        .EnsureProviderReadyAsync()
                        .ConfigureAwait(false);

                    usePoTokens =
                        true;
                }
            }

            var result =
                await WebMediaUrlResolver
                    .ResolveAsync(
                        Resources,
                        originalUrl,
                        _lifetimeCancellation.Token,
                        usePoTokens,
                        useAuthenticatedYouTubeSession
                            ? CookiesPath
                            : null)
                    .ConfigureAwait(false);


            if (_disposing ||
                failedPlaybackGeneration !=
                _playbackGeneration)
            {
                AepLog.Debug(
                    "[WebResolver] Ignoring fallback result because playback changed.");

                _webResolverFallbackRunning =
                    false;

                return;
            }


            var resolvedUrl =
                result.Url;


            if (string.IsNullOrWhiteSpace(
                    resolvedUrl))
            {
                var resolverError =
                    string.IsNullOrWhiteSpace(
                        result.Error)
                        ? "No playable video URL was found."
                        : result.Error;


                _webResolverFallbackRunning =
                    false;


                LastError =
                    resolverError;


                _rendererFailed =
                    true;


                AepLog.Warning(
                    $"[WebResolver] Automatic fallback failed: {resolverError}");


                try
                {
                    _mpvRenderer?.Stop();
                }
                catch (Exception exception)
                {
                    AepLog.Warning(
                        $"[WebResolver] Failed to stop renderer after resolver failure: {exception.Message}");
                }


                return;
            }


            if (string.Equals(
                    resolvedUrl,
                    originalUrl,
                    StringComparison.OrdinalIgnoreCase))
            {
                _webResolverFallbackRunning =
                    false;


                LastError =
                    originalFailure;


                _rendererFailed =
                    true;


                AepLog.Warning(
                    "[WebResolver] Resolver returned the same URL that already failed. No retry performed.");


                try
                {
                    _mpvRenderer?.Stop();
                }
                catch (Exception exception)
                {
                    AepLog.Warning(
                        $"[WebResolver] Failed to stop renderer after unusable resolver result: {exception.Message}");
                }


                return;
            }


            var renderer =
                _mpvRenderer;


            if (renderer is null)
            {
                _webResolverFallbackRunning =
                    false;


                _rendererFailed =
                    true;


                LastError =
                    "The MPV renderer disappeared while the fallback URL was being resolved.";


                AepLog.Warning(
                    $"[WebResolver] {LastError}");


                return;
            }


            AepLog.Info(
                $"[WebResolver] Automatic fallback resolved using " +
                $"{result.Method}: {resolvedUrl}");


            //
            // =========================================================
            // RETRY ON THE EXISTING MPV INSTANCE
            // =========================================================
            //
            // Do NOT:
            //
            //   await _renderTask
            //   ResetFailedRenderer()
            //   Dispose()
            //   PlayVideo(...)
            //
            // The renderer and its D3D render context stay alive.
            //
            // MpvRenderer.Play() performs a loadfile/replace on the same
            // mpv instance. Its existing RenderFrame loop is still waiting
            // and will naturally wake when this media produces a frame.
            //

            _rendererFailed =
                false;


            LastError =
                null;


            _isActive =
                true;


            IsAudioOnly =
                false;


            _screenPainter.SetAudioOnly(
                false);


            _screenPainter.SetLoading(
                true);


            _screenPainter.SetTransform(
                ScreenPosition,
                ScreenYaw,
                ScreenWidthScale,
                ScreenHeightScale);


            AepLog.Info(
                $"[WebResolver] Retrying on existing MPV renderer: {resolvedUrl}");


            renderer.Play(
       resolvedUrl,
       playbackPosition,
       isPlaying,
       preparedVisualizerMode:
           _audioVisualizerMode !=
               AudioVisualizerMode.ClassicBars
               ? _audioVisualizerMode
               : null,
       preparedVisualizerTheme:
           _audioVisualizerTheme);


            AepLog.Info(
                "[WebResolver] Existing MPV renderer accepted resolved retry.");


            //
            // IMPORTANT:
            //
            // Do NOT set _webResolverFallbackRunning=false here.
            //
            // Play() only queues MPV's loadfile command. It does not mean
            // FILE_LOADED has happened yet.
            //
            // OnMediaLoaded will clear the flag when MPV confirms that the
            // resolved media really loaded.
            //
        }

        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
            _webResolverFallbackRunning =
                false;
        }
        catch (Exception exception)
        {
            if (failedPlaybackGeneration !=
                _playbackGeneration)
            {
                return;
            }


            _webResolverFallbackRunning =
                false;


            _rendererFailed =
                true;


            LastError =
                exception.Message;


            AepLog.Warning(
                $"[WebResolver] Automatic fallback failed: {exception}");


            try
            {
                _mpvRenderer?.Stop();
            }
            catch (Exception stopException)
            {
                AepLog.Warning(
                    $"[WebResolver] Failed to stop renderer after resolver exception: {stopException.Message}");
            }
        }
    }

    internal void Pause(bool pause)
    {
        //
        // Still images have no running playback clock to pause.
        //
        if (_isPlayingImage)
        {
            if (pause ==
                _imagePlaybackPaused)
            {
                return;
            }

            if (pause)
            {
                _imageElapsedBeforeClockStart =
                    GetImageElapsedSeconds();

                _imagePlaybackPaused =
                    true;
            }
            else
            {
                _imageClockStartedUtc =
                    DateTime.UtcNow;

                _imagePlaybackPaused =
                    false;
            }

            return;
        }

        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            _mpvRenderer?.Pause(
                pause);
        }
    }

    internal bool GetIdle()
    {
        //
        // A still image remains active until explicitly replaced or stopped.
        //
        if (_isPlayingImage)
        {
            if (_currentImageSelection is
                {
                    Mode: ImageMediaMode.Slideshow,
                    Loop: false
                } slideshow)
            {
                var duration =
                    slideshow.SecondsPerImage *
                    slideshow.ImageUrls.Count;

                return GetImageElapsedSeconds() >=
                       duration;
            }

            return false;
        }

        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            return _mpvRenderer?.IsEofReached() ??
                   true;
        }

        return true;
    }

    internal bool GetPaused()
    {
        if (_isPlayingImage)
        {
            return _imagePlaybackPaused;
        }

        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            return _mpvRenderer?.GetPaused() ??
                   false;
        }

        return false;
    }

    internal double[] GetInfo()
    {
        if (_isPlayingImage)
        {
            var elapsed =
                GetImageElapsedSeconds();

            var duration =
                _currentImageSelection is
                {
                    Mode: ImageMediaMode.Slideshow,
                    Loop: false
                } slideshow
                    ? slideshow.SecondsPerImage *
                      slideshow.ImageUrls.Count
                    : 0d;

            return
            [
                elapsed,
                duration,
                _pendingVolume,
                ScreenWidth,
                ScreenHeight
            ];
        }

        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            return _mpvRenderer?.GetProperties() ??
                   [0, 0, 0, 0, 0];
        }

        return [0, 0, 0, 0, 0];
    }

    internal void Seek(int seconds)
    {
        if (_isPlayingImage)
        {
            //
            // Still images have no timeline.
            //
            if (_currentImageSelection is not
                {
                    Mode: ImageMediaMode.Slideshow
                } slideshow)
            {
                return;
            }

            var requestedSeconds =
                Math.Max(
                    0d,
                    seconds);

            //
            // Do not seek a non-looping slideshow beyond its natural end.
            // Looping slideshows may use an unrestricted elapsed time because
            // RunSlideshowAsync converts it to a slide using modulo arithmetic.
            //
            if (!slideshow.Loop)
            {
                var duration =
                    slideshow.SecondsPerImage *
                    slideshow.ImageUrls.Count;

                requestedSeconds =
                    Math.Min(
                        requestedSeconds,
                        duration);
            }

            _imageElapsedBeforeClockStart =
                requestedSeconds;

            _imageClockStartedUtc =
                DateTime.UtcNow;

            return;
        }

        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            _mpvRenderer?.Seek(
                seconds);
        }
    }

    internal void SetAudioVisualizerMode(
        AudioVisualizerMode mode,
        AudioVisualizerTheme theme)
    {
        _audioVisualizerMode =
            mode;

        _audioVisualizerTheme =
    theme;

        //
        // Remember the selection even before MPV confirms that the media
        // is audio-only. It will be applied after FILE_LOADED.
        //
        if (!IsAudioOnly ||
            _mpvRenderer is not { } renderer)
        {
            return;
        }

        if (mode == AudioVisualizerMode.ClassicBars)
        {
            renderer.SetAudioSpectrumEnabled(
                false);

            _screenPainter.SetAudioOnly(
                true);



            return;
        }

        var ffmpegEnabled =
renderer.SetAudioSpectrumEnabled(
    true,
    mode,
    theme);

        //
        // If FFmpeg rejects the graph, leave the original visualizer active
        // as a safe fallback.
        //
        _screenPainter.SetAudioOnly(
            !ffmpegEnabled);

        AepLog.Info(
            ffmpegEnabled
                ? $"[AudioVisualizer] Switched to {AudioVisualizerSelection.GetDisplayName(mode)}."
                : "[AudioVisualizer] FFmpeg graph failed; using Classic Bars.");
    }

    internal void SetVolume(int vol)
    {
        vol = Math.Clamp(
            vol,
            0,
            200);

        // DJ host monitoring uses mpv's output mute so its decoded audio still
        // drives the visualizer. Calls which represent the already-muted UI
        // must not replace that signal with zero-volume samples.
        if (_pendingOutputMuted && vol == 0)
        {
            return;
        }

        if (vol > 0 && _pendingOutputMuted)
        {
            _pendingOutputMuted = false;
            _mpvRenderer?.SetMuted(false);
        }

        _pendingVolume =
            vol;

        if (_isPlayingSnes)
        {
            _snesRenderer?.SetVolume(
                vol);

            return;
        }

        if (_isPlayingGameBoy)
        {
            _gameBoyRenderer?.SetVolume(
                vol);

            return;
        }

        if (_isPlayingNes)
        {
            _nesRenderer?.SetVolume(vol);
            return;
        }

        if (_isPlayingGameBoyAdvance)
        {
            _gameBoyAdvanceRenderer?.SetVolume(vol);
            return;
        }

        if (_isPlayingMasterSystem)
        {
            _masterSystemRenderer?.SetVolume(vol);
            return;
        }

        if (_isPlayingGameGear)
        {
            _gameGearRenderer?.SetVolume(vol);
            return;
        }

        if (_isPlayingBrowser)
        {
            _browserRenderer?.SetVolume(vol);
            return;
        }

        if (!_renderCancellation.Token
                        .IsCancellationRequested)
        {
            _mpvRenderer?.SetVolume(
                vol);
        }
    }

    internal void SetOutputMuted(bool muted)
    {
        _pendingOutputMuted = muted;
        _mpvRenderer?.SetMuted(muted);
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

    private void RecoverNativePlaybackFailure(string source, string message, Action releaseAndDispose)
    {
        if (_disposing) return;

        var safeMessage = string.IsNullOrWhiteSpace(message)
            ? $"The {source} component stopped unexpectedly."
            : message;

        AepLog.Error($"[NATIVE-RECOVERY] {source}: {safeMessage}");

        try
        {
            releaseAndDispose();
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[NATIVE-RECOVERY] {source} cleanup was incomplete: {exception.Message}");
        }

        _playbackGeneration++;
        _isActive = false;
        IsAudioOnly = false;
        _lastIdle = true;
        _screenPainter.SetAudioOnly(false);
        _screenPainter.SetAudioLevel(0f);
        _screenPainter.SetLoading(false);
        _screenPainter.SetTarget(null);
        LastError = safeMessage;

        Plugin.ChatGui.Print($"[AlphaChannel] {safeMessage} You can start something else without reloading the plugin.");
    }

    private void CheckBroadcastFailures()
    {
        BroadcastDiagnosticsSnapshot? failed = null;
        Action? stop = null;

        void Consider(BroadcastDiagnosticsSnapshot snapshot, Action stopAction)
        {
            if (failed is null && snapshot.Health == BroadcastHealth.Failed &&
                snapshot.UpdatedUtc > _lastHandledBroadcastFailureUtc)
            {
                failed = snapshot;
                stop = stopAction;
            }
        }

        if (_isPlayingBrowser && _browserRenderer is not null)
            Consider(_browserRenderer.BroadcastDiagnostics, _browserRenderer.StopBroadcast);
        if (_isPlayingSnes && _snesRenderer is not null)
            Consider(_snesRenderer.BroadcastDiagnostics, _snesRenderer.StopBroadcast);
        if (_isPlayingGameBoy && _gameBoyRenderer is not null)
            Consider(_gameBoyRenderer.BroadcastDiagnostics, _gameBoyRenderer.StopBroadcast);
        if (_isPlayingNes && _nesRenderer is not null)
            Consider(_nesRenderer.BroadcastDiagnostics, _nesRenderer.StopBroadcast);
        if (_isPlayingGameBoyAdvance && _gameBoyAdvanceRenderer is not null)
            Consider(_gameBoyAdvanceRenderer.BroadcastDiagnostics, _gameBoyAdvanceRenderer.StopBroadcast);
        if (_isPlayingMasterSystem && _masterSystemRenderer is not null)
            Consider(_masterSystemRenderer.BroadcastDiagnostics, _masterSystemRenderer.StopBroadcast);
        if (_isPlayingGameGear && _gameGearRenderer is not null)
            Consider(_gameGearRenderer.BroadcastDiagnostics, _gameGearRenderer.StopBroadcast);
        if (_isPlayingLocalVideo)
            Consider(_localVideoBroadcastEncoder.Diagnostics, _localVideoBroadcastEncoder.Stop);

        if (failed is null) return;

        _lastHandledBroadcastFailureUtc = failed.UpdatedUtc;
        _lastBroadcastDiagnostics = failed;
        try { stop?.Invoke(); }
        catch (Exception exception)
        {
            AepLog.Warning($"[BROADCAST-RECOVERY] Failed cleanup: {exception.Message}");
        }

        AepLog.Warning($"[BROADCAST-RECOVERY] {failed.Source} upload stopped; local playback remains active.");
        Plugin.ChatGui.Print($"[AlphaChannel] The {failed.Source} broadcast encoder stopped unexpectedly. Local playback is still running.");
    }

    internal void OnFrameworkUpdate()
    {
        if (_rendererFailed && !_webResolverFallbackRunning && _renderTask?.IsCompleted != false)
        {
            // Dispose a renderer that failed during initialization or outside MPV's
            // normal END_FILE cleanup. LastError remains available to VideoPlayer/UI.
            ResetFailedRenderer();
        }

        CheckBroadcastFailures();

        if (_isPlayingSnes)
        {
            var renderer = _snesRenderer;
            if (renderer?.HasFailed == true)
            {
                RecoverNativePlaybackFailure("Super Nintendo emulator", renderer.FailureMessage ?? string.Empty, () =>
                {
                    SetSnesControlsEnabled(false);
                    _isPlayingSnes = false;
                    _snesRenderer = null;
                    renderer.Dispose();
                });
                return;
            }

            try
            {
                UpdateSnesInput();
                renderer?.OnFrameworkUpdate();
            }
            catch (Exception exception)
            {
                RecoverNativePlaybackFailure("Super Nintendo emulator",
                    "The Super Nintendo emulator stopped unexpectedly.", () =>
                    {
                        SetSnesControlsEnabled(false);
                        _isPlayingSnes = false;
                        _snesRenderer = null;
                        renderer?.Dispose();
                    });
                AepLog.Error($"[SNES9X] Framework update failed: {exception}");
                return;
            }

            _lastIdle = false;

            return;
        }

        if (_isPlayingGameBoy)
        {
            var renderer = _gameBoyRenderer;
            if (renderer?.HasFailed == true)
            {
                RecoverFailedGambatte(renderer, "Game Boy emulator", renderer.FailureMessage);
                return;
            }

            try { UpdateGameBoyInput(); renderer?.OnFrameworkUpdate(); }
            catch (Exception exception)
            {
                AepLog.Error($"[GAMBATTE] Framework update failed: {exception}");
                RecoverFailedGambatte(renderer, "Game Boy emulator", null);
                return;
            }

            _lastIdle = false;

            return;
        }


        if (_isPlayingNes)
        {
            var renderer = _nesRenderer;
            if (renderer?.HasFailed == true)
            {
                RecoverFailedGambatte(renderer, "NES emulator", renderer.FailureMessage);
                return;
            }
            try { UpdateGameBoyInput(); renderer?.OnFrameworkUpdate(); }
            catch (Exception exception)
            {
                AepLog.Error($"[NESTOPIA] Framework update failed: {exception}");
                RecoverFailedGambatte(renderer, "NES emulator", null);
                return;
            }
            _lastIdle = false;
            return;
        }

        if (_isPlayingGameBoyAdvance)
        {
            var renderer = _gameBoyAdvanceRenderer;
            if (renderer?.HasFailed == true)
            {
                RecoverFailedGambatte(renderer, "Game Boy Advance emulator", renderer.FailureMessage);
                return;
            }
            try { UpdateGameBoyInput(); renderer?.OnFrameworkUpdate(); }
            catch (Exception exception)
            {
                AepLog.Error($"[MGBA] Framework update failed: {exception}");
                RecoverFailedGambatte(renderer, "Game Boy Advance emulator", null);
                return;
            }
            _lastIdle = false;
            return;
        }

        if (_isPlayingMasterSystem)
        {
            var renderer = _masterSystemRenderer;
            if (renderer?.HasFailed == true)
            {
                RecoverFailedGambatte(renderer, "Master System / SG-1000 emulator", renderer.FailureMessage);
                return;
            }
            try { UpdateGameBoyInput(); renderer?.OnFrameworkUpdate(); }
            catch (Exception exception)
            {
                AepLog.Error($"[GEARSYSTEM] Framework update failed: {exception}");
                RecoverFailedGambatte(renderer, "Master System / SG-1000 emulator", null);
                return;
            }
            _lastIdle = false;
            return;
        }

        if (_isPlayingGameGear)
        {
            var renderer = _gameGearRenderer;
            if (renderer?.HasFailed == true)
            {
                RecoverFailedGambatte(renderer, "Game Gear emulator", renderer.FailureMessage);
                return;
            }
            try { UpdateGameBoyInput(); renderer?.OnFrameworkUpdate(); }
            catch (Exception exception)
            {
                AepLog.Error($"[GEARSYSTEM] Game Gear framework update failed: {exception}");
                RecoverFailedGambatte(renderer, "Game Gear emulator", null);
                return;
            }
            _lastIdle = false;
            return;
        }

        if (_isPlayingBrowser)
        {
            var renderer = _browserRenderer;
            if (renderer?.HasFailed != false)
            {
                RecoverNativePlaybackFailure("browser", renderer?.FailureMessage ?? "The browser process stopped unexpectedly.", () =>
                {
                    SetBrowserControlsEnabled(false);
                    _isPlayingBrowser = false;
                    _browserRenderer = null;
                    renderer?.Dispose();
                });
                return;
            }

            if (BrowserControlsEnabled)
            {
                if (TryForceFfxivControl()) return;
                SuppressAllFfxivKeyboardInput();
            }
            try { renderer.OnFrameworkUpdate(); }
            catch (Exception exception)
            {
                AepLog.Error($"[BROWSER] Framework update failed: {exception}");
                RecoverNativePlaybackFailure("browser", "The browser process stopped unexpectedly.", () =>
                {
                    SetBrowserControlsEnabled(false);
                    _isPlayingBrowser = false;
                    _browserRenderer = null;
                    renderer.Dispose();
                });
                return;
            }
            _lastIdle = false;
            return;
        }

        //
        // Update the audio visualizer at roughly 30 Hz.
        //
        // There is no reason to query mpv 60+ times per second;
        // the shader itself still renders every frame using the
        // most recently measured value.
        //
        if (IsAudioOnly &&
     _mpvRenderer is not null &&
     !_mpvRenderer.AudioSpectrumEnabled &&
             (DateTime.UtcNow -
             _lastAudioLevelUpdate)
                .TotalMilliseconds >= 33)
        {
            _lastAudioLevelUpdate =
                DateTime.UtcNow;

            _screenPainter.SetAudioLevel(
                _mpvRenderer.GetAudioLevel());
        }


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

    private void RecoverFailedGambatte(GambatteRenderer? renderer, string source, string? message)
    {
        RecoverNativePlaybackFailure(source,
            message ?? $"The {source} stopped unexpectedly.", () =>
            {
                SetGameBoyControlsEnabled(false);
                if (ReferenceEquals(renderer, _gameBoyRenderer))
                {
                    _isPlayingGameBoy = false;
                    _gameBoyRenderer = null;
                }
                if (ReferenceEquals(renderer, _nesRenderer))
                {
                    _isPlayingNes = false;
                    _nesRenderer = null;
                }
                if (ReferenceEquals(renderer, _gameBoyAdvanceRenderer))
                {
                    _isPlayingGameBoyAdvance = false;
                    _gameBoyAdvanceRenderer = null;
                }
                if (ReferenceEquals(renderer, _masterSystemRenderer))
                {
                    _isPlayingMasterSystem = false;
                    _masterSystemRenderer = null;
                }
                if (ReferenceEquals(renderer, _gameGearRenderer))
                {
                    _isPlayingGameGear = false;
                    _gameGearRenderer = null;
                }
                renderer?.Dispose();
            });
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
    //
    // Compatibility overload for existing callers and old presets.
    //
    internal void SetScreenTransform(
        Vector3 position,
        float yaw,
        float scale)
    {
        SetScreenTransform(
            position,
            yaw,
            disableFixedScaleRatio: false,
            widthScale: scale,
            heightScale: scale);
    }

    internal void SetScreenTransform(
        Vector3 position,
        float yaw,
        bool disableFixedScaleRatio,
        float widthScale,
        float heightScale)
    {
        var useIndependentScale =
            IndependentScreenScalingEnabled &&
            disableFixedScaleRatio;

        ScreenPosition =
            position;

        ScreenYaw =
            yaw;

        DisableFixedScreenScaleRatio =
            useIndependentScale;

        ScreenWidthScale =
            Math.Clamp(
                widthScale,
                MinScreenScale,
                MaxScreenScale);

        ScreenHeightScale =
            useIndependentScale
                ? Math.Clamp(
                    heightScale,
                    MinScreenScale,
                    MaxScreenScale)
                : ScreenWidthScale;

        //
        // Preserve a meaningful legacy scale for older clients.
        //
        ScreenScale =
            ScreenWidthScale;

        if (_isActive)
        {
            _screenPainter.SetTransform(
                ScreenPosition,
                ScreenYaw,
                ScreenWidthScale,
                ScreenHeightScale);
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
        _screenPresets.Add(
            new ScreenPositionPreset
            {
                Name =
                    name,

                X =
                    ScreenPosition.X,

                Y =
                    ScreenPosition.Y,

                Z =
                    ScreenPosition.Z,

                Yaw =
                    ScreenYaw,

                Scale =
                    ScreenScale,

                DisableFixedScaleRatio =
                    DisableFixedScreenScaleRatio,

                WidthScale =
                    ScreenWidthScale,

                HeightScale =
                    ScreenHeightScale,
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

    internal void ApplyScreenPreset(
        ScreenPositionPreset preset)
    {
        var position =
            new Vector3(
                preset.X,
                preset.Y,
                preset.Z);

        ScreenSpawnAnchor =
            position;

        var widthScale =
            preset.WidthScale ??
            preset.Scale;

        var heightScale =
            preset.HeightScale ??
            preset.Scale;

        SetScreenTransform(
            position,
            preset.Yaw,
            preset.DisableFixedScaleRatio,
            widthScale,
            heightScale);
    }

    // Applied when watching someone else's AetherStream over StreamClient and their host client
    // publishes a screen transform (see StreamClient.PublishStateAsync/StreamControl's
    // ScreenX/Y/Z/Yaw/Scale). There is no shared/networked 3D object - this just makes the local
    // ScreenPainter draw at the same coordinates the host is using, same as any other placement.
    internal void ApplyRemoteScreenTransform(
        Vector3 position,
        float yaw,
        float legacyScale,
        bool disableFixedScaleRatio,
        float? widthScale,
        float? heightScale)
    {
        ScreenSpawnAnchor =
            position;

        SetScreenTransform(
            position,
            yaw,
            disableFixedScaleRatio,
            widthScale ??
            legacyScale,
            heightScale ??
            legacyScale);
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
        static void Cleanup(
            string name,
            Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                AepLog.Warning(
                    $"[Shutdown] {name} cleanup failed: {exception.Message}");
            }
        }

        _disposing = true;
        _stopRequested = true;
        _isActive = false;

        _lifetimeCancellation.Cancel();

        Cleanup("SNES controls", () => SetSnesControlsEnabled(false));
        Cleanup("Game Boy controls", () => SetGameBoyControlsEnabled(false));
        Cleanup("browser controls", () => SetBrowserControlsEnabled(false));
        SetBlockAllFfxivKeyboardInput(false);

        CancellationTokenSource renderCancellation;
        lock (_renderCancellationLock)
        {
            renderCancellation = _renderCancellation;
        }

        try { renderCancellation.Cancel(); }
        catch (ObjectDisposedException) { }

        try
        {
            _mpvRenderer?.Stop();
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[MPV] Failed to stop renderer during shutdown: {exception.Message}");
        }

        try
        {
            if (_renderTask is not null &&
                !_renderTask.IsCompleted)
            {
                _renderTask.Wait(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[MPV] Failed waiting for render task during shutdown: {exception.Message}");
        }

        try
        {
            if (_webResolverFallbackTask is { IsCompleted: false } &&
                !_webResolverFallbackTask.Wait(TimeSpan.FromSeconds(3)))
            {
                AepLog.Warning(
                    "[WebResolver] Fallback worker did not stop within 3 seconds.");
            }
        }
        catch (Exception exception)
        {
            AepLog.Debug(
                $"[WebResolver] Fallback cleanup warning: {exception.Message}");
        }

        try
        {
            _mpvRenderer?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // The render task already disposed it.
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[MPV] Failed renderer dispose during shutdown: {exception.Message}");
        }

        _mpvRenderer = null;

        try
        {
            _snesRenderer?.Dispose();
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[SNES9X] Failed renderer dispose during shutdown: {exception.Message}");
        }

        _snesRenderer = null;
        _isPlayingSnes = false;

        try { _gameBoyRenderer?.Dispose(); }
        catch (Exception exception) { AepLog.Warning($"[GAMBATTE] Failed renderer dispose during shutdown: {exception.Message}"); }
        _gameBoyRenderer = null;
        _isPlayingGameBoy = false;

        try { _nesRenderer?.Dispose(); }
        catch (Exception exception) { AepLog.Warning($"[NESTOPIA] Failed renderer dispose during shutdown: {exception.Message}"); }
        _nesRenderer = null;
        _isPlayingNes = false;

        try { _gameBoyAdvanceRenderer?.Dispose(); }
        catch (Exception exception) { AepLog.Warning($"[MGBA] Failed renderer dispose during shutdown: {exception.Message}"); }
        _gameBoyAdvanceRenderer = null;
        _isPlayingGameBoyAdvance = false;

        try { _masterSystemRenderer?.Dispose(); }
        catch (Exception exception) { AepLog.Warning($"[GEARSYSTEM] Failed renderer dispose during shutdown: {exception.Message}"); }
        _masterSystemRenderer = null;
        _isPlayingMasterSystem = false;

        try { _gameGearRenderer?.Dispose(); }
        catch (Exception exception) { AepLog.Warning($"[GEARSYSTEM] Failed Game Gear renderer dispose during shutdown: {exception.Message}"); }
        _gameGearRenderer = null;
        _isPlayingGameGear = false;

        try { _browserRenderer?.Dispose(); }
        catch (Exception exception) { AepLog.Warning($"[BROWSER] Failed renderer dispose during shutdown: {exception.Message}"); }
        _browserRenderer = null;
        _isPlayingBrowser = false;

        Cleanup("local video broadcaster", _localVideoBroadcastEncoder.Dispose);

        Cleanup("image playback", () => StopImagePlayback(waitForCompletion: true));
        Cleanup("image renderer", _imageRenderer.Dispose);

        Cleanup("screen painter", _screenPainter.Dispose);

        Cleanup("preview texture", _previewShaderResourceView.Dispose);
        Cleanup("image transition texture", _imageTransitionTexture.Dispose);
        Cleanup("screen texture", _screenTexture.Dispose);

        Cleanup("media resources", Resources.Dispose);

        lock (_renderCancellationLock)
        {
            try { _renderCancellation.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        Cleanup("playback lifetime token", _lifetimeCancellation.Dispose);

        _imageElapsedBeforeClockStart =
    0d;

        _imageClockStartedUtc =
            DateTime.MinValue;

        _imagePlaybackPaused =
            false;
    }
}
