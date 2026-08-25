using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{

    private void DrawWatchPartyPage()
    {
        ImGui.Spacing();

        ImGui.Separator();

        ImGui.Spacing();

        if (stream.Mode == StreamMode.Hosting ||
            stream.Mode == StreamMode.Viewing)
        {
            DrawWatchPartyDrawer();
        }
        else
        {
            DrawWatchPartyLanding();
        }
    }

    private void DrawWatchPartyLanding()
    {
        DrawWatchPartyHero();

        DrawWatchPartyActions();

        ImGui.Spacing();
        ImGui.Spacing();

        DrawWatchPartyFeatures();
    }

    private void DrawWatchPartyHero()
    {
        using var hero =
    ImRaii.Child(
        "##watchPartyHero",
new Vector2(
    0,
    230),
        false,
        ImGuiWindowFlags.NoScrollbar |
        ImGuiWindowFlags.NoScrollWithMouse);

        if (!hero)
            return;

        var heroMin = ImGui.GetCursorScreenPos();

        var heroMax = new Vector2(
            heroMin.X + ImGui.GetContentRegionAvail().X,
            heroMin.Y + 230);

        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
      heroMin,
      heroMax,
      ImGui.GetColorU32(
          new Vector4(
              0.08f,
              0.05f,
              0.16f,
              1f)),
      18f);

        drawList.AddRect(
            heroMin,
            heroMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.55f)),
            18f,
            ImDrawFlags.RoundCornersAll,
            1.5f);


        // add padding inside hero
        ImGui.SetCursorScreenPos(
            heroMin + new Vector2(24, 24));


        var width =
            ImGui.GetContentRegionAvail().X;

        var previewWidth = 620f;

        var textWidth =
            width - previewWidth - 40f;


        using (ImRaii.Child(
            "##watchPartyHeroText",
            new Vector2(
                textWidth,
                190),
            false,
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {

            using (ImRaii.PushFont(UiBuilder.DefaultFont))
            {
                ImGui.SetWindowFontScale(1.65f);

                ImGui.TextColored(
                    Vector4.One,
                    "Watch together,");

                ImGui.TextColored(
                    Accent,
                    "anywhere in Eorzea.");

                ImGui.SetWindowFontScale(1f);
            }

            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.Spacing();

            ImGui.PushTextWrapPos(
                            ImGui.GetCursorPosX() + textWidth - 30);

            using (ImRaii.PushFont(UiBuilder.DefaultFont))
            {
                ImGui.SetWindowFontScale(1.25f);

                ImGui.TextColored(
                    MutedText,
                    "Create a room, invite friends, and enjoy videos with synced playback, chat, and live reactions.");

                ImGui.SetWindowFontScale(1f);
            }

            ImGui.PopTextWrapPos();
        }


        ImGui.SameLine();


        using (ImRaii.Child(
            "##watchPartyPreview",
            new Vector2(
    previewWidth,
    190),
                    false,
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            var innerWidth =
                ImGui.GetContentRegionAvail().X;

            var previewMin = ImGui.GetCursorScreenPos();

            var previewMax = new Vector2(
                previewMin.X + innerWidth,
                previewMin.Y + 190);

            drawList.AddRectFilled(
                previewMin,
                previewMax,
                ImGui.GetColorU32(
                    new Vector4(
                        0.04f,
                        0.04f,
                        0.08f,
                        1f)),
                14f);

            drawList.AddRect(
                previewMin,
                previewMax,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.35f)),
                14f,
                ImDrawFlags.RoundCornersAll,
                1f);

            var panelHeight = 190f;

            var panelMin = ImGui.GetCursorScreenPos();

            var panelMax = new Vector2(
                panelMin.X + innerWidth,
                panelMin.Y + panelHeight);

            drawList.AddRectFilled(
                panelMin,
                panelMax,
                ImGui.GetColorU32(
                    new Vector4(
                        0.04f,
                        0.04f,
                        0.08f,
                        1f)),
                14f);

            drawList.AddRect(
                panelMin,
                panelMax,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.35f)),
                14f,
                ImDrawFlags.RoundCornersAll,
                1f);


            using (ImRaii.Child(
                "##watchPreviewPanel",
                new Vector2(
                    innerWidth,
                    panelHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.SetCursorPos(
    Vector2.Zero);
                var panelWidth =
                    ImGui.GetContentRegionAvail().X;

                var imageWidth =
                    panelWidth * 0.60f;

                var chatWidth =
                    panelWidth - imageWidth - 12f;


                if (watchPartyHeaderImage is not null)
                {
                    var texture =
                        watchPartyHeaderImage.GetWrapOrEmpty();

                    ImGui.Image(
                        texture.Handle,
                        new Vector2(
                            innerWidth,
                            panelHeight));
                }
                drawList.AddRect(
        panelMin,
        panelMax,
        ImGui.GetColorU32(
            new Vector4(
                Accent.X,
                Accent.Y,
                Accent.Z,
                0.45f)),
        12f,
        ImDrawFlags.RoundCornersAll,
        1.5f);
            }
        }
    }
       
    

    private void DrawHeroFeature(
    string icon,
    string title,
    string description)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                Accent,
                icon);
        }

        ImGui.SameLine();

        ImGui.BeginGroup();

        ImGui.Text(title);

        ImGui.TextColored(
            MutedText,
            description);

        ImGui.EndGroup();
    }

   private void DrawWatchPartyActions()
{
    var width =
        ImGui.GetContentRegionAvail().X;

    var cardWidth =
        (width - 12f) / 2f;


        using (var start =
            ImRaii.Child(
                "##startParty",
                new Vector2(cardWidth, 325),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
        if (start)
        {
                var startMin = ImGui.GetCursorScreenPos();

                var startMax =
                    startMin + new Vector2(
                        cardWidth,
                        325);

                var startDraw =
                    ImGui.GetWindowDrawList();


                startDraw.AddRectFilled(
                    startMin,
                    startMax,
                    ImGui.GetColorU32(
                        new Vector4(
                            0.08f,
                            0.05f,
                            0.15f,
                            1f)),
                    16f);


                startDraw.AddRect(
                    startMin,
                    startMax,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.45f)),
                    16f,
                    ImDrawFlags.RoundCornersAll,
                    1.5f);
                //
                // Header
                //

                var iconPos =
                    ImGui.GetCursorScreenPos()
                    + new Vector2(18, 12);

                ImGui.SetCursorScreenPos(iconPos);


                ImGui.GetWindowDrawList().AddCircleFilled(
                    iconPos + new Vector2(24, 24),
                    24f,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.25f)));


                ImGui.SetCursorScreenPos(
                    iconPos + new Vector2(12, 12));


                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    ImGui.SetWindowFontScale(1.4f);

                    ImGui.TextColored(
                        Accent,
                        FontAwesomeIcon.Clapperboard.ToIconString());

                    ImGui.SetWindowFontScale(1f);
                }

                ImGui.SameLine(0, 18f);

                using (ImRaii.PushFont(UiBuilder.DefaultFont))
                {
                    ImGui.SetWindowFontScale(1.25f);

                    ImGui.BeginGroup();

                    ImGui.Text(
                        "Start a Watch Party");

                    ImGui.SetWindowFontScale(1f);

                    ImGui.TextColored(
                        MutedText,
                        "Pick a video and instantly create a room.");

                    ImGui.SetCursorPosY(
                        ImGui.GetCursorPosY() - 4);

                    ImGui.TextColored(
                        MutedText,
                        "or make a room and add content later.");

                    ImGui.EndGroup();
                    ImGui.SetCursorPosY(
    ImGui.GetCursorPosY() + 12);
                }


                ImGui.SetCursorPosY(
    ImGui.GetCursorPosY() - 12);


                var optionWidth =
    (cardWidth - 70f) / 2f;

                ImGui.SetCursorPosX(35f);


                //
                // Start Watching option
                //
                using (ImRaii.Child(
                    "##startWatchingOption",
                    new Vector2(
                        optionWidth,
                        200),
                    false,
                    ImGuiWindowFlags.NoBackground))
                {
                    var optionMin =
                        ImGui.GetCursorScreenPos();
                    var hovered =
    ImGui.IsMouseHoveringRect(
        optionMin,
        optionMin + new Vector2(optionWidth, 200));

                    ImGui.GetWindowDrawList().AddRectFilled(
                        optionMin,
                        optionMin + new Vector2(optionWidth, 200),
                        ImGui.GetColorU32(
hovered
    ? new Vector4(0.12f, 0.08f, 0.22f, 1f)
    : new Vector4(0.07f, 0.06f, 0.12f, 1f)),
                        12f);

                    ImGui.GetWindowDrawList().AddRect(
                        optionMin,
                        optionMin + new Vector2(optionWidth, 200),
                        ImGui.GetColorU32(
                            new Vector4(
                                Accent.X,
                                Accent.Y,
                                Accent.Z,
                                0.45f)),
                        12f,
                        ImDrawFlags.RoundCornersAll,
                        1f);

                    var center =
(optionWidth / 2f) - 16f;


                    ImGui.SetCursorPosX(center);
                    ImGui.SetCursorPosY(
    ImGui.GetCursorPosY() + 28);

                    using (ImRaii.PushFont(UiBuilder.IconFont))
                    {
                        ImGui.SetWindowFontScale(2f);

                        ImGui.TextColored(
                            Accent,
                            FontAwesomeIcon.Plus.ToIconString());

                        ImGui.SetWindowFontScale(1f);
                    }


                    ImGui.Spacing();


                ImGui.SetCursorPosX(
                    (optionWidth - ImGui.CalcTextSize("Start Watching").X) / 2f);

                ImGui.Text(
                    "Start Watching");


                ImGui.SetCursorPosX(
                    (optionWidth - ImGui.CalcTextSize("Choose a video").X) / 2f);

                ImGui.TextColored(
                    MutedText,
                    "Choose a video");


                ImGui.SetCursorPosX(
                    (optionWidth - ImGui.CalcTextSize("and watch together").X) / 2f);

                ImGui.TextColored(
                    MutedText,
                    "and watch together");
            }


            ImGui.SameLine();


                //
                // Create Room option
                //
                using (ImRaii.Child(
                    "##createRoomOption",
                    new Vector2(
                        optionWidth,
                        200),
                    false,
                    ImGuiWindowFlags.NoBackground))
                {
                    var optionMin =
                        ImGui.GetCursorScreenPos();
                    var hovered =
    ImGui.IsMouseHoveringRect(
        optionMin,
        optionMin + new Vector2(optionWidth, 200));


                    var optionMax =
                        optionMin + new Vector2(
                            optionWidth,
                            200);


                    var optionDraw =
                        ImGui.GetWindowDrawList();


                    optionDraw.AddRectFilled(
                        optionMin,
                        optionMax,
                        ImGui.GetColorU32(
                            hovered
    ? new Vector4(0.12f, 0.08f, 0.22f, 1f)
    : new Vector4(0.07f, 0.06f, 0.12f, 1f)),
                        12f);


                    optionDraw.AddRect(
                        optionMin,
                        optionMax,
                        ImGui.GetColorU32(
                            new Vector4(
                                Accent.X,
                                Accent.Y,
                                Accent.Z,
                                0.45f)),
                        12f,
                        ImDrawFlags.RoundCornersAll,
                        1f);


                    // center content vertically
                    ImGui.SetCursorPosY(
                        ImGui.GetCursorPosY() + 28);


                    ImGui.SetCursorPosX(
                        (optionWidth - 32) / 2f);


                    using (ImRaii.PushFont(UiBuilder.IconFont))
                    {
                        ImGui.SetWindowFontScale(2f);

                        ImGui.TextColored(
                            Accent,
                            FontAwesomeIcon.Plus.ToIconString());

                        ImGui.SetWindowFontScale(1f);
                    }


                    ImGui.Spacing();


                    ImGui.SetCursorPosX(
                        (optionWidth -
                        ImGui.CalcTextSize("Create Empty Room").X) / 2f);

                    ImGui.Text(
                        "Create Empty Room");


                    ImGui.SetCursorPosX(
                        (optionWidth -
                        ImGui.CalcTextSize("Create a room now").X) / 2f);

                    ImGui.TextColored(
                        MutedText,
                        "Create a room now");


                    ImGui.SetCursorPosX(
                        (optionWidth -
                        ImGui.CalcTextSize("and add videos later").X) / 2f);

                    ImGui.TextColored(
                        MutedText,
                        "and add videos later");
                    ImGui.SetCursorScreenPos(optionMin);

                    if (ImGui.InvisibleButton(
                            "##createEmptyRoom",
                            new Vector2(optionWidth, 200)))
                    {
                        CreateEmptyWatchParty();
                    }

                }

            }
    }


    ImGui.SameLine();


        //
        // Keep Join panel for now
        //
        using (var join =
        ImRaii.Child(
"##joinParty",
new Vector2(cardWidth, 325),
    false,
              ImGuiWindowFlags.NoScrollbar |
              ImGuiWindowFlags.NoScrollWithMouse))
        {
        if (join)
        {
                var joinMin = ImGui.GetCursorScreenPos();

                var joinMax =
joinMin + new Vector2(
    cardWidth,
    325);

                var joinDraw =
                    ImGui.GetWindowDrawList();

                joinDraw.AddRectFilled(
                    joinMin,
                    joinMax,
                    ImGui.GetColorU32(
                        new Vector4(
                            0.08f,
                            0.05f,
                            0.15f,
                            1f)),
                    16f);

                joinDraw.AddRect(
                    joinMin,
                    joinMax,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.45f)),
                    16f,
                    ImDrawFlags.RoundCornersAll,
                    1.5f);
                var joinIconPos =
                    ImGui.GetCursorScreenPos()
                    + new Vector2(18, 12);

                ImGui.GetWindowDrawList().AddCircleFilled(
                    joinIconPos + new Vector2(24, 24),
                    24f,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.25f)));

                ImGui.SetCursorScreenPos(
                    joinIconPos + new Vector2(12, 12));

                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    ImGui.SetWindowFontScale(1.4f);

                    ImGui.TextColored(
                        Accent,
                        FontAwesomeIcon.Users.ToIconString());

                    ImGui.SetWindowFontScale(1f);
                }

                ImGui.SameLine(0, 18f);

                using (ImRaii.PushFont(UiBuilder.DefaultFont))
                {
                    ImGui.SetWindowFontScale(1.25f);

                    ImGui.BeginGroup();

                    ImGui.Text(
                        "Join a Watch Party");

                    ImGui.SetWindowFontScale(1f);

                    ImGui.TextColored(
                        MutedText,
                        "Join a friend's room or discover public watch parties happening now.");

                    ImGui.EndGroup();
                }

                ImGui.Spacing();

                var inputWidth = cardWidth - 150;
                ImGui.SetCursorPosY(
     ImGui.GetCursorPosY() + 4);
                ImGui.SetCursorPosX(
    ImGui.GetCursorPosX() + 12);

                var joinButtonWidth = 72f;
                var joinGap = 10f;

                var joinInputWidth =
                    cardWidth - joinButtonWidth - joinGap - 64f;

                ImGui.SetNextItemWidth(joinInputWidth);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    10f)
                    .Push(
                        ImGuiStyleVar.FramePadding,
                        new Vector2(14f, 8f)))
                using (ImRaii.PushColor(
        ImGuiCol.FrameBg,
        new Vector4(0.04f, 0.04f, 0.08f, 1f)))
                using (ImRaii.PushColor(
                    ImGuiCol.FrameBgHovered,
                    new Vector4(0.10f, 0.07f, 0.18f, 1f)))
                using (ImRaii.PushColor(
                    ImGuiCol.FrameBgActive,
                    new Vector4(0.12f, 0.08f, 0.22f, 1f)))
                {
                    ImGui.InputTextWithHint(
                        "##hostName",
                        "Enter their AlphaChannel name",
                        ref joinHostNameInput,
                        32);
                }

                ImGui.SameLine(
    cardWidth - joinButtonWidth - 20f);

                if (ImGui.Button(
                    "Join",
                  new Vector2(72f, 34f)))
                {
                    DoJoin(joinHostNameInput);
                }
                if (joinError is { } error)
                {
                    var errorPos = ImGui.GetCursorScreenPos();

                    ImGui.SetCursorScreenPos(
                        errorPos + new Vector2(0, -6));

                    ImGui.TextColored(
                        Danger,
                        error);

                    ImGui.SetCursorScreenPos(errorPos);
                }

                ImGui.SetCursorPosY(
                    ImGui.GetCursorPosY() + 8);

                var optionWidth = (cardWidth - 70f) / 3f;

                void DrawJoinOptionCard(
     float width,
     string icon,
     string title,
     string description)
                {
                    var optionMin = ImGui.GetCursorScreenPos();

                    var optionMax = optionMin + new Vector2(
    width,
    130);

                    var hovered =
                        ImGui.IsMouseHoveringRect(
                            optionMin,
                            optionMax);

                    if (hovered)
                    {
                        ImGui.SetMouseCursor(
                            ImGuiMouseCursor.Hand);
                    }

                    var optionDraw =
                        ImGui.GetWindowDrawList();


                    optionDraw.AddRectFilled(
                        optionMin,
                        optionMax,
                        ImGui.GetColorU32(
                            hovered
                                ? new Vector4(0.12f, 0.08f, 0.22f, 1f)
                                : new Vector4(0.07f, 0.06f, 0.12f, 1f)),
                        12f);


                    optionDraw.AddRect(
                        optionMin,
                        optionMax,
                        ImGui.GetColorU32(
                            new Vector4(
                                Accent.X,
                                Accent.Y,
                                Accent.Z,
                                0.45f)),
                        12f,
                        ImDrawFlags.RoundCornersAll,
                        1f);


                    ImGui.SetCursorScreenPos(
                        optionMin + new Vector2(14, 14));


                    using (ImRaii.PushFont(UiBuilder.IconFont))
                    {
                        ImGui.SetWindowFontScale(1.2f);

                        ImGui.TextColored(
                            Accent,
                            icon);

                        ImGui.SetWindowFontScale(1f);
                    }


                    ImGui.SetCursorScreenPos(
                       optionMin + new Vector2(14, 48));


                    ImGui.Text(title);

                    ImGui.SetCursorScreenPos(
                        optionMin + new Vector2(14, 70));


                    ImGui.TextColored(
                        MutedText,
                        description);




                }


                var joinOptionWidth = 135f;

                var totalWidth =
    (joinOptionWidth * 3) + 10f * 2;

                ImGui.SetCursorPosX(
                    (cardWidth - totalWidth) / 2f);

                ImGui.SetCursorPosY(
                    ImGui.GetCursorPosY() + 18);


                var startX = ImGui.GetCursorPosX();
                var startY = ImGui.GetCursorPosY();

                DrawJoinOptionCard(
                    joinOptionWidth,
                    FontAwesomeIcon.Users.ToIconString(),
                    "Friends",
                    "See friends \nwith active rooms");

                ImGui.SetCursorPos(
                    new Vector2(
                        startX + joinOptionWidth + 10,
                        startY));

                DrawJoinOptionCard(
                    joinOptionWidth,
                    FontAwesomeIcon.Globe.ToIconString(),
                    "Public Rooms",
                    "Browse public \nwatch parties");

                ImGui.SetCursorPos(
                    new Vector2(
                        startX + (joinOptionWidth + 10) * 2,
                        startY));

                DrawJoinOptionCard(
                    joinOptionWidth,
                    FontAwesomeIcon.MapMarker.ToIconString(),
                    "Venues",
                    "Explore rooms \nnearby");
            }
    }
}
    private async void CreateEmptyWatchParty()
    {
        screenController.Engine.ShowWaitingScreen();

        await stream.PublishStateAsync(
            null,
            0,
            true,
            screenController.Engine.ScreenPosition,
            screenController.Engine.ScreenYaw,
            screenController.Engine.ScreenScale);
    }

    private void DrawWatchPartyFeatures()
    {


        var width =
            ImGui.GetContentRegionAvail().X;

        const float gap = 12f;

        var cardWidth =
            (width - (gap * 2)) / 3f;


        DrawWatchPartyFeatureCard(
            "💬",
            "Live Chat",
            "Talk with friends while watching.",
            cardWidth);

        ImGui.SameLine(0, gap);

        DrawWatchPartyFeatureCard(
            "😂",
            "Reactions",
            "Send emojis and react live.",
            cardWidth);

        ImGui.SameLine(0, gap);

        DrawWatchPartyFeatureCard(
            "🔄",
            "Sync Playback",
            "Everyone stays on the same moment.",
            cardWidth);
    }

    private void DrawWatchPartyFeatureCard(
    string icon,
    string title,
    string description,
    float width)
    {
        using var card =
            ImRaii.Child(
                $"##watchFeature_{title}",
new Vector2(
    width,
    85),
                true);

        if (!card)
            return;

        ImGui.Text(icon);

        ImGui.SameLine();

        ImGui.Text(title);

        ImGui.TextColored(
            MutedText,
            description);

        // restore layout cursor for SameLine
        ImGui.SetCursorScreenPos(
            ImGui.GetCursorScreenPos() + new Vector2(width, 0));
    }
    private void DrawChatDrawer()
    {
        DrawPartySocialPanel();
    }
    private void DrawWatchPartyDrawer()
    {
        DrawPartyPanel();
    }
}