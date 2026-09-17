using System.Diagnostics;
using System.Buffers;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;

namespace AlphaChannel.Plugin.Video;

internal sealed class GambatteRenderer(
    string corePath,
    string romsDirectory,
    bool nestopia = false,
    bool mgba = false,
    bool gearsystem = false,
    bool gameGear = false) : IDisposable
{
    private const uint RETRO_DEVICE_JOYPAD = 1;
    private const uint RETRO_MEMORY_SAVE_RAM = 0;

    private const uint ENV_GET_CAN_DUPE = 3;
    private const uint ENV_GET_SYSTEM_DIRECTORY = 9;
    private const uint ENV_SET_PIXEL_FORMAT = 10;
    private const uint ENV_GET_VARIABLE = 15;
    private const uint ENV_GET_VARIABLE_UPDATE = 17;
    private const uint ENV_GET_SAVE_DIRECTORY = 31;

    private const int PIXFMT_RGB565 = 2;
    private const int PIXFMT_XRGB8888 = 1;

    private static GambatteRenderer? _instance;

    private static readonly RetroEnvironmentT _envCb =
        EnvironmentCb;

    private static readonly RetroVideoRefreshT _videoCb =
        VideoRefreshCb;

    private static readonly RetroAudioSampleT _audioCb =
        AudioSampleCb;

    private static readonly RetroAudioSampleBatchT _audioBatchCb =
        AudioBatchCb;

    private static readonly RetroInputPollT _inputPollCb =
        InputPollCb;

    private static readonly RetroInputStateT _inputStateCb =
        InputStateCb;

    private static IntPtr _sysDirPtr;
    private static IntPtr _romPathPtr;

    private static IntPtr _lib;

    private static RetroApiVersionFn _apiVersion = null!;
    private static RetroSetEnvironmentFn _setEnvironment = null!;
    private static RetroSetVideoRefreshFn _setVideoRefresh = null!;
    private static RetroSetAudioSampleFn _setAudioSample = null!;
    private static RetroSetAudioSampleBatchFn _setAudioSampleBatch = null!;
    private static RetroSetInputPollFn _setInputPoll = null!;
    private static RetroSetInputStateFn _setInputState = null!;
    private static RetroInitFn _init = null!;
    private static RetroDeinitFn _deinit = null!;
    private static RetroGetSystemInfoFn _getSystemInfo = null!;
    private static RetroGetSystemAvInfoFn _getSystemAvInfo = null!;
    private static RetroLoadGameFn _loadGame = null!;
    private static RetroUnloadGameFn _unloadGame = null!;
    private static RetroRunFn _run = null!;
    private static RetroSetControllerPortDeviceFn _setControllerPortDevice = null!;
    private static RetroGetMemoryDataFn _getMemoryData = null!;
    private static RetroGetMemorySizeFn _getMemorySize = null!;

    private readonly string _corePath =
        corePath;

    private readonly string _romsDirectory =
        romsDirectory;

    private readonly bool _isNestopia =
        nestopia;

    private readonly bool _isMgba =
        mgba;

    private readonly bool _isGearsystem =
        gearsystem;

    private readonly bool _isGameGear =
        gameGear;

    private string LogPrefix => _isNestopia ? "[NESTOPIA]" : _isMgba ? "[MGBA]" : _isGearsystem ? "[GEARSYSTEM]" : "[GAMBATTE]";
    private string SystemName => _isNestopia ? "NES" : _isMgba ? "Game Boy Advance" : _isGameGear ? "Game Gear" : _isGearsystem ? "Master System / SG-1000" : "Game Boy";

    private readonly short[,] _input =
        new short[1, 16];

    private readonly Lock _lock =
        new();

    private Texture2D? _targetTexture;
    private CrtLottesScaler? _scaler;
    private Snes9xAudio? _audio;

    private GameBroadcastEncoder? _broadcastEncoder;

    private bool _crtFilterEnabled;

    private Thread? _runThread;
    private CancellationTokenSource? _cancel;
    private volatile bool _deferredTeardown;

    private volatile bool _running;
    private volatile bool _failed;
    private string? _failureMessage;
    private bool _coreInited;

    private double _fps =
        60.0;

    private int _sampleRate =
        48000;

    private int _videoWidth;
    private int _videoHeight;
    private int _pixelFormat = nestopia ? PIXFMT_XRGB8888 : PIXFMT_RGB565;

    private string _savePath =
        string.Empty;

    private byte[]? _lastSaveRam;

    private DateTime _lastSaveCheck =
        DateTime.UtcNow;


    // =============================================================
    // Native loading
    // =============================================================

    private static T Get<T>(
        string name)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(
            NativeLibrary.GetExport(
                _lib,
                name));


    private static void LoadNative(
        string dllPath)
    {
        _lib =
            NativeLibrary.Load(
                dllPath);

        _apiVersion =
            Get<RetroApiVersionFn>(
                "retro_api_version");

        _setEnvironment =
            Get<RetroSetEnvironmentFn>(
                "retro_set_environment");

        _setVideoRefresh =
            Get<RetroSetVideoRefreshFn>(
                "retro_set_video_refresh");

        _setAudioSample =
            Get<RetroSetAudioSampleFn>(
                "retro_set_audio_sample");

        _setAudioSampleBatch =
            Get<RetroSetAudioSampleBatchFn>(
                "retro_set_audio_sample_batch");

        _setInputPoll =
            Get<RetroSetInputPollFn>(
                "retro_set_input_poll");

        _setInputState =
            Get<RetroSetInputStateFn>(
                "retro_set_input_state");

        _init =
            Get<RetroInitFn>(
                "retro_init");

        _deinit =
            Get<RetroDeinitFn>(
                "retro_deinit");

        _getSystemInfo =
            Get<RetroGetSystemInfoFn>(
                "retro_get_system_info");

        _getSystemAvInfo =
            Get<RetroGetSystemAvInfoFn>(
                "retro_get_system_av_info");

        _loadGame =
            Get<RetroLoadGameFn>(
                "retro_load_game");

        _unloadGame =
            Get<RetroUnloadGameFn>(
                "retro_unload_game");

        _run =
            Get<RetroRunFn>(
                "retro_run");

        _setControllerPortDevice =
            Get<RetroSetControllerPortDeviceFn>(
                "retro_set_controller_port_device");

        _getMemoryData =
            Get<RetroGetMemoryDataFn>(
                "retro_get_memory_data");

        _getMemorySize =
            Get<RetroGetMemorySizeFn>(
                "retro_get_memory_size");
    }


    private static void FreeNative()
    {
        _apiVersion = null!;
        _setEnvironment = null!;
        _setVideoRefresh = null!;
        _setAudioSample = null!;
        _setAudioSampleBatch = null!;
        _setInputPoll = null!;
        _setInputState = null!;
        _init = null!;
        _deinit = null!;
        _getSystemInfo = null!;
        _getSystemAvInfo = null!;
        _loadGame = null!;
        _unloadGame = null!;
        _run = null!;
        _setControllerPortDevice = null!;
        _getMemoryData = null!;
        _getMemorySize = null!;

        if (_lib == IntPtr.Zero)
        {
            return;
        }

        AepLog.Debug(
            $"[GAMBATTE] Freeing native DLL: {_lib}");

        NativeLibrary.Free(
            _lib);

        _lib =
            IntPtr.Zero;
    }


    // =============================================================
    // Libretro native declarations
    // =============================================================

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate uint RetroApiVersionFn();

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroSetEnvironmentFn(
        RetroEnvironmentT callback);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroSetVideoRefreshFn(
        RetroVideoRefreshT callback);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroSetAudioSampleFn(
        RetroAudioSampleT callback);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroSetAudioSampleBatchFn(
        RetroAudioSampleBatchT callback);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroSetInputPollFn(
        RetroInputPollT callback);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroSetInputStateFn(
        RetroInputStateT callback);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroInitFn();

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroDeinitFn();

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroGetSystemInfoFn(
        out RetroSystemInfo info);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroGetSystemAvInfoFn(
        out RetroSystemAvInfo info);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool RetroLoadGameFn(
        ref RetroGameInfo game);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroUnloadGameFn();

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroRunFn();

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroSetControllerPortDeviceFn(
        uint port,
        uint device);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate IntPtr RetroGetMemoryDataFn(
        uint id);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate nuint RetroGetMemorySizeFn(
        uint id);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool RetroEnvironmentT(
        uint cmd,
        IntPtr data);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroVideoRefreshT(
        IntPtr data,
        uint width,
        uint height,
        nuint pitch);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroAudioSampleT(
        short left,
        short right);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate nuint RetroAudioSampleBatchT(
        IntPtr data,
        nuint frames);

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate void RetroInputPollT();

    [UnmanagedFunctionPointer(
        CallingConvention.Cdecl)]
    private delegate short RetroInputStateT(
        uint port,
        uint device,
        uint index,
        uint id);


    [StructLayout(
        LayoutKind.Sequential)]
    private struct RetroSystemInfo
    {
        internal IntPtr LibraryName;
        internal IntPtr LibraryVersion;
        internal IntPtr ValidExtensions;

        [MarshalAs(UnmanagedType.U1)]
        internal bool NeedFullpath;

        [MarshalAs(UnmanagedType.U1)]
        internal bool BlockExtract;
    }


    [StructLayout(
        LayoutKind.Sequential)]
    private struct RetroGameGeometry
    {
        internal uint BaseWidth;
        internal uint BaseHeight;
        internal uint MaxWidth;
        internal uint MaxHeight;
        internal float AspectRatio;
    }


    [StructLayout(
        LayoutKind.Sequential)]
    private struct RetroSystemTiming
    {
        internal double Fps;
        internal double SampleRate;
    }


    [StructLayout(
        LayoutKind.Sequential)]
    private struct RetroSystemAvInfo
    {
        internal RetroGameGeometry Geometry;
        internal RetroSystemTiming Timing;
    }


    [StructLayout(
        LayoutKind.Sequential)]
    private struct RetroGameInfo
    {
        internal IntPtr Path;
        internal IntPtr Data;
        internal nuint Size;
        internal IntPtr Meta;
    }


    // =============================================================
    // Load / unload
    // =============================================================

    internal bool Load(
        Texture2D? targetTexture,
        string romPath)
    {
        if (_running)
        {
            Unload();
        }

        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(
                    _corePath) ||
                !File.Exists(
                    _corePath))
            {
                AepLog.Error(
                    $"{LogPrefix} Core DLL not found: {_corePath}");

                return false;
            }

            if (!File.Exists(
                    romPath))
            {
                AepLog.Error(
                    $"{LogPrefix} ROM not found: {romPath}");

                return false;
            }

            var extension =
                Path.GetExtension(
                    romPath);

            var supported = _isNestopia
                ? extension.Equals(".nes", StringComparison.OrdinalIgnoreCase)
                : _isMgba
                    ? extension.Equals(".gba", StringComparison.OrdinalIgnoreCase)
                    : _isGearsystem
                        ? extension.Equals(".sms", StringComparison.OrdinalIgnoreCase) ||
                          extension.Equals(".sg", StringComparison.OrdinalIgnoreCase) ||
                          extension.Equals(".gg", StringComparison.OrdinalIgnoreCase)
                        : extension.Equals(".gb", StringComparison.OrdinalIgnoreCase) ||
                          extension.Equals(".gbc", StringComparison.OrdinalIgnoreCase) ||
                          extension.Equals(".dmg", StringComparison.OrdinalIgnoreCase);

            if (!supported)
            {
                AepLog.Error(
                    $"{LogPrefix} Unsupported ROM extension: {extension}");

                return false;
            }

            _instance =
                this;

            _cancel =
                new CancellationTokenSource();

            _targetTexture =
                targetTexture;

            if (_targetTexture is not null &&
                DxHandler.Device is not null)
            {
                _scaler =
                    new CrtLottesScaler(
                        DxHandler.Device,
                        targetTexture);

                _scaler.Enabled =
                    _crtFilterEnabled;
            }

            try
            {
                LoadNative(
                    _corePath);

                _setEnvironment(
                    _envCb);

                _setVideoRefresh(
                    _videoCb);

                _setAudioSample(
                    _audioCb);

                _setAudioSampleBatch(
                    _audioBatchCb);

                _setInputPoll(
                    _inputPollCb);

                _setInputState(
                    _inputStateCb);

                _init();

                _coreInited =
                    true;

                _getSystemInfo(
                    out var systemInfo);

                var coreName =
                    Marshal.PtrToStringAnsi(
                        systemInfo.LibraryName) ??
                    (_isNestopia ? "Nestopia" : _isMgba ? "mGBA" : _isGearsystem ? "Gearsystem" : "Gambatte");

                var coreVersion =
                    Marshal.PtrToStringAnsi(
                        systemInfo.LibraryVersion) ??
                    string.Empty;

                AepLog.Info(
                    $"{LogPrefix} Core loaded: {coreName} {coreVersion}");

                var gameInfo =
                    new RetroGameInfo();

                IntPtr dataPtr =
                    IntPtr.Zero;

                try
                {
                    if (_romPathPtr !=
                        IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(
                            _romPathPtr);

                        _romPathPtr =
                            IntPtr.Zero;
                    }

                    _romPathPtr =
                        Marshal.StringToHGlobalAnsi(
                            romPath);

                    gameInfo.Path =
                        _romPathPtr;

                    if (!systemInfo.NeedFullpath)
                    {
                        var rom =
                            File.ReadAllBytes(
                                romPath);

                        dataPtr =
                            Marshal.AllocHGlobal(
                                rom.Length);

                        Marshal.Copy(
                            rom,
                            0,
                            dataPtr,
                            rom.Length);

                        gameInfo.Data =
                            dataPtr;

                        gameInfo.Size =
                            (nuint)rom.Length;
                    }

                    if (!_loadGame(
                            ref gameInfo))
                    {
                        AepLog.Error(
                            $"{LogPrefix} retro_load_game failed.");

                        TeardownLocked();

                        return false;
                    }
                }
                finally
                {
                    if (dataPtr !=
                        IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(
                            dataPtr);
                    }
                }

                _savePath =
                    Path.ChangeExtension(
                        romPath,
                        ".srm");

                LoadSaveRam();

                _getSystemAvInfo(
                    out var av);

                _fps =
                    av.Timing.Fps > 1
                        ? av.Timing.Fps
                        : 60.0;

                _sampleRate =
     av.Timing.SampleRate > 1
         ? (int)av.Timing.SampleRate
         : 48000;

                _audio =
                    new Snes9xAudio(
                        _sampleRate);

                _setControllerPortDevice(
                    0,
                    RETRO_DEVICE_JOYPAD);

                SetVolume(
                    Plugin.Cfg.Volume);


                AepLog.Info(
                    $"{LogPrefix} Loaded {Path.GetFileName(romPath)} " +
                    $"@ {_fps:0.##}fps, " +
                    $"{_sampleRate}Hz, " +
                    $"{av.Geometry.BaseWidth}x{av.Geometry.BaseHeight}");
            }
            catch (Exception exception)
            {
                AepLog.Error(
                    $"{LogPrefix} Failed to initialize core: {exception}");

                TeardownLocked();

                return false;
            }
        }

        _running =
            true;

        _failed = false;
        _failureMessage = null;

        _runThread =
            new Thread(
                RunLoop)
            {
                IsBackground = true,
                Name = _isGearsystem ? "gearsystem-run" : "gambatte-run"
            };

        _runThread.Start();

        return true;
    }


    internal void Unload()
    {
        _running =
            false;

        _cancel?.Cancel();

        var runThread =
            _runThread;

        if (runThread is not null &&
            runThread != Thread.CurrentThread)
        {
            _deferredTeardown = true;

            if (!runThread.Join(TimeSpan.FromSeconds(3)))
            {
            // Never block Dalamud's unload indefinitely or unload a native
            // libretro core while its thread may still be executing in it.
            AepLog.Error(
                $"{LogPrefix} Emulator thread did not stop within 3 seconds; " +
                "native teardown will run when the core call returns.");
                return;
            }

            if (!_deferredTeardown)
            {
                _runThread = null;
                return;
            }

            _deferredTeardown = false;
        }

        _runThread =
            null;

        lock (_lock)
        {
            TeardownLocked();
        }
    }


    public void Dispose()
    {
        Unload();

        GC.SuppressFinalize(
            this);
    }


    private void TeardownLocked()
    {
        //
        // Stop FFmpeg before unloading the libretro core.
        //

        StopBroadcast();

        if (_coreInited)
        {
            SaveRamIfChanged();

            try
            {
                _unloadGame();
            }
            catch (Exception exception)
            {
                AepLog.Warning(
                    $"{LogPrefix} retro_unload_game failed: {exception.Message}");
            }

            try
            {
                _deinit();
            }
            catch (Exception exception)
            {
                AepLog.Warning(
                    $"{LogPrefix} retro_deinit failed: {exception.Message}");
            }

            _coreInited =
                false;
        }

        _audio?.Dispose();
        _audio = null;

        _scaler?.Dispose();
        _scaler = null;

        _targetTexture =
            null;

        FreeNative();

        if (_romPathPtr !=
            IntPtr.Zero)
        {
            Marshal.FreeHGlobal(
                _romPathPtr);

            _romPathPtr =
                IntPtr.Zero;
        }

        if (_sysDirPtr !=
            IntPtr.Zero)
        {
            Marshal.FreeHGlobal(
                _sysDirPtr);

            _sysDirPtr =
                IntPtr.Zero;
        }

        _cancel?.Dispose();
        _cancel = null;

        if (_instance == this)
        {
            _instance =
                null;
        }
    }


    // =============================================================
    // Save RAM
    // =============================================================

    internal void OnFrameworkUpdate()
    {
        if ((DateTime.UtcNow -
             _lastSaveCheck).TotalSeconds <
            3)
        {
            return;
        }

        _lastSaveCheck =
            DateTime.UtcNow;

        SaveRamIfChanged();
    }


    private void SaveRamIfChanged()
    {
        if (!_coreInited ||
            string.IsNullOrWhiteSpace(
                _savePath))
        {
            return;
        }

        var size =
            _getMemorySize(
                RETRO_MEMORY_SAVE_RAM);

        var ptr =
            _getMemoryData(
                RETRO_MEMORY_SAVE_RAM);

        if (size == 0 ||
            ptr == IntPtr.Zero)
        {
            return;
        }

        var saveRam =
            new byte[(int)size];

        Marshal.Copy(
            ptr,
            saveRam,
            0,
            (int)size);

        if (_lastSaveRam is not null &&
            saveRam.AsSpan()
                .SequenceEqual(
                    _lastSaveRam))
        {
            return;
        }

        _lastSaveRam =
            saveRam;

        File.WriteAllBytes(
            _savePath,
            saveRam);
    }


    private void LoadSaveRam()
    {
        if (!File.Exists(
                _savePath))
        {
            return;
        }

        var size =
            _getMemorySize(
                RETRO_MEMORY_SAVE_RAM);

        var ptr =
            _getMemoryData(
                RETRO_MEMORY_SAVE_RAM);

        if (size == 0 ||
            ptr == IntPtr.Zero)
        {
            return;
        }

        var saveRam =
            File.ReadAllBytes(
                _savePath);

        var copyLength =
            Math.Min(
                saveRam.Length,
                (int)size);

        Marshal.Copy(
            saveRam,
            0,
            ptr,
            copyLength);

        _lastSaveRam =
            saveRam;
    }


    // =============================================================
    // Input
    // =============================================================

    internal void SetButton(
        int id,
        bool pressed)
    {
        if (id is < 0 or > 15)
        {
            return;
        }

        _input[0, id] =
            (short)(
                pressed
                    ? 1
                    : 0);
    }


    // =============================================================
    // Core loop
    // =============================================================

    private void RunLoop()
    {
        try
        {
            var frameMs = 1000.0 / _fps;
            var stopwatch = Stopwatch.StartNew();
            double next = 0;

            while (_running)
            {
                if (_cancel?.IsCancellationRequested == true)
                {
                    break;
                }

                lock (_lock)
                {
                    if (!_running) break;
                    _run();
                }

                next += frameMs;
                var wait = next - stopwatch.Elapsed.TotalMilliseconds;
                if (wait > 1) Thread.Sleep((int)wait);
                else if (wait < -250) next = stopwatch.Elapsed.TotalMilliseconds;
            }
        }
        catch (Exception exception)
        {
            if (_running)
            {
                _failureMessage = $"The {SystemName} emulator stopped unexpectedly.";
                _failed = true;
                AepLog.Error($"{LogPrefix} Emulator loop failed: {exception}");
            }
        }
        finally
        {
            _running = false;

            if (_deferredTeardown)
            {
                lock (_lock)
                {
                    if (_deferredTeardown)
                    {
                        _deferredTeardown = false;
                        TeardownLocked();
                        _runThread = null;
                    }
                }
            }
        }
    }


    // =============================================================
    // Libretro callbacks
    // =============================================================

    private static void AudioSampleCb(
        short left,
        short right)
    {
        // Gambatte normally uses the batch callback.
    }


    private static void InputPollCb()
    {
    }


    private static bool EnvironmentCb(
        uint cmd,
        IntPtr data)
    {
        var self =
            _instance;

        if (self is null ||
            data == IntPtr.Zero)
        {
            return false;
        }

        switch (cmd)
        {
            case ENV_SET_PIXEL_FORMAT:
                {
                    var format =
                        Marshal.ReadInt32(
                            data);

                    if (format ==
                        PIXFMT_RGB565)
                    {
                        self._pixelFormat = format;
                        AepLog.Debug(
                            $"{self.LogPrefix} Core selected RGB565 video.");

                        return true;
                    }

                    if (format == PIXFMT_XRGB8888)
                    {
                        self._pixelFormat = format;
                        AepLog.Debug("[NESTOPIA] Core selected XRGB8888 video.");
                        return true;
                    }

                    AepLog.Warning(
                        $"{self.LogPrefix} Unsupported pixel format requested: {format}");

                    return false;
                }

            case ENV_GET_CAN_DUPE:
                Marshal.WriteByte(
                    data,
                    1);

                return true;

            case ENV_GET_SYSTEM_DIRECTORY:
                Marshal.WriteIntPtr(
                    data,
                    self.GetSystemDirectory());

                return true;

            case ENV_GET_SAVE_DIRECTORY:
                // Same behaviour as the working SNES frontend.
                // Save RAM is handled directly by Alpha Channel.
                return false;

            case ENV_GET_VARIABLE_UPDATE:
                Marshal.WriteByte(
                    data,
                    0);

                return true;

            case ENV_GET_VARIABLE:
            default:
                return false;
        }
    }


    private IntPtr GetSystemDirectory()
    {
        if (_sysDirPtr ==
            IntPtr.Zero)
        {
            _sysDirPtr =
                Marshal.StringToHGlobalAnsi(
                    _romsDirectory);
        }

        return _sysDirPtr;
    }


    private static void VideoRefreshCb(
     IntPtr data,
     uint width,
     uint height,
     nuint pitch)
    {
        try
        {
            var self =
                _instance;

            if (self is null ||
                data == IntPtr.Zero)
            {
                return;
            }

            self.SubmitVideoFrame(data, (int)width, (int)height, (int)pitch);
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"{_instance?.LogPrefix ?? "[LIBRETRO]"} Video callback failed: {exception.Message}");
        }
    }

    private unsafe void SubmitVideoFrame(IntPtr data, int width, int height, int pitch)
    {
        _videoWidth = width;
        _videoHeight = height;

        if (_pixelFormat == PIXFMT_RGB565)
        {
            _scaler?.Submit(data, width, height, pitch);
            _broadcastEncoder?.SubmitVideoFrame(data, width, height, pitch);
            return;
        }

        // Cores may supply XRGB8888. Convert at the frontend boundary so
        // the established TV scaler and FFmpeg encoder continue to receive
        // the same RGB565 frames used by SNES and Game Boy.
        var rowBytes = checked(width * 2);
        var buffer = ArrayPool<byte>.Shared.Rent(checked(rowBytes * height));
        try
        {
            fixed (byte* destinationBase = buffer)
            {
                var sourceBase = (byte*)data;
                for (var y = 0; y < height; y++)
                {
                    var source = sourceBase + y * pitch;
                    var destination = (ushort*)(destinationBase + y * rowBytes);
                    for (var x = 0; x < width; x++)
                    {
                        var offset = x * 4;
                        var blue = source[offset];
                        var green = source[offset + 1];
                        var red = source[offset + 2];
                        destination[x] = (ushort)(((red >> 3) << 11) | ((green >> 2) << 5) | (blue >> 3));
                    }
                }

                var converted = (IntPtr)destinationBase;
                _scaler?.Submit(converted, width, height, rowBytes);
                _broadcastEncoder?.SubmitVideoFrame(converted, width, height, rowBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static nuint AudioBatchCb(
    IntPtr data,
    nuint frames)
    {
        try
        {
            var self = _instance;

            self?._audio?.Submit(
                data,
                (int)frames);

            self?._broadcastEncoder?.SubmitAudio(
                data,
                (int)frames);
        }
        catch
        {
            // Keep emulator thread alive if the audio device hiccups.
        }

        return frames;
    }


    private static short InputStateCb(
        uint port,
        uint device,
        uint index,
        uint id)
    {
        try
        {
            var self =
                _instance;

            if (self is null ||
                device != RETRO_DEVICE_JOYPAD ||
                port != 0 ||
                id >= 16)
            {
                return 0;
            }

            return self._input[0, id];
        }
        catch
        {
            return 0;
        }
    }


    internal void SetVolume(
    int volume)
    {
        _audio?.SetVolume(
            volume);
    }


    internal bool IsBroadcasting =>
        _broadcastEncoder?.IsRunning ==
        true;

    internal bool HasFailed => _failed;
    internal string? FailureMessage => _failureMessage;

    internal BroadcastDiagnosticsSnapshot BroadcastDiagnostics =>
        _broadcastEncoder?.Diagnostics ?? BroadcastDiagnosticsSnapshot.Idle;


    internal bool StartBroadcast(
        string ffmpegPath,
        string publishUrl)
    {
        if (!_running ||
            !_coreInited)
        {
            AepLog.Warning(
                $"{LogPrefix} Cannot start broadcast because no {SystemName} game is running.");

            return false;
        }

        if (_broadcastEncoder?.IsRunning ==
            true)
        {
            AepLog.Info(
                $"{LogPrefix} Game broadcast is already running.");

            return true;
        }

        StopBroadcast();

        //
        // Use the actual core geometry and timing obtained from
        // retro_get_system_av_info when the ROM was loaded.
        //
        // The encoder dimensions are fixed for the lifetime of the
        // FFmpeg process. If the core unexpectedly supplies different
        // geometry, SubmitVideoFrame will safely ignore those frames.
        //

        var width = _videoWidth;
        var height = _videoHeight;

        if (width <= 0 || height <= 0)
        {
            AepLog.Warning("[GAME-BROADCAST] No emulator video frame has been received yet.");
            return false;
        }

        var encoder =
            new GameBroadcastEncoder();

        if (!encoder.Start(
                ffmpegPath,
                publishUrl,
                width,
                height,
                _fps,
                _sampleRate,
                sourceName: SystemName))
        {
            encoder.Dispose();

            AepLog.Error(
                $"{LogPrefix} Failed to start game broadcast.");

            return false;
        }

        _broadcastEncoder =
            encoder;

        AepLog.Info(
            $"{LogPrefix} Game broadcast started at {width}x{height} @ {_fps:0.###}fps.");

        return true;
    }


    internal void StopBroadcast()
    {
        var encoder =
            _broadcastEncoder;

        _broadcastEncoder =
            null;

        if (encoder is null)
        {
            return;
        }

        try
        {
            encoder.Dispose();
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"{LogPrefix} Error while stopping game broadcast: {exception.Message}");
        }

        AepLog.Info(
            $"{LogPrefix} Game broadcast stopped.");
    }


    internal void SetCrtFilterEnabled(
        bool enabled)
    {
        _crtFilterEnabled =
            enabled;

        if (_scaler is not null)
        {
            _scaler.Enabled =
                enabled;
        }
    }
}


//
// Standard libretro joypad IDs.
//
// Shared libretro joypad inputs used by Game Boy, NES and Game Boy Advance.
//

internal enum GambatteInput
{
    B = 0,
    Select = 2,
    Start = 3,

    Up = 4,
    Down = 5,
    Left = 6,
    Right = 7,

    A = 8,
    L = 10,
    R = 11
}
