using System.Diagnostics;
using System.Globalization;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Runs a second, muted-to-the-host FFmpeg reader over the current local
/// video file and publishes it to AlphaChannel's RTMP relay.
///
/// The local MPV renderer remains responsible for what the host sees and
/// hears. FFmpeg is only started while a Watch Party viewer is present.
/// </summary>
internal sealed class LocalVideoBroadcastEncoder : IDisposable
{
    private readonly BroadcastDiagnosticsTracker diagnostics = new();
    private Process? process;
    private bool stopping;
    private bool disposed;

    internal string? LastError
    {
        get;
        private set;
    }

    internal BroadcastDiagnosticsSnapshot Diagnostics => diagnostics.Snapshot;

    internal bool IsRunning
    {
        get
        {
            var current =
                process;

            if (current is null)
            {
                return false;
            }

            try
            {
                return !current.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    internal bool Start(
        string ffmpegPath,
        string sourcePath,
        string publishUrl,
        double startPositionSeconds)
    {
        if (disposed)
        {
            LastError =
                "The local video broadcaster has already been disposed.";

            return false;
        }

        Stop();

        LastError =
            null;

        if (string.IsNullOrWhiteSpace(
                ffmpegPath) ||
            !File.Exists(
                ffmpegPath))
        {
            LastError =
                "FFmpeg is not installed yet. Try again in a few seconds.";

            return false;
        }

        if (string.IsNullOrWhiteSpace(
                sourcePath) ||
            !File.Exists(
                sourcePath))
        {
            LastError =
                "The selected local video file could not be found.";

            return false;
        }

        if (string.IsNullOrWhiteSpace(
                publishUrl))
        {
            LastError =
                "The relay publish address is unavailable.";

            return false;
        }

        stopping =
            false;

        diagnostics.Start("Local video");

        try
        {
            var startInfo =
                new ProcessStartInfo
                {
                    FileName =
                        ffmpegPath,

                    UseShellExecute =
                        false,

                    CreateNoWindow =
                        true,

                    RedirectStandardInput =
                        false,

                    RedirectStandardOutput =
                        false,

                    RedirectStandardError =
                        true
                };

            //
            // Do not replace this with one assembled argument string.
            // ArgumentList safely handles spaces in local filenames.
            //
            // Never log ArgumentList: publishUrl contains the private
            // AlphaChannel stream key.
            //

            startInfo.ArgumentList.Add(
                "-hide_banner");

            startInfo.ArgumentList.Add(
                "-loglevel");

            startInfo.ArgumentList.Add(
                "warning");

            startInfo.ArgumentList.Add("-nostats");
            startInfo.ArgumentList.Add("-stats_period");
            startInfo.ArgumentList.Add("2");
            startInfo.ArgumentList.Add("-progress");
            startInfo.ArgumentList.Add("pipe:2");

            startInfo.ArgumentList.Add(
                "-nostdin");

            //
            // Seek before opening the input so a viewer joining midway
            // through a long file does not require FFmpeg to decode from
            // the beginning.
            //

            startInfo.ArgumentList.Add(
                "-ss");

            startInfo.ArgumentList.Add(
                Math.Max(
                        0d,
                        startPositionSeconds)
                    .ToString(
                        "0.###",
                        CultureInfo.InvariantCulture));

            //
            // Read the file at normal playback speed rather than
            // transcoding and uploading it as quickly as possible.
            //

            startInfo.ArgumentList.Add(
                "-re");

            startInfo.ArgumentList.Add(
                "-i");

            startInfo.ArgumentList.Add(
                sourcePath);

            startInfo.ArgumentList.Add(
                "-map");

            startInfo.ArgumentList.Add(
                "0:v:0");

            startInfo.ArgumentList.Add(
                "-map");

            startInfo.ArgumentList.Add(
                "0:a:0?");

            startInfo.ArgumentList.Add(
                "-c:v");

            startInfo.ArgumentList.Add(
                "h264");

            startInfo.ArgumentList.Add(
                "-preset");

            startInfo.ArgumentList.Add(
                "veryfast");

            startInfo.ArgumentList.Add(
                "-tune");

            startInfo.ArgumentList.Add(
                "zerolatency");

            startInfo.ArgumentList.Add(
                "-pix_fmt");

            startInfo.ArgumentList.Add(
                "yuv420p");

            startInfo.ArgumentList.Add(
                "-c:a");

            startInfo.ArgumentList.Add(
                "aac");

            startInfo.ArgumentList.Add(
                "-b:a");

            startInfo.ArgumentList.Add(
                "128k");

            startInfo.ArgumentList.Add(
                "-ar");

            startInfo.ArgumentList.Add(
                "48000");

            //
            // Frequent keyframes help newly joined HLS viewers begin
            // displaying the stream sooner.
            //

            startInfo.ArgumentList.Add(
                "-g");

            startInfo.ArgumentList.Add(
                "60");

            startInfo.ArgumentList.Add(
                "-keyint_min");

            startInfo.ArgumentList.Add(
                "1");

            startInfo.ArgumentList.Add(
                "-sc_threshold");

            startInfo.ArgumentList.Add(
                "0");

            startInfo.ArgumentList.Add(
                "-f");

            startInfo.ArgumentList.Add(
                "flv");

            startInfo.ArgumentList.Add(
                publishUrl);

            var newProcess =
                new Process
                {
                    StartInfo =
                        startInfo,

                    EnableRaisingEvents =
                        true
                };

            newProcess.Exited +=
                (_, _) =>
                {
                    if (!stopping)
                    {
                        int? code = null;
                        try { code = newProcess.ExitCode; } catch { }

                        if (code == 0)
                        {
                            diagnostics.Stop(0);
                            return;
                        }

                        LastError =
                            "The local video relay encoder stopped unexpectedly.";

                        AepLog.Warning(
                            "[LOCAL-VIDEO-BROADCAST] FFmpeg stopped unexpectedly.");

                        diagnostics.Failed("FFmpeg stopped unexpectedly.", code);
                    }
                };

            if (!newProcess.Start())
            {
                newProcess.Dispose();

                LastError =
                    "FFmpeg could not be started.";

                diagnostics.Failed("FFmpeg could not start.");

                return false;
            }

            //
            // Consume stderr so FFmpeg cannot block when its error
            // pipe becomes full. Do not log the raw output because it
            // can contain both the private path and relay stream key.
            //

            newProcess.ErrorDataReceived +=
                (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        diagnostics.TryConsumeProgress(args.Data);
                    }
                };

            newProcess.BeginErrorReadLine();

            process =
                newProcess;

            AepLog.Info(
                $"[LOCAL-VIDEO-BROADCAST] Upload started at {Math.Max(0d, startPositionSeconds):0.###}s.");

            return true;
        }
        catch (Exception exception)
        {
            LastError =
                $"The local video broadcaster could not start: {exception.Message}";

            AepLog.Warning(
                $"[LOCAL-VIDEO-BROADCAST] Failed to start: {exception.Message}");

            diagnostics.Failed("FFmpeg could not start.");

            Stop();

            return false;
        }
    }

    internal void Stop()
    {
        var current =
            process;

        process =
            null;

        if (current is null)
        {
            return;
        }

        stopping =
            true;

        try
        {
            if (!current.HasExited)
            {
                current.Kill(
                    entireProcessTree: true);

                current.WaitForExit(
                    2000);
            }
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[LOCAL-VIDEO-BROADCAST] Failed to stop FFmpeg cleanly: {exception.Message}");
        }
        finally
        {
            int? exitCode = null;
            try { if (current.HasExited) exitCode = current.ExitCode; } catch { }
            diagnostics.Stop(exitCode);
            current.Dispose();

            stopping =
                false;
        }

        AepLog.Info(
            "[LOCAL-VIDEO-BROADCAST] Upload stopped.");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed =
            true;

        Stop();
    }
}
