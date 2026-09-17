using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

// Settings is a preferences sheet — stacked labeled sections with hairlines, not the same
// CardBg tiles used on Home/Player. Identity: configure the plugin, don't browse content.
internal sealed partial class MainWindow
{
#if DEBUG
    private const string ProductionServerUrl = Configuration.ProductionRelayServerUrl;
    private const string DevServerUrl = "http://194.113.211.29:5001";

    private string serverUrlInput = string.Empty;
    private bool serverUrlSynced;
#endif

    // ---------------------------------------------------------
    // YouTube subscription management
    // ---------------------------------------------------------

    private string subscriptionChannelInput = string.Empty;
    private bool isAddingManualSubscription;
    private string? subscriptionMessage;
    private bool subscriptionMessageIsError;

    private string? subscriptionClipboardMessage;
    private bool subscriptionClipboardMessageIsError;
    private bool manageSubscriptionsOpen;
    private bool manageTopicsOpen;

    private sealed class SubscriptionClipboardPayload
    {
        public string? Type { get; set; }

        public int Version { get; set; }

        public List<SubscriptionClipboardChannel>? Channels { get; set; }
    }

    private sealed class SubscriptionClipboardChannel
    {
        public string? ChannelId { get; set; }

        public string? ChannelName { get; set; }
    }

    private bool topicSelectionLimitWarning;

    private readonly HashSet<string> subscriptionNamesLoading =
    new(StringComparer.OrdinalIgnoreCase);

    private enum SettingsTab
    {
        Account,
        Profile,
        Appearance,
        Other,
    }

    private SettingsTab settingsTab = SettingsTab.Account;

    private void DrawSettings()
    {
        // ---------------------------------------------------------
        // Settings tabs
        // ---------------------------------------------------------

        var tabGap = Ui(8f);

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var tabWidth =
            (availableWidth - (tabGap * 3f)) / 4f;

        DrawSettingsTab(
            SettingsTab.Account,
            "Account",
            tabWidth);

        ImGui.SameLine(0f, tabGap);

        DrawSettingsTab(
            SettingsTab.Profile,
            "Profile",
            tabWidth);

        ImGui.SameLine(0f, tabGap);

        DrawSettingsTab(
            SettingsTab.Appearance,
            "Appearance",
            tabWidth);

        ImGui.SameLine(0f, tabGap);

        DrawSettingsTab(
            SettingsTab.Other,
            "YouTube",
            tabWidth);

        ImGui.Dummy(
            UiVec(0f, 14f));

        // Divider under tabs.
        var dividerOrigin =
            ImGui.GetCursorScreenPos();

        var dividerWidth =
            ImGui.GetContentRegionAvail().X;

        ImGui.GetWindowDrawList()
            .AddRectFilled(
                dividerOrigin,
                dividerOrigin +
                new Vector2(
                    dividerWidth,
                    1f),
                ImGui.GetColorU32(
                    BorderSubtle));

        ImGui.Dummy(
            new Vector2(
                dividerWidth,
                Ui(18f)));

        // ---------------------------------------------------------
        // Selected tab
        // ---------------------------------------------------------

        switch (settingsTab)
        {
            case SettingsTab.Account:
                SettingsSection(
      "Account",
      "Sign-in, username, invite code, and live-streaming credentials.");

                DrawAccountSettings();
                break;

            case SettingsTab.Profile:
                SettingsSection(
                    "Profile",
                    "Your avatar, status, bio, and profile appearance.");

                if (CurrentSession is { } session)
                {
                    DrawProfileEditor(session);
                }
                else
                {
                    DrawPlainEmpty(
                        "Sign in to edit your profile.");
                }

                break;

            case SettingsTab.Appearance:
                DrawAppearanceSettings();
                break;

            case SettingsTab.Other:
                DrawOtherSettings();
                break;
        }
    }

    private async Task LoadSubscriptionChannelNameAsync(
    string channelId)
    {
        try
        {
            var channelName =
                await searchResolver
                    .GetChannelNameAsync(
                        channelId,
                        CancellationToken.None)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(
                    channelName))
            {
                return;
            }

            Plugin.Cfg
                .SubscribedYouTubeChannelNames[
                    channelId] =
                channelName;

            Plugin.Cfg.Save();
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Subscriptions] Failed to cache channel name " +
                $"{channelId}: {exception.Message}");
        }
        finally
        {
            subscriptionNamesLoading.Remove(
                channelId);
        }
    }

    private async Task AddManualYouTubeSubscriptionAsync(
    string requestedChannelName)
    {
        try
        {
            var results =
                await searchResolver
                    .SearchWithMetadataAsync(
                        requestedChannelName,
                        12,
                        CancellationToken.None)
                    .ConfigureAwait(false);

            var matchingVideo =
                results.FirstOrDefault(
                    result =>
                        !string.IsNullOrWhiteSpace(
                            result.ChannelId)
                        &&
                        string.Equals(
                            result.ChannelName.Trim(),
                            requestedChannelName.Trim(),
                            StringComparison.OrdinalIgnoreCase));

            if (matchingVideo is null ||
                string.IsNullOrWhiteSpace(
                    matchingVideo.ChannelId))
            {
                subscriptionMessageIsError =
                    true;

                subscriptionMessage =
                    $"Couldn't find an exact channel named \"{requestedChannelName}\".";

                return;
            }

            var channelId =
                matchingVideo.ChannelId;

            if (Plugin.Cfg
                .SubscribedYouTubeChannelIds
                .Contains(
                    channelId,
                    StringComparer.OrdinalIgnoreCase))
            {
                subscriptionMessageIsError =
                    false;

                subscriptionMessage =
                    $"Already subscribed to {matchingVideo.ChannelName}.";

                subscriptionChannelInput =
                    string.Empty;

                return;
            }

            Plugin.Cfg
                .SubscribedYouTubeChannelIds
                .Add(
                    channelId);

            Plugin.Cfg
                .SubscribedYouTubeChannelNames[
                    channelId] =
                matchingVideo.ChannelName;

            Plugin.Cfg.Save();

            subscriptionChannelInput =
                string.Empty;

            subscriptionMessageIsError =
                false;

            subscriptionMessage =
                $"Subscribed to {matchingVideo.ChannelName}.";
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Subscriptions] Manual subscription lookup failed: " +
                $"{exception.Message}");

            subscriptionMessageIsError =
                true;

            subscriptionMessage =
                "Couldn't search for that YouTube channel.";
        }
        finally
        {
            isAddingManualSubscription =
                false;
        }
    }

    private static void SettingsSection(string title, string blurb)
    {
        ImGui.TextUnformatted(title);
        ImGui.TextColored(MutedText, blurb);
        ImGui.Spacing();
    }

    private void DrawSettingsTab(
    SettingsTab tab,
    string label,
    float width)
    {
        var selected =
            settingsTab == tab;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            selected
                ? Accent
                : new Vector4(
                    0.045f,
                    0.055f,
                    0.09f,
                    1f))
            .Push(
                ImGuiCol.ButtonHovered,
                selected
                    ? AccentHover
                    : new Vector4(
                        0.065f,
                        0.08f,
                        0.125f,
                        1f))
            .Push(
                ImGuiCol.ButtonActive,
                selected
                    ? AccentActive
                    : new Vector4(
                        0.075f,
                        0.09f,
                        0.14f,
                        1f))
            .Push(
                ImGuiCol.Text,
                selected
                    ? Vector4.One
                    : MutedText))
        {
            if (ImGui.Button(
                $"##settingsTab_{tab}",
                new Vector2(
                    width,
                    Ui(40f))))
            {
                settingsTab = tab;
            }

            var buttonMin =
                ImGui.GetItemRectMin();

            var buttonMax =
                ImGui.GetItemRectMax();

            var labelSize =
                ImGui.CalcTextSize(label);

            ImGui.GetWindowDrawList()
                .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    new Vector2(
                        buttonMin.X +
                        (buttonMax.X -
                         buttonMin.X -
                         labelSize.X) *
                        0.5f,

                        buttonMin.Y +
                        (buttonMax.Y -
                         buttonMin.Y -
                         labelSize.Y) *
                        0.5f),
                    ImGui.GetColorU32(
                        selected
                            ? Vector4.One
                            : MutedText),
                    label);
        }
    }

    private void DrawWindowSizeSettings()
    {
        SetUiFontScale(1.10f);
        ImGui.TextColored(Vector4.One, "Window size");
        SetUiFontScale(1f);

        ImGui.Dummy(UiVec(0f, 3f));
        ImGui.TextColored(
            MutedText,
            "Design is the original layout. 4K is capped to the game window so Dalamud UI scale cannot push it off-screen.");

        ImGui.Dummy(UiVec(0f, 12f));

        ReadOnlySpan<UiWindowSizePreset> presets =
        [
            UiWindowSizePreset.Design,
            UiWindowSizePreset.FullHd,
            UiWindowSizePreset.Qhd,
            UiWindowSizePreset.Uhd,
        ];

        var gap = Ui(8f);
        var buttonWidth = MathF.Max(
            Ui(88f),
            (ImGui.GetContentRegionAvail().X - gap * 3f) / 4f);
        var buttonHeight = Ui(34f);

        for (var i = 0; i < presets.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(0f, gap);
            }

            var preset = presets[i];
            var selected = Plugin.Cfg.WindowSizePreset == preset;
            var label = $"{ThemeCatalog.Label(preset)}##windowSize_{preset}";

            using (selected
                ? ImRaii.PushColor(ImGuiCol.Button, Accent)
                    .Push(ImGuiCol.ButtonHovered, AccentHover)
                    .Push(ImGuiCol.ButtonActive, AccentActive)
                : ImRaii.PushColor(ImGuiCol.Button, CardBg)
                    .Push(ImGuiCol.ButtonHovered, CardBgHover)
                    .Push(ImGuiCol.ButtonActive, CardBgHover))
            {
                if (ImGui.Button(label, new Vector2(buttonWidth, buttonHeight)) && !selected)
                {
                    ApplyWindowSizePreset(preset);
                }
            }
        }

        var applied = ClampWindowSize(userWindowSize);
        ImGui.Dummy(UiVec(0f, 10f));
        ImGui.TextColored(
            MutedText,
            Plugin.Cfg.WindowSizePreset == UiWindowSizePreset.Custom
                ? $"Custom  {applied.X:0} × {applied.Y:0}  — drag a window edge to resize"
                : $"Applied  {applied.X:0} × {applied.Y:0}");
    }

    private void DrawAppearanceSettings()
    {
        SettingsSection(
            "Appearance",
            "Colors and window chrome.");

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f))
            .Push(
                ImGuiCol.Border,
                BorderSubtle))
        using (var sizeCard = ImRaii.Child(
            "##appearanceWindowSizeCard",
            new Vector2(-1f, Ui(168f)),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (sizeCard)
            {
                DrawWindowSizeSettings();
            }
        }

        ImGui.Dummy(
            UiVec(0f, 14f));

        // =========================================================
        // ACCENT COLOUR
        // =========================================================

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f))
            .Push(
                ImGuiCol.Border,
                BorderSubtle))
        using (var accentCard = ImRaii.Child(
            "##appearanceAccentCard",
            new Vector2(-1f, Ui(205f)),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (accentCard)
            {
                SetUiFontScale(1.10f);

                ImGui.TextColored(
                    Vector4.One,
                    "Accent colour");

                SetUiFontScale(1f);

                ImGui.Dummy(
                    UiVec(0f, 3f));

                ImGui.TextColored(
                    MutedText,
                    "Choose the highlight colour used for buttons, tabs and selected items.");

                ImGui.Dummy(
                    UiVec(0f, 16f));

                var available =
                    ImGui.GetContentRegionAvail().X;

                // On normal Settings widths, put the controls left
                // and the mini preview on the right.
                var previewWidth = Ui(210f);
                var previewHeight = Ui(82f);
                var previewGap = Ui(24f);

                var optionsWidth =
                    MathF.Max(
                        280f,
                        available -
                        previewWidth -
                        previewGap);

                ImGui.BeginGroup();

                var startX =
                    ImGui.GetCursorPosX();

                ImGui.PushItemWidth(
                    optionsWidth);

                DrawThemeSettings(
                    optionsWidth);

                ImGui.PopItemWidth();

                ImGui.EndGroup();

                ImGui.SameLine(
                    0f,
                    previewGap);

                DrawAccentPreview(
                    new Vector2(
                        previewWidth,
                        previewHeight));
            }
        }

        ImGui.Dummy(
            UiVec(0f, 14f));

        // =========================================================
        // APP BACKGROUND
        // =========================================================

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f))
            .Push(
                ImGuiCol.Border,
                BorderSubtle))
        using (var backgroundCard = ImRaii.Child(
            "##appearanceBackgroundCard",
            new Vector2(-1f, Ui(230f)),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (backgroundCard)
            {
                SetUiFontScale(1.10f);

                ImGui.TextColored(
                    Vector4.One,
                    "Plugin background");

                SetUiFontScale(1f);

                ImGui.Dummy(
                    UiVec(0f, 3f));

                ImGui.TextColored(
                    MutedText,
                    "Choose the background used throughout AlphaChannel.");

                ImGui.Dummy(
                    UiVec(0f, 16f));

                ImGui.TextColored(
                    Vector4.One,
                    "Background style");

                ImGui.Dummy(
                    UiVec(0f, 8f));

                DrawBackgroundSettings();
            }
        }

        ImGui.Dummy(UiVec(0f, 14f));

        DrawOtherSettingsPanel(
            "##appearanceHomeSectionsCard",
            100f,
            () =>
            {
                ImGui.TextUnformatted(
                    "Home sections");

                ImGui.Dummy(UiVec(0f, 10f));

                var showFfxivVideos =
                    Plugin.Cfg.ShowFfxivYouTubeSection;

                if (DrawSettingsToggle(
                        "##showFfxivVideosSection",
                        "FFXIV Videos Section",
                        ref showFfxivVideos))
                {
                    Plugin.Cfg.ShowFfxivYouTubeSection =
                        showFfxivVideos;
                    Plugin.Cfg.Save();
                }
            });
    }

    private void DrawOtherSettings()
    {
        SettingsSection(
            "YouTube Settings",
            "Manage YouTube data and video settings");

        DrawOtherSettingsPanel(
            "##otherYouTubeQualityCard",
            250f,
            () =>
            {
                SettingsSection(
                    "YouTube video quality",
                    "Choose between faster startup or higher-resolution YouTube playback.");

                var preferHighQualityYouTube =
                    Plugin.Cfg.PreferHighQualityYouTubeVideos;

                if (ImGui.Checkbox(
                        "Prefer higher-resolution YouTube videos",
                        ref preferHighQualityYouTube))
                {
                    Plugin.Cfg.PreferHighQualityYouTubeVideos =
                        preferHighQualityYouTube;
                    Plugin.Cfg.Save();
                }

                ImGui.Dummy(UiVec(0f, 6f));

                if (preferHighQualityYouTube)
                {
                    ImGui.TextColored(
                        new Vector4(1.00f, 0.72f, 0.28f, 1.00f),
                        "Higher-resolution mode is enabled.");

                    ImGui.PushTextWrapPos(
                        ImGui.GetCursorPosX() +
                        Math.Max(ImGui.GetContentRegionAvail().X, 200f));
                    ImGui.TextColored(
                        MutedText,
                        "YouTube videos may take between 5–10 seconds longer to begin " +
                        "playing when high resolution is enabled due to the way Alpha " +
                        "Channel has to fetch those videos.");
                    ImGui.PopTextWrapPos();
                }
                else
                {
                    ImGui.PushTextWrapPos(
                        ImGui.GetCursorPosX() +
                        Math.Max(ImGui.GetContentRegionAvail().X, 200f));
                    ImGui.TextColored(
                        MutedText,
                        "When this setting is enabled Alpha Channel will try to fetch higher resolution videos " +
                        "but it can delay the start of the YouTube videos by 2 to 8 seconds.");
                    ImGui.PopTextWrapPos();
                }

                ImGui.Dummy(UiVec(0f, 8f));
                ImGui.TextColored(
                    MutedText,
                    "Changes apply to the next YouTube video.");
            });

        DrawOtherSettingsPanel(
            "##youtubeAccountCard",
            230f,
            () =>
            {
                SettingsSection(
                    "YouTube account",
                    "Connect the account signed into Alpha Channel's browser for videos that require sign-in.");
                DrawEmbeddedBrowserYouTubeAccountSettings();
            });

        var subscriptionMessageRows =
            (isAddingManualSubscription || subscriptionMessage is not null ? 1 : 0) +
            (!string.IsNullOrWhiteSpace(subscriptionClipboardMessage) ? 1 : 0);
        DrawOtherSettingsPanel(
            "##otherYouTubeSubscriptionsCard",
            455f + subscriptionMessageRows * 24f,
            () =>
            {
                SettingsSection(
                    "YouTube subscriptions",
                    "Manage the channels used for your subscription feed.");
                DrawYouTubeSubscriptionSettings();
            });

        DrawOtherSettingsPanel(
            "##otherSubscribedTopicsCard",
            195f,
            () =>
            {
                SettingsSection(
                    "Subscribed Topics",
                    "Choose the topics used for your Home recommendations and the Browse Videos Topics tab.");

                var subscribedTopicCount = GetSubscribedTopicCount();
                ImGui.TextColored(
                    subscribedTopicCount is >= 3 and <= 15
                        ? AccentHover
                        : MutedText,
                    $"{subscribedTopicCount} / 15 topics selected");

                ImGui.SameLine();
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Choose between 3 and 15 topics. These help Alpha Channel show relevant YouTube videos and can be changed here later.");

                ImGui.Dummy(UiVec(0f, 12f));

                DrawDjActionButton(
                    "##manageYouTubeTopics",
                    FontAwesomeIcon.List,
                    "Manage Topics",
                    new Vector2(
                        ImGui.GetContentRegionAvail().X,
                        Ui(36f)),
                    false,
                    () => manageTopicsOpen = true,
                    true);
            });

#if DEBUG
        // Debug-only relay switching. None of this UI or the development
        // endpoint is compiled into public Release builds.
        if (Plugin.Cfg.ShowServerStackSwitcher)
        {
            DrawOtherSettingsPanel(
                "##otherAdvancedCard",
                175f,
                () =>
                {
                    SettingsSection(
                        "Advanced",
                        "Prod vs isolated dev relay.");
                    DrawServerSettings();
                });
        }
#endif
    }

    private void DrawOtherSettingsPanel(
        string id,
        float height,
        Action draw)
    {
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f))
            .Push(
                ImGuiCol.Border,
                BorderSubtle))
        using (var panel = ImRaii.Child(
            id,
            new Vector2(-1f, Ui(height)),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (panel)
                draw();
        }

        ImGui.Dummy(UiVec(0f, 14f));
    }

    private static bool DrawSettingsToggle(
        string id,
        string label,
        ref bool value)
    {
        var switchSize = UiVec(38f, 20f);
        var switchOrigin = ImGui.GetCursorScreenPos();
        var changed = false;

        if (ImGui.InvisibleButton(
                id,
                switchSize))
        {
            value = !value;
            changed = true;
        }

        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            switchOrigin,
            switchOrigin + switchSize,
            ImGui.GetColorU32(
                value
                    ? Accent
                    : hovered
                        ? new Vector4(0.22f, 0.20f, 0.29f, 1f)
                        : new Vector4(0.14f, 0.13f, 0.19f, 1f)),
            switchSize.Y * 0.5f);

        var knobCenter = new Vector2(
            value
                ? switchOrigin.X + switchSize.X - switchSize.Y * 0.5f
                : switchOrigin.X + switchSize.Y * 0.5f,
            switchOrigin.Y + switchSize.Y * 0.5f);

        drawList.AddCircleFilled(
            knobCenter,
            Ui(7f),
            ImGui.GetColorU32(Vector4.One),
            18);

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        ImGui.SameLine(0f, Ui(9f));
        ImGui.TextUnformatted(label);

        if (ImGui.IsItemClicked())
        {
            value = !value;
            changed = true;
        }

        return changed;
    }

    private void DrawYouTubeSubscriptionSettings()
    {
        // ---------------------------------------------------------
        // Add channel manually
        // ---------------------------------------------------------

        ImGui.TextColored(
            Vector4.One,
            "Subscribe to a channel");

        ImGui.Dummy(
            UiVec(0f, 4f));

        ImGui.TextColored(
            MutedText,
            "Enter the exact YouTube channel name.");

        ImGui.Dummy(
            UiVec(0f, 8f));

        var buttonWidth = Ui(110f);
        const float gap = 10f;

        ImGui.SetNextItemWidth(
            ImGui.GetContentRegionAvail().X -
            buttonWidth -
            gap);

        bool submitted;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                UiVec(12f, 9f)))
        {
            submitted =
                ImGui.InputTextWithHint(
                    "##manualYouTubeSubscription",
                    "Channel name...",
                    ref subscriptionChannelInput,
                    128,
                    ImGuiInputTextFlags.EnterReturnsTrue);
        }

        ImGui.SameLine(
            0f,
            gap);

        bool clicked;

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
        using (ImRaii.Disabled(
            isAddingManualSubscription ||
            string.IsNullOrWhiteSpace(
                subscriptionChannelInput)))
        {
            clicked =
                ImGui.Button(
                    "Subscribe##manualYouTubeSubscription",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        if ((submitted || clicked) &&
            !isAddingManualSubscription &&
            !string.IsNullOrWhiteSpace(
                subscriptionChannelInput))
        {
            var channelName =
                subscriptionChannelInput.Trim();

            subscriptionMessage = null;
            isAddingManualSubscription = true;

            _ = AddManualYouTubeSubscriptionAsync(
                channelName);
        }

        if (isAddingManualSubscription)
        {
            ImGui.Dummy(
                UiVec(0f, 6f));

            ImGui.TextColored(
                MutedText,
                "Finding channel...");
        }
        else if (subscriptionMessage is not null)
        {
            ImGui.Dummy(
                UiVec(0f, 6f));

            ImGui.TextColored(
                subscriptionMessageIsError
                    ? Danger
                    : Good,
                subscriptionMessage);
        }

        ImGui.Dummy(
            UiVec(0f, 18f));

        // ---------------------------------------------------------
        // Current subscriptions
        // ---------------------------------------------------------

        ImGui.TextColored(
            Vector4.One,
            "Subscribed channels");

        ImGui.Dummy(
            UiVec(0f, 5f));

        using (ImRaii.PushColor(
            ImGuiCol.Text,
            MutedText))
        {
            ImGui.TextWrapped(
                "Subscriptions are stored locally. If you move devices or fully delete the plugin, you may want to export your subscriptions first. You can also use an export to share subscriptions with friends.");
        }

        ImGui.Dummy(
            UiVec(0f, 9f));

        DrawDjActionButton(
            "##manageYouTubeSubscriptions",
            FontAwesomeIcon.List,
            "Manage Subscriptions",
            new Vector2(
                ImGui.GetContentRegionAvail().X,
                Ui(36f)),
            false,
            () => manageSubscriptionsOpen = true,
            true);

        ImGui.Dummy(
            UiVec(0f, 9f));

        var clipboardGap = Ui(10f);
        var clipboardButtonWidth =
            (ImGui.GetContentRegionAvail().X - clipboardGap) * 0.5f;

        DrawDjActionButton(
            "##exportYouTubeSubscriptions",
            FontAwesomeIcon.Copy,
            "Export subscriptions",
            new Vector2(
                clipboardButtonWidth,
                Ui(34f)),
            false,
            ExportYouTubeSubscriptionsToClipboard,
            true);

        ImGui.SameLine(
            0f,
            clipboardGap);

        DrawDjActionButton(
            "##importYouTubeSubscriptions",
            FontAwesomeIcon.Clipboard,
            "Import subscriptions",
            new Vector2(
                clipboardButtonWidth,
                Ui(34f)),
            false,
            ImportYouTubeSubscriptionsFromClipboard,
            true);

        if (!string.IsNullOrWhiteSpace(
                subscriptionClipboardMessage))
        {
            ImGui.Dummy(
                UiVec(0f, 6f));

            ImGui.TextColored(
                subscriptionClipboardMessageIsError
                    ? Danger
                    : Good,
                subscriptionClipboardMessage);
        }

    }

    private void DrawYouTubeSubscriptionsOverlay()
    {
        if (!manageSubscriptionsOpen)
        {
            return;
        }

        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelWidth = MathF.Min(
            Ui(800f),
            parentSize.X - Ui(40f));
        var panelHeight = MathF.Min(
            Ui(580f),
            parentSize.Y - Ui(40f));
        var panelPosition = parentPosition +
            new Vector2(
                (parentSize.X - panelWidth) * 0.5f,
                (parentSize.Y - panelHeight) * 0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
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
                "##youtubeSubscriptionsOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        ImGui.GetWindowDrawList().AddRectFilled(
            parentPosition,
            parentPosition + parentSize,
            ImGui.GetColorU32(
                new Vector4(0f, 0f, 0f, 0.58f)));

        ImGui.SetCursorScreenPos(panelPosition);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            Ui(14f))
            .Push(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f))
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(22f, 20f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.025f, 0.03f, 0.06f, 0.995f))
            .Push(
                ImGuiCol.Border,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.82f)))
        using (var panel = ImRaii.Child(
            "##youtubeSubscriptionsPanel",
            new Vector2(panelWidth, panelHeight),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (panel)
            {
                DrawYouTubeSubscriptionsOverlayContents();
            }
        }

        ImGui.End();
    }

    private void DrawYouTubeSubscriptionsOverlayContents()
    {
        var subscriptions = Plugin.Cfg.SubscribedYouTubeChannelIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        SetUiFontScale(1.15f);
        ImGui.TextColored(
            Vector4.One,
            "Manage Subscriptions");
        SetUiFontScale(1f);

        ImGui.TextColored(
            MutedText,
            subscriptions.Count == 1
                ? "1 subscribed YouTube channel"
                : $"{subscriptions.Count} subscribed YouTube channels");

        ImGui.Dummy(UiVec(0f, 8f));
        ImGui.Separator();
        ImGui.Dummy(UiVec(0f, 10f));

        var listHeight = MathF.Max(
            Ui(120f),
            ImGui.GetContentRegionAvail().Y - Ui(58f));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            Ui(9f))
            .Push(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f))
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(8f, 8f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.018f, 0.024f, 0.046f, 1f))
            .Push(
                ImGuiCol.Border,
                BorderSubtle))
        using (var list = ImRaii.Child(
            "##youtubeSubscriptionsList",
            new Vector2(-1f, listHeight),
            true))
        {
            if (list)
            {
                if (subscriptions.Count == 0)
                {
                    ImGui.Dummy(UiVec(0f, 12f));
                    ImGui.TextColored(
                        MutedText,
                        "You aren't subscribed to any channels yet.");
                }
                else
                {
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.CellPadding,
                        UiVec(5f, 5f)))
                    {
                        if (ImGui.BeginTable(
                                "##youtubeSubscriptionsGrid",
                                2,
                                ImGuiTableFlags.SizingStretchSame))
                        {
                            foreach (var channelId in subscriptions)
                            {
                                ImGui.TableNextColumn();
                                ImGui.PushID(
                                    $"managedSubscription_{channelId}");
                                DrawYouTubeSubscriptionSettingsRow(
                                    channelId);
                                ImGui.PopID();
                            }

                            ImGui.EndTable();
                        }
                    }
                }
            }
        }

        ImGui.Dummy(UiVec(0f, 12f));

        var closeSize = UiVec(120f, 36f);
        ImGui.SetCursorPosX(
            ImGui.GetWindowWidth() - closeSize.X - Ui(22f));
        DrawDjActionButton(
            "##closeYouTubeSubscriptions",
            FontAwesomeIcon.Times,
            "Close",
            closeSize,
            false,
            () => manageSubscriptionsOpen = false,
            true);
    }

    private void DrawYouTubeTopicsOverlay()
    {
        if (!manageTopicsOpen)
        {
            return;
        }

        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelWidth = MathF.Min(
            Ui(960f),
            parentSize.X - Ui(40f));
        var panelHeight = MathF.Min(
            Ui(620f),
            parentSize.Y - Ui(40f));
        var panelPosition = parentPosition +
            new Vector2(
                (parentSize.X - panelWidth) * 0.5f,
                (parentSize.Y - panelHeight) * 0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
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
                "##youtubeTopicsOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        ImGui.GetWindowDrawList().AddRectFilled(
            parentPosition,
            parentPosition + parentSize,
            ImGui.GetColorU32(
                new Vector4(0f, 0f, 0f, 0.58f)));

        ImGui.SetCursorScreenPos(panelPosition);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            Ui(14f))
            .Push(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f))
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(22f, 20f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.025f, 0.03f, 0.06f, 0.995f))
            .Push(
                ImGuiCol.Border,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.82f)))
        using (var panel = ImRaii.Child(
            "##youtubeTopicsPanel",
            new Vector2(panelWidth, panelHeight),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (panel)
            {
                DrawYouTubeTopicsOverlayContents();
            }
        }

        ImGui.End();
    }

    private void DrawYouTubeTopicsOverlayContents()
    {
        SetUiFontScale(1.15f);
        ImGui.TextColored(
            Vector4.One,
            "Manage Topics");
        SetUiFontScale(1f);

        ImGui.TextColored(
            MutedText,
            "Choose the topics used for your Home recommendations and the Browse Videos Topics tab.");

        ImGui.Dummy(UiVec(0f, 8f));

        var subscribedTopicCount = GetSubscribedTopicCount();
        ImGui.TextColored(
            subscribedTopicCount is >= 3 and <= 15
                ? AccentHover
                : new Vector4(1f, 0.55f, 0.35f, 1f),
            $"{subscribedTopicCount} / 15 topics selected");

        if (topicSelectionLimitWarning)
        {
            ImGui.SameLine(0f, Ui(12f));
            ImGui.TextColored(
                new Vector4(1f, 0.55f, 0.35f, 1f),
                "You can select up to 15 topics.");
        }

        ImGui.Dummy(UiVec(0f, 8f));
        ImGui.Separator();
        ImGui.Dummy(UiVec(0f, 10f));

        var topicGridHeight = MathF.Max(
            Ui(250f),
            ImGui.GetContentRegionAvail().Y - Ui(58f));
        var topicSignatureBefore =
            GetBrowseTopicSignature(GetEnabledTrendingTopics());

        DrawTrendingTopicTags(
            columnHeight: topicGridHeight,
            availableWidthOverride: ImGui.GetContentRegionAvail().X);

        var topicSignatureAfter =
            GetBrowseTopicSignature(GetEnabledTrendingTopics());

        if (!string.Equals(
                topicSignatureBefore,
                topicSignatureAfter,
                StringComparison.Ordinal))
        {
            NotifyTopicSettingsChanged();
        }

        ImGui.Dummy(UiVec(0f, 12f));

        var closeSize = UiVec(120f, 36f);
        ImGui.SetCursorPosX(
            ImGui.GetWindowWidth() - closeSize.X - Ui(22f));
        DrawDjActionButton(
            "##closeYouTubeTopics",
            FontAwesomeIcon.Times,
            "Close",
            closeSize,
            false,
            () =>
            {
                manageTopicsOpen = false;
                topicSelectionLimitWarning = false;
            },
            true);
    }

    private void ExportYouTubeSubscriptionsToClipboard()
    {
        var channels =
            Plugin.Cfg.SubscribedYouTubeChannelIds
                .Where(
                    channelId =>
                        !string.IsNullOrWhiteSpace(
                            channelId))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    channelId =>
                    {
                        Plugin.Cfg.SubscribedYouTubeChannelNames.TryGetValue(
                            channelId,
                            out var channelName);

                        return new SubscriptionClipboardChannel
                        {
                            ChannelId = channelId.Trim(),
                            ChannelName = string.IsNullOrWhiteSpace(channelName)
                                ? null
                                : channelName.Trim(),
                        };
                    })
                .ToList();

        var payload =
            new SubscriptionClipboardPayload
            {
                Type = "AlphaChannelYouTubeSubscriptions",
                Version = 1,
                Channels = channels,
            };

        ImGui.SetClipboardText(
            JsonSerializer.Serialize(payload));

        subscriptionClipboardMessage =
            channels.Count == 1
                ? "1 subscription copied to the clipboard."
                : $"{channels.Count} subscriptions copied to the clipboard.";

        subscriptionClipboardMessageIsError =
            false;
    }

    private void ImportYouTubeSubscriptionsFromClipboard()
    {
        try
        {
            var clipboardText =
                ImGui.GetClipboardText();

            if (string.IsNullOrWhiteSpace(
                    clipboardText))
            {
                SetSubscriptionClipboardError(
                    "The clipboard is empty.");

                return;
            }

            var payload =
                JsonSerializer.Deserialize<SubscriptionClipboardPayload>(
                    clipboardText);

            if (payload is null ||
                !string.Equals(
                    payload.Type,
                    "AlphaChannelYouTubeSubscriptions",
                    StringComparison.Ordinal) ||
                payload.Version != 1 ||
                payload.Channels is null ||
                payload.Channels.Count > 1000)
            {
                SetSubscriptionClipboardError(
                    "The clipboard does not contain a valid Alpha Channel subscriptions list.");

                return;
            }

            var importedChannels =
                new Dictionary<string, string?>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var channel in payload.Channels)
            {
                if (channel is null)
                {
                    SetSubscriptionClipboardError(
                        "The clipboard does not contain a valid Alpha Channel subscriptions list.");

                    return;
                }

                var channelId =
                    channel.ChannelId?.Trim();

                var channelName =
                    channel.ChannelName?.Trim();

                if (string.IsNullOrWhiteSpace(channelId) ||
                    channelId.Length > 200 ||
                    channelName?.Length > 300)
                {
                    SetSubscriptionClipboardError(
                        "The clipboard does not contain a valid Alpha Channel subscriptions list.");

                    return;
                }

                importedChannels.TryAdd(
                    channelId,
                    string.IsNullOrWhiteSpace(channelName)
                        ? null
                        : channelName);
            }

            var existingIds =
                new HashSet<string>(
                    Plugin.Cfg.SubscribedYouTubeChannelIds,
                    StringComparer.OrdinalIgnoreCase);

            var addedCount = 0;
            var existingCount = 0;
            var changed = false;

            foreach (var (channelId, channelName) in importedChannels)
            {
                if (existingIds.Add(channelId))
                {
                    Plugin.Cfg.SubscribedYouTubeChannelIds.Add(
                        channelId);

                    addedCount++;
                    changed = true;
                }
                else
                {
                    existingCount++;
                }

                if (!string.IsNullOrWhiteSpace(channelName) &&
                    (!Plugin.Cfg.SubscribedYouTubeChannelNames.TryGetValue(
                         channelId,
                         out var savedName) ||
                     string.IsNullOrWhiteSpace(savedName)))
                {
                    Plugin.Cfg.SubscribedYouTubeChannelNames[channelId] =
                        channelName;

                    changed = true;
                }
            }

            if (changed)
            {
                Plugin.Cfg.Save();
            }

            subscriptionClipboardMessageIsError =
                false;

            subscriptionClipboardMessage =
                addedCount switch
                {
                    0 when importedChannels.Count == 0 =>
                        "The imported subscriptions list is empty.",
                    0 =>
                        "All imported subscriptions were already in your list.",
                    1 when existingCount > 0 =>
                        $"Added 1 subscription; {existingCount} already existed.",
                    1 =>
                        "Added 1 subscription.",
                    _ when existingCount > 0 =>
                        $"Added {addedCount} subscriptions; {existingCount} already existed.",
                    _ =>
                        $"Added {addedCount} subscriptions.",
                };
        }
        catch (JsonException)
        {
            SetSubscriptionClipboardError(
                "The clipboard does not contain a valid Alpha Channel subscriptions list.");
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Subscriptions] Clipboard import failed: {exception.Message}");

            SetSubscriptionClipboardError(
                "The subscriptions list could not be imported.");
        }
    }

    private void SetSubscriptionClipboardError(
        string message)
    {
        subscriptionClipboardMessage =
            message;

        subscriptionClipboardMessageIsError =
            true;
    }

    private void DrawYouTubeSubscriptionSettingsRow(
    string channelId)
    {
        var rowHeight = Ui(46f);
        var removeSize = Ui(32f);

        var origin =
            ImGui.GetCursorScreenPos();

        var width =
            ImGui.GetContentRegionAvail().X;

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin +
            new Vector2(
                width,
                rowHeight),
            ImGui.GetColorU32(
                new Vector4(
                    0.045f,
                    0.06f,
                    0.10f,
                    1f)),
            8f);

        drawList.AddRect(
            origin,
            origin +
            new Vector2(
                width,
                rowHeight),
            ImGui.GetColorU32(
                BorderSubtle),
            8f);

        var hasSavedName =
     Plugin.Cfg.SubscribedYouTubeChannelNames
         .TryGetValue(
             channelId,
             out var savedName)
     &&
     !string.IsNullOrWhiteSpace(
         savedName);

        var channelName =
            hasSavedName
                ? savedName!
                : "Loading channel...";

        if (!hasSavedName &&
            subscriptionNamesLoading.Add(
                channelId))
        {
            _ = LoadSubscriptionChannelNameAsync(
                channelId);
        }

        // Channel icon
        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                origin +
                UiVec(12f, 13f),
                ImGui.GetColorU32(
                    AccentHover),
                FontAwesomeIcon.User.ToIconString());
        }

        var channelTextPosition =
            origin + UiVec(34f, 14f);

        drawList.PushClipRect(
            channelTextPosition,
            new Vector2(
                origin.X + width - removeSize - Ui(16f),
                origin.Y + rowHeight),
            true);
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(),
            channelTextPosition,
            ImGui.GetColorU32(
                Vector4.One),
            channelName);
        drawList.PopClipRect();

        // Remove button
        ImGui.SetCursorScreenPos(
            new Vector2(
                origin.X +
                width -
                removeSize -
                Ui(7f),
                origin.Y + Ui(7f)));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            7f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            new Vector4(
                0.09f,
                0.05f,
                0.07f,
                1f))
            .Push(
                ImGuiCol.ButtonHovered,
                new Vector4(
                    0.18f,
                    0.07f,
                    0.09f,
                    1f))
            .Push(
                ImGuiCol.ButtonActive,
                new Vector4(
                    0.22f,
                    0.08f,
                    0.10f,
                    1f)))
        {
            bool removeClicked;
            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                removeClicked = ImGui.Button(
                    $"{FontAwesomeIcon.Trash.ToIconString()}##removeSubscription",
                    new Vector2(
                        removeSize,
                        removeSize));
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"Remove {channelName}");
            }

            if (removeClicked)
            {
                Plugin.Cfg
                    .SubscribedYouTubeChannelIds
                    .RemoveAll(
                        id => string.Equals(
                            id,
                            channelId,
                            StringComparison.OrdinalIgnoreCase));

                Plugin.Cfg
                    .SubscribedYouTubeChannelNames
                    .Remove(
                        channelId);

                Plugin.Cfg.Save();
            }
        }

        // Invisible spacer so ImGui advances past our
        // manually-drawn row.
        ImGui.SetCursorScreenPos(
            origin);

        ImGui.Dummy(
            new Vector2(
                width,
                rowHeight));
    }

    private static void SettingsHairline()
    {
        ImGui.Dummy(UiVec(0f, 12f));

        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        ImGui.GetWindowDrawList().AddRectFilled(
            origin,
            origin + new Vector2(width, 1f),
            ImGui.GetColorU32(BorderSubtle));

        ImGui.Dummy(new Vector2(width, Ui(12f)));
    }

    private void DrawThemeSettings(
    float availableWidth)
    {
        const float gap = 12f;
        var buttonSize = Ui(38f);

        var totalWidth =
            (buttonSize * 4f) +
            (gap * 3f);

        var startX =
            ImGui.GetCursorPosX() +
            MathF.Max(
                0f,
                (availableWidth - totalWidth) * 0.5f);

        ImGui.SetCursorPosX(startX);

        DrawThemeOption(
            UiTheme.Purple,
            Hex(0x8B5CF6),
            buttonSize);

        ImGui.SameLine(0f, gap);

        DrawThemeOption(
            UiTheme.Gold,
            Hex(0xD4AF37),
            buttonSize);

        ImGui.SameLine(0f, gap);

        DrawThemeOption(
            UiTheme.Green,
            Hex(0x34D399),
            buttonSize);

        ImGui.SameLine(0f, gap);

        DrawThemeOption(
            UiTheme.Red,
            Hex(0xE11D48),
            buttonSize);
    }


    private void DrawBackgroundSettings()
    {
        const float gap = 8f;

        var available =
            ImGui.GetContentRegionAvail().X;

        // Six built-in background styles.
        var width =
            (available -
             gap * 5f) / 6f;

        DrawBackgroundOption(
            UiBackground.Theme,
            width);

        ImGui.SameLine(
            0f,
            gap);

        DrawBackgroundOption(
            UiBackground.Midnight,
            width);

        ImGui.SameLine(
            0f,
            gap);

        DrawBackgroundOption(
            UiBackground.Void,
            width);

        ImGui.SameLine(
            0f,
            gap);

        DrawBackgroundOption(
            UiBackground.Slate,
            width);

        ImGui.SameLine(
            0f,
            gap);

        DrawBackgroundOption(
            UiBackground.Warm,
            width);

        ImGui.SameLine(
            0f,
            gap);

        DrawBackgroundOption(
            UiBackground.Carbon,
            width);
    }


    private void DrawCustomBackgroundSettings()
    {
        if (!customBackgroundPathSynced)
        {
            customBackgroundPathInput =
                Plugin.Cfg.CustomBackgroundPath ??
                string.Empty;

            customBackgroundPathSynced =
                true;
        }

        ImGui.TextColored(
            Vector4.One,
            "Custom background image");

        ImGui.Dummy(
            UiVec(0f, 3f));

        ImGui.TextColored(
            MutedText,
            "Add your own image instead of using a built-in background style.");

        ImGui.Dummy(
            UiVec(0f, 9f));

        // ---------------------------------------------------------
        // Path + Apply
        // ---------------------------------------------------------

        var applyWidth = Ui(82f);
        var inputGap = Ui(10f);

        ImGui.SetNextItemWidth(
            ImGui.GetContentRegionAvail().X -
            applyWidth -
            inputGap);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                UiVec(12f, 9f)))
        using (ImRaii.PushColor(
            ImGuiCol.FrameBg,
            new Vector4(
                0.055f,
                0.07f,
                0.115f,
                1f))
            .Push(
                ImGuiCol.FrameBgHovered,
                new Vector4(
                    0.07f,
                    0.09f,
                    0.145f,
                    1f))
            .Push(
                ImGuiCol.FrameBgActive,
                new Vector4(
                    0.07f,
                    0.09f,
                    0.145f,
                    1f)))
        {
            ImGui.InputTextWithHint(
                "##customBgPath",
                "/path/to/image.png",
                ref customBackgroundPathInput,
                512);
        }

        ImGui.SameLine(
            0f,
            inputGap);

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
            if (ImGui.Button(
                "Apply##customBg",
                new Vector2(
                    applyWidth,
                    Ui(36f))))
            {
                TryApplyCustomBackgroundFromPath(
                    customBackgroundPathInput);
            }
        }

        ImGui.Dummy(
            UiVec(0f, 10f));

        // ---------------------------------------------------------
        // Action tiles
        // ---------------------------------------------------------
        var actionAvailable =
    ImGui.GetContentRegionAvail().X;

        var actionGap = Ui(10f);
        var actionInset = Ui(8f);

        var actionWidth =
            MathF.Min(
                190f,
                (actionAvailable -
                 actionInset * 2f -
                 actionGap) * 0.5f);

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() +
            actionInset);

        if (DrawProfileActionButton(
            FontAwesomeIcon.FolderOpen,
            "Newest image",
            "In Downloads",
            Accent,
            width: actionWidth))
        {
            var found =
                FindImageInDownloads();

            if (found is null)
            {
                customBackgroundError =
                    "No image found in Downloads.";
            }
            else
            {
                customBackgroundPathInput =
                    found;

                TryApplyCustomBackgroundFromPath(
                    found);
            }
        }

        ImGui.SameLine(
            0f,
            actionGap);

        if (DrawProfileActionButton(
            FontAwesomeIcon.Trash,
            "Remove image",
            "Return to selected style",
            Hex(0xF87171),
            disabled:
                string.IsNullOrEmpty(
                    Plugin.Cfg.CustomBackgroundPath),
            width:
                actionWidth))
        {
            ClearCustomBackground();
        }
        ImGui.Dummy(UiVec(0f, 10f));
        // ---------------------------------------------------------
        // Dim amount
        // ---------------------------------------------------------

        if (Plugin.Cfg.UiBackground ==
                UiBackground.Custom ||
            !string.IsNullOrEmpty(
                Plugin.Cfg.CustomBackgroundPath))
        {
            ImGui.Dummy(
                UiVec(0f, 11f));

            var dim =
                Plugin.Cfg.CustomBackgroundDim;

            ImGui.SetNextItemWidth(
                180f);

            if (ImGui.SliderFloat(
                "Dim##customBgDim",
                ref dim,
                0f,
                0.85f,
                "%.2f"))
            {
                Plugin.Cfg.CustomBackgroundDim =
                    dim;
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                Plugin.Cfg.Save();
            }

            ImGui.SameLine(
                0f,
                10f);

            ImGui.TextColored(
                MutedText,
                "Higher = darker overlay");
        }

        // ---------------------------------------------------------
        // Feedback
        // ---------------------------------------------------------

        if (customBackgroundError is { } error)
        {
            ImGui.Dummy(
                UiVec(0f, 5f));

            ImGui.TextColored(
                Danger,
                error);
        }
        else if (
            Plugin.Cfg.UiBackground ==
                UiBackground.Custom &&
            customBackground is not null)
        {
            ImGui.Dummy(
                UiVec(0f, 5f));

            ImGui.TextColored(
                Good,
                "Custom background active.");
        }
    }


    private void DrawHomeHeroSettings()
    {
        if (!customHomeHeroPathSynced)
        {
            customHomeHeroPathInput =
                Plugin.Cfg.CustomHomeHeroPath ??
                string.Empty;

            customHomeHeroPathSynced =
                true;
        }

        if (Plugin.Cfg.ShowHomeHeroImage)
        {
            EnsureHomeHeroLoaded();
        }

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f))
            .Push(
                ImGuiCol.Border,
                BorderSubtle))
        using (var heroCard = ImRaii.Child(
            "##appearanceHomeHeroCard",
            new Vector2(-1f, Ui(445f)),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!heroCard)
            {
                return;
            }

            SetUiFontScale(
                1.10f);

            ImGui.TextColored(
                Vector4.One,
                "Home illustration");

            SetUiFontScale(
                1f);

            ImGui.Dummy(
                UiVec(0f, 3f));

            ImGui.TextColored(
                MutedText,
                "Control the artwork shown beside Welcome on the Home page.");

            ImGui.Dummy(
                UiVec(0f, 13f));

            // -----------------------------------------------------
            // Left side controls
            // -----------------------------------------------------

            var available =
                ImGui.GetContentRegionAvail().X;

            var previewWidth = Ui(170f);
            var previewHeight = Ui(170f);
            var previewGap = Ui(18f);

            var controlsWidth =
                MathF.Max(
                    280f,
                    available -
                    previewWidth -
                    previewGap);

            ImGui.BeginGroup();

            var showHero =
                Plugin.Cfg.ShowHomeHeroImage;

            if (ImGui.Checkbox(
                "Show illustration",
                ref showHero))
            {
                Plugin.Cfg.ShowHomeHeroImage =
                    showHero;

                Plugin.Cfg.Save();

                if (showHero)
                {
                    EnsureHomeHeroLoaded();
                }
            }

            ImGui.Dummy(
                UiVec(0f, 13f));

            ImGui.TextColored(
                Vector4.One,
                "Illustration image");

            ImGui.Dummy(
                UiVec(0f, 3f));

            ImGui.TextColored(
                MutedText,
                "Use the default AlphaChannel artwork or choose your own.");

            ImGui.Dummy(
                UiVec(0f, 8f));

            using (ImRaii.Disabled(
                !Plugin.Cfg.ShowHomeHeroImage))
            {
                var applyWidth = Ui(82f);
                var inputGap = Ui(10f);

                ImGui.SetNextItemWidth(
                    controlsWidth -
                    applyWidth -
                    inputGap);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    8f)
                    .Push(
                        ImGuiStyleVar.FramePadding,
                        UiVec(12f, 9f)))
                using (ImRaii.PushColor(
                    ImGuiCol.FrameBg,
                    new Vector4(
                        0.055f,
                        0.07f,
                        0.115f,
                        1f))
                    .Push(
                        ImGuiCol.FrameBgHovered,
                        new Vector4(
                            0.07f,
                            0.09f,
                            0.145f,
                            1f))
                    .Push(
                        ImGuiCol.FrameBgActive,
                        new Vector4(
                            0.07f,
                            0.09f,
                            0.145f,
                            1f)))
                {
                    ImGui.InputTextWithHint(
                        "##customHomeHeroPath",
                        "/path/to/image.png",
                        ref customHomeHeroPathInput,
                        512);
                }

                ImGui.SameLine(
                    0f,
                    inputGap);

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
                    if (ImGui.Button(
                        "Apply##homeHero",
                        new Vector2(
                            applyWidth,
                            Ui(36f))))
                    {
                        TryApplyCustomHomeHeroFromPath(
                            customHomeHeroPathInput);
                    }
                }

                ImGui.Dummy(
                    UiVec(0f, 10f));

                var actionGap = Ui(10f);
                var actionInset = Ui(6f);

                var actionWidth =
                    MathF.Min(
                        185f,
                        (controlsWidth -
                         actionInset * 2f -
                         actionGap) * 0.5f);

                ImGui.SetCursorPosX(
                    ImGui.GetCursorPosX() +
                    actionInset);

                if (DrawProfileActionButton(
                    FontAwesomeIcon.FolderOpen,
                    "Newest image",
                    "In Downloads",
                    Accent,
                    width:
                        actionWidth))
                {
                    var found =
                        FindImageInDownloads();

                    if (found is null)
                    {
                        customHomeHeroError =
                            "No image found in Downloads.";
                    }
                    else
                    {
                        customHomeHeroPathInput =
                            found;

                        TryApplyCustomHomeHeroFromPath(
                            found);
                    }
                }

                ImGui.SameLine(
                    0f,
                    actionGap);

                if (DrawProfileActionButton(
                    FontAwesomeIcon.Image,
                    "Use default",
                    "Default image",
                    MutedText,
                    disabled:
                        string.IsNullOrEmpty(
                            Plugin.Cfg.CustomHomeHeroPath),
                    width:
                        actionWidth))
                {
                    ClearCustomHomeHero();
                }
                ImGui.Dummy(UiVec(0f, 10f));
            }

            if (customHomeHeroError is { } error)
            {
                ImGui.Dummy(
                    UiVec(0f, 7f));

                ImGui.TextColored(
                    Danger,
                    error);
            }
            else if (
                !string.IsNullOrEmpty(
                    Plugin.Cfg.CustomHomeHeroPath) &&
                Plugin.Cfg.ShowHomeHeroImage)
            {
                ImGui.Dummy(
                    UiVec(0f, 7f));

                ImGui.TextColored(
                    Good,
                    "Using your Home illustration.");
            }

            ImGui.EndGroup();

            // -----------------------------------------------------
            // Right side preview
            // -----------------------------------------------------

            ImGui.SameLine(
                0f,
                previewGap);

            DrawHomeHeroPreview(
                new Vector2(
                    previewWidth,
                    previewHeight));
        }
    }


    private void DrawBackgroundOption(
        UiBackground background,
        float width)
    {
        var selected =
            Plugin.Cfg.UiBackground ==
            background;

        var label =
            ThemeCatalog.Label(
                background);

        var swatch =
            background == UiBackground.Theme
                ? ThemeCatalog
                    .Get(Plugin.Cfg.UiTheme)
                    .WindowBg
                : ThemeCatalog.Swatch(
                    background);

        var size =
            new Vector2(
                width,
                Ui(38f));

        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.PushID(
            (int)background + 100);

        var clicked =
            ImGui.InvisibleButton(
                "##bg",
                size);

        var hovered =
            ImGui.IsItemHovered();

        ImGui.PopID();

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                selected
                    ? new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.18f)
                    : hovered
                        ? CardBgHover
                        : CardBg),
            8f);

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                selected
                    ? Accent
                    : BorderSubtle),
            8f,
            ImDrawFlags.None,
            selected
                ? 1.5f
                : 1f);

        var circleCenter =
            origin +
            new Vector2(
                Ui(17f),
                size.Y * 0.5f);

        drawList.AddCircleFilled(
            circleCenter,
            6.5f,
            ImGui.GetColorU32(
                swatch));

        drawList.AddCircle(
            circleCenter,
            6.5f,
            ImGui.GetColorU32(
                new Vector4(
                    1f,
                    1f,
                    1f,
                    0.25f)),
            0,
            1f);

        var labelSize =
            ImGui.CalcTextSize(
                label);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X + Ui(29f),
                origin.Y +
                (size.Y -
                 labelSize.Y) * 0.5f),
            ImGui.GetColorU32(
                selected
                    ? Vector4.One
                    : MutedText),
            label);

        if (clicked &&
            !selected)
        {
            Plugin.Cfg.UiBackground =
                background;

            Plugin.Cfg.Save();

            Colors =
                ThemeCatalog.Get(
                    Plugin.Cfg.UiTheme,
                    background);
        }
    }


    private void DrawThemeOption(
     UiTheme theme,
     Vector4 swatch,
     float size)
    {
        var selected =
            Plugin.Cfg.UiTheme == theme;

        var origin =
            ImGui.GetCursorScreenPos();

        var buttonSize =
            new Vector2(size, size);

        ImGui.PushID((int)theme);

        var clicked =
            ImGui.InvisibleButton(
                "##theme",
                buttonSize);

        var hovered =
            ImGui.IsItemHovered();

        ImGui.PopID();

        var drawList =
            ImGui.GetWindowDrawList();

        var center =
            origin +
            buttonSize * 0.5f;

        var outerRadius =
            size * 0.5f;

        var innerRadius =
            selected
                ? 10f
                : 9f;

        // Button background.
        drawList.AddCircleFilled(
            center,
            outerRadius,
            ImGui.GetColorU32(
                hovered
                    ? CardBgHover
                    : CardBg));

        // Subtle border.
        drawList.AddCircle(
            center,
            outerRadius,
            ImGui.GetColorU32(
                selected
                    ? swatch
                    : BorderSubtle),
            0,
            selected
                ? 2f
                : 1f);

        // Colour swatch.
        drawList.AddCircleFilled(
            center,
            innerRadius,
            ImGui.GetColorU32(swatch));

        // Selected check.
        if (selected)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var check =
                    FontAwesomeIcon.Check
                        .ToIconString();

                var checkSize =
                    ImGui.CalcTextSize(check);

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    center -
                    checkSize * 0.5f,
                    ImGui.GetColorU32(Vector4.One),
                    check);
            }
        }

        if (hovered)
        {
            ImGui.SetTooltip(
                ThemeCatalog.Label(theme));
        }

        if (clicked && !selected)
        {
            Plugin.Cfg.UiTheme =
                theme;

            Plugin.Cfg.Save();

            Colors =
                ThemeCatalog.Get(
                    theme,
                    Plugin.Cfg.UiBackground);
        }
    }


    // =============================================================
    // NEW HELPER — ACCENT PREVIEW
    // =============================================================

    private static void DrawAccentPreview(
        Vector2 size)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.Dummy(size);

        // Outer mini-window.
        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    0.035f,
                    0.045f,
                    0.075f,
                    1f)),
            9f);

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                BorderSubtle),
            9f);

        const float pad = 8f;

        var innerMin =
            origin +
            new Vector2(
                pad,
                Ui(22f));

        var innerMax =
            origin +
            size -
            new Vector2(
                pad,
                pad);

        drawList.AddRectFilled(
            innerMin,
            innerMax,
            ImGui.GetColorU32(
                new Vector4(
                    0.025f,
                    0.032f,
                    0.055f,
                    1f)),
            5f);

        // Tiny title bar marks.
        drawList.AddRectFilled(
            origin +
            UiVec(14f, 10f),
            origin +
            UiVec(52f, 14f),
            ImGui.GetColorU32(
                Accent),
            3f);

        drawList.AddCircleFilled(
            origin +
            new Vector2(
                size.X - Ui(34f),
                Ui(12f)),
            2f,
            ImGui.GetColorU32(
                MutedText));

        drawList.AddCircleFilled(
            origin +
            new Vector2(
                size.X - Ui(22f),
                Ui(12f)),
            2f,
            ImGui.GetColorU32(
                MutedText));

        // Mini selected tab.
        var tabMin =
            innerMin +
            UiVec(8f, 8f);

        var tabMax =
            tabMin +
            UiVec(50f, 7f);

        drawList.AddRectFilled(
            tabMin,
            tabMax,
            ImGui.GetColorU32(
                Accent),
            3f);

        // Content lines.
        drawList.AddRectFilled(
            innerMin +
            UiVec(8f, 28f),
            innerMin +
            UiVec(90f, 33f),
            ImGui.GetColorU32(
                new Vector4(
                    0.45f,
                    0.47f,
                    0.55f,
                    0.45f)),
            2f);

        drawList.AddRectFilled(
            innerMin +
            UiVec(8f, 40f),
            innerMin +
            UiVec(64f, 44f),
            ImGui.GetColorU32(
                new Vector4(
                    0.45f,
                    0.47f,
                    0.55f,
                    0.30f)),
            2f);

        // Accent action.
        var buttonMax =
            innerMax -
            UiVec(8f, 8f);

        var buttonMin =
            buttonMax -
            UiVec(70f, 23f);

        drawList.AddRectFilled(
            buttonMin,
            buttonMax,
            ImGui.GetColorU32(
                Accent),
            5f);

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            var check =
                FontAwesomeIcon.Check
                    .ToIconString();

            var checkSize =
                ImGui.CalcTextSize(
                    check);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                buttonMin +
                new Vector2(
                    Ui(9f),
                    (Ui(23f) -
                     checkSize.Y) * 0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                check);
        }
    }


    // =============================================================
    // NEW HELPER — HOME ILLUSTRATION PREVIEW
    // =============================================================

    private void DrawHomeHeroPreview(
        Vector2 size)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.Dummy(size);

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    0.025f,
                    0.032f,
                    0.055f,
                    1f)),
            9f);

        if (homeHero is not null &&
            Plugin.Cfg.ShowHomeHeroImage)
        {
            var imageOrigin =
    origin +
    UiVec(6f, 6f);

            var imageSize =
                size -
                UiVec(12f, 12f);

            var (uv0, uv1) =
                CoverUvs(
                    homeHero.Width,
                    homeHero.Height,
                    imageSize.X,
                    imageSize.Y);

            drawList.AddImageRounded(
                homeHero.Handle,
                imageOrigin,
                imageOrigin + imageSize,
                uv0,
                uv1,
                ImGui.GetColorU32(Vector4.One),
                7f);
        }
        else
        {
            var icon =
                FontAwesomeIcon.Image
                    .ToIconString();

            Vector2 iconSize;

            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                iconSize =
                    ImGui.CalcTextSize(
                        icon);

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    origin +
                    (size - iconSize) *
                    0.5f,
                    ImGui.GetColorU32(
                        MutedText),
                    icon);
            }
        }

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                BorderSubtle),
            9f,
            ImDrawFlags.None,
            1f);
    }

#if DEBUG
    // Lets a dev-build plugin point at the isolated dev server (own DB, no real accounts) instead of
    // prod, so server-side changes can be tried end-to-end before the same build goes live - see
    // docker-compose.yml's alphachannel-server-dev for the other half of this. Signing in again is
    // required after switching since prod/dev accounts live in separate databases.
    private void DrawServerSettings()
    {
        if (!serverUrlSynced)
        {
            serverUrlInput = Plugin.Cfg.RelayServerUrl;
            serverUrlSynced = true;
        }

        ImGui.TextColored(MutedText, "Switching requires signing in again.");
        ImGui.SetNextItemWidth(Ui(320f));
        ImGui.InputText("##serverUrl", ref serverUrlInput, 128);
        ImGui.SameLine();
        using (ImRaii.Disabled(serverUrlInput.Trim() == Plugin.Cfg.RelayServerUrl))
        {
            if (ImGui.SmallButton("Save"))
            {
                Plugin.Cfg.RelayServerUrl = serverUrlInput.Trim();
                Plugin.Cfg.Save();
            }
        }

        if (ImGui.SmallButton("Use production"))
        {
            serverUrlInput = ProductionServerUrl;
            Plugin.Cfg.RelayServerUrl = ProductionServerUrl;
            Plugin.Cfg.Save();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Use dev"))
        {
            serverUrlInput = DevServerUrl;
            Plugin.Cfg.RelayServerUrl = DevServerUrl;
            Plugin.Cfg.Save();
        }

        ImGui.TextColored(MutedText, $"Currently: {Plugin.Cfg.RelayServerUrl}");
    }
#endif

    private void DrawTrendingTopicTags(
        float columnHeight = 405f,
        float? availableWidthOverride = null)
    {
        var availableWidth =
            availableWidthOverride ??
            ImGui.GetContentRegionAvail().X;

        var columnGap = Ui(8f);

        var columnWidth =
            MathF.Max(
                150f,
                (
                    availableWidth -
                    (columnGap * 2f)
                ) /
                3f);

        DrawTopicColumn(
            "Entertainment",
            columnWidth,
            columnHeight,
            () =>
            {
                Plugin.Cfg.TrendingGaming =
                    DrawTrendingTopicRow(
                        "Gaming",
                        Plugin.Cfg.TrendingGaming);

                Plugin.Cfg.TrendingMMORPG =
                    DrawTrendingTopicRow(
                        "MMORPG",
                        Plugin.Cfg.TrendingMMORPG);

                Plugin.Cfg.TrendingFinalFantasy =
                    DrawTrendingTopicRow(
                        "Final Fantasy",
                        Plugin.Cfg.TrendingFinalFantasy);

                Plugin.Cfg.TrendingAnime =
                    DrawTrendingTopicRow(
                        "Anime",
                        Plugin.Cfg.TrendingAnime);

                Plugin.Cfg.TrendingMovies =
                    DrawTrendingTopicRow(
                        "Movies",
                        Plugin.Cfg.TrendingMovies);

                Plugin.Cfg.TrendingTvShows =
                    DrawTrendingTopicRow(
                        "TV Shows",
                        Plugin.Cfg.TrendingTvShows);

                Plugin.Cfg.TrendingMusic =
                    DrawTrendingTopicRow(
                        "Music",
                        Plugin.Cfg.TrendingMusic);

                Plugin.Cfg.TrendingMemes =
                    DrawTrendingTopicRow(
                        "Memes",
                        Plugin.Cfg.TrendingMemes);

                Plugin.Cfg.TrendingCartoons =
                    DrawTrendingTopicRow(
                        "Cartoons",
                        Plugin.Cfg.TrendingCartoons);

                Plugin.Cfg.TrendingHorror =
                    DrawTrendingTopicRow(
                        "Horror",
                        Plugin.Cfg.TrendingHorror);

                Plugin.Cfg.TrendingSciFi =
                    DrawTrendingTopicRow(
                        "Sci-Fi",
                        Plugin.Cfg.TrendingSciFi);

                Plugin.Cfg.TrendingComedy =
                    DrawTrendingTopicRow(
                        "Comedy",
                        Plugin.Cfg.TrendingComedy);

                Plugin.Cfg.TrendingMinecraft =
                    DrawTrendingTopicRow(
                        "Minecraft",
                        Plugin.Cfg.TrendingMinecraft);

                Plugin.Cfg.TrendingDisney =
                    DrawTrendingTopicRow(
                        "Disney",
                        Plugin.Cfg.TrendingDisney);

                Plugin.Cfg.TrendingFantasy =
                    DrawTrendingTopicRow(
                        "Fantasy",
                        Plugin.Cfg.TrendingFantasy);
            });

        ImGui.SameLine(
            0f,
            columnGap);

        DrawTopicColumn(
            "World & Knowledge",
            columnWidth,
            columnHeight,
            () =>
            {
                Plugin.Cfg.TrendingWildlife =
                    DrawTrendingTopicRow(
                        "Wildlife",
                        Plugin.Cfg.TrendingWildlife);

                Plugin.Cfg.TrendingArchitecture =
                    DrawTrendingTopicRow(
                        "Architecture",
                        Plugin.Cfg.TrendingArchitecture);

                Plugin.Cfg.TrendingScience =
                    DrawTrendingTopicRow(
                        "Science",
                        Plugin.Cfg.TrendingScience);

                Plugin.Cfg.TrendingSpace =
                    DrawTrendingTopicRow(
                        "Space",
                        Plugin.Cfg.TrendingSpace);

                Plugin.Cfg.TrendingHistory =
                    DrawTrendingTopicRow(
                        "History",
                        Plugin.Cfg.TrendingHistory);

                Plugin.Cfg.TrendingTechnology =
                    DrawTrendingTopicRow(
                        "Technology",
                        Plugin.Cfg.TrendingTechnology);

                Plugin.Cfg.TrendingUrbanExploration =
                    DrawTrendingTopicRow(
                        "Urban Exploration",
                        Plugin.Cfg.TrendingUrbanExploration);
            });

        ImGui.SameLine(
            0f,
            columnGap);

        DrawTopicColumn(
            "Lifestyle & Creative",
            columnWidth,
            columnHeight,
            () =>
            {
                Plugin.Cfg.TrendingPets =
                    DrawTrendingTopicRow(
                        "Pets",
                        Plugin.Cfg.TrendingPets);

                Plugin.Cfg.TrendingFood =
                    DrawTrendingTopicRow(
                        "Food",
                        Plugin.Cfg.TrendingFood);

                Plugin.Cfg.TrendingTravel =
                    DrawTrendingTopicRow(
                        "Travel",
                        Plugin.Cfg.TrendingTravel);

                Plugin.Cfg.TrendingCars =
                    DrawTrendingTopicRow(
                        "Cars",
                        Plugin.Cfg.TrendingCars);

                Plugin.Cfg.TrendingSports =
                    DrawTrendingTopicRow(
                        "Sports",
                        Plugin.Cfg.TrendingSports);

                Plugin.Cfg.TrendingArtsAndCrafts =
                    DrawTrendingTopicRow(
                        "Arts & Crafts",
                        Plugin.Cfg.TrendingArtsAndCrafts);

                Plugin.Cfg.TrendingCosplaying =
                    DrawTrendingTopicRow(
                        "Cosplaying",
                        Plugin.Cfg.TrendingCosplaying);

                Plugin.Cfg.TrendingDiy =
                    DrawTrendingTopicRow(
                        "DIY",
                        Plugin.Cfg.TrendingDiy);

                Plugin.Cfg.TrendingFashion =
                    DrawTrendingTopicRow(
                        "Fashion",
                        Plugin.Cfg.TrendingFashion);
            });

        Plugin.Cfg.Save();
    }

    private void DrawTopicColumn(
      string heading,
      float width,
      float height,
      Action drawRows)
    {
        ImGui.PushID(
            heading);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            7f))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildBorderSize,
            1f))
        using (ImRaii.PushColor(
            ImGuiCol.Border,
            new Vector4(
                Accent.X,
                Accent.Y,
                Accent.Z,
                0.55f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.025f,
                0.03f,
                0.055f,
                0.72f)))
        using (var child =
            ImRaii.Child(
                "##topicColumn",
                new Vector2(
                    width,
                    height),
                true))
        {
            if (child)
            {
                ImGui.TextColored(
                    AccentHover,
                    heading);

                ImGui.Dummy(
                    new Vector2(
                        0f,
                        1f));

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.ItemSpacing,
                    UiVec(4f, 1f)))
                {
                    drawRows();
                }
            }
        }

        ImGui.PopID();
    }

    private bool DrawTrendingTopicRow(
      string label,
      bool selected)
    {
        var selectionLimitReached =
            !selected &&
            GetSubscribedTopicCount() >= 15;

        if (selectionLimitReached)
        {
            ImGui.BeginDisabled();
        }

        var rowLabel =
            selected
                ? $"✓  {label}"
                : $"    {label}";

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            2f))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FramePadding,
            UiVec(5f, 0f)))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ButtonTextAlign,
            new Vector2(
                0f,
                0.5f)))
        using (ImRaii.PushColor(
            ImGuiCol.Text,
            selected
                ? AccentHover
                : Vector4.One))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            selected
                ? new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.18f)
                : new Vector4(
                    0f,
                    0f,
                    0f,
                    0f)))
        using (ImRaii.PushColor(
            ImGuiCol.ButtonHovered,
            selected
                ? new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.28f)
                : CardBgHover))
        using (ImRaii.PushColor(
            ImGuiCol.ButtonActive,
            new Vector4(
                Accent.X,
                Accent.Y,
                Accent.Z,
                0.35f)))
        {
            if (ImGui.Button(
                    $"{rowLabel}##topicRow_{label}",
                    UiVec(-1f, 17f)))
            {
                selected =
                    !selected;

                topicSelectionLimitWarning =
                    false;
            }
        }

        if (selectionLimitReached)
        {
            ImGui.EndDisabled();

            var attemptedSelection =
                ImGui.IsItemHovered(
                    ImGuiHoveredFlags.AllowWhenDisabled) &&
                ImGui.IsMouseClicked(
                    ImGuiMouseButton.Left);

            if (attemptedSelection)
            {
                topicSelectionLimitWarning =
                    true;
            }
        }

        return selected;
    }
}
