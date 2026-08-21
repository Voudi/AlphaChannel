using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Settings is a preferences sheet — stacked labeled sections with hairlines, not the same
// CardBg tiles used on Home/Player. Identity: configure the plugin, don't browse content.
internal sealed partial class MainWindow
{
    private const string ProductionServerUrl = "https://alphachannel.duckdns.org";
    private const string DevServerUrl = "http://194.113.211.29:5001";

    private string serverUrlInput = string.Empty;
    private bool serverUrlSynced;

    // ---------------------------------------------------------
    // YouTube subscription management
    // ---------------------------------------------------------

    private string subscriptionChannelInput = string.Empty;
    private bool isAddingManualSubscription;
    private string? subscriptionMessage;
    private bool subscriptionMessageIsError;

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

        const float tabGap = 8f;

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
            "Other",
            tabWidth);

        ImGui.Dummy(
            new Vector2(0f, 14f));

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
                18f));

        // ---------------------------------------------------------
        // Selected tab
        // ---------------------------------------------------------

        switch (settingsTab)
        {
            case SettingsTab.Account:
                SettingsSection(
                    "Account",
                    "Sign-in, display name, and invite code.");

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
                    40f)))
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
                .AddText(
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

    private void DrawAppearanceSettings()
    {
        SettingsSection(
            "Appearance",
            "Colors and window chrome.");

        // =========================================================
        // ACCENT COLOUR
        // =========================================================

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f))
            .Push(
                ImGuiCol.Border,
                BorderSubtle))
        using (var accentCard = ImRaii.Child(
            "##appearanceAccentCard",
            new Vector2(-1f, 205f),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (accentCard)
            {
                ImGui.SetWindowFontScale(1.10f);

                ImGui.TextColored(
                    Vector4.One,
                    "Accent colour");

                ImGui.SetWindowFontScale(1f);

                ImGui.Dummy(
                    new Vector2(0f, 3f));

                ImGui.TextColored(
                    MutedText,
                    "Choose the highlight colour used for buttons, tabs and selected items.");

                ImGui.Dummy(
                    new Vector2(0f, 16f));

                var available =
                    ImGui.GetContentRegionAvail().X;

                // On normal Settings widths, put the controls left
                // and the mini preview on the right.
                const float previewWidth = 210f;
                const float previewHeight = 82f;
                const float previewGap = 24f;

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
            new Vector2(0f, 14f));

        // =========================================================
        // APP BACKGROUND
        // =========================================================

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f))
            .Push(
                ImGuiCol.Border,
                BorderSubtle))
        using (var backgroundCard = ImRaii.Child(
            "##appearanceBackgroundCard",
            new Vector2(-1f, 515f),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (backgroundCard)
            {
                ImGui.SetWindowFontScale(1.10f);

                ImGui.TextColored(
                    Vector4.One,
                    "Plugin background");

                ImGui.SetWindowFontScale(1f);

                ImGui.Dummy(
                    new Vector2(0f, 3f));

                ImGui.TextColored(
                    MutedText,
                    "Choose the background used throughout AlphaChannel.");

                ImGui.Dummy(
                    new Vector2(0f, 16f));

                ImGui.TextColored(
                    Vector4.One,
                    "Background style");

                ImGui.Dummy(
                    new Vector2(0f, 8f));

                DrawBackgroundSettings();

                ImGui.Dummy(
                    new Vector2(0f, 18f));

                // Divider between presets and custom image.
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
                        16f));

                DrawCustomBackgroundSettings();
            }
        }

        ImGui.Dummy(
            new Vector2(0f, 14f));

        // =========================================================
        // HOME ILLUSTRATION
        // =========================================================

        DrawHomeHeroSettings();
    }

    private void DrawOtherSettings()
    {
        SettingsSection(
            "Other",
            "Storage and other plugin preferences.");

        DrawWhisperSettings();

        SettingsHairline();

        SettingsSection(
      "Video playback",
      "Playback options for YouTube and online video sources.");

        DrawCookiesSettings();

        SettingsHairline();

        DrawYouTubeSubscriptionSettings();

        SettingsHairline();

        SettingsSection(
            "Home sections",
                    "Restore hidden sections on the Home page.");

        if (!Plugin.Cfg.ShowFfxivYouTubeSection)
        {
            if (ImGui.Button("Show FFXIV videos section"))
            {
                Plugin.Cfg.ShowFfxivYouTubeSection = true;
                Plugin.Cfg.Save();
            }
        }
        else
        {
            ImGui.TextColored(
                MutedText,
                "FFXIV videos section is currently visible.");
        }

        SettingsHairline();

        SettingsSection(
            "Trending video topics",
            "Choose the topics used for your Trending videos.");

        DrawTrendingTopicTags();

        // Hidden from players — enable ShowServerStackSwitcher
        // in the plugin config JSON to show.
        if (Plugin.Cfg.ShowServerStackSwitcher)
        {
            SettingsHairline();

            SettingsSection(
                "Advanced",
                "Prod vs isolated dev relay.");

            DrawServerSettings();
        }
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
            new Vector2(
                0f,
                4f));

        ImGui.TextColored(
            MutedText,
            "Enter the exact YouTube channel name.");

        ImGui.Dummy(
            new Vector2(
                0f,
                8f));

        const float buttonWidth = 110f;
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
                new Vector2(
                    12f,
                    9f)))
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
                        36f));
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
                new Vector2(
                    0f,
                    6f));

            ImGui.TextColored(
                MutedText,
                "Finding channel...");
        }
        else if (subscriptionMessage is not null)
        {
            ImGui.Dummy(
                new Vector2(
                    0f,
                    6f));

            ImGui.TextColored(
                subscriptionMessageIsError
                    ? Danger
                    : Good,
                subscriptionMessage);
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                18f));

        // ---------------------------------------------------------
        // Current subscriptions
        // ---------------------------------------------------------

        ImGui.TextColored(
            Vector4.One,
            "Subscribed channels");

        ImGui.Dummy(
            new Vector2(
                0f,
                7f));

        if (Plugin.Cfg.SubscribedYouTubeChannelIds.Count == 0)
        {
            ImGui.TextColored(
                MutedText,
                "You aren't subscribed to any channels yet.");

            return;
        }

        // Copy the IDs because clicking Remove modifies the
        // underlying config collection while we're drawing.
        var subscriptions =
            Plugin.Cfg.SubscribedYouTubeChannelIds
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        foreach (var channelId in subscriptions)
        {
            ImGui.PushID(
                $"subscriptionSettings_{channelId}");

            DrawYouTubeSubscriptionSettingsRow(
                channelId);

            ImGui.PopID();

            ImGui.Dummy(
                new Vector2(
                    0f,
                    6f));
        }
    }

    private void DrawYouTubeSubscriptionSettingsRow(
    string channelId)
    {
        const float rowHeight = 42f;
        const float removeWidth = 88f;

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
            drawList.AddText(
                origin +
                new Vector2(
                    12f,
                    13f),
                ImGui.GetColorU32(
                    AccentHover),
                FontAwesomeIcon.User.ToIconString());
        }

        drawList.AddText(
            origin +
            new Vector2(
                34f,
                12f),
            ImGui.GetColorU32(
                Vector4.One),
            channelName);

        // Remove button
        ImGui.SetCursorScreenPos(
            new Vector2(
                origin.X +
                width -
                removeWidth -
                7f,
                origin.Y + 6f));

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
            if (ImGui.Button(
                "Remove",
                new Vector2(
                    removeWidth,
                    30f)))
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
        ImGui.Dummy(new Vector2(0f, 12f));

        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        ImGui.GetWindowDrawList().AddRectFilled(
            origin,
            origin + new Vector2(width, 1f),
            ImGui.GetColorU32(BorderSubtle));

        ImGui.Dummy(new Vector2(width, 12f));
    }

    private void DrawThemeSettings(
    float availableWidth)
    {
        const float gap = 12f;
        const float buttonSize = 38f;

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

        // Six actual built-in background styles.
        // Applying a custom image automatically switches to Custom.
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
            new Vector2(0f, 3f));

        ImGui.TextColored(
            MutedText,
            "Add your own image instead of using a built-in background style.");

        ImGui.Dummy(
            new Vector2(0f, 9f));

        // ---------------------------------------------------------
        // Path + Apply
        // ---------------------------------------------------------

        const float applyWidth = 82f;
        const float inputGap = 10f;

        ImGui.SetNextItemWidth(
            ImGui.GetContentRegionAvail().X -
            applyWidth -
            inputGap);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                new Vector2(12f, 9f)))
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
                    36f)))
            {
                TryApplyCustomBackgroundFromPath(
                    customBackgroundPathInput);
            }
        }

        ImGui.Dummy(
            new Vector2(0f, 10f));

        // ---------------------------------------------------------
        // Action tiles
        // ---------------------------------------------------------
        var actionAvailable =
    ImGui.GetContentRegionAvail().X;

        const float actionGap = 10f;
        const float actionInset = 8f;

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
        ImGui.Dummy(new Vector2(0f, 10f));
        // ---------------------------------------------------------
        // Dim amount
        // ---------------------------------------------------------

        if (Plugin.Cfg.UiBackground ==
                UiBackground.Custom ||
            !string.IsNullOrEmpty(
                Plugin.Cfg.CustomBackgroundPath))
        {
            ImGui.Dummy(
                new Vector2(0f, 11f));

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
                new Vector2(0f, 5f));

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
                new Vector2(0f, 5f));

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
                new Vector2(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f))
            .Push(
                ImGuiCol.Border,
                BorderSubtle))
        using (var heroCard = ImRaii.Child(
            "##appearanceHomeHeroCard",
            new Vector2(-1f, 445f),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!heroCard)
            {
                return;
            }

            ImGui.SetWindowFontScale(
                1.10f);

            ImGui.TextColored(
                Vector4.One,
                "Home illustration");

            ImGui.SetWindowFontScale(
                1f);

            ImGui.Dummy(
                new Vector2(0f, 3f));

            ImGui.TextColored(
                MutedText,
                "Control the artwork shown beside Welcome on the Home page.");

            ImGui.Dummy(
                new Vector2(0f, 13f));

            // -----------------------------------------------------
            // Left side controls
            // -----------------------------------------------------

            var available =
                ImGui.GetContentRegionAvail().X;

            const float previewWidth = 170f;
            const float previewHeight = 170f;
            const float previewGap = 18f;

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
                new Vector2(0f, 13f));

            ImGui.TextColored(
                Vector4.One,
                "Illustration image");

            ImGui.Dummy(
                new Vector2(0f, 3f));

            ImGui.TextColored(
                MutedText,
                "Use the default AlphaChannel artwork or choose your own.");

            ImGui.Dummy(
                new Vector2(0f, 8f));

            using (ImRaii.Disabled(
                !Plugin.Cfg.ShowHomeHeroImage))
            {
                const float applyWidth = 82f;
                const float inputGap = 10f;

                ImGui.SetNextItemWidth(
                    controlsWidth -
                    applyWidth -
                    inputGap);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    8f)
                    .Push(
                        ImGuiStyleVar.FramePadding,
                        new Vector2(12f, 9f)))
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
                            36f)))
                    {
                        TryApplyCustomHomeHeroFromPath(
                            customHomeHeroPathInput);
                    }
                }

                ImGui.Dummy(
                    new Vector2(0f, 10f));

                const float actionGap = 10f;
                const float actionInset = 6f;

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
                ImGui.Dummy(new Vector2(0f, 10f));
            }

            if (customHomeHeroError is { } error)
            {
                ImGui.Dummy(
                    new Vector2(0f, 7f));

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
                    new Vector2(0f, 7f));

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
                38f);

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
                17f,
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

        drawList.AddText(
            new Vector2(
                origin.X + 29f,
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

                drawList.AddText(
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
                22f);

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
            new Vector2(
                14f,
                10f),
            origin +
            new Vector2(
                52f,
                14f),
            ImGui.GetColorU32(
                Accent),
            3f);

        drawList.AddCircleFilled(
            origin +
            new Vector2(
                size.X - 34f,
                12f),
            2f,
            ImGui.GetColorU32(
                MutedText));

        drawList.AddCircleFilled(
            origin +
            new Vector2(
                size.X - 22f,
                12f),
            2f,
            ImGui.GetColorU32(
                MutedText));

        // Mini selected tab.
        var tabMin =
            innerMin +
            new Vector2(
                8f,
                8f);

        var tabMax =
            tabMin +
            new Vector2(
                50f,
                7f);

        drawList.AddRectFilled(
            tabMin,
            tabMax,
            ImGui.GetColorU32(
                Accent),
            3f);

        // Content lines.
        drawList.AddRectFilled(
            innerMin +
            new Vector2(
                8f,
                28f),
            innerMin +
            new Vector2(
                90f,
                33f),
            ImGui.GetColorU32(
                new Vector4(
                    0.45f,
                    0.47f,
                    0.55f,
                    0.45f)),
            2f);

        drawList.AddRectFilled(
            innerMin +
            new Vector2(
                8f,
                40f),
            innerMin +
            new Vector2(
                64f,
                44f),
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
            new Vector2(
                8f,
                8f);

        var buttonMin =
            buttonMax -
            new Vector2(
                70f,
                23f);

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

            drawList.AddText(
                buttonMin +
                new Vector2(
                    9f,
                    (23f -
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
    new Vector2(6f, 6f);

            var imageSize =
                size -
                new Vector2(12f, 12f);

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

                drawList.AddText(
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

    private void DrawWhisperSettings()
    {
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f))
            .Push(
                ImGuiCol.Border,
                BorderSubtle))
        using (var card = ImRaii.Child(
            "##whisperHistoryCard",
            new Vector2(-1f, 205f),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card)
            {
                return;
            }

            ImGui.SetWindowFontScale(1.10f);
            ImGui.TextColored(
                Vector4.One,
                "Whisper history");
            ImGui.SetWindowFontScale(1f);

            ImGui.Dummy(new Vector2(0f, 3f));

            ImGui.TextColored(
                MutedText,
                "Choose whether your /tell direct messages are kept between sessions.");

            ImGui.Dummy(new Vector2(0f, 16f));

            var archive =
                Plugin.Cfg.ArchiveWhispersToDisk;

            if (ImGui.Checkbox(
                "Save /tell history to this device",
                ref archive))
            {
                Plugin.Cfg.ArchiveWhispersToDisk =
                    archive;

                Plugin.Cfg.Save();
            }

            ImGui.Dummy(new Vector2(0f, 8f));

            if (archive)
            {
                ImGui.TextColored(
                    MutedText,
                    "Your /tell history is stored locally on this computer and will still be available next session.");
            }
            else
            {
                ImGui.TextColored(
                    MutedText,
                    "Your /tell history is only kept for this session. It will not be available after you start a new session.");
            }
        }
    }

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
        ImGui.SetNextItemWidth(320f);
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

    private void DrawTrendingTopicTags()
    {
        ImGui.TextColored(
            MutedText,
            "Entertainment");

        Plugin.Cfg.TrendingGaming =
            DrawTrendingTopicTag(
                "Gaming",
                Plugin.Cfg.TrendingGaming);

        ImGui.SameLine();

        Plugin.Cfg.TrendingMMORPG =
            DrawTrendingTopicTag(
                "MMORPG",
                Plugin.Cfg.TrendingMMORPG);

        ImGui.SameLine();

        Plugin.Cfg.TrendingFinalFantasy =
            DrawTrendingTopicTag(
                "Final Fantasy",
                Plugin.Cfg.TrendingFinalFantasy);

        ImGui.SameLine();

        Plugin.Cfg.TrendingAnime =
            DrawTrendingTopicTag(
                "Anime",
                Plugin.Cfg.TrendingAnime);

        ImGui.NewLine();

        Plugin.Cfg.TrendingMovies =
            DrawTrendingTopicTag(
                "Movies",
                Plugin.Cfg.TrendingMovies);

        ImGui.SameLine();

        Plugin.Cfg.TrendingTvShows =
            DrawTrendingTopicTag(
                "TV Shows",
                Plugin.Cfg.TrendingTvShows);

        ImGui.SameLine();

        Plugin.Cfg.TrendingMusic =
            DrawTrendingTopicTag(
                "Music",
                Plugin.Cfg.TrendingMusic);

        ImGui.SameLine();

        Plugin.Cfg.TrendingMemes =
            DrawTrendingTopicTag(
                "Memes",
                Plugin.Cfg.TrendingMemes);


        ImGui.Dummy(
            new Vector2(0f, 8f));


        ImGui.TextColored(
            MutedText,
            "World & Knowledge");

        Plugin.Cfg.TrendingWildlife =
            DrawTrendingTopicTag(
                "Wildlife",
                Plugin.Cfg.TrendingWildlife);

        ImGui.SameLine();

        Plugin.Cfg.TrendingArchitecture =
            DrawTrendingTopicTag(
                "Architecture",
                Plugin.Cfg.TrendingArchitecture);

        ImGui.SameLine();

        Plugin.Cfg.TrendingScience =
            DrawTrendingTopicTag(
                "Science",
                Plugin.Cfg.TrendingScience);

        ImGui.SameLine();

        Plugin.Cfg.TrendingSpace =
            DrawTrendingTopicTag(
                "Space",
                Plugin.Cfg.TrendingSpace);

        ImGui.NewLine();

        Plugin.Cfg.TrendingHistory =
            DrawTrendingTopicTag(
                "History",
                Plugin.Cfg.TrendingHistory);

        ImGui.SameLine();

        Plugin.Cfg.TrendingTechnology =
            DrawTrendingTopicTag(
                "Technology",
                Plugin.Cfg.TrendingTechnology);


        ImGui.Dummy(
            new Vector2(0f, 8f));


        ImGui.TextColored(
            MutedText,
            "Lifestyle");

        Plugin.Cfg.TrendingPets =
            DrawTrendingTopicTag(
                "Pets",
                Plugin.Cfg.TrendingPets);

        ImGui.SameLine();

        Plugin.Cfg.TrendingFood =
            DrawTrendingTopicTag(
                "Food",
                Plugin.Cfg.TrendingFood);

        ImGui.SameLine();

        Plugin.Cfg.TrendingTravel =
            DrawTrendingTopicTag(
                "Travel",
                Plugin.Cfg.TrendingTravel);

        ImGui.SameLine();

        Plugin.Cfg.TrendingCars =
            DrawTrendingTopicTag(
                "Cars",
                Plugin.Cfg.TrendingCars);

        ImGui.SameLine();

        Plugin.Cfg.TrendingSports =
            DrawTrendingTopicTag(
                "Sports",
                Plugin.Cfg.TrendingSports);


        Plugin.Cfg.Save();
    }
    private bool DrawTrendingTopicTag(
    string label,
    bool enabled)
    {
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            14f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            enabled
                ? Accent
                : CardBg))
        using (ImRaii.PushColor(
            ImGuiCol.ButtonHovered,
            enabled
                ? AccentHover
                : CardBgHover))
        {
            if (ImGui.SmallButton(label))
            {
                return !enabled;
            }
        }

        return enabled;
    }
}
