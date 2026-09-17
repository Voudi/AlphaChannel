using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SharpDX;
using SharpDX.Direct3D11;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin.Video;

// CefSharp runs in AlphaChannel.BrowserHost.exe because its mixed C++/CLI
// bridge cannot load in Dalamud's collectible AssemblyLoadContext.
internal sealed class BrowserRenderer : IDisposable
{
    internal const int Width = 960;
    internal const int Height = 540;
    private const int FramesPerSecond = 20;
    private const int AudioSampleRate = 48000;
    private readonly string renderKey = $"AlphaChannel.Browser.{Guid.NewGuid():N}";
    private readonly Texture2D target;
    private readonly BrowserFrameScaler frameScaler;
    private readonly object frameLock = new();
    private readonly object writeLock = new();
    private readonly byte[] broadcastFrame = new byte[Width * Height * 4];
    private readonly byte[] privacyFrame;
    private NamedPipeServerStream? pipe;
    private NamedPipeServerStream? audioPipe;
    private BinaryReader? reader;
    private BinaryReader? audioReader;
    private BinaryWriter? writer;
    private Process? process;
    private CancellationTokenSource? cancellation;
    private Task? hostMessagesTask;
    private Task? hostAudioTask;
    private Snes9xAudio? localAudio;
    private GameBroadcastEncoder? encoder;
    private CancellationTokenSource? broadcastCancellation;
    private Task? broadcastTask;
    private byte[]? pendingFrame;
    private BrowserState state = new("https://www.google.com/", "Browser", true, false, false, false);
    private long lastVideoTick;
    private long lastAudioTick;
    private long lastSilenceTick;
    private bool disposed;
    private volatile bool privacyMode;
    private readonly object youtubeCookieRequestLock = new();
    private TaskCompletionSource<YouTubeCookieExportResult>? youtubeCookieRequest;

    internal string Address => state.Address;
    internal string Title => string.IsNullOrWhiteSpace(state.Title) ? "Browser" : state.Title;
    internal string? LastError { get; private set; }
    internal bool IsReady => state.IsReady && process is { HasExited: false };
    internal bool IsLoading => state.IsLoading;
    internal bool CanGoBack => state.CanGoBack;
    internal bool CanGoForward => state.CanGoForward;
    internal bool IsBroadcasting => encoder?.IsRunning == true;
    internal bool HasFailed
    {
        get
        {
            if (disposed) return false;
            try
            {
                return process is null || process.HasExited || pipe?.IsConnected != true;
            }
            catch
            {
                return true;
            }
        }
    }
    internal string FailureMessage =>
        LastError ?? "The browser process stopped unexpectedly.";
    internal BroadcastDiagnosticsSnapshot BroadcastDiagnostics =>
        encoder?.Diagnostics ?? BroadcastDiagnosticsSnapshot.Idle;

    internal BrowserRenderer(Texture2D target, string cacheDirectory)
    {
        this.target = target;
        frameScaler = new BrowserFrameScaler(target, Width, Height);
        privacyFrame = CreatePrivacyFrame();
        localAudio = new Snes9xAudio(AudioSampleRate);
        StartHost(cacheDirectory);
    }

    private void StartHost(string cacheDirectory)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var runtimeDirectory = Plugin.PluginInterface.AssemblyLocation.DirectoryName
                ?? throw new InvalidOperationException("The Alpha Channel plugin directory could not be located.");
            var executable = Path.Combine(runtimeDirectory, "AlphaChannel.BrowserHost.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("The Alpha Channel browser host is missing.", executable);
            var pipeName = $"AlphaChannelBrowser_{Guid.NewGuid():N}";
            var audioPipeName = $"AlphaChannelBrowserAudio_{Guid.NewGuid():N}";
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 1024 * 1024, 1024 * 1024);
            audioPipe = new NamedPipeServerStream(audioPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add(cacheDirectory);
            startInfo.ArgumentList.Add(audioPipeName);
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("The browser host could not be started.");
            var connection = pipe.WaitForConnectionAsync();
            var audioConnection = audioPipe.WaitForConnectionAsync();
            if (!Task.WaitAll([connection, audioConnection], TimeSpan.FromSeconds(12)))
            {
                var detail = process.HasExited ? $" It exited with code {process.ExitCode}." : string.Empty;
                throw new TimeoutException("The browser host did not connect in time." + detail);
            }
            reader = new BinaryReader(pipe, Encoding.UTF8, true);
            audioReader = new BinaryReader(audioPipe, Encoding.UTF8, true);
            writer = new BinaryWriter(pipe, Encoding.UTF8, true);
            cancellation = new CancellationTokenSource();
            hostMessagesTask =
                Task.Run(() => ReadHostMessages(cancellation.Token));
            hostAudioTask =
                Task.Run(() => ReadHostAudio(cancellation.Token));
        }
        catch
        {
            pipe?.Dispose();
            audioPipe?.Dispose();
            try { if (process is { HasExited: false }) process.Kill(true); } catch { }
            process?.Dispose();
            process = null;
            throw;
        }
    }

    private void ReadHostAudio(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && audioPipe?.IsConnected == true)
            {
                var length = audioReader!.ReadInt32();
                if (length <= 0 || length > AudioSampleRate * 4) throw new InvalidDataException("Invalid browser audio packet.");
                var payload = audioReader.ReadBytes(length);
                if (payload.Length != length) throw new EndOfStreamException();
                SubmitAudio(payload);
            }
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or ObjectDisposedException)
        {
            if (!disposed) LastError = "The browser audio connection closed unexpectedly.";
        }
    }

    private void ReadHostMessages(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && pipe?.IsConnected == true)
            {
                var type = reader!.ReadByte();
                var length = reader.ReadInt32();
                if (length < 0 || length > Width * Height * 4 + 1024 * 1024) throw new InvalidDataException("Invalid browser-host message.");
                var payload = reader.ReadBytes(length);
                if (payload.Length != length) throw new EndOfStreamException();
                switch (type)
                {
                    case 1 when length == Width * Height * 4:
                        lock (frameLock)
                        {
                            System.Buffer.BlockCopy(payload, 0, broadcastFrame, 0, length);
                            pendingFrame = payload;
                        }
                        break;
                    case 3: state = JsonSerializer.Deserialize<BrowserState>(payload) ?? state; break;
                    case 4: LastError = Encoding.UTF8.GetString(payload); break;
                    case 5:
                        var result =
                            JsonSerializer.Deserialize<YouTubeCookieExportResult>(payload);
                        if (result is not null)
                        {
                            lock (youtubeCookieRequestLock)
                                youtubeCookieRequest?.TrySetResult(result);
                        }
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            if (!disposed)
                LastError = exception is IOException or EndOfStreamException or ObjectDisposedException
                    ? "The browser host connection closed unexpectedly."
                    : $"The browser host sent invalid data: {exception.Message}";
        }
    }

    private unsafe void SubmitAudio(byte[] pcm)
    {
        if (pcm.Length == 0 || pcm.Length % 4 != 0) return;
        lastAudioTick = Stopwatch.GetTimestamp();
        fixed (byte* data = pcm)
        {
            localAudio?.Submit((IntPtr)data, pcm.Length / 4);
            if (!privacyMode) encoder?.SubmitAudio((IntPtr)data, pcm.Length / 4);
        }
    }

    internal void OnFrameworkUpdate()
    {
        byte[]? frame;
        lock (frameLock) { frame = pendingFrame; pendingFrame = null; }
        if (frame is not null)
        {
            // The immediate context belongs to FFXIV and may only be touched from
            // the game's Present thread. Browser pages can produce frames almost
            // continuously, so uploading here on Dalamud's framework thread races
            // the NVIDIA driver and can terminate the game.
            DxHandler.RunOnRenderThread(renderKey, () => UploadFrame(frame));
        }
    }

    private unsafe void UploadFrame(byte[] frame)
    {
        if (disposed) return;
        frameScaler.Blit(frame);
    }

    private unsafe void BroadcastPump(GameBroadcastEncoder activeEncoder, CancellationToken token)
    {
        var frameInterval = Stopwatch.Frequency / FramesPerSecond;
        var nextVideoTick = Stopwatch.GetTimestamp();
        while (!token.IsCancellationRequested && activeEncoder.IsRunning)
        {
            var now = Stopwatch.GetTimestamp();
            if (now >= nextVideoTick)
            {
                lock (frameLock)
                {
                    var selectedFrame = privacyMode ? privacyFrame : broadcastFrame;
                    fixed (byte* data = selectedFrame)
                        activeEncoder.SubmitBgraVideoFrame((IntPtr)data, Width, Height, Width * 4);
                }
                lastVideoTick = now;
                // Do not send a burst of old frames after a scheduler stall. The
                // latest browser image begins a fresh real-time interval instead.
                nextVideoTick = now + frameInterval;
            }

            // CEF stops producing packets for silent pages. Keep FFmpeg's audio
            // clock moving, but allow ample time for normal packet scheduling.
            if (now - lastAudioTick < Stopwatch.Frequency / 4)
            {
                lastSilenceTick = now;
            }
            else
            {
                var frames = (int)Math.Min(AudioSampleRate / 20,
                    (now - lastSilenceTick) * AudioSampleRate / Stopwatch.Frequency);
                if (frames >= 240)
                {
                    var silence = ArrayPool<short>.Shared.Rent(frames * 2);
                    try
                    {
                        Array.Clear(silence, 0, frames * 2);
                        fixed (short* data = silence) activeEncoder.SubmitAudio((IntPtr)data, frames);
                    }
                    finally { ArrayPool<short>.Shared.Return(silence); }
                    lastSilenceTick = now;
                }
            }

            var remainingTicks = nextVideoTick - Stopwatch.GetTimestamp();
            if (remainingTicks > Stopwatch.Frequency / 500)
                Thread.Sleep((int)Math.Max(1, remainingTicks * 1000 / Stopwatch.Frequency - 1));
            else
                Thread.Yield();
        }
    }

    internal void Navigate(string address) { LastError = null; Send(10, Encoding.UTF8.GetBytes(address)); }
    internal void Back() => Send(11);
    internal void Forward() => Send(12);
    internal void Reload() => Send(13);
    internal void StopLoading() => Send(14);
    internal void SetVolume(int value) => localAudio?.SetVolume(Math.Clamp(value, 0, 200));
    internal void SetPrivacyMode(bool enabled)
    {
        privacyMode = enabled;
        if (enabled) encoder?.DiscardPendingAudio();
    }
    internal void SendMouseMove(int x, int y, bool leave = false) => SendBinary(15, w => { w.Write(x); w.Write(y); });
    internal void SendMouseClick(int x, int y, int button, bool mouseUp) => SendBinary(16, w => { w.Write(x); w.Write(y); w.Write(button); w.Write(mouseUp); });
    internal void SendMouseWheel(int x, int y, int dx, int dy) => SendBinary(17, w => { w.Write(x); w.Write(y); w.Write(dx); w.Write(dy); });
    internal void SendKey(int code, bool up, int modifiers) => SendBinary(18, w => { w.Write(code); w.Write(up); w.Write(modifiers); });
    internal void SendCharacter(char value, int modifiers) => SendBinary(19, w => { w.Write(value); w.Write(modifiers); });
    internal void Focus(bool focused) => SendBinary(20, w => w.Write(focused));

    internal async Task<YouTubeCookieExportResult> RequestYouTubeCookiesAsync(
        CancellationToken token = default)
    {
        TaskCompletionSource<YouTubeCookieExportResult> request;

        lock (youtubeCookieRequestLock)
        {
            if (youtubeCookieRequest is not null)
            {
                return new YouTubeCookieExportResult(
                    false,
                    null,
                    "A YouTube session check is already in progress.");
            }

            request =
                new TaskCompletionSource<YouTubeCookieExportResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            youtubeCookieRequest =
                request;
        }

        try
        {
            Send(22);
            return await request.Task
                .WaitAsync(
                    TimeSpan.FromSeconds(20),
                    token)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new YouTubeCookieExportResult(
                false,
                null,
                "The browser took too long to return the YouTube session. Try again.");
        }
        finally
        {
            lock (youtubeCookieRequestLock)
            {
                if (ReferenceEquals(youtubeCookieRequest, request))
                    youtubeCookieRequest = null;
            }
        }
    }

    private void SendBinary(byte type, Action<BinaryWriter> write)
    {
        using var memory = new MemoryStream();
        using (var payloadWriter = new BinaryWriter(memory, Encoding.UTF8, true)) write(payloadWriter);
        Send(type, memory.ToArray());
    }
    private void Send(byte type, byte[]? payload = null)
    {
        payload ??= [];
        lock (writeLock)
        {
            if (disposed || writer is null) return;
            try { writer.Write(type); writer.Write(payload.Length); writer.Write(payload); writer.Flush(); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                if (!disposed) LastError = "The browser host connection closed unexpectedly.";
            }
        }
    }

    internal bool StartBroadcast(string ffmpegPath, string publishUrl)
    {
        if (IsBroadcasting) return true;
        var next = new GameBroadcastEncoder();
        if (!next.Start(ffmpegPath, publishUrl, Width, Height, FramesPerSecond, AudioSampleRate,
                maxPendingAudioPackets: 12, ffmpegAudioQueuePackets: 8, sourceName: "Web browser"))
        { next.Dispose(); LastError = "FFmpeg failed to start the browser broadcast."; return false; }
        encoder = next; lastVideoTick = 0; lastAudioTick = 0; lastSilenceTick = Stopwatch.GetTimestamp();
        broadcastCancellation = new CancellationTokenSource();
        broadcastTask = Task.Run(() => BroadcastPump(next, broadcastCancellation.Token));
        return true;
    }
    internal void StopBroadcast()
    {
        var cancellationToStop = broadcastCancellation;
        var taskToStop = broadcastTask;
        broadcastCancellation = null;
        broadcastTask = null;
        try { cancellationToStop?.Cancel(); } catch { }
        try { taskToStop?.Wait(500); } catch { }
        encoder?.Dispose();
        encoder = null;
        try
        {
            if (taskToStop is { IsCompleted: false } &&
                !taskToStop.Wait(1500))
            {
                AepLog.Warning(
                    "[BROWSER] Broadcast pump did not stop within 2 seconds.");
            }
        }
        catch { }
        try { cancellationToStop?.Dispose(); } catch { }
    }

    private static byte[] CreatePrivacyFrame()
    {
        using var image = new Image<Bgra32>(Width, Height, new Bgra32(5, 7, 14, 255));
        try
        {
            var fontPath = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty,
                "Fonts", "Inter-SemiBold.ttf");
            var collection = new FontCollection();
            var font = collection.Add(fontPath).CreateFont(34f);
            const string message = "Privacy Mode Enabled";
            var size = TextMeasurer.MeasureSize(message, new TextOptions(font));
            image.Mutate(context => context.DrawText(message, font, SixLabors.ImageSharp.Color.White,
                new PointF((Width - size.Width) / 2f, (Height - size.Height) / 2f)));
        }
        catch { }
        var pixels = new byte[Width * Height * 4];
        image.CopyPixelDataTo(pixels);
        return pixels;
    }

    public void Dispose()
    {
        if (disposed) return;
        StopBroadcast();
        DxHandler.CancelRenderThreadWork(renderKey);
        try { Send(21); } catch { }
        disposed = true;
        lock (youtubeCookieRequestLock)
        {
            youtubeCookieRequest?.TrySetCanceled();
            youtubeCookieRequest = null;
        }
        cancellation?.Cancel();
        try { pipe?.Dispose(); } catch { }
        try { audioPipe?.Dispose(); } catch { }

        try
        {
            var readers =
                new[] { hostMessagesTask, hostAudioTask }
                    .Where(task => task is not null)
                    .Cast<Task>()
                    .ToArray();

            if (readers.Length > 0 &&
                !Task.WaitAll(readers, TimeSpan.FromSeconds(2)))
            {
                AepLog.Warning(
                    "[BROWSER] Host pipe readers did not stop within 2 seconds.");
            }
        }
        catch (Exception exception)
        {
            AepLog.Debug(
                $"[BROWSER] Host reader cleanup warning: {exception.Message}");
        }

        hostMessagesTask = null;
        hostAudioTask = null;
        try
        {
            if (process is { HasExited: false } &&
                !process.WaitForExit(1500))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }
        }
        catch { }
        try { process?.Dispose(); } catch { }
        try { reader?.Dispose(); } catch { }
        try { audioReader?.Dispose(); } catch { }
        try { writer?.Dispose(); } catch { }
        try { cancellation?.Dispose(); } catch { }
        try { localAudio?.Dispose(); } catch { }
        try { frameScaler.Dispose(); } catch { }
        lock (frameLock) pendingFrame = null;
    }

    private sealed record BrowserState(string Address, string Title, bool IsLoading, bool CanGoBack, bool CanGoForward, bool IsReady);
}

internal sealed record YouTubeCookieExportResult(
    bool Success,
    string? Cookies,
    string? Error);
