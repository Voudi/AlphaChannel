using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Game.Config;

namespace AlphaChannel.Plugin;

// Watch party lives on Player: host/join/roster + ephemeral room chat (stream.chat).
internal sealed partial class MainWindow
{
    private string partyJoinInput = string.Empty;
    private readonly List<(string Name, string Text)> partyChatLines = [];
    private string partyChatInput = string.Empty;
    private bool partyChatStickToBottom = true;

    private enum PartyPanelTab
    {
        NowPlaying,
        ChatReact,
    }

    private PartyPanelTab partyPanelTab = PartyPanelTab.NowPlaying;

    private void DrainPartyChat()
    {
        while (stream.IncomingChat.TryDequeue(out var line))
        {
            partyChatLines.Add(line);

            if (Plugin.Cfg.RelayPartyChatToGameChat)
            {
                Plugin.ChatGui.Print(
                   $"[Alpha Channel Party] {line.DisplayName}: {line.Text}");
            }
            if (partyChatLines.Count > 200)
            {
                partyChatLines.RemoveRange(0, partyChatLines.Count - 200);
            }

            partyChatStickToBottom = true;
        }

        if (stream.Mode == StreamMode.None && partyChatLines.Count > 0)
        {
            partyChatLines.Clear();
        }
    }

    private void DrawPartyPanel()
    {
        if (CurrentSession is null)
        {
            DrawLegacyPartyPanel();
            return;
        }

        if (stream.Mode is not (StreamMode.Hosting or StreamMode.Viewing))
        {
            DrawLegacyPartyPanel();
            return;
        }

        DrawPartyHeaderCard();

        ImGui.Dummy(new Vector2(0f, 12f));

        DrawPartyTabButtons();

        ImGui.Dummy(new Vector2(0f, 10f));

        switch (partyPanelTab)
        {
            case PartyPanelTab.NowPlaying:
                DrawPartyNowPlayingTab();
                break;

            case PartyPanelTab.ChatReact:
                DrawPartyChatReactTab();
                break;
        }
    }

    private void DrawPartyHeaderCard()
    {
        var isHost =
            stream.Mode == StreamMode.Hosting;

        var hostName =
            isHost
                ? CurrentDisplayName ?? "You"
                : joinedHostDisplayName ?? "Host";

        // TEMP: room name is still visual-only until real room metadata exists.
        var roomName =
            $"{hostName}'s Watch Party";

        // TEMP: description backend field does not exist yet.
        const string roomDescription =
            "Just hanging out watching together.";

        var isPrivate =
            stream.IsPrivate;

        const float cardHeight = 150f;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            14f))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.WindowPadding,
            new Vector2(20f, 16f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.05f, 0.09f, 1f)))
        using (var card = ImRaii.Child(
            "##partyHeaderCard",
            new Vector2(-1f, cardHeight),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card)
            {
                return;
            }

            var cardPos =
                ImGui.GetWindowPos();

            var cardSize =
                ImGui.GetWindowSize();

            ImGui.GetWindowDrawList().AddRect(
                cardPos,
                cardPos + cardSize,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.40f)),
                14f,
                ImDrawFlags.RoundCornersAll,
                1.2f);

            //
            // Room title
            //
            ImGui.SetWindowFontScale(1.45f);

            ImGui.TextColored(
                Vector4.One,
                roomName);

            ImGui.SetWindowFontScale(1f);

            //
            // Leave button - always visible.
            //
            const float leaveWidth = 132f;

            ImGui.SetCursorPos(
                new Vector2(
                    ImGui.GetWindowWidth() - leaveWidth - 18f,
                    14f));

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.16f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(
                        Danger.X,
                        Danger.Y,
                        Danger.Z,
                        0.28f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(
                        Danger.X,
                        Danger.Y,
                        Danger.Z,
                        0.38f)))
            {
                if (ImGui.Button(
                    "Leave Watch Party",
                    new Vector2(leaveWidth, 32f)))
                {
                    LeaveStream();
                    partyChatLines.Clear();
                    return;
                }
            }

            //
            // Host identity row
            //
            ImGui.SetCursorPos(
                new Vector2(20f, 52f));

            if (isHost)
            {
                DrawAvatarChip(
                    CurrentSession.AvatarIcon,
                    CurrentSession.AvatarColorHex,
                    42,
                    CurrentSession.AvatarImageUrl);
            }
            else
            {
                // TEMP: the watch-party realtime roster currently does not
                // expose the host's Alpha Channel avatar.
                var avatarOrigin =
                    ImGui.GetCursorScreenPos();

                ImGui.GetWindowDrawList().AddCircleFilled(
                    avatarOrigin + new Vector2(21f, 21f),
                    21f,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.20f)));

                ImGui.SetCursorScreenPos(
                    avatarOrigin + new Vector2(12f, 11f));

                using (ImRaii.PushFont(
                    UiBuilder.IconFont))
                {
                    ImGui.TextColored(
                        Accent,
                        FontAwesomeIcon.User.ToIconString());
                }

                ImGui.SetCursorScreenPos(
                    avatarOrigin + new Vector2(42f, 0f));
            }

            ImGui.SameLine(0f, 10f);

            ImGui.BeginGroup();

            ImGui.TextColored(
                Vector4.One,
                $"Hosted by {hostName}");

            ImGui.SetWindowFontScale(0.88f);

            ImGui.TextColored(
                Good,
                $"● {stream.Roster.Length} watching");

            ImGui.SetWindowFontScale(1f);

            ImGui.EndGroup();

            //
            // Privacy
            //
            ImGui.SetCursorPos(
                new Vector2(
                    ImGui.GetWindowWidth() - 158f,
                    63f));

            if (isHost)
            {
                if (ImGui.Checkbox(
                    "Private party",
                    ref isPrivate))
                {
                    stream.IsPrivate =
                        isPrivate;
                }
            }
            else
            {
                ImGui.TextColored(
                    MutedText,
                    isPrivate
                        ? "Private party"
                        : "Public party");
            }

            //
            // Description
            //
            ImGui.SetCursorPos(
                new Vector2(20f, 112f));

            ImGui.TextColored(
                MutedText,
                roomDescription);

            if (isHost)
            {
                ImGui.SameLine(0f, 8f);

                using (ImRaii.PushFont(
                    UiBuilder.IconFont))
                {
                    ImGui.TextColored(
                        Accent,
                        FontAwesomeIcon.PencilAlt.ToIconString());
                }
            }
        }
    }

    private void DrawPartyTabButtons()
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        const float gap = 8f;

        var tabWidth =
            (width - gap) / 2f;

        DrawPartyTabButton(
            PartyPanelTab.NowPlaying,
            FontAwesomeIcon.Tv,
            "Now Playing / TV",
            tabWidth);

        ImGui.SameLine(0f, gap);

        DrawPartyTabButton(
            PartyPanelTab.ChatReact,
            FontAwesomeIcon.Comments,
            "Chat / React",
            tabWidth);
    }

    private void DrawPartyTabButton(
     PartyPanelTab tab,
     FontAwesomeIcon icon,
     string label,
     float width)
    {
        var selected =
            partyPanelTab == tab;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                new Vector2(12f, 9f)))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            selected
                ? Accent
                : new Vector4(
                    0.045f,
                    0.05f,
                    0.085f,
                    1f))
            .Push(
                ImGuiCol.ButtonHovered,
                selected
                    ? AccentHover
                    : new Vector4(
                        0.075f,
                        0.065f,
                        0.13f,
                        1f))
            .Push(
                ImGuiCol.ButtonActive,
                selected
                    ? AccentActive
                    : new Vector4(
                        0.09f,
                        0.075f,
                        0.15f,
                        1f)))
        {
            //
            // Draw an invisible-label button first.
            //
            if (ImGui.Button(
                $"##partyTab_{tab}",
                new Vector2(width, 40f)))
            {
                partyPanelTab = tab;
            }

            var buttonMin =
                ImGui.GetItemRectMin();

            var buttonMax =
                ImGui.GetItemRectMax();

            var centerY =
                buttonMin.Y +
                (buttonMax.Y - buttonMin.Y) * 0.5f;

            var iconText =
                icon.ToIconString();

            Vector2 iconSize;

            //
            // Measure icon using the actual icon font.
            //
            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                iconSize =
                    ImGui.CalcTextSize(
                        iconText);
            }

            //
            // Measure label using the normal font.
            //
            Vector2 labelSize;

            using (ImRaii.PushFont(
                UiBuilder.DefaultFont))
            {
                labelSize =
                    ImGui.CalcTextSize(
                        label);
            }

            const float gap = 8f;

            var totalWidth =
                iconSize.X +
                gap +
                labelSize.X;

            var startX =
                buttonMin.X +
                ((buttonMax.X - buttonMin.X) - totalWidth) * 0.5f;

            //
            // Icon
            //
            ImGui.GetWindowDrawList().AddText(
                UiBuilder.IconFont,
                ImGui.GetFontSize(),
                new Vector2(
                    startX,
                    centerY - iconSize.Y * 0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                iconText);

            //
            // Normal label
            //
            ImGui.GetWindowDrawList().AddText(
                UiBuilder.DefaultFont,
                ImGui.GetFontSize(),
                new Vector2(
                    startX + iconSize.X + gap,
                    centerY - labelSize.Y * 0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                label);
        }
    }

    private static bool IsFfxivSoundMuted()
    {
        return Plugin.GameConfig.TryGet(
                   SystemConfigOption.IsSndMaster,
                   out uint muted)
               && muted != 0;
    }

    private static void SetFfxivSoundMuted(
        bool muted)
    {
        Plugin.GameConfig.Set(
            SystemConfigOption.IsSndMaster,
            muted ? 1u : 0u);
    }

    private void DrawPartyTvSpawnButton()
    {
        if (stream.Mode is not (StreamMode.Viewing or StreamMode.Hosting))
        {
            return;
        }

        var isHost =
            stream.Mode == StreamMode.Hosting;

        var tvSpawned =
            isHost
                ? screenController.Engine.IsActive
                : ViewerTvEnabled;

        var label =
            tvSpawned
                ? "Despawn TV"
                : "Spawn TV";

        var buttonColor =
            tvSpawned
                ? new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.55f)
                : Accent;

        var buttonHover =
            tvSpawned
                ? new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.72f)
                : AccentHover;

        var buttonActive =
            tvSpawned
                ? new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.85f)
                : AccentActive;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            buttonColor)
            .Push(
                ImGuiCol.ButtonHovered,
                buttonHover)
            .Push(
                ImGuiCol.ButtonActive,
                buttonActive))
        {
            if (!ImGui.Button(
                    label,
                    new Vector2(120f, 34f)))
            {
                return;
            }

            //
            // VIEWER
            //
            if (!isHost)
            {
                if (ViewerTvEnabled)
                {
                    ViewerTvEnabled = false;
                    video.Stop();
                }
                else
                {
                    ViewerTvEnabled = true;
                    OnViewerTvSpawnRequested?.Invoke();
                }

                return;
            }

            //
            // HOST
            //
            var engine =
                screenController.Engine;

            //
            // TV currently despawned:
            // simply put the existing screen back.
            //
            if (!engine.IsActive)
            {
                engine.RespawnScreen();
                return;
            }

            //
            // TV currently spawned:
            // don't allow the host to remove it while
            // media is actively playing.
            //
            if (queue.Current is not null)
            {
                var (_, _, isPaused) =
                    video.GetProgress();

                if (!isPaused)
                {
                    Plugin.ChatGui.Print(
                        "[Alpha Channel] Host can't despawn TV during playback. Pause playback first.");

                    return;
                }
            }

            //
            // Nothing playing, or current media is paused.
            //
            engine.DespawnScreen();
        }
    }

    private void DrawPartyNowPlayingTab()
    {
        var current =
            queue.Current;

        var (position, duration, isPaused) =
            video.GetProgress();

        var width =
            ImGui.GetContentRegionAvail().X;

        const float gap = 12f;

        var mediaWidth =
            width * 0.62f;

        var tvWidth =
            width - mediaWidth - gap;

        //
        // LEFT — Now Playing
        //
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            12f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(16f, 14f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.04f,
                0.07f,
                1f)))
        using (var mediaPanel = ImRaii.Child(
            "##partyNowPlayingMedia",
            new Vector2(mediaWidth, 410f),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (mediaPanel)
            {
                DrawPartyNowPlayingMedia(
                    current,
                    position,
                    duration,
                    isPaused);
            }
        }

        ImGui.SameLine(0f, gap);

        //
        // RIGHT — TV Status
        //
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            12f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(16f, 14f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.04f,
                0.07f,
                1f)))
        using (var tvPanel = ImRaii.Child(
            "##partyTvStatusPanel",
            new Vector2(tvWidth, 410f),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (tvPanel)
            {
                DrawPartyTvStatus(
                    current,
                    isPaused);
            }
        }
    }

    private void DrawPartyNowPlayingMedia(
    Video.VideoQueueEntry? current,
    float position,
    float duration,
    bool isPaused)
    {
        DrawSectionTitle(
            FontAwesomeIcon.PlayCircle,
            "Now Playing");

        ImGui.Dummy(
            new Vector2(0f, 8f));

        //
        // Nothing currently playing.
        //
        if (current is null)
        {
            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                10f)
                .Push(
                    ImGuiStyleVar.WindowPadding,
                    new Vector2(18f, 18f)))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    0.045f,
                    0.05f,
                    0.085f,
                    1f)))
            using (var empty = ImRaii.Child(
                "##partyNoMedia",
                new Vector2(-1f, 128f),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (empty)
                {
                    using (ImRaii.PushFont(
                        UiBuilder.IconFont))
                    {
                        ImGui.SetWindowFontScale(1.35f);

                        ImGui.TextColored(
                            MutedText,
                            FontAwesomeIcon.PlayCircle.ToIconString());

                        ImGui.SetWindowFontScale(1f);
                    }

                    ImGui.Dummy(
                        new Vector2(0f, 7f));

                    ImGui.TextColored(
                        Vector4.One,
                        "Nothing is playing");

                    ImGui.SetWindowFontScale(0.84f);

                    ImGui.TextColored(
                        MutedText,
                        stream.Mode == StreamMode.Hosting
                            ? "Choose some media when you're ready."
                            : "Waiting for the host to start something.");

                    ImGui.SetWindowFontScale(1f);
                }
            }

            return;
        }

        //
        // Media card
        //
        const float cardHeight = 160f;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(12f, 12f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.045f,
                0.05f,
                0.085f,
                1f)))
        using (var media = ImRaii.Child(
            "##partyCurrentMedia",
            new Vector2(-1f, cardHeight),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (media)
            {
                var origin =
                    ImGui.GetCursorScreenPos();

                var drawList =
                    ImGui.GetWindowDrawList();

                const float thumbWidth = 210f;
                const float thumbHeight = 118f;

                var thumbMin =
                    origin;

                var thumbMax =
                    origin +
                    new Vector2(
                        thumbWidth,
                        thumbHeight);

                drawList.AddRectFilled(
                    thumbMin,
                    thumbMax,
                    ImGui.GetColorU32(
                        new Vector4(
                            0.025f,
                            0.03f,
                            0.05f,
                            1f)),
                    8f);

                var thumbnail =
                    thumbnails.Get(
                        current.ThumbnailUrl);

                if (thumbnail is not null)
                {
                    drawList.AddImageRounded(
                        thumbnail.Handle,
                        thumbMin,
                        thumbMax,
                        Vector2.Zero,
                        Vector2.One,
                        uint.MaxValue,
                        8f);
                }
                else
                {
                    using (ImRaii.PushFont(
                        UiBuilder.IconFont))
                    {
                        var icon =
                            FontAwesomeIcon.Play.ToIconString();

                        var iconSize =
                            ImGui.CalcTextSize(icon);

                        drawList.AddText(
                            thumbMin +
                            (thumbMax - thumbMin) / 2f -
                            iconSize / 2f,
                            ImGui.GetColorU32(
                                Accent),
                            icon);
                    }
                }

                var contentX =
                    origin.X +
                    thumbWidth +
                    16f;

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        contentX,
                        origin.Y + 6f));

                ImGui.SetWindowFontScale(1.12f);

                ImGui.PushTextWrapPos(
                    origin.X +
                    ImGui.GetWindowWidth() -
                    20f);

                ImGui.TextColored(
                    Vector4.One,
                    current.Title);

                ImGui.PopTextWrapPos();

                ImGui.SetWindowFontScale(1f);

                if (!string.IsNullOrWhiteSpace(
                        current.Source))
                {
                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            origin.Y + 56f));

                    ImGui.SetWindowFontScale(0.86f);

                    ImGui.TextColored(
                        MutedText,
                        current.Source);

                    ImGui.SetWindowFontScale(1f);
                }

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        contentX,
                        origin.Y + 86f));

                using (ImRaii.PushFont(
                    UiBuilder.IconFont))
                {
                    ImGui.TextColored(
                        isPaused
                            ? Gold
                            : Good,
                        isPaused
                            ? FontAwesomeIcon.Pause.ToIconString()
                            : FontAwesomeIcon.Play.ToIconString());
                }

                ImGui.SameLine(0f, 7f);

                ImGui.TextColored(
                    isPaused
                        ? Gold
                        : Good,
                    isPaused
                        ? "Paused"
                        : "Playing");

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        contentX,
                        origin.Y + 111f));

                ImGui.SetWindowFontScale(0.82f);

                ImGui.TextColored(
                    MutedText,
                    duration > 0f
                        ? $"{FormatTime(position)} / {FormatTime(duration)}"
                        : "Live");

                ImGui.SetWindowFontScale(1f);
            }
        }

        ImGui.Dummy(
            new Vector2(0f, 12f));

        //
        // Progress — display only.
        // No playback controls on this page.
        //
        if (duration > 0f)
        {
            var progress =
                Math.Clamp(
                    position / duration,
                    0f,
                    1f);

            ImGui.ProgressBar(
                progress,
                new Vector2(-1f, 7f),
                string.Empty);
        }

        ImGui.Dummy(
            new Vector2(0f, 8f));

        ImGui.SetWindowFontScale(0.80f);

        ImGui.TextColored(
            MutedText,
            stream.Mode == StreamMode.Hosting
                ? "Playback is synced to everyone in the watch party."
                : "Playback is controlled by the host.");

        ImGui.SetWindowFontScale(1f);
    }

    private void DrawPartyTvStatus(
    Video.VideoQueueEntry? current,
    bool isPaused)
    {
        DrawSectionTitle(
            FontAwesomeIcon.Tv,
            "Your TV");

        ImGui.Dummy(
            new Vector2(0f, 8f));

        //
        // Status indicator.
        //
        var tvSpawned =
            stream.Mode == StreamMode.Hosting
                ? screenController.Engine.IsActive
                : ViewerTvEnabled;

        var statusColor =
            tvSpawned
                ? Good
                : MutedText;

        var statusText =
            !tvSpawned
                ? "TV not spawned"
                : current is null
                    ? "TV ready"
                    : isPaused
                        ? "Paused"
                        : "Playing";

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(14f, 14f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.045f,
                0.05f,
                0.085f,
                1f)))
        using (var status = ImRaii.Child(
            "##partyTvState",
            new Vector2(-1f, 92f),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (status)
            {
                using (ImRaii.PushFont(
                    UiBuilder.IconFont))
                {
                    ImGui.SetWindowFontScale(1.15f);

                    ImGui.TextColored(
                        statusColor,
                        tvSpawned
                            ? FontAwesomeIcon.Tv.ToIconString()
                            : FontAwesomeIcon.Tv.ToIconString());

                    ImGui.SetWindowFontScale(1f);
                }

                ImGui.SameLine(0f, 10f);

                ImGui.BeginGroup();

                ImGui.TextColored(
                    Vector4.One,
                    statusText);

                ImGui.SetWindowFontScale(0.80f);

                ImGui.TextColored(
                    MutedText,
                    tvSpawned
                        ? "The watch-party screen is visible."
                        : "Spawn the screen when you're ready to watch.");

                ImGui.SetWindowFontScale(1f);

                ImGui.EndGroup();
            }
        }

        ImGui.Dummy(
            new Vector2(0f, 12f));

        DrawPartyTvSpawnButton();

        ImGui.Dummy(
            new Vector2(0f, 18f));

        //
        // Local TV volume
        //

        ImGui.SetWindowFontScale(0.88f);

        ImGui.TextColored(
            Vector4.One,
            "TV volume");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(0f, 6f));

        var volume = Plugin.Cfg.Volume;

        ImGui.SetNextItemWidth(
            MathF.Max(
                80f,
                ImGui.GetContentRegionAvail().X - 58f));

        if (ImGui.SliderInt(
                "##partyTvVolume",
                ref volume,
                0,
                130,
                ""))
        {
            Plugin.Cfg.Volume = volume;

            video.SetVolume(
                Plugin.Cfg.Muted
                    ? 0
                    : volume);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Plugin.Cfg.Save();
        }

        ImGui.SameLine(0f, 8f);

        ImGui.TextColored(
            volume > 100
                ? Gold
                : Vector4.One,
            $"{volume}%");

        ImGui.Dummy(
            new Vector2(0f, 8f));
        //
        // Mute FFXIV Control
        //
        ImGui.Dummy(
    new Vector2(0f, 10f));

        var ffxivMuted =
            IsFfxivSoundMuted();

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            ffxivMuted
                ? new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.45f)
                : new Vector4(
                    0.055f,
                    0.07f,
                    0.115f,
                    1f))
            .Push(
                ImGuiCol.ButtonHovered,
                ffxivMuted
                    ? new Vector4(
                        Danger.X,
                        Danger.Y,
                        Danger.Z,
                        0.62f)
                    : new Vector4(
                        0.075f,
                        0.095f,
                        0.15f,
                        1f))
            .Push(
                ImGuiCol.ButtonActive,
                ffxivMuted
                    ? new Vector4(
                        Danger.X,
                        Danger.Y,
                        Danger.Z,
                        0.78f)
                    : new Vector4(
                        0.075f,
                        0.095f,
                        0.15f,
                        1f)))
        {
            var buttonSize =
                new Vector2(
                    180f,
                    34f);

            var buttonPos =
                ImGui.GetCursorScreenPos();

            if (ImGui.Button(
                "##toggleFfxivSound",
                buttonSize))
            {
                SetFfxivSoundMuted(
                    !ffxivMuted);
            }

            DrawPlayerActionButtonContent(
                buttonPos,
                buttonSize,
                ffxivMuted
                    ? FontAwesomeIcon.VolumeUp
                    : FontAwesomeIcon.VolumeMute,
                ffxivMuted
                    ? "Restore FFXIV Sounds"
                    : "Mute FFXIV Sounds",
                Vector4.One);
        }
        //
        // TV mute
        //

        var muted = Plugin.Cfg.Muted;

        if (ImGui.Checkbox(
                "Mute TV",
                ref muted))
        {
            Plugin.Cfg.Muted = muted;

            video.SetVolume(
                muted
                    ? 0
                    : Plugin.Cfg.Volume);

            Plugin.Cfg.Save();
        }

        ImGui.Dummy(
            new Vector2(0f, 20f));

        //
        // Position sync — UI placeholder for the moment.
        //
        ImGui.SetWindowFontScale(0.88f);

        ImGui.TextColored(
            Vector4.One,
            "Screen placement");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(0f, 6f));

        var syncPosition = false;

        using (ImRaii.Disabled(
            stream.Mode == StreamMode.Hosting))
        {
            ImGui.Checkbox(
                "Sync TV position / size from host",
                ref syncPosition);
        }

        ImGui.SetWindowFontScale(0.76f);

        ImGui.TextColored(
            MutedText,
            stream.Mode == StreamMode.Hosting
                ? "Viewers can optionally match your TV placement."
                : "Placement sync will be connected next.");

        ImGui.SetWindowFontScale(1f);
    }

    private void DrawPartyChatReactTab()
    {
        DrainPartyChat();

        var width =
            ImGui.GetContentRegionAvail().X;

        const float gap = 12f;

        var leftWidth =
            width * 0.62f;

        var rightWidth =
            width - leftWidth - gap;

        //
        // LEFT — Chat
        //
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            12f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(16f, 14f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.04f,
                0.07f,
                1f)))
        using (var chatPanel = ImRaii.Child(
            "##partyChatPanel",
            new Vector2(leftWidth, 410f),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (chatPanel)
            {
                DrawPartyChat();
            }
        }

        ImGui.SameLine(0f, gap);

        //
        // RIGHT — Members + reactions
        //
        using (var rightColumn = ImRaii.Child(
            "##partySocialRight",
            new Vector2(rightWidth, 410f),
            false,
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (rightColumn)
            {
                //
                // Members
                //
                using (ImRaii.PushStyle(
                    ImGuiStyleVar.ChildRounding,
                    12f)
                    .Push(
                        ImGuiStyleVar.WindowPadding,
                        new Vector2(14f, 12f)))
                using (ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    new Vector4(
                        0.035f,
                        0.04f,
                        0.07f,
                        1f)))
                using (var members = ImRaii.Child(
                    "##partyMembersPanel",
                    new Vector2(-1f, 205f),
                    false,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
                {
                    if (members)
                    {
                        DrawRoster(
                            $"Watching ({stream.Roster.Length})",
                            allowPromote:
                                stream.Mode == StreamMode.Hosting);
                    }
                }

                ImGui.Dummy(
                    new Vector2(0f, 10f));

                //
                // Reactions
                //
                using (ImRaii.PushStyle(
                    ImGuiStyleVar.ChildRounding,
                    12f)
                    .Push(
                        ImGuiStyleVar.WindowPadding,
                        new Vector2(14f, 12f)))
                using (ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    new Vector4(
                        0.035f,
                        0.04f,
                        0.07f,
                        1f)))
                using (var reactions = ImRaii.Child(
                    "##partyReactionsPanel",
                    new Vector2(-1f, 195f),
                    false,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
                {
                    if (reactions)
                    {
                        DrawReactions();
                    }
                }
            }
        }
    }

    private void DrawPartyTabPlaceholder(
        string id,
        FontAwesomeIcon icon,
        string title,
        string description)
    {
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            12f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.04f,
                0.07f,
                1f)))
        using (var panel = ImRaii.Child(
            id,
            new Vector2(-1f, 410f),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!panel)
            {
                return;
            }

            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    Accent,
                    icon.ToIconString());
            }

            ImGui.SameLine(0f, 8f);

            ImGui.SetWindowFontScale(1.15f);

            ImGui.TextColored(
                Vector4.One,
                title);

            ImGui.SetWindowFontScale(1f);

            ImGui.Dummy(
                new Vector2(0f, 6f));

            ImGui.TextColored(
                MutedText,
                description);
        }
    }

    private void DrawLegacyPartyPanel()
    {
        if (CurrentSession is null)
        {
            ImGui.SetWindowFontScale(1.15f);

            ImGui.TextColored(
                Vector4.One,
                "Watch party");

            ImGui.SetWindowFontScale(1f);

            ImGui.Dummy(new Vector2(0f, 6f));

            ImGui.TextColored(
                MutedText,
                "Sign in to host or join a synced watch party.");

            ImGui.Dummy(new Vector2(0f, 12f));

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
                if (ImGui.Button(
                    "Open Settings",
                    new Vector2(120f, 34f)))
                {
                    currentPage = HomePage.Settings;
                }
            }

            return;
        }

        // ---------------------------------------------------------
        // Heading
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Watch party");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 4f));

        switch (stream.Mode)
        {
            // -----------------------------------------------------
            // Hosting
            // -----------------------------------------------------

            case StreamMode.Hosting:
                {
                    // Temporary visual-only room name.
                    var previewRoomName =
                        $"{CurrentDisplayName ?? "Your"}'s Watch Party";

                    var isPrivate =
                        stream.IsPrivate;

                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.ChildRounding,
                        8f))
                    using (ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        new Vector4(0.045f, 0.06f, 0.10f, 1f)))
                    using (var statusCard = ImRaii.Child(
                        "##partyHosting",
                        new Vector2(-1f, 154f),
                        false,
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse))
                    {
                        if (statusCard)
                        {
                            // HOSTING
                            ImGui.SetCursorPos(
                                new Vector2(14f, 12f));

                            ImGui.TextColored(
                                Good,
                                "HOSTING");

                            // Private party toggle in top-right.
                            ImGui.SetCursorPos(
                                new Vector2(
                                    ImGui.GetWindowWidth() - 140f,
                                    9f));

                            if (ImGui.Checkbox(
                                "Private party",
                                ref isPrivate))
                            {
                                stream.IsPrivate =
                                    isPrivate;
                            }

                            // Host
                            ImGui.SetCursorPos(
                                new Vector2(14f, 39f));

                            ImGui.TextColored(
                                Vector4.One,
                                $"Host: {CurrentDisplayName ?? "You"}");

                            // Status
                            ImGui.SetCursorPos(
                                new Vector2(14f, 63f));

                            ImGui.SetWindowFontScale(0.80f);

                            ImGui.TextColored(
                                MutedText,
                                $"{stream.Roster.Length} watching  •  Playback stays synced to you");

                            ImGui.SetWindowFontScale(1f);

                            // Room name label
                            ImGui.SetCursorPos(
                                new Vector2(14f, 91f));

                            ImGui.SetWindowFontScale(0.78f);

                            ImGui.TextColored(
                                MutedText,
                                "Room name");

                            ImGui.SetWindowFontScale(1f);

                            // Room name input
                            ImGui.SetCursorPos(
                                new Vector2(14f, 111f));

                            ImGui.SetNextItemWidth(
                                ImGui.GetWindowWidth() - 106f);

                            using (ImRaii.PushStyle(
                                ImGuiStyleVar.FrameRounding,
                                7f)
                                .Push(
                                    ImGuiStyleVar.FramePadding,
                                    new Vector2(12f, 7f)))
                            using (ImRaii.PushColor(
                                ImGuiCol.FrameBg,
                                new Vector4(0.055f, 0.07f, 0.115f, 1f))
                                .Push(
                                    ImGuiCol.FrameBgHovered,
                                    new Vector4(0.07f, 0.09f, 0.145f, 1f))
                                .Push(
                                    ImGuiCol.FrameBgActive,
                                    new Vector4(0.07f, 0.09f, 0.145f, 1f)))
                            {
                                ImGui.InputText(
                                    "##previewRoomName",
                                    ref previewRoomName,
                                    80);
                            }

                            ImGui.SameLine(0f, 8f);

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
                                ImGui.Button(
                                    "Save",
                                    new Vector2(64f, 32f));
                            }
                        }
                    }

                    ImGui.Dummy(
                        new Vector2(0f, 10f));

                    // Invite button directly below the card.
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
                        if (ImGui.Button(
                            "Copy party invite",
                            new Vector2(150f, 32f)))
                        {
                            ImGui.SetClipboardText(
                                $"Come watch with me! Right-click my character and choose \"Join Stream\" " +
                                $"(or open AlphaChannel → Player and join \"{CurrentDisplayName}\").");
                        }
                    }

                    ImGui.Dummy(
                        new Vector2(0f, 14f));

                    DrawRoster(
                        $"Watching ({stream.Roster.Length})",
                        allowPromote: true);

                    break;
                }

            // -----------------------------------------------------
            // Viewing
            // -----------------------------------------------------

            case StreamMode.Viewing:
                {
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.ChildRounding,
                        8f))
                    using (ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        new Vector4(0.045f, 0.06f, 0.10f, 1f)))
                    using (var statusCard = ImRaii.Child(
                        "##partyViewing",
                        new Vector2(-1f, 104f),
                        false,
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse))
                    {
                        if (statusCard)
                        {
                            ImGui.SetCursorPos(
                                new Vector2(14f, 12f));

                            ImGui.TextColored(
                                Good,
                                "IN ROOM");

                            ImGui.SetCursorPos(
                                new Vector2(14f, 38f));

                            ImGui.TextColored(
                                Vector4.One,
                                joinedHostDisplayName is { } host
                                    ? $"{host}'s room"
                                    : "A friend's room");

                            ImGui.SetCursorPos(
                                new Vector2(14f, 64f));

                            ImGui.SetWindowFontScale(0.82f);

                            ImGui.TextColored(
                                MutedText,
                                $"{stream.Roster.Length} also here  •  Playback is synced to the host");

                            ImGui.SetWindowFontScale(1f);
                        }
                    }

                    ImGui.Dummy(new Vector2(0f, 14f));

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
                        if (ImGui.Button(
                            "Leave room",
                            new Vector2(120f, 34f)))
                        {
                            LeaveStream();
                            partyChatLines.Clear();
                        }
                    }

                    ImGui.Dummy(new Vector2(0f, 20f));

                    DrawRoster(
                        $"Also here ({stream.Roster.Length})",
                        allowPromote: false);

                    break;
                }

            // -----------------------------------------------------
            // Not currently in a party
            // -----------------------------------------------------

            default:
                {
                    ImGui.SetWindowFontScale(0.88f);

                    ImGui.TextColored(
                        MutedText,
                        "Host automatically while playing, or join a friend's watch party.");

                    ImGui.SetWindowFontScale(1f);

                    ImGui.Dummy(new Vector2(0f, 14f));

                    ImGui.TextColored(
                        MutedText,
                        "Join a party");

                    ImGui.Dummy(new Vector2(0f, 4f));

                    ImGui.SetNextItemWidth(-118f);

                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        10f)
                        .Push(
                            ImGuiStyleVar.FramePadding,
                            new Vector2(14f, 8f)))
                    using (ImRaii.PushColor(
                        ImGuiCol.FrameBg,
                        new Vector4(0.055f, 0.07f, 0.115f, 1f))
                        .Push(
                            ImGuiCol.FrameBgHovered,
                            new Vector4(0.07f, 0.09f, 0.145f, 1f))
                        .Push(
                            ImGuiCol.FrameBgActive,
                            new Vector4(0.07f, 0.09f, 0.145f, 1f)))
                    {
                        if (playerFocusJoin)
                        {
                            ImGui.SetKeyboardFocusHere();
                            playerFocusJoin = false;
                        }

                        ImGui.InputTextWithHint(
                            "##hostName",
                            "Enter their AlphaChannel name",
                            ref joinHostNameInput,
                            32);
                    }
                }

                    ImGui.SameLine(0f, 10f);

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
                            "Join",
                            new Vector2(88f, 38f)))
                        {
                            DoJoin(joinHostNameInput);
                        }
                    }

                    if (joinError is { } error)
                    {
                        ImGui.Dummy(new Vector2(0f, 8f));

                        ImGui.TextColored(
                            Danger,
                            error);
                    }

                    break;
                }
        }
    

    private void DrawPartySocialPanel()
    {
        DrainPartyChat();

        if (CurrentSession is null)
        {
            ImGui.TextColored(
                MutedText,
                "Sign in under Settings to use room chat and reactions.");

            return;
        }

        if (stream.Mode == StreamMode.None)
        {
            ImGui.SetWindowFontScale(1.15f);

            ImGui.TextColored(
                Vector4.One,
                "Chat");

            ImGui.SetWindowFontScale(1f);

            ImGui.Dummy(new Vector2(0f, 6f));

            ImGui.TextColored(
                MutedText,
                "Join or host a watch party to use chat and reactions.");

            return;
        }

        // DrawReactions already provides its own heading.
        DrawPartyChat();

        ImGui.Dummy(new Vector2(0f, 10f));

        DrawReactions();
    }

    private void DrawPartyChat()
    {
        // ---------------------------------------------------------
        // Heading
        // ---------------------------------------------------------

        DrawSectionTitle(
     FontAwesomeIcon.Comments,
     "Party Chat");

        ImGui.Dummy(new Vector2(0f, 2f));

        ImGui.SetWindowFontScale(0.88f);

        ImGui.TextColored(
            MutedText,
            "Messages from everyone watching together.");

        ImGui.SetWindowFontScale(0.78f);

        ImGui.TextColored(
            MutedText,
           "Use /wp <message> to send from the FFXIV chatbox.");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 4f));

        // ---------------------------------------------------------
        // Chat log
        // ---------------------------------------------------------

        var height =
            MathF.Max(
                200f,
                ImGui.GetContentRegionAvail().Y - 148f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            8f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(14f, 12f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f)))
        using (var child = ImRaii.Child(
            "##partyChatLog",
            new Vector2(-1f, height),
            false,
            ImGuiWindowFlags.None))
        {
            if (child)
            {
                if (partyChatLines.Count == 0)
                {
                    ImGui.SetWindowFontScale(0.88f);

                    ImGui.TextColored(
                        MutedText,
                        "No messages yet.");

                    ImGui.SetWindowFontScale(1f);
                }
                else
                {
                    foreach (var (name, text) in partyChatLines)
                    {
                        ImGui.TextColored(
                            Accent,
                            name);

                        ImGui.SameLine(0f, 8f);

                        ImGui.TextWrapped(
                            text);

                        ImGui.Dummy(
                            new Vector2(0f, 4f));
                    }
                }

                if (partyChatStickToBottom)
                {
                    ImGui.SetScrollHereY(1f);
                    partyChatStickToBottom = false;
                }
            }
        }

        ImGui.Dummy(new Vector2(0f, 4f));

        // ---------------------------------------------------------
        // Message input
        // ---------------------------------------------------------

        ImGui.SetNextItemWidth(-76f);

        bool sent;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                new Vector2(12f, 9f)))
        using (ImRaii.PushColor(
            ImGuiCol.FrameBg,
            new Vector4(0.055f, 0.07f, 0.115f, 1f))
            .Push(
                ImGuiCol.FrameBgHovered,
                new Vector4(0.07f, 0.09f, 0.145f, 1f))
            .Push(
                ImGuiCol.FrameBgActive,
                new Vector4(0.07f, 0.09f, 0.145f, 1f)))
        {
            sent = ImGui.InputTextWithHint(
                "##partyChatInput",
                "Message the watch party...",
                ref partyChatInput,
                280,
                ImGuiInputTextFlags.EnterReturnsTrue);
        }

        ImGui.SameLine(0f, 8f);

        // ---------------------------------------------------------
        // Send
        // ---------------------------------------------------------

        var hasMessage =
            partyChatInput.Trim().Length > 0;

        var sendClicked = false;

        using (ImRaii.Disabled(!hasMessage))
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
            sendClicked = ImGui.Button(
                "Send",
                new Vector2(68f, 36f));
        }

        // ---------------------------------------------------------
        // Send message
        // ---------------------------------------------------------

        if ((sendClicked || sent) &&
            hasMessage)
        {
            var text =
                partyChatInput.Trim();

            partyChatInput =
                string.Empty;

            _ = stream.SendChatAsync(
                text);

            partyChatStickToBottom =
                true;
        }
        ImGui.Dummy(
    new Vector2(0f, 4f));

        ImGui.SetWindowFontScale(0.82f);

        var relayChat =
            Plugin.Cfg.RelayPartyChatToGameChat;

        if (ImGui.Checkbox(
            "Relay party chat messages to FFXIV chatbox",
            ref relayChat))
        {
            Plugin.Cfg.RelayPartyChatToGameChat =
                relayChat;

            Plugin.Cfg.Save();
        }

        ImGui.SetWindowFontScale(1f);
    }
}
