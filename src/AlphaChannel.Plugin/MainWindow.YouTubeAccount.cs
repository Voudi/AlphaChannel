using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const string YouTubeBrowserSignInUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";

    private bool youtubeBrowserConnectionPending;
    private bool youtubeBrowserConnectionBusy;
    private string? youtubeBrowserConnectionMessage;
    private bool youtubeBrowserConnectionMessageIsError;

    private void DrawEmbeddedBrowserYouTubeAccountSettings()
    {
        var connected =
            YouTubeEmbeddedBrowserSession.IsConnected(Plugin.Cfg);

        ImGui.TextColored(
            connected ? Good : MutedText,
            connected
                ? "YouTube account connected"
                : "No YouTube account connected");

        ImGui.Dummy(UiVec(0f, 6f));
        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            Math.Max(ImGui.GetContentRegionAvail().X, Ui(200f)));
        ImGui.TextColored(
            MutedText,
            connected
                ? "Alpha Channel will use this account only when a YouTube video requires sign-in. Normal playback remains anonymous."
                : "Sign into YouTube in Alpha Channel's browser, then explicitly choose to use that account for account-required videos.");
        ImGui.PopTextWrapPos();

        ImGui.Dummy(UiVec(0f, 12f));

        if (connected)
        {
            var gap = Ui(8f);
            var width =
                (ImGui.GetContentRegionAvail().X - gap) / 2f;

            DrawDjActionButton(
                "##refreshEmbeddedYouTubeAccount",
                FontAwesomeIcon.SyncAlt,
                "Refresh YouTube Session",
                new Vector2(width, Ui(36f)),
                false,
                BeginEmbeddedBrowserYouTubeConnection,
                true);

            ImGui.SameLine(0f, gap);

            DrawDjActionButton(
                "##disconnectEmbeddedYouTubeAccount",
                FontAwesomeIcon.Unlink,
                "Disconnect YouTube Account",
                new Vector2(width, Ui(36f)),
                false,
                DisconnectEmbeddedBrowserYouTubeAccount,
                false);
        }
        else
        {
            DrawDjActionButton(
                "##connectEmbeddedYouTubeAccount",
                FontAwesomeIcon.SignInAlt,
                "Connect YouTube Account",
                new Vector2(
                    ImGui.GetContentRegionAvail().X,
                    Ui(36f)),
                false,
                BeginEmbeddedBrowserYouTubeConnection,
                true);
        }

        if (!string.IsNullOrWhiteSpace(youtubeBrowserConnectionMessage))
        {
            ImGui.Dummy(UiVec(0f, 8f));
            ImGui.TextColored(
                youtubeBrowserConnectionMessageIsError ? Danger : Good,
                youtubeBrowserConnectionMessage);
        }
    }

    private void BeginEmbeddedBrowserYouTubeConnection()
    {
        youtubeBrowserConnectionPending =
            true;
        youtubeBrowserConnectionMessage =
            null;
        youtubeBrowserConnectionMessageIsError =
            false;

        browserAddress =
            YouTubeBrowserSignInUrl;
        currentPage =
            HomePage.Browser;

        var engine =
            screenController.Engine;

        if (engine.Browser is { } browser)
        {
            browser.Navigate(browserAddress);
        }
        else if (!engine.PlayBrowser(browserAddress))
        {
            youtubeBrowserConnectionPending =
                false;
            youtubeBrowserConnectionMessage =
                engine.LastError ??
                "The Alpha Channel browser could not be opened.";
            youtubeBrowserConnectionMessageIsError =
                true;
            currentPage =
                HomePage.Settings;
            settingsTab =
                SettingsTab.Other;
        }
    }

    private void DisconnectEmbeddedBrowserYouTubeAccount()
    {
        YouTubeEmbeddedBrowserSession.Disconnect(
            Plugin.Cfg);

        video.CookiesPath =
            null;
        youtubeBrowserConnectionPending =
            false;
        youtubeBrowserConnectionMessage =
            "YouTube account disconnected. The browser itself remains signed in.";
        youtubeBrowserConnectionMessageIsError =
            false;
    }

    private void DrawPendingYouTubeBrowserConnection(
        BrowserRenderer browser)
    {
        if (!youtubeBrowserConnectionPending)
            return;

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   Ui(8f))
               .Push(
                   ImGuiStyleVar.WindowPadding,
                   UiVec(14f, 9f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(0.075f, 0.06f, 0.12f, 1f))
               .Push(
                   ImGuiCol.Border,
                   new Vector4(Accent.X, Accent.Y, Accent.Z, 0.85f)))
        using (var panel = ImRaii.Child(
                   "##youtubeBrowserConnection",
                   new Vector2(-1f, Ui(56f)),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!panel)
                return;

            var cancelWidth =
                Ui(92f);
            var connectWidth =
                Ui(190f);
            var gap =
                Ui(8f);
            var y =
                Ui(7f);
            var right =
                ImGui.GetWindowContentRegionMax().X;

            ImGui.PushTextWrapPos(
                right - cancelWidth - connectWidth - gap - Ui(20f));
            ImGui.TextColored(
                Vector4.One,
                youtubeBrowserConnectionBusy
                    ? "Checking the YouTube session…"
                    : "Finish signing in, then connect this YouTube account to Alpha Channel.");
            ImGui.PopTextWrapPos();

            ImGui.SetCursorPos(
                new Vector2(
                    right - cancelWidth - connectWidth - gap,
                    y));

            using (ImRaii.Disabled(youtubeBrowserConnectionBusy))
            {
                if (GameLayoutButton(
                        "Use This Account",
                        FontAwesomeIcon.Check,
                        connectWidth,
                        true))
                {
                    _ = CompleteEmbeddedBrowserYouTubeConnectionAsync(
                        browser);
                }

                ImGui.SameLine(0f, gap);

                if (GameLayoutButton(
                        "Cancel",
                        FontAwesomeIcon.Times,
                        cancelWidth))
                {
                    youtubeBrowserConnectionPending =
                        false;
                    youtubeBrowserConnectionMessage =
                        "YouTube account connection cancelled.";
                    youtubeBrowserConnectionMessageIsError =
                        false;
                    currentPage =
                        HomePage.Settings;
                    settingsTab =
                        SettingsTab.Other;
                }
            }
        }
    }

    private async Task CompleteEmbeddedBrowserYouTubeConnectionAsync(
        BrowserRenderer browser)
    {
        if (youtubeBrowserConnectionBusy)
            return;

        youtubeBrowserConnectionBusy =
            true;
        youtubeBrowserConnectionMessage =
            null;

        try
        {
            var result =
                await browser
                    .RequestYouTubeCookiesAsync()
                    .ConfigureAwait(false);

            if (!result.Success ||
                string.IsNullOrWhiteSpace(result.Cookies))
            {
                youtubeBrowserConnectionMessage =
                    result.Error ??
                    "No signed-in YouTube session was found.";
                youtubeBrowserConnectionMessageIsError =
                    true;
                return;
            }

            var path =
                YouTubeEmbeddedBrowserSession.Save(
                    result.Cookies);

            Plugin.Cfg.YouTubeEmbeddedBrowserSessionEnabled =
                true;
            Plugin.Cfg.Save();

            video.CookiesPath =
                path;
            youtubeBrowserConnectionPending =
                false;
            youtubeBrowserConnectionMessage =
                "YouTube account connected. It will only be used when a video requires sign-in.";
            youtubeBrowserConnectionMessageIsError =
                false;
            currentPage =
                HomePage.Settings;
            settingsTab =
                SettingsTab.Other;
        }
        catch (Exception exception)
        {
            youtubeBrowserConnectionMessage =
                exception.Message;
            youtubeBrowserConnectionMessageIsError =
                true;
        }
        finally
        {
            youtubeBrowserConnectionBusy =
                false;
        }
    }
}
