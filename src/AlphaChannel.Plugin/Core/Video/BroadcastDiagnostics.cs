using System.Diagnostics;
using System.Globalization;

namespace AlphaChannel.Plugin.Video;

internal enum BroadcastHealth : byte
{
    Idle,
    Starting,
    Healthy,
    DroppingFrames,
    AudioBacklog,
    EncoderBehind,
    Failed,
}

internal sealed record BroadcastDiagnosticsSnapshot(
    bool Active,
    string Source,
    BroadcastHealth Health,
    int Width,
    int Height,
    double TargetFps,
    double CaptureFps,
    double EncodeFps,
    double Speed,
    double BitrateKbps,
    long BytesUploaded,
    int VideoQueueDepth,
    int AudioQueueDepth,
    long DroppedFrames,
    long DroppedAudioPackets,
    TimeSpan Duration,
    int? ExitCode,
    string? LastError,
    DateTime UpdatedUtc)
{
    internal static readonly BroadcastDiagnosticsSnapshot Idle = new(
        false, "No active broadcast", BroadcastHealth.Idle, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, TimeSpan.Zero, null, null, DateTime.UtcNow);

    internal string ToSanitizedSummary()
    {
        var state = Health.ToString();
        var resolution = Width > 0 && Height > 0 ? $"{Width}x{Height}" : "unknown";
        return string.Join(Environment.NewLine,
            "Alpha Channel broadcast diagnostics",
            $"Source: {Source}",
            $"State: {state}",
            $"Resolution: {resolution}",
            $"Target / captured / encoded FPS: {TargetFps:0.##} / {CaptureFps:0.##} / {EncodeFps:0.##}",
            $"Encoder speed: {Speed:0.##}x",
            $"Bitrate: {BitrateKbps:0} kbps",
            $"Uploaded: {FormatBytes(BytesUploaded)}",
            $"Queues (video/audio): {VideoQueueDepth} / {AudioQueueDepth}",
            $"Discarded (frames/audio packets): {DroppedFrames} / {DroppedAudioPackets}",
            $"Duration: {Duration:hh\\:mm\\:ss}",
            $"Exit code: {(ExitCode.HasValue ? ExitCode.Value.ToString(CultureInfo.InvariantCulture) : "n/a")}",
            $"Last error: {LastError ?? "none"}");
    }

    internal static string FormatBytes(long value)
    {
        if (value < 1024) return $"{value} B";
        if (value < 1024L * 1024) return $"{value / 1024d:0.0} KiB";
        if (value < 1024L * 1024 * 1024) return $"{value / (1024d * 1024d):0.0} MiB";
        return $"{value / (1024d * 1024d * 1024d):0.00} GiB";
    }
}

internal sealed class BroadcastDiagnosticsTracker
{
    private readonly object gate = new();
    private readonly Stopwatch clock = new();
    private string source = "Broadcast";
    private int width;
    private int height;
    private double targetFps;
    private long capturedFrames;
    private long lastCapturedFrames;
    private long lastCaptureTicks;
    private double captureFps;
    private double encodeFps;
    private double speed;
    private double bitrateKbps;
    private long bytesUploaded;
    private int videoQueueDepth;
    private int audioQueueDepth;
    private long droppedFrames;
    private long droppedAudioPackets;
    private DateTime lastVideoDropUtc;
    private DateTime lastAudioDropUtc;
    private DateTime nextLogUtc;
    private BroadcastHealth lastReportedHealth;
    private TimeSpan outputDuration;
    private int? exitCode;
    private string? lastError;
    private bool active;
    private DateTime updatedUtc = DateTime.UtcNow;

    internal void Start(string sourceName, int frameWidth = 0, int frameHeight = 0, double fps = 0)
    {
        lock (gate)
        {
            source = string.IsNullOrWhiteSpace(sourceName) ? "Broadcast" : sourceName;
            width = frameWidth;
            height = frameHeight;
            targetFps = fps;
            capturedFrames = lastCapturedFrames = 0;
            lastCaptureTicks = 0;
            captureFps = encodeFps = speed = bitrateKbps = 0;
            bytesUploaded = droppedFrames = droppedAudioPackets = 0;
            lastVideoDropUtc = lastAudioDropUtc = DateTime.MinValue;
            nextLogUtc = DateTime.UtcNow.AddSeconds(30);
            lastReportedHealth = BroadcastHealth.Starting;
            videoQueueDepth = audioQueueDepth = 0;
            outputDuration = TimeSpan.Zero;
            exitCode = null;
            lastError = null;
            active = true;
            updatedUtc = DateTime.UtcNow;
            clock.Restart();
        }
    }

    internal void Stop(int? code = null)
    {
        lock (gate)
        {
            active = false;
            exitCode = code ?? exitCode;
            updatedUtc = DateTime.UtcNow;
            clock.Stop();
        }
    }

    internal void Failed(string message, int? code = null)
    {
        lock (gate)
        {
            active = false;
            exitCode = code ?? exitCode;
            lastError = string.IsNullOrWhiteSpace(message) ? "FFmpeg stopped unexpectedly." : message;
            updatedUtc = DateTime.UtcNow;
            clock.Stop();
        }
    }

    internal void CapturedFrame(bool replacedPendingFrame)
    {
        lock (gate)
        {
            capturedFrames++;
            if (replacedPendingFrame)
            {
                droppedFrames++;
                lastVideoDropUtc = DateTime.UtcNow;
            }
            videoQueueDepth = 1;
            var ticks = clock.ElapsedTicks;
            if (lastCaptureTicks == 0) lastCaptureTicks = ticks;
            var elapsed = (ticks - lastCaptureTicks) / (double)Stopwatch.Frequency;
            if (elapsed >= 1d)
            {
                captureFps = (capturedFrames - lastCapturedFrames) / elapsed;
                lastCapturedFrames = capturedFrames;
                lastCaptureTicks = ticks;
            }
            updatedUtc = DateTime.UtcNow;
        }
    }

    internal void SetQueues(int video, int audio)
    {
        lock (gate)
        {
            videoQueueDepth = Math.Max(0, video);
            audioQueueDepth = Math.Max(0, audio);
            updatedUtc = DateTime.UtcNow;
        }
    }

    internal void DroppedAudio(long count)
    {
        if (count <= 0) return;
        lock (gate)
        {
            droppedAudioPackets += count;
            lastAudioDropUtc = DateTime.UtcNow;
        }
    }

    internal bool TryConsumeProgress(string line)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0) return false;
        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();
        lock (gate)
        {
            switch (key)
            {
                case "fps":
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out encodeFps);
                    break;
                case "speed":
                    double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
                    break;
                case "bitrate":
                    double.TryParse(value.Replace("kbits/s", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out bitrateKbps);
                    break;
                case "total_size":
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytesUploaded);
                    break;
                case "out_time_us":
                case "out_time_ms":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                        outputDuration = TimeSpan.FromTicks(microseconds * 10);
                    break;
                case "drop_frames":
                    if (long.TryParse(value, out var encoderDrops) && encoderDrops > droppedFrames)
                    {
                        droppedFrames = encoderDrops;
                        lastVideoDropUtc = DateTime.UtcNow;
                    }
                    break;
                case "progress":
                case "frame":
                case "dup_frames":
                case "out_time":
                case "packet":
                    break;
                default:
                    if (!key.StartsWith("stream_", StringComparison.Ordinal)) return false;
                    break;
            }
            updatedUtc = DateTime.UtcNow;

            if (key == "progress")
            {
                var snapshot = Snapshot;
                var unhealthy = snapshot.Health is BroadcastHealth.DroppingFrames
                    or BroadcastHealth.AudioBacklog or BroadcastHealth.EncoderBehind or BroadcastHealth.Failed;

                if (unhealthy && snapshot.Health != lastReportedHealth)
                {
                    AepLog.Warning($"[BROADCAST-DIAGNOSTICS] {source}: {snapshot.Health}; " +
                                   $"speed={speed:0.00}x fps={captureFps:0.0}/{encodeFps:0.0} " +
                                   $"queues={videoQueueDepth}/{audioQueueDepth} drops={droppedFrames}/{droppedAudioPackets}.");
                }
                else if (DateTime.UtcNow >= nextLogUtc)
                {
                    AepLog.Info($"[BROADCAST-DIAGNOSTICS] {source}: {snapshot.Health}; " +
                                $"speed={speed:0.00}x fps={captureFps:0.0}/{encodeFps:0.0} " +
                                $"bitrate={bitrateKbps:0}kbps queues={videoQueueDepth}/{audioQueueDepth} " +
                                $"drops={droppedFrames}/{droppedAudioPackets}.");
                    nextLogUtc = DateTime.UtcNow.AddSeconds(30);
                }

                lastReportedHealth = snapshot.Health;
            }
            return true;
        }
    }

    internal BroadcastDiagnosticsSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                var health = BroadcastHealth.Idle;
                if (!active && lastError is not null) health = BroadcastHealth.Failed;
                else if (active && clock.Elapsed < TimeSpan.FromSeconds(3)) health = BroadcastHealth.Starting;
                else if (active && DateTime.UtcNow - lastAudioDropUtc < TimeSpan.FromSeconds(10)) health = BroadcastHealth.AudioBacklog;
                else if (active && DateTime.UtcNow - lastVideoDropUtc < TimeSpan.FromSeconds(10)) health = BroadcastHealth.DroppingFrames;
                else if (active && speed > 0 && speed < .95) health = BroadcastHealth.EncoderBehind;
                else if (active) health = BroadcastHealth.Healthy;

                return new BroadcastDiagnosticsSnapshot(active, source, health, width, height, targetFps,
                    captureFps, encodeFps, speed, bitrateKbps, bytesUploaded, videoQueueDepth,
                    audioQueueDepth, droppedFrames, droppedAudioPackets,
                    outputDuration > TimeSpan.Zero ? outputDuration : clock.Elapsed,
                    exitCode, lastError, updatedUtc);
            }
        }
    }
}
