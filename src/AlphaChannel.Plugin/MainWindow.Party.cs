using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Watch party lives on Player: host/join/roster + ephemeral room chat (stream.chat).
internal sealed partial class MainWindow
{
    private string partyJoinInput = string.Empty;
    private readonly List<(string Name, string Text)> partyChatLines = [];
    private string partyChatInput = string.Empty;
    private bool partyChatStickToBottom = true;

    private void DrainPartyChat()
    {
        while (stream.IncomingChat.TryDequeue(out var line))
        {
            partyChatLines.Add(line);
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

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 4f));

        // ---------------------------------------------------------
        // Chat log
        // ---------------------------------------------------------

        var height = MathF.Min(
            200f,
            MathF.Max(
                120f,
                ImGui.GetContentRegionAvail().Y * 0.24f));

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
                "Message...",
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
    }
}
