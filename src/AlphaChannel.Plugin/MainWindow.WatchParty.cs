using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using TerritoryType = Lumina.Excel.Sheets.TerritoryType;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool ShouldUseViewerMediaActions =>
        stream.Mode == StreamMode.Viewing;


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
        if (!openViewerMediaActionPopup && pendingViewerMediaEntry is null)
        {
            return;
        }

        openViewerMediaActionPopup = false;
        var entry = pendingViewerMediaEntry;

        if (entry is null)
        {
            return;
        }

        var parentPos = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelSize = new Vector2(
            MathF.Min(Ui(560f), parentSize.X - Ui(40f)),
            MathF.Min(Ui(420f), parentSize.Y - Ui(40f)));

        ImGui.SetNextWindowPos(parentPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;

        if (!ImGui.Begin("##viewerMediaActionOverlay", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.GetWindowDrawList().AddRectFilled(
            parentPos, parentPos + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.54f)));
        ImGui.SetCursorScreenPos(parentPos + (parentSize - panelSize) * 0.5f);

        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f))
                   .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(24f, 20f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.025f, 0.03f, 0.06f, 0.995f))
                   .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.82f)))
        using (var card = ImRaii.Child("##viewerMediaActionCard", panelSize, true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (card)
            {
                DrawSectionTitle(FontAwesomeIcon.Users, "Watch Party Video");
                var afterTitle = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(new Vector2(
                    ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - Ui(48f),
                    ImGui.GetWindowPos().Y + Ui(12f)));
                using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
                {
                    if (ImGui.Button("X##closeViewerMediaAction", UiVec(28f, 28f)))
                    {
                        pendingViewerMediaEntry = null;
                    }
                }
                ImGui.SetCursorScreenPos(afterTitle);
                ImGui.Separator();
                ImGui.Dummy(UiVec(0f, 10f));

                ImGui.TextColored(MutedText,
                    pendingViewerMediaWasPlayNow
                        ? "The host controls the currently playing video."
                        : "The host controls the shared playback queue.");
                ImGui.TextColored(MutedText, "Choose what you would like to do with this video.");
                ImGui.Dummy(UiVec(0f, 12f));

                using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(9f))
                           .Push(ImGuiStyleVar.WindowPadding, UiVec(14f, 12f)))
                using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.055f, 0.065f, 0.115f, 1f)))
                using (var mediaCard = ImRaii.Child("##requestedVideo", UiVec(-1f, 76f), false,
                           ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                {
                    if (mediaCard)
                    {
                        ImGui.TextWrapped(string.IsNullOrWhiteSpace(entry.Title) ? entry.Url : entry.Title);
                        if (!string.IsNullOrWhiteSpace(entry.Source))
                            ImGui.TextColored(MutedText, entry.Source);
                    }
                }

                ImGui.Dummy(UiVec(0f, 10f));
                using (ImRaii.PushColor(ImGuiCol.Button, Accent)
                           .Push(ImGuiCol.ButtonHovered, AccentHover)
                           .Push(ImGuiCol.ButtonActive, AccentActive))
                {
                    if (DrawViewerMediaActionButton(
                            "sendRequest",
                            FontAwesomeIcon.PaperPlane,
                            "Send video request to host",
                            UiVec(-1f, 40f)))
                    {
                        _ = stream.SendMediaRequestAsync(entry.Url, entry.Title, entry.Source,
                            entry.Duration, entry.ThumbnailUrl);
                        pendingViewerMediaEntry = null;
                    }
                }

                if (DrawViewerMediaActionButton(
                        "personalQueue",
                        FontAwesomeIcon.ListUl,
                        "Add to my personal queue",
                        UiVec(-1f, 40f)))
                {
                    queue.Add(entry);
                    queueAddedFeedbackUntil = ImGui.GetTime() + 2.0;
                    pendingViewerMediaEntry = null;
                }

                var cancelWidth = Ui(90f);
                ImGui.SetCursorPosX((ImGui.GetWindowWidth() - cancelWidth) * 0.5f);
                if (ImGui.Button("Cancel", UiVec(90f, 30f)))
                    pendingViewerMediaEntry = null;
            }
        }
        ImGui.End();
    }

    private bool DrawViewerMediaActionButton(
        string id,
        FontAwesomeIcon icon,
        string label,
        Vector2 size)
    {
        var clicked = ImGui.Button($"##{id}", size);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var labelSize = ImGui.CalcTextSize(label);
        var glyph = icon.ToIconString();
        Vector2 glyphSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            glyphSize = ImGui.CalcTextSize(glyph);

        var gap = Ui(8f);
        var contentWidth = glyphSize.X + gap + labelSize.X;
        var start = new Vector2(
            min.X + (max.X - min.X - contentWidth) * 0.5f,
            min.Y + (max.Y - min.Y - labelSize.Y) * 0.5f);
        var drawList = ImGui.GetWindowDrawList();

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(start.X, min.Y + (max.Y - min.Y - glyphSize.Y) * 0.5f),
                ImGui.GetColorU32(Vector4.One),
                glyph);
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(start.X + glyphSize.X + gap, start.Y),
            ImGui.GetColorU32(Vector4.One),
            label);
        return clicked;
    }

    //
    // Resolve the player's location only when explicitly requested.
    // The Location field otherwise remains completely user-controlled.
    //
    private unsafe string GetCurrentWatchPartyLocation()
    {
        if (!Plugin.ClientState.IsLoggedIn)
        {
            return string.Empty;
        }

        try
        {
            var territoryId =
                Plugin.ClientState.TerritoryType;

            if (territoryId == 0)
            {
                return string.Empty;
            }

            var housingManager =
                HousingManager.Instance();

            var isHousingTerritory =
                housingManager is not null &&
                housingManager->CurrentTerritory is not null;

            //
            // When inside an estate, the current client territory can
            // be an interior territory. Resolve its original housing
            // district so the form says "Mist" rather than an internal
            // estate-interior name.
            //
            if (isHousingTerritory)
            {
                var originalTerritoryId =
                    HousingManager
                        .GetOriginalHouseTerritoryTypeId();

                if (originalTerritoryId != 0)
                {
                    territoryId =
                        originalTerritoryId;
                }
            }

            var territory =
                Plugin.DataManager
                    .GetExcelSheet<TerritoryType>()
                    .GetRow(territoryId);

            var territoryName =
                territory
                    .PlaceName
                    .Value
                    .Name
                    .ToString()
                    .Trim();

            if (string.IsNullOrWhiteSpace(
                    territoryName))
            {
                return string.Empty;
            }

            if (!isHousingTerritory)
            {
                var instance =
                    Plugin.ClientState.Instance;

                return instance > 0
                    ? $"{territoryName} ({instance})"
                    : territoryName;
            }

            var wardIndex =
                housingManager->GetCurrentWard();

            if (wardIndex < 0)
            {
                return territoryName;
            }

            var wardNumber =
                wardIndex + 1;

            var plotIndex =
                housingManager->GetCurrentPlot();

            var roomNumber =
                housingManager->GetCurrentRoom();

            //
            // HousingManager uses -128 and -127 for apartments in the
            // main division and subdivision respectively.
            //
            var isApartment =
                plotIndex is -128 or -127;

            if (isApartment)
            {
                return roomNumber > 0
                    ? $"{territoryName}, Ward {wardNumber}, Apt {roomNumber}"
                    : $"{territoryName}, Ward {wardNumber}, Apartments";
            }

            if (plotIndex >= 0)
            {
                var plotNumber =
                    plotIndex + 1;

                return
                    $"{territoryName}, Ward {wardNumber}, Plot {plotNumber}";
            }

            return
                $"{territoryName}, Ward {wardNumber}";
        }
        catch (Exception exception)
        {
            //
            // Location detection is convenience-only. A sheet lookup
            // or transient territory-loading failure must never stop
            // the Watch Party page from drawing.
            //
            Plugin.Log.Debug(
                exception,
                "Could not prefill the Watch Party location.");

            return string.Empty;
        }
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
        //
        // Pull the first panel closer to the page-header divider while
        // retaining a small visual gap.
        //
        ImGui.SetCursorPosY(
            ImGui.GetCursorPosY() -
            Ui(7f));

        var heroH =
            Ui(210f);

        //
        // Give the Start and Join panels more vertical room so their
        // bottom buttons do not sit against the panel borders.
        //
        var actionsH =
            Ui(440f);

        var featuresH =
            Ui(72f);

        var gap =
            Ui(8f);

        DrawWatchPartyHero(
            heroH);

        DrawWatchPartyActions(
            actionsH);

        ImGui.SetCursorPosY(
            ImGui.GetCursorPosY() +
            gap);

        DrawWatchPartyFeatures(
            featuresH);
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


        var pad =
                Ui(18f);

        var innerH =
            MathF.Max(
                Ui(120f),
                heroHeight -
                pad * 2f);

        //
        // Center the complete text/image row vertically inside the hero.
        //
        var rowY =
            heroMin.Y +
            (heroHeight -
             innerH) *
            0.5f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                heroMin.X +
                pad,
                rowY));

        var width =
            ImGui.GetContentRegionAvail().X;

        var previewWidth =
            Ui(620f);

        var textWidth =
            width -
            previewWidth -
            Ui(28f);


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
                SetUiFontScale(1.65f);

                ImGui.TextColored(
                    Vector4.One,
                    "Watch together,");

                ImGui.TextColored(
                    Accent,
                    "anywhere in Eorzea.");

                SetUiFontScale(1f);
            }

            ImGui.Spacing();
            ImGui.Spacing();

            ImGui.PushTextWrapPos(
                ImGui.GetCursorPosX() +
                textWidth -
                Ui(30f));

            using (ImRaii.PushFont(UiBuilder.DefaultFont))
            {
                SetUiFontScale(1.25f);

                ImGui.TextColored(
                    MutedText,
                    "Create a room, invite friends, and enjoy videos with synced playback, chat, and live reactions.");

                SetUiFontScale(1f);
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

    private void DrawWatchPartyActions(
        float cardHeight)
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        var gap =
            Ui(12f);

        //
        // Stack the forms vertically at narrower window sizes.
        //
        var stacked =
            width < Ui(760f);

        var cardWidth =
            stacked
                ? width
                : (width - gap) / 2f;

        var startHeight =
           Math.Max(
               cardHeight,
               Ui(390f));

        var joinHeight =
            Math.Max(
                cardHeight,
                Ui(390f));

        DrawStartWatchPartyPanel(
            cardWidth,
            startHeight);

        if (stacked)
        {
            ImGui.Dummy(
                new Vector2(
                    0f,
                    gap));
        }
        else
        {
            ImGui.SameLine(
                0f,
                gap);
        }

        DrawJoinWatchPartyPanel(
            cardWidth,
            joinHeight);
    }

    private void DrawStartWatchPartyPanel(
        float width,
        float height)
    {
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       0.08f,
                       0.05f,
                       0.15f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.48f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   Ui(16f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildBorderSize,
                   Ui(1.5f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.WindowPadding,
                   UiVec(20f, 18f)))
        using (var panel =
               ImRaii.Child(
                   "##startParty",
                   new Vector2(
                       width,
                       height),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!panel)
            {
                return;
            }

            DrawWatchPartyFormHeader(
                   FontAwesomeIcon.Clapperboard,
                   "Start a Watch Party",
                   "Create a room now and add content whenever you're ready.",
                   showDetailsTooltip: true);

            //
            // Place the compact 18+ toggle in the unused right side of
            // the header rather than consuming a form row.
            //
            var formStart =
                ImGui.GetCursorScreenPos();

            var toggleWidth =
                Ui(76f);

            ImGui.SetCursorScreenPos(
                           new Vector2(
                               ImGui.GetWindowPos().X +
                               ImGui.GetWindowSize().X -
                               Ui(20f) -
                               toggleWidth,
                                 formStart.Y -
                    Ui(4f)));


            DrawWatchPartyAdultToggle();

            ImGui.SetCursorScreenPos(
                formStart);

            //
            // Leave additional clearance below the header and 18+
            // toggle before beginning the room-detail fields.
            //
            ImGui.Dummy(
                UiVec(
                    0f,
                    10f));

            DrawCreateRoomFields(
                ImGui.GetContentRegionAvail().X);

            ImGui.Dummy(
                UiVec(
                    0f,
                    5f));

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       Ui(7f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FramePadding,
                       UiVec(12f, 9f)))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       Accent))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonHovered,
                       AccentHover))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           Accent.X * 0.82f,
                           Accent.Y * 0.82f,
                           Accent.Z * 0.82f,
                           1f)))
            {
                if (ImGui.Button(
                        "Create Room",
                        new Vector2(
                            ImGui.GetContentRegionAvail().X,
                            Ui(38f))))
                {
                    CreateEmptyWatchParty();
                }
            }
        }
    }

    private void DrawJoinWatchPartyPanel(
        float width,
        float height)
    {
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       0.08f,
                       0.05f,
                       0.15f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.48f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   Ui(16f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildBorderSize,
                   Ui(1.5f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.WindowPadding,
                   UiVec(20f, 18f)))
        using (var panel =
               ImRaii.Child(
                   "##joinParty",
                   new Vector2(
                       width,
                       height),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!panel)
            {
                return;
            }

            DrawWatchPartyFormHeader(
                FontAwesomeIcon.Users,
                "Join a Watch Party",
                "Join a friend's room or discover public Watch Parties.",
                showDetailsTooltip: false);

            ImGui.Dummy(
                 UiVec(
                     0f,
                     5f));

            using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        Ui(7f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FramePadding,
                       UiVec(9f, 5f)))
            using (ImRaii.PushColor(
                       ImGuiCol.FrameBg,
                       new Vector4(
                           0.04f,
                           0.04f,
                           0.08f,
                           1f)))
            using (ImRaii.PushColor(
                       ImGuiCol.FrameBgHovered,
                       new Vector4(
                           0.10f,
                           0.07f,
                           0.18f,
                           1f)))
            using (ImRaii.PushColor(
                       ImGuiCol.FrameBgActive,
                       new Vector4(
                           0.12f,
                           0.08f,
                           0.22f,
                           1f)))
            {
                var fieldGap =
                   Ui(10f);

                var availableWidth =
                    ImGui.GetContentRegionAvail().X;

                var hostWidth =
                    availableWidth *
                    0.58f;

                var passwordWidth =
                    availableWidth -
                    hostWidth -
                    fieldGap;

                var fieldsOrigin =
                    ImGui.GetCursorScreenPos();

                //
                // Host username column
                //
                ImGui.SetCursorScreenPos(
                    fieldsOrigin);

                ImGui.BeginGroup();

                ImGui.TextColored(
                    MutedText,
                    "Host username");

                ImGui.SetNextItemWidth(
                    hostWidth);

                ImGui.InputTextWithHint(
                    "##hostName",
                    "Alpha Channel username",
                    ref joinHostNameInput,
                    32);

                ImGui.EndGroup();

                var hostBottomY =
                    ImGui.GetItemRectMax().Y;

                //
                // Password column
                //
                ImGui.SetCursorScreenPos(
                    new Vector2(
                        fieldsOrigin.X +
                        hostWidth +
                        fieldGap,
                        fieldsOrigin.Y));

                ImGui.BeginGroup();

                ImGui.TextColored(
                    MutedText,
                    "Password");

                ImGui.SetNextItemWidth(
                    passwordWidth);

                ImGui.InputTextWithHint(
                    "##joinRoomPassword",
                    "Optional",
                    ref joinPasswordInput,
                    64,
                    ImGuiInputTextFlags.Password);

                var passwordHovered =
                    ImGui.IsItemHovered();

                ImGui.EndGroup();

                var passwordBottomY =
                    ImGui.GetItemRectMax().Y;

                if (passwordHovered)
                {
                    ImGui.SetTooltip(
                        "For locked rooms only");
                }

                //
                // Continue beneath the taller of the two columns.
                //
                ImGui.SetCursorScreenPos(
                    new Vector2(
                        fieldsOrigin.X,
                        MathF.Max(
                            hostBottomY,
                            passwordBottomY)));

                ImGui.Dummy(
                    Vector2.Zero);
            }

            ImGui.Dummy(
                          UiVec(
                              0f,
                              6f));

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       Ui(7f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FramePadding,
                       UiVec(12f, 9f)))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       Accent))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonHovered,
                       AccentHover))
            {
                if (ImGui.Button(
                        "Join Room",
                        new Vector2(
                            ImGui.GetContentRegionAvail().X,
                            Ui(38f))))
                {
                    DoJoin(
                        joinHostNameInput,
                        joinPasswordInput);
                }
            }

            if (joinError is { } error)
            {
                ImGui.Dummy(
                    UiVec(
                        0f,
                        5f));

                ImGui.TextColored(
                    Danger,
                    error);
            }

            ImGui.Dummy(
                 UiVec(
                     0f,
                     9f));

            DrawWatchPartyOrDivider();

            ImGui.Dummy(
                UiVec(
                    0f,
                    8f));

            DrawWatchPartyBrowseButton(
              ImGui.GetContentRegionAvail().X,
              FontAwesomeIcon.Globe,
              "Browse public Watch Parties and venues",
              () =>
              {
                  currentPage =
                      HomePage.PartyDirectory;
              });
        }
    }

    private void DrawWatchPartyFormHeader(
        FontAwesomeIcon icon,
        string title,
        string subtitle,
        bool showDetailsTooltip)
    {
        var drawList =
            ImGui.GetWindowDrawList();

        var headerStart =
            ImGui.GetCursorScreenPos();

        var iconSize =
            Ui(44f);

        var iconCenter =
            headerStart +
            new Vector2(
                iconSize * 0.5f,
                iconSize * 0.5f);

        drawList.AddCircleFilled(
            iconCenter,
            iconSize * 0.5f,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.24f)),
            24);

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var glyph =
                icon.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconCenter -
                glyphSize * 0.5f,
                ImGui.GetColorU32(
                    Accent),
                glyph);
        }

        var textX =
            headerStart.X +
            iconSize +
            Ui(12f);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                textX,
                headerStart.Y +
                Ui(2f)),
            ImGui.GetColorU32(
                Vector4.One),
            title);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                textX,
                headerStart.Y +
                Ui(24f)),
            ImGui.GetColorU32(
                MutedText),
            subtitle);

        if (showDetailsTooltip)
        {
            var infoGlyph =
                FontAwesomeIcon.InfoCircle
                    .ToIconString();

            Vector2 infoSize;
            Vector2 infoPos;

            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                infoSize =
                    ImGui.CalcTextSize(
                        infoGlyph);

                infoPos =
                    new Vector2(
                        ImGui.GetWindowPos().X +
                        ImGui.GetWindowSize().X -
                        Ui(20f) -
                        infoSize.X,
                        headerStart.Y +
                        Ui(4f));

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    infoPos,
                    ImGui.GetColorU32(
                        MutedText),
                    infoGlyph);
            }

            //
            // Evaluate and draw the tooltip after restoring the normal font.
            //
            if (ImGui.IsMouseHoveringRect(
                    infoPos -
                    UiVec(4f, 4f),
                    infoPos +
                    infoSize +
                    UiVec(4f, 4f)))
            {
                ImGui.SetTooltip(
                    "Room details can be changed later while hosting.");
            }
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                iconSize));
    }

    private void DrawWatchPartyOrDivider()
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        const string text =
            "OR";

        var textSize =
            ImGui.CalcTextSize(
                text);

        var gap =
            Ui(10f);

        var lineWidth =
            MathF.Max(
                0f,
                (
                    width -
                    textSize.X -
                    gap * 2f
                ) *
                0.5f);

        var origin =
            ImGui.GetCursorScreenPos();

        var lineY =
            origin.Y +
            textSize.Y *
            0.5f;

        var drawList =
            ImGui.GetWindowDrawList();

        var lineColor =
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.30f));

        drawList.AddLine(
            new Vector2(
                origin.X,
                lineY),
            new Vector2(
                origin.X +
                lineWidth,
                lineY),
            lineColor,
            Ui(1f));

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X +
                lineWidth +
                gap,
                origin.Y),
            ImGui.GetColorU32(
                MutedText),
            text);

        drawList.AddLine(
            new Vector2(
                origin.X +
                lineWidth +
                gap * 2f +
                textSize.X,
                lineY),
            new Vector2(
                origin.X +
                width,
                lineY),
            lineColor,
            Ui(1f));

        ImGui.Dummy(
            new Vector2(
                width,
                textSize.Y));
    }

    private void LoadPublicWatchPartiesAndVenues()
    {
        roomBrowseTitle =
            "Public Watch Parties and Venues";

        roomBrowseFriendsOnly =
            false;

        roomBrowseLoading =
            true;
        roomBrowseError =
            null;

        var token =
            CurrentSession?.Token;

        if (string.IsNullOrEmpty(
                token))
        {
            roomBrowseList =
                [];

            roomBrowseLoading =
                false;

            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var publicRooms =
                        await roomsClient
                            .ListAsync(
                                token,
                                RoomKind.Public)
                            .ConfigureAwait(false);

                    var venueRooms =
                        await roomsClient
                            .ListAsync(
                                token,
                                RoomKind.Venue)
                            .ConfigureAwait(false);

                    roomBrowseList =
                        publicRooms
                            .Concat(
                                venueRooms)
                            .OrderByDescending(
                                room =>
                                    room.ViewerCount)
                            .ThenBy(
                                room =>
                                    room.HostDisplayName,
                                StringComparer.OrdinalIgnoreCase)
                            .ToArray();

                    roomBrowseError =
                        roomsClient.LastFailure?.UserMessage;
                }
                finally
                {
                    roomBrowseLoading =
                        false;
                }
            });
    }

    private void DrawWatchPartyBrowseButton(
        float width,
        FontAwesomeIcon icon,
        string label,
        Action onClick)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                Ui(66f));

        var max =
            origin +
            size;

        var hovered =
            ImGui.IsMouseHoveringRect(
                origin,
                max);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            max,
            ImGui.GetColorU32(
                hovered
                    ? new Vector4(
                        0.12f,
                        0.08f,
                        0.22f,
                        1f)
                    : new Vector4(
                        0.055f,
                        0.045f,
                        0.10f,
                        1f)),
            Ui(8f));

        drawList.AddRect(
            origin,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    hovered
                        ? 0.80f
                        : 0.38f)),
            Ui(8f));

        var glyph =
            icon.ToIconString();

        Vector2 glyphSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    origin.X +
                    (width -
                     glyphSize.X) *
                    0.5f,
                    origin.Y +
                    Ui(10f)),
                ImGui.GetColorU32(
                    Accent),
                glyph);
        }

        var labelSize =
            ImGui.CalcTextSize(
                label);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X +
                (width -
                 labelSize.X) *
                0.5f,
                origin.Y +
                Ui(37f)),
            ImGui.GetColorU32(
                Vector4.One),
            label);

        ImGui.SetCursorScreenPos(
            origin);

        if (ImGui.InvisibleButton(
                $"##watchPartyBrowse_{label}",
                size))
        {
            onClick();
        }

        if (hovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);
        }
    }
    private void CreateEmptyWatchParty()
    {
        //
        // Public and venue rooms can be created immediately.
        //
        if (createRoomKindIndex != 1)
        {
            createRoomPassword =
                string.Empty;

            createLockedRoomPasswordError =
                null;

            StartWatchParty(
                goToPlayer: false);

            return;
        }

        //
        // Let the existing hosting validation report a sign-in error
        // before asking an unsigned-in user to choose a password.
        //
        if (CurrentSession is null)
        {
            StartWatchParty(
                goToPlayer: false);

            return;
        }

        //
        // Locked rooms collect their password in a separate confirmation
        // popup after the rest of the form has been completed.
        //
        createRoomPassword =
               string.Empty;

        createLockedRoomPasswordError =
            null;

        createLockedRoomPasswordForRoomEdit =
            false;

        createLockedRoomPasswordPopupRequested =
            true;
    }

    private void DrawCreateLockedRoomPasswordPopup()
    {
        if (!createLockedRoomPasswordPopupRequested)
        {
            return;
        }

        var popupWidth = Ui(470f);

        var popupHeight = Ui(285f);

        //
        // Cover only the Alpha Channel window. This follows the same
        // overlay pattern as the username and controller prompts.
        //
        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            new Vector2(
                parentPos.X +
                (parentSize.X -
                 popupWidth) *
                0.5f,

                parentPos.Y +
                (parentSize.Y -
                 popupHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

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
                "##createLockedRoomPasswordOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        //
        // Darken the Alpha Channel window behind the prompt.
        //
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

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        //
        // Popup background and border.
        //
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

        const float padding =
            20f;

        var contentWidth =
            popupWidth -
            padding *
            2f;

        //
        // Header icon.
        //
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
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.24f)),
            24);

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var lockGlyph =
                FontAwesomeIcon.Lock
                    .ToIconString();

            var lockGlyphSize =
                ImGui.CalcTextSize(
                    lockGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconCenter -
                lockGlyphSize *
                0.5f,
                ImGui.GetColorU32(
                    Accent),
                lockGlyph);
        }

        //
        // Title.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        SetUiFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Set a room password");

        SetUiFontScale(
            1f);

        //
        // Explanation.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(63f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        ImGui.TextColored(
            MutedText,
            "Users will require the password to join your room and locked rooms do not publish what you're watching to the party directory.");

        ImGui.PopTextWrapPos();

        //
        // Password field.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(119f)));

        ImGui.TextColored(
            MutedText,
            "Room password");

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(143f)));

        ImGui.SetNextItemWidth(
            contentWidth);

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        var submitRequested =
            false;

        using (ImRaii.PushColor(
                   ImGuiCol.FrameBg,
                   new Vector4(
                       0.025f,
                       0.03f,
                       0.055f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.75f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   5f))
        {
            submitRequested =
                ImGui.InputTextWithHint(
                    "##createLockedRoomPassword",
                    "Enter a password",
                    ref createRoomPassword,
                    64,
                    ImGuiInputTextFlags.Password |
                    ImGuiInputTextFlags.EnterReturnsTrue);
        }

        if (createLockedRoomPasswordError is { } passwordError)
        {
            ImGui.SetCursorScreenPos(
                popupPos +
                new Vector2(
                    padding,
                    Ui(181f)));

            ImGui.TextColored(
                Danger,
                passwordError);
        }

        //
        // Footer divider.
        //
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
                BorderSubtle),
            1f);

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        var cancelRequested =
            false;

        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       0.08f,
                       0.08f,
                       0.14f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   new Vector4(
                       0.14f,
                       0.11f,
                       0.22f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   new Vector4(
                       0.18f,
                       0.13f,
                       0.28f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.55f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f))
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

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   Accent))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   AccentHover))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   AccentActive))
        {
            if (ImGui.Button(
                 createLockedRoomPasswordForRoomEdit
                     ? "Save Changes"
                     : "Create Room",
                 new Vector2(
                     buttonWidth,
                     Ui(36f))))
            {
                submitRequested =
                    true;
            }
        }

        var createConfirmed =
            false;

        if (cancelRequested)
        {
            createLockedRoomPasswordPopupRequested =
                false;

            createLockedRoomPasswordError =
                null;

            createRoomPassword =
                string.Empty;
        }
        else if (submitRequested)
        {
            createRoomPassword =
                createRoomPassword.Trim();

            if (string.IsNullOrWhiteSpace(
                    createRoomPassword))
            {
                createLockedRoomPasswordError =
                    "Please enter a password.";
            }
            else
            {
                createLockedRoomPasswordPopupRequested =
                    false;

                createLockedRoomPasswordError =
                    null;

                createConfirmed =
                    true;
            }
        }

        ImGui.End();

        //
        // Complete the requested action only after closing the
        // overlay's ImGui window.
        //
        if (createConfirmed)
        {
            if (createLockedRoomPasswordForRoomEdit)
            {
                createLockedRoomPasswordForRoomEdit =
                    false;

                SavePartyRoomDetails();
            }
            else
            {
                StartWatchParty(
                    goToPlayer: false);
            }
        }
    }

    private void StartWatchParty(bool goToPlayer)
    {
        if (CurrentSession is null)
        {
            joinError =
                "Sign in to host a watch party.";

            Plugin.ChatGui.Print(
                "[AlphaChannel] Sign in before hosting a watch party.");

            return;
        }

        if (createRoomKindIndex == 1 &&
            string.IsNullOrWhiteSpace(createRoomPassword))
        {
            joinError =
                "Locked rooms need a password.";

            return;
        }

        ApplyCreateRoomToStream();

        gameplayStreamOfferDismissed =
            false;

        browserStreamOfferDismissed =
            false;

        var engine =
            screenController.Engine;

        var current =
            queue.Current;

        var shareExistingExclusivePlayback =
            pendingWatchPartyMediaKind is
                PendingWatchPartyMediaKind.None or
                PendingWatchPartyMediaKind.GameRoom;

        var existingGame =
            shareExistingExclusivePlayback &&
            engine.IsPlayingGame;

        var existingBrowser =
            shareExistingExclusivePlayback &&
            engine.IsPlayingBrowser;

        var existingLocalVideo =
            shareExistingExclusivePlayback &&
            video.IsPlayingLocalVideo;

        //
        // Keep every existing playback surface intact. Only show the empty
        // room screen when there is genuinely no active media to inherit.
        //
        if (current is null &&
            !engine.IsPlayingGame &&
            !engine.IsPlayingBrowser &&
            !video.IsPlayingLocalVideo)
        {
            engine.ShowWaitingScreen();
        }

        //
        // Publish the current queue item immediately. This also changes the
        // StreamClient to Hosting before an exclusive source is armed below.
        // Games, browser playback, and local files replace this initial state
        // with their public relay URL without stopping local playback.
        //
        var (position, _, paused) =
            current is null
                ? (0d, 0d, true)
                : video.GetProgress();

        var mediaTitle =
            current is not null &&
            !string.IsNullOrWhiteSpace(current.Title) &&
            !string.Equals(
                current.Title,
                current.Url,
                StringComparison.OrdinalIgnoreCase)
                ? current.Title
                : null;

        var publishedUrl =
            current?.Url;

        if (!string.IsNullOrWhiteSpace(publishedUrl) &&
            engine.IsAudioOnly)
        {
            publishedUrl =
                AudioVisualizerSelection.AddToUrl(
                    publishedUrl,
                    PartyVisualizerMode,
                    PartyVisualizerTheme);
        }

        _ = stream.PublishStateAsync(
            publishedUrl,
            position,
            paused,
            engine.IsActive
                ? engine.ScreenPosition
                : null,
            engine.IsActive
                ? engine.ScreenYaw
                : null,
            engine.IsActive
                ? engine.ScreenScale
                : null,
            engine.IsActive
                ? engine.DisableFixedScreenScaleRatio
                : null,
            engine.IsActive
                ? engine.ScreenWidthScale
                : null,
            engine.IsActive
                ? engine.ScreenHeightScale
                : null,
            mediaTitle,
            current?.ThumbnailUrl);

        if (existingGame)
        {
            StartGameWatchPartyBroadcast();
        }
        else if (existingBrowser)
        {
            StartBrowserWatchPartyBroadcast();
        }
        else if (existingLocalVideo)
        {
            StartLocalVideoWatchPartyBroadcast();
        }

        if (goToPlayer)
        {
            currentPage =
                HomePage.Player;

            playerSourceTab =
                0;
        }

        Plugin.ChatGui.Print(
            stream.IsConnected
                ? "[AlphaChannel] Watch party is live. Friends join with your Alpha Channel username."
                : "[AlphaChannel] Connecting… the room will go live when the relay is up.");
        CompletePendingWatchPartyMedia();
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
     string? hlsUrl)
    {
        var romPath = gameBroadcastSystem switch
        {
            GameSystem.Snes => snesSelectedRomPath,
            GameSystem.Nes => nesSelectedRomPath,
            GameSystem.GameBoyAdvance => gameBoyAdvanceSelectedRomPath,
            _ => gameBoySelectedRomPath
        };

        var gameName =
            string.IsNullOrWhiteSpace(
                romPath)
                ? string.Empty
                : LibraryGameName(romPath);

        var title =
            string.IsNullOrWhiteSpace(
                gameName)
                ? gameBroadcastSystem switch
                {
                    GameSystem.Snes => "SNES Gameplay",
                    GameSystem.Nes => "NES Gameplay",
                    GameSystem.GameBoyAdvance => "Game Boy Advance Gameplay",
                    _ => "Game Boy Gameplay"
                }
                : $"Playing: {gameName}";

        await stream.PublishStateAsync(
            hlsUrl,
            0d,
            false,
            screenController.Engine.ScreenPosition,
            screenController.Engine.ScreenYaw,
            screenController.Engine.ScreenScale,
            title,
            null);
    }

    private void DrawHostLeaveConfirmationPopup()
    {
        if (!hostLeaveConfirmationRequested)
        {
            return;
        }

        var popupWidth = Ui(510f);

        var popupHeight = Ui(285f);

        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            new Vector2(
                parentPos.X +
                (parentSize.X -
                 popupWidth) *
                0.5f,

                parentPos.Y +
                (parentSize.Y -
                 popupHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

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
                "##hostLeaveWatchPartyOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        //
        // Darken the Alpha Channel window behind the prompt.
        //
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

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        //
        // Popup card.
        //
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

        const float padding =
            20f;

        var contentWidth =
            popupWidth -
            padding *
            2f;

        //
        // Warning icon.
        //
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
                    0.22f)),
            24);

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var warningGlyph =
                FontAwesomeIcon.ExclamationTriangle
                    .ToIconString();

            var warningGlyphSize =
                ImGui.CalcTextSize(
                    warningGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconCenter -
                warningGlyphSize *
                0.5f,
                ImGui.GetColorU32(
                    Danger),
                warningGlyph);
        }

        //
        // Title.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        SetUiFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Close this Watch Party?");

        SetUiFontScale(
            1f);

        //
        // Explanation.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(67f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        ImGui.TextColored(
            Vector4.One,
            "As the host, leaving will close the room and kick all viewers from your current Watch Party.");

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
            "Alternatively, you can transfer host to another viewer before leaving if you'd like the room to remain open.");

        ImGui.PopTextWrapPos();

        //
        // Footer divider.
        //
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
                BorderSubtle),
            1f);

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        var cancelRequested =
            false;

        var closeRequested =
            false;

        //
        // Cancel.
        //
        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       0.08f,
                       0.08f,
                       0.14f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   new Vector4(
                       0.14f,
                       0.11f,
                       0.22f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   new Vector4(
                       0.18f,
                       0.13f,
                       0.28f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.55f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f))
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

        //
        // Destructive confirmation.
        //
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       Danger.X,
                       Danger.Y,
                       Danger.Z,
                       0.72f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   Danger))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   new Vector4(
                       Danger.X * 0.82f,
                       Danger.Y * 0.82f,
                       Danger.Z * 0.82f,
                       1f)))
        {
            closeRequested =
                ImGui.Button(
                    "Close Room",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        if (cancelRequested ||
            closeRequested)
        {
            hostLeaveConfirmationRequested =
                false;
        }

        ImGui.End();

        //
        // Perform the network and playback changes after closing the
        // overlay window.
        //
        if (closeRequested)
        {
            LeaveStream();
            partyChatItems.Clear();
        }
    }

    private void DrawViewerTvSpawnPrompt()
    {
        if (!viewerTvSpawnPromptRequested)
        {
            return;
        }

        var popupWidth = Ui(470f);

        var popupHeight = Ui(245f);

        //
        // Cover only the Alpha Channel window, matching the username,
        // controller configuration, and room-password prompts.
        //
        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            new Vector2(
                parentPos.X +
                (parentSize.X -
                 popupWidth) *
                0.5f,

                parentPos.Y +
                (parentSize.Y -
                 popupHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

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
                "##viewerTvSpawnOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        //
        // Darkened window background.
        //
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

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        //
        // Popup card and accent border.
        //
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

        const float padding =
            20f;

        var contentWidth =
            popupWidth -
            padding *
            2f;

        //
        // Header icon.
        //
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
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.24f)),
            24);

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var tvGlyph =
                FontAwesomeIcon.Tv
                    .ToIconString();

            var tvGlyphSize =
                ImGui.CalcTextSize(
                    tvGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                iconCenter -
                tvGlyphSize *
                0.5f,
                ImGui.GetColorU32(
                    Accent),
                tvGlyph);
        }

        //
        // Title.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        SetUiFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Watch Party content available");

        SetUiFontScale(
            1f);

        //
        // Main question.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(67f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        ImGui.TextColored(
            Vector4.One,
            "Watch party host is currently sharing content. Spawn virtual screen?");

        ImGui.PopTextWrapPos();

        //
        // Supporting note.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(112f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        ImGui.TextColored(
            MutedText,
            "You can also spawn/despawn the TV via the Watch Party 'Now Playing' tab.");

        ImGui.PopTextWrapPos();

        //
        // Footer divider.
        //
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
                BorderSubtle),
            1f);

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        var noRequested =
            false;

        var yesRequested =
            false;

        //
        // Secondary "No" button.
        //
        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       0.08f,
                       0.08f,
                       0.14f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   new Vector4(
                       0.14f,
                       0.11f,
                       0.22f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   new Vector4(
                       0.18f,
                       0.13f,
                       0.28f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.55f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f))
        {
            noRequested =
                ImGui.Button(
                    "No",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        ImGui.SameLine(
            0f,
            buttonGap);

        //
        // Primary "Yes" button.
        //
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   Accent))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   AccentHover))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   AccentActive))
        {
            yesRequested =
                ImGui.Button(
                    "Yes",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        if (noRequested)
        {
            ViewerTvEnabled =
                false;

            viewerTvSpawnPromptRequested =
                false;
        }

        if (yesRequested)
        {
            ViewerTvEnabled =
                true;

            viewerTvSpawnPromptRequested =
                false;
        }

        ImGui.End();

        //
        // Spawn only after closing the overlay window, matching the
        // deferred-action pattern used by the password prompt.
        //
        if (yesRequested)
        {
            OnViewerTvSpawnRequested
                ?.Invoke();
        }
    }

    private void DrawWatchPartyFeatures(
      float rowHeight)
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        var gap =
            Ui(8f);

        var cardWidth =
            (width - gap * 2f) /
            3f;

        DrawWatchPartyFeatureCard(
            FontAwesomeIcon.CommentDots.ToIconString(),
            "Live Chat",
            cardWidth,
            rowHeight,
            new Vector4(
                0.35f,
                0.75f,
                1.00f,
                1f));

        ImGui.SameLine(
            0f,
            gap);

        DrawWatchPartyFeatureCard(
            FontAwesomeIcon.Heart.ToIconString(),
            "Reactions",
            cardWidth,
            rowHeight,
            new Vector4(
                1.00f,
                0.55f,
                0.75f,
                1f));

        ImGui.SameLine(
            0f,
            gap);

        DrawWatchPartyFeatureCard(
            FontAwesomeIcon.Sync.ToIconString(),
            "Sync Playback",
            cardWidth,
            rowHeight,
            new Vector4(
                0.45f,
                0.90f,
                0.60f,
                1f));
    }

    private void DrawWatchPartyFeatureCard(
        string icon,
        string title,
        float width,
        float height,
        Vector4 featureColor)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin +
            size,
            ImGui.GetColorU32(
                new Vector4(
                    featureColor.X,
                    featureColor.Y,
                    featureColor.Z,
                    0.07f)),
            Ui(9f));

        drawList.AddRect(
            origin,
            origin +
            size,
            ImGui.GetColorU32(
                new Vector4(
                    featureColor.X,
                    featureColor.Y,
                    featureColor.Z,
                    0.24f)),
            Ui(9f));

        var iconGlyphSize =
            Vector2.Zero;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            iconGlyphSize =
                ImGui.CalcTextSize(
                    icon);
        }

        var titleSize =
            ImGui.CalcTextSize(
                title);

        var contentWidth =
            iconGlyphSize.X +
            Ui(9f) +
            titleSize.X;

        var contentX =
            origin.X +
            (width -
             contentWidth) *
            0.5f;

        var contentY =
            origin.Y +
            (height -
             MathF.Max(
                 iconGlyphSize.Y,
                 titleSize.Y)) *
            0.5f;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    contentX,
                    contentY),
                ImGui.GetColorU32(
                    featureColor),
                icon);
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                contentX +
                iconGlyphSize.X +
                Ui(9f),
                contentY +
                (
                    iconGlyphSize.Y -
                    titleSize.Y
                ) *
                0.5f),
            ImGui.GetColorU32(
                featureColor),
            title);

        ImGui.Dummy(
            size);
    }

    private void ApplyCreateRoomToStream()
    {
        var category =
            WatchPartyCategoryOptions[
                Math.Clamp(
                    createRoomCategoryIndex,
                    0,
                    WatchPartyCategoryOptions.Length - 1)];

        var rating =
            createRoomAdultOnly
                ? 1
                : 0;

        var currentWorld =
            string.IsNullOrWhiteSpace(
                CurrentWorldName)
                ? string.Empty
                : CurrentWorldName.Trim();

        var metadata =
            $"<#{category}#{rating}#{currentWorld}#>";

        var description =
            string.IsNullOrWhiteSpace(
                createRoomDescription)
                ? string.Empty
                : createRoomDescription.Trim();

        stream.RoomDescription =
            string.IsNullOrEmpty(
                description)
                ? metadata
                : $"{metadata} {description}";

        stream.RoomLocation =
            string.IsNullOrWhiteSpace(
                createRoomLocation)
                ? string.Empty
                : createRoomLocation.Trim();

        stream.RoomKind =
            createRoomKindIndex switch
            {
                1 =>
                    RoomKind.Locked,

                2 =>
                    RoomKind.Venue,

                _ =>
                    RoomKind.Public,
            };

        stream.RoomPassword =
            stream.RoomKind ==
            RoomKind.Locked
                ? createRoomPassword.Trim()
                : string.Empty;
    }

    private void DrawCreateRoomFields(
     float width)
    {
        const int descriptionLimit =
            60;

        const int locationLimit =
            50;

        //
        // Preserve the caller-provided form margin. ImGui resets new
        // lines to the child window's default cursor start, so each
        // major field section restores this position explicitly.
        //
        var formLeftX =
            ImGui.GetCursorPosX();

        if (createRoomDescription.Length >
            descriptionLimit)
        {
            createRoomDescription =
                createRoomDescription[
                    ..descriptionLimit];
        }

        if (createRoomLocation.Length >
            locationLimit)
        {
            createRoomLocation =
                createRoomLocation[
                    ..locationLimit];
        }

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   Ui(7f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FramePadding,
                   UiVec(9f, 6f)))
        using (ImRaii.PushColor(
                   ImGuiCol.FrameBg,
                   new Vector4(
                       0.075f,
                       0.085f,
                       0.14f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.FrameBgHovered,
                   new Vector4(
                       0.10f,
                       0.07f,
                       0.18f,
                       1f)))
        using (ImRaii.PushColor(
                   ImGuiCol.FrameBgActive,
                   new Vector4(
                       0.12f,
                       0.08f,
                       0.22f,
                       1f)))
        using (ImRaii.PushColor(
ImGuiCol.Border,
new Vector4(
Accent.X,
Accent.Y,
Accent.Z,
0.32f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   Ui(1f)))
        {
            ImGui.SetCursorPosX(
                formLeftX);

            DrawWatchPartyFieldLabel(
                "Description",
                $"{createRoomDescription.Length}/{descriptionLimit}",
                width);

            ImGui.SetCursorPosX(
                formLeftX);

            ImGui.SetNextItemWidth(
                width);

            ImGui.InputTextWithHint(
                "##createRoomDescription",
                "What are you watching?",
                ref createRoomDescription,
                descriptionLimit);

            ImGui.Dummy(
                           UiVec(
                               0f,
                               1f));

            ImGui.SetCursorPosX(
      formLeftX);

            DrawWatchPartyFieldLabel(
                "Location",
                $"{createRoomLocation.Length}/{locationLimit}",
                width);

            ImGui.SetCursorPosX(
                formLeftX);

            //
            // Keep the Location field editable, with a compact button
            // that fetches the player's location only when clicked.
            //
            var locationButtonGap =
                Ui(6f);

            var locationButtonSize =
                ImGui.GetFrameHeight();

            var locationInputWidth =
                Math.Max(
                    Ui(80f),
                    width -
                    locationButtonSize -
                    locationButtonGap);

            ImGui.SetNextItemWidth(
                locationInputWidth);

            ImGui.InputTextWithHint(
                "##createRoomLocation",
                "House, venue or meeting place",
                ref createRoomLocation,
                locationLimit);

            ImGui.SameLine(
                0f,
                locationButtonGap);

            var locationButtonClicked =
                false;

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       Ui(7f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FramePadding,
                       Vector2.Zero))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.18f)))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonHovered,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.34f)))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.48f)))
            using (ImRaii.PushColor(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.72f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameBorderSize,
                       Ui(1f)))
            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                locationButtonClicked =
                    ImGui.Button(
                        FontAwesomeIcon
                            .MapMarkerAlt
                            .ToIconString() +
                        "##useCurrentWatchPartyLocation",
                        new Vector2(
                            locationButtonSize,
                            locationButtonSize));
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Use your current FFXIV location");
            }

            if (locationButtonClicked)
            {
                var detectedLocation =
                    GetCurrentWatchPartyLocation();

                if (!string.IsNullOrWhiteSpace(
                        detectedLocation))
                {
                    createRoomLocation =
                        detectedLocation.Length <=
                        locationLimit
                            ? detectedLocation
                            : detectedLocation[
                                ..locationLimit];
                }
                else
                {
                    Plugin.ChatGui.Print(
                        "[AlphaChannel] Your current location could not be detected.");
                }
            }

            ImGui.Dummy(
                UiVec(
                    0f,
                    1f));

            ImGui.SetCursorPosX(
                formLeftX);

            var fieldGap =
                Ui(10f);

            var halfWidth =
                (width - fieldGap) /
                2f;

            var fieldsOrigin =
                ImGui.GetCursorScreenPos();

            //
            // Category column
            //
            ImGui.SetCursorScreenPos(
                fieldsOrigin);

            ImGui.BeginGroup();

            ImGui.TextColored(
                MutedText,
                "Category");

            ImGui.SetNextItemWidth(
                halfWidth);

            ImGui.Combo(
                "##createRoomCategory",
                ref createRoomCategoryIndex,
                WatchPartyCategoryOptions,
                WatchPartyCategoryOptions.Length);

            ImGui.EndGroup();

            var categoryBottomY =
                ImGui.GetItemRectMax().Y;

            //
            // Room type column
            //
            ImGui.SetCursorScreenPos(
                new Vector2(
                    fieldsOrigin.X +
                    halfWidth +
                    fieldGap,
                    fieldsOrigin.Y));

            ImGui.BeginGroup();

            ImGui.TextColored(
                MutedText,
                "Type");

            ImGui.SetNextItemWidth(
                halfWidth);

            ImGui.Combo(
                "##createRoomKind",
                ref createRoomKindIndex,
                ["Public", "Locked", "Venue"],
                3);

            ImGui.EndGroup();

            var visibilityBottomY =
                ImGui.GetItemRectMax().Y;

            //
            // Continue beneath the taller of the two columns.
            //
            ImGui.SetCursorScreenPos(
                new Vector2(
                    fieldsOrigin.X,
                    MathF.Max(
                        categoryBottomY,
                        visibilityBottomY)));

            ImGui.Dummy(
                Vector2.Zero);
        }
    }

    private void DrawWatchPartyFieldLabel(
       string label,
       string counter,
       float width)
    {
        var rowStartX =
            ImGui.GetCursorPosX();

        ImGui.TextColored(
            MutedText,
            label);

        var counterSize =
            ImGui.CalcTextSize(
                counter);

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            rowStartX +
            width -
            counterSize.X);

        ImGui.TextColored(
            new Vector4(
                MutedText.X,
                MutedText.Y,
                MutedText.Z,
                0.72f),
            counter);

        //
        // The right-aligned counter changes the active cursor column.
        // Restore the form's original left edge before the following
        // input control is drawn.
        //
        ImGui.SetCursorPosX(
            rowStartX);
    }
    private void DrawWatchPartyAdultToggle()
    {
        var switchSize =
            UiVec(
                38f,
                20f);

        var switchOrigin =
            ImGui.GetCursorScreenPos();

        if (ImGui.InvisibleButton(
                "##createRoomAdultOnly",
                switchSize))
        {
            createRoomAdultOnly =
                !createRoomAdultOnly;
        }

        var hovered =
            ImGui.IsItemHovered();

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            switchOrigin,
            switchOrigin +
            switchSize,
            ImGui.GetColorU32(
                createRoomAdultOnly
                    ? Accent
                    : hovered
                        ? new Vector4(
                            0.22f,
                            0.20f,
                            0.29f,
                            1f)
                        : new Vector4(
                            0.14f,
                            0.13f,
                            0.19f,
                            1f)),
            switchSize.Y *
            0.5f);

        var knobRadius =
            Ui(7f);

        var knobCenter =
            new Vector2(
                createRoomAdultOnly
                    ? switchOrigin.X +
                      switchSize.X -
                      switchSize.Y *
                      0.5f
                    : switchOrigin.X +
                      switchSize.Y *
                      0.5f,
                switchOrigin.Y +
                switchSize.Y *
                0.5f);

        drawList.AddCircleFilled(
            knobCenter,
            knobRadius,
            ImGui.GetColorU32(
                Vector4.One),
            18);

        ImGui.SameLine(
            0f,
            Ui(7f));

        ImGui.TextUnformatted(
            "18+");

        if (hovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);

            ImGui.SetTooltip(
                "Mark room as an 18+ adult only room");
        }
    }

    private void LoadRoomBrowse(string title, RoomKind? kind, bool friendsOnly)
    {
        roomBrowseTitle = title;
        roomBrowseFriendsOnly = friendsOnly;
        roomBrowseLoading = true;
        roomBrowseError = null;
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
            roomBrowseError = roomsClient.LastFailure?.UserMessage;
            roomBrowseLoading = false;
        });
    }

    private void DrawRoomBrowseList(float width)
    {
        if (roomBrowseTitle is null)
        {
            return;
        }

        ImGui.Dummy(UiVec(0, 8));
        ImGui.Text(roomBrowseTitle);
        if (roomBrowseLoading)
        {
            ImGui.TextColored(MutedText, "Loading…");
            return;
        }

        if (!string.IsNullOrWhiteSpace(roomBrowseError))
        {
            ImGui.TextColored(Danger, roomBrowseError);
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
