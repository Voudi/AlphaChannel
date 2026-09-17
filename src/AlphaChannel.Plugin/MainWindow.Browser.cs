using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Runtime.InteropServices;
using System.Text;

namespace AlphaChannel.Plugin;

// Alpha Channel embedded-browser page. Removing this file plus the labelled
// integration points removes the UI and Watch Party browser feature.
internal sealed partial class MainWindow
{
    private string browserAddress = "https://www.google.com/";
    private string? browserError;
    private bool browserBroadcastArmed;
    private bool browserBroadcastStartFailed;
    private string? browserBroadcastPublishUrl;
    private string? browserBroadcastHlsUrl;
    private string? browserPatreonAccessMessage;

    internal string? ActiveBrowserBroadcastHlsUrl =>
        browserBroadcastArmed
            ? browserBroadcastHlsUrl
            : null;

    internal string? ActiveBrowserBroadcastTitle =>
        browserBroadcastArmed
            ? screenController.Engine.Browser?.Title ?? "Web Browser"
            : null;

    private bool browserPrivacyMode;
    private bool browserLibraryLoaded;
    private string? lastBrowserHistoryUrl;
    private readonly List<AlphaChannel.Plugin.Video.BrowserPageEntry> browserFavourites = [];
    private readonly List<AlphaChannel.Plugin.Video.BrowserPageEntry> browserHistory = [];
    private readonly AlphaChannel.Plugin.Video.BrowserLibraryStorage browserLibraryStorage = new();

    private readonly HashSet<int> browserKeysDown = [];
    private readonly Dictionary<int, long> browserKeyRepeatAt = [];
    private static readonly int[] BrowserVirtualKeys =
        Enumerable.Range(0x30, 10).Concat(Enumerable.Range(0x41, 26))
            .Concat([0x08, 0x09, 0x0D, 0x1B, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25,
                0x26, 0x27, 0x28, 0x2D, 0x2E, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5,
                0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE])
            .ToArray();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyboardState,
        StringBuilder buffer, int bufferLength, uint flags, nint keyboardLayout);

    private static string NormalizeBrowserAddress(string value)
    {
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return uri.AbsoluteUri;
        if (!value.Contains(' ') && Uri.TryCreate("https://" + value, UriKind.Absolute, out uri))
            return uri.AbsoluteUri;
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(value);
    }

    private void DrawBrowserPage()
    {
        var engine = screenController.Engine;
        var browser = engine.Browser;
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8f, 8f));
        using var frames = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10f, 7f)).Push(ImGuiStyleVar.FrameRounding, Ui(7f));
        using var colors = ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0.10f, 0.09f, 0.18f, 1f))
            .Push(ImGuiCol.FrameBgHovered, new Vector4(0.20f, 0.16f, 0.32f, 1f));

        var button = Ui(38f);
        using (ImRaii.Disabled(browser?.CanGoBack != true))
            if (GameLayoutButton("Back", FontAwesomeIcon.ArrowLeft, Ui(82f))) browser?.Back();
        ImGui.SameLine(0f, Ui(8f));
        using (ImRaii.Disabled(browser?.CanGoForward != true))
            if (GameLayoutButton("Forward", FontAwesomeIcon.ArrowRight, Ui(98f))) browser?.Forward();
        ImGui.SameLine(0f, Ui(8f));
        if (GameLayoutButton(browser?.IsLoading == true ? "Stop" : "Refresh",
                browser?.IsLoading == true ? FontAwesomeIcon.Stop : FontAwesomeIcon.Sync, Ui(94f)))
        {
            if (browser?.IsLoading == true) browser.StopLoading(); else browser?.Reload();
        }
        ImGui.SameLine(0f, Ui(8f));
        ImGui.SetNextItemWidth(MathF.Max(Ui(120f), ImGui.GetContentRegionAvail().X - Ui(246f)));
        var submit = ImGui.InputTextWithHint("##browserAddress", "Enter a web address or search...", ref browserAddress, 2048,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var addressEditing = ImGui.IsItemActive();
        ImGui.SameLine(0f, Ui(8f));
        if (GameLayoutButton("Go", FontAwesomeIcon.ArrowRight, Ui(84f), true) || submit)
        {
            var address = NormalizeBrowserAddress(browserAddress);
            if (browser is null)
            {
                if (!engine.PlayBrowser(address)) browserError = engine.LastError;
            }
            else browser.Navigate(address);
        }
        ImGui.SameLine(0f, Ui(8f));
        if (BrowserToolbarIconButton("browserFavourites", FontAwesomeIcon.Star, false, "Favourites"))
            activeGameDialog = "Browser favourites";
        ImGui.SameLine(0f, Ui(8f));
        if (BrowserToolbarIconButton("browserHistory", FontAwesomeIcon.History, false, "History"))
            activeGameDialog = "Browser history";
        ImGui.SameLine(0f, Ui(8f));
        if (BrowserToolbarIconButton("browserPrivacy", browserPrivacyMode ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye,
                browserPrivacyMode, browserPrivacyMode
                    ? "Privacy mode is enabled. Viewers see a privacy screen and cannot hear browser audio."
                    : "Hide browser video and audio from viewers"))
        {
            browserPrivacyMode = !browserPrivacyMode;
            browser?.SetPrivacyMode(browserPrivacyMode);
        }

        if (browser is null)
        {
            ImGui.Dummy(UiVec(0f, 25f));
            GameLayoutPanel("##browserEmpty", ImGui.GetContentRegionAvail().X, Ui(505f), () =>
            {
                GameLayoutHeading("Embedded Browser", FontAwesomeIcon.Globe);
                ImGui.TextWrapped("Browse the web on your in-game screen, then share the browser and its audio with your Watch Party.");
                ImGui.Dummy(UiVec(0f, 12f));
                using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.20f, 0.11f, 0.04f, 0.92f))
                    .Push(ImGuiCol.Border, new Vector4(1f, 0.56f, 0.12f, 0.9f)))
                {
                    ImGui.BeginChild("##browserDrmWarning", UiVec(ImGui.GetContentRegionAvail().X, 132f), true,
                        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
                    using (ImRaii.PushFont(UiBuilder.IconFont))
                        ImGui.TextColored(new Vector4(1f, 0.63f, 0.18f, 1f), FontAwesomeIcon.ShieldAlt.ToIconString());
                    ImGui.SameLine(0f, Ui(8f));
                    ImGui.TextColored(new Vector4(1f, 0.63f, 0.18f, 1f), "Protected video services");
                    ImGui.TextWrapped("Our in-built browser can not currently play content from protected video services such as Netflix, Disney+ and Amazon Prime. YouTube and sites without DRM will work. For sites you can't play via the in-built browser, you may be able to open them in your standard browser and then broadcast the application via our live streaming in the media section");
                    ImGui.EndChild();
                }
                ImGui.Dummy(UiVec(0f, 18f));
                if (GameLayoutButton("Open Browser", FontAwesomeIcon.Globe, ImGui.GetContentRegionAvail().X, true))
                {
                    if (!engine.PlayBrowser(NormalizeBrowserAddress(browserAddress))) browserError = engine.LastError;
                }

                if (!HasConfirmedPatreonAccess())
                    DrawBrowserPatreonGate();
            });
            var emptyBrowserError = browserError ?? engine.LastError;
            if (!string.IsNullOrWhiteSpace(emptyBrowserError)) ImGui.TextColored(Danger, emptyBrowserError);
            DrawBrowserDialogs(null);
            return;
        }

        if (!addressEditing) browserAddress = browser.Address;
        EnsureBrowserLibraryLoaded();
        TrackBrowserHistory(browser);
        browser.SetPrivacyMode(browserPrivacyMode);
        var available = ImGui.GetContentRegionAvail();
        var browserBroadcastLocked =
            !browserBroadcastArmed &&
            !HasConfirmedPatreonAccess();
        var controlsHeight = Ui(170f) +
            (browserBroadcastLocked ? Ui(62f) : 0f) +
            (youtubeBrowserConnectionPending ? Ui(64f) : 0f);
        var viewWidth = available.X;
        var viewHeight = MathF.Max(Ui(260f), MathF.Min(available.Y - controlsHeight, viewWidth * 9f / 16f));
        var viewOrigin = ImGui.GetCursorScreenPos();
        ImGui.Image(new ImTextureID(engine.PreviewTextureHandle), new Vector2(viewWidth, viewHeight));
        HandleBrowserInput(browser, viewOrigin, new Vector2(viewWidth, viewHeight), addressEditing);

        ImGui.Dummy(UiVec(0f, 4f));
        DrawPendingYouTubeBrowserConnection(browser);
        if (youtubeBrowserConnectionPending)
            ImGui.Dummy(UiVec(0f, 2f));
        if (browserBroadcastLocked)
        {
            DrawCompactBrowserPatreonStrip();
            ImGui.Dummy(UiVec(0f, 2f));
        }
        var inputHalf = (ImGui.GetContentRegionAvail().X - Ui(8f)) / 2f;
        if (GameLayoutButton("Control Browser", FontAwesomeIcon.Keyboard, inputHalf, engine.BrowserControlsEnabled))
            engine.SetBrowserControlsEnabled(true);
        ImGui.SameLine(0f, Ui(8f));
        if (GameLayoutButton("Control FFXIV", FontAwesomeIcon.Desktop, inputHalf, !engine.BrowserControlsEnabled))
        {
            ReleaseBrowserKeys(browser);
            engine.SetBrowserControlsEnabled(false);
        }
        if (engine.BrowserControlsEnabled && ImGui.IsItemHovered())
            ImGui.SetTooltip("Press Ctrl + F12 at any time to return keyboard control to FFXIV.");
        ImGui.Dummy(UiVec(0f, 2f));
        var third = (ImGui.GetContentRegionAvail().X - Ui(16f)) / 3f;
        if (GameLayoutButton("Close Browser", FontAwesomeIcon.Times, third, danger: true))
        {
            ReleaseBrowserKeys(browser);
            StopBrowserWatchPartyBroadcast();
            engine.StopVideo();
        }
        ImGui.SameLine(0f, Ui(8f));
        DrawBrowserPartyButton(third);
        ImGui.SameLine(0f, Ui(8f));
        DrawBrowserBroadcastButton(third);
        if (!string.IsNullOrWhiteSpace(browser.LastError ?? browserError))
            ImGui.TextColored(Danger, browser.LastError ?? browserError);
        DrawBrowserDialogs(browser);
    }

    private void EnsureBrowserLibraryLoaded()
    {
        if (browserLibraryLoaded) return;
        browserLibraryLoaded = true;
        var loaded = browserLibraryStorage.Load();
        browserFavourites.AddRange(loaded.Favourites);
        browserHistory.AddRange(loaded.History.Take(30));
        if (!string.IsNullOrWhiteSpace(loaded.RecoveryMessage))
            Plugin.ChatGui.Print("[AlphaChannel] " + loaded.RecoveryMessage);
    }

    private void TrackBrowserHistory(AlphaChannel.Plugin.Video.BrowserRenderer browser)
    {
        if (browser.IsLoading || !Uri.TryCreate(browser.Address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || browser.Address == lastBrowserHistoryUrl) return;
        lastBrowserHistoryUrl = browser.Address;
        browserHistory.RemoveAll(page => string.Equals(page.Url, browser.Address, StringComparison.OrdinalIgnoreCase));
        browserHistory.Insert(0, new(browser.Title, browser.Address));
        if (browserHistory.Count > 30) browserHistory.RemoveRange(30, browserHistory.Count - 30);
        browserLibraryStorage.Save(browserFavourites, browserHistory);
    }

    private void DrawBrowserDialogs(AlphaChannel.Plugin.Video.BrowserRenderer? browser)
    {
        EnsureBrowserLibraryLoaded();
        if (BeginGameDialog("Browser favourites", "Browser Favourites", FontAwesomeIcon.Star, 680f, 560f))
        {
            if (browser is not null)
            {
                var index = browserFavourites.FindIndex(page => string.Equals(page.Url, browser.Address, StringComparison.OrdinalIgnoreCase));
                if (GameLayoutButton(index >= 0 ? "Remove Current Page" : "Add Current Page",
                        index >= 0 ? FontAwesomeIcon.Times : FontAwesomeIcon.Star, ImGui.GetContentRegionAvail().X, true))
                {
                    if (index >= 0) browserFavourites.RemoveAt(index);
                    else browserFavourites.Insert(0, new(browser.Title, browser.Address));
                    browserLibraryStorage.Save(browserFavourites, browserHistory);
                }
                ImGui.Spacing();
            }
            DrawBrowserPageList(browserFavourites, browser, "No favourite pages yet.", true);
            if (GameLayoutButton("Done", FontAwesomeIcon.Check, ImGui.GetContentRegionAvail().X, true)) activeGameDialog = null;
            EndGameDialog();
        }
        if (BeginGameDialog("Browser history", "Browser History", FontAwesomeIcon.History, 680f, 600f))
        {
            ImGui.TextDisabled("Your last 30 visited pages");
            ImGui.Spacing();
            DrawBrowserPageList(browserHistory, browser, "No browsing history yet.", false);
            if (browserHistory.Count > 0 && GameLayoutButton("Clear History", FontAwesomeIcon.Trash, ImGui.GetContentRegionAvail().X, danger: true))
            {
                browserHistory.Clear();
                browserLibraryStorage.Save(browserFavourites, browserHistory);
            }
            if (GameLayoutButton("Done", FontAwesomeIcon.Check, ImGui.GetContentRegionAvail().X, true)) activeGameDialog = null;
            EndGameDialog();
        }
    }

    private void DrawBrowserPageList(List<AlphaChannel.Plugin.Video.BrowserPageEntry> pages,
        AlphaChannel.Plugin.Video.BrowserRenderer? browser, string emptyText, bool allowDelete)
    {
        if (pages.Count == 0) { ImGui.TextDisabled(emptyText); ImGui.Dummy(UiVec(0f, 24f)); return; }
        var height = MathF.Min(Ui(330f), pages.Count * Ui(54f));
        ImGui.BeginChild("##browserPageList", UiVec(ImGui.GetContentRegionAvail().X, height), false);
        foreach (var page in pages.ToArray())
        {
            ImGui.PushID(page.Url);
            var deleteSpace = allowDelete ? Ui(50f) : 0f;
            if (GameLayoutButton(page.Title, FontAwesomeIcon.Globe, ImGui.GetContentRegionAvail().X - deleteSpace))
            {
                browserAddress = page.Url;
                if (browser is null) screenController.Engine.PlayBrowser(page.Url); else browser.Navigate(page.Url);
                activeGameDialog = null;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(page.Url);
            if (allowDelete)
            {
                ImGui.SameLine(0f, Ui(8f));
                if (BrowserToolbarIconButton("deleteFavourite", FontAwesomeIcon.Trash, false,
                        "Remove from favourites", true))
                {
                    browserFavourites.Remove(page);
                    browserLibraryStorage.Save(browserFavourites, browserHistory);
                }
            }
            ImGui.PopID();
        }
        ImGui.EndChild();
        ImGui.Spacing();
    }

    private bool BrowserToolbarIconButton(string id, FontAwesomeIcon icon, bool selected, string tooltip,
        bool danger = false)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = UiVec(42f, 36f);
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, selected || danger ? 0f : Ui(1f));
        using var colors = ImRaii.PushColor(ImGuiCol.Button,
                danger ? Danger : selected ? Accent : new Vector4(0.055f, 0.07f, 0.115f, 1f))
            .Push(ImGuiCol.ButtonHovered, danger ? Danger : selected ? AccentHover : CardBgHover)
            .Push(ImGuiCol.ButtonActive, danger ? Danger : AccentActive)
            .Push(ImGuiCol.Border, BorderSubtle);
        var clicked = ImGui.Button("##" + id, size);
        var glyph = icon.ToIconString();
        Vector2 glyphSize;
        using (ImRaii.PushFont(UiBuilder.IconFont)) glyphSize = ImGui.CalcTextSize(glyph);
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(), origin + (size - glyphSize) / 2f,
                ImGui.GetColorU32(ImGuiCol.Text), glyph);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    private void HandleBrowserInput(AlphaChannel.Plugin.Video.BrowserRenderer browser, Vector2 origin, Vector2 size,
        bool addressEditing)
    {
        var hovered = ImGui.IsItemHovered();
        if (screenController.Engine.BrowserControlsEnabled && !addressEditing)
            ForwardBrowserKeyboard(browser);
        else if (browserKeysDown.Count > 0)
            ReleaseBrowserKeys(browser);
        if (!hovered) return;
        var mouse = ImGui.GetMousePos();
        var x = Math.Clamp((int)((mouse.X - origin.X) / size.X * AlphaChannel.Plugin.Video.BrowserRenderer.Width), 0, AlphaChannel.Plugin.Video.BrowserRenderer.Width - 1);
        var y = Math.Clamp((int)((mouse.Y - origin.Y) / size.Y * AlphaChannel.Plugin.Video.BrowserRenderer.Height), 0, AlphaChannel.Plugin.Video.BrowserRenderer.Height - 1);
        browser.SendMouseMove(x, y);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) browser.SendMouseClick(x, y, 0, false);
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) browser.SendMouseClick(x, y, 0, true);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) browser.SendMouseClick(x, y, 2, false);
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Right)) browser.SendMouseClick(x, y, 2, true);
        var wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0f) browser.SendMouseWheel(x, y, 0, (int)(wheel * 120));
    }

    private void ForwardBrowserKeyboard(AlphaChannel.Plugin.Video.BrowserRenderer browser)
    {
        ImGui.SetNextFrameWantCaptureKeyboard(true);
        var modifiers = BrowserKeyModifiers();
        var now = Environment.TickCount64;
        foreach (var virtualKey in BrowserVirtualKeys)
        {
            var down = (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
            if (down && browserKeysDown.Add(virtualKey))
            {
                SendBrowserKeyPress(browser, virtualKey, modifiers);
                browserKeyRepeatAt[virtualKey] = now + 450;
            }
            else if (down && browserKeyRepeatAt.TryGetValue(virtualKey, out var repeatAt) && now >= repeatAt)
            {
                SendBrowserKeyPress(browser, virtualKey, modifiers);
                browserKeyRepeatAt[virtualKey] = now + 35;
            }
            else if (!down && browserKeysDown.Remove(virtualKey))
            {
                browser.SendKey(virtualKey, true, modifiers);
                browserKeyRepeatAt.Remove(virtualKey);
            }
        }
    }

    private static int BrowserKeyModifiers()
    {
        var modifiers = 0;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) modifiers |= 2;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) modifiers |= 4;
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) modifiers |= 8;
        return modifiers;
    }

    private static void SendBrowserKeyPress(AlphaChannel.Plugin.Video.BrowserRenderer browser,
        int virtualKey, int modifiers)
    {
        browser.SendKey(virtualKey, false, modifiers);
        if ((modifiers & (4 | 8)) != 0) return;
        var keyboardState = new byte[256];
        for (var key = 0; key < keyboardState.Length; key++)
            if ((GetAsyncKeyState(key) & 0x8000) != 0) keyboardState[key] = 0x80;
        if ((GetKeyState(0x14) & 1) != 0) keyboardState[0x14] |= 1;
        var text = new StringBuilder(4);
        var count = ToUnicodeEx((uint)virtualKey, MapVirtualKey((uint)virtualKey, 0), keyboardState,
            text, text.Capacity, 0, GetKeyboardLayout(0));
        if (count > 0)
            for (var index = 0; index < count; index++) browser.SendCharacter(text[index], modifiers);
    }

    private void ReleaseBrowserKeys(AlphaChannel.Plugin.Video.BrowserRenderer browser)
    {
        var modifiers = BrowserKeyModifiers();
        foreach (var virtualKey in browserKeysDown) browser.SendKey(virtualKey, true, modifiers);
        browserKeysDown.Clear();
        browserKeyRepeatAt.Clear();
    }

    private void DrawBrowserPartyButton(float width)
    {
        if (stream.Mode == StreamMode.Hosting)
        {
            GameLayoutButton("Watch Party Ready", FontAwesomeIcon.Users, width);
            return;
        }
        var patreonLocked = !HasConfirmedPatreonAccess();
        using var disabled = ImRaii.Disabled(
            CurrentSession is null ||
            stream.Mode == StreamMode.Viewing ||
            patreonLocked);
        if (GameLayoutButton("Create Watch Party", FontAwesomeIcon.Users, width))
        {
            pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.GameRoom;
            watchPartyCreationPopupOpen = true;
            createRoomPassword = string.Empty;
            createLockedRoomPasswordError = null;
        }
        if (patreonLocked &&
            ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "A Patreon membership is required to create a Watch Party that broadcasts your browser.");
        }
    }

    private void DrawBrowserBroadcastButton(float width)
    {
        if (browserBroadcastArmed)
        {
            if (GameLayoutButton(browserBroadcastStartFailed ? "Stop Failed Broadcast" : screenController.Engine.IsBrowserBroadcasting ? "Stop Broadcast" : "Waiting for Viewers",
                    FontAwesomeIcon.Stop, width, danger: true)) StopBrowserWatchPartyBroadcast();
            return;
        }

        if (!HasConfirmedPatreonAccess())
        {
            DrawLockedBrowserBroadcastButton(width);
            return;
        }
        var canStart = stream.Mode == StreamMode.Hosting && CurrentSession is { } session &&
            !string.IsNullOrWhiteSpace(Plugin.Cfg.StreamKeys.GetValueOrDefault(session.AccountId));
        using var disabled = ImRaii.Disabled(!canStart);
        if (GameLayoutButton("Start Broadcast", FontAwesomeIcon.BroadcastTower, width, true)) StartBrowserWatchPartyBroadcast();
    }

    private void StartBrowserWatchPartyBroadcast()
    {
        if (!HasConfirmedPatreonAccess())
        {
            browserPatreonAccessMessage =
                "A Patreon membership is required to broadcast the browser.";
            Plugin.ChatGui.Print(
                "[AlphaChannel] A Patreon membership is required to broadcast the browser.");
            return;
        }

        if (CurrentSession is not { } session || stream.Mode != StreamMode.Hosting) return;
        var key = Plugin.Cfg.StreamKeys.GetValueOrDefault(session.AccountId);
        if (string.IsNullOrWhiteSpace(key)) return;
        var engine = screenController.Engine;
        if (!engine.IsActive) engine.RespawnScreen();
        browserBroadcastPublishUrl = $"{BuildRtmpServer()}/{key}";
        browserBroadcastHlsUrl = $"{BuildMyHlsUrl(session)}?broadcast={Guid.NewGuid():N}";
        browserBroadcastStartFailed = false;
        browserBroadcastArmed = true;
        _ = stream.PublishStateAsync(browserBroadcastHlsUrl, 0d, false,
            engine.ScreenPosition, engine.ScreenYaw,
            engine.ScreenScale, engine.Browser?.Title ?? "Web Browser", null);
    }

    internal void UpdateBrowserBroadcastDemand()
    {
        if (!browserBroadcastArmed) return;
        var engine = screenController.Engine;
        if (stream.Mode != StreamMode.Hosting || !engine.IsPlayingBrowser || string.IsNullOrWhiteSpace(browserBroadcastPublishUrl))
        {
            StopBrowserWatchPartyBroadcast();
            return;
        }
        var hasViewer = stream.Roster.Length > 0;
        if (!hasViewer)
        {
            if (engine.IsBrowserBroadcasting) engine.StopBrowserBroadcast();
            browserBroadcastStartFailed = false;
            return;
        }
        if (!engine.IsBrowserBroadcasting && !browserBroadcastStartFailed)
            browserBroadcastStartFailed = !engine.StartBrowserBroadcast(browserBroadcastPublishUrl);
    }

    private void StopBrowserWatchPartyBroadcast()
    {
        screenController.Engine.StopBrowserBroadcast();
        browserBroadcastArmed = false;
        browserBroadcastStartFailed = false;
        browserBroadcastPublishUrl = null;
        browserBroadcastHlsUrl = null;
    }

    private void RefreshBrowserPatreonAccess()
    {
        if (!HasConfiguredPatreonAccess())
        {
            patreonAccessConfirmed = false;
            browserPatreonAccessMessage =
                "No active Patreon membership was found.";
            return;
        }

        patreonAccessConfirmed = true;
        browserPatreonAccessMessage = null;
        Plugin.ChatGui.Print(
            $"[AlphaChannel] Patreon access confirmed (tier {Plugin.Cfg.PatreonMembershipTier}).");
    }

    private void DrawLockedBrowserBroadcastButton(float width)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f)))
        using (ImRaii.PushColor(ImGuiCol.Button, PatreonOrange)
                   .Push(ImGuiCol.ButtonHovered, PatreonOrangeHover)
                   .Push(ImGuiCol.ButtonActive, new Vector4(0.92f, 0.42f, 0.08f, 1f)))
        {
            var origin = ImGui.GetCursorScreenPos();
            var size = new Vector2(width, Ui(36f));
            if (ImGui.Button("##unlockBrowserBroadcast", size))
                patreonPopupOpen = true;
            DrawPlayerActionButtonContent(
                origin,
                size,
                FontAwesomeIcon.Lock,
                "Unlock Broadcast",
                Vector4.One);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "A Patreon membership is required to broadcast the browser.");
    }

    private void DrawBrowserPatreonGate()
    {
        ImGui.Dummy(UiVec(0f, 14f));
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
                   .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 12f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.075f, 0.06f, 0.12f, 1f))
                   .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.82f)))
        {
            if (ImGui.BeginChild(
                    "##browserPatreonGate",
                    new Vector2(-1f, Ui(145f)),
                    true,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                var panelMin = ImGui.GetWindowPos();
                var panelSize = ImGui.GetWindowSize();
                var panelMax = panelMin + panelSize;
                var drawList = ImGui.GetWindowDrawList();
                drawList.AddCircleFilled(
                    new Vector2(panelMax.X - panelSize.X * 0.12f, (panelMin.Y + panelMax.Y) * 0.5f),
                    panelSize.Y * 1.25f,
                    ImGui.GetColorU32(new Vector4(
                        PatreonOrange.X, PatreonOrange.Y, PatreonOrange.Z, 0.07f)),
                    64);

                var orangeBorder = ImGui.GetColorU32(new Vector4(
                    PatreonOrange.X, PatreonOrange.Y, PatreonOrange.Z, 0.82f));
                var middleX = (panelMin.X + panelMax.X) * 0.5f;
                drawList.AddLine(new Vector2(middleX, panelMin.Y), new Vector2(panelMax.X - Ui(10f), panelMin.Y), orangeBorder);
                drawList.AddLine(new Vector2(panelMax.X, panelMin.Y + Ui(10f)), new Vector2(panelMax.X, panelMax.Y - Ui(10f)), orangeBorder);
                drawList.AddLine(new Vector2(panelMax.X - Ui(10f), panelMax.Y), new Vector2(middleX, panelMax.Y), orangeBorder);

                var contentOrigin = ImGui.GetCursorScreenPos();
                DrawLocalVideoPatreonHeart(contentOrigin + UiVec(38f, 58f));

                var actionWidth = MathF.Min(Ui(270f), panelSize.X * 0.30f);
                var actionX = panelSize.X - Ui(18f) - actionWidth;
                var copyX = Ui(88f);
                var copyWidth = MathF.Max(Ui(220f), actionX - copyX - Ui(24f));

                ImGui.SetCursorPos(UiVec(88f, 20f));
                SetUiFontScale(0.82f);
                ImGui.TextColored(PatreonOrange, "PATREON FEATURE");
                SetUiFontScale(1.16f);
                ImGui.SetCursorPosX(copyX);
                ImGui.TextColored(Vector4.One, "Browser broadcasting");
                SetUiFontScale(1f);
                ImGui.SetCursorPosX(copyX);
                ImGui.PushTextWrapPos(copyX + copyWidth);
                ImGui.TextColored(
                    MutedText,
                    "Browse freely on your local TV. Join our Patreon to broadcast your browser and its audio to a Watch Party.");
                ImGui.PopTextWrapPos();

                ImGui.SetCursorPos(new Vector2(actionX, Ui(27f)));
                DrawDjActionButton(
                    "##unlockBrowserWithPatreon",
                    FontAwesomeIcon.LockOpen,
                    "Unlock with Patreon",
                    new Vector2(actionWidth, Ui(40f)),
                    false,
                    () => patreonPopupOpen = true,
                    true);

                ImGui.SetCursorPos(new Vector2(actionX, Ui(72f)));
                DrawBrowserRefreshAccessButton(
                    "##refreshBrowserPatreon",
                    actionWidth);

                if (browserPatreonAccessMessage is { } accessMessage)
                {
                    ImGui.SetCursorPos(new Vector2(actionX, Ui(104f)));
                    SetUiFontScale(0.82f);
                    ImGui.TextColored(Danger, accessMessage);
                    SetUiFontScale(1f);
                }
            }
            ImGui.EndChild();
        }
    }

    private void DrawCompactBrowserPatreonStrip()
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(8f))
                   .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
                   .Push(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.065f, 0.05f, 0.10f, 1f))
                   .Push(ImGuiCol.Border, Vector4.Zero))
        {
            if (ImGui.BeginChild(
                    "##compactBrowserPatreonStrip",
                    new Vector2(-1f, Ui(54f)),
                    true,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                var panelMin = ImGui.GetWindowPos();
                var panelSize = ImGui.GetWindowSize();
                var panelMax = panelMin + panelSize;
                var drawList = ImGui.GetWindowDrawList();
                var refreshWidth = Ui(122f);
                var unlockWidth = Ui(104f);
                var actionGap = Ui(8f);
                var rightPadding = Ui(12f);
                var right = panelSize.X - rightPadding;
                var unlockX = right - refreshWidth - actionGap - unlockWidth;
                var buttonY = (panelSize.Y - Ui(34f)) * 0.5f;

                drawList.AddCircleFilled(
                    new Vector2(panelMax.X + Ui(8f), (panelMin.Y + panelMax.Y) * 0.5f),
                    Ui(115f),
                    ImGui.GetColorU32(new Vector4(
                        PatreonOrange.X, PatreonOrange.Y, PatreonOrange.Z, 0.07f)),
                    48);

                var accentBorder = ImGui.GetColorU32(new Vector4(
                    Accent.X, Accent.Y, Accent.Z, 0.88f));
                var orangeBorder = ImGui.GetColorU32(new Vector4(
                    PatreonOrange.X, PatreonOrange.Y, PatreonOrange.Z, 0.88f));
                var middleX = (panelMin.X + panelMax.X) * 0.5f;
                drawList.AddRect(
                    panelMin,
                    panelMax,
                    accentBorder,
                    Ui(8f),
                    ImDrawFlags.RoundCornersAll,
                    Ui(1.2f));
                drawList.AddLine(new Vector2(middleX, panelMin.Y), new Vector2(panelMax.X - Ui(8f), panelMin.Y), orangeBorder, Ui(1.2f));
                drawList.AddLine(new Vector2(panelMax.X, panelMin.Y + Ui(8f)), new Vector2(panelMax.X, panelMax.Y - Ui(8f)), orangeBorder, Ui(1.2f));
                drawList.AddLine(new Vector2(panelMax.X - Ui(8f), panelMax.Y), new Vector2(middleX, panelMax.Y), orangeBorder, Ui(1.2f));

                var icon = FontAwesomeIcon.Lock.ToIconString();
                Vector2 iconSize;
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    iconSize = ImGui.CalcTextSize(icon);
                var title = "Broadcasting requires Patreon";
                var titleSize = ImGui.CalcTextSize(title);
                var detail = "Local browsing remains available.";
                var detailSize = ImGui.CalcTextSize(detail);
                var contentY = panelMin.Y + (panelSize.Y - titleSize.Y) * 0.5f;
                var iconX = panelMin.X + Ui(15f);
                var titleX = iconX + iconSize.X + Ui(10f);
                var dividerX = titleX + titleSize.X + Ui(16f);
                var detailX = dividerX + Ui(16f);

                using (ImRaii.PushFont(UiBuilder.IconFont))
                    drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                        new Vector2(iconX, panelMin.Y + (panelSize.Y - iconSize.Y) * 0.5f),
                        ImGui.GetColorU32(PatreonOrange),
                        icon);
                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    new Vector2(titleX, contentY),
                    ImGui.GetColorU32(Vector4.One),
                    title);
                drawList.AddLine(
                    new Vector2(dividerX, panelMin.Y + Ui(15f)),
                    new Vector2(dividerX, panelMax.Y - Ui(15f)),
                    ImGui.GetColorU32(new Vector4(MutedText.X, MutedText.Y, MutedText.Z, 0.42f)),
                    Ui(1f));
                if (detailX + detailSize.X < panelMin.X + unlockX - Ui(14f))
                {
                    drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                        new Vector2(detailX, contentY),
                        ImGui.GetColorU32(MutedText),
                        detail);
                }

                ImGui.SetCursorPos(new Vector2(right - refreshWidth, buttonY + Ui(3f)));
                DrawBrowserRefreshAccessButton(
                    "##refreshCompactBrowserPatreon",
                    refreshWidth);
                ImGui.SetCursorPos(new Vector2(unlockX, buttonY));
                DrawDjActionButton(
                    "##unlockCompactBrowserPatreon",
                    FontAwesomeIcon.Heart,
                    "Unlock",
                    new Vector2(unlockWidth, Ui(34f)),
                    false,
                    () => patreonPopupOpen = true,
                    true);
            }
            ImGui.EndChild();
        }
    }

    private void DrawBrowserRefreshAccessButton(string id, float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, Ui(28f));
        if (ImGui.InvisibleButton(id, size))
        {
            RefreshBrowserPatreonAccess();
        }

        var hovered = ImGui.IsItemHovered();
        var label = "Refresh access";
        var textSize = ImGui.CalcTextSize(label);
        var textOrigin = origin + new Vector2(
            (size.X - textSize.X) * 0.5f,
            (size.Y - textSize.Y) * 0.5f);
        var color = hovered ? AccentHover : MutedText;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), textOrigin, ImGui.GetColorU32(color), label);
        drawList.AddLine(
            new Vector2(textOrigin.X, textOrigin.Y + textSize.Y + Ui(1f)),
            new Vector2(textOrigin.X + textSize.X, textOrigin.Y + textSize.Y + Ui(1f)),
            ImGui.GetColorU32(color),
            Ui(1f));
    }
}
