using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Crypto;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Alpha Chat (E2E DMs + group chats). Decrypts on render via DmCipher/KeyVault - the plaintext
// cache is keyed by message id so re-renders every ImGui frame don't re-run the ECDH+AES-GCM math
// each time, same "decrypt once, cache the result" idiom Aetherphone's own MessageCipher uses.
//
// Group E2E is sender-side pairwise fan-out (see DmMessage's server-side doc comment) - sending to
// a conversation means encrypting the same plaintext once per other member with that member's own
// public key, and a received message's RecipientAccountId/SenderAccountId together say exactly
// which pairwise key decrypts it (see ResolveOtherPartyIdAsync below). No new crypto primitives
// versus 1:1 - just more calls to the same DmCipher.Encrypt/Decrypt.
internal sealed partial class MainWindow
{
    private bool conversationsDirty = true;
    private bool conversationsLoading;
    private ConversationSummaryDto[] conversations = [];
    private string? openConversationId;
    private ConversationMemberDto[] openConversationMembers = [];
    private bool openConversationIsGroup;
    private string openConversationTitle = string.Empty;
    private MessageDto[] openMessages = [];
    private string? messagesNextCursor;
    private bool messagesLoadingOlder;
    private bool threadScrollToBottom;
    private readonly Dictionary<string, string> decryptedCache = new();
    private readonly Dictionary<string, string> memberDisplayNameCache = new();
    private string messageComposerInput = string.Empty;
    private bool messageComposerFocus;
    private bool messagesLoading;
    private string? messagesError;
    private FriendDto[] messagesFriendPicker = [];
    private bool messagesFriendPickerLoaded;

    private bool newGroupPanelOpen;
    private readonly HashSet<string> newGroupSelectedMembers = [];
    private string newGroupNameInput = string.Empty;

    private const string DecryptFailedMarker = "\u0001decrypt_failed";

    // Two tabs: Alpha Chat (AlphaChannel accounts, E2E) and Whispers (native /tell mirror, no
    // account needed at all - see WhisperMirror's own doc comment on why that's a separate system).
    private void DrawMessages()
    {
        if (!ImGui.BeginTabBar("##messagesTabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("Alpha Chat"))
        {
            ImGui.Spacing();
            DrawAlphaChatTab();
            ImGui.EndTabItem();
        }

        var whisperLabel = unreadWhisperKeys.Count > 0 ? $"Whispers ({unreadWhisperKeys.Count})###whispersTab" : "Whispers###whispersTab";
        if (ImGui.BeginTabItem(whisperLabel))
        {
            ImGui.Spacing();
            DrawWhispers();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawAlphaChatTab()
    {
        if (CurrentSession is not { } session)
        {
            DrawPlainEmpty("Private E2E chats need a signed-in account.", "Open Settings",
                () => currentPage = HomePage.Settings);
            return;
        }

        if (openConversationId is { } openId)
        {
            DrawThread(session, openId);
            return;
        }

        if (conversationsDirty && !conversationsLoading)
        {
            RefreshConversations(session.Token);
        }

        if (!messagesFriendPickerLoaded)
        {
            messagesFriendPickerLoaded = true;
            var token = session.Token;
            _ = Task.Run(async () => messagesFriendPicker = await friendsClient.GetFriendsAsync(token) ?? []);
        }

        var startable = messagesFriendPicker
            .Where(f => !conversations.Any(c => !c.IsGroup && c.Members.Any(m => m.AccountId == f.AccountId)))
            .ToArray();
        if (startable.Length > 0)
        {
            SectionHeader("New message");
            foreach (var friend in startable)
            {
                ImGui.PushID("start_" + friend.AccountId);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(friend.DisplayName);
                ImGui.SameLine();
                if (ImGui.SmallButton("Message"))
                {
                    StartOrOpenConversation(session, friend.AccountId, friend.DisplayName);
                }

                ImGui.PopID();
            }

            ImGui.Spacing();
        }

        if (ImGui.SmallButton(newGroupPanelOpen ? "Cancel new group" : "New group"))
        {
            newGroupPanelOpen = !newGroupPanelOpen;
            newGroupSelectedMembers.Clear();
            newGroupNameInput = string.Empty;
        }

        if (newGroupPanelOpen)
        {
            DrawNewGroupPanel(session);
        }

        ImGui.Spacing();
        ImGui.Spacing();
        SectionHeader("Conversations");

        if (conversationsLoading && conversations.Length == 0)
        {
            ImGui.TextDisabled("Loading…");
            return;
        }

        if (conversations.Length == 0)
        {
            DrawPlainEmpty("No conversations yet — message a friend above.");
            return;
        }

        foreach (var conversation in conversations.OrderByDescending(c => c.LastMessageAtUnix ?? 0))
        {
            DrawConversationRow(session, conversation);
        }
    }

    private void DrawConversationRow(CharacterSession session, ConversationSummaryDto conversation)
    {
        ImGui.PushID(conversation.ConversationId);
        var title = ConversationTitle(conversation);
        var height = 52f;
        var width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(origin, origin + new Vector2(width, height), ImGui.GetColorU32(CardBg), 10f);

        if (ImGui.InvisibleButton("##openConv", new Vector2(width, height)))
        {
            OpenConversation(session, conversation.ConversationId, conversation.Members, conversation.IsGroup, title);
        }

        if (ImGui.IsItemHovered())
        {
            drawList.AddRectFilled(origin, origin + new Vector2(width, height),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.05f)), 10f);
        }

        var titlePos = origin + new Vector2(14, 10);
        drawList.AddText(titlePos, ImGui.GetColorU32(Vector4.One), title);
        if (conversation.IsGroup)
        {
            var titleWidth = ImGui.CalcTextSize(title).X;
            drawList.AddText(titlePos + new Vector2(titleWidth + 8, 0), ImGui.GetColorU32(MutedText),
                $"· {conversation.Members.Length + 1}");
        }

        var subtitle = conversation.LastMessageAtUnix is { } at
            ? FormatRelativeTime(at)
            : "No messages yet";
        drawList.AddText(origin + new Vector2(14, 28), ImGui.GetColorU32(MutedText), subtitle);

        if (conversation.UnreadCount > 0)
        {
            var badge = conversation.UnreadCount > 9 ? "9+" : conversation.UnreadCount.ToString();
            var badgeSize = ImGui.CalcTextSize(badge);
            var badgeCenter = origin + new Vector2(width - 28f, height / 2f);
            drawList.AddCircleFilled(badgeCenter, 10f, ImGui.GetColorU32(Accent));
            drawList.AddText(badgeCenter - badgeSize / 2f, ImGui.GetColorU32(Vector4.One), badge);
        }

        ImGui.Dummy(new Vector2(0, 6));
        ImGui.PopID();
    }

    private void DrawNewGroupPanel(CharacterSession session)
    {
        ImGui.Spacing();
        SectionHeader("New group");
        ImGui.SetNextItemWidth(240f);
        ImGui.InputTextWithHint("##newGroupName", "Group name", ref newGroupNameInput, 48);

        ImGui.TextColored(MutedText, "Pick friends to add:");
        foreach (var friend in messagesFriendPicker)
        {
            var selected = newGroupSelectedMembers.Contains(friend.AccountId);
            if (ImGui.Checkbox(friend.DisplayName + "##group_" + friend.AccountId, ref selected))
            {
                if (selected)
                {
                    newGroupSelectedMembers.Add(friend.AccountId);
                }
                else
                {
                    newGroupSelectedMembers.Remove(friend.AccountId);
                }
            }
        }

        using (ImRaii.Disabled(newGroupSelectedMembers.Count < 2 || newGroupNameInput.Trim().Length == 0))
        {
            if (ImGui.Button("Create group"))
            {
                var token = session.Token;
                var members = newGroupSelectedMembers.ToArray();
                var name = newGroupNameInput.Trim();
                _ = Task.Run(async () =>
                {
                    var conversationId = await dmClient.CreateConversationAsync(token, members, name);
                    if (conversationId is not null)
                    {
                        var memberDtos = messagesFriendPicker.Where(f => members.Contains(f.AccountId))
                            .Select(f => new ConversationMemberDto(f.AccountId, f.Handle, f.DisplayName)).ToArray();
                        OpenConversation(session, conversationId, memberDtos, isGroup: true, name);
                    }
                });

                newGroupPanelOpen = false;
            }
        }

        if (newGroupSelectedMembers.Count < 2)
        {
            ImGui.TextColored(MutedText, "Pick at least 2 friends - a single friend is just a 1:1 conversation.");
        }
    }

    private static string ConversationTitle(ConversationSummaryDto conversation) =>
        conversation.IsGroup
            ? conversation.Name ?? "Group chat"
            : conversation.Members.FirstOrDefault()?.DisplayName ?? "Unknown";

    private static string FormatRelativeTime(long unixSeconds)
    {
        var then = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        var delta = DateTime.UtcNow - then;
        if (delta.TotalMinutes < 1)
        {
            return "Just now";
        }

        if (delta.TotalHours < 1)
        {
            return $"{(int)delta.TotalMinutes}m ago";
        }

        if (delta.TotalDays < 1)
        {
            return $"{(int)delta.TotalHours}h ago";
        }

        if (delta.TotalDays < 7)
        {
            return $"{(int)delta.TotalDays}d ago";
        }

        return then.ToLocalTime().ToString("MMM d");
    }

    // Called from MainWindow.Social.cs's/Tweeter's "Message" button too, not just from within this
    // page - starts (or resumes) a 1:1 with a friend and switches straight to the thread view.
    private void StartOrOpenConversation(CharacterSession session, string otherAccountId, string otherDisplayName)
    {
        currentPage = HomePage.Messages;
        _ = Task.Run(async () =>
        {
            var conversationId = await dmClient.CreateConversationAsync(session.Token, [otherAccountId]);
            if (conversationId is not null)
            {
                OpenConversation(session, conversationId, [new ConversationMemberDto(otherAccountId, "", otherDisplayName)], isGroup: false, otherDisplayName);
            }
        });
    }

    private void OpenConversation(CharacterSession session, string conversationId, ConversationMemberDto[] members, bool isGroup, string title)
    {
        openConversationId = conversationId;
        openConversationMembers = members;
        openConversationIsGroup = isGroup;
        openConversationTitle = title;
        openMessages = [];
        messagesNextCursor = null;
        messageComposerInput = string.Empty;
        messagesError = null;
        threadScrollToBottom = true;
        messageComposerFocus = true;

        memberDisplayNameCache.Clear();
        memberDisplayNameCache[session.AccountId] = "You";
        foreach (var member in members)
        {
            memberDisplayNameCache[member.AccountId] = member.DisplayName;
        }

        RefreshMessages(session, reset: true);
    }

    private void DrawThread(CharacterSession session, string conversationId)
    {
        if (ImGui.Button("< Back"))
        {
            openConversationId = null;
            conversationsDirty = true;
            return;
        }

        ImGui.SameLine();
        if (!openConversationIsGroup && openConversationMembers.FirstOrDefault() is { } otherMember)
        {
            if (ImGui.SmallButton(openConversationTitle))
            {
                OpenProfilePopup(session, otherMember.AccountId, openConversationTitle);
            }
        }
        else
        {
            ImGui.TextUnformatted(openConversationTitle);
        }

        ImGui.Spacing();

        using (var child = ImRaii.Child("##thread", new Vector2(0, -Ui(68f)), false, ImGuiWindowFlags.NoScrollbar))
        {
            if (child)
            {
                if (messagesNextCursor is { Length: > 0 })
                {
                    using (ImRaii.Disabled(messagesLoadingOlder || messagesLoading))
                    {
                        if (ImGui.Button("Load older", new Vector2(-1, 28)))
                        {
                            RefreshMessages(session, reset: false);
                        }
                    }

                    ImGui.Spacing();
                }

                if (messagesLoading && openMessages.Length == 0)
                {
                    ImGui.TextDisabled("Loading…");
                }

                foreach (var message in openMessages)
                {
                    DrawChatBubble(session, message);
                }

                if (threadScrollToBottom)
                {
                    ImGui.SetScrollHereY(1f);
                    threadScrollToBottom = false;
                }
            }
        }

        if (messagesError is { Length: > 0 } error)
        {
            ImGui.TextColored(Danger, error);
        }

        if (messageComposerFocus)
        {
            ImGui.SetKeyboardFocusHere();
            messageComposerFocus = false;
        }

        ImGui.SetNextItemWidth(-80f);
        var sent = ImGui.InputTextWithHint("##composer", "Message…", ref messageComposerInput, 2000,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Send", new Vector2(72, 0)) || sent) && messageComposerInput.Trim().Length > 0)
        {
            SendMessage(session, conversationId, messageComposerInput.Trim());
            messageComposerInput = string.Empty;
            // Keep typing until the user clicks away — same Linkpearl composer behavior.
            messageComposerFocus = true;
        }
    }

    private void DrawChatBubble(CharacterSession session, MessageDto message)
    {
        var mine = message.SenderAccountId == session.AccountId;
        var raw = decryptedCache.GetValueOrDefault(message.Id);
        string text;
        if (raw is null)
        {
            text = messagesLoading ? "…" : "Couldn't decrypt — key missing";
        }
        else if (raw == DecryptFailedMarker)
        {
            text = "Couldn't decrypt — key missing";
        }
        else
        {
            text = raw;
        }

        var senderName = memberDisplayNameCache.GetValueOrDefault(message.SenderAccountId, "Someone");
        var avail = ImGui.GetContentRegionAvail().X;
        var padding = 12f;
        var bubbleWidth = MathF.Min(avail * 0.78f, MathF.Max(avail - 24f, 80f));
        var wrapWidth = MathF.Max(bubbleWidth - padding * 2f, 40f);
        var showSender = !mine && openConversationIsGroup;
        var meta = FormatRelativeTime(message.SentAtUnix);
        if (mine && message.ReadAtUnix is not null)
        {
            meta += " · Read";
        }

        var senderH = showSender ? ImGui.CalcTextSize(senderName).Y + 4f : 0f;
        var textSize = ImGui.CalcTextSize(text, false, wrapWidth);
        var metaH = ImGui.CalcTextSize(meta).Y + 4f;
        var reportH = !mine ? ImGui.GetFrameHeight() + 6f : 0f;
        var bubbleHeight = padding * 2f + senderH + textSize.Y + metaH + reportH;
        var offsetX = mine ? MathF.Max(avail - bubbleWidth, 0f) : 0f;

        ImGui.PushID(message.Id);
        var start = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(start.X + offsetX, start.Y));
        var screen = ImGui.GetCursorScreenPos();
        var fill = mine ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.22f) : CardBgHover;
        ImGui.GetWindowDrawList().AddRectFilled(screen, screen + new Vector2(bubbleWidth, bubbleHeight),
            ImGui.GetColorU32(fill), 12f);

        var y = start.Y + padding;
        if (showSender)
        {
            ImGui.SetCursorPos(new Vector2(start.X + offsetX + padding, y));
            ImGui.TextColored(Accent, senderName);
            y += senderH;
        }

        ImGui.SetCursorPos(new Vector2(start.X + offsetX + padding, y));
        ImGui.PushTextWrapPos(start.X + offsetX + padding + wrapWidth);
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
        y += textSize.Y + 4f;

        ImGui.SetCursorPos(new Vector2(start.X + offsetX + padding, y));
        ImGui.TextColored(MutedText, meta);
        y += metaH;

        if (!mine)
        {
            ImGui.SetCursorPos(new Vector2(start.X + offsetX + padding, y));
            if (ImGui.SmallButton("Report"))
            {
                ReportMessage(session, message, text);
            }
        }

        ImGui.SetCursorPos(new Vector2(start.X, start.Y + bubbleHeight + 6f));
        ImGui.PopID();
    }

    // Fired from StreamClient's receive loop when a dm.message push arrives (see
    // AlphaChannel.Contracts.SocialSignalType) - the push carries this recipient's own sealed copy
    // (Ciphertext/Nonce/Tag), so a message to the currently-open thread can be decrypted and
    // appended immediately with no REST round-trip. Anything else just marks the conversation list
    // stale so its unread count catches up next time it's drawn.
    private void ApplyIncomingDm(SocialControl update)
    {
        conversationsDirty = true;

        if (update.ConversationId != openConversationId ||
            update.MessageId is not { Length: > 0 } messageId ||
            update.AccountId is not { Length: > 0 } senderId ||
            update.Ciphertext is not { Length: > 0 } ciphertextBase64 ||
            update.Nonce is not { Length: > 0 } nonceBase64 ||
            update.Tag is not { Length: > 0 } tagBase64 ||
            CurrentSession is not { } session)
        {
            return;
        }

        // The push is always this recipient's own copy, so RecipientAccountId is the viewer.
        var message = new MessageDto(messageId, Guid.NewGuid().ToString(), senderId, session.AccountId, ciphertextBase64, nonceBase64, tagBase64, update.TimestampUnix ?? 0, null);
        openMessages = [.. openMessages, message];
        threadScrollToBottom = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var myIdentity = await keyVault.EnsureIdentityAsync(session.AccountId, session.Token);
                await DecryptAndCacheAsync(session, message, myIdentity);
                await dmClient.MarkReadAsync(session.Token, openConversationId!);
            }
            catch (Exception exception)
            {
                AepLog.Warning($"[Messages] live decrypt failed: {exception.Message}");
            }
        });
    }

    private void RefreshConversations(string bearerToken)
    {
        conversationsDirty = false;
        conversationsLoading = true;
        _ = Task.Run(async () =>
        {
            try
            {
                conversations = await dmClient.GetConversationsAsync(bearerToken) ?? [];
            }
            catch (Exception exception)
            {
                AepLog.Warning($"[Messages] conversations fetch failed: {exception.Message}");
                conversations = [];
            }
            finally
            {
                conversationsLoading = false;
            }
        });
    }

    private void RefreshMessages(CharacterSession session, bool reset)
    {
        var conversationId = openConversationId;
        if (conversationId is null)
        {
            return;
        }

        if (reset)
        {
            messagesLoading = true;
        }
        else
        {
            if (messagesNextCursor is null || messagesLoadingOlder)
            {
                return;
            }

            messagesLoadingOlder = true;
        }

        var before = reset ? null : (long?)long.Parse(messagesNextCursor!);
        _ = Task.Run(async () =>
        {
            try
            {
                var page = await dmClient.GetMessagesAsync(session.Token, conversationId, before);
                if (page is null)
                {
                    messagesError = "Couldn't load messages.";
                    return;
                }

                var myIdentity = await keyVault.EnsureIdentityAsync(session.AccountId, session.Token);
                var ordered = page.Items.OrderBy(m => m.SentAtUnix).ToArray();

                foreach (var message in ordered)
                {
                    await DecryptAndCacheAsync(session, message, myIdentity);
                }

                if (reset)
                {
                    openMessages = ordered;
                    threadScrollToBottom = true;
                    await dmClient.MarkReadAsync(session.Token, conversationId);
                }
                else
                {
                    // Prepend older page; keep scroll position roughly stable by not forcing bottom.
                    openMessages = [.. ordered, .. openMessages];
                }

                messagesNextCursor = page.NextCursor;
            }
            catch (Exception exception)
            {
                AepLog.Warning($"[Messages] load failed: {exception.Message}");
                messagesError = "Couldn't load messages.";
            }
            finally
            {
                messagesLoading = false;
                messagesLoadingOlder = false;
            }
        });
    }

    private void SendMessage(CharacterSession session, string conversationId, string plaintext)
    {
        var members = openConversationMembers;
        if (members.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var myIdentity = await keyVault.EnsureIdentityAsync(session.AccountId, session.Token);
                var envelopes = new List<MessageEnvelope>();
                foreach (var member in members)
                {
                    var otherKey = await keyVault.GetOtherPartyKeyAsync(member.AccountId, session.Token);
                    if (otherKey is null)
                    {
                        messagesError = $"{member.DisplayName} hasn't set up encryption yet - they need to sign in at least once.";
                        return;
                    }

                    var sealedMessage = DmCipher.Encrypt(myIdentity, otherKey, plaintext);
                    envelopes.Add(new MessageEnvelope(
                        member.AccountId,
                        Convert.ToBase64String(sealedMessage.Ciphertext),
                        Convert.ToBase64String(sealedMessage.Nonce),
                        Convert.ToBase64String(sealedMessage.Tag),
                        Convert.ToBase64String(sealedMessage.CommitmentTag)));
                }

                var sent = await dmClient.SendMessageAsync(session.Token, conversationId, new SendMessageRequest([.. envelopes]));
                if (sent is not null)
                {
                    decryptedCache[sent.Id] = plaintext;
                    openMessages = [.. openMessages, sent];
                    threadScrollToBottom = true;
                    conversationsDirty = true;
                }
            }
            catch (Exception exception)
            {
                AepLog.Warning($"[Messages] send failed: {exception.Message}");
                messagesError = "Couldn't send that message.";
            }
        });
    }

    // Which account's public key pairs with this specific message row - see DmMessage's doc
    // comment on why a message the viewer sent is decrypted using the RECIPIENT's key (their own
    // pairwise shared secret with that member), while a received message uses the SENDER's key.
    private async Task DecryptAndCacheAsync(CharacterSession session, MessageDto message, DmIdentity myIdentity)
    {
        if (decryptedCache.ContainsKey(message.Id))
        {
            return;
        }

        var otherAccountId = message.SenderAccountId == session.AccountId ? message.RecipientAccountId : message.SenderAccountId;
        var otherKey = await keyVault.GetOtherPartyKeyAsync(otherAccountId, session.Token);
        if (otherKey is null)
        {
            decryptedCache[message.Id] = DecryptFailedMarker;
            return;
        }

        var opened = DmCipher.Decrypt(myIdentity, otherKey,
            Convert.FromBase64String(message.Ciphertext), Convert.FromBase64String(message.Nonce), Convert.FromBase64String(message.Tag));
        decryptedCache[message.Id] = opened is not null ? opened.Plaintext : DecryptFailedMarker;
    }

    private void ReportMessage(CharacterSession session, MessageDto message, string revealedPlaintext)
    {
        if (revealedPlaintext is "Couldn't decrypt — key missing" or "…" or "")
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var myIdentity = await keyVault.EnsureIdentityAsync(session.AccountId, session.Token);
            var otherAccountId = message.SenderAccountId == session.AccountId ? message.RecipientAccountId : message.SenderAccountId;
            var otherKey = await keyVault.GetOtherPartyKeyAsync(otherAccountId, session.Token);
            if (otherKey is null)
            {
                return;
            }

            var opened = DmCipher.Decrypt(myIdentity, otherKey,
                Convert.FromBase64String(message.Ciphertext), Convert.FromBase64String(message.Nonce), Convert.FromBase64String(message.Tag));
            if (opened is null)
            {
                return;
            }

            await reportClient.SubmitAsync(session.Token, "harassment", null, message.SenderAccountId, message.Id,
                revealedPlaintext, Convert.ToBase64String(opened.FrankingKey));
        });
    }
}
