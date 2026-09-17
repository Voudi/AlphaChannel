using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const float QueueThumbnailHeight = 40f;

    private void DrawSavedQueueSlots()
    {
        SetUiFontScale(0.95f);

        ImGui.TextColored(
            Vector4.One,
            "QUEUE SLOTS");

        ImGui.SameLine();

        using (ImRaii.PushColor(
                   ImGuiCol.Text,
                   MutedText))
        {
            ImGui.Text("(?)");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Each slot keeps its own queue. Activating another slot does not clear either queue.");
        }

        ImGui.TextColored(
            MutedText,
            "Switch between your queues. The active queue is what plays on the TV.");

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(10f)));

        const int cardsPerRow = 3;

        var cardGap =
            Ui(12f);

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var cardWidth =
            (
                availableWidth -
                cardGap *
                (cardsPerRow - 1)
            ) /
            cardsPerRow;

        var cardHeight =
            Ui(145f);

        for (var i = 0;
             i < queueManager.Slots.Count;
             i++)
        {
            if (i > 0)
            {
                if (i % cardsPerRow == 0)
                {
                    ImGui.Dummy(
                        new Vector2(
                            0f,
                            cardGap));
                }
                else
                {
                    ImGui.SameLine(
                        0f,
                        cardGap);
                }
            }

            ImGui.PushID(
                $"queueSlot_{i}");

            var profile =
                queueManager.Slots[i];

            var isActive =
                profile is not null &&
                queueManager.ActiveSlotIndex == i;

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.ChildRounding,
                       Ui(9f))
                   .Push(
                       ImGuiStyleVar.ChildBorderSize,
                       isActive
                           ? 2f
                           : 1f)
                   .Push(
                       ImGuiStyleVar.WindowPadding,
                       new Vector2(
                           Ui(14f),
                           Ui(13f))))
            using (ImRaii.PushColor(
                       ImGuiCol.ChildBg,
                       new Vector4(
                           0.045f,
                           0.055f,
                           0.09f,
                           1f))
                   .Push(
                       ImGuiCol.Border,
                       isActive
                           ? Accent
                           : BorderSubtle))
            using (var card = ImRaii.Child(
                       "##queueSlotCard",
                       new Vector2(
                           cardWidth,
                           cardHeight),
                       true,
                       ImGuiWindowFlags.NoScrollbar |
                       ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (card)
                {
                    if (profile is null)
                    {
                        DrawEmptyQueueSlot(
                            i,
                            cardWidth,
                            cardHeight);
                    }
                    else
                    {
                        DrawQueueSlot(
                            i,
                            profile,
                            isActive,
                            cardWidth,
                            cardHeight);
                    }
                }
            }

            ImGui.PopID();
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(14f)));

        SetUiFontScale(1f);
    }

    private void DrawEmptyQueueSlot(
      int slotIndex,
      float cardWidth,
      float cardHeight)
    {
        var cardOrigin =
            ImGui.GetWindowPos();

        var drawList =
            ImGui.GetWindowDrawList();

        var iconCenter =
            cardOrigin +
            new Vector2(
                Ui(38f),
                cardHeight * 0.5f);

        drawList.AddCircle(
            iconCenter,
            Ui(20f),
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.65f)),
            32,
            2f);

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var glyph =
                FontAwesomeIcon.Plus
                    .ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconCenter -
                glyphSize *
                0.5f,
                ImGui.GetColorU32(
                    MutedText),
                glyph);
        }

        ImGui.SetCursorScreenPos(
            cardOrigin +
            new Vector2(
                Ui(70f),
                Ui(43f)));

        ImGui.TextColored(
            Vector4.One,
            "Empty Queue Slot");

        ImGui.SetCursorScreenPos(
            cardOrigin +
            new Vector2(
                Ui(70f),
                Ui(69f)));

        SetUiFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "Create a new queue");

        SetUiFontScale(1f);

        ImGui.SetCursorScreenPos(
            cardOrigin);

        if (ImGui.InvisibleButton(
                "##createQueueSlot",
                new Vector2(
                    cardWidth,
                    cardHeight)))
        {
            OpenQueueEditor(
                slotIndex,
                null);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);
        }
    }

    private void DrawQueueSlot(
        int slotIndex,
        SavedQueueProfile profile,
        bool isActive,
        float cardWidth,
        float cardHeight)
    {
        var cardOrigin =
            ImGui.GetWindowPos();

        var drawList =
            ImGui.GetWindowDrawList();

        var iconMin =
            cardOrigin +
            new Vector2(
                Ui(14f),
                Ui(14f));

        var iconSize =
            new Vector2(
                Ui(56f),
                Ui(56f));

        drawList.AddRectFilled(
            iconMin,
            iconMin +
            iconSize,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.16f)),
            Ui(9f));

        DrawQueueProfileIcon(
            profile.Icon,
            iconMin,
            iconSize,
            Accent);

        ImGui.SetCursorScreenPos(
            cardOrigin +
            new Vector2(
                Ui(82f),
                Ui(19f)));

        ImGui.TextColored(
            Vector4.One,
            profile.Name);

        ImGui.SetCursorScreenPos(
            cardOrigin +
            new Vector2(
                Ui(82f),
                Ui(45f)));

        SetUiFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            profile.Entries.Count == 1
                ? "1 video"
                : $"{profile.Entries.Count} videos");

        SetUiFontScale(1f);

        if (isActive)
        {
            const string activeText =
                "ACTIVE";

            var badgeSize =
                new Vector2(
                    Ui(64f),
                    Ui(22f));

            var badgePos =
                cardOrigin +
                new Vector2(
                    cardWidth -
                    badgeSize.X -
                    Ui(12f),
                    Ui(10f));

            drawList.AddRectFilled(
                badgePos,
                badgePos +
                badgeSize,
                ImGui.GetColorU32(
                    Accent),
                Ui(7f));

            var badgeTextSize =
                ImGui.CalcTextSize(
                    activeText);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                badgePos +
                new Vector2(
                    (
                        badgeSize.X -
                        badgeTextSize.X
                    ) *
                    0.5f,
                    (
                        badgeSize.Y -
                        badgeTextSize.Y
                    ) *
                    0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                activeText);
        }

        var menuSize =
            new Vector2(
                Ui(38f),
                Ui(34f));

        var actionPos =
            cardOrigin +
            new Vector2(
                Ui(14f),
                cardHeight -
                Ui(48f));

        var actionWidth =
            cardWidth -
            Ui(14f) -
            Ui(14f) -
            menuSize.X -
            Ui(8f);

        //
        // Activate / Active Queue button.
        //
        ImGui.SetCursorScreenPos(
            actionPos);

        var actionClicked =
            ImGui.InvisibleButton(
                "##queueAction",
                new Vector2(
                    actionWidth,
                    menuSize.Y));

        var actionHovered =
            ImGui.IsItemHovered();

        var actionMin =
            ImGui.GetItemRectMin();

        var actionMax =
            ImGui.GetItemRectMax();

        var actionColor =
            isActive
                ? new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    actionHovered
                        ? 0.26f
                        : 0.18f)
                : actionHovered
                    ? new Vector4(
                        0.10f,
                        0.11f,
                        0.17f,
                        1f)
                    : new Vector4(
                        0.075f,
                        0.085f,
                        0.125f,
                        1f);

        drawList.AddRectFilled(
            actionMin,
            actionMax,
            ImGui.GetColorU32(
                actionColor),
            Ui(7f));

        var actionText =
            isActive
                ? "Active Queue"
                : "Activate";

        var actionTextSize =
            ImGui.CalcTextSize(
                actionText);

        var actionTextX =
            actionMin.X +
            (
                actionWidth -
                actionTextSize.X
            ) *
            0.5f;

        if (isActive)
        {
            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                var checkGlyph =
                    FontAwesomeIcon.Check
                        .ToIconString();

                var checkSize =
                    ImGui.CalcTextSize(
                        checkGlyph);

                var combinedWidth =
                    checkSize.X +
                    Ui(7f) +
                    actionTextSize.X;

                var combinedStartX =
                    actionMin.X +
                    (
                        actionWidth -
                        combinedWidth
                    ) *
                    0.5f;

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    new Vector2(
                        combinedStartX,
                        actionMin.Y +
                        (
                            menuSize.Y -
                            checkSize.Y
                        ) *
                        0.5f),
                    ImGui.GetColorU32(
                        Accent),
                    checkGlyph);

                actionTextX =
                    combinedStartX +
                    checkSize.X +
                    Ui(7f);
            }
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                actionTextX,
                actionMin.Y +
                (
                    menuSize.Y -
                    actionTextSize.Y
                ) *
                0.5f),
            ImGui.GetColorU32(
                isActive
                    ? Vector4.One
                    : Accent),
            actionText);

        if (actionClicked &&
            !isActive)
        {
            queueManager.Activate(
                slotIndex);
        }

        //
        // Queue options button.
        //
        ImGui.SetCursorScreenPos(
            actionPos +
            new Vector2(
                actionWidth +
                Ui(8f),
                0f));

        var optionsClicked =
            ImGui.InvisibleButton(
                "##queueOptions",
                menuSize);

        var optionsHovered =
            ImGui.IsItemHovered();

        var optionsMin =
            ImGui.GetItemRectMin();

        var optionsMax =
            ImGui.GetItemRectMax();

        drawList.AddRectFilled(
            optionsMin,
            optionsMax,
            ImGui.GetColorU32(
                optionsHovered
                    ? new Vector4(
                        0.12f,
                        0.10f,
                        0.19f,
                        1f)
                    : new Vector4(
                        0.075f,
                        0.085f,
                        0.13f,
                        1f)),
            Ui(7f));

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var optionsGlyph =
                FontAwesomeIcon.EllipsisV
                    .ToIconString();

            var optionsGlyphSize =
                ImGui.CalcTextSize(
                    optionsGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                optionsMin +
                new Vector2(
                    (
                        menuSize.X -
                        optionsGlyphSize.X
                    ) *
                    0.5f,
                    (
                        menuSize.Y -
                        optionsGlyphSize.Y
                    ) *
                    0.5f),
                ImGui.GetColorU32(
                    optionsHovered
                        ? Vector4.One
                        : MutedText),
                optionsGlyph);
        }

        if (optionsClicked)
        {
            ImGui.OpenPopup(
                "##queueSlotOptions");
        }

        if (ImGui.BeginPopup(
               "##queueSlotOptions"))
        {
            if (ImGui.MenuItem(
                    "Edit queue"))
            {
                OpenQueueEditor(
                    slotIndex,
                    profile);
            }

            ImGui.Separator();

            if (ImGui.MenuItem(
                    "Delete queue"))
            {
                deletingQueueIndex =
                    slotIndex;

                deletingQueueName =
                    profile.Name;

                deleteQueuePopupOpen =
                    true;
            }

            ImGui.EndPopup();
        }
    }

    private static void DrawQueueProfileIcon(
        string? iconKey,
        Vector2 origin,
        Vector2 size,
        Vector4 color)
    {
        var icon =
      iconKey switch
      {
          "Film" => FontAwesomeIcon.Film,
          "Music" => FontAwesomeIcon.Music,
          "Gamepad" => FontAwesomeIcon.Gamepad,
          "List" => FontAwesomeIcon.List,
          "Clapperboard" => FontAwesomeIcon.Clapperboard,

          "BookOpen" => FontAwesomeIcon.BookOpen,
          "Globe" => FontAwesomeIcon.Globe,
          "Bolt" => FontAwesomeIcon.Bolt,
          "Lightbulb" => FontAwesomeIcon.Lightbulb,
          "BroadcastTower" => FontAwesomeIcon.BroadcastTower,
          "Fire" => FontAwesomeIcon.Fire,

          "Heart" => FontAwesomeIcon.Heart,
          "Smile" => FontAwesomeIcon.Smile,
          "Images" => FontAwesomeIcon.Images,
          "Headphones" => FontAwesomeIcon.Headphones,
          "Video" => FontAwesomeIcon.Video,
          "Crown" => FontAwesomeIcon.Crown,

          _ => FontAwesomeIcon.Tv
      };

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var glyph =
                icon.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            ImGui.GetWindowDrawList()
                .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    origin +
                    new Vector2(
                        (
                            size.X -
                            glyphSize.X
                        ) *
                        0.5f,
                        (
                            size.Y -
                            glyphSize.Y
                        ) *
                        0.5f),
                    ImGui.GetColorU32(
                        color),
                    glyph);
        }
    }

    private void OpenQueueEditor(
        int slotIndex,
        SavedQueueProfile? profile)
    {
        creatingQueueIndex =
            slotIndex;

        editingQueueProfile =
            profile is not null;

        newQueueName =
            profile?.Name ??
            string.Empty;

        newQueueIcon =
            NormalizeQueueIconKey(
                profile?.Icon);

        queueEditorError =
            null;

        createQueuePopupOpen =
            true;
    }

    private static string NormalizeQueueIconKey(
     string? iconKey) =>
     iconKey switch
     {
         "Film" => "Film",
         "Music" => "Music",
         "Gamepad" => "Gamepad",
         "List" => "List",
         "Clapperboard" => "Clapperboard",

         "BookOpen" => "BookOpen",
         "Globe" => "Globe",
         "Bolt" => "Bolt",
         "Lightbulb" => "Lightbulb",
         "BroadcastTower" => "BroadcastTower",
         "Fire" => "Fire",

         "Heart" => "Heart",
         "Smile" => "Smile",
         "Images" => "Images",
         "Headphones" => "Headphones",
         "Video" => "Video",
         "Crown" => "Crown",

         _ => "Tv"
     };

    private void DrawQueue()
    {
        DrawSavedQueueSlots();

        var count =
    queue.Entries.Count(
        entry =>
            !entry.IsTransient);



        // ---------------------------------------------------------
        // Header
        // ---------------------------------------------------------

        SetUiFontScale(1.15f);

        ImGui.TextColored(
           Vector4.One,
           "CURRENT QUEUE");

        SetUiFontScale(1f);

        if (queueManager.ActiveProfile is { } activeProfile)
        {
            ImGui.SameLine(0f, 10f);

            using (ImRaii.PushColor(
                       ImGuiCol.Text,
                       Vector4.One)
                   .Push(
                       ImGuiCol.Button,
                       Accent)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       Accent)
                   .Push(
                       ImGuiCol.ButtonActive,
                       Accent))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       10f)
                   .Push(
                       ImGuiStyleVar.FramePadding,
                       UiVec(10f, 3f)))
            {
                ImGui.SmallButton(
                    $"{activeProfile.Name}##activeQueueName");
            }
        }

        ImGui.SameLine(0f, 8f);

        SetUiFontScale(0.72f);

        ImGui.TextColored(
            MutedText,
            count == 1
                ? "1 video"
                : $"{count} videos");

        SetUiFontScale(1f);

        // Autoplay toggle and Clear Queue are aligned on the right.

        var clearSize =
            UiVec(112f, 32f);

        var toggleWidth =
            Ui(42f);

        var toggleHeight =
            Ui(22f);

        var toggleLabel =
            "Autoplay";

        var toggleLabelWidth =
            ImGui.CalcTextSize(
                toggleLabel).X;

        var rightControlsWidth =
            toggleLabelWidth +
            Ui(7f) +
            toggleWidth +
            (count > 0
                ? Ui(12f) +
                  clearSize.X
                : 0f);

        ImGui.SameLine(
            ImGui.GetContentRegionMax().X -
            rightControlsWidth);

        ImGui.TextColored(
            MutedText,
            toggleLabel);

        ImGui.SameLine(
            0f,
            Ui(7f));

        var togglePosition =
            ImGui.GetCursorScreenPos();

        var autoplayEnabled =
            Plugin.Cfg.AutoPlayNextQueueVideo;

        ImGui.InvisibleButton(
            "##queueAutoplayToggle",
            new Vector2(
                toggleWidth,
                toggleHeight));

        var toggleHovered =
            ImGui.IsItemHovered();

        if (ImGui.IsItemClicked())
        {
            Plugin.Cfg.AutoPlayNextQueueVideo =
                !Plugin.Cfg.AutoPlayNextQueueVideo;

            Plugin.Cfg.Save();

            autoplayEnabled =
                Plugin.Cfg.AutoPlayNextQueueVideo;
        }

        var toggleDrawList =
            ImGui.GetWindowDrawList();

        toggleDrawList.AddRectFilled(
            togglePosition,
            togglePosition +
            new Vector2(
                toggleWidth,
                toggleHeight),
            ImGui.GetColorU32(
                autoplayEnabled
                    ? Accent
                    : new Vector4(
                        0.15f,
                        0.17f,
                        0.23f,
                        1f)),
            toggleHeight *
            0.5f);

        var knobRadius =
            toggleHeight *
            0.5f -
            Ui(3f);

        var knobCenter =
            new Vector2(
                autoplayEnabled
                    ? togglePosition.X +
                      toggleWidth -
                      toggleHeight *
                      0.5f
                    : togglePosition.X +
                      toggleHeight *
                      0.5f,
                togglePosition.Y +
                toggleHeight *
                0.5f);

        toggleDrawList.AddCircleFilled(
            knobCenter,
            knobRadius,
            ImGui.GetColorU32(
                Vector4.One),
            24);

        if (toggleHovered)
        {
            ImGui.SetTooltip(
                "Auto play next video in queue when media ends");
        }

        if (count > 0)
        {

            ImGui.SameLine(
                0f,
                Ui(12f));

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(0.045f, 0.055f, 0.09f, 1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(0.065f, 0.08f, 0.125f, 1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(0.075f, 0.09f, 0.14f, 1f)))
            {
                var buttonPos =
                    ImGui.GetCursorScreenPos();

                if (ImGui.Button(
                     "##clearQueue",
                     clearSize))
                {
                    clearingQueueName =
                        queueManager.ActiveProfile?.Name ??
                        "Current Queue";

                    clearingQueueVideoCount =
                        queue.Entries.Count;

                    clearQueuePopupOpen =
                        true;
                }

                DrawPlayerActionButtonContent(
                    buttonPos,
                    clearSize,
                    FontAwesomeIcon.Trash,
                    "Clear queue",
                    MutedText);
            }
        }

        // ---------------------------------------------------------
        // Resume queue
        // ---------------------------------------------------------

        var isViewingWatchParty =
      stream.Mode ==
      StreamMode.Viewing;

        var canResumeQueue =
            count > 0 &&
            queue.Current is null &&
            !isViewingWatchParty;

        var resumeQueueLabel =
            stream.Mode switch
            {
                StreamMode.Hosting =>
                    "Resume playing queue in watch party",

                StreamMode.Viewing =>
                    "Leave watch party to resume queue",

                _ =>
                    "Resume playing queue"
            };

        var resumeButtonHeight = Ui(38f);

        var resumeButtonSize =
            new Vector2(
                ImGui.GetContentRegionAvail().X,
                resumeButtonHeight);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   canResumeQueue
                       ? Accent
                       : new Vector4(
                           0.055f,
                           0.065f,
                           0.09f,
                           1f))
               .Push(
                   ImGuiCol.ButtonHovered,
                   canResumeQueue
                       ? AccentHover
                       : new Vector4(
                           0.055f,
                           0.065f,
                           0.09f,
                           1f))
               .Push(
                   ImGuiCol.ButtonActive,
                   canResumeQueue
                       ? AccentActive
                       : new Vector4(
                           0.055f,
                           0.065f,
                           0.09f,
                           1f))
               .Push(
                   ImGuiCol.Text,
                   canResumeQueue
                       ? Vector4.One
                       : MutedText))
        {
            if (!canResumeQueue)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button(
                    resumeQueueLabel,
                    resumeButtonSize))
            {
                queue.Advance();
            }

            if (!canResumeQueue)
            {
                ImGui.EndDisabled();
            }
        }

        ImGui.Dummy(
            UiVec(0f, 14f));

        ImGui.Dummy(UiVec(0f, 14f));

        // ---------------------------------------------------------
        // Empty state
        // ---------------------------------------------------------

        if (count == 0)
        {
            SetUiFontScale(0.9f);

            ImGui.TextColored(
                MutedText,
                "Nothing queued yet.");

            SetUiFontScale(1f);

            ImGui.Dummy(UiVec(0f, 4f));

            SetUiFontScale(0.78f);

            ImGui.TextColored(
                MutedText,
                "Add videos from Link, YouTube, Twitch, or Discover.");

            SetUiFontScale(1f);

            return;
        }

        // ---------------------------------------------------------
        // Scrollable queue list
        // ---------------------------------------------------------

        var queueListHeight = MathF.Max(
            120f,
            ImGui.GetContentRegionAvail().Y - 4f);

        using var child = ImRaii.Child(
            "##queueList",
            new Vector2(-1f, queueListHeight),
            false,
            ImGuiWindowFlags.None);

        if (!child)
        {
            return;
        }

        for (var index = 0; index < queue.Entries.Count; index++)
        {
            var entry =
                queue.Entries[index];

            if (entry.IsTransient)
            {
                continue;
            }

            var isNowPlaying =
            ReferenceEquals(
        entry,
        queue.Current);

            var (currentPosition, currentDuration, _) =
                isNowPlaying
                    ? video.GetProgress()
                    : (
                        (float)entry.ResumePositionSeconds,
                        (float)(
                            entry.Duration?.TotalSeconds ??
                            0d),
                        true
                    );

            ImGui.PushID(index);

            var rowHeight = Ui(66f);

            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(0.045f, 0.06f, 0.10f, 1f)))
            using (var row = ImRaii.Child(
                $"##queue_{entry.Id}",
                new Vector2(Ui(-10f), rowHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (row)
                {
                    var rowOrigin =
                        ImGui.GetCursorScreenPos();

                    var rowWidth =
                        ImGui.GetWindowWidth();

                    var drawList =
                        ImGui.GetWindowDrawList();

                    // -------------------------------------------------
                    // Thumbnail
                    // -------------------------------------------------

                    var thumbWidth = Ui(118f);
                    var thumbHeight = rowHeight;

                    var thumbnail =
                        thumbnails.Get(entry.ThumbnailUrl);

                    if (thumbnail is not null)
                    {
                        drawList.AddImageRounded(
                            thumbnail.Handle,
                            rowOrigin,
                            rowOrigin + new Vector2(
                                thumbWidth,
                                thumbHeight),
                            Vector2.Zero,
                            Vector2.One,
                            uint.MaxValue,
                            8f);
                    }

                    var hasSavedProgress =
                        entry.ResumePositionSeconds > 0.5d &&
                        currentDuration > 0f;

                    if (isNowPlaying ||
                        hasSavedProgress)
                    {
                        var progress =
                            currentDuration > 0f
                                ? Math.Clamp(
                                    currentPosition /
                                    currentDuration,
                                    0f,
                                    1f)
                                : 0f;

                        var progressHeight =
                            Ui(4f);

                        var progressMin =
                            new Vector2(
                                rowOrigin.X,
                                rowOrigin.Y +
                                thumbHeight -
                                progressHeight);

                        var progressMax =
                            new Vector2(
                                rowOrigin.X +
                                thumbWidth,
                                rowOrigin.Y +
                                thumbHeight);

                        drawList.AddRectFilled(
                            progressMin,
                            progressMax,
                            ImGui.GetColorU32(
                                new Vector4(
                                    0.12f,
                                    0.13f,
                                    0.18f,
                                    1f)),
                            0f);

                        drawList.AddRectFilled(
                            progressMin,
                            new Vector2(
                                progressMin.X +
                                thumbWidth *
                                progress,
                                progressMax.Y),
                            ImGui.GetColorU32(
                                Accent),
                            0f);
                    }

                    // -------------------------------------------------
                    // Text
                    // -------------------------------------------------

                    var contentX =
                        rowOrigin.X +
                        thumbWidth +
                        12f;

                    // Keep this much space free for all right-side
                    // controls, regardless of which arrows are visible.
                    var controlsWidth = Ui(150f);

                    var textWidth =
                        MathF.Max(
                            80f,
                            rowWidth -
                            thumbWidth -
                            controlsWidth -
                            28f);

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            rowOrigin.Y + Ui(9f)));

                    ImGui.PushTextWrapPos(
                        contentX + textWidth);

                    ImGui.TextColored(
                        Vector4.One,
                        entry.Title);

                    ImGui.PopTextWrapPos();

                    var meta =
                        $"{entry.Source}  •  " +
                        (entry.Duration is { } duration
                            ? FormatTime(
                                (float)duration.TotalSeconds)
                            : "Live");

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            rowOrigin.Y + Ui(37f)));

                    SetUiFontScale(0.88f);

                    ImGui.TextColored(
                        MutedText,
                        meta);

                    SetUiFontScale(1f);

                    // -------------------------------------------------
                    // Fixed right-side controls
                    // -------------------------------------------------

                    // Everything is positioned from the right edge.
                    // This means missing Up/Down arrows never move the
                    // dots or any of the other controls.

                    var rightPadding = Ui(12f);
                    var iconSize = Ui(22f);
                    var iconGap = Ui(4f);

                    var controlsY =
                        rowOrigin.Y +
                        (rowHeight - iconSize) * 0.5f;

                    // Vertical dots are ALWAYS fixed to the far right.
                    var menuX =
                        rowOrigin.X +
                        rowWidth -
                        rightPadding -
                        iconSize;

                    // Down arrow always owns this slot.
                    var downX =
                        menuX -
                        iconGap -
                        iconSize;

                    // Up arrow always owns this slot.
                    var upX =
                        downX -
                        iconGap -
                        iconSize;

                    // -------------------------------------------------
                    // Menu dots
                    // -------------------------------------------------

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            menuX,
                            controlsY));

                    if (DrawQueueGhostIcon(
                        $"##queueMenuButton_{entry.Id}",
                        FontAwesomeIcon.EllipsisV))
                    {
                        ImGui.OpenPopup(
                            $"queueMenu_{entry.Id}");
                    }

                    if (ImGui.BeginPopup(
                        $"queueMenu_{entry.Id}"))
                    {
                        if (ImGui.MenuItem("Remove"))
                        {
                            queue.Remove(entry);
                        }

                        ImGui.EndPopup();
                    }

                    // -------------------------------------------------
                    // Down
                    // -------------------------------------------------

                    if (index < queue.Entries.Count - 1)
                    {
                        ImGui.SetCursorScreenPos(
                            new Vector2(
                                downX,
                                controlsY));

                        if (DrawQueueGhostIcon(
                            $"##queueDown_{entry.Id}",
                            FontAwesomeIcon.ChevronDown))
                        {
                            queue.Reorder(
                                index,
                                index + 1);
                        }
                    }

                    // -------------------------------------------------
                    // Up
                    // -------------------------------------------------

                    if (index > 0)
                    {
                        ImGui.SetCursorScreenPos(
                            new Vector2(
                                upX,
                                controlsY));

                        if (DrawQueueGhostIcon(
                            $"##queueUp_{entry.Id}",
                            FontAwesomeIcon.ChevronUp))
                        {
                            queue.Reorder(
                                index,
                                index - 1);
                        }
                    }

                    // -------------------------------------------------
                    // Queue position / Up Next badge
                    // -------------------------------------------------

                    var upcomingPosition =
                        queue.Current is not null
                            ? index
                            : index + 1;

                    var pillText =
                        isNowPlaying
                            ? "Now Playing"
                            : upcomingPosition == 1
                                ? "Up Next"
                                : $"{upcomingPosition}";

                    var pillWidth =
                        isNowPlaying
                            ? 82f
                            : upcomingPosition == 1
                                ? 58f
                                : 28f;

                    var pillSize =
                        new Vector2(
                            pillWidth,
                            Ui(22f));

                    // The badge ends just before the Up-arrow slot.
                    var pillX =
                        upX -
                        8f -
                        pillWidth;

                    var pillY =
                        rowOrigin.Y +
                        (rowHeight - pillSize.Y) * 0.5f;

                    var pillPos =
                        new Vector2(
                            pillX,
                            pillY);

                    drawList.AddRectFilled(
                        pillPos,
                        pillPos + pillSize,
                        ImGui.GetColorU32(
                            new Vector4(
                                0.06f,
                                0.075f,
                                0.125f,
                                1f)),
                        6f);

                    var pillTextSize =
                        ImGui.CalcTextSize(
                            pillText);

                    drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                        pillPos +
                        new Vector2(
                            (pillSize.X - pillTextSize.X) * 0.5f,
                            (pillSize.Y - pillTextSize.Y) * 0.5f),
                        ImGui.GetColorU32(
                            isNowPlaying ||
upcomingPosition == 1
    ? Accent
    : MutedText),
                        pillText);
                }
            }

            ImGui.PopID();

            ImGui.Dummy(
                UiVec(0f, 8f));
        }
    }

    private void DrawCreateQueuePopup()
    {
        if (!createQueuePopupOpen)
        {
            return;
        }

        var popupWidth = Ui(470f);
        var popupHeight = Ui(430f);
        const float padding = 20f;

        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            parentPos +
            new Vector2(
                (
                    parentSize.X -
                    popupWidth
                ) *
                0.5f,
                (
                    parentSize.Y -
                    popupHeight
                ) *
                0.5f);

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!ImGui.Begin(
                "##queueEditorOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            parentPos,
            parentPos +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.48f)));

        drawList.AddRectFilled(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    0.055f,
                    0.065f,
                    0.11f,
                    1f)),
            10f);

        drawList.AddRect(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.45f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        var contentWidth =
            popupWidth -
            padding *
            2f;

        var headerIconOrigin =
            popupPos +
            new Vector2(
                padding,
                Ui(13f));

        DrawQueueProfileIcon(
            newQueueIcon,
            headerIconOrigin,
            UiVec(38f, 38f),
            Accent);

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        SetUiFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            editingQueueProfile
                ? "Edit queue"
                : "Create a new queue");

        SetUiFontScale(1f);

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(67f)));

        ImGui.TextColored(
            MutedText,
            "Queue name");

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(89f)));

        ImGui.SetNextItemWidth(
            contentWidth);

        using (ImRaii.PushColor(
                   ImGuiCol.FrameBg,
                   new Vector4(
                       0.025f,
                       0.03f,
                       0.055f,
                       1f))
                   .Push(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.65f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f)
                   .Push(
                       ImGuiStyleVar.FrameRounding,
                       5f))
        {
            ImGui.InputTextWithHint(
                "##queueName",
                "For example: Movie Night",
                ref newQueueName,
                64);
        }

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(135f)));

        ImGui.TextColored(
            MutedText,
            "Queue icon");

        var iconChoices =
     new (string Key, FontAwesomeIcon Icon)[]
     {
                // General media
                ("Tv", FontAwesomeIcon.Tv),
                ("Film", FontAwesomeIcon.Film),
                ("Music", FontAwesomeIcon.Music),
                ("Gamepad", FontAwesomeIcon.Gamepad),
                ("List", FontAwesomeIcon.List),
                ("Clapperboard", FontAwesomeIcon.Clapperboard),

                // Fantasy, science fiction, technology, and action
                ("BookOpen", FontAwesomeIcon.BookOpen),
                ("Globe", FontAwesomeIcon.Globe),
                ("Bolt", FontAwesomeIcon.Bolt),
                ("Lightbulb", FontAwesomeIcon.Lightbulb),
                ("BroadcastTower", FontAwesomeIcon.BroadcastTower),
                ("Fire", FontAwesomeIcon.Fire),

                // Romance, comedy, art, audio, video, and royalty
                ("Heart", FontAwesomeIcon.Heart),
                ("Smile", FontAwesomeIcon.Smile),
                ("Images", FontAwesomeIcon.Images),
                ("Headphones", FontAwesomeIcon.Headphones),
                ("Video", FontAwesomeIcon.Video),
                ("Crown", FontAwesomeIcon.Crown)
     };

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(159f)));

        for (var i = 0;
      i < iconChoices.Length;
      i++)
        {
            if (i > 0)
            {
                if (i % 6 == 0)
                {
                    ImGui.SetCursorScreenPos(
                        popupPos +
                        new Vector2(
                            padding,
                            Ui(159f) +
                            (
                                i /
                                6
                            ) *
                            Ui(50f)));
                }
                else
                {
                    ImGui.SameLine(
                        0f,
                        8f);
                }
            }

            var choice =
                iconChoices[i];

            var selected =
                newQueueIcon ==
                choice.Key;

            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       selected
                           ? Accent
                           : new Vector4(
                               0.075f,
                               0.085f,
                               0.13f,
                               1f))
                       .Push(
                           ImGuiCol.ButtonHovered,
                           selected
                               ? AccentHover
                               : new Vector4(
                                   0.11f,
                                   0.12f,
                                   0.19f,
                                   1f))
                       .Push(
                           ImGuiCol.ButtonActive,
                           selected
                               ? AccentActive
                               : new Vector4(
                                   0.13f,
                                   0.14f,
                                   0.22f,
                                   1f)))
            {
                if (ImGui.Button(
                        $"{choice.Icon.ToIconString()}##queueIcon_{choice.Key}",
                        UiVec(52f, 42f)))
                {
                    newQueueIcon =
                        choice.Key;
                }
            }
        }

        if (queueEditorError is { } error)
        {
            ImGui.SetCursorScreenPos(
                popupPos +
                new Vector2(
                    padding,
                    Ui(314f)));

            ImGui.TextColored(
                Danger,
                error);
        }

        var dividerY =
            popupMax.Y -
            65f;

        drawList.AddLine(
            new Vector2(
                popupPos.X +
                padding,
                dividerY),
            new Vector2(
                popupMax.X -
                padding,
                dividerY),
            ImGui.GetColorU32(
                BorderSubtle));

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        var cancelRequested =
            ImGui.Button(
                "Cancel",
                new Vector2(
                    buttonWidth,
                    Ui(36f)));

        ImGui.SameLine(
            0f,
            buttonGap);

        var saveRequested =
            false;

        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   Accent)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       AccentHover)
                   .Push(
                       ImGuiCol.ButtonActive,
                       AccentActive))
        {
            saveRequested =
                ImGui.Button(
                    editingQueueProfile
                        ? "Save changes"
                        : "Create queue",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        if (cancelRequested)
        {
            createQueuePopupOpen = false;
            creatingQueueIndex = -1;
            editingQueueProfile = false;
            newQueueName = string.Empty;
            newQueueIcon = "Tv";
            queueEditorError = null;
        }
        else if (saveRequested)
        {
            if (string.IsNullOrWhiteSpace(
                    newQueueName))
            {
                queueEditorError =
                    "Please enter a queue name.";
            }
            else
            {
                var succeeded =
                    editingQueueProfile
                        ? queueManager.UpdateDetails(
                            creatingQueueIndex,
                            newQueueName,
                            newQueueIcon)
                        : queueManager.Create(
                            creatingQueueIndex,
                            newQueueName,
                            newQueueIcon);

                if (succeeded)
                {
                    createQueuePopupOpen = false;
                    creatingQueueIndex = -1;
                    editingQueueProfile = false;
                    newQueueName = string.Empty;
                    newQueueIcon = "Tv";
                    queueEditorError = null;
                }
                else
                {
                    queueEditorError =
                        "That queue slot could not be updated.";
                }
            }
        }

        ImGui.End();
    }

    private void DrawDeleteQueuePopup()
    {
        if (!deleteQueuePopupOpen)
        {
            return;
        }

        var popupWidth = Ui(470f);
        var popupHeight = Ui(255f);
        const float padding = 20f;

        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            parentPos +
            new Vector2(
                (
                    parentSize.X -
                    popupWidth
                ) *
                0.5f,
                (
                    parentSize.Y -
                    popupHeight
                ) *
                0.5f);

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!ImGui.Begin(
                "##deleteQueueOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            parentPos,
            parentPos +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.48f)));

        drawList.AddRectFilled(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    0.055f,
                    0.065f,
                    0.11f,
                    1f)),
            10f);

        drawList.AddRect(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.55f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        var iconCenter =
            popupPos +
            new Vector2(
                padding +
                Ui(18f),
                Ui(31f));

        drawList.AddCircleFilled(
            iconCenter,
            18f,
            ImGui.GetColorU32(
                new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.18f)),
            24);

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var trashGlyph =
                FontAwesomeIcon.Trash
                    .ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    trashGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconCenter -
                glyphSize *
                0.5f,
                ImGui.GetColorU32(
                    Danger),
                trashGlyph);
        }

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        SetUiFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Delete queue?");

        SetUiFontScale(1f);

        var contentWidth =
            popupWidth -
            padding *
            2f;

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(72f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        ImGui.TextColored(
            MutedText,
            $"Deleting \"{deletingQueueName}\" will permanently remove all videos saved in that queue.");

        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(123f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        ImGui.TextColored(
            MutedText,
            "A video already playing will continue. If this is the active queue, another available queue will become active.");

        ImGui.PopTextWrapPos();

        var dividerY =
            popupMax.Y -
            65f;

        drawList.AddLine(
            new Vector2(
                popupPos.X +
                padding,
                dividerY),
            new Vector2(
                popupMax.X -
                padding,
                dividerY),
            ImGui.GetColorU32(
                BorderSubtle));

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        var cancelRequested =
            false;

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       0.08f,
                       0.08f,
                       0.14f,
                       1f))
                   .Push(
                       ImGuiCol.ButtonHovered,
                       new Vector4(
                           0.14f,
                           0.11f,
                           0.22f,
                           1f))
                   .Push(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           0.18f,
                           0.13f,
                           0.28f,
                           1f)))
        {
            cancelRequested =
                ImGui.Button(
                    "Cancel",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        ImGui.SameLine(
            0f,
            buttonGap);

        var deleteRequested =
            false;

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       Danger.X * 0.65f,
                       Danger.Y * 0.65f,
                       Danger.Z * 0.65f,
                       1f))
                   .Push(
                       ImGuiCol.ButtonHovered,
                       Danger)
                   .Push(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           Danger.X * 0.8f,
                           Danger.Y * 0.8f,
                           Danger.Z * 0.8f,
                           1f)))
        {
            deleteRequested =
                ImGui.Button(
                    "Delete queue",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        if (cancelRequested)
        {
            deleteQueuePopupOpen = false;
            deletingQueueIndex = -1;
            deletingQueueName = string.Empty;
        }
        else if (deleteRequested)
        {
            queueManager.Delete(
                deletingQueueIndex);

            deleteQueuePopupOpen = false;
            deletingQueueIndex = -1;
            deletingQueueName = string.Empty;
        }

        ImGui.End();
    }

    private void DrawClearQueuePopup()
    {
        if (!clearQueuePopupOpen)
        {
            return;
        }

        var popupWidth = Ui(470f);
        var popupHeight = Ui(245f);
        const float padding = 20f;

        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            parentPos +
            new Vector2(
                (
                    parentSize.X -
                    popupWidth
                ) *
                0.5f,
                (
                    parentSize.Y -
                    popupHeight
                ) *
                0.5f);

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        var contentWidth =
            popupWidth -
            padding *
            2f;

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!ImGui.Begin(
                "##clearQueueOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            parentPos,
            parentPos +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.48f)));

        drawList.AddRectFilled(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    0.055f,
                    0.065f,
                    0.11f,
                    1f)),
            10f);

        drawList.AddRect(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.55f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        var iconCenter =
            popupPos +
            new Vector2(
                padding +
                Ui(18f),
                Ui(31f));

        drawList.AddCircleFilled(
            iconCenter,
            18f,
            ImGui.GetColorU32(
                new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.18f)),
            24);

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var trashGlyph =
                FontAwesomeIcon.Trash
                    .ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    trashGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconCenter -
                glyphSize *
                0.5f,
                ImGui.GetColorU32(
                    Danger),
                trashGlyph);
        }

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        SetUiFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Clear active queue?");

        SetUiFontScale(1f);

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(75f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        ImGui.TextColored(
            MutedText,
            $"This will remove all {clearingQueueVideoCount} videos from \"{clearingQueueName}\".");

        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(118f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        ImGui.TextColored(
            MutedText,
            "A video already playing will continue, but every upcoming video in this queue will be removed.");

        ImGui.PopTextWrapPos();

        var dividerY =
            popupMax.Y -
            65f;

        drawList.AddLine(
            new Vector2(
                popupPos.X +
                padding,
                dividerY),
            new Vector2(
                popupMax.X -
                padding,
                dividerY),
            ImGui.GetColorU32(
                BorderSubtle));

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        var cancelRequested =
            ImGui.Button(
                "Cancel",
                new Vector2(
                    buttonWidth,
                    Ui(36f)));

        ImGui.SameLine(
            0f,
            buttonGap);

        var clearRequested =
            false;

        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       Danger.X * 0.65f,
                       Danger.Y * 0.65f,
                       Danger.Z * 0.65f,
                       1f))
                   .Push(
                       ImGuiCol.ButtonHovered,
                       Danger)
                   .Push(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           Danger.X * 0.8f,
                           Danger.Y * 0.8f,
                           Danger.Z * 0.8f,
                           1f)))
        {
            clearRequested =
                ImGui.Button(
                    "Clear queue",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        if (cancelRequested)
        {
            clearQueuePopupOpen = false;
            clearingQueueName = string.Empty;
            clearingQueueVideoCount = 0;
        }
        else if (clearRequested)
        {
            //
            // Remove upcoming entries without stopping the video
            // that is currently playing.
            //
            foreach (var entry in
                     queue.Entries.ToList())
            {
                queue.Remove(entry);
            }

            clearQueuePopupOpen = false;
            clearingQueueName = string.Empty;
            clearingQueueVideoCount = 0;
        }

        ImGui.End();
    }

    private static bool DrawQueueGhostIcon(
        string id,
        FontAwesomeIcon icon)
    {
        const float size = 22f;

        var origin = ImGui.GetCursorScreenPos();

        var clicked = ImGui.InvisibleButton(
            id,
            new Vector2(size, size));

        var hovered = ImGui.IsItemHovered();

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);

            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                origin + new Vector2(
                    (size - glyphSize.X) * 0.5f,
                    (size - glyphSize.Y) * 0.5f),
                ImGui.GetColorU32(
                    hovered
                        ? Vector4.One
                        : MutedText),
                glyph);
        }

        return clicked;
    }
}