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
        string Name,
        bool PatreonOnly = false);

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
        var locked =
            reaction.PatreonOnly &&
            CurrentSession?.PatreonTier is not (PatreonTier.Tier1 or PatreonTier.Tier2 or PatreonTier.Tier3);

        using (ImRaii.Disabled(
            locked))
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

        var max =
            ImGui.GetItemRectMax();

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

            drawList.AddText(
                pos,
                ImGui.GetColorU32(
                    locked
                        ? MutedText
                        : Vector4.One),
                iconText);
        }

        if (locked)
        {
            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                var lockText =
                    FontAwesomeIcon.Lock.ToIconString();

                var lockSize =
                    ImGui.CalcTextSize(
                        lockText);

                drawList.AddText(
                    new Vector2(
                        max.X -
                        lockSize.X -
                        4f,
                        max.Y -
                        lockSize.Y -
                        3f),
                    ImGui.GetColorU32(
                        Gold),
                    lockText);
            }
        }

        if (ImGui.IsItemHovered(
                ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                locked
                    ? $"{reaction.Name} — Patreon reaction"
                    : reaction.Name);
        }
    }

    private void DrawCompactReactions(
      float width)
    {
        if (stream.Mode == StreamMode.None)
        {
            return;
        }

        const float reactionSize = 28f;
        const float reactionGap = 5f;
        const float arrowWidth = 26f;

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
