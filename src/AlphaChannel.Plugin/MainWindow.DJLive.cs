using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string djStreamUrl = string.Empty;

    private bool djCredentialsLoading;
    private bool djPasswordVisible;
    private bool djPatreonGateVisible;
    private bool patreonAccessConfirmed;
    private string? djPatreonAccessMessage;

    private bool djConnectionCheckInProgress;
    private bool djConnectionSearchActive;
    private bool djConnectionMonitoringEnabled;

    private bool djIsOnAir;
    private DateTime? djOnAirSinceUtc;

    private DateTime djConnectionSearchEndsUtc =
        DateTime.MinValue;

    private DateTime djNextConnectionCheckUtc =
        DateTime.MaxValue;



    private bool djConnectionGuideOpen;
    private int djConnectionGuideTab;

    private bool djAddStationPopupOpen;
    private int? djEditingStationIndex;
    private string? djStationEditorError;


    //
    // Radio UI
    //

    private string djStationNameInput = string.Empty;
    private string djStationUrlInput = string.Empty;

    private List<SavedRadioStationRecord> djSavedStations =>
     Plugin.Cfg.SavedRadioStations;

    //
    // Temporary legacy-guide state. These can be removed when the obsolete
    // guide methods themselves are deleted.
    //

    private bool djSimpleGuideOpen;
    private bool djAdvancedGuideOpen;

    private int djSimpleGuideStep;
    private int djAdvancedGuideStep;

    private enum PendingWatchPartyMediaKind
    {
        None,
        DjBroadcast,
        LiveStreamBroadcast,
        LocalVideoStartAndBroadcast,
        LocalVideoCurrentPlaybackBroadcast,
        GameRoom,
        WebLink,
        RadioStation,
    }

    private PendingWatchPartyMediaKind pendingWatchPartyMediaKind;
    private Video.VideoQueueEntry? pendingWatchPartyVideoEntry;
    private string? pendingWatchPartyRadioUrl;
    private string? pendingWatchPartyRadioTitle;
    private bool watchPartyCreationPopupOpen;
    private bool djBroadcastingToWatchParty;
    private bool djAutoMuteNoticeVisible;
    private string? djAutoMutedStreamUrl;


    // ---------------------------------------------------------
    // Music / DJ page
    // ---------------------------------------------------------

    private void DrawDJLive()
    {
        UpdateDjConnectionStatus();

        if (djBroadcastingToWatchParty &&
    stream.Mode != StreamMode.Hosting)
        {
            djBroadcastingToWatchParty =
                false;
        }

        DrawDjLiveHeading();

        ImGui.Dummy(
            UiVec(0f, 14f));

        DrawDjBroadcastCard();

        ImGui.Dummy(
            UiVec(0f, 18f));

        DrawDjOrDivider();

        ImGui.Dummy(
            UiVec(0f, 18f));

        DrawDjRadioSection();
    }


    // ---------------------------------------------------------
    // Main Music / Radio dashboard
    // ---------------------------------------------------------

    private void DrawDjMediaDashboard()
    {
        DrawDjBroadcastCard();

        ImGui.Dummy(
            UiVec(0f, 18f));

        DrawDjOrDivider();

        ImGui.Dummy(
            UiVec(0f, 18f));

        DrawDjRadioSection();

    }


    private void DrawDjLiveHeading()
    {
        var origin =
            ImGui.GetCursorScreenPos();

        SetUiFontScale(
            1.35f);

        ImGui.TextColored(
            Vector4.One,
            "DJ Live");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 2f));

        SetUiFontScale(
            0.84f);

        ImGui.TextColored(
            MutedText,
            "Broadcast live audio using Icecast-compatible DJ software.");

        SetUiFontScale(
            1f);

        DrawDjOnAirBadge(
            origin);
    }

    private void DrawDjOnAirBadge(
        Vector2 headingOrigin)
    {
        var label =
            djConnectionSearchActive
                ? "CHECKING"
                : djIsOnAir
                    ? djOnAirSinceUtc is { } started
                        ? $"ON AIR · {FormatDjOnAirDuration(DateTime.UtcNow - started)}"
                        : "ON AIR"
                    : "OFF AIR";

        var color =
            djConnectionSearchActive
                ? Gold
                : djIsOnAir
                    ? new Vector4(
                        0.94f,
                        0.16f,
                        0.20f,
                        1f)
                    : MutedText;

        var textSize =
            ImGui.CalcTextSize(
                label);

        var badgeSize =
            new Vector2(
                textSize.X + Ui(40f),
                Ui(38f));

        var badgeOrigin =
            new Vector2(
                ImGui.GetWindowPos().X +
                ImGui.GetWindowWidth() -
                badgeSize.X -
                Ui(18f),
                headingOrigin.Y);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            badgeOrigin,
            badgeOrigin + badgeSize,
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    djIsOnAir
                        ? 0.25f
                        : 0.11f)),
            10f);

        drawList.AddRect(
            badgeOrigin,
            badgeOrigin + badgeSize,
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    0.65f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        drawList.AddCircleFilled(
            new Vector2(
                badgeOrigin.X + Ui(17f),
                badgeOrigin.Y +
                badgeSize.Y * 0.5f),
            6f,
            ImGui.GetColorU32(
                color),
            20);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                badgeOrigin.X + Ui(29f),
                badgeOrigin.Y +
                (badgeSize.Y - textSize.Y) * 0.5f),
            ImGui.GetColorU32(
                djIsOnAir
                    ? Vector4.One
                    : color),
            label);
    }

    private void DrawDjBroadcastCard()
    {
        var cardHeight =
            djPatreonGateVisible &&
            !patreonAccessConfirmed
                ? 550f
                : radioCredentials is null
                    ? radioError is { Length: > 0 }
                        ? 175f
                        : 150f
                    : 450f;

        using var cardStyle =
            ImRaii.PushStyle(
                    ImGuiStyleVar.ChildRounding,
                    12f)
                .Push(
                    ImGuiStyleVar.ChildBorderSize,
                    1f)
                .Push(
                    ImGuiStyleVar.WindowPadding,
                    UiVec(20f, 18f));

        using var cardColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    CardBg)
                .Push(
                    ImGuiCol.Border,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.38f));

        if (ImGui.BeginChild(
                "##djBroadcastCard",
                new Vector2(
                    -1f,
                    cardHeight),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            DrawDjBroadcastCardHeading();

            ImGui.Dummy(
                UiVec(0f, 14f));

            if (djPatreonGateVisible &&
                !patreonAccessConfirmed)
            {
                DrawDjPatreonLockedState();
            }
            else if (radioCredentials is null)
            {
                DrawDjCredentialsEmptyState();
            }
            else
            {
                DrawDjCredentials(
                    radioCredentials);
            }
        }

        ImGui.EndChild();
    }

    private void DrawDjBroadcastCardHeading()
    {
        var icon =
            FontAwesomeIcon.BroadcastTower
                .ToIconString();

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            SetUiFontScale(
                1.35f);

            ImGui.TextColored(
                Accent,
                icon);

            SetUiFontScale(
                1f);
        }

        ImGui.SameLine(
            0f,
            12f);

        ImGui.BeginGroup();

        SetUiFontScale(
            1.08f);

        ImGui.TextColored(
            Vector4.One,
            "Broadcast connection");

        if (HasConfirmedPatreonAccess())
        {
            DrawPatreonFeatureTag();
        }

        SetUiFontScale(
            1f);

        ImGui.TextColored(
            MutedText,
            "Use these settings in any Icecast compatible DJ software to broadcast live to your Alpha Channel Watch Party");

        ImGui.EndGroup();
    }

    private void DrawDjCredentialsEmptyState()
    {
        const string prompt =
            "Get the broadcast connection details before connecting your DJ software.";

        var rowOrigin =
            ImGui.GetCursorScreenPos();

        var gap =
            Ui(20f);

        var columnWidth =
            (ImGui.GetContentRegionAvail().X - gap) * 0.5f;

        var buttonSize =
            new Vector2(
                columnWidth,
                Ui(40f));

        var textSize =
            ImGui.CalcTextSize(
                prompt,
                false,
                columnWidth);

        var rowHeight =
            MathF.Max(
                buttonSize.Y,
                textSize.Y);

        ImGui.SetCursorScreenPos(
            rowOrigin +
            new Vector2(
                0f,
                (rowHeight - textSize.Y) * 0.5f));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPos().X +
            columnWidth);

        ImGui.TextColored(
            MutedText,
            prompt);

        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(
            rowOrigin +
            new Vector2(
                columnWidth + gap,
                (rowHeight - buttonSize.Y) * 0.5f));

        using (ImRaii.Disabled(
                   djCredentialsLoading))
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
            var buttonOrigin =
                ImGui.GetCursorScreenPos();

            if (ImGui.Button(
                    "##getDjCredentials",
                    buttonSize))
            {
                RequestDjConnectionDetails();
            }

            DrawPlayerActionButtonContent(
                buttonOrigin,
                buttonSize,
                FontAwesomeIcon.Key,
                djCredentialsLoading
                    ? "Getting details..."
                    : "Get connection details",
                Vector4.One);
        }

        ImGui.SetCursorScreenPos(
            rowOrigin +
            new Vector2(
                0f,
                rowHeight));

        if (radioError is { Length: > 0 } error)
        {
            ImGui.Dummy(
                UiVec(0f, 9f));

            ImGui.TextColored(
                Danger,
                error);
        }
    }

    private static bool HasConfiguredPatreonAccess()
    {
        return Plugin.Cfg.PatreonMember &&
               Plugin.Cfg.PatreonMembershipTier is > 0;
    }

    private void RequestDjConnectionDetails()
    {
        if (patreonAccessConfirmed &&
            HasConfiguredPatreonAccess())
        {
            IssueAlphaChannelRadio();
            return;
        }

        patreonAccessConfirmed = false;
        djPatreonGateVisible = true;
        djPatreonAccessMessage = null;
        radioError = null;
    }

    private void RefreshDjPatreonAccess()
    {
        if (!HasConfiguredPatreonAccess())
        {
            patreonAccessConfirmed = false;
            djPatreonAccessMessage =
                "No active Patreon membership was found.";
            return;
        }

        patreonAccessConfirmed = true;
        djPatreonGateVisible = false;
        djPatreonAccessMessage = null;
        radioError = null;

        Plugin.ChatGui.Print(
            $"[AlphaChannel] Patreon access confirmed (tier {Plugin.Cfg.PatreonMembershipTier}).");

        IssueAlphaChannelRadio();
    }

    internal void NotifyLocalPatreonMembershipChanged()
    {
        var hadConnectionDetails =
            radioCredentials is not null;

        patreonAccessConfirmed = false;
        djPatreonGateVisible =
            djPatreonGateVisible ||
            hadConnectionDetails;
        djPatreonAccessMessage = null;
        radioCredentials = null;
        djPasswordVisible = false;
        djStreamUrl = string.Empty;
        djConnectionSearchActive = false;
        djConnectionMonitoringEnabled = false;
        djConnectionCheckInProgress = false;
        djIsOnAir = false;
        djOnAirSinceUtc = null;
        djNextConnectionCheckUtc = DateTime.MaxValue;

        localVideoPatreonAccessMessage = null;
        livePatreonAccessMessage = null;
        gamePatreonAccessMessage = null;
        browserPatreonAccessMessage = null;
        obsConnectionChecking = false;
        obsConnectionOnline = false;
        obsConnectionError = null;

        if (!HasConfiguredPatreonAccess() &&
            localVideoBroadcastArmed)
        {
            StopLocalVideoWatchPartyBroadcast();
        }

        if (!HasConfiguredPatreonAccess() &&
            gameBroadcastArmed)
        {
            StopGameWatchPartyBroadcast();
        }

        if (!HasConfiguredPatreonAccess() &&
            browserBroadcastArmed)
        {
            StopBrowserWatchPartyBroadcast();
        }
    }

    private void DrawDjPatreonLockedState()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   10f)
                   .Push(
                       ImGuiStyleVar.ChildBorderSize,
                       1f)
                   .Push(
                       ImGuiStyleVar.WindowPadding,
                       UiVec(18f, 12f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       0.075f,
                       0.06f,
                       0.12f,
                       1f))
                   .Push(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.82f)))
        {
            if (ImGui.BeginChild(
                    "##djPatreonGate",
                    UiVec(-1f, 285f),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
            {
                var gateMin =
                    ImGui.GetWindowPos();

                var gateMax =
                    gateMin +
                    ImGui.GetWindowSize();

                var drawList =
                    ImGui.GetWindowDrawList();

                drawList.AddRectFilled(
                    gateMin,
                    gateMax,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.10f)),
                    10f);

                drawList.AddCircleFilled(
                    new Vector2(
                        gateMax.X -
                        ImGui.GetWindowWidth() * 0.13f,
                        (gateMin.Y + gateMax.Y) * 0.5f),
                    ImGui.GetWindowSize().Y * 0.72f,
                    ImGui.GetColorU32(
                        new Vector4(
                            PatreonOrange.X,
                            PatreonOrange.Y,
                            PatreonOrange.Z,
                            0.075f)),
                    64);

                var gateMiddleX =
                    (gateMin.X + gateMax.X) * 0.5f;

                var orangeBorder =
                    ImGui.GetColorU32(
                        new Vector4(
                            PatreonOrange.X,
                            PatreonOrange.Y,
                            PatreonOrange.Z,
                            0.82f));

                drawList.AddLine(
                    new Vector2(
                        gateMiddleX,
                        gateMin.Y),
                    new Vector2(
                        gateMax.X -
                        Ui(10f),
                        gateMin.Y),
                    orangeBorder);

                drawList.AddLine(
                    new Vector2(
                        gateMax.X,
                        gateMin.Y +
                        Ui(10f)),
                    new Vector2(
                        gateMax.X,
                        gateMax.Y -
                        Ui(10f)),
                    orangeBorder);

                drawList.AddLine(
                    new Vector2(
                        gateMax.X -
                        Ui(10f),
                        gateMax.Y),
                    new Vector2(
                        gateMiddleX,
                        gateMax.Y),
                    orangeBorder);

                var baseFontSize =
                    ImGui.GetFontSize();

                Vector2 heartOrigin;
                Vector2 heartSize;
                Vector2 lockSize;
                var lockIcon =
                    FontAwesomeIcon.Lock.ToIconString();

                using (ImRaii.PushFont(
                           UiBuilder.IconFont))
                {
                    SetUiFontScale(
                        2.05f);

                    var heart =
                        FontAwesomeIcon.Heart.ToIconString();

                    heartSize =
                        ImGui.CalcTextSize(
                            heart);

                    ImGui.SetCursorPosX(
                        MathF.Max(
                            ImGui.GetCursorPosX(),
                            (ImGui.GetWindowWidth() -
                             heartSize.X) *
                            0.5f));

                    heartOrigin =
                        ImGui.GetCursorScreenPos();

                    ImGui.TextColored(
                        PatreonOrange,
                        heart);

                    SetUiFontScale(
                        0.62f);

                    lockSize =
                        ImGui.CalcTextSize(
                            lockIcon);

                    SetUiFontScale(
                        1f);
                }

                drawList.AddText(
                    UiBuilder.IconFont,
                    baseFontSize * 0.62f,
                    heartOrigin +
                    (heartSize - lockSize) *
                    0.5f,
                    ImGui.GetColorU32(
                        new Vector4(
                            0.055f,
                            0.06f,
                            0.09f,
                            1f)),
                    lockIcon);

                DrawPatreonCenteredText(
                    "PATREON FEATURE",
                    PatreonOrange,
                    0.92f);

                DrawPatreonCenteredText(
                    "Unlock DJ Live",
                    Vector4.One,
                    1.65f);

                DrawPatreonCenteredText(
                    "Join our Patreon to access your private DJ connection details",
                    MutedText,
                    1.02f);

                DrawPatreonCenteredText(
                    "and broadcast live audio to your Watch Party.",
                    MutedText,
                    1.02f);

                ImGui.Dummy(
                    UiVec(0f, 5f));

                var unlockSize =
                    UiVec(276f, 46f);

                ImGui.SetCursorPosX(
                    (ImGui.GetWindowWidth() -
                     unlockSize.X) *
                    0.5f);

                DrawDjActionButton(
                    "##unlockDjWithPatreon",
                    FontAwesomeIcon.LockOpen,
                    "Unlock with Patreon",
                    unlockSize,
                    false,
                    () =>
                    {
                        patreonPopupOpen = true;
                    },
                    true);

                var refreshSize =
                    UiVec(270f, 27f);

                ImGui.SetCursorPosX(
                    (ImGui.GetWindowWidth() -
                     refreshSize.X) *
                    0.5f);

                using (ImRaii.PushColor(
                           ImGuiCol.Button,
                           Vector4.Zero)
                           .Push(
                               ImGuiCol.ButtonHovered,
                               new Vector4(
                                   Accent.X,
                                   Accent.Y,
                                   Accent.Z,
                                   0.14f))
                           .Push(
                               ImGuiCol.ButtonActive,
                               new Vector4(
                                   Accent.X,
                                   Accent.Y,
                                   Accent.Z,
                                   0.24f))
                           .Push(
                               ImGuiCol.Text,
                               MutedText))
                {
                    if (ImGui.Button(
                            "Already a member? Refresh access##refreshDjPatreon",
                            refreshSize))
                    {
                        RefreshDjPatreonAccess();
                    }
                }

                if (djPatreonAccessMessage is { } accessMessage)
                {
                    DrawPatreonCenteredText(
                        accessMessage,
                        Danger,
                        0.82f);
                }
            }

            ImGui.EndChild();
        }

        ImGui.Dummy(
            UiVec(0f, 8f));

        const float gap =
            12f;

        var recommendationWidth =
            (availableWidth - gap) *
            0.5f;

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() +
            availableWidth -
            recommendationWidth);

        DrawDjRecommendationField(
            recommendationWidth);

        ImGui.Dummy(
            UiVec(0f, 10f));

        DrawDjPatreonLockedActions(
            availableWidth);
    }

    private void DrawDjPatreonLockedActions(
        float availableWidth)
    {
        const float gap = 9f;
        var smallWidth = Ui(170f);
        var actionHeight = Ui(38f);

        DrawDjActionButton(
            "##copyLockedDjSettings",
            FontAwesomeIcon.Clipboard,
            "Copy all settings",
            new Vector2(
                smallWidth,
                actionHeight),
            true,
            () => { },
            false,
            MutedText);

        if (ImGui.IsItemHovered(
                ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "A Patreon membership is required to access DJ connection details.");
        }

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##checkLockedDjConnection",
            FontAwesomeIcon.BroadcastTower,
            "Check connection",
            new Vector2(
                smallWidth,
                actionHeight),
            false,
            () =>
            {
                djPatreonAccessMessage =
                    "Unlock DJ Live before checking your connection.";
            });

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##openLockedDjGuide",
            FontAwesomeIcon.BookOpen,
            "Connection guide",
            new Vector2(
                smallWidth,
                actionHeight),
            false,
            () =>
            {
                djConnectionGuideOpen = true;
            });

        var primaryWidth =
            Math.Max(
                245f,
                availableWidth -
                smallWidth * 3f -
                gap * 3f);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##broadcastLockedDjToWatchParty",
            FontAwesomeIcon.Play,
            "Broadcast DJ Stream to Watch Party",
            new Vector2(
                primaryWidth,
                actionHeight),
            true,
            () => { },
            true,
            MutedText);

        ImGui.Dummy(
            UiVec(0f, 12f));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            availableWidth);

        ImGui.TextColored(
            MutedText,
            "After connecting to our Icecast server, press check connection. Once you see 'ON AIR' you'll then be able to stream your DJ broadcast to your watch party.");

        ImGui.PopTextWrapPos();
    }

    private void DrawDjCredentials(
        RadioCredentialsDto radio)
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float gap =
            12f;

        var columnWidth =
            (availableWidth - gap) *
            0.5f;

        DrawDjCredentialField(
            "##djServerCredential",
            "Server",
            radio.SourceHost,
            columnWidth);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjCredentialField(
            "##djPortCredential",
            "Port",
            radio.SourcePort.ToString(),
            columnWidth);

        ImGui.Dummy(
            UiVec(0f, 8f));

        DrawDjCredentialField(
            "##djUsernameCredential",
            "Username",
            radio.SourceUser,
            columnWidth);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjCredentialField(
            "##djMountCredential",
            "Mount",
            radio.Mount,
            columnWidth);

        ImGui.Dummy(
            UiVec(0f, 8f));

        DrawDjPasswordField(
            radio.SourcePassword ?? string.Empty,
            columnWidth);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjRecommendationField(
            columnWidth);

        ImGui.Dummy(
            UiVec(0f, 12f));

        DrawDjCredentialActions(
            radio,
            availableWidth);
    }

    private static void DrawDjCredentialField(
        string id,
        string label,
        string value,
        float width)
    {
        const float height =
            48f;

        using var fieldStyle =
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(12f, 8f));

        using var fieldColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    FrameBg)
                .Push(
                    ImGuiCol.Border,
                    BorderSubtle);

        if (ImGui.BeginChild(
                id,
                new Vector2(
                    width,
                    height),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(
                MutedText,
                label);

            ImGui.SameLine(
                0f,
                16f);

            ImGui.TextColored(
                Vector4.One,
                value);

            var copySize =
                UiVec(30f, 28f);

            ImGui.SameLine();

            ImGui.SetCursorPosX(
                ImGui.GetWindowWidth() -
                copySize.X -
                8f);

            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       Vector4.Zero)
                       .Push(
                           ImGuiCol.ButtonHovered,
                           FrameBgHover)
                       .Push(
                           ImGuiCol.ButtonActive,
                           AccentActive))
            {
                if (ImGui.Button(
                        $"{FontAwesomeIcon.Clipboard.ToIconString()}##copy{id}",
                        copySize))
                {
                    ImGui.SetClipboardText(
                        value);
                }
            }
        }

        ImGui.EndChild();
    }

    private void DrawDjPasswordField(
        string password,
        float width)
    {
        const float height =
            48f;

        using var fieldStyle =
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(12f, 8f));

        using var fieldColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    FrameBg)
                .Push(
                    ImGuiCol.Border,
                    BorderSubtle);

        if (ImGui.BeginChild(
                "##djPasswordCredential",
                new Vector2(
                    width,
                    height),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            var displayedPassword =
                djPasswordVisible
                    ? password
                    : new string(
                        '•',
                        Math.Clamp(
                            password.Length,
                            8,
                            16));

            ImGui.TextColored(
                MutedText,
                "Password");

            ImGui.SameLine(
                0f,
                16f);

            ImGui.TextColored(
                Vector4.One,
                displayedPassword);

            var buttonSize =
                UiVec(30f, 28f);

            ImGui.SameLine();

            ImGui.SetCursorPosX(
                ImGui.GetWindowWidth() -
                buttonSize.X * 2f -
                12f);

            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       Vector4.Zero)
                       .Push(
                           ImGuiCol.ButtonHovered,
                           FrameBgHover)
                       .Push(
                           ImGuiCol.ButtonActive,
                           AccentActive))
            {
                if (ImGui.Button(
                        $"{(djPasswordVisible ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye).ToIconString()}##showDjPassword",
                        buttonSize))
                {
                    djPasswordVisible =
                        !djPasswordVisible;
                }

                ImGui.SameLine(
                    0f,
                    2f);

                if (ImGui.Button(
                        $"{FontAwesomeIcon.Clipboard.ToIconString()}##copyDjPassword",
                        buttonSize))
                {
                    ImGui.SetClipboardText(
                        password);
                }
            }
        }

        ImGui.EndChild();
    }

    private static void DrawDjRecommendationField(
        float width)
    {
        const float height =
            48f;

        using var fieldStyle =
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(12f, 8f));

        using var fieldColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    new Vector4(
                        Accent.X * 0.10f,
                        Accent.Y * 0.10f,
                        Accent.Z * 0.16f,
                        0.95f))
                .Push(
                    ImGuiCol.Border,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.32f));

        if (ImGui.BeginChild(
                "##djRecommendations",
                new Vector2(
                    width,
                    height),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(
                AccentHover,
                "Recommended");

            ImGui.SameLine(
                0f,
                12f);

            ImGui.TextColored(
                MutedText,
                "MP3, OGG or AAC  •  192 kbps  •  Stereo  •  44.1/48 kHz");
        }

        ImGui.EndChild();
    }

    private void DrawDjCredentialActions(
     RadioCredentialsDto radio,
     float availableWidth)
    {
        const float gap = 9f;
        var smallWidth = Ui(170f);
        var actionHeight = Ui(38f);

        var viewingAnotherParty =
            stream.Mode == StreamMode.Viewing;

        DrawDjActionButton(
            "##copyAllDjSettings",
            FontAwesomeIcon.Clipboard,
            "Copy all settings",
            new Vector2(
                smallWidth,
                actionHeight),
            false,
            () =>
            {
                ImGui.SetClipboardText(
                    BuildDjCredentialClipboard(
                        radio));
            });

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
       "##checkDjConnection",
       FontAwesomeIcon.BroadcastTower,
       djConnectionSearchActive
           ? "Checking..."
           : "Check connection",
new Vector2(
    smallWidth,
    actionHeight),
false,
RequestDjConnectionCheck,
false,
djConnectionSearchActive
    ? Gold
    : Vector4.One);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##openDjGuide",
            FontAwesomeIcon.BookOpen,
            "Connection guide",
            new Vector2(
                smallWidth,
                actionHeight),
            false,
            () =>
            {
                djConnectionGuideOpen =
                    true;
            });

        var primaryWidth =
            Math.Max(
                245f,
                availableWidth -
                smallWidth * 3f -
                gap * 3f);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##broadcastDjToWatchParty",
            FontAwesomeIcon.Play,
            "Broadcast DJ Stream to Watch Party",
            new Vector2(
                primaryWidth,
                actionHeight),
            !djIsOnAir ||
            viewingAnotherParty,
            () =>
            {
                BeginDjWatchPartyBroadcast(
                    radio);
            },
            true);

        ImGui.Dummy(
            UiVec(0f, 12f));

        if (viewingAnotherParty)
        {
            ImGui.TextColored(
                MutedText,
                "Leave your current Watch Party before broadcasting your DJ stream.");
        }
        else if (djBroadcastingToWatchParty &&
                 stream.Mode == StreamMode.Hosting)
        {
            ImGui.TextColored(
                new Vector4(
                    0.25f,
                    0.90f,
                    0.48f,
                    1f),
                "Currently broadcasting DJ stream to watch party");
        }
        else
        {
            ImGui.PushTextWrapPos(
                ImGui.GetCursorPosX() +
                availableWidth);

            ImGui.TextColored(
                MutedText,
                "After connecting to our Icecast server, press check connection. Once you see 'ON AIR' you'll then be able to stream your DJ broadcast to your watch party.");

            ImGui.PopTextWrapPos();
        }
    }

    private void BeginDjWatchPartyBroadcast(
    RadioCredentialsDto radio)
    {
        if (!djIsOnAir)
        {
            return;
        }

        if (stream.Mode == StreamMode.Viewing)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Leave your current Watch Party before broadcasting your DJ stream.");

            return;
        }

        if (stream.Mode == StreamMode.Hosting)
        {
            StartDjBroadcastInHostedRoom(
                radio);

            return;
        }

        pendingWatchPartyMediaKind =
            PendingWatchPartyMediaKind.DjBroadcast;

        watchPartyCreationPopupOpen =
            true;

        createRoomPassword =
            string.Empty;

        createLockedRoomPasswordError =
            null;
    }

    private void StartDjBroadcastInHostedRoom(
       RadioCredentialsDto radio,
       bool navigateToWatchParty = false)
    {
        //
        // DJ streams are intended to be heard directly rather than through
        // the in-game TV. Mute the local host TV when broadcasting begins.
        //
        Plugin.Cfg.Muted =
            true;

        // Keep the decoded audio at the configured level so it can continue to
        // drive both Classic Bars and the FFmpeg visualizers. The local output
        // is muted separately after playback starts to avoid hearing the
        // delayed Icecast return feed.
        video.SetVolume(
            Plugin.Cfg.Volume);

        Plugin.Cfg.Save();

        djAutoMuteNoticeVisible =
            true;

        djAutoMutedStreamUrl =
            radio.ListenUrl;

        PlayDjUrl(
            radio.ListenUrl,
            "Live DJ broadcast",
            "Music / DJ");

        video.SetOutputMuted(
            true);

        djBroadcastingToWatchParty =
            true;

        if (navigateToWatchParty)
        {
            currentPage =
                HomePage.WatchAlong;

            partyPanelTab =
                PartyPanelTab.NowPlaying;
        }

        Plugin.ChatGui.Print(
            "[AlphaChannel] Now broadcasting DJ stream to watch party");
    }

    private void CompletePendingWatchPartyMedia()
    {
        if (pendingWatchPartyMediaKind ==
            PendingWatchPartyMediaKind.None)
        {
            return;
        }

        var pendingKind =
            pendingWatchPartyMediaKind;

        pendingWatchPartyMediaKind =
            PendingWatchPartyMediaKind.None;

        watchPartyCreationPopupOpen =
            false;

        switch (pendingKind)
        {
            case PendingWatchPartyMediaKind.GameRoom:
                currentPage =
                    HomePage.WatchAlong;

                partyPanelTab =
                    PartyPanelTab.NowPlaying;
                break;

            case PendingWatchPartyMediaKind.DjBroadcast:
                if (radioCredentials is { } radio)
                {
                    StartDjBroadcastInHostedRoom(
                        radio,
                        navigateToWatchParty: true);
                }

                break;

            case PendingWatchPartyMediaKind.LiveStreamBroadcast:
                StartLiveBroadcastInHostedRoom(
                    navigateToWatchParty: true);

                break;

            case PendingWatchPartyMediaKind.LocalVideoStartAndBroadcast:
                StartLocalVideoBroadcastInHostedRoom(
                    startPlayback: true,
                    navigateToWatchParty: true);

                break;

            case PendingWatchPartyMediaKind.LocalVideoCurrentPlaybackBroadcast:
                StartLocalVideoBroadcastInHostedRoom(
                    startPlayback: false,
                    navigateToWatchParty: true);

                break;

            case PendingWatchPartyMediaKind.WebLink:
                if (pendingWatchPartyVideoEntry is { } videoEntry)
                {
                    HandlePlayNow(
                        videoEntry);

                    currentPage =
                        HomePage.WatchAlong;

                    partyPanelTab =
                        PartyPanelTab.NowPlaying;
                }

                break;

            case PendingWatchPartyMediaKind.RadioStation:
                if (!string.IsNullOrWhiteSpace(
                        pendingWatchPartyRadioUrl))
                {
                    PlayDjUrl(
                        pendingWatchPartyRadioUrl,
                        pendingWatchPartyRadioTitle ??
                        "Radio station",
                        "Radio");

                    currentPage =
                        HomePage.WatchAlong;

                    partyPanelTab =
                        PartyPanelTab.NowPlaying;
                }

                break;
        }

        ClearPendingWatchPartySelection();
    }

    private void CancelPendingWatchPartyMedia()
    {
        watchPartyCreationPopupOpen =
            false;

        pendingWatchPartyMediaKind =
            PendingWatchPartyMediaKind.None;

        ClearPendingWatchPartySelection();

        createRoomPassword =
            string.Empty;

        createLockedRoomPasswordError =
            null;
    }

    private void ClearPendingWatchPartySelection()
    {
        pendingWatchPartyVideoEntry =
            null;

        pendingWatchPartyRadioUrl =
            null;

        pendingWatchPartyRadioTitle =
            null;
    }

    private void BeginWebLinkWatchParty(
        Video.VideoQueueEntry entry)
    {
        if (stream.Mode != StreamMode.None)
        {
            return;
        }

        ClearPendingWatchPartySelection();

        pendingWatchPartyVideoEntry =
            entry;

        pendingWatchPartyMediaKind =
            PendingWatchPartyMediaKind.WebLink;

        OpenPendingWatchPartyCreation();
    }

    private void BeginRadioStationWatchParty(
        string url,
        string title)
    {
        if (stream.Mode != StreamMode.None ||
            string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        ClearPendingWatchPartySelection();

        pendingWatchPartyRadioUrl =
            url;

        pendingWatchPartyRadioTitle =
            title;

        pendingWatchPartyMediaKind =
            PendingWatchPartyMediaKind.RadioStation;

        OpenPendingWatchPartyCreation();
    }

    private void OpenPendingWatchPartyCreation()
    {
        watchPartyCreationPopupOpen =
            true;

        createRoomPassword =
            string.Empty;

        createLockedRoomPasswordError =
            null;
    }

    private static string BuildDjCredentialClipboard(
        RadioCredentialsDto radio)
    {
        return
            $"Server: {radio.SourceHost}{Environment.NewLine}" +
            $"Port: {radio.SourcePort}{Environment.NewLine}" +
            $"Username: {radio.SourceUser}{Environment.NewLine}" +
            $"Mount: {radio.Mount}{Environment.NewLine}" +
            $"Password: {radio.SourcePassword ?? string.Empty}";
    }

    private static void DrawDjActionButton(
    string id,
    FontAwesomeIcon icon,
    string label,
    Vector2 size,
    bool disabled,
    Action action,
    bool primary = false,
    Vector4? textColor = null)
    {
        using (ImRaii.Disabled(
                   disabled))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   primary
                       ? Accent
                       : FrameBg)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       primary
                           ? AccentHover
                           : FrameBgHover)
                   .Push(
                       ImGuiCol.ButtonActive,
                       AccentActive))
        {
            var origin =
                ImGui.GetCursorScreenPos();

            if (ImGui.Button(
                    id,
                    size))
            {
                action();
            }

            DrawPlayerActionButtonContent(
                origin,
                size,
                icon,
                label,
                textColor ??
                Vector4.One);
        }
    }

    private static void DrawDjOrDivider()
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        const string label =
            "OR";

        var textSize =
            ImGui.CalcTextSize(
                label);

        var centerX =
            ImGui.GetCursorScreenPos().X +
            width * 0.5f;

        var y =
            ImGui.GetCursorScreenPos().Y +
            textSize.Y * 0.5f;

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddLine(
            new Vector2(
                ImGui.GetCursorScreenPos().X,
                y),
            new Vector2(
                centerX -
                textSize.X -
                Ui(16f),
                y),
            ImGui.GetColorU32(
                BorderSubtle),
            1f);

        drawList.AddLine(
            new Vector2(
                centerX +
                textSize.X +
                Ui(16f),
                y),
            new Vector2(
                ImGui.GetCursorScreenPos().X +
                width,
                y),
            ImGui.GetColorU32(
                BorderSubtle),
            1f);

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() +
            (width - textSize.X) * 0.5f);

        ImGui.TextColored(
            MutedText,
            label);
    }

    private void DrawDjRadioSection()
    {
        var height =
            400f +
            djSavedStations.Count *
            56f;

        using var sectionStyle =
            ImRaii.PushStyle(
                    ImGuiStyleVar.ChildRounding,
                    12f)
                .Push(
                    ImGuiStyleVar.ChildBorderSize,
                    1f)
                .Push(
                    ImGuiStyleVar.WindowPadding,
                    UiVec(18f, 16f));

        using var sectionColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    CardBg)
                .Push(
                    ImGuiCol.Border,
                    BorderSubtle);

        if (ImGui.BeginChild(
                "##djRadioSection",
                new Vector2(
                    -1f,
                    height),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            DrawDjRadioPanel(
                ImGui.GetContentRegionAvail().X);
        }

        ImGui.EndChild();
    }

    //
    // Periodically checks whether Icecast currently has an active
    // source connected to this account's mount.
    //

    private void UpdateDjConnectionStatus()
    {
        if (radioCredentials is null ||
            djConnectionCheckInProgress)
        {
            return;
        }

        var now =
            DateTime.UtcNow;

        //
        // An explicit connection search runs for a maximum of
        // three minutes and checks every eight seconds.
        //
        if (djConnectionSearchActive)
        {
            if (now >=
                djConnectionSearchEndsUtc)
            {
                djConnectionSearchActive =
                    false;

                djNextConnectionCheckUtc =
                    DateTime.MaxValue;

                return;
            }

            if (now >=
                djNextConnectionCheckUtc)
            {
                PerformDjConnectionCheck();
            }

            return;
        }

        //
        // Once a connection has been confirmed, perform only one
        // passive health check every 60 seconds.
        //
        if (djConnectionMonitoringEnabled &&
            now >=
            djNextConnectionCheckUtc)
        {
            PerformDjConnectionCheck();
        }
    }

    private void RequestDjConnectionCheck()
{
    if (radioCredentials is null)
    {
        return;
    }

    //
    // Pressing the button begins or restarts the
    // three-minute connection search.
    //
    djConnectionSearchActive =
        true;

    djConnectionSearchEndsUtc =
        DateTime.UtcNow.AddMinutes(
            3);

    djNextConnectionCheckUtc =
        DateTime.MinValue;

    //
    // Start the first check immediately.
    //
    if (!djConnectionCheckInProgress)
    {
        PerformDjConnectionCheck();
    }
}

    private void PerformDjConnectionCheck()
    {
        if (radioCredentials is not { } radio ||
            djConnectionCheckInProgress)
        {
            return;
        }

        djConnectionCheckInProgress =
            true;

        var listenUrl =
            radio.ListenUrl;

        _ = Task.Run(
            async () =>
            {
                bool isOnAir;

                try
                {
                    isOnAir =
                        await radioClient
                            .IsOnAirAsync(
                                listenUrl)
                            .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    AepLog.Warning(
                        $"[Radio] connection check failed: {exception.Message}");

                    isOnAir =
                        false;
                }

                var now =
                    DateTime.UtcNow;

                var wasOnAir =
                    djIsOnAir;

                djIsOnAir =
                    isOnAir;

                if (isOnAir)
                {
                    if (!wasOnAir ||
                        djOnAirSinceUtc is null)
                    {
                        djOnAirSinceUtc =
                            now;
                    }

                    //
                    // A successful result ends the frequent search and enables
                    // low-frequency health monitoring.
                    //
                    djConnectionSearchActive =
                        false;

                    djConnectionMonitoringEnabled =
                        true;

                    djNextConnectionCheckUtc =
                        now.AddSeconds(
                            60);
                }
                else
                {
                    djOnAirSinceUtc =
                        null;

                    if (djConnectionSearchActive &&
                        now <
                        djConnectionSearchEndsUtc)
                    {
                        //
                        // During the explicit three-minute search, retry using
                        // the existing eight-second polling frequency.
                        //
                        djNextConnectionCheckUtc =
                            now.AddSeconds(
                                8);
                    }
                    else if (djConnectionMonitoringEnabled)
                    {
                        //
                        // A previously confirmed connection is checked only
                        // once per minute, even after it goes offline.
                        //
                        djNextConnectionCheckUtc =
                            now.AddSeconds(
                                60);
                    }
                    else
                    {
                        djConnectionSearchActive =
                            false;

                        djNextConnectionCheckUtc =
                            DateTime.MaxValue;
                    }
                }

                djConnectionCheckInProgress =
                    false;
            });
    }

    private static string FormatDjOnAirDuration(
        TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration =
                TimeSpan.Zero;
        }

        if (duration.TotalHours >= 1d)
        {
            return
                $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return
            $"{duration.Minutes:00}:{duration.Seconds:00}";
    }


    // ---------------------------------------------------------
    // Direct audio
    // ---------------------------------------------------------

    private void DrawDjDirectAudioPanel(
        float width)
    {
        using var group =
            ImRaii.Group();

        SetUiFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Direct Audio");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 3f));

        if (ImGui.Button("Use AlphaChannel radio", new Vector2(MathF.Min(Ui(230f), width), Ui(32f))))
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
            UiVec(0f, 8f));

        SetUiFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "Play an MP3 file or live audio stream by URL.");

        SetUiFontScale(
            1f);


        ImGui.Dummy(
            UiVec(0f, 16f));


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
                UiVec(12f, 9f)))
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
                        Ui(36f))))
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
            UiVec(0f, 14f));


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
                        Ui(230f),
                        width),
                    Ui(38f));

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
            UiVec(0f, 18f));


        //
        // Supported-format information box
        //

        DrawDjSupportedFormatsBox(
            width);

        ImGui.Dummy(
    UiVec(0f, 10f));

        SetUiFontScale(
            0.74f);

        ImGui.TextColored(
            MutedText,
            "Direct audio links can also be shared through Watch Party.");

        SetUiFontScale(
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
                UiVec(22f, 36f);

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

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
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
               UiVec(48f, 10f));

            SetUiFontScale(
                0.86f);

            ImGui.TextColored(
                Vector4.One,
                "Supported formats");

            SetUiFontScale(
                1f);


            ImGui.SetCursorPos(
        UiVec(48f, 34f));

            SetUiFontScale(
                0.78f);

            ImGui.TextColored(
                MutedText,
                "MP3, AAC, OGG, M4A, WAV and most live audio streams.");

            SetUiFontScale(
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

        SetUiFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Listen to a radio station");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 3f));

        SetUiFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "Play a suggested station or save your own direct stream.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 7f));

        SetUiFontScale(
            0.78f);

        ImGui.TextColored(
            MutedText,
            "SUGGESTED STATIONS");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 4f));

        DrawDjFeaturedStation(
     "##djFeaturedTruckersFm",
     FontAwesomeIcon.BroadcastTower,
     "Truckers FM",
     "Chart hits, pop, rock and EDM",
     "https://radio.truckers.fm/",
     width);

        ImGui.Dummy(
            UiVec(0f, 3f));

        DrawDjFeaturedStation(
       "##djFeatured181Power",
       FontAwesomeIcon.Music,
       "181 Power",
       "Current pop, Top 40 and dance hits",
       "https://listen.181fm.com/181-power_128k.mp3",
       width);

        ImGui.Dummy(
            UiVec(0f, 3f));

        DrawDjFeaturedStation(
            "##djFeatured181Mix",
            FontAwesomeIcon.Headphones,
            "181 The Mix",
            "Pop, chart hits and adult contemporary",
            "https://listen.181fm.com/181-themix_128k.mp3",
            width);

        ImGui.Dummy(
            UiVec(0f, 3f));

        DrawDjFeaturedStation(
            "##djFeaturedPinguinPop",
            FontAwesomeIcon.Music,
            "Pinguin Pop",
            "Non-stop modern and alternative pop",
            "https://13683.live.streamtheworld.com/SP_R2591862_SC",
            width);

        ImGui.Dummy(
            UiVec(0f, 3f));

        DrawDjFeaturedStation(
            "##djFeaturedWrtiClassical",
            FontAwesomeIcon.Music,
            "WRTI Classical",
            "Classical music throughout the day",
            "https://wrti-live.streamguys1.com/classical-mp3",
            width);

        ImGui.Dummy(
            UiVec(0f, 3f));

        DrawDjFeaturedStation(
            "##djFeaturedWrtiJazz",
            FontAwesomeIcon.Headphones,
            "WRTI Jazz",
            "Classic and contemporary jazz",
            "https://wrti-live.streamguys1.com/jazz-mp3",
            width);

        ImGui.Dummy(
            UiVec(0f, 8f));

        //
        // Saved stations header and Add button.
        //

        var savedHeaderPosition =
            ImGui.GetCursorScreenPos();

        SetUiFontScale(
            0.78f);

        ImGui.TextColored(
            MutedText,
            "YOUR STATIONS");

        SetUiFontScale(
            1f);

        var addButtonSize =
            UiVec(122f, 30f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                savedHeaderPosition.X +
                width -
                addButtonSize.X,
                savedHeaderPosition.Y -
                Ui(6f)));

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
            var buttonOrigin =
                ImGui.GetCursorScreenPos();

            if (ImGui.Button(
                    "##openAddDjStation",
                    addButtonSize))
            {
                OpenDjStationEditor(
                    null);
            }

            DrawPlayerActionButtonContent(
                buttonOrigin,
                addButtonSize,
                FontAwesomeIcon.Plus,
                "Add station",
                Vector4.One);
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                savedHeaderPosition.X,
                savedHeaderPosition.Y + Ui(25f)));

        if (djSavedStations.Count == 0)
        {
            ImGui.TextColored(
                MutedText,
                "No saved stations yet.");
        }
        else
        {
            for (var index = 0;
                 index < djSavedStations.Count;
                 index++)
            {
                DrawDjSavedStation(
                    djSavedStations[index],
                    index,
                    width);

                ImGui.Dummy(
                    UiVec(0f, 4f));
            }
        }
    }


    private void DrawDjFeaturedStation(
    string id,
    FontAwesomeIcon icon,
    string name,
    string description,
    string url,
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
            UiVec(5f, 5f);

        var tileMax =
            tileMin +
            UiVec(32f, 32f);

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

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var glyph =
                icon.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
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
        // Station name and description
        //

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            min +
            UiVec(45f, 5f),
            ImGui.GetColorU32(
                Vector4.One),
            name);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            min +
            UiVec(45f, 23f),
            ImGui.GetColorU32(
                MutedText),
            description);

        //
        // Working Play button
        //

        var playWidth = Ui(58f);

        var partyWidth = Ui(148f);

        var buttonGap = Ui(6f);

        var showPartyAction =
            stream.Mode == StreamMode.None;

        ImGui.SetCursorScreenPos(
            new Vector2(
                max.X -
                (showPartyAction
                    ? partyWidth + buttonGap
                    : 0f) -
                playWidth -
                Ui(6f),
                min.Y +
                Ui(6f)));

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
            if (ImGui.Button(
                    $"Play{id}",
                    new Vector2(
                        playWidth,
                        Ui(29f))))
            {
                PlayDjUrl(
                    url,
                    name,
                    "Radio");
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                url);
        }

        if (showPartyAction)
        {
            ImGui.SetCursorScreenPos(
                new Vector2(
                    max.X -
                    partyWidth -
                    Ui(6f),
                    min.Y +
                    Ui(6f)));

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       7f))
            using (ImRaii.PushColor(
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
                        $"Play in Watch Party{id}Party",
                        new Vector2(
                            partyWidth,
                            Ui(29f))))
                {
                    BeginRadioStationWatchParty(
                        url,
                        name);
                }
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

    private void DrawDjSavedStation(
     SavedRadioStationRecord station,
     int index,
     float width)
    {
        const float height =
            44f;

        var origin =
            ImGui.GetCursorScreenPos();

        var maximum =
            origin +
            new Vector2(
                width,
                height);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            maximum,
            ImGui.GetColorU32(
                FrameBg),
            8f);

        drawList.AddRect(
            origin,
            maximum,
            ImGui.GetColorU32(
                BorderSubtle),
            8f);

        drawList.AddCircleFilled(
            origin +
            new Vector2(
                Ui(14f),
                height * 0.5f),
            4f,
            ImGui.GetColorU32(
                Good),
            16);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            origin +
            UiVec(27f, 7f),
            ImGui.GetColorU32(
                Vector4.One),
            station.Name);

        var displayedUrl =
            station.Url.Length > 70
                ? $"{station.Url[..69]}…"
                : station.Url;

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            origin +
            UiVec(27f, 25f),
            ImGui.GetColorU32(
                MutedText),
            displayedUrl);

        var menuWidth = Ui(34f);

        var playWidth = Ui(62f);

        var partyWidth = Ui(148f);

        var buttonGap = Ui(6f);

        var showPartyAction =
            stream.Mode == StreamMode.None;

        ImGui.SetCursorScreenPos(
            new Vector2(
                maximum.X -
                menuWidth -
                Ui(7f),
                origin.Y + Ui(7f)));

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   Vector4.Zero)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       FrameBgHover)
                   .Push(
                       ImGuiCol.ButtonActive,
                       AccentActive))
        {
            if (ImGui.Button(
                    $"{FontAwesomeIcon.EllipsisH.ToIconString()}##djStationMenu{index}",
                    new Vector2(
                        menuWidth,
                        Ui(30f))))
            {
                ImGui.OpenPopup(
                    $"##djSavedStationActions{index}");
            }
        }

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.PopupRounding,
                   8f)
                   .Push(
                       ImGuiStyleVar.WindowPadding,
                       UiVec(8f, 8f)))
        using (ImRaii.PushColor(
                   ImGuiCol.PopupBg,
                   new Vector4(
                       0.045f,
                       0.05f,
                       0.085f,
                       1f))
                   .Push(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.55f)))
        {
            if (ImGui.BeginPopup(
                    $"##djSavedStationActions{index}"))
            {
                if (ImGui.MenuItem(
                        "Edit station"))
                {
                    OpenDjStationEditor(
                        index);
                }

                using (ImRaii.PushColor(
                           ImGuiCol.Text,
                           Danger))
                {
                    if (ImGui.MenuItem(
                            "Remove station"))
                    {
                        djSavedStations.RemoveAt(
                            index);

                        Plugin.Cfg.Save();
                    }
                }

                ImGui.EndPopup();
            }
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                maximum.X -
                menuWidth -
                (showPartyAction
                    ? partyWidth + buttonGap
                    : 0f) -
                playWidth -
                Ui(14f),
                origin.Y + Ui(7f)));

        using (ImRaii.PushColor(
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
                        Ui(30f))))
            {
                PlayDjStation(
                    station);
            }
        }

        if (ImGui.IsItemHovered() &&
            displayedUrl != station.Url)
        {
            ImGui.SetTooltip(
                station.Url);
        }

        if (showPartyAction)
        {
            ImGui.SetCursorScreenPos(
                new Vector2(
                    maximum.X -
                    menuWidth -
                    partyWidth -
                    Ui(13f),
                    origin.Y +
                    Ui(7f)));

            using (ImRaii.PushColor(
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
                        $"Play in Watch Party##djStationParty_{index}",
                        new Vector2(
                            partyWidth,
                            Ui(30f))))
                {
                    BeginRadioStationWatchParty(
                        station.Url,
                        station.Name);
                }
            }
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                origin.X,
                maximum.Y));

        ImGui.Dummy(
            new Vector2(
                width,
                1f));
    }


    private void OpenDjStationEditor(
    int? stationIndex)
    {
        djEditingStationIndex =
            stationIndex;

        djStationEditorError =
            null;

        if (stationIndex is { } index &&
            index >= 0 &&
            index < djSavedStations.Count)
        {
            var station =
                djSavedStations[index];

            djStationNameInput =
                station.Name;

            djStationUrlInput =
                station.Url;
        }
        else
        {
            djStationNameInput =
                string.Empty;

            djStationUrlInput =
                string.Empty;
        }

        djAddStationPopupOpen =
            true;
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
       new SavedRadioStationRecord
       {
           Name =
               name,

           Url =
               url,
       });

        Plugin.Cfg.Save();

        djStationNameInput =
            string.Empty;

        djStationUrlInput =
            string.Empty;
    }


    private void PlayDjStation(
        SavedRadioStationRecord station)
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
        if (!patreonAccessConfirmed ||
            !HasConfiguredPatreonAccess())
        {
            patreonAccessConfirmed = false;
            djPatreonGateVisible = true;
            djPatreonAccessMessage = null;
            radioCredentials = null;
            return;
        }

        var token =
            CurrentSession?.Token;

        if (string.IsNullOrEmpty(
                token))
        {
            radioError =
                "Sign in to get broadcast connection details.";

            return;
        }

        if (djCredentialsLoading)
        {
            return;
        }

        radioError =
            null;

        djCredentialsLoading =
            true;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var issued =
                        await radioClient
                            .IssueAsync(
                                token)
                            .ConfigureAwait(false);

                    if (issued is null)
                    {
                        radioError =
                            "Couldn't get broadcast connection details.";

                        return;
                    }

                    radioCredentials =
                        issued;

                    //
                    // Retain the listening URL internally for connection
                    // checks and Watch Party playback. It is not displayed.
                    //

                    djStreamUrl =
                        issued.ListenUrl;

                    djConnectionSearchActive =
                        false;

                    djConnectionMonitoringEnabled =
                        false;

                    djConnectionCheckInProgress =
                        false;

                    djIsOnAir =
                        false;

                    djOnAirSinceUtc =
                        null;

                    djNextConnectionCheckUtc =
                        DateTime.MaxValue;
                }
                finally
                {
                    djCredentialsLoading =
                        false;
                }
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

        //
        // Preserve the currently selected Watch Party visualizer and colour
        // whenever a different radio station or audio stream is started.
        //
        // AudioVisualizerSelection removes any existing visualizer fragment
        // before adding the current selection, preventing duplicate fragments.
        //
        var playbackUrl =
            AudioVisualizerSelection.AddToUrl(
                url,
                partyVisualizerMode,
                partyVisualizerTheme);

        queue.PlayTransient(
            new Video.VideoQueueEntry(
                playbackUrl,
                title,
                source,
                null,
                null,
                0d,
                true));

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

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                min +
                new Vector2(
                    Ui(17f),
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

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            min +
            UiVec(49f, 12f),
            ImGui.GetColorU32(
                Vector4.One),
            title);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            min +
            UiVec(49f, 34f),
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

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                max -
                new Vector2(
                    chevronSize.X +
                    Ui(15f),
                    height / 2f +
                    chevronSize.Y /
                    2f),
                ImGui.GetColorU32(
                    MutedText),
                chevron);
        }
    }
    private void DrawPendingWatchPartyCreationOverlay()
    {
        if (!watchPartyCreationPopupOpen)
        {
            return;
        }

        var parentPosition =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var panelWidth = Ui(680f);
        var panelHeight = Ui(520f);

        var panelPosition =
            parentPosition +
            new Vector2(
                (parentSize.X - panelWidth) * 0.5f,
                (parentSize.Y - panelHeight) * 0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
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
                "##pendingWatchPartyCreationOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            parentPosition,
            parentPosition + parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.54f)));

        ImGui.SetCursorScreenPos(
            panelPosition);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   Ui(14f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildBorderSize,
                   Ui(1f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.WindowPadding,
                   UiVec(
                       24f,
                       20f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       0.025f,
                       0.03f,
                       0.06f,
                       0.995f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.82f)))
        using (var panel =
               ImRaii.Child(
                   "##pendingWatchPartyCreationPanel",
                   new Vector2(
                       panelWidth,
                       panelHeight),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (panel)
            {
                SetUiFontScale(
                    1.18f);

                ImGui.TextColored(
                    Vector4.One,
                    "Create a Watch Party");

                SetUiFontScale(
                    1f);

                var creationDescription =
                    pendingWatchPartyMediaKind switch
                    {
                        PendingWatchPartyMediaKind.GameRoom =>
                            "Create a room to share your gameplay with friends.",

                        PendingWatchPartyMediaKind.LiveStreamBroadcast =>
                            "Create a room before broadcasting your live video stream.",

                        PendingWatchPartyMediaKind.LocalVideoStartAndBroadcast or
                        PendingWatchPartyMediaKind.LocalVideoCurrentPlaybackBroadcast =>
                            "Create a room before broadcasting your local video.",

                        PendingWatchPartyMediaKind.WebLink =>
                            "Create a room and play this video for your Watch Party.",

                        PendingWatchPartyMediaKind.RadioStation =>
                            "Create a room and play this radio station for your Watch Party.",

                        _ =>
                            "Create a room before broadcasting your DJ stream."
                    };

                ImGui.TextColored(
                    MutedText,
                    creationDescription);

                ImGui.Dummy(
    UiVec(
        0f,
        14f));

                var closeSize =
                    ImGui.CalcTextSize(
                        FontAwesomeIcon.Times
                            .ToIconString());

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        panelPosition.X +
                        panelWidth -
                        Ui(24f) -
                        closeSize.X,
                        panelPosition.Y +
                        Ui(20f)));

                using (ImRaii.PushFont(
                           UiBuilder.IconFont))
                {
                    if (ImGui.InvisibleButton(
                            "##closePendingWatchPartyCreation",
                            closeSize))
                    {
                        CancelPendingWatchPartyMedia();
                    }

                    ImGui.GetWindowDrawList()
                        .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                            ImGui.GetItemRectMin(),
                            ImGui.GetColorU32(
                                Vector4.One),
                            FontAwesomeIcon.Times
                                .ToIconString());
                }

                ImGui.SetCursorScreenPos(
                    panelPosition +
                    UiVec(
                        24f,
                        92f));

                var toggleOrigin =
                    ImGui.GetCursorScreenPos();

                ImGui.TextColored(
                    MutedText,
                    "Room details");

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        panelPosition.X +
                        panelWidth -
                        Ui(100f),
                        toggleOrigin.Y));

                DrawWatchPartyAdultToggle();

                ImGui.SetCursorScreenPos(
                    panelPosition +
                    UiVec(
                        24f,
                        132f));

                DrawCreateRoomFields(
                    panelWidth -
                    Ui(48f));

                var buttonWidth =
                    (panelWidth -
                     Ui(48f) -
                     Ui(10f)) /
                    2f;

                var footerY =
                    panelPosition.Y +
                    panelHeight -
                    Ui(60f);

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        panelPosition.X +
                        Ui(24f),
                        footerY));

                DrawDjActionButton(
                    "##cancelPendingWatchPartyCreation",
                    FontAwesomeIcon.Times,
                    "Cancel",
                    new Vector2(
                        buttonWidth,
                        Ui(38f)),
                    false,
                    CancelPendingWatchPartyMedia);

                ImGui.SameLine(
                    0f,
                    Ui(10f));

                DrawDjActionButton(
                    "##createPendingWatchParty",
                    FontAwesomeIcon.Users,
                    "Create Watch Party",
                    new Vector2(
                        buttonWidth,
                        Ui(38f)),
                    false,
                    CreateEmptyWatchParty,
                    true);
            }
        }

        ImGui.End();
    }

    // ---------------------------------------------------------
    // Add/edit saved radio station overlay
    // ---------------------------------------------------------

    private void DrawDjStationEditorOverlay()
    {
        if (!djAddStationPopupOpen)
        {
            return;
        }

        var parentPosition =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var panelWidth = Ui(520f);

        var panelHeight = Ui(430f);

        var panelPosition =
            parentPosition +
            new Vector2(
                (parentSize.X - panelWidth) * 0.5f,
                (parentSize.Y - panelHeight) * 0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
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
                "##djStationEditorOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            parentPosition,
            parentPosition + parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.50f)));

        ImGui.SetCursorScreenPos(
            panelPosition);

        {
            using var panelStyle =
                ImRaii.PushStyle(
                        ImGuiStyleVar.ChildRounding,
                        14f)
                    .Push(
                        ImGuiStyleVar.ChildBorderSize,
                        1f)
                    .Push(
                        ImGuiStyleVar.WindowPadding,
                        UiVec(24f, 20f))
                    .Push(
                        ImGuiStyleVar.FrameRounding,
                        8f);

            using var panelColors =
                ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        new Vector4(
                            0.025f,
                            0.03f,
                            0.06f,
                            0.995f))
                    .Push(
                        ImGuiCol.Border,
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.82f));

            if (ImGui.BeginChild(
                    "##djStationEditorPanel",
                    new Vector2(
                        panelWidth,
                        panelHeight),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
            {
                DrawDjStationEditorContents();
            }

            ImGui.EndChild();
        }

        ImGui.End();
    }

    private void DrawDjStationEditorContents()
    {
        var editing =
            djEditingStationIndex is not null;

        SetUiFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            editing
                ? "Edit radio station"
                : "Add radio station");

        SetUiFontScale(
            1f);

        ImGui.TextColored(
            MutedText,
            "Save a direct internet-radio stream to your station list.");

        ImGui.Dummy(
            UiVec(0f, 8f));

        ImGui.Separator();

        ImGui.Dummy(
            UiVec(0f, 12f));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   8f))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.WindowPadding,
                   UiVec(12f, 10f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.10f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.38f)))
        using (var information =
               ImRaii.Child(
                   "##stationUrlInformation",
                   UiVec(-1f, 76f),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (information)
            {
                ImGui.TextColored(
                    AccentHover,
                    "Direct stream URLs");

                ImGui.PushTextWrapPos(
                    ImGui.GetCursorPosX() +
                    ImGui.GetContentRegionAvail().X);

                ImGui.TextColored(
                    MutedText,
                    "Add a supported radio station stream to your saved stations to play locally or broadcast to your Watch Party. Enter the direct MP3, OGG or AAC stream URL, not the station's website.");

                ImGui.PopTextWrapPos();
            }
        }

        ImGui.Dummy(
            UiVec(0f, 12f));

        ImGui.TextUnformatted(
            "Station name");

        ImGui.SetNextItemWidth(
            -1f);

        using (ImRaii.PushColor(
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
                "##djStationEditorName",
                "My radio station",
                ref djStationNameInput,
                100);
        }

        ImGui.Dummy(
            UiVec(0f, 8f));

        ImGui.TextUnformatted(
            "Direct stream URL");

        ImGui.SetNextItemWidth(
            -1f);

        using (ImRaii.PushColor(
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
                "##djStationEditorUrl",
                "https://example.com/live.mp3",
                ref djStationUrlInput,
                2000);
        }

        if (djStationEditorError is { Length: > 0 } error)
        {
            ImGui.Dummy(
                UiVec(0f, 5f));

            ImGui.TextColored(
                Danger,
                error);
        }

        var cancelSize =
            UiVec(100f, 36f);

        var saveSize =
            UiVec(120f, 36f);

        var footerY =
            ImGui.GetWindowHeight() -
            saveSize.Y -
            20f;

        var right =
            ImGui.GetWindowWidth() -
            24f;

        ImGui.SetCursorPos(
            new Vector2(
                right -
                saveSize.X -
                cancelSize.X -
                Ui(10f),
                footerY));

        DrawDjActionButton(
            "##cancelDjStationEditor",
            FontAwesomeIcon.Times,
            "Cancel",
            cancelSize,
            false,
            CloseDjStationEditor);

        ImGui.SetCursorPos(
            new Vector2(
                right -
                saveSize.X,
                footerY));

        DrawDjActionButton(
            "##saveDjStationEditor",
            FontAwesomeIcon.Save,
            editing
                ? "Save changes"
                : "Add station",
            saveSize,
            false,
            SaveDjStationEditor,
            true);
    }

    private void SaveDjStationEditor()
    {
        var name =
            djStationNameInput.Trim();

        var url =
            djStationUrlInput.Trim();

        if (string.IsNullOrWhiteSpace(
                name))
        {
            djStationEditorError =
                "Enter a station name.";

            return;
        }

        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp &&
             parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            djStationEditorError =
                "Enter a valid HTTP or HTTPS direct stream URL.";

            return;
        }

        if (djEditingStationIndex is { } index &&
            index >= 0 &&
            index < djSavedStations.Count)
        {
            djSavedStations[index].Name =
                name;

            djSavedStations[index].Url =
                url;
        }
        else
        {
            djSavedStations.Add(
                new SavedRadioStationRecord
                {
                    Name =
                        name,

                    Url =
                        url,
                });
        }

        Plugin.Cfg.Save();

        CloseDjStationEditor();
    }

    private void CloseDjStationEditor()
    {
        djAddStationPopupOpen =
            false;

        djEditingStationIndex =
            null;

        djStationEditorError =
            null;

        djStationNameInput =
            string.Empty;

        djStationUrlInput =
            string.Empty;
    }

    // ---------------------------------------------------------
    // Unified Icecast connection guide
    // ---------------------------------------------------------

    private void DrawDjConnectionGuideOverlay()
    {
        if (!djConnectionGuideOpen)
        {
            return;
        }

        //
        // This method is called from MainWindow.Draw after the normal
        // layout has finished, so the current window is AlphaChannel's
        // main window rather than the scrolling DJ content child.
        //

        var parentPosition =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var panelWidth =
            Math.Min(
                820f,
                parentSize.X - 40f);

        var panelHeight =
            Math.Min(
                620f,
                parentSize.Y - 40f);

        var panelPosition =
            parentPosition +
            new Vector2(
                (parentSize.X - panelWidth) * 0.5f,
                (parentSize.Y - panelHeight) * 0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
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
                "##djConnectionGuideOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var overlayDrawList =
            ImGui.GetWindowDrawList();

        overlayDrawList.AddRectFilled(
            parentPosition,
            parentPosition + parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.50f)));

        ImGui.SetCursorScreenPos(
            panelPosition);

        {
            using var panelStyle =
                ImRaii.PushStyle(
                        ImGuiStyleVar.ChildRounding,
                        14f)
                    .Push(
                        ImGuiStyleVar.ChildBorderSize,
                        1f)
                    .Push(
                        ImGuiStyleVar.WindowPadding,
                        UiVec(24f, 20f))
                    .Push(
                        ImGuiStyleVar.ItemSpacing,
                        UiVec(10f, 9f))
                    .Push(
                        ImGuiStyleVar.FrameRounding,
                        8f);

            using var panelColors =
                ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        new Vector4(
                            0.025f,
                            0.03f,
                            0.06f,
                            0.995f))
                    .Push(
                        ImGuiCol.Border,
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.82f));

            if (ImGui.BeginChild(
                    "##djConnectionGuidePanel",
                    new Vector2(
                        panelWidth,
                        panelHeight),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
            {
                DrawDjConnectionGuideHeader();

                ImGui.Dummy(
                    UiVec(0f, 8f));

                ImGui.Separator();

                ImGui.Dummy(
                    UiVec(0f, 12f));

                DrawDjConnectionGuideTabs();

                ImGui.Dummy(
                    UiVec(0f, 12f));

                var footerHeight = Ui(58f);

                using (ImRaii.PushStyle(
                           ImGuiStyleVar.ChildRounding,
                           9f)
                           .Push(
                               ImGuiStyleVar.WindowPadding,
                               UiVec(18f, 16f)))
                using (ImRaii.PushColor(
                           ImGuiCol.ChildBg,
                           new Vector4(
                               0.05f,
                               0.055f,
                               0.095f,
                               1f))
                           .Push(
                               ImGuiCol.Border,
                               new Vector4(
                                   Accent.X,
                                   Accent.Y,
                                   Accent.Z,
                                   0.38f)))
                {
                    if (ImGui.BeginChild(
                            "##djConnectionGuideContent",
                            new Vector2(
                                -1f,
                                -footerHeight),
                            true,
                            ImGuiWindowFlags.None))
                    {
                        DrawDjConnectionGuideTabContent();
                    }

                    ImGui.EndChild();
                }

                DrawDjConnectionGuideFooter();
            }

            ImGui.EndChild();
        }

        ImGui.End();
    }

    private void DrawDjConnectionGuideHeader()
    {
        SetUiFontScale(
            1.18f);

        ImGui.TextColored(
            new Vector4(
                0.94f,
                0.92f,
                1f,
                1f),
            "DJ connection guide");

        SetUiFontScale(
            1f);

        ImGui.TextColored(
            MutedText,
            "Connect popular Icecast-compatible DJ software to AlphaChannel.");

        var closeSize =
            UiVec(34f, 28f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                ImGui.GetWindowPos().X +
                ImGui.GetWindowWidth() -
                closeSize.X -
                Ui(21f),
                ImGui.GetWindowPos().Y +
                Ui(17f)));

        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   Vector4.Zero)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       FrameBgHover)
                   .Push(
                       ImGuiCol.ButtonActive,
                       AccentActive))
        {
            if (ImGui.Button(
                    "×##closeDjConnectionGuide",
                    closeSize))
            {
                djConnectionGuideOpen =
                    false;
            }
        }
    }

    private void DrawDjConnectionGuideTabs()
    {
        string[] tabs =
        [
            "BUTT",
        "Mixxx",
        "AltaCast",
        "Rocket",
        "Other",
    ];

        const float gap =
            8f;

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var tabWidth =
            (availableWidth -
             gap * (tabs.Length - 1)) /
            tabs.Length;

        for (var index = 0;
             index < tabs.Length;
             index++)
        {
            if (index > 0)
            {
                ImGui.SameLine(
                    0f,
                    gap);
            }

            var selected =
                djConnectionGuideTab ==
                index;

            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       selected
                           ? Accent
                           : FrameBg)
                       .Push(
                           ImGuiCol.ButtonHovered,
                           selected
                               ? AccentHover
                               : FrameBgHover)
                       .Push(
                           ImGuiCol.ButtonActive,
                           AccentActive))
            {
                if (ImGui.Button(
                        $"{tabs[index]}##djConnectionGuideTab{index}",
                        new Vector2(
                            tabWidth,
                            Ui(36f))))
                {
                    djConnectionGuideTab =
                        index;
                }
            }
        }
    }

    private void DrawDjConnectionGuideTabContent()
    {
        switch (djConnectionGuideTab)
        {
            case 0:
                DrawDjButtGuide();
                break;

            case 1:
                DrawDjMixxxGuide();
                break;

            case 2:
                DrawDjAltaCastGuide();
                break;

            case 3:
                DrawDjRocketGuide();
                break;

            default:
                DrawDjGenericIcecastGuide();
                break;
        }
    }

    private void DrawDjButtGuide()
    {
        DrawDjSoftwareGuideHeading(
            "BUTT",
            "Broadcast Using This Tool is a lightweight application designed specifically for Icecast and Shoutcast broadcasting.");

        DrawDjGuideNumberedLine(
            "1",
            "Open BUTT and select Settings.");

        DrawDjGuideNumberedLine(
            "2",
            "In the Main tab, add a new server.");

        DrawDjGuideNumberedLine(
            "3",
            "Set the server type to IceCast and copy the AlphaChannel connection values into Address, Port, User, Password and Mount.");

        DrawDjGuideNumberedLine(
            "4",
            "Open the Audio tab and select MP3 or OGG, stereo, with a 192 kbps target where available.");

        DrawDjGuideNumberedLine(
            "5",
            "Save the settings, choose your audio input, then press the play button in BUTT to connect.");

        DrawDjReturnAndCheckNotice();
    }

    private void DrawDjMixxxGuide()
    {
        DrawDjSoftwareGuideHeading(
            "Mixxx",
            "Mixxx provides decks, playlists, mixing controls and native Icecast live broadcasting.");

        DrawDjGuideNumberedLine(
            "1",
            "Open Preferences and select Live Broadcasting.");

        DrawDjGuideNumberedLine(
            "2",
            "Set Type to Icecast 2.");

        DrawDjGuideNumberedLine(
            "3",
            "Enter AlphaChannel's Server, Port, Mount, Username and Password in the matching fields.");

        DrawDjGuideNumberedLine(
            "4",
            "Select MP3 or OGG, stereo, and use a 192 kbps target where available.");

        DrawDjGuideNumberedLine(
            "5",
            "Apply the settings, then enable live broadcasting from Mixxx's Options menu.");

        DrawDjReturnAndCheckNotice();
    }

    private void DrawDjAltaCastGuide()
    {
        DrawDjSoftwareGuideHeading(
            "AltaCast",
            "AltaCast is a traditional encoder for sending an existing audio mix to an Icecast server.");

        DrawDjGuideNumberedLine(
            "1",
            "Open AltaCast and add a new encoder.");

        DrawDjGuideNumberedLine(
            "2",
            "Open the encoder configuration and select Icecast2.");

        DrawDjGuideNumberedLine(
            "3",
            "Enter the AlphaChannel Server, Port, Username, Password and Mountpoint.");

        DrawDjGuideNumberedLine(
            "4",
            "Choose MP3 or OGG and configure stereo audio at an appropriate bitrate.");

        DrawDjGuideNumberedLine(
            "5",
            "Select the audio source carrying your DJ mix, then connect the encoder.");

        DrawDjReturnAndCheckNotice();
    }

    private void DrawDjRocketGuide()
    {
        DrawDjSoftwareGuideHeading(
            "Rocket Broadcaster",
            "Rocket Broadcaster combines microphone, music and application audio for live radio-style production.");

        DrawDjGuideNumberedLine(
            "1",
            "Open Broadcast Streams and add a new stream.");

        DrawDjGuideNumberedLine(
            "2",
            "Choose Icecast 2 as the streaming server type.");

        DrawDjGuideNumberedLine(
            "3",
            "Enter AlphaChannel's Server, Port, Mount, Username and Password.");

        DrawDjGuideNumberedLine(
            "4",
            "Select a supported MP3 or OGG encoder and configure your desired audio sources.");

        DrawDjGuideNumberedLine(
            "5",
            "Start broadcasting and confirm that Rocket reports a successful connection.");

        DrawDjReturnAndCheckNotice();
    }

    private void DrawDjGenericIcecastGuide()
    {
        DrawDjSoftwareGuideHeading(
            "Other Icecast software",
            "Most Icecast-compatible applications request the same basic connection values.");

        DrawDjGuideSetting(
            "Address / Host",
            "AlphaChannel Server");

        DrawDjGuideSetting(
            "Port",
            "AlphaChannel Port");

        DrawDjGuideSetting(
            "User / Login",
            "AlphaChannel Username");

        DrawDjGuideSetting(
            "Password",
            "AlphaChannel Password");

        DrawDjGuideSetting(
            "Mount / Mountpoint",
            "AlphaChannel Mount");

        ImGui.Dummy(
            UiVec(0f, 12f));

        ImGui.TextWrapped(
            "Select Icecast or Icecast 2 when the software asks for a server type. Use MP3 or OGG, stereo, and a 192 kbps target where supported.");

        DrawDjReturnAndCheckNotice();
    }

    private void DrawDjSoftwareGuideHeading(
        string title,
        string description)
    {
        SetUiFontScale(
            1.12f);

        ImGui.TextColored(
            Vector4.One,
            title);

        SetUiFontScale(
            1f);

        ImGui.TextWrapped(
            description);

        ImGui.Dummy(
            UiVec(0f, 14f));
    }

    private static void DrawDjReturnAndCheckNotice()
    {
        ImGui.Dummy(
            UiVec(0f, 12f));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   8f)
                   .Push(
                       ImGuiStyleVar.WindowPadding,
                       UiVec(12f, 10f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       Accent.X * 0.10f,
                       Accent.Y * 0.10f,
                       Accent.Z * 0.16f,
                       0.95f))
                   .Push(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.34f)))
        {
            if (ImGui.BeginChild(
                    "##djReturnAndCheck",
                    UiVec(-1f, 58f),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.TextColored(
                    AccentHover,
                    "Return to AlphaChannel");

                ImGui.TextColored(
                    MutedText,
                    "Wait for ON AIR, or press Check connection after your software reports that it is connected.");
            }

            ImGui.EndChild();
        }
    }

    private void DrawDjConnectionGuideFooter()
    {
        var copySize =
            UiVec(170f, 36f);

        var closeSize =
            UiVec(100f, 36f);

        using (ImRaii.Disabled(
                   radioCredentials is null))
        {
            DrawDjActionButton(
                "##copyGuideDjSettings",
                FontAwesomeIcon.Clipboard,
                "Copy all settings",
                copySize,
                radioCredentials is null,
                () =>
                {
                    if (radioCredentials is { } radio)
                    {
                        ImGui.SetClipboardText(
                            BuildDjCredentialClipboard(
                                radio));
                    }
                });
        }

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            ImGui.GetWindowWidth() -
            closeSize.X -
            24f);

        DrawDjActionButton(
            "##closeDjGuideFooter",
            FontAwesomeIcon.Times,
            "Close",
            closeSize,
            false,
            () =>
            {
                djConnectionGuideOpen =
                    false;
            });
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
            UiVec(650f, 520f),
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
            UiVec(0f, 12f));

        ImGui.TextWrapped(
            "Caster.fm is our recommended simple option, but Alpha Channel itself only needs a compatible direct audio stream URL.");

        ImGui.Dummy(
            UiVec(0f, 16f));

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
            UiVec(0f, 16f));

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
            UiVec(0f, 10f));

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
            UiVec(650f, 540f),
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
            UiVec(0f, 10f));

        ImGui.TextWrapped(
            "You do not have to use Caster.fm. Other Icecast, Shoutcast or internet radio services can work as long as they provide a compatible direct audio stream URL.");

        ImGui.Dummy(
            UiVec(0f, 16f));

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
            UiVec(0f, 12f));

        ImGui.TextWrapped(
            "Other broadcasting software can also be used. BUTT is a lightweight option if your audio is already being mixed elsewhere.");

        ImGui.Dummy(
            UiVec(0f, 16f));

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
            UiVec(0f, 10f));

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
            UiVec(0f, 12f));

        ImGui.TextWrapped(
            "Your hosting provider may impose its own bitrate limit. If so, use the highest compatible setting allowed by that service.");

        ImGui.Dummy(
            UiVec(0f, 10f));

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
            UiVec(0f, 10f));

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
        Action<int> drawStep,
        Action? onDone = null)
    {
        SetUiFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            title);

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 3f));

        SetUiFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            subtitle);

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 14f));

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
                        Ui(36f))))
                {
                    currentStep = i;
                }
            }
        }

        ImGui.Dummy(
            UiVec(0f, 14f));

        ImGui.Separator();

        ImGui.Dummy(
            UiVec(0f, 8f));

        var footerHeight = Ui(58f);

        // Only the actual guide content scrolls.
        // Navigation stays fixed at the bottom.
        using (ImRaii.PushStyle(
            ImGuiStyleVar.WindowPadding,
            UiVec(16f, 14f)))
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
                    UiVec(0f, 6f));
            }
        }

        ImGui.Separator();

        ImGui.Dummy(
            UiVec(0f, 8f));

        if (currentStep > 0)
        {
            if (ImGui.Button(
                $"Back##{title}",
                UiVec(90f, 32f)))
            {
                currentStep--;
            }
        }
        else
        {
            ImGui.Dummy(
                UiVec(90f, 32f));
        }

        ImGui.SameLine();

        var rightButtonWidth = Ui(100f);

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
                        Ui(32f))))
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
           Ui(32f))))
                {
                    if (onDone is not null)
                    {
                        onDone();
                    }
                    else
                    {
                        ImGui.CloseCurrentPopup();
                    }
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
        SetUiFontScale(
            1.08f);

        ImGui.TextColored(
            Vector4.One,
            title);

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 4f));

        SetUiFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            subtitle);

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 16f));
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
            UiVec(0f, 6f));
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
            UiVec(0f, 6f));
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
            UiVec(0f, 8f));
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
                UiVec(190f, 36f);

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
