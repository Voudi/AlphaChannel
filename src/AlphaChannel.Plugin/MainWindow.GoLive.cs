using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// "Go Live" self-hosted streaming - OBS pushes RTMP to our own MediaMTX ingest, friends watch the
// resulting HLS stream through the exact same "play a URL" primitive (queue.PlayNow) the existing
// YouTube/Twitch flow already uses. See Server/Live/LiveService.cs for the server half and why the
// stream key format keeps the secret out of the public HLS URL.
internal sealed partial class MainWindow
{
    private bool liveStatusDirty = true;
    private bool liveStatusLoading;
    private LiveStatusDto? liveStatus;
    private bool streamKeyRevealed;
    private bool keyRotating;
    private string? keyError;
    private bool keyRegenerateConfirmPending;

    private bool friendsLiveDirty = true;
    private LiveFriendDto[] friendsLive = [];

    private void DrawGoLive()
    {
        if (CurrentSession is not { } session)
        {
            DrawPlainEmpty(
                "OBS ingest + stream keys live here after you sign in.",
                "Open Settings",
                () => currentPage = HomePage.Settings);

            return;
        }

        if (liveStatusDirty && !liveStatusLoading)
        {
            RefreshLiveStatus(session);
        }

        if (friendsLiveDirty)
        {
            RefreshFriendsLive(session.Token);
        }

        // Keep the Go Live content scrollable without scrolling
        // the Player header/source navigation above it.
        using var content = ImRaii.Child(
            "##goLiveContent",
            new Vector2(-1f, -1f),
            false,
            ImGuiWindowFlags.None);

        if (!content)
        {
            return;
        }

        // ---------------------------------------------------------
        // Heading
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Go Live with OBS");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 10f));

        // ---------------------------------------------------------
        // Stream status
        // ---------------------------------------------------------

        if (liveStatus is null)
        {
            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(0.045f, 0.06f, 0.10f, 1f)))
            using (var statusCard = ImRaii.Child(
                "##goLiveStatus",
                new Vector2(-1f, 74f),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (statusCard)
                {
                    ImGui.SetCursorPos(
                        new Vector2(14f, 16f));

                    ImGui.TextColored(
                        MutedText,
                        liveStatusLoading
                            ? "Loading stream status..."
                            : "Stream status unavailable.");
                }
            }
        }
        else
        {
            var status = liveStatus;

            // KEEP YOUR EXISTING 94px status card HERE
        }

        ImGui.Dummy(new Vector2(0f, 18f));

        // ---------------------------------------------------------
        // OBS setup
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(1.08f);

        ImGui.TextColored(
            Vector4.One,
            "OBS setup");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 4f));

        ImGui.SetWindowFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "Connect OBS using the server and stream key below.");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 14f));

        // ---------------------------------------------------------
        // Server
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(0.88f);

        ImGui.TextColored(
            MutedText,
            "Server");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 4f));

        var rtmpServer = BuildRtmpServer();

        ImGui.SetNextItemWidth(-126f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                new Vector2(14f, 10f)))
        using (ImRaii.PushColor(
            ImGuiCol.FrameBg,
            new Vector4(0.045f, 0.06f, 0.105f, 1f))
            .Push(
                ImGuiCol.FrameBgHovered,
                new Vector4(0.045f, 0.06f, 0.105f, 1f))
            .Push(
                ImGuiCol.FrameBgActive,
                new Vector4(0.045f, 0.06f, 0.105f, 1f)))
        {
            ImGui.InputText(
                "##rtmpServer",
                ref rtmpServer,
                256,
                ImGuiInputTextFlags.ReadOnly);
        }

        ImGui.SameLine(0f, 10f);

        // Copy server button
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            new Vector4(0.055f, 0.07f, 0.115f, 1f))
            .Push(
                ImGuiCol.ButtonHovered,
                new Vector4(0.075f, 0.095f, 0.15f, 1f))
            .Push(
                ImGuiCol.ButtonActive,
                new Vector4(0.075f, 0.095f, 0.15f, 1f)))
        {
            var buttonPos =
                ImGui.GetCursorScreenPos();

            var buttonSize =
                new Vector2(110f, 38f);

            if (ImGui.Button(
                "##copyServer",
                buttonSize))
            {
                ImGui.SetClipboardText(
                    rtmpServer);
            }

            DrawPlayerActionButtonContent(
                buttonPos,
                buttonSize,
                FontAwesomeIcon.Copy,
                "Copy",
                Vector4.One);
        }

        ImGui.Dummy(new Vector2(0f, 16f));

        // ---------------------------------------------------------
        // Stream key
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(0.88f);

        ImGui.TextColored(
            MutedText,
            "Stream key");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 4f));

        var cachedKey =
            Plugin.Cfg.StreamKeys.GetValueOrDefault(
                session.AccountId);

        if (cachedKey is null)
        {
            ImGui.SetWindowFontScale(0.82f);

            ImGui.TextColored(
                MutedText,
                liveStatus?.HasKey ?? false
                    ? "A stream key exists on another install. Regenerate it to use it here."
                    : "No stream key yet. Generate one to connect OBS.");

            ImGui.SetWindowFontScale(1f);

            ImGui.Dummy(new Vector2(0f, 8f));
        }
        else
        {
            var displayKey =
                streamKeyRevealed
                    ? cachedKey
                    : new string(
                        '•',
                        Math.Min(
                            cachedKey.Length,
                            32));

            ImGui.SetNextItemWidth(-1f);

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f)
                .Push(
                    ImGuiStyleVar.FramePadding,
                    new Vector2(14f, 10f)))
            using (ImRaii.PushColor(
                ImGuiCol.FrameBg,
                new Vector4(0.045f, 0.06f, 0.105f, 1f))
                .Push(
                    ImGuiCol.FrameBgHovered,
                    new Vector4(0.045f, 0.06f, 0.105f, 1f))
                .Push(
                    ImGuiCol.FrameBgActive,
                    new Vector4(0.045f, 0.06f, 0.105f, 1f)))
            {
                ImGui.InputText(
                    "##streamKey",
                    ref displayKey,
                    256,
                    ImGuiInputTextFlags.ReadOnly);
            }

            ImGui.Dummy(new Vector2(0f, 8f));

            // Reveal / Hide
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(0.055f, 0.07f, 0.115f, 1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(0.075f, 0.095f, 0.15f, 1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(0.075f, 0.095f, 0.15f, 1f)))
            {
                var buttonPos =
                    ImGui.GetCursorScreenPos();

                var buttonSize =
                    new Vector2(110f, 34f);

                if (ImGui.Button(
                    "##toggleStreamKey",
                    buttonSize))
                {
                    streamKeyRevealed =
                        !streamKeyRevealed;
                }

                DrawPlayerActionButtonContent(
                    buttonPos,
                    buttonSize,
                    streamKeyRevealed
                        ? FontAwesomeIcon.EyeSlash
                        : FontAwesomeIcon.Eye,
                    streamKeyRevealed
                        ? "Hide"
                        : "Reveal",
                    Vector4.One);
            }

            ImGui.SameLine(0f, 8f);

            // Copy key
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(0.055f, 0.07f, 0.115f, 1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(0.075f, 0.095f, 0.15f, 1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(0.075f, 0.095f, 0.15f, 1f)))
            {
                var buttonPos =
                    ImGui.GetCursorScreenPos();

                var buttonSize =
                    new Vector2(118f, 34f);

                if (ImGui.Button(
                    "##copyStreamKey",
                    buttonSize))
                {
                    ImGui.SetClipboardText(
                        cachedKey);
                }

                DrawPlayerActionButtonContent(
                    buttonPos,
                    buttonSize,
                    FontAwesomeIcon.Copy,
                    "Copy key",
                    Vector4.One);
            }

            ImGui.SameLine(0f, 8f);
        }

        // Generate / Regenerate
        using (ImRaii.Disabled(keyRotating))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            new Vector4(0.055f, 0.07f, 0.115f, 1f))
            .Push(
                ImGuiCol.ButtonHovered,
                new Vector4(0.075f, 0.095f, 0.15f, 1f))
            .Push(
                ImGuiCol.ButtonActive,
                new Vector4(0.075f, 0.095f, 0.15f, 1f)))
        {
            var buttonPos =
                ImGui.GetCursorScreenPos();

            var buttonSize =
                new Vector2(142f, 34f);

            if (ImGui.Button(
                "##regenerateStreamKey",
                buttonSize))
            {
                if (cachedKey is null)
                {
                    RotateStreamKey(session);
                }
                else
                {
                    keyRegenerateConfirmPending =
                        true;
                }
            }

            DrawPlayerActionButtonContent(
                buttonPos,
                buttonSize,
                FontAwesomeIcon.SyncAlt,
                cachedKey is null
                    ? "Generate"
                    : "Regenerate",
                Vector4.One);
        }

        // Regenerate confirmation
        if (keyRegenerateConfirmPending)
        {
            ImGui.Dummy(
                new Vector2(0f, 10f));

            ImGui.TextColored(
                Danger,
                "Regenerating disconnects OBS sessions using the old key. Continue?");

            ImGui.Dummy(
                new Vector2(0f, 6f));

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                Danger))
            {
                if (ImGui.Button(
                    "Yes, regenerate"))
                {
                    keyRegenerateConfirmPending =
                        false;

                    RotateStreamKey(session);
                }
            }

            ImGui.SameLine(0f, 8f);

            if (ImGui.Button("Cancel"))
            {
                keyRegenerateConfirmPending =
                    false;
            }
        }

        if (keyError is { Length: > 0 } error)
        {
            ImGui.Dummy(
                new Vector2(0f, 8f));

            ImGui.TextColored(
                Danger,
                error);
        }

        ImGui.Dummy(
            new Vector2(0f, 22f));

        // ---------------------------------------------------------
        // Friends live
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(1.08f);

        ImGui.TextColored(
            Vector4.One,
            $"Friends live ({friendsLive.Length})");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(0f, 8f));

        if (friendsLive.Length == 0)
        {
            ImGui.SetWindowFontScale(0.88f);

            ImGui.TextColored(
                MutedText,
                "Nobody you know is live.");

            ImGui.SetWindowFontScale(1f);

            return;
        }

        foreach (var friend in friendsLive)
        {
            ImGui.PushID(
                friend.AccountId);

            const float rowHeight = 58f;

            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(0.045f, 0.06f, 0.10f, 1f)))
            using (var row = ImRaii.Child(
                $"##friendLive_{friend.AccountId}",
                new Vector2(-6f, rowHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (row)
                {
                    var origin =
                        ImGui.GetCursorScreenPos();

                    // Live dot
                    using (ImRaii.PushFont(
                        UiBuilder.IconFont))
                    {
                        ImGui.GetWindowDrawList()
                            .AddText(
                                origin +
                                new Vector2(14f, 21f),
                                ImGui.GetColorU32(Good),
                                FontAwesomeIcon.Circle
                                    .ToIconString());
                    }

                    // Friend name
                    ImGui.GetWindowDrawList()
                        .AddText(
                            origin +
                            new Vector2(38f, 20f),
                            ImGui.GetColorU32(
                                Vector4.One),
                            friend.DisplayName);

                    // Watch button
                    var watchSize =
                        new Vector2(104f, 34f);

                    var watchPos =
                        new Vector2(
                            origin.X +
                            ImGui.GetWindowWidth() -
                            116f,
                            origin.Y +
                            (rowHeight -
                             watchSize.Y) *
                            0.5f);

                    ImGui.SetCursorScreenPos(
                        watchPos);

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

                        if (ImGui.Button(
                            $"##watch_{friend.AccountId}",
                            watchSize))
                        {
                            queue.PlayNow(
                                new VideoQueueEntry(
                                    friend.HlsUrl,
                                    $"{friend.DisplayName}'s stream",
                                    "Live",
                                    null,
                                    null));

                            currentPage =
                                HomePage.Player;
                        }

                        DrawPlayerActionButtonContent(
                            buttonPos,
                            watchSize,
                            FontAwesomeIcon.Play,
                            "Watch",
                            Vector4.One);
                    }
                }
            }

            ImGui.PopID();

            ImGui.Dummy(
                new Vector2(0f, 8f));
        }
    }

    private string BuildRtmpServer()
    {
        var host = new Uri(Plugin.Cfg.RelayServerUrl).Host;
        return $"rtmp://{host}:1935/live";
    }

    private void RotateStreamKey(CharacterSession session)
    {
        keyRotating = true;
        keyError = null;
        var token = session.Token;
        var accountId = session.AccountId;
        _ = Task.Run(async () =>
        {
            var key = await liveClient.RotateKeyAsync(token);
            keyRotating = false;
            if (key is null)
            {
                keyError = "Couldn't generate a stream key.";
                return;
            }

            Plugin.Cfg.StreamKeys[accountId] = key;
            Plugin.Cfg.Save();
            streamKeyRevealed = true;
            liveStatusDirty = true;
        });
    }

    private void RefreshLiveStatus(CharacterSession session)
    {
        liveStatusDirty = false;
        liveStatusLoading = true;
        var token = session.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                liveStatus = await liveClient.GetMyStatusAsync(token);
            }
            finally
            {
                liveStatusLoading = false;
            }
        });
    }

    private void RefreshFriendsLive(string bearerToken)
    {
        friendsLiveDirty = false;
        _ = Task.Run(async () => friendsLive = await liveClient.GetFriendsLiveAsync(bearerToken));
    }
}
