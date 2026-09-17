using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AlphaChannel.Plugin;

// Draws the engine's existing shared texture. Mini Mode never starts a
// second decoder, browser host, emulator or broadcast process.
internal sealed class MiniPlayerWindow : Window, IDisposable
{
    private readonly VideoPlayer video;
    private readonly VideoEngine engine;
    private readonly StreamClient stream;
    private readonly Func<string> titleProvider;
    private readonly Action openMiniChat;
    private readonly Action restoreFullWindow;

    private float seekPreview;
    private bool seekDragging;
    private bool saveLayoutPending;

    internal MiniPlayerWindow(
        VideoPlayer video,
        VideoEngine engine,
        StreamClient stream,
        Func<string> titleProvider,
        Action openMiniChat,
        Action restoreFullWindow)
        : base("Alpha Channel Mini Player###AlphaChannelMiniPlayer")
    {
        this.video = video;
        this.engine = engine;
        this.stream = stream;
        this.titleProvider = titleProvider;
        this.openMiniChat = openMiniChat;
        this.restoreFullWindow = restoreFullWindow;

        Flags = ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoSavedSettings;

        Size = new Vector2(
            Math.Clamp(Plugin.Cfg.MiniPlayerWidth, 380f, 1100f),
            Math.Clamp(Plugin.Cfg.MiniPlayerHeight, 285f, 760f));
        SizeCondition = ImGuiCond.FirstUseEver;
        Position = Plugin.Cfg.MiniPlayerPosition;
        PositionCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380f, 285f),
            MaximumSize = new Vector2(1100f, 760f),
        };
    }

    public override void Draw()
    {
        var colors = ThemeCatalog.Get(Plugin.Cfg.UiTheme, Plugin.Cfg.UiBackground);
        DrawHeader(colors);
        ImGui.Separator();
        ImGui.Spacing();

        var available = ImGui.GetContentRegionAvail();
        var previewAvailable = new Vector2(
            available.X,
            MathF.Max(120f, available.Y - 72f));
        var previewSize = FitAspect(previewAvailable, 16f / 9f);
        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() + MathF.Max(0f, (available.X - previewSize.X) * 0.5f));

        var previewOrigin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##miniVideo", previewSize);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            previewOrigin,
            previewOrigin + previewSize,
            ImGui.GetColorU32(new Vector4(0.01f, 0.015f, 0.03f, 1f)),
            7f);

        var hasPreview = video.State is not VideoPlaybackState.Idle ||
                         engine.IsPlayingGame ||
                         engine.IsPlayingBrowser ||
                         video.IsPlayingImage ||
                         video.IsPlayingLocalVideo;

        if (engine.PreviewTextureHandle != nint.Zero && hasPreview)
        {
            drawList.AddImageRounded(
                new ImTextureID(unchecked((ulong)engine.PreviewTextureHandle)),
                previewOrigin,
                previewOrigin + previewSize,
                Vector2.Zero,
                Vector2.One,
                uint.MaxValue,
                7f);
        }
        else
        {
            const string idle = "Nothing is playing";
            var idleSize = ImGui.CalcTextSize(idle);
            drawList.AddText(
                previewOrigin + (previewSize - idleSize) * 0.5f,
                ImGui.GetColorU32(colors.MutedText),
                idle);
        }

        ImGui.Spacing();
        DrawControls(colors);
        CaptureLayout();
    }

    private void DrawHeader(ThemeColors colors)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(colors.Accent, FontAwesomeIcon.PlayCircle.ToIconString());
        }
        ImGui.SameLine();

        var reserved = 150f;
        ImGui.TextUnformatted(
            Truncate(titleProvider(), MathF.Max(100f, ImGui.GetContentRegionAvail().X - reserved)));

        var closeX = ImGui.GetWindowContentRegionMax().X - 27f;
        ImGui.SameLine(closeX - 68f);
        if (IconButton("miniOpenChat", FontAwesomeIcon.Comments, "Show Mini Chat"))
        {
            openMiniChat();
        }

        ImGui.SameLine(closeX - 34f);
        if (IconButton("miniRestoreFull", FontAwesomeIcon.Expand, "Open full Alpha Channel"))
        {
            IsOpen = false;
            restoreFullWindow();
        }

        ImGui.SameLine(closeX);
        if (IconButton("miniClose", FontAwesomeIcon.Times, "Close Mini Player"))
        {
            IsOpen = false;
        }
    }

    private void DrawControls(ThemeColors colors)
    {
        var (position, duration, paused) = video.GetProgress();
        var canTransport = stream.Mode != StreamMode.Viewing &&
                           video.State is VideoPlaybackState.Playing or VideoPlaybackState.Paused;
        var seekable = canTransport && duration > 0.5f && !video.IsAudioOnly;

        if (!seekDragging)
        {
            seekPreview = position;
        }

        if (!canTransport)
        {
            ImGui.BeginDisabled();
        }

        if (IconButton(
                "miniPlayPause",
                paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause,
                canTransport ? (paused ? "Play" : "Pause") : "Playback is controlled by the host") &&
            canTransport)
        {
            video.Pause(!paused);
        }

        if (!canTransport)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Max(90f, ImGui.GetContentRegionAvail().X - 168f));
        if (!seekable)
        {
            ImGui.BeginDisabled();
        }

        ImGui.SliderFloat("##miniSeek", ref seekPreview, 0f, MathF.Max(duration, 0.01f), "");
        seekDragging = seekable && ImGui.IsItemActive();
        if (seekable && ImGui.IsItemDeactivatedAfterEdit())
        {
            video.Seek(seekPreview);
        }

        if (!seekable)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.TextColored(colors.MutedText, $"{FormatTime(position)} / {FormatTime(duration)}");

        var volume = Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                colors.Accent,
                (volume == 0 ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp).ToIconString());
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.SliderInt("##miniVolume", ref volume, 0, 100, $"{volume}%"))
        {
            Plugin.Cfg.Volume = volume;
            Plugin.Cfg.Muted = volume == 0;
            video.SetVolume(volume);
            Plugin.Cfg.Save();
        }
    }

    private static bool IconButton(string id, FontAwesomeIcon icon, string tooltip)
    {
        bool clicked;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}", new Vector2(27f, 25f));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        return clicked;
    }

    private void CaptureLayout()
    {
        var position = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var changed = Plugin.Cfg.MiniPlayerPosition != position ||
                      MathF.Abs(Plugin.Cfg.MiniPlayerWidth - size.X) > 1f ||
                      MathF.Abs(Plugin.Cfg.MiniPlayerHeight - size.Y) > 1f;

        if (changed)
        {
            Plugin.Cfg.MiniPlayerPosition = position;
            Plugin.Cfg.MiniPlayerWidth = size.X;
            Plugin.Cfg.MiniPlayerHeight = size.Y;
            saveLayoutPending = true;
        }

        if (saveLayoutPending && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            Plugin.Cfg.Save();
            saveLayoutPending = false;
        }
    }

    public override void OnClose()
    {
        if (saveLayoutPending)
        {
            Plugin.Cfg.Save();
            saveLayoutPending = false;
        }
    }

    private static Vector2 FitAspect(Vector2 available, float aspect)
    {
        var width = available.X;
        var height = width / aspect;
        if (height > available.Y)
        {
            height = available.Y;
            width = height * aspect;
        }

        return new Vector2(MathF.Max(1f, width), MathF.Max(1f, height));
    }

    private static string Truncate(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
        {
            return text;
        }

        while (text.Length > 1 && ImGui.CalcTextSize(text + "…").X > width)
        {
            text = text[..^1];
        }

        return text + "…";
    }

    private static string FormatTime(float seconds) =>
        TimeSpan.FromSeconds(Math.Max(0f, seconds))
            .ToString(seconds >= 3600f ? @"h\:mm\:ss" : @"m\:ss");

    public void Dispose() => OnClose();
}
