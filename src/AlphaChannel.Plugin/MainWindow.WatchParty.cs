using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // TEMP: development/testing helper.
    // This does not change the real StreamMode. It only lets media-action
    // UI behave as though this client were a watch-party viewer.
    private bool sandboxActAsViewer;

    private bool ShouldUseViewerMediaActions =>
        stream.Mode == StreamMode.Viewing ||
        sandboxActAsViewer;


    // =========================================================
    // Watch Party media-action interception
    // =========================================================

    private VideoQueueEntry? pendingViewerMediaEntry;

    private bool pendingViewerMediaWasPlayNow;

    private bool openViewerMediaActionPopup;


    // Route Play Now actions through here.
    //
    // Hosts / normal local playback continue exactly as before.
    // Watch-party viewers are instead asked whether they want to
    // request the video from the host or save it locally for later.
    //
    private void HandlePlayNow(
     VideoQueueEntry entry)
    {
        //
        // Local Video exclusively owns the TV.
        //
        // Block here BEFORE queue.PlayNow() can change Current,
        // reorder anything, or cause the rest of the UI to believe
        // another media item owns the active MPV session.
        //

        if (video.IsPlayingLocalVideo)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the local video before playing other media.");

            return;
        }


        if (!ShouldUseViewerMediaActions)
        {
            queue.PlayNow(entry);
            return;
        }

        pendingViewerMediaEntry =
            entry;

        pendingViewerMediaWasPlayNow =
            true;

        openViewerMediaActionPopup =
            true;
    }


    // Route Add to Queue actions through here.
    //
    // Outside a viewed watch party this behaves exactly like the
    // existing queue.Add call.
    //
    // Viewers are asked whether they want to request the media from
    // the host or add it to their own private/local queue.
    //
    private void HandleAddToQueue(
    VideoQueueEntry entry)
    {
        //
        // Keep Local Video completely isolated from normal media.
        //
        // While it owns the TV we don't even allow the normal queue
        // to be edited through media actions.
        //

        if (video.IsPlayingLocalVideo)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the local video before adding other media to the queue.");

            return;
        }


        if (!ShouldUseViewerMediaActions)
        {
            queue.Add(entry);

            queueAddedFeedbackUntil =
                ImGui.GetTime() + 2.0;

            return;
        }

        pendingViewerMediaEntry =
            entry;

        pendingViewerMediaWasPlayNow =
            false;

        openViewerMediaActionPopup =
            true;
    }


    // Draw this once per MainWindow frame.
    //
    // For now "Request this video" deliberately does not send
    // anything over the network. We will connect that after the
    // interception path has been tested.
    //
    private void DrawViewerMediaActionPopup()
    {
        if (openViewerMediaActionPopup)
        {
            ImGui.OpenPopup(
                "Watch Party##viewerMediaAction");

            openViewerMediaActionPopup =
                false;
        }

        ImGui.SetNextWindowSize(
            new Vector2(
                460f,
                0f),
            ImGuiCond.Always);

        if (!ImGui.BeginPopupModal(
                "Watch Party##viewerMediaAction",
                ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        var entry =
            pendingViewerMediaEntry;

        if (entry is null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }


        ImGui.SetWindowFontScale(
            1.10f);

        ImGui.TextColored(
            Vector4.One,
            "You're currently in a watch party.");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                4f));

        ImGui.TextColored(
            MutedText,
            pendingViewerMediaWasPlayNow
                ? "You can't replace the host's current video directly."
                : "The host controls the shared playback queue.");

        ImGui.TextColored(
            MutedText,
            "What would you like to do with this video?");


        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        ImGui.Separator();

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));


        //
        // Media details
        //

        var displayTitle =
            string.IsNullOrWhiteSpace(entry.Title)
                ? entry.Url
                : entry.Title;

        ImGui.SetWindowFontScale(
            1.08f);

        ImGui.TextWrapped(
            displayTitle);

        ImGui.SetWindowFontScale(
            1f);


        if (!string.IsNullOrWhiteSpace(
                entry.Source))
        {
            ImGui.Dummy(
                new Vector2(
                    0f,
                    2f));

            ImGui.TextColored(
                MutedText,
                entry.Source);
        }


        ImGui.Dummy(
            new Vector2(
                0f,
                16f));


        //
        // Request from host
        //
        // Transport is intentionally added in the next stage.
        //

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
            if (ImGui.Button(
                    "Request this video",
                    new Vector2(
                        -1f,
                        38f)))
            {
                _ = stream.SendMediaRequestAsync(
                    entry.Url,
                    entry.Title,
                    entry.Source,
                    entry.Duration,
                    entry.ThumbnailUrl);

                pendingViewerMediaEntry =
                    null;

                ImGui.CloseCurrentPopup();
            }
        }


        ImGui.Dummy(
            new Vector2(
                0f,
                6f));


        //
        // Personal/local queue
        //

        if (ImGui.Button(
                "Add to my personal queue",
                new Vector2(
                    -1f,
                    38f)))
        {
            queue.Add(entry);

            queueAddedFeedbackUntil =
                ImGui.GetTime() + 2.0;

            pendingViewerMediaEntry =
                null;

            ImGui.CloseCurrentPopup();
        }


        ImGui.Dummy(
            new Vector2(
                0f,
                6f));


        //
        // Cancel
        //

        if (ImGui.Button(
                "Cancel",
                new Vector2(
                    -1f,
                    34f)))
        {
            pendingViewerMediaEntry =
                null;

            ImGui.CloseCurrentPopup();
        }


        ImGui.EndPopup();
    }

    private void DrawWatchPartyPage()
    {
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
        var avail = ImGui.GetContentRegionAvail();
        var heroH = Ui(230f);
        var featuresH = Ui(130f);
        var gap = Ui(12f);
        var actionsH = Math.Max(Ui(325f), avail.Y - heroH - featuresH - gap);

        DrawWatchPartyHero(heroH);
        DrawWatchPartyActions(actionsH);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + gap);
        DrawWatchPartyFeatures(featuresH);
    }

    private void DrawWatchPartyHero(float heroHeight)
    {
        using var hero =
    ImRaii.Child(
        "##watchPartyHero",
        new Vector2(0, heroHeight),
        false,
        ImGuiWindowFlags.NoScrollbar |
        ImGuiWindowFlags.NoScrollWithMouse);

        if (!hero)
            return;

        var heroMin = ImGui.GetCursorScreenPos();

        var heroMax = new Vector2(
            heroMin.X + ImGui.GetContentRegionAvail().X,
            heroMin.Y + heroHeight);

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


        var pad = Ui(24f);
        ImGui.SetCursorScreenPos(heroMin + new Vector2(pad, pad));

        var width =
            ImGui.GetContentRegionAvail().X;

        var innerH = Math.Max(Ui(190f), heroHeight - pad * 2f);
        var previewWidth = Ui(620f);

        var textWidth =
            width - previewWidth - Ui(40f);


        using (ImRaii.Child(
            "##watchPartyHeroText",
            new Vector2(
                textWidth,
                innerH),
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
    innerH),
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
                previewMin.Y + innerH);

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

            var panelHeight = innerH;

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

   private void DrawWatchPartyActions(float cardHeight)
{
    var width =
        ImGui.GetContentRegionAvail().X;

    var cardWidth =
        (width - Ui(12f)) / 2f;


        using (var start =
            ImRaii.Child(
                "##startParty",
                new Vector2(cardWidth, cardHeight),
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
                        cardHeight);

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
                    + UiVec(18, 12);

                ImGui.SetCursorScreenPos(iconPos);


                ImGui.GetWindowDrawList().AddCircleFilled(
                    iconPos + UiVec(24, 24),
                    Ui(24f),
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.25f)));


                ImGui.SetCursorScreenPos(
                    iconPos + UiVec(12, 12));


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

                DrawCreateRoomFields(cardWidth - Ui(40f));

                ImGui.SetCursorPosY(
    ImGui.GetCursorPosY() - 12);


                var optionHeight = Math.Max(Ui(200f), cardHeight - Ui(125f));

                var optionWidth =
    (cardWidth - Ui(70f)) / 2f;

                ImGui.SetCursorPosX(Ui(35f));


                //
                // Start Watching option
                //
                using (ImRaii.Child(
                    "##startWatchingOption",
                    new Vector2(
                        optionWidth,
                        optionHeight),
                    false,
                    ImGuiWindowFlags.NoBackground))
                {
                    var optionMin =
                        ImGui.GetCursorScreenPos();
                    var hovered =
    ImGui.IsMouseHoveringRect(
        optionMin,
        optionMin + new Vector2(optionWidth, optionHeight));

                    ImGui.GetWindowDrawList().AddRectFilled(
                        optionMin,
                        optionMin + new Vector2(optionWidth, optionHeight),
                        ImGui.GetColorU32(
hovered
    ? new Vector4(0.12f, 0.08f, 0.22f, 1f)
    : new Vector4(0.07f, 0.06f, 0.12f, 1f)),
                        12f);

                    ImGui.GetWindowDrawList().AddRect(
                        optionMin,
                        optionMin + new Vector2(optionWidth, optionHeight),
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
                            FontAwesomeIcon.Play.ToIconString());

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

                    ImGui.SetCursorScreenPos(optionMin);
                    if (ImGui.InvisibleButton(
                            "##startWatchingRoom",
                            new Vector2(optionWidth, optionHeight)))
                    {
                        ApplyCreateRoomToStream();
                        StartWatchParty(goToPlayer: true);
                    }
            }


            ImGui.SameLine();


                //
                // Create Room option
                //
                using (ImRaii.Child(
                    "##createRoomOption",
                    new Vector2(
                        optionWidth,
                        optionHeight),
                    false,
                    ImGuiWindowFlags.NoBackground))
                {
                    var optionMin =
                        ImGui.GetCursorScreenPos();
                    var hovered =
    ImGui.IsMouseHoveringRect(
        optionMin,
        optionMin + new Vector2(optionWidth, optionHeight));


                    var optionMax =
                        optionMin + new Vector2(
                            optionWidth,
                            optionHeight);


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
                            new Vector2(optionWidth, optionHeight)))
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
new Vector2(cardWidth, cardHeight),
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
    cardHeight);

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
                    + UiVec(18, 12);

                ImGui.GetWindowDrawList().AddCircleFilled(
                    joinIconPos + UiVec(24, 24),
                    Ui(24f),
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.25f)));

                ImGui.SetCursorScreenPos(
                    joinIconPos + UiVec(12, 12));

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

                var joinButtonWidth = Ui(72f);
                var joinGap = Ui(10f);

                var joinInputWidth =
                    cardWidth - joinButtonWidth - joinGap - Ui(64f);

                ImGui.SetNextItemWidth(joinInputWidth);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    Ui(10f))
                    .Push(
                        ImGuiStyleVar.FramePadding,
                        UiVec(14f, 8f)))
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
                        "Enter their Alpha Channel username",
                        ref joinHostNameInput,
                        32);
                }

                ImGui.SetNextItemWidth(joinInputWidth);
                ImGui.InputTextWithHint(
                    "##joinRoomPassword",
                    "Password (locked rooms)",
                    ref joinPasswordInput,
                    64,
                    ImGuiInputTextFlags.Password);

                ImGui.SameLine(
    cardWidth - joinButtonWidth - Ui(20f));

                if (ImGui.Button(
                    "Join",
                  new Vector2(joinButtonWidth, Ui(34f))))
                {
                    DoJoin(joinHostNameInput, joinPasswordInput);
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

                var joinOptionH = Math.Max(Ui(130f), cardHeight - Ui(195f));

                void DrawJoinOptionCard(
     float width,
     string icon,
     string title,
     string description,
     Action onClick)
                {
                    var optionMin = ImGui.GetCursorScreenPos();

                    var optionMax = optionMin + new Vector2(
    width,
    joinOptionH);

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
                        optionMin + UiVec(14, 14));


                    using (ImRaii.PushFont(UiBuilder.IconFont))
                    {
                        ImGui.SetWindowFontScale(1.2f);

                        ImGui.TextColored(
                            Accent,
                            icon);

                        ImGui.SetWindowFontScale(1f);
                    }


                    ImGui.SetCursorScreenPos(
                       optionMin + new Vector2(Ui(14f), Ui(48f)));


                    ImGui.Text(title);

                    ImGui.SetCursorScreenPos(
                        optionMin + new Vector2(Ui(14f), Ui(70f)));


                    ImGui.TextColored(
                        MutedText,
                        description);

                    ImGui.SetCursorScreenPos(optionMin);
                    if (ImGui.InvisibleButton($"##joinOption{title}", new Vector2(width, joinOptionH)))
                    {
                        onClick();
                    }
                }


                var joinOptionWidth = Math.Max(Ui(135f), (cardWidth - Ui(70f)) / 3f);
                var joinOptionGap = Ui(10f);

                var totalWidth =
    (joinOptionWidth * 3) + joinOptionGap * 2;

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
                    "See friends \nwith active rooms",
                    () => LoadRoomBrowse("Friends", null, friendsOnly: true));

                ImGui.SetCursorPos(
                    new Vector2(
                        startX + joinOptionWidth + joinOptionGap,
                        startY));

                DrawJoinOptionCard(
                    joinOptionWidth,
                    FontAwesomeIcon.Globe.ToIconString(),
                    "Public Rooms",
                    "Browse public \nwatch parties",
                    () => LoadRoomBrowse("Public Rooms", RoomKind.Public, friendsOnly: false));

                ImGui.SetCursorPos(
                    new Vector2(
                        startX + (joinOptionWidth + joinOptionGap) * 2,
                        startY));

                DrawJoinOptionCard(
                    joinOptionWidth,
                    FontAwesomeIcon.MapMarker.ToIconString(),
                    "Venues",
                    "Explore rooms \nnearby",
                    () => LoadRoomBrowse("Venues", RoomKind.Venue, friendsOnly: false));

                DrawRoomBrowseList(cardWidth);
            }
    }
}
    private async void CreateEmptyWatchParty()
    {
        StartWatchParty(goToPlayer: false);
        await Task.CompletedTask;
    }

    private void StartWatchParty(bool goToPlayer)
    {
        if (CurrentSession is null)
        {
            joinError = "Sign in to host a watch party.";
            Plugin.ChatGui.Print("[AlphaChannel] Sign in before hosting a watch party.");
            return;
        }

        if (createRoomKindIndex == 1 && string.IsNullOrWhiteSpace(createRoomPassword))
        {
            joinError = "Locked rooms need a password.";
            return;
        }

        ApplyCreateRoomToStream();
        gameplayStreamOfferDismissed = false;
        screenController.Engine.ShowWaitingScreen();

        var current = queue.Current;
        var engine = screenController.Engine;
        _ = stream.PublishStateAsync(
            current?.Url,
            0,
            current is null,
            engine.IsActive ? engine.ScreenPosition : null,
            engine.IsActive ? engine.ScreenYaw : null,
            engine.IsActive ? engine.ScreenScale : null);

        if (goToPlayer)
        {
            currentPage = HomePage.Player;
            playerSourceTab = 0;
        }

        Plugin.ChatGui.Print(
            stream.IsConnected
                ? "[AlphaChannel] Watch party is live. Friends join with your Alpha Channel username."
                : "[AlphaChannel] Connecting… the room will go live when the relay is up.");
    }


    // =========================================================
    // Gameplay Watch Party
    // =========================================================
    //
    // Publishes the public HLS viewer URL to the Watch Party
    // without changing the host's local playback queue.
    //
    // The host continues rendering the emulator locally.
    // Viewers receive the HLS stream through normal Watch Party
    // synchronization.
    //
    // IMPORTANT:
    // This must only ever receive the public HLS URL.
    // Never pass the RTMP publish URL or stream key here.
    //
    private async Task PublishGameplayWatchPartyAsync(
        string hlsUrl)
    {
        if (string.IsNullOrWhiteSpace(hlsUrl))
        {
            return;
        }

        await stream.PublishStateAsync(
            hlsUrl,
            0,
            false,
            screenController.Engine.ScreenPosition,
            screenController.Engine.ScreenYaw,
            screenController.Engine.ScreenScale);
    }

    private void DrawWatchPartyFeatures(float rowHeight)
    {


        var width =
            ImGui.GetContentRegionAvail().X;

        var gap = Ui(12f);

        var cardWidth =
            (width - (gap * 2)) / 3f;


        DrawWatchPartyFeatureCard(
     FontAwesomeIcon.CommentDots.ToIconString(),
     "Live Chat",
     "Talk with friends while watching.",
     cardWidth,
     rowHeight,
     new Vector4(0.35f, 0.75f, 1.00f, 1f));

        ImGui.SameLine(0, gap);

        DrawWatchPartyFeatureCard(
            FontAwesomeIcon.Heart.ToIconString(),
            "Reactions",
            "Send emojis and react live.",
            cardWidth,
            rowHeight,
            new Vector4(1.00f, 0.55f, 0.75f, 1f));

        ImGui.SameLine(0, gap);

        DrawWatchPartyFeatureCard(
            FontAwesomeIcon.Sync.ToIconString(),
            "Sync Playback",
            "Everyone stays on the same moment.",
            cardWidth,
            rowHeight,
            new Vector4(0.45f, 0.90f, 0.60f, 1f));
    }

    private void DrawWatchPartyFeatureCard(
     string icon,
     string title,
     string description,
     float width,
     float height,
     Vector4 featureColor)
    {
        using var card =
            ImRaii.Child(
                $"##watchFeature_{title}",
                new Vector2(
                    width,
                    height),
                false,
                ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse);

        if (!card)
            return;

        //
        // Work out the width of the icon + title so the whole heading
        // can be centered as one unit.
        //
        Vector2 iconSize;
        Vector2 titleSize;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.SetWindowFontScale(1.7f);
            iconSize = ImGui.CalcTextSize(icon);
            ImGui.SetWindowFontScale(1f);
        }

        using (ImRaii.PushFont(UiBuilder.DefaultFont))
        {
            ImGui.SetWindowFontScale(1.4f);
            titleSize = ImGui.CalcTextSize(title);
            ImGui.SetWindowFontScale(1f);
        }

        const float headingGap = 12f;

        var headingWidth =
            iconSize.X +
            headingGap +
            titleSize.X;

        var headingStartX =
            MathF.Max(
                0f,
                (width - headingWidth) * 0.5f);

        ImGui.SetCursorPosX(
            headingStartX);

        //
        // Icon
        //
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.SetWindowFontScale(1.7f);

            ImGui.TextColored(
                featureColor,
                icon);

            ImGui.SetWindowFontScale(1f);
        }

        ImGui.SameLine(
            0f,
            headingGap);

        //
        // Title
        //
        using (ImRaii.PushFont(UiBuilder.DefaultFont))
        {
            ImGui.SetWindowFontScale(1.4f);

            ImGui.TextColored(
                featureColor,
                title);

            ImGui.SetWindowFontScale(1f);
        }

        //
        // Description
        //
        ImGui.SetCursorPosY(
            ImGui.GetCursorPosY() + 7f);

        using (ImRaii.PushFont(UiBuilder.DefaultFont))
        {
            ImGui.SetWindowFontScale(1.1f);

            var descriptionSize =
                ImGui.CalcTextSize(description);

            ImGui.SetCursorPosX(
                MathF.Max(
                    0f,
                    (width - descriptionSize.X) * 0.5f));

            ImGui.TextColored(
                MutedText,
                description);

            ImGui.SetWindowFontScale(1f);
        }
    }

    private void ApplyCreateRoomToStream()
    {
        stream.RoomDescription = string.IsNullOrWhiteSpace(createRoomDescription) ? "" : createRoomDescription.Trim();
        stream.RoomLocation = string.IsNullOrWhiteSpace(createRoomLocation) ? "" : createRoomLocation.Trim();
        stream.RoomKind = createRoomKindIndex switch
        {
            1 => RoomKind.Locked,
            2 => RoomKind.Venue,
            _ => RoomKind.Public,
        };
        stream.RoomPassword = stream.RoomKind == RoomKind.Locked ? createRoomPassword : "";
    }

    private void DrawCreateRoomFields(float width)
    {
        ImGui.SetNextItemWidth(width);
        ImGui.InputTextWithHint("##createRoomDescription", "Description", ref createRoomDescription, 280);
        ImGui.SetNextItemWidth(width);
        ImGui.InputTextWithHint("##createRoomLocation", "Location", ref createRoomLocation, 120);
        ImGui.SetNextItemWidth(width);
        ImGui.Combo("##createRoomKind", ref createRoomKindIndex, ["Public", "Locked", "Venue"], 3);
        if (createRoomKindIndex == 1)
        {
            ImGui.SetNextItemWidth(width);
            ImGui.InputTextWithHint("##createRoomPassword", "Room password", ref createRoomPassword, 64, ImGuiInputTextFlags.Password);
        }
    }

    private void LoadRoomBrowse(string title, RoomKind? kind, bool friendsOnly)
    {
        roomBrowseTitle = title;
        roomBrowseFriendsOnly = friendsOnly;
        roomBrowseLoading = true;
        var token = CurrentSession?.Token;
        if (string.IsNullOrEmpty(token))
        {
            roomBrowseList = [];
            roomBrowseLoading = false;
            return;
        }

        _ = Task.Run(async () =>
        {
            var rooms = await roomsClient.ListAsync(token, kind).ConfigureAwait(false);
            if (string.Equals(title, "Public Rooms", StringComparison.Ordinal))
            {
                var locked = await roomsClient.ListAsync(token, RoomKind.Locked).ConfigureAwait(false);
                rooms = rooms.Concat(locked).ToArray();
            }

            if (friendsOnly)
            {
                var friends = await friendsClient.GetFriendsAsync(token).ConfigureAwait(false) ?? [];
                var ids = friends.Select(f => f.AccountId).ToHashSet(StringComparer.Ordinal);
                rooms = rooms.Where(r => ids.Contains(r.HostAccountId)).ToArray();
            }

            roomBrowseList = rooms;
            roomBrowseLoading = false;
        });
    }

    private void DrawRoomBrowseList(float width)
    {
        if (roomBrowseTitle is null)
        {
            return;
        }

        ImGui.Dummy(new Vector2(0, 8));
        ImGui.Text(roomBrowseTitle);
        if (roomBrowseLoading)
        {
            ImGui.TextColored(MutedText, "Loading…");
            return;
        }

        if (roomBrowseList.Length == 0)
        {
            ImGui.TextColored(MutedText, "No rooms right now.");
            return;
        }

        ImGui.SetNextItemWidth(width - Ui(40f));
        ImGui.InputTextWithHint("##roomBrowsePassword", "Password if locked", ref roomBrowsePassword, 64, ImGuiInputTextFlags.Password);

        foreach (var room in roomBrowseList)
        {
            var label = $"{room.HostDisplayName} · {room.Kind}";
            if (!string.IsNullOrEmpty(room.Location))
            {
                label += $" · {room.Location}";
            }

            if (ImGui.Button($"{label}##{room.HostAccountId}", new Vector2(width - Ui(40f), 0)))
            {
                DoJoin(room.HostDisplayName, room.Kind == RoomKind.Locked ? roomBrowsePassword : joinPasswordInput);
            }

            if (!string.IsNullOrEmpty(room.Description))
            {
                ImGui.TextColored(MutedText, room.Description);
            }
        }
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