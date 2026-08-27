using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string djStreamUrl = string.Empty;

    private bool djSimpleGuideOpen;
    private bool djAdvancedGuideOpen;

    private int djSimpleGuideStep;
    private int djAdvancedGuideStep;

    // ---------------------------------------------------------
    // Music / DJ page
    // ---------------------------------------------------------

    private void DrawDJLive()
    {
        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Music / DJ");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(0f, 4f));

        ImGui.SetWindowFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "Play music or broadcast a live DJ set to your Alpha Channel watch party.");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(0f, 18f));

        // -----------------------------------------------------
        // Direct stream
        // -----------------------------------------------------

        DrawDjStreamBox();

        ImGui.Dummy(
            new Vector2(0f, 24f));

        DrawDjDivider();

        ImGui.Dummy(
            new Vector2(0f, 24f));

        // -----------------------------------------------------
        // Setup choices
        // -----------------------------------------------------

        ImGui.SetWindowFontScale(1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Need help getting your music online?");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(0f, 4f));

        ImGui.SetWindowFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "Choose the setup that best matches how you want to broadcast.");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(0f, 14f));

        DrawDjSetupCards();

        DrawDjSimpleGuide();
        DrawDjAdvancedGuide();
    }

    // ---------------------------------------------------------
    // Direct URL box
    // ---------------------------------------------------------

    private void DrawDjStreamBox()
    {
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.045f,
                0.075f,
                1f)))
        using (var box =
            ImRaii.Child(
                "##djStreamBox",
                new Vector2(
                    -1f,
                    185f),
                false,
                ImGuiWindowFlags.NoScrollbar))
        {
            if (!box)
            {
                return;
            }

            ImGui.SetCursorPos(
                new Vector2(
                    16f,
                    14f));

            ImGui.SetWindowFontScale(1.02f);

            ImGui.TextColored(
                Vector4.One,
                "Have an MP3 or live audio stream link?");

            ImGui.SetWindowFontScale(1f);

            ImGui.SetCursorPosX(16f);

            ImGui.SetWindowFontScale(0.80f);

            ImGui.TextColored(
                MutedText,
                "Paste a direct audio URL below to start playing it on your TV.");

            ImGui.SetWindowFontScale(1f);

            ImGui.Dummy(
                new Vector2(0f, 10f));

            ImGui.SetCursorPosX(16f);

            var inputWidth =
                ImGui.GetContentRegionAvail().X -
                82f;

            ImGui.SetNextItemWidth(
                MathF.Max(
                    120f,
                    inputWidth));

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f)
                .Push(
                    ImGuiStyleVar.FramePadding,
                    new Vector2(
                        12f,
                        9f)))
            using (ImRaii.PushColor(
                ImGuiCol.FrameBg,
                new Vector4(
                    0.045f,
                    0.06f,
                    0.105f,
                    1f))
                .Push(
                    ImGuiCol.FrameBgHovered,
                    new Vector4(
                        0.065f,
                        0.085f,
                        0.14f,
                        1f))
                .Push(
                    ImGuiCol.FrameBgActive,
                    new Vector4(
                        0.065f,
                        0.085f,
                        0.14f,
                        1f)))
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

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f)
                .Push(
                    ImGuiStyleVar.FramePadding,
                    new Vector2(
                        12f,
                        9f)))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(
                    0.055f,
                    0.07f,
                    0.115f,
                    1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(
                        0.075f,
                        0.095f,
                        0.15f,
                        1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(
                        0.075f,
                        0.095f,
                        0.15f,
                        1f)))
            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                if (ImGui.Button(
                    FontAwesomeIcon.Clipboard
                        .ToIconString()))
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
                new Vector2(0f, 10f));

            ImGui.SetCursorPosX(16f);

            using (ImRaii.Disabled(
                string.IsNullOrWhiteSpace(
                    djStreamUrl)))
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
                        170f,
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
        }
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

        queue.PlayNow(
            new Video.VideoQueueEntry(
                url,
                "Live music stream",
                "Music / DJ",
                null,
                null));

        djStreamUrl =
            string.Empty;

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

        const float gap = 12f;

        var cardWidth =
            (availableWidth - gap) /
            2f;

        DrawDjSetupCard(
            "##simpleDjSetup",
            cardWidth,
            FontAwesomeIcon.Music,
            "Simple",
            "Play music or talk on mic",
            "Use Caster.fm's broadcaster to get your audio online without needing DJ software.",
            "Simple Setup Guide",
            () =>
            {
                djSimpleGuideStep = 0;
                djSimpleGuideOpen = true;
            });

        ImGui.SameLine(
            0f,
            gap);

        DrawDjSetupCard(
            "##advancedDjSetup",
            cardWidth,
            FontAwesomeIcon.Headphones,
            "Advanced / DJ",
            "Mix, DJ and broadcast live",
            "Use Mixxx with an internet radio host for playlists, decks, transitions and microphone control.",
            "DJ Setup Guide",
            () =>
            {
                djAdvancedGuideStep = 0;
                djAdvancedGuideOpen = true;
            });
    }

    private void DrawDjSetupCard(
        string id,
        float width,
        FontAwesomeIcon icon,
        string title,
        string subtitle,
        string description,
        string buttonLabel,
        Action onClick)
    {
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.045f,
                0.075f,
                1f)))
        using (var card =
            ImRaii.Child(
                id,
                new Vector2(
                    width,
                    205f),
                false,
                ImGuiWindowFlags.NoScrollbar))
        {
            if (!card)
            {
                return;
            }

            ImGui.SetCursorPos(
                new Vector2(
                    16f,
                    16f));

            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    AccentHover,
                    icon.ToIconString());
            }

            ImGui.SameLine(
                0f,
                9f);

            ImGui.SetWindowFontScale(
                1.05f);

            ImGui.TextColored(
                Vector4.One,
                title);

            ImGui.SetWindowFontScale(
                1f);

            ImGui.SetCursorPosX(
                16f);

            ImGui.SetWindowFontScale(
                0.82f);

            ImGui.TextColored(
                Gold,
                subtitle);

            ImGui.SetWindowFontScale(
                1f);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    8f));

            ImGui.SetCursorPosX(
                16f);

            ImGui.PushTextWrapPos(
                width - 16f);

            ImGui.SetWindowFontScale(
                0.82f);

            ImGui.TextColored(
                MutedText,
                description);

            ImGui.SetWindowFontScale(
                1f);

            ImGui.PopTextWrapPos();

            ImGui.SetCursorPos(
                new Vector2(
                    16f,
                    151f));

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
                        MathF.Min(
                            180f,
                            width - 32f),
                        36f);

                if (ImGui.Button(
                    id + "_button",
                    buttonSize))
                {
                    onClick();
                }

                DrawPlayerActionButtonContent(
                    buttonPos,
                    buttonSize,
                    FontAwesomeIcon.BookOpen,
                    buttonLabel,
                    Vector4.One);
            }
        }
    }

    private void DrawDjDivider()
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        var text =
            "OR";

        var textSize =
            ImGui.CalcTextSize(
                text);

        var middle =
            origin.X +
            width * 0.5f;

        var lineColor =
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.20f));

        drawList.AddLine(
            new Vector2(
                origin.X,
                origin.Y + 7f),
            new Vector2(
                middle - 28f,
                origin.Y + 7f),
            lineColor);

        drawList.AddLine(
            new Vector2(
                middle + 28f,
                origin.Y + 7f),
            new Vector2(
                origin.X + width,
                origin.Y + 7f),
            lineColor);

        drawList.AddText(
            new Vector2(
                middle -
                textSize.X * 0.5f,
                origin.Y),
            ImGui.GetColorU32(
                MutedText),
            text);

        ImGui.Dummy(
            new Vector2(
                width,
                14f));
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