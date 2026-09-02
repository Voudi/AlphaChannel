using AlphaChannel.Plugin.Whispers;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Native /tell mirror - see WhisperMirror's own doc comment for why this is separate from Alpha
// Chat's account/E2E system. Reachable as a tab on the Alpha Chat page (MainWindow.Messages.cs);
// works with no AlphaChannel sign-in at all, since it's just local chat-log data.
internal sealed partial class MainWindow
{
    private string? openWhisperKey;
    private string whisperComposerInput = string.Empty;
    private bool whisperComposerFocus;
    private readonly HashSet<string> unreadWhisperKeys = [];
    private bool whisperScrollToBottom;

    internal void ResetWhisperUi()
    {
        openWhisperKey = null;
        whisperComposerInput = string.Empty;
        whisperComposerFocus = false;
        unreadWhisperKeys.Clear();
        whisperScrollToBottom = false;
    }

    private void DrawWhispers()
    {
        if (openWhisperKey is { } key)
        {
            DrawWhisperThread(key);
            return;
        }

        ImGui.TextColored(MutedText,
            "Native /tell messages — not encrypted. History stays on this PC when archiving is on.");
        ImGui.Spacing();

        var keys = whisperMirror.GetCorrespondentKeys();
        if (keys.Length == 0)
        {
            DrawPlainEmpty(Plugin.Cfg.ArchiveWhispersToDisk
                ? "No whispers yet — /tell someone in-game, or wait for an incoming tell."
                : "No whispers this session. Turn on disk archive in Settings to keep history across reloads.");
            return;
        }

        foreach (var correspondentKey in keys)
        {
            DrawWhisperRow(correspondentKey);
        }
    }

    private void DrawWhisperRow(string correspondentKey)
    {
        ImGui.PushID(correspondentKey);
        var height = 52f;
        var width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var display = whisperMirror.GetDisplayName(correspondentKey);
        var lastAt = whisperMirror.GetLastActivity(correspondentKey);
        var unread = unreadWhisperKeys.Contains(correspondentKey);

        drawList.AddRectFilled(origin, origin + new Vector2(width, height), ImGui.GetColorU32(CardBg), 10f);

        if (ImGui.InvisibleButton("##openWhisper", new Vector2(width - 40f, height)))
        {
            openWhisperKey = correspondentKey;
            unreadWhisperKeys.Remove(correspondentKey);
            whisperComposerInput = string.Empty;
            whisperScrollToBottom = true;
            whisperComposerFocus = true;
        }

        if (ImGui.IsItemHovered())
        {
            drawList.AddRectFilled(origin, origin + new Vector2(width - 40f, height),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.05f)), 10f);
        }

        drawList.AddText(origin + new Vector2(14, 10), ImGui.GetColorU32(Vector4.One), display);
        var subtitle = lastAt > DateTime.MinValue
            ? FormatRelativeTime(new DateTimeOffset(lastAt).ToUnixTimeSeconds())
            : correspondentKey;
        drawList.AddText(origin + new Vector2(14, 28), ImGui.GetColorU32(MutedText), subtitle);

        if (unread)
        {
            drawList.AddCircleFilled(origin + new Vector2(width - 52f, height / 2f), 6f, ImGui.GetColorU32(Accent));
        }

        ImGui.SetCursorScreenPos(origin + new Vector2(width - 36f, (height - 22f) / 2f));
        if (ImGui.SmallButton("×"))
        {
            whisperMirror.Remove(correspondentKey);
            unreadWhisperKeys.Remove(correspondentKey);
            if (openWhisperKey == correspondentKey)
            {
                openWhisperKey = null;
            }
        }

        ImGui.SetCursorScreenPos(origin + new Vector2(0, height + 6f));
        ImGui.PopID();
    }

    private void DrawWhisperThread(string correspondentKey)
    {
        if (ImGui.Button("< Back"))
        {
            openWhisperKey = null;
            return;
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(whisperMirror.GetDisplayName(correspondentKey));
        ImGui.SameLine();
        if (ImGui.SmallButton("Delete"))
        {
            whisperMirror.Remove(correspondentKey);
            unreadWhisperKeys.Remove(correspondentKey);
            openWhisperKey = null;
            return;
        }

        ImGui.Spacing();

        using (var child = ImRaii.Child("##whisperThread", new Vector2(0, -Ui(68f)), false,
                   ImGuiWindowFlags.NoScrollbar))
        {
            if (child)
            {
                foreach (var message in whisperMirror.GetMessages(correspondentKey))
                {
                    DrawWhisperBubble(message);
                }

                if (whisperScrollToBottom)
                {
                    ImGui.SetScrollHereY(1f);
                    whisperScrollToBottom = false;
                }
            }
        }

        var canReply = whisperMirror.CanReply(correspondentKey);
        if (!canReply)
        {
            ImGui.TextColored(MutedText,
                "Can't reply yet — need their world (shows up on a cross-world tell).");
        }

        using (ImRaii.Disabled(!canReply))
        {
            if (whisperComposerFocus)
            {
                ImGui.SetKeyboardFocusHere();
                whisperComposerFocus = false;
            }

            ImGui.SetNextItemWidth(-80f);
            var sent = ImGui.InputTextWithHint("##whisperComposer", "Message…", ref whisperComposerInput,
                WhisperMirror.MaxMessageLength, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if ((ImGui.Button("Send", new Vector2(72, 0)) || sent) && whisperComposerInput.Trim().Length > 0)
            {
                whisperMirror.TrySendReply(correspondentKey, whisperComposerInput.Trim());
                whisperComposerInput = string.Empty;
                whisperScrollToBottom = true;
                whisperComposerFocus = true;
            }
        }
    }

    private void DrawWhisperBubble(WhisperMessage message)
    {
        var mine = message.Mine;
        var avail = ImGui.GetContentRegionAvail().X;
        var padding = 12f;
        var bubbleWidth = MathF.Min(avail * 0.78f, MathF.Max(avail - 24f, 80f));
        var wrapWidth = MathF.Max(bubbleWidth - padding * 2f, 40f);
        var meta = FormatRelativeTime(new DateTimeOffset(message.AtUtc).ToUnixTimeSeconds());
        var textSize = ImGui.CalcTextSize(message.Text, false, wrapWidth);
        var metaH = ImGui.CalcTextSize(meta).Y + 4f;
        var bubbleHeight = padding * 2f + textSize.Y + metaH;
        var offsetX = mine ? MathF.Max(avail - bubbleWidth, 0f) : 0f;

        ImGui.PushID($"{message.AtUtc.Ticks}:{message.Text.GetHashCode()}");
        var start = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(start.X + offsetX, start.Y));
        var screen = ImGui.GetCursorScreenPos();
        var fill = mine ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.22f) : CardBgHover;
        ImGui.GetWindowDrawList().AddRectFilled(screen, screen + new Vector2(bubbleWidth, bubbleHeight),
            ImGui.GetColorU32(fill), 12f);

        ImGui.SetCursorPos(new Vector2(start.X + offsetX + padding, start.Y + padding));
        ImGui.PushTextWrapPos(start.X + offsetX + padding + wrapWidth);
        ImGui.TextWrapped(message.Text);
        ImGui.PopTextWrapPos();
        ImGui.SetCursorPos(new Vector2(start.X + offsetX + padding, start.Y + padding + textSize.Y + 4f));
        ImGui.TextColored(MutedText, meta);
        ImGui.SetCursorPos(new Vector2(start.X, start.Y + bubbleHeight + 6f));
        ImGui.PopID();
    }

    // Wired to WhisperMirror.OnWhisperMessage from MainWindow's constructor - flags a badge unless
    // it's a message the game echoed back for one we just sent, or the thread's already open.
    private void ApplyIncomingWhisper(WhisperMessage message)
    {
        if (openWhisperKey == message.CorrespondentKey)
        {
            whisperScrollToBottom = true;
            return;
        }

        if (!message.Mine)
        {
            unreadWhisperKeys.Add(message.CorrespondentKey);
        }
    }
}
