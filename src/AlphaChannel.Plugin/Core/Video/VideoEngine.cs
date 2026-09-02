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

    private Snes9xRenderer? _snesRenderer;
    private bool _isPlayingSnes;

    private GambatteRenderer? _gameBoyRenderer;
    private bool _isPlayingGameBoy;

    private bool _isPlayingLocalVideo;

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
    private Task? _renderTask;
    private int _playbackGeneration;
    private volatile bool _disposing;

    private volatile bool _webResolverFallbackRunning;

    internal bool WebResolverFallbackRunning =>
        _webResolverFallbackRunning;

    private DateTime _lastAudioLevelUpdate =
    DateTime.MinValue;

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
    internal bool IsPlayingSnes => _isPlayingSnes;
    internal bool IsPlayingGameBoy => _isPlayingGameBoy;
    internal bool IsPlayingLocalVideo => _isPlayingLocalVideo;

    internal bool IsSnesBroadcasting =>
        _snesRenderer?.IsBroadcasting ==
        true;

    internal bool IsGameBoyBroadcasting =>
        _gameBoyRenderer?.IsBroadcasting ==
        true;

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
            _gameBoyRenderer is not null)
        {
            foreach (GambatteInput input in
                     Enum.GetValues<GambatteInput>())
            {
                _gameBoyRenderer.SetButton(
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


        Plugin.ChatGui.Print(
            "[AlphaChannel] Game controls released. Keyboard control returned to FFXIV.");


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
            Pad(GamepadButtons.DpadUp));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.DOWN,
            IsSnesKeyHeld(keyDown) ||
            Pad(GamepadButtons.DpadDown));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.LEFT,
            IsSnesKeyHeld(keyLeft) ||
            Pad(GamepadButtons.DpadLeft));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.RIGHT,
            IsSnesKeyHeld(keyRight) ||
            Pad(GamepadButtons.DpadRight));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.B,
            IsSnesKeyHeld(keyB) ||
            Pad(GamepadButtons.South));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.A,
            IsSnesKeyHeld(keyA) ||
            Pad(GamepadButtons.East));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.Y,
            IsSnesKeyHeld(keyY) ||
            Pad(GamepadButtons.West));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.X,
            IsSnesKeyHeld(keyX) ||
            Pad(GamepadButtons.North));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.L,
            IsSnesKeyHeld(keyL) ||
            Pad(GamepadButtons.L1));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.R,
            IsSnesKeyHeld(keyR) ||
            Pad(GamepadButtons.R1));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.START,
            IsSnesKeyHeld(keyStart) ||
            Pad(GamepadButtons.Start));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.SELECT,
            IsSnesKeyHeld(keySelect) ||
            Pad(GamepadButtons.Select));


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
        if (!_isPlayingGameBoy ||
            _gameBoyRenderer is null)
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

        var keyStart =
            (VirtualKey)cfg.SnesKeyStart;

        var keySelect =
            (VirtualKey)cfg.SnesKeySelect;


        //
        // D-pad
        //

        _gameBoyRenderer.SetButton(
            (int)GambatteInput.Up,
            IsSnesKeyHeld(
                keyUp) ||
            Pad(
                GamepadButtons.DpadUp));

        _gameBoyRenderer.SetButton(
            (int)GambatteInput.Down,
            IsSnesKeyHeld(
                keyDown) ||
            Pad(
                GamepadButtons.DpadDown));

        _gameBoyRenderer.SetButton(
            (int)GambatteInput.Left,
            IsSnesKeyHeld(
                keyLeft) ||
            Pad(
                GamepadButtons.DpadLeft));

        _gameBoyRenderer.SetButton(
            (int)GambatteInput.Right,
            IsSnesKeyHeld(
                keyRight) ||
            Pad(
                GamepadButtons.DpadRight));


        //
        // Face buttons
        //

        _gameBoyRenderer.SetButton(
            (int)GambatteInput.B,
            IsSnesKeyHeld(
                keyB) ||
            Pad(
                GamepadButtons.South));

        _gameBoyRenderer.SetButton(
            (int)GambatteInput.A,
            IsSnesKeyHeld(
                keyA) ||
            Pad(
                GamepadButtons.East));


        //
        // Start / Select
        //

        _gameBoyRenderer.SetButton(
            (int)GambatteInput.Start,
            IsSnesKeyHeld(
                keyStart) ||
            Pad(
                GamepadButtons.Start));

        _gameBoyRenderer.SetButton(
            (int)GambatteInput.Select,
            IsSnesKeyHeld(
                keySelect) ||
            Pad(
                GamepadButtons.Select));


        //
        // Prevent FFXIV from receiving the Game Boy
        // keyboard controls while emulator input is active.
        //

        VirtualKey[] gameBoyKeys =
 [
     keyUp,
    keyDown,
    keyLeft,
    keyRight,
    keyA,
    keyB,
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
            ScreenScale);
    }

    internal nint PreviewTextureHandle =>
    _previewShaderResourceView.NativePointer;

    // Set only from PlayVideo's background task on a genuine init/decode failure (e.g. mpv/yt-dlp
    // never downloaded, so mpv_create() throws DllNotFoundException) - VideoPlayer polls this from
    // GetProgress() to flip its own State/LastError, since PlayVideo itself returns long before
    // the failure is known and its caller's try/catch never sees it.
    internal string? LastError { get; private set; }


    internal void StopVideo()
    {
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
        IsAudioOnly = false;

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
        StopVideo();

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
                ScreenScale);

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

        StopVideo();

        try
        {
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
                ScreenScale);

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
        bool isLocalVideo = false)
    {
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


        if (_isPlayingSnes ||
         _isPlayingGameBoy)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] End gameplay to begin playback.");

            return;
        }


        if (_disposing)
        {
            return;
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


            _ =
                Task.Run(
                    async () =>
                    {
                        await TryWebResolverFallbackAsync(
                                url,
                                playbackPosition,
                                isPlaying,
                                playbackGeneration,
                                failureMessage)
                            .ConfigureAwait(false);
                    });
        }


        AssignScreenForSession(
            _screenTexture);


        _screenPainter.SetLoading(
            true);


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
                            // MPV emits many warnings during otherwise healthy playback:
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


            StartWebResolverFallback(
                message);


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


         bool audioOnly =
             hasAudio &&
             !hasVideo;


         IsAudioOnly =
             audioOnly;


         _screenPainter
             .SetAudioOnly(
                 audioOnly);


         if (audioOnly)
         {
             Interlocked.Exchange(
                 ref playbackStarted,
                 1);


             _screenPainter
                 .SetLoading(
                     false);
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
                                };
                        }


                        //
                        // =================================================
                        // Existing renderer
                        // =================================================
                        //

                        if (_mpvRenderer != null)
                        {
                            ConfigureRendererCallbacks(
                                _mpvRenderer);


                            _mpvRenderer.Play(
                                url,
                                playbackPosition,
                                isPlaying);


                            _isActive =
                                true;


                            _screenPainter
                                .SetTransform(
                                    ScreenPosition,
                                    ScreenYaw,
                                    ScreenScale);


                            return;
                        }


                        //
                        // =================================================
                        // New renderer
                        // =================================================
                        //

                        AepLog.Info(
         $"[MPV] Creating renderer for: {url}");


                        _mpvRenderer =
                            new MpvRenderer();


                        AepLog.Info(
                            "[MPV] Renderer object created.");


                        ConfigureRendererCallbacks(
                            _mpvRenderer);


                        AepLog.Info(
                            "[MPV] Renderer callbacks configured.");


                        AepLog.Info(
                            $"[MPV] Initializing renderer. " +
                            $"CancellationRequested={_renderCancellation.IsCancellationRequested}");


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


                        AepLog.Info(
                            "[MPV] Renderer initialized successfully.");


                        AepLog.Info(
                            $"[MPV] Sending Play command for: {url}");


                        _mpvRenderer.Play(
                            url,
                            playbackPosition,
                            isPlaying);


                        AepLog.Info(
                            "[MPV] Play command returned successfully.");


                        _isActive =
                            true;


                        _screenPainter
                            .SetTransform(
                                ScreenPosition,
                                ScreenYaw,
                                ScreenScale);


                        AepLog.Info(
                            "[MPV] Entering render loop.");


                        while (!_stopRequested &&
                               _mpvRenderer.RenderFrame())
                        {
                        }


                        AepLog.Info(
                            "[MPV] Render loop exited.");


                        AepLog.Debug(
                            "[MPV] Video render loop ended.");


                        //
                        // A resolver retry or another playback mode may have
                        // replaced this generation while the old render loop
                        // was shutting down.
                        //

                        if (playbackGeneration !=
                            _playbackGeneration)
                        {
                            AepLog.Debug(
                                "[MPV] Ignoring stale video cleanup because another playback session owns the screen.");

                            return;
                        }


                        _isActive =
                            false;


                        _screenPainter
                            .SetLoading(
                                false);


                        _screenPainter
                            .SetTarget(
                                null);


                        var oldRenderer =
                            _mpvRenderer;


                        _mpvRenderer =
                            null;


                        if (oldRenderer is not null)
                        {
                            try
                            {
                                oldRenderer.Dispose();
                            }
                            catch (Exception exception)
                            {
                                AepLog.Warning(
                                    $"[MPV] Failed to dispose renderer after video end: {exception.Message}");
                            }
                        }


                        _renderCancellation
                            .Dispose();


                        _renderCancellation =
                            new CancellationTokenSource();
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
    string originalFailure)
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


            var result =
                await WebMediaUrlResolver
                    .ResolveAsync(
                        Resources,
                        originalUrl,
                        CancellationToken.None)
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
                ScreenScale);


            AepLog.Info(
                $"[WebResolver] Retrying on existing MPV renderer: {resolvedUrl}");


            renderer.Play(
                resolvedUrl,
                playbackPosition,
                isPlaying);


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
        vol = Math.Clamp(
            vol,
            0,
            200);

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

        if (!_renderCancellation.Token
                        .IsCancellationRequested)
        {
            _mpvRenderer?.SetVolume(
                vol);
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
        if (_isPlayingSnes)
        {
            UpdateSnesInput();

            _snesRenderer?.OnFrameworkUpdate();

            _lastIdle = false;

            return;
        }

        if (_isPlayingGameBoy)
        {
            UpdateGameBoyInput();

            _gameBoyRenderer?.OnFrameworkUpdate();

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
        _disposing = true;
        _stopRequested = true;
        _isActive = false;

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

        _screenPainter.Dispose();
        _previewShaderResourceView.Dispose();
        _screenTexture.Dispose();
        Resources.Dispose();
    }
}
