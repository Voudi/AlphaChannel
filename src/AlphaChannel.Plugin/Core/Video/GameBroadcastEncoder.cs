using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Encodes raw emulator video frames with FFmpeg and publishes them
/// to an RTMP endpoint.
///
/// Version 1 is intentionally video-only. Audio will be added after
/// the basic emulator -> FFmpeg -> RTMP path has been proven.
/// </summary>
internal sealed class GameBroadcastEncoder : IDisposable
{
    private readonly object _frameLock =
        new();

    private Process? _process;
    private Stream? _ffmpegInput;
    private NamedPipeServerStream? _ffmpegAudioPipe;

    private string? _ffmpegAudioPipeName;

    private Thread? _writerThread;
    private CancellationTokenSource? _cancel;

    private byte[]? _pendingFrame;

    private readonly Queue<byte[]> _pendingAudio =
    new();

    private int _frameWidth;
    private int _frameHeight;
    private int _frameBytes;

    // Temporary framebuffer diagnostics.

    private volatile bool _running;
    private volatile bool _stopping;
    private bool _disposed;

    internal bool IsRunning =>
        _running &&
        _process is not null &&
        !_process.HasExited;


    /// <summary>
    /// Starts FFmpeg and prepares it to receive tightly-packed
    /// RGB565 little-endian frames through stdin.
    /// </summary>
    internal bool Start(
        string ffmpegPath,
        string publishUrl,
        int width,
        int height,
        double fps,
        int audioSampleRate)
    {
        if (_disposed)
        {
            AepLog.Error(
                "[GAME-BROADCAST] Cannot start after disposal.");

            return false;
        }

        if (_running)
        {
            AepLog.Warning(
                "[GAME-BROADCAST] Broadcast is already running.");

            return true;
        }

        if (string.IsNullOrWhiteSpace(ffmpegPath) ||
            !File.Exists(ffmpegPath))
        {
            AepLog.Error(
                $"[GAME-BROADCAST] FFmpeg was not found: {ffmpegPath}");

            return false;
        }

        if (string.IsNullOrWhiteSpace(publishUrl))
        {
            AepLog.Error(
                "[GAME-BROADCAST] Publish URL was empty.");

            return false;
        }

        if (width <= 0 ||
            height <= 0)
        {
            AepLog.Error(
                $"[GAME-BROADCAST] Invalid video size: {width}x{height}");

            return false;
        }

        if (fps <= 1)
        {
            AepLog.Error(
                $"[GAME-BROADCAST] Invalid frame rate: {fps}");

            return false;
        }
        if (audioSampleRate <= 0)
        {
            AepLog.Error(
                $"[GAME-BROADCAST] Invalid audio sample rate: {audioSampleRate}");

            return false;
        }

        Stop();

        _frameWidth =
            width;

        _frameHeight =
            height;

        _frameBytes =
           checked(
               width *
               height *
               4);

        try
        {
            //
            // IMPORTANT:
            //
            // Do not log Arguments or publishUrl here.
            // The RTMP URL contains the user's stream secret.
            //

            _ffmpegAudioPipeName =
    $"AlphaChannelAudio_{Guid.NewGuid():N}";

            _ffmpegAudioPipe =
                new NamedPipeServerStream(
                    _ffmpegAudioPipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

            var arguments =
                string.Join(
                    " ",
                    "-hide_banner",
                    "-loglevel warning",

// Raw emulator video from stdin.
//
// The libretro framebuffer is converted to BGRA32 inside
// SubmitVideoFrame before being handed to FFmpeg. Using an
// explicit 32-bit format avoids ambiguity around the native
// RGB565/BGR565 channel layout.
"-f rawvideo",
"-pix_fmt bgra",
$"-video_size {width}x{height}",
$"-framerate {fps.ToString(
    "0.######",
    System.Globalization.CultureInfo.InvariantCulture)}",
"-i pipe:0",

"-f s16le",
$"-ar {audioSampleRate}",
"-ac 2",
$"-i \\\\.\\pipe\\{_ffmpegAudioPipeName}",

// Explicitly map the video and audio inputs into the output.
"-map",
"0:v:0",

"-map",
"1:a:0",

// Encode video to H.264.
"-c:v",
"h264",

"-tune",
"zerolatency",

"-pix_fmt",
"yuv420p",

"-c:a",
"aac",

"-b:a",
"128k",

// Encode AAC at a standard broadly-supported sample rate.
// FFmpeg resamples the emulator PCM when necessary.
"-ar",
"48000",
                    // Keep keyframes frequent for live playback.
                    $"-g {Math.Max(
                        1,
                        (int)Math.Round(fps * 2.0))}",
                    "-keyint_min 1",
                    "-sc_threshold 0",

                    // RTMP uses FLV as the transport container.
                    "-f flv",

                    QuoteArgument(
                        publishUrl));

            var startInfo =
                new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false
                };

            _process =
                new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

            _process.Exited +=
                OnFFmpegExited;

            AepLog.Info(
                $"[GAME-BROADCAST] Starting FFmpeg for {width}x{height} @ {fps:0.###}fps.");

            //
            // Do NOT log the complete FFmpeg command line here because
            // it contains the RTMP stream key.
            //

            if (!_process.Start())
            {
                AepLog.Error(
                    "[GAME-BROADCAST] FFmpeg Process.Start returned false.");

                CleanupProcess();

                return false;
            }

            _ffmpegInput =
                _process.StandardInput.BaseStream;

            _cancel =
                new CancellationTokenSource();

            _running =
                true;

            //
            // Drain stderr asynchronously so FFmpeg can never block
            // because its stderr pipe filled up.
            //

            _ =
                Task.Run(
                    () => ReadFFmpegErrorsAsync(
                        _process,
                        _cancel.Token));

            //
            // Emulator callbacks never write to FFmpeg themselves.
            // This worker performs all potentially-blocking pipe I/O.
            //

            _writerThread =
                new Thread(
                    WriterLoop)
                {
                    IsBackground = true,
                    Name = "alpha-game-broadcast"
                };

            _writerThread.Start();

            //
            // Audio is written through a separate named pipe because stdin
            // is already used by the raw video stream.
            //

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        var audioPipe =
                            _ffmpegAudioPipe;

                        if (audioPipe is null)
                        {
                            return;
                        }

                        await audioPipe.WaitForConnectionAsync();

                        AepLog.Info(
                            "[GAME-BROADCAST] Audio writer started.");

                        while (_running)
                        {
                            byte[]? audio =
      null;

                            lock (_frameLock)
                            {
                                if (_pendingAudio.Count > 0)
                                {
                                    audio =
                                        _pendingAudio.Dequeue();
                                }
                            }

                            if (audio is null)
                            {
                                await Task.Delay(
                                    2);

                                continue;
                            }

                            await audioPipe.WriteAsync(
                                audio,
                                0,
                                audio.Length);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal during shutdown.
                    }
                    catch (IOException exception)
                    {
                        if (_running)
                        {
                            AepLog.Warning(
                                $"[GAME-BROADCAST] Audio pipe stopped: {exception.Message}");
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // Normal during shutdown.
                    }
                    catch (Exception exception)
                    {
                        if (_running)
                        {
                            AepLog.Warning(
                                $"[GAME-BROADCAST] Audio writer stopped: {exception.Message}");
                        }
                    }
                });

            AepLog.Info(
                "[GAME-BROADCAST] FFmpeg process started.");

            return true;
        }
        catch (Exception exception)
        {
            AepLog.Error(
                $"[GAME-BROADCAST] Failed to start FFmpeg: {exception}");

            Stop();

            return false;
        }
    }

    internal void SubmitAudio(
    IntPtr data,
    int frames)
    {
        if (!_running ||
            data == IntPtr.Zero ||
            frames <= 0)
        {
            return;
        }

        try
        {
            // Libretro supplies signed 16-bit stereo PCM:
            // 2 channels * 2 bytes per sample.
            int bytes =
                frames * 4;

            var audio =
                new byte[bytes];

            Marshal.Copy(
                data,
                audio,
                0,
                bytes);

            lock (_frameLock)
            {
                _pendingAudio.Enqueue(
                    audio);

                // Keep the emulator thread non-blocking if FFmpeg ever
                // falls badly behind. This is intentionally generous;
                // normal operation should never reach this limit.
                while (_pendingAudio.Count > 256)
                {
                    _pendingAudio.Dequeue();
                }
            }
        }
        catch (Exception exception)
        {
            if (_running)
            {
                AepLog.Warning(
                    $"[GAME-BROADCAST] Audio submission failed: {exception.Message}");
            }
        }
    }


    /// <summary>
    /// Submits one RGB565 emulator frame.
    ///
    /// The libretro framebuffer pointer is only valid for the duration
    /// of its callback, so the frame must be copied immediately.
    ///
    /// The input pitch may contain padding. FFmpeg is given tightly
    /// packed rows, so each row is copied separately.
    /// </summary>
    internal unsafe void SubmitVideoFrame(
    IntPtr data,
    int width,
    int height,
    int pitch)
    {
        if (!_running ||
            data == IntPtr.Zero)
        {
            return;
        }

        if (width != _frameWidth ||
            height != _frameHeight)
        {
            return;
        }

        //
        // Gambatte/libretro is supplying a 16-bit framebuffer.
        //
        // The source pitch can be larger than width * 2:
        //
        //   160 pixel image = 320 useful bytes
        //   Gambatte pitch   = 512 bytes
        //
        // Therefore we walk each source row using pitch rather
        // than treating the native framebuffer as tightly packed.
        //

        const int sourceBytesPerPixel =
            2;

        const int destinationBytesPerPixel =
            4;

        var sourceRowBytes =
            width *
            sourceBytesPerPixel;

        if (pitch <
            sourceRowBytes)
        {
            return;
        }

        var destinationRowBytes =
            width *
            destinationBytesPerPixel;

        var requiredBytes =
            destinationRowBytes *
            height;

        if (requiredBytes !=
            _frameBytes)
        {
            return;
        }

        byte[]? frame =
            null;

        try
        {
            frame =
                ArrayPool<byte>.Shared.Rent(
                    requiredBytes);

            fixed (byte* destinationBase =
                   frame)
            {
                var sourceBase =
                    (byte*)data;

                for (var y = 0;
                     y < height;
                     y++)
                {
                    var sourceRow =
                        sourceBase +
                        (y * pitch);

                    var destinationRow =
                        destinationBase +
                        (y * destinationRowBytes);

                    for (var x = 0;
                         x < width;
                         x++)
                    {
                        //
                        // Read one little-endian 16-bit pixel.
                        //

                        var sourceOffset =
                            x * 2;

                        ushort pixel =
                            (ushort)(
                                sourceRow[sourceOffset] |
                                (sourceRow[sourceOffset + 1] << 8));


                        //
                        // The working D3D path treats the framebuffer as
                        // B5G6R5_UNorm:
                        //
                        // bits  0-4  = red
                        // bits  5-10 = green
                        // bits 11-15 = blue
                        //

                        var blue5 =
                            pixel &
                            0x1F;

                        var green6 =
                            (pixel >> 5) &
                            0x3F;

                        var red5 =
                            (pixel >> 11) &
                            0x1F;


                        //
                        // Expand 5/6-bit channels to full 8-bit channels.
                        //
                        // Bit replication gives the complete 0-255 range
                        // without needing floating-point conversion.
                        //

                        var red8 =
                            (byte)(
                                (red5 << 3) |
                                (red5 >> 2));

                        var green8 =
                            (byte)(
                                (green6 << 2) |
                                (green6 >> 4));

                        var blue8 =
                            (byte)(
                                (blue5 << 3) |
                                (blue5 >> 2));


                        //
                        // FFmpeg input format is BGRA:
                        //
                        // byte 0 = blue
                        // byte 1 = green
                        // byte 2 = red
                        // byte 3 = alpha
                        //

                        var destinationOffset =
                            x * 4;

                        destinationRow[destinationOffset] =
                            blue8;

                        destinationRow[destinationOffset + 1] =
                            green8;

                        destinationRow[destinationOffset + 2] =
                            red8;

                        destinationRow[destinationOffset + 3] =
                            255;


                    }
                }
            }


            //
            // Hand every converted emulator frame to the FFmpeg writer.
            //
            // If FFmpeg is still busy with the previous frame, replace that
            // pending frame with the newest one rather than blocking Gambatte.
            //

            lock (_frameLock)
            {
                if (_pendingFrame is not null)
                {
                    ArrayPool<byte>.Shared.Return(
                        _pendingFrame);
                }

                _pendingFrame =
                    frame;

                frame =
                    null;

                Monitor.Pulse(
                    _frameLock);
            }
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[GAME-BROADCAST] Frame submission failed: {exception.Message}");
        }
        finally
        {
            if (frame is not null)
            {
                ArrayPool<byte>.Shared.Return(
                    frame);
            }
        }
    }


    private void WriterLoop()
    {
        try
        {
            while (_running)
            {
                byte[]? frame =
                    null;

                lock (_frameLock)
                {
                    while (_running &&
                           _pendingFrame is null)
                    {
                        Monitor.Wait(
                            _frameLock,
                            100);
                    }

                    if (!_running)
                    {
                        break;
                    }

                    frame =
                        _pendingFrame;

                    _pendingFrame =
                        null;
                }

                if (frame is null)
                {
                    continue;
                }

                try
                {
                    var input =
                        _ffmpegInput;

                    if (input is null)
                    {
                        break;
                    }

                    input.Write(
                        frame,
                        0,
                        _frameBytes);
                }
                catch (IOException exception)
                {
                    if (_running)
                    {
                        AepLog.Error(
                            $"[GAME-BROADCAST] FFmpeg video pipe closed: {exception.Message}");
                    }

                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    if (_running)
                    {
                        AepLog.Error(
                            $"[GAME-BROADCAST] Video writer failed: {exception}");
                    }

                    break;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(
                        frame);
                }
            }
        }
        finally
        {
            _running =
                false;
        }
    }


    private static async Task ReadFFmpegErrorsAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   !process.HasExited)
            {
                var line =
                    await process.StandardError.ReadLineAsync();

                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    AepLog.Warning(
                        $"[FFMPEG-BROADCAST] {line}");
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Normal during shutdown.
        }
        catch (InvalidOperationException)
        {
            // Process already exited/disposed.
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                AepLog.Warning(
                    $"[GAME-BROADCAST] FFmpeg stderr reader stopped: {exception.Message}");
            }
        }
    }


    private void OnFFmpegExited(
     object? sender,
     EventArgs args)
    {
        //
        // Stop() deliberately shuts FFmpeg down.
        // Do not report that as a broadcast failure.
        //

        if (_stopping ||
            !_running)
        {
            return;
        }


        int? exitCode =
            null;

        try
        {
            if (sender is Process process)
            {
                exitCode =
                    process.ExitCode;
            }
        }
        catch
        {
            // Exit code is diagnostic only.
        }


        if (exitCode.HasValue)
        {
            AepLog.Warning(
                $"[GAME-BROADCAST] FFmpeg exited unexpectedly. Exit code: {exitCode.Value}");
        }
        else
        {
            AepLog.Warning(
                "[GAME-BROADCAST] FFmpeg exited unexpectedly.");
        }


        //
        // Immediately expose the failed state to the UI and stop
        // accepting emulator frames/audio.
        //

        _running =
            false;

        _cancel?.Cancel();


        lock (_frameLock)
        {
            Monitor.PulseAll(
                _frameLock);
        }


        //
        // Never perform the full cleanup directly inside Process.Exited.
        //
        // Stop() may wait for the writer thread and dispose the Process,
        // so move that work away from the Process event callback.
        //

        _ =
            Task.Run(
                () =>
                {
                    try
                    {
                        Stop();
                    }
                    catch (Exception exception)
                    {
                        AepLog.Warning(
                            $"[GAME-BROADCAST] Cleanup after unexpected FFmpeg exit failed: {exception.Message}");
                    }
                });
    }


    internal void Stop()
    {
        if (_stopping)
        {
            return;
        }

        _stopping =
            true;

        try
        {
            _running =
                false;

            _cancel?.Cancel();

            lock (_frameLock)
            {
                Monitor.PulseAll(
                    _frameLock);
            }


            if (_writerThread is not null &&
                _writerThread != Thread.CurrentThread)
            {
                try
                {
                    _writerThread.Join(
                        2000);
                }
                catch
                {
                    // Best-effort shutdown.
                }
            }

            _writerThread =
                null;


            lock (_frameLock)
            {
                if (_pendingFrame is not null)
                {
                    ArrayPool<byte>.Shared.Return(
                        _pendingFrame);

                    _pendingFrame =
                        null;
                }

                _pendingAudio.Clear();
            }


            //
            // Closing the audio pipe releases both our writer and
            // FFmpeg's named-pipe input if either side is still waiting.
            //

            try
            {
                _ffmpegAudioPipe?.Dispose();
            }
            catch
            {
                // Best effort.
            }

            _ffmpegAudioPipe =
                null;

            _ffmpegAudioPipeName =
                null;


            //
            // Closing stdin gives FFmpeg EOF and allows it to finish the
            // FLV stream cleanly where possible.
            //

            try
            {
                _ffmpegInput?.Flush();
            }
            catch
            {
                // Best effort.
            }

            try
            {
                _ffmpegInput?.Dispose();
            }
            catch
            {
                // Best effort.
            }

            _ffmpegInput =
                null;


            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        if (!_process.WaitForExit(
                                2000))
                        {
                            _process.Kill(
                                entireProcessTree: true);

                            _process.WaitForExit(
                                2000);
                        }
                    }
                }
                catch
                {
                    // Best-effort process cleanup.
                }
            }


            CleanupProcess();


            try
            {
                _cancel?.Dispose();
            }
            catch
            {
                // Best effort.
            }

            _cancel =
                null;


            AepLog.Info(
                "[GAME-BROADCAST] Broadcast stopped.");
        }
        finally
        {
            _stopping =
                false;
        }
    }


    private void CleanupProcess()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            _process.Exited -=
                OnFFmpegExited;
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _process.Dispose();
        }
        catch
        {
            // Best effort.
        }

        _process =
            null;
    }


    private static string QuoteArgument(
        string value)
    {
        //
        // The publish URL contains '&' / '?' characters and potentially
        // other command-line-sensitive characters. ProcessStartInfo is
        // not using a shell, but quoting still keeps the URL one argument.
        //

        return "\"" +
               value.Replace(
                   "\"",
                   "\\\"") +
               "\"";
    }


    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();

        _disposed =
            true;

        GC.SuppressFinalize(
            this);
    }
}