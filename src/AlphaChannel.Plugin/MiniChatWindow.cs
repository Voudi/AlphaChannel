using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AlphaChannel.Plugin;

internal sealed class MiniChatWindow : Window, IDisposable
{
    private readonly StreamClient stream;
    private readonly Func<string> titleProvider;
    private readonly Action drawContents;
    private readonly Action openMiniPlayer;
    private readonly Action openFullChat;
    private bool saveLayoutPending;

    internal MiniChatWindow(
        StreamClient stream,
        Func<string> titleProvider,
        Action drawContents,
        Action openMiniPlayer,
        Action openFullChat)
        : base("Alpha Channel Mini Chat###AlphaChannelMiniChat")
    {
        this.stream = stream;
        this.titleProvider = titleProvider;
        this.drawContents = drawContents;
        this.openMiniPlayer = openMiniPlayer;
        this.openFullChat = openFullChat;

        Flags = ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoSavedSettings;

        Size = new Vector2(
            Math.Clamp(Plugin.Cfg.MiniChatWidth, 320f, 650f),
            Math.Clamp(Plugin.Cfg.MiniChatHeight, 350f, 850f));
        SizeCondition = ImGuiCond.FirstUseEver;
        Position = Plugin.Cfg.MiniChatPosition;
        PositionCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320f, 350f),
            MaximumSize = new Vector2(650f, 850f),
        };
    }

    public override void Draw()
    {
        var colors = ThemeCatalog.Get(Plugin.Cfg.UiTheme, Plugin.Cfg.UiBackground);
        DrawHeader(colors);
        ImGui.Separator();
        ImGui.Spacing();
        drawContents();
        CaptureLayout();
    }

    private void DrawHeader(ThemeColors colors)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(colors.Accent, FontAwesomeIcon.Comments.ToIconString());
        }
        ImGui.SameLine();

        var title = titleProvider();
        var maxTitleWidth = MathF.Max(80f, ImGui.GetContentRegionAvail().X - 152f);
        ImGui.TextUnformatted(Truncate(title, maxTitleWidth));

        if (stream.Mode is StreamMode.Hosting or StreamMode.Viewing)
        {
            ImGui.SameLine();
            ImGui.TextColored(colors.Good, $"{stream.Roster.Length} watching");
        }

        var closeX = ImGui.GetWindowContentRegionMax().X - 27f;
        ImGui.SameLine(closeX - 68f);
        if (IconButton("miniChatPlayer", FontAwesomeIcon.Tv, "Show Mini Player"))
        {
            openMiniPlayer();
        }

        ImGui.SameLine(closeX - 34f);
        if (IconButton("miniChatFull", FontAwesomeIcon.Expand, "Open full Watch Party chat"))
        {
            IsOpen = false;
            openFullChat();
        }

        ImGui.SameLine(closeX);
        if (IconButton("miniChatClose", FontAwesomeIcon.Times, "Close Mini Chat"))
        {
            IsOpen = false;
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
        var changed = Plugin.Cfg.MiniChatPosition != position ||
                      MathF.Abs(Plugin.Cfg.MiniChatWidth - size.X) > 1f ||
                      MathF.Abs(Plugin.Cfg.MiniChatHeight - size.Y) > 1f;

        if (changed)
        {
            Plugin.Cfg.MiniChatPosition = position;
            Plugin.Cfg.MiniChatWidth = size.X;
            Plugin.Cfg.MiniChatHeight = size.Y;
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

    public void Dispose() => OnClose();
}
