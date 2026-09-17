using AlphaChannel.Contracts;
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
    private sealed record ReactionDefinition(
        FontAwesomeIcon Icon,
        string Name);

    private static readonly ReactionDefinition[] Reactions =
    [
        new(
        FontAwesomeIcon.ThumbsUp,
        "Like"),

    new(
        FontAwesomeIcon.Laugh,
        "Laugh"),

    new(
        FontAwesomeIcon.Heart,
        "Love"),

    new(
        FontAwesomeIcon.Surprise,
        "Surprised"),

    new(
        FontAwesomeIcon.Star,
        "Hype"),
];

    private const int VisibleReactionCount = 5;

    private int reactionPage;

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

        SetUiFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "Like what you see? React directly on the screen!");

        SetUiFontScale(1f);

        ImGui.Dummy(
            UiVec(0f, 6f));

        if (stream.Mode == StreamMode.None)
        {
            ImGui.TextColored(
                MutedText,
                "Join or host a room first.");

            return;
        }




        var buttonSize =
            UiVec(48f, 48f);



        var panelSize = new Vector2(
            ImGui.GetContentRegionAvail().X,
            Ui(72f));





        using (ImRaii.Child(
       "ReactionPanel",
       panelSize,
       false,
       ImGuiWindowFlags.NoScrollbar))
        {
            var panelWidth =
                ImGui.GetContentRegionAvail().X;

            var totalWidth =
                (Reactions.Length * buttonSize.X) +
                ((Reactions.Length - 1) * 12f);

            var startX =
                MathF.Max(
                    0f,
                    (panelWidth - totalWidth) * 0.5f);

            ImGui.SetCursorPosX(startX);

            ImGui.SetCursorPosY(
     ImGui.GetCursorPosY() + 8f);


            for (var index = 0;
                 index < Reactions.Length;
                 index++)
            {
                if (index > 0)
                {
                    ImGui.SameLine(
                        0f,
                        12f);
                }

                DrawReactionButton(
                    Reactions[index],
                    buttonSize);
            }
        }
    }

    private void DrawReactionButton(
     ReactionDefinition reaction,
     Vector2 size)
    {
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            14f))
        {
            if (ImGui.Button(
                    $"##reaction_{reaction.Name}",
                    size))
            {
                _ = stream.SendReactionAsync(
                    reaction.Icon.ToIconString());
            }
        }

        var drawList =
            ImGui.GetWindowDrawList();

        var min =
            ImGui.GetItemRectMin();

        var iconText =
            reaction.Icon.ToIconString();

        Vector2 textSize;

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            textSize =
                ImGui.CalcTextSize(
                    iconText);

            var pos =
                new Vector2(
                    min.X +
                    ((size.X - textSize.X) * 0.5f),

                    min.Y +
                    ((size.Y - textSize.Y) * 0.5f));

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                pos,
                ImGui.GetColorU32(
                    Vector4.One),
                iconText);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(reaction.Name);
        }
    }

    private void DrawCompactReactions(
      float width)
    {
        if (stream.Mode == StreamMode.None)
        {
            return;
        }

        var reactionSize = Ui(28f);
        var reactionGap = Ui(5f);
        var arrowWidth = Ui(26f);

        var maxPage =
            Math.Max(
                0,
                (Reactions.Length - 1) /
                VisibleReactionCount);

        reactionPage =
            Math.Clamp(
                reactionPage,
                0,
                maxPage);

        var startIndex =
            reactionPage *
            VisibleReactionCount;

        var endIndex =
            Math.Min(
                startIndex +
                VisibleReactionCount,
                Reactions.Length);

        var showPrevious =
            reactionPage > 0;

        var showNext =
            reactionPage < maxPage;

        var visibleCount =
            endIndex -
            startIndex;

        var totalWidth =
            visibleCount *
            reactionSize +
            Math.Max(
                0,
                visibleCount - 1) *
            reactionGap;

        if (showPrevious)
        {
            totalWidth +=
                arrowWidth +
                reactionGap;
        }

        if (showNext)
        {
            totalWidth +=
                arrowWidth +
                reactionGap;
        }

        // Center the whole reaction strip inside the space
        // allocated to the React Live block.
        var startX =
            ImGui.GetCursorPosX() +
            MathF.Max(
                0f,
                (width - totalWidth) *
                0.5f);

        ImGui.SetCursorPosX(
            startX);

        if (showPrevious)
        {
            if (ImGui.Button(
                    "‹##reactionPrevious",
                    new Vector2(
                        arrowWidth,
                        reactionSize)))
            {
                reactionPage--;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Previous reactions");
            }

            ImGui.SameLine(
                0f,
                reactionGap);
        }

        for (var index = startIndex;
             index < endIndex;
             index++)
        {
            if (index > startIndex)
            {
                ImGui.SameLine(
                    0f,
                    reactionGap);
            }

            DrawReactionButton(
                Reactions[index],
                new Vector2(
                    reactionSize,
                    reactionSize));
        }

        if (showNext)
        {
            ImGui.SameLine(
                0f,
                reactionGap);

            if (ImGui.Button(
                    "›##reactionNext",
                    new Vector2(
                        arrowWidth,
                        reactionSize)))
            {
                reactionPage++;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "More reactions");
            }
        }
    }

}
