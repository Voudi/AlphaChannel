using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum WatchPartyDrawer
    {
        WatchParty,
        Chat
    }

    private WatchPartyDrawer activeWatchPartyDrawer =
        WatchPartyDrawer.WatchParty;

    private void DrawWatchPartyPage()
    {
        DrawWatchPartyDrawerTabs();

        ImGui.Spacing();

        ImGui.Separator();

        ImGui.Spacing();
        ImGui.Spacing();

        switch (activeWatchPartyDrawer)
        {
            case WatchPartyDrawer.WatchParty:
                DrawWatchPartyDrawer();
                break;

            case WatchPartyDrawer.Chat:
                DrawChatDrawer();
                break;
        }
    }

    private void DrawWatchPartyDrawerTabs()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float gap = 8f;
        const int tabCount = 2;

        var buttonWidth =
            (availableWidth -
             (gap * (tabCount - 1))) /
            tabCount;

        var buttonSize =
            new Vector2(
                buttonWidth,
                46f);

        DrawWatchPartyDrawerTab(
            FontAwesomeIcon.Users,
            "Watch Party",
            WatchPartyDrawer.WatchParty,
            buttonSize);

        ImGui.SameLine(0, gap);

        DrawWatchPartyDrawerTab(
            FontAwesomeIcon.Comment,
            "Chat",
            WatchPartyDrawer.Chat,
            buttonSize);
    }

    private void DrawWatchPartyDrawerTab(
        FontAwesomeIcon icon,
        string label,
        WatchPartyDrawer drawer,
        Vector2 size)
    {
        var selected =
            activeWatchPartyDrawer == drawer;

        var buttonPos =
            ImGui.GetCursorScreenPos();

        var bg = selected
            ? Accent
            : new Vector4(
                0.055f,
                0.07f,
                0.115f,
                1f);

        var hoverBg = selected
            ? AccentHover
            : new Vector4(
                0.075f,
                0.095f,
                0.15f,
                1f);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   bg)
               .Push(
                   ImGuiCol.ButtonHovered,
                   hoverBg)
               .Push(
                   ImGuiCol.ButtonActive,
                   selected
                       ? AccentActive
                       : hoverBg))
        {
            if (ImGui.Button(
                    $"##watchPartyDrawer_{drawer}",
                    size))
            {
                activeWatchPartyDrawer =
                    drawer;
            }
        }

        var drawList =
            ImGui.GetWindowDrawList();

        if (!selected)
        {
            drawList.AddRect(
                buttonPos,
                buttonPos + size,
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.10f)),
                8f,
                ImDrawFlags.None,
                1f);
        }

        var iconText =
            icon.ToIconString();

        Vector2 iconSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            iconSize =
                ImGui.CalcTextSize(iconText);
        }

        var textSize =
            ImGui.CalcTextSize(label);

        const float iconGap = 9f;

        var totalWidth =
            iconSize.X +
            iconGap +
            textSize.X;

        var start =
            new Vector2(
                buttonPos.X +
                (size.X - totalWidth) * 0.5f,

                buttonPos.Y +
                (size.Y - textSize.Y) * 0.5f);

        var textColor =
            selected
                ? Vector4.One
                : MutedText;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            drawList.AddText(
                start,
                ImGui.GetColorU32(
                    textColor),
                iconText);
        }

        drawList.AddText(
            start +
            new Vector2(
                iconSize.X + iconGap,
                0f),
            ImGui.GetColorU32(
                textColor),
            label);
    }

    private void DrawWatchPartyDrawer()
    {
        DrawPartyPanel();
    }

    private void DrawChatDrawer()
    {
        DrawPartySocialPanel();
    }
}