using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Decodes a live network audio stream with the bundled FFmpeg process and
/// exposes stable PCM to libmpv over a loopback-only HTTP connection.
/// </summary>
internal sealed class LiveAudioFfmpegRelay : IDisposable
{
    private readonly Process process;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task relayTask;
    private bool disposed;

    private LiveAudioFfmpegRelay(
        Process process,
        TcpListener listener,
        string playbackUrl)
    {
        this.process = process;
        this.listener = listener;
        PlaybackUrl = playbackUrl;
        relayTask = Task.Run(ServeAsync);
    }

    internal string PlaybackUrl { get; }

    internal static LiveAudioFfmpegRelay? TryStart(
        string ffmpegPath,
        string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) ||
            !File.Exists(ffmpegPath) ||
            string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        Process? process = null;
        TcpListener? listener = null;

        try
        {
            // Own the loopback listener here instead of asking FFmpeg to create
            // it after connecting upstream. This removes the startup race.
            listener = new TcpListener(
                IPAddress.Loopback,
                0);
            listener.Start(1);

            var port =
                ((IPEndPoint)listener.LocalEndpoint).Port;
            var playbackUrl =
                $"http://127.0.0.1:{port}/alphachannel-live.wav";

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = true
            };

            AddArgument(startInfo, "-hide_banner");
            AddArgument(startInfo, "-loglevel");
            AddArgument(startInfo, "warning");
            AddArgument(startInfo, "-nostdin");
            AddArgument(startInfo, "-reconnect");
            AddArgument(startInfo, "1");
            AddArgument(startInfo, "-reconnect_streamed");
            AddArgument(startInfo, "1");
            AddArgument(startInfo, "-reconnect_delay_max");
            AddArgument(startInfo, "2");
            AddArgument(startInfo, "-i");
            AddArgument(startInfo, sourceUrl);
            AddArgument(startInfo, "-map");
            AddArgument(startInfo, "0:a:0");
            AddArgument(startInfo, "-vn");
            AddArgument(startInfo, "-af");
            AddArgument(
                startInfo,
                "aresample=48000:async=1000:first_pts=0");
            AddArgument(startInfo, "-ac");
            AddArgument(startInfo, "2");
            AddArgument(startInfo, "-ar");
            AddArgument(startInfo, "48000");
            AddArgument(startInfo, "-c:a");
            AddArgument(startInfo, "pcm_s16le");
            AddArgument(startInfo, "-f");
            AddArgument(startInfo, "wav");
            AddArgument(startInfo, "pipe:1");

            process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.ErrorDataReceived +=
                (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        AepLog.Warning(
                            $"[LIVE-AUDIO-RELAY] {args.Data}");
                    }
                };

            if (!process.Start())
            {
                process.Dispose();
                listener.Stop();
                return null;
            }

            process.BeginErrorReadLine();

            AepLog.Info(
                $"[LIVE-AUDIO-RELAY] Stable PCM relay started on 127.0.0.1:{port}.");

            return new LiveAudioFfmpegRelay(
                process,
                listener,
                playbackUrl);
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[LIVE-AUDIO-RELAY] Could not start: {exception.Message}");

            try
            {
                listener?.Stop();

                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort cleanup after a failed start.
            }

            process?.Dispose();
            return null;
        }
    }

    private async Task ServeAsync()
    {
        try
        {
            using var client =
                await listener.AcceptTcpClientAsync(
                    cancellation.Token);
            using var network = client.GetStream();

            await ReadHttpRequestAsync(
                network,
                cancellation.Token);

            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: audio/wav\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n");

            await network.WriteAsync(
                header,
                cancellation.Token);

            await process.StandardOutput.BaseStream.CopyToAsync(
                network,
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Normal shutdown while the listener or stream is closing.
        }
        catch (IOException exception) when (disposed)
        {
            AepLog.Debug(
                $"[LIVE-AUDIO-RELAY] Stream closed during cleanup: {exception.Message}");
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[LIVE-AUDIO-RELAY] Loopback stream failed: {exception.Message}");
        }
    }

    private static async Task ReadHttpRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var terminator = new byte[]
        {
            (byte)'\r',
            (byte)'\n',
            (byte)'\r',
            (byte)'\n'
        };
        var matched = 0;

        while (matched < terminator.Length)
        {
            var read = await stream.ReadAsync(
                buffer,
                cancellationToken);

            if (read == 0)
            {
                throw new IOException(
                    "The local player disconnected before sending its request.");
            }

            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == terminator[matched])
                {
                    matched++;

                    if (matched == terminator.Length)
                    {
                        return;
                    }
                }
                else
                {
                    matched = buffer[index] == terminator[0]
                        ? 1
                        : 0;
                }
            }
        }
    }

    private static void AddArgument(
        ProcessStartInfo startInfo,
        string argument)
    {
        startInfo.ArgumentList.Add(argument);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();

        try
        {
            listener.Stop();

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }

            relayTask.Wait(500);
        }
        catch (Exception exception)
        {
            AepLog.Debug(
                $"[LIVE-AUDIO-RELAY] Cleanup warning: {exception.Message}");
        }
        finally
        {
            process.Dispose();
            cancellation.Dispose();
        }
    }
}
