using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Compact rendering over the same history and composer used by the full
    // Watch Party page. Media requests deliberately remain summary-only here.
    internal void DrawMiniPartyChatContents()
    {
        DrainPartyChat();

        if (stream.Mode is not (StreamMode.Hosting or StreamMode.Viewing))
        {
            ImGui.TextColored(MutedText, "Join or create a Watch Party to use Mini Chat.");
            return;
        }

        var composerHeight = 82f;
        using (var feed = ImRaii.Child(
                   "##miniPartyChatFeed",
                   new Vector2(-1f, MathF.Max(Ui(100f), ImGui.GetContentRegionAvail().Y - composerHeight)),
                   true,
                   ImGuiWindowFlags.None))
        {
            if (feed)
            {
                if (partyChatItems.Count == 0)
                {
                    ImGui.TextColored(MutedText, "No messages yet.");
                }

                foreach (var item in partyChatItems)
                {
                    ImGui.PushID(item.Id.ToString());
                    DrawMiniPartyChatItem(item);
                    ImGui.PopID();
                }

                if (partyChatStickToBottom)
                {
                    ImGui.SetScrollHereY(1f);
                    partyChatStickToBottom = false;
                }
            }
        }

        ImGui.Spacing();
        DrawMiniPartyChatComposer();
    }

    private void DrawMiniPartyChatItem(PartyChatItem item)
    {
        var name = string.IsNullOrWhiteSpace(item.Name) ? "Someone" : item.Name;

        switch (item.Kind)
        {
            case PartyChatItemKind.Message:
                ImGui.TextColored(AccentHover, name);
                if (item.ReceivedAt is { } received)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(MutedText, received.ToString("HH:mm"));
                }
                ImGui.TextWrapped(item.Text);
                break;

            case PartyChatItemKind.MediaRequest:
                ImGui.TextColored(Accent, $"{name} requested a video");
                if (stream.Mode == StreamMode.Hosting)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(MutedText, "Open full chat to review it.");
                }
                break;

            case PartyChatItemKind.MediaQueued:
                ImGui.TextColored(Good, $"{name}'s requested video was added to the queue.");
                break;

            case PartyChatItemKind.MediaPlaying:
                ImGui.TextColored(Good, $"{name}'s requested video is now playing.");
                break;

            case PartyChatItemKind.Reaction:
                ImGui.TextColored(AccentHover, $"{name} reacted");
                ImGui.SameLine();
                ImGui.TextUnformatted(item.Text);
                break;
        }

        ImGui.Dummy(UiVec(0f, 8f));
    }

    private void DrawMiniPartyChatComposer()
    {
        var sendWidth = 34f;
        ImGui.SetNextItemWidth(MathF.Max(Ui(100f), ImGui.GetContentRegionAvail().X - sendWidth - Ui(8f)));
        var enterPressed = ImGui.InputTextWithHint(
            "##miniPartyChatInput",
            "Message the Watch Party...",
            ref partyChatInput,
            280,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        var hasMessage = !string.IsNullOrWhiteSpace(partyChatInput);
        using (ImRaii.Disabled(!hasMessage))
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var sendClicked = ImGui.Button(
                $"{Dalamud.Interface.FontAwesomeIcon.PaperPlane.ToIconString()}##miniChatSend",
                new Vector2(sendWidth, 0f));

            if ((enterPressed || sendClicked) && hasMessage)
            {
                var text = partyChatInput.Trim();
                partyChatInput = string.Empty;
                _ = stream.SendChatAsync(text);
                partyChatStickToBottom = true;
            }
        }

        DrawCompactReactions(ImGui.GetContentRegionAvail().X);
    }
}
