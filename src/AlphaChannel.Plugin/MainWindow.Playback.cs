using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Player transport deck — only draws the stage while something is actually playing.
internal sealed partial class MainWindow
{
    private string urlInput = string.Empty;

    // The seek bar tracks live playback position every frame except while the user is actively
    // dragging it - see the Draw body below for why: mpv keeps advancing position during a drag,
    // and resetting the slider's value from that every frame would fight the user's own drag
    // input, snapping back to "now" instead of following the mouse.
    private float seekPreview;
    private bool seekDragging;
    private double recentlyWatchedLastSave;

    // Playback failure toast
    private string? playbackErrorToast;
    private double playbackErrorToastStartedAt;
    private int lastPlaybackErrorAttempt;

    private void DrawPlayback()
    {
        if (queue.Current is not { } current)
        {
            if (video.LastError is { } idleError)
            {
                ImGui.TextColored(Danger, idleError);
            }

            return;
        }

        var (position, duration, isPaused) = video.GetProgress();


        DrawStage("##nowPlaying", () =>
        {
            ImGui.TextColored(Accent, "NOW PLAYING");
            ImGui.SetWindowFontScale(1.25f);
            ImGui.TextWrapped(current.Title);
            ImGui.SetWindowFontScale(1f);

            if (!seekDragging)
            {
                seekPreview = position;
            }

            ImGui.Spacing();
            ImGui.SetNextItemWidth(-1f);
            ImGui.SliderFloat("##seek", ref seekPreview, 0f, MathF.Max(duration, 0.01f), "");
            seekDragging = ImGui.IsItemActive();
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                video.Seek(seekPreview);
            }

            var (streamWidth, streamHeight) = video.GetResolution();
            var timeText = $"{FormatTime(position)} / {FormatTime(duration)}";
            ImGui.TextColored(MutedText, streamWidth > 0 && streamHeight > 0
                ? $"{timeText}  ·  {streamWidth}x{streamHeight}"
                : timeText);

            if (video.LastError is { } error)
            {
                ImGui.TextColored(Danger, error);
            }

            ImGui.Spacing();

            if (IconButton(isPaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause))
            {
                // The local host must have their TV spawned before
                // paused playback can be resumed.
                if (isPaused &&
                    stream.Mode != StreamMode.Viewing &&
                    !screenController.Engine.IsActive)
                {
                    Plugin.ChatGui.Print(
                        "[Alpha Channel] Respawn TV to begin playing.");
                }
                else
                {
                    video.Pause(!isPaused);
                }
            }

            ImGui.SameLine();
            DrawVolumeControl();
        });

        ImGui.Spacing();
    }

    private void DrawPlaybackErrorToast()
    {
        // ---------------------------------------------------------
        // Detect a new playback failure
        // ---------------------------------------------------------

        if (video.State == VideoPlaybackState.Failed &&
      video.LastError is { Length: > 0 } error &&
      video.PlaybackAttemptId != lastPlaybackErrorAttempt)
        {
            lastPlaybackErrorAttempt = video.PlaybackAttemptId;

            playbackErrorToast = error;
            playbackErrorToastStartedAt = ImGui.GetTime();
        }

        if (playbackErrorToast is null)
        {
            return;
        }

        var elapsed =
            ImGui.GetTime() -
            playbackErrorToastStartedAt;

        const double totalDuration = 4.5;
        const double slideDuration = 0.25;
        const double fadeDuration = 0.5;

        if (elapsed >= totalDuration)
        {
            playbackErrorToast = null;
            return;
        }

        // ---------------------------------------------------------
        // Slide animation
        // ---------------------------------------------------------

        var slideProgress =
            Math.Clamp(
                elapsed / slideDuration,
                0.0,
                1.0);

        // Smooth-step so it doesn't move mechanically.
        slideProgress =
            slideProgress *
            slideProgress *
            (3.0 - 2.0 * slideProgress);

        var alpha = 1f;

        if (elapsed >
            totalDuration - fadeDuration)
        {
            alpha =
                (float)Math.Clamp(
                    (totalDuration - elapsed) /
                    fadeDuration,
                    0.0,
                    1.0);
        }

        const float width = 420f;
        const float height = 78f;
        const float margin = 18f;

        var windowPos =
            ImGui.GetWindowPos();

        var windowSize =
            ImGui.GetWindowSize();

        var finalX =
            windowPos.X +
            windowSize.X -
            width -
            margin;

        var finalY =
            windowPos.Y +
            margin;

        // Start above the window and slide downward.
        var startY =
            windowPos.Y -
            height -
            10f;

        var y =
            startY +
            (finalY - startY) *
            (float)slideProgress;

        var min =
            new Vector2(
                finalX,
                y);

        var max =
            new Vector2(
                finalX + width,
                y + height);

        var drawList =
            ImGui.GetForegroundDrawList();

        // ---------------------------------------------------------
        // Background
        // ---------------------------------------------------------

        var background =
            new Vector4(
                0.055f,
                0.06f,
                0.09f,
                0.96f * alpha);

        var border =
            new Vector4(
                Danger.X,
                Danger.Y,
                Danger.Z,
                alpha);

        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(background),
            9f);

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(border),
            9f,
            ImDrawFlags.None,
            1.5f);

        // Red accent strip.
        drawList.AddRectFilled(
            min,
            new Vector2(
                min.X + 4f,
                max.Y),
            ImGui.GetColorU32(border),
            9f);

        // ---------------------------------------------------------
        // Text
        // ---------------------------------------------------------

        var titlePos =
            new Vector2(
                min.X + 18f,
                min.Y + 13f);

        drawList.AddText(
            titlePos,
            ImGui.GetColorU32(
                new Vector4(
                    1f,
                    1f,
                    1f,
                    alpha)),
            "Video couldn't be played");

        var message =
            playbackErrorToast;

        // Keep the raw MPV error from taking over the whole window.
        if (message.Length > 90)
        {
            message =
                message[..87] + "...";
        }

        drawList.AddText(
            new Vector2(
                min.X + 18f,
                min.Y + 40f),
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    alpha)),
            message);
    }

    private void DrawVolumeControl()
    {
        if (IconButton(Plugin.Cfg.Muted ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp))
        {
            Plugin.Cfg.Muted = !Plugin.Cfg.Muted;
            video.SetVolume(Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume);
            Plugin.Cfg.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);

        var volume = Plugin.Cfg.Volume;

        if (ImGui.SliderInt("##volume", ref volume, 0, 130, "%d%%"))
        {
            Plugin.Cfg.Volume = volume;
            video.SetVolume(Plugin.Cfg.Muted ? 0 : volume);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Plugin.Cfg.Save();
        }
    }

    private static string FormatTime(float totalSeconds)
    {
        if (totalSeconds < 0 || float.IsNaN(totalSeconds) || float.IsInfinity(totalSeconds))
        {
            totalSeconds = 0;
        }

        var span = TimeSpan.FromSeconds(totalSeconds);
        return span.Hours > 0 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    internal void UpdateRecentlyWatched(
    VideoQueueEntry entry,
    double positionSeconds,
    double durationSeconds)
    {
        AepLog.Warning(
    $"[Recently Watched] Saving {entry.Title} at {positionSeconds:F0}s");
        var existing =
            Plugin.Cfg.RecentlyWatchedVideos
                .FirstOrDefault(
                    x =>
                        string.Equals(
                            x.Url,
                            entry.Url,
                            StringComparison.OrdinalIgnoreCase));


        if (existing is null)
        {
            existing = new RecentlyWatchedVideoRecord
            {
                Url = entry.Url
            };

            Plugin.Cfg.RecentlyWatchedVideos.Insert(
                0,
                existing);
        }


        existing.Title = entry.Title;
        existing.ThumbnailUrl = entry.ThumbnailUrl;
        existing.ChannelName = entry.Source;
        existing.WatchedSeconds = positionSeconds;
        existing.DurationSeconds = durationSeconds;
        existing.LastWatchedUtc = DateTime.UtcNow;


        Plugin.Cfg.RecentlyWatchedVideos =
            Plugin.Cfg.RecentlyWatchedVideos
                .OrderByDescending(
                    x => x.LastWatchedUtc)
                .Take(5)
                .ToList();


        Plugin.Cfg.Save();
    }

}
