using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string djStreamUrl = string.Empty;


    //
    // Radio UI
    //

    private string djStationNameInput = string.Empty;
    private string djStationUrlInput = string.Empty;

    private readonly List<DjSavedStation> djSavedStations = [];

    private sealed class DjSavedStation
    {
        internal string Name { get; }
        internal string Url { get; }

        internal DjSavedStation(
            string name,
            string url)
        {
            Name = name;
            Url = url;
        }
    }


    private bool djSimpleGuideOpen;
    private bool djAdvancedGuideOpen;

    private int djSimpleGuideStep;
    private int djAdvancedGuideStep;

    // ---------------------------------------------------------
    // Music / DJ page
    // ---------------------------------------------------------

    private void DrawDJLive()
    {
        //
        // Page heading
        //

        ImGui.SetWindowFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Music / DJ");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                3f));

        ImGui.SetWindowFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "Play music or broadcast a live DJ set to your Alpha Channel watch party.");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));


        //
        // =====================================================
        // Main Music / Radio dashboard
        // =====================================================
        //

        DrawDjMediaDashboard();


        ImGui.Dummy(
     new Vector2(
         0f,
         12f));


        //
        // =====================================================
        // Help / broadcast setup
        // =====================================================
        //

        ImGui.Separator();

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));


        ImGui.SetWindowFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Need help broadcasting your own music stream?");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                2f));

        ImGui.SetWindowFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "Choose the setup that best matches how you want to broadcast.");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));


        DrawDjSetupCards();


        //
        // Existing setup-guide modals.
        //

        DrawDjSimpleGuide();
        DrawDjAdvancedGuide();
    }


    // ---------------------------------------------------------
    // Main Music / Radio dashboard
    // ---------------------------------------------------------

    private void DrawDjMediaDashboard()
    {
        const float gap =
            16f;

        const float baseDashboardHeight =
     440f;

        // A rendered saved-station row is 44px, but ImGui's
        // item/cursor spacing means its real layout cost is higher.
        // Give each station enough room that the add form never
        // gets pushed into the bottom edge of the dashboard.
        const float savedStationHeight =
            68f;

        var dashboardHeight =
            baseDashboardHeight +
            djSavedStations.Count *
            savedStationHeight;

        var separatorHeight =
            dashboardHeight -
            34f;


        using (
                ImRaii.PushStyle(
                    ImGuiStyleVar.WindowPadding,
                new Vector2(
                    16f,
                    16f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                12f))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                CardBg))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.28f)))
        using (
var dashboard =
    ImRaii.Child(
        "##djMediaDashboard",
        new Vector2(
            -1f,
            dashboardHeight),
        true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!dashboard)
            {
                return;
            }


            //
            // Calculate the columns from the actual usable width
            // inside the padded dashboard.
            //

            var availableWidth =
                ImGui.GetContentRegionAvail().X;

            var leftWidth =
                MathF.Max(
                    300f,
                    availableWidth * 0.46f);

            var rightWidth =
              MathF.Max(
                  300f,
                  availableWidth -
                  leftWidth -
                  gap * 2f -
                  1f);


            //
            // Left column
            //

            DrawDjDirectAudioPanel(
                leftWidth);


            ImGui.SameLine(
                0f,
                gap);


            //
            // Vertical separator
            //

            var separatorOrigin =
                ImGui.GetCursorScreenPos();

            var drawList =
                ImGui.GetWindowDrawList();

            drawList.AddLine(
          separatorOrigin +
          new Vector2(
              0f,
              2f),
          separatorOrigin +
          new Vector2(
              0f,
              separatorHeight),
          ImGui.GetColorU32(
              new Vector4(
                  MutedText.X,
                  MutedText.Y,
                  MutedText.Z,
                  0.16f)),
          1f);


            ImGui.Dummy(
                new Vector2(
                    1f,
                    separatorHeight));


            ImGui.SameLine(
                0f,
                gap);


            //
            // Right column
            //

            DrawDjRadioPanel(
                rightWidth);
        }
    }


    // ---------------------------------------------------------
    // Direct audio
    // ---------------------------------------------------------

    private void DrawDjDirectAudioPanel(
        float width)
    {
        using var group =
            ImRaii.Group();

        ImGui.SetWindowFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Direct Audio");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                3f));

        if (ImGui.Button("Use AlphaChannel radio", new Vector2(MathF.Min(230f, width), 32f)))
        {
            IssueAlphaChannelRadio();
        }

        if (radioError is { } radioIssue)
        {
            ImGui.TextColored(Danger, radioIssue);
        }

        if (radioCredentials is { } radio)
        {
            ImGui.TextColored(MutedText, $"Mount {radio.Mount}");
            ImGui.TextWrapped($"Server {radio.SourceHost}  Port {radio.SourcePort}  User {radio.SourceUser}");
            if (!string.IsNullOrEmpty(radio.SourcePassword))
            {
                ImGui.TextWrapped($"Source password {radio.SourcePassword}");
            }

            ImGui.TextWrapped(radio.ListenUrl);
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));

        ImGui.SetWindowFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "Play an MP3 file or live audio stream by URL.");

        ImGui.SetWindowFontScale(
            1f);


        ImGui.Dummy(
            new Vector2(
                0f,
                16f));


        //
        // URL field + paste
        //

        var pasteWidth =
            42f;

        var inputWidth =
            MathF.Max(
                120f,
                width -
                pasteWidth -
                12f);


        ImGui.SetNextItemWidth(
            inputWidth);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FramePadding,
                new Vector2(
                    12f,
                    9f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.FrameBg,
                FrameBg)
                .Push(
                    ImGuiCol.FrameBgHovered,
                    FrameBgHover)
                .Push(
                    ImGuiCol.FrameBgActive,
                    FrameBgHover))
        {
            ImGui.InputTextWithHint(
                "##djStreamUrl",
                "https://example.com/live.mp3",
                ref djStreamUrl,
                2000);
        }


        ImGui.SameLine(
            0f,
            8f);


        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
        using (
            ImRaii.PushColor(
                ImGuiCol.Button,
                FrameBg)
                .Push(
                    ImGuiCol.ButtonHovered,
                    FrameBgHover)
                .Push(
                    ImGuiCol.ButtonActive,
                    FrameBgHover))
        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            if (ImGui.Button(
                    FontAwesomeIcon.Clipboard
                        .ToIconString(),
                    new Vector2(
                        pasteWidth,
                        36f)))
            {
                var clipboard =
                    ImGui.GetClipboardText();

                if (!string.IsNullOrWhiteSpace(
                        clipboard))
                {
                    djStreamUrl =
                        clipboard.Trim();
                }
            }
        }


        ImGui.Dummy(
            new Vector2(
                0f,
                14f));


        //
        // Play button
        //

        using (
            ImRaii.Disabled(
                string.IsNullOrWhiteSpace(
                    djStreamUrl)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
        using (
            ImRaii.PushColor(
                ImGuiCol.Button,
                Accent)
                .Push(
                    ImGuiCol.ButtonHovered,
                    AccentHover)
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentActive))
        {
            var buttonPos =
                ImGui.GetCursorScreenPos();

            var buttonSize =
                new Vector2(
                    MathF.Min(
                        230f,
                        width),
                    38f);

            if (ImGui.Button(
                    "##playDjStream",
                    buttonSize))
            {
                PlayDjStream();
            }

            DrawPlayerActionButtonContent(
                buttonPos,
                buttonSize,
                FontAwesomeIcon.Play,
                "Play on TV",
                Vector4.One);
        }


        ImGui.Dummy(
            new Vector2(
                0f,
                18f));


        //
        // Supported-format information box
        //

        DrawDjSupportedFormatsBox(
            width);

        ImGui.Dummy(
    new Vector2(
        0f,
        10f));

        ImGui.SetWindowFontScale(
            0.74f);

        ImGui.TextColored(
            MutedText,
            "Direct audio links can also be shared through Watch Party.");

        ImGui.SetWindowFontScale(
            1f);
    }


    private void DrawDjSupportedFormatsBox(
        float width)
    {
        const float height =
            72f;

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                9f))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    Accent.X * 0.14f,
                    Accent.Y * 0.12f,
                    Accent.Z * 0.20f,
                    0.92f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.22f)))
        using (
            var box =
                ImRaii.Child(
                    "##djSupportedFormats",
                    new Vector2(
                        width,
                        height),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!box)
            {
                return;
            }

            var start =
                ImGui.GetCursorScreenPos();

            var drawList =
                ImGui.GetWindowDrawList();


            //
            // Icon
            //

            var iconCenter =
                start +
                new Vector2(
                    22f,
                    36f);

            drawList.AddCircleFilled(
                iconCenter,
                14f,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.18f)),
                24);

            using (
                ImRaii.PushFont(
                    UiBuilder.IconFont))
            {
                var glyph =
                    FontAwesomeIcon.Lightbulb
                        .ToIconString();

                var glyphSize =
                    ImGui.CalcTextSize(
                        glyph);

                drawList.AddText(
                    iconCenter -
                    glyphSize / 2f,
                    ImGui.GetColorU32(
                        Accent),
                    glyph);
            }


            //
            // Copy
            //

            ImGui.SetCursorPos(
               new Vector2(
                   48f,
                   10f));

            ImGui.SetWindowFontScale(
                0.86f);

            ImGui.TextColored(
                Vector4.One,
                "Supported formats");

            ImGui.SetWindowFontScale(
                1f);


            ImGui.SetCursorPos(
        new Vector2(
            48f,
            34f));

            ImGui.SetWindowFontScale(
                0.78f);

            ImGui.TextColored(
                MutedText,
                "MP3, AAC, OGG, M4A, WAV and most live audio streams.");

            ImGui.SetWindowFontScale(
                1f);
        }
    }


    // ---------------------------------------------------------
    // Radio stations
    // ---------------------------------------------------------

    private void DrawDjRadioPanel(
        float width)
    {
        using var group =
            ImRaii.Group();

        ImGui.SetWindowFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Radio Stations");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                3f));

        ImGui.SetWindowFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "Choose a station below or add your own stream.");

        ImGui.SetWindowFontScale(
            1f);


        ImGui.Dummy(
            new Vector2(
                0f,
                6f));


        ImGui.SetWindowFontScale(
            0.78f);

        ImGui.TextColored(
            MutedText,
            "FEATURED STATIONS");

        ImGui.SetWindowFontScale(
            1f);


        ImGui.Dummy(
            new Vector2(
                0f,
                3f));


        //
        // Featured placeholders.
        //

        DrawDjFeaturedStation(
            "##djFeaturedXiv",
            FontAwesomeIcon.BroadcastTower,
            "XIV Radio",
            "FFXIV music, talk & community",
            width);

        ImGui.Dummy(
            new Vector2(
                0f,
                3f));

        DrawDjFeaturedStation(
            "##djFeaturedLofi",
            FontAwesomeIcon.Headphones,
            "Lofi Beats",
            "Chill beats and background music",
            width);

        ImGui.Dummy(
            new Vector2(
                0f,
                3f));

        DrawDjFeaturedStation(
            "##djFeaturedRetro",
            FontAwesomeIcon.Gamepad,
            "Retro Game Radio",
            "Classic gaming music",
            width);


        ImGui.Dummy(
            new Vector2(
                0f,
                4f));


        //
        // Saved station section
        //

        ImGui.SetWindowFontScale(
            0.78f);

        ImGui.TextColored(
            MutedText,
            "YOUR STATIONS");

        ImGui.SetWindowFontScale(
            1f);


        ImGui.Dummy(
            new Vector2(
                0f,
                3f));


        //
        // Display custom stations.
        //

        for (var index = 0;
             index < djSavedStations.Count;
             index++)
        {
            var station =
                djSavedStations[index];

            DrawDjSavedStation(
                station,
                index,
                width);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    4f));
        }


        //
        // Add station form
        //

        var addButtonWidth =
            105f;

        const float fieldGap =
            8f;

        var remainingForFields =
            width -
            addButtonWidth -
            fieldGap * 2f;

        var nameWidth =
            MathF.Max(
                100f,
                remainingForFields * 0.40f);

        var urlWidth =
            MathF.Max(
                140f,
                remainingForFields -
                nameWidth);


        ImGui.SetNextItemWidth(
            nameWidth);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
        using (
            ImRaii.PushColor(
                ImGuiCol.FrameBg,
                FrameBg)
                .Push(
                    ImGuiCol.FrameBgHovered,
                    FrameBgHover)
                .Push(
                    ImGuiCol.FrameBgActive,
                    FrameBgHover))
        {
            ImGui.InputTextWithHint(
                "##djStationName",
                "Station name",
                ref djStationNameInput,
                100);
        }


        ImGui.SameLine(
            0f,
            fieldGap);


        ImGui.SetNextItemWidth(
            urlWidth);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
        using (
            ImRaii.PushColor(
                ImGuiCol.FrameBg,
                FrameBg)
                .Push(
                    ImGuiCol.FrameBgHovered,
                    FrameBgHover)
                .Push(
                    ImGuiCol.FrameBgActive,
                    FrameBgHover))
        {
            ImGui.InputTextWithHint(
                "##djStationUrl",
                "Stream URL",
                ref djStationUrlInput,
                2000);
        }


        ImGui.SameLine(
            0f,
            fieldGap);


        var canAdd =
            !string.IsNullOrWhiteSpace(
                djStationNameInput) &&
            !string.IsNullOrWhiteSpace(
                djStationUrlInput);


        using (
            ImRaii.Disabled(
                !canAdd))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
        using (
            ImRaii.PushColor(
                ImGuiCol.Button,
                Accent)
                .Push(
                    ImGuiCol.ButtonHovered,
                    AccentHover)
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentActive))
        {
            var buttonPos =
                ImGui.GetCursorScreenPos();

            var buttonSize =
                new Vector2(
                    addButtonWidth,
                    30f);

            if (ImGui.Button(
                    "##addDjStation",
                    buttonSize))
            {
                AddDjStation();
            }

            DrawPlayerActionButtonContent(
                buttonPos,
                buttonSize,
                FontAwesomeIcon.Plus,
                "Add Station",
                Vector4.One);
        }


        ImGui.Dummy(
            new Vector2(
                0f,
                4f));


        ImGui.SetWindowFontScale(
            0.72f);

        ImGui.TextColored(
            MutedText,
            djSavedStations.Count == 0
                ? "Your saved stations will appear here."
                : $"{djSavedStations.Count} saved station{(djSavedStations.Count == 1 ? string.Empty : "s")}.");

        ImGui.SetWindowFontScale(
            1f);
    }


    private void DrawDjFeaturedStation(
        string id,
        FontAwesomeIcon icon,
        string name,
        string description,
        float width)
    {
        const float height =
       42f;

        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        var min =
            origin;

        var max =
            origin +
            new Vector2(
                width,
                height);


        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                FrameBg),
            8f);

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                BorderSubtle),
            8f);


        //
        // Icon tile
        //

        var tileMin =
            min +
            new Vector2(
                5f,
                5f);

        var tileMax =
            tileMin +
            new Vector2(
                32f,
                32f);


        drawList.AddRectFilled(
            tileMin,
            tileMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.22f)),
            7f);


        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            var glyph =
                icon.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            drawList.AddText(
                tileMin +
                (tileMax - tileMin) /
                2f -
                glyphSize /
                2f,
                ImGui.GetColorU32(
                    Accent),
                glyph);
        }


        //
        // Station name
        //

        drawList.AddText(
            min +
            new Vector2(
                45f,
                5f),
                    ImGui.GetColorU32(
                Vector4.One),
            name);


        //
        // Description
        //

        drawList.AddText(
            min +
            new Vector2(
                45f,
                23f),
                    ImGui.GetColorU32(
                MutedText),
            description);


        //
        // Play button placeholder.
        //
        // We don't have real station URLs yet.
        //

        const float playWidth =
            58f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                max.X -
                playWidth -
                6f,
                min.Y + 6f));

        using (
            ImRaii.Disabled())
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
        {
            ImGui.Button(
                $"Play{id}",
                new Vector2(
                    playWidth,
                    29f));
        }


        ImGui.SetCursorScreenPos(
            new Vector2(
                min.X,
                max.Y));

        ImGui.Dummy(
            new Vector2(
                width,
                1f));
    }


    private void DrawDjSavedStation(
        DjSavedStation station,
        int index,
        float width)
    {
        const float height =
            44f;

        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        var min =
            origin;

        var max =
            origin +
            new Vector2(
                width,
                height);


        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                FrameBg),
            8f);

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                BorderSubtle),
            8f);


        //
        // Status dot
        //

        drawList.AddCircleFilled(
            min +
            new Vector2(
                14f,
                height / 2f),
            4f,
            ImGui.GetColorU32(
                Good),
            16);


        //
        // Station title
        //

        drawList.AddText(
            min +
            new Vector2(
                27f,
                8f),
            ImGui.GetColorU32(
                Vector4.One),
            station.Name);


        drawList.AddText(
            min +
            new Vector2(
                27f,
                25f),
            ImGui.GetColorU32(
                MutedText),
            "Saved station");


        //
        // Play button
        //

        const float playWidth =
            62f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                max.X -
                playWidth -
                7f,
                min.Y + 7f));


        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
        using (
            ImRaii.PushColor(
                ImGuiCol.Button,
                FrameBgHover)
                .Push(
                    ImGuiCol.ButtonHovered,
                    Accent)
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentActive))
        {
            if (ImGui.Button(
                    $"Play##djStation_{index}",
                    new Vector2(
                        playWidth,
                        30f)))
            {
                PlayDjStation(
                    station);
            }
        }


        ImGui.SetCursorScreenPos(
            new Vector2(
                min.X,
                max.Y));

        ImGui.Dummy(
            new Vector2(
                width,
                1f));
    }


    private void AddDjStation()
    {
        var name =
            djStationNameInput.Trim();

        var url =
            djStationUrlInput.Trim();

        if (name.Length == 0 ||
            url.Length == 0)
        {
            return;
        }

        djSavedStations.Add(
            new DjSavedStation(
                name,
                url));

        djStationNameInput =
            string.Empty;

        djStationUrlInput =
            string.Empty;
    }


    private void PlayDjStation(
        DjSavedStation station)
    {
        PlayDjUrl(
            station.Url,
            station.Name,
            "Radio");
    }


    // ---------------------------------------------------------
    // Shared audio playback
    // ---------------------------------------------------------

    private void IssueAlphaChannelRadio()
    {
        var token = CurrentSession?.Token;
        if (string.IsNullOrEmpty(token))
        {
            radioError = "Sign in to get a radio mount.";
            return;
        }

        radioError = null;
        _ = Task.Run(async () =>
        {
            var issued = await radioClient.IssueAsync(token).ConfigureAwait(false);
            if (issued is null)
            {
                radioError = "Couldn't issue radio credentials.";
                return;
            }

            radioCredentials = issued;
            djStreamUrl = issued.ListenUrl;
        });
    }

    private void PlayDjStream()
    {
        var url =
            djStreamUrl.Trim();

        if (string.IsNullOrWhiteSpace(
                url))
        {
            return;
        }

        PlayDjUrl(
            url,
            "Live music stream",
            "Music / DJ");

        djStreamUrl =
            string.Empty;
    }


    private void PlayDjUrl(
        string url,
        string title,
        string source)
    {
        if (string.IsNullOrWhiteSpace(
                url))
        {
            return;
        }

        queue.PlayNow(
            new Video.VideoQueueEntry(
                url,
                title,
                source,
                null,
                null));

        activePlayerDrawer =
            PlayerDrawer.Player;
    }


    // ---------------------------------------------------------
    // Setup cards
    // ---------------------------------------------------------

    private void DrawDjSetupCards()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float gap =
            12f;

        var cardWidth =
            (availableWidth - gap) /
            2f;


        DrawDjSetupCompactCard(
            "##simpleDjSetup",
            cardWidth,
            FontAwesomeIcon.Music,
            "Simple",
            "Play music or talk on mic",
            () =>
            {
                djSimpleGuideStep = 0;
                djSimpleGuideOpen = true;
            });


        ImGui.SameLine(
            0f,
            gap);


        DrawDjSetupCompactCard(
            "##advancedDjSetup",
            cardWidth,
            FontAwesomeIcon.Headphones,
            "Advanced / DJ",
            "Mix DJ sets and broadcast live",
            () =>
            {
                djAdvancedGuideStep = 0;
                djAdvancedGuideOpen = true;
            });
    }


    private void DrawDjSetupCompactCard(
        string id,
        float width,
        FontAwesomeIcon icon,
        string title,
        string subtitle,
        Action onClick)
    {
        const float height =
            62f;

        var origin =
            ImGui.GetCursorScreenPos();

        var clicked =
            ImGui.InvisibleButton(
                id,
                new Vector2(
                    width,
                    height));

        var hovered =
            ImGui.IsItemHovered();

        if (clicked)
        {
            onClick();
        }


        var drawList =
            ImGui.GetWindowDrawList();

        var min =
            origin;

        var max =
            origin +
            new Vector2(
                width,
                height);


        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                hovered
                    ? CardBgHover
                    : CardBg),
            9f);

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                hovered
                    ? new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.50f)
                    : BorderSubtle),
            9f,
            ImDrawFlags.None,
            1f);


        //
        // Icon
        //

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            var glyph =
                icon.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            drawList.AddText(
                min +
                new Vector2(
                    17f,
                    (height -
                     glyphSize.Y) /
                    2f),
                ImGui.GetColorU32(
                    Accent),
                glyph);
        }


        //
        // Text
        //

        drawList.AddText(
            min +
            new Vector2(
                49f,
                12f),
            ImGui.GetColorU32(
                Vector4.One),
            title);

        drawList.AddText(
            min +
            new Vector2(
                49f,
                34f),
            ImGui.GetColorU32(
                MutedText),
            subtitle);


        //
        // Chevron
        //

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            var chevron =
                FontAwesomeIcon.ChevronRight
                    .ToIconString();

            var chevronSize =
                ImGui.CalcTextSize(
                    chevron);

            drawList.AddText(
                max -
                new Vector2(
                    chevronSize.X +
                    15f,
                    height / 2f +
                    chevronSize.Y /
                    2f),
                ImGui.GetColorU32(
                    MutedText),
                chevron);
        }
    }

    // ---------------------------------------------------------
    // Simple guide
    // ---------------------------------------------------------

    private void DrawDjSimpleGuide()
    {
        if (djSimpleGuideOpen)
        {
            ImGui.OpenPopup(
                "Simple Music Setup##djSimpleGuide");

            djSimpleGuideOpen = false;
        }

        ImGui.SetNextWindowSize(
            new Vector2(
                650f,
                520f),
            ImGuiCond.Appearing);

        var popupOpen = true;

        if (!ImGui.BeginPopupModal(
                "Simple Music Setup##djSimpleGuide",
                ref popupOpen,
                ImGuiWindowFlags.NoCollapse))
        {
            return;
        }

        DrawDjGuideShell(
            "Simple Music Setup",
            "The easiest way to share music or microphone audio with your Alpha Channel watch party.",
            4,
            ref djSimpleGuideStep,
            step =>
            {
                switch (step)
                {
                    case 0:
                        DrawDjSimpleStep1();
                        break;

                    case 1:
                        DrawDjSimpleStep2();
                        break;

                    case 2:
                        DrawDjSimpleStep3();
                        break;

                    case 3:
                        DrawDjSimpleStep4();
                        break;
                }
            });

        ImGui.EndPopup();
    }

    private void DrawDjSimpleStep1()
    {
        DrawDjGuideHeading(
            "1. Create your streaming account",
            "Caster.fm can provide the online audio stream that Alpha Channel listens to.");

        ImGui.TextWrapped(
            "Create a Caster.fm account and set up a radio stream.");

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        ImGui.TextWrapped(
            "Caster.fm is our recommended simple option, but Alpha Channel itself only needs a compatible direct audio stream URL.");

        ImGui.Dummy(
            new Vector2(
                0f,
                16f));

        DrawDjExternalButton(
            "Open Caster.fm",
            "https://www.caster.fm/");
    }

    private void DrawDjSimpleStep2()
    {
        DrawDjGuideHeading(
            "2. Install the broadcaster",
            "Use Caster.fm Broadcaster to send your music and microphone audio to your stream.");

        ImGui.TextWrapped(
            "Install Caster.fm Broadcaster and sign in or enter the connection details provided by your Caster.fm stream.");

        ImGui.Dummy(
            new Vector2(
                0f,
                16f));

        DrawDjExternalButton(
            "Open Broadcaster Page",
            "https://www.caster.fm/free-cloud-stream-hosting/broadcaster-software/");
    }

    private void DrawDjSimpleStep3()
    {
        DrawDjGuideHeading(
            "3. Choose your audio",
            "Select what you want your Alpha Channel watch party to hear.");

        DrawDjGuideBullet(
            "Music",
            "Choose the audio source carrying your music.");

        DrawDjGuideBullet(
            "Microphone",
            "Enable your microphone if you want to talk to listeners.");

        DrawDjGuideBullet(
            "Levels",
            "Watch the audio meters and make sure they move while sound is playing.");

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));

        ImGui.TextWrapped(
            "Keep the levels below clipping. If the stream sounds too quiet or distorted, adjust the source levels in the broadcaster before changing Alpha Channel's playback volume.");
    }

    private void DrawDjSimpleStep4()
    {
        DrawDjGuideHeading(
            "4. Start broadcasting",
            "Once your stream is online, give its direct audio URL to Alpha Channel.");

        DrawDjGuideNumberedLine(
            "1",
            "Start broadcasting from Caster.fm Broadcaster.");

        DrawDjGuideNumberedLine(
            "2",
            "Find the direct listening or stream URL provided for your station.");

        DrawDjGuideNumberedLine(
            "3",
            "Copy that URL.");

        DrawDjGuideNumberedLine(
            "4",
            "Close this guide and paste it into the Music / DJ box.");

        DrawDjGuideNumberedLine(
            "5",
            "Press Play on TV to start it for your watch party.");
    }

    // ---------------------------------------------------------
    // Advanced guide
    // ---------------------------------------------------------

    private void DrawDjAdvancedGuide()
    {
        if (djAdvancedGuideOpen)
        {
            ImGui.OpenPopup(
                "DJ Setup Guide##djAdvancedGuide");

            djAdvancedGuideOpen = false;
        }

        ImGui.SetNextWindowSize(
            new Vector2(
                650f,
                540f),
            ImGuiCond.Appearing);

        var popupOpen = true;

        if (!ImGui.BeginPopupModal(
                "DJ Setup Guide##djAdvancedGuide",
                ref popupOpen,
                ImGuiWindowFlags.NoCollapse))
        {
            return;
        }

        DrawDjGuideShell(
            "Advanced / DJ Setup",
            "Broadcast a live DJ mix, playlist and microphone to your Alpha Channel watch party.",
            5,
            ref djAdvancedGuideStep,
            step =>
            {
                switch (step)
                {
                    case 0:
                        DrawDjAdvancedStep1();
                        break;

                    case 1:
                        DrawDjAdvancedStep2();
                        break;

                    case 2:
                        DrawDjAdvancedStep3();
                        break;

                    case 3:
                        DrawDjAdvancedStep4();
                        break;

                    case 4:
                        DrawDjAdvancedStep5();
                        break;
                }
            });

        ImGui.EndPopup();
    }

    private void DrawDjAdvancedStep1()
    {
        DrawDjGuideHeading(
            "1. Choose your stream host",
            "Your stream host provides the public audio URL that Alpha Channel plays.");

        ImGui.TextWrapped(
            "We recommend Caster.fm as an easy starting point.");

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));

        ImGui.TextWrapped(
            "You do not have to use Caster.fm. Other Icecast, Shoutcast or internet radio services can work as long as they provide a compatible direct audio stream URL.");

        ImGui.Dummy(
            new Vector2(
                0f,
                16f));

        DrawDjExternalButton(
            "Open Caster.fm",
            "https://www.caster.fm/");
    }

    private void DrawDjAdvancedStep2()
    {
        DrawDjGuideHeading(
            "2. Install your DJ software",
            "Mixxx is our recommended free option for live DJ sets.");

        ImGui.TextWrapped(
            "Mixxx gives you decks, playlists, mixing controls and live broadcasting support.");

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        ImGui.TextWrapped(
            "Other broadcasting software can also be used. BUTT is a lightweight option if your audio is already being mixed elsewhere.");

        ImGui.Dummy(
            new Vector2(
                0f,
                16f));

        DrawDjExternalButton(
            "Open Mixxx Website",
            "https://mixxx.org/");
    }

    private void DrawDjAdvancedStep3()
    {
        DrawDjGuideHeading(
            "3. Connect your DJ software",
            "Enter the broadcasting details supplied by your stream host.");

        DrawDjGuideBullet(
            "Server / Host",
            "The address supplied by your radio host.");

        DrawDjGuideBullet(
            "Port",
            "The broadcasting port supplied by your host.");

        DrawDjGuideBullet(
            "Mount",
            "Your Icecast mount point, when required.");

        DrawDjGuideBullet(
            "Username / Password",
            "The source credentials supplied by your host.");

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));

        ImGui.TextWrapped(
            "In Mixxx, these settings are configured in the live broadcasting section.");
    }

    private void DrawDjAdvancedStep4()
    {
        DrawDjGuideHeading(
            "4. Configure your audio",
            "Use a broadly compatible stream format and sensible bitrate.");

        DrawDjGuideSetting(
            "Format",
            "MP3");

        DrawDjGuideSetting(
            "Bitrate",
            "128 - 160 Kbps");

        DrawDjGuideSetting(
            "Channels",
            "Stereo");

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        ImGui.TextWrapped(
            "Your hosting provider may impose its own bitrate limit. If so, use the highest compatible setting allowed by that service.");

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));

        ImGui.TextWrapped(
            "Before going live, check that your music and microphone levels are balanced and are not clipping.");
    }

    private void DrawDjAdvancedStep5()
    {
        DrawDjGuideHeading(
            "5. Broadcast to Alpha Channel",
            "Start your DJ broadcast, then give Alpha Channel the direct listening URL.");

        DrawDjGuideNumberedLine(
            "1",
            "Start live broadcasting in Mixxx or your chosen broadcasting software.");

        DrawDjGuideNumberedLine(
            "2",
            "Confirm your radio host shows the stream as online.");

        DrawDjGuideNumberedLine(
            "3",
            "Copy the direct stream or listening URL from your radio host.");

        DrawDjGuideNumberedLine(
            "4",
            "Close this guide and paste the URL into Music / DJ.");

        DrawDjGuideNumberedLine(
            "5",
            "Press Play on TV.");

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));

        ImGui.TextColored(
            Gold,
            "Important");

        ImGui.TextWrapped(
            "A station webpage is not necessarily the audio stream itself. Alpha Channel needs the direct stream URL that an audio player can open.");
    }

    // ---------------------------------------------------------
    // Shared guide shell
    // ---------------------------------------------------------

    private void DrawDjGuideShell(
        string title,
        string subtitle,
        int stepCount,
        ref int currentStep,
        Action<int> drawStep)
    {
        ImGui.SetWindowFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            title);

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                3f));

        ImGui.SetWindowFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            subtitle);

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float spacing = 8f;

        var stepWidth =
            (availableWidth -
             spacing * (stepCount - 1)) /
            stepCount;

        for (var i = 0;
             i < stepCount;
             i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(
                    0f,
                    spacing);
            }

            var selected =
                currentStep == i;

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                selected
                    ? Accent
                    : new Vector4(
                        0.055f,
                        0.07f,
                        0.115f,
                        1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    selected
                        ? AccentHover
                        : new Vector4(
                            0.075f,
                            0.095f,
                            0.15f,
                            1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentActive))
            {
                if (ImGui.Button(
                    $"{i + 1}##djGuideStep_{title}_{i}",
                    new Vector2(
                        stepWidth,
                        36f)))
                {
                    currentStep = i;
                }
            }
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));

        ImGui.Separator();

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));

        const float footerHeight =
            58f;

        // Only the actual guide content scrolls.
        // Navigation stays fixed at the bottom.
        using (ImRaii.PushStyle(
            ImGuiStyleVar.WindowPadding,
            new Vector2(
                16f,
                14f)))
        using (var content =
            ImRaii.Child(
                $"##djGuideContent_{title}",
                new Vector2(
                    -1f,
                    -footerHeight),
                false,
                ImGuiWindowFlags.None))
        {
            if (content)
            {
                drawStep(
                    currentStep);

                ImGui.Dummy(
                    new Vector2(
                        0f,
                        6f));
            }
        }

        ImGui.Separator();

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));

        if (currentStep > 0)
        {
            if (ImGui.Button(
                $"Back##{title}",
                new Vector2(
                    90f,
                    32f)))
            {
                currentStep--;
            }
        }
        else
        {
            ImGui.Dummy(
                new Vector2(
                    90f,
                    32f));
        }

        ImGui.SameLine();

        const float rightButtonWidth =
            100f;

        ImGui.SetCursorPosX(
            ImGui.GetWindowWidth() -
            rightButtonWidth -
            16f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            7f))
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
            if (currentStep <
                stepCount - 1)
            {
                if (ImGui.Button(
                    $"Next##{title}",
                    new Vector2(
                        rightButtonWidth,
                        32f)))
                {
                    currentStep++;
                }
            }
            else
            {
                if (ImGui.Button(
                    $"Done##{title}",
                    new Vector2(
                        rightButtonWidth,
                        32f)))
                {
                    ImGui.CloseCurrentPopup();
                }
            }
        }
    }

    // ---------------------------------------------------------
    // Shared guide helpers
    // ---------------------------------------------------------

    private void DrawDjGuideHeading(
        string title,
        string subtitle)
    {
        ImGui.SetWindowFontScale(
            1.08f);

        ImGui.TextColored(
            Vector4.One,
            title);

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                4f));

        ImGui.SetWindowFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            subtitle);

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                16f));
    }

    private void DrawDjGuideBullet(
        string title,
        string description)
    {
        ImGui.TextColored(
            Accent,
            "•");

        ImGui.SameLine();

        ImGui.TextColored(
            Vector4.One,
            title);

        ImGui.SameLine();

        ImGui.TextColored(
            MutedText,
            $"— {description}");

        ImGui.Dummy(
            new Vector2(
                0f,
                6f));
    }

    private void DrawDjGuideSetting(
        string name,
        string value)
    {
        var startX =
            ImGui.GetCursorPosX();

        ImGui.TextColored(
            MutedText,
            name);

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            startX + 180f);

        ImGui.TextColored(
            Vector4.One,
            value);

        ImGui.Dummy(
            new Vector2(
                0f,
                6f));
    }

    private void DrawDjGuideNumberedLine(
        string number,
        string text)
    {
        ImGui.TextColored(
            Accent,
            number + ".");

        ImGui.SameLine(
            0f,
            8f);

        ImGui.TextWrapped(
            text);

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));
    }

    private void DrawDjExternalButton(
        string label,
        string url)
    {
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
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
            var buttonPos =
                ImGui.GetCursorScreenPos();

            var buttonSize =
                new Vector2(
                    190f,
                    36f);

            if (ImGui.Button(
                $"##djExternal_{label}",
                buttonSize))
            {
                Dalamud.Utility.Util.OpenLink(
                    url);
            }

            DrawPlayerActionButtonContent(
                buttonPos,
                buttonSize,
                FontAwesomeIcon.ExternalLinkAlt,
                label,
                Vector4.One);
        }
    }
}