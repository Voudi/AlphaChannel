using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Just the send buttons - the actual reactions now render on the in-world screen itself
// (Plugin.cs's UpdateReactions/VideoEngine.SetReactions/ScreenPainter's ReactionsPS), not floating
// in this GUI window. Only one place can drain stream.IncomingReactions (it's a ConcurrentQueue,
// not a broadcast), and Plugin.cs is it.
internal sealed partial class MainWindow
{
    private static readonly FontAwesomeIcon[] ReactionIcons =
    [
        FontAwesomeIcon.ThumbsUp,
        FontAwesomeIcon.Laugh,
        FontAwesomeIcon.Heart,
        FontAwesomeIcon.Surprise,
        FontAwesomeIcon.Star,
    ];

    private void DrawSectionTitle(
    FontAwesomeIcon icon,
    string title)
    {
        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            ImGui.TextColored(
                Vector4.One,
                icon.ToIconString());
        }

        ImGui.SameLine(0, 8);

        ImGui.TextColored(
            Vector4.One,
            title);
    }

    private void DrawReactions()
    {
        DrawSectionTitle(
    FontAwesomeIcon.Bolt,
    "React Live");

        ImGui.SetWindowFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "Like what you see? React directly on the screen!");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(0f, 6f));

        if (stream.Mode == StreamMode.None)
        {
            ImGui.TextColored(
                MutedText,
                "Join or host a room first.");

            return;
        }




        var buttonSize =
            new Vector2(
                48f,
                48f);



        var panelSize = new Vector2(
            ImGui.GetContentRegionAvail().X,
            72f);





        using (ImRaii.Child(
       "ReactionPanel",
       panelSize,
       false,
       ImGuiWindowFlags.NoScrollbar))
        {
            var panelWidth =
                ImGui.GetContentRegionAvail().X;

            var totalWidth =
                (ReactionIcons.Length * buttonSize.X) +
                ((ReactionIcons.Length - 1) * 12f);

            var startX =
                MathF.Max(
                    0f,
                    (panelWidth - totalWidth) * 0.5f);

            ImGui.SetCursorPosX(startX);

            ImGui.SetCursorPosY(
     ImGui.GetCursorPosY() + 8f);


            for (var index = 0;
         index < ReactionIcons.Length;
         index++)
            {
                if (index > 0)
                {
                    ImGui.SameLine(0, 12);
                }

                DrawReactionButton(
                    ReactionIcons[index],
                    buttonSize);
            }
        }
    }

    private void DrawReactionButton(
    FontAwesomeIcon icon,
    Vector2 size)
    {
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
14f))
        {
            if (ImGui.Button(
                    $"##reaction_{icon}",
                    size))
            {
                _ = stream.SendReactionAsync(
                    icon.ToIconString());
            }
        }


        var drawList =
            ImGui.GetWindowDrawList();


        var min =
            ImGui.GetItemRectMin();


        var iconText =
     icon.ToIconString();

        Vector2 textSize;

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            textSize =
                ImGui.CalcTextSize(iconText);

            var pos =
                new Vector2(
                    min.X +
                    ((size.X - textSize.X) * 0.5f),

                    min.Y +
                    ((size.Y - textSize.Y) * 0.5f));

            drawList.AddText(
                pos,
                ImGui.GetColorU32(
                    Vector4.One),
                iconText);
        }
    }
}
