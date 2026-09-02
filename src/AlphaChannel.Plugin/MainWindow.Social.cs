using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Friends Channel: friend list (with live online status), incoming/outgoing requests, add-by-
// handle. REST-backed (FriendsClient) with live refresh triggered by StreamClient's
// OnFriendRequestReceived/OnFriendAccepted/OnFriendRemoved pushes (wired in MainWindow's
// constructor) rather than polling - see AlphaChannel.Contracts.SocialSignalType's own note on why
// those pushes exist.
internal sealed partial class MainWindow
{
    private bool friendsDirty = true;
    private bool friendsLoading;
    private FriendDto[] friends = [];
    // Live global count from presence.onlineCount — not friends, every AlphaChannel /rt client.
    private int usersOnlineCount;
    private FriendRequestsPage friendRequests = new([], []);
    private string? friendsError;
    private AccountSummaryDto[] blockedAccounts = [];
    private string inviteCodeInput = string.Empty;
    private bool inviteCodeRedeeming;
    private string? inviteCodeError;

    // Live search-as-you-type, replacing a type-the-full-name-then-Send box. friendSearchGeneration
    // discards a stale response that lands after a newer keystroke already fired a fresher search -
    // same race the old exact-search box never had to worry about since it only ever fired once per
    // button click.
    private string friendSearchInput = string.Empty;
    private string friendSearchQuery = string.Empty;
    private long friendSearchGeneration;
    private bool friendSearchLoading;
    private FriendSearchResultDto[] friendSearchResults = [];
    private readonly HashSet<string> friendSearchSendingIds = [];

    // Called from Plugin.cs's right-click "Add Friend" context-menu handler - surfaces the result
    // the same way the in-page "Add a friend" flow does (friendsError + a refreshed request list),
    // and jumps straight to Friends so the outcome is actually visible instead of silent.
    internal void HandleAddFriendByCharacterResult(bool ok, string characterName)
    {
        friendsDirty = true;
        friendsError = ok ? null : $"Couldn't add {characterName} - they may not have AlphaChannel yet.";
        currentPage = HomePage.Friends;
        IsOpen = true;
    }

    private void DrawFriends()
    {
        if (CurrentSession is not { } session)
        {
            DrawPlainEmpty(
                "Sign in to see your friends.",
                "Open Settings",
                () => currentPage = HomePage.Settings);

            return;
        }

        if (friendsDirty && !friendsLoading)
        {
            RefreshFriends(session.Token);
        }

        if (friendsClient.LastAccessDeniedReason is { } deniedReason)
        {
            ImGui.TextColored(
                Danger,
                deniedReason switch
                {
                    "lalafell_pending" =>
                        "Your account is pending review before Lalafell accounts can use Friends. Check back soon.",

                    "lalafell_denied" =>
                        "Social features aren't available for this account.",

                    _ =>
                        "Friends isn't available for this account right now.",
                });

            return;
        }

        // ---------------------------------------------------------
        // Display-name warning
        // ---------------------------------------------------------

        if (session.DisplayName == session.Handle)
        {
            ImGui.TextColored(
                Danger,
                "Pick a username in Settings so friends can find you.");

            ImGui.SameLine();

            if (ImGui.SmallButton("Open Settings"))
            {
                currentPage = HomePage.Settings;
            }

            ImGui.Dummy(new Vector2(0f, 10f));
        }

        // ---------------------------------------------------------
        // Add a friend
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Add a friend");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 10f));

        const float addCardHeight = 178f;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f)))
        using (var addCard = ImRaii.Child(
            "##addFriendCard",
            new Vector2(-1f, addCardHeight),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (addCard)
            {
                var origin =
                    ImGui.GetCursorScreenPos();

                var cardWidth =
                    ImGui.GetWindowWidth();

                const float outerPadding = 18f;
                const float centreGap = 34f;

                var usableWidth =
                    cardWidth -
                    (outerPadding * 2f);

                var columnWidth =
                    (usableWidth - centreGap) * 0.5f;

                var leftX =
                    origin.X + outerPadding;

                var rightX =
                    leftX +
                    columnWidth +
                    centreGap;

                var dividerX =
                    leftX +
                    columnWidth +
                    (centreGap * 0.5f);

                // -------------------------------------------------
                // Vertical divider
                // -------------------------------------------------

                ImGui.GetWindowDrawList()
                    .AddRectFilled(
                        new Vector2(
                            dividerX,
                            origin.Y + 20f),
                        new Vector2(
                            dividerX + 1f,
                            origin.Y + addCardHeight - 20f),
                        ImGui.GetColorU32(
                            BorderSubtle));

                // =================================================
                // LEFT — invite code
                // =================================================

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        leftX,
                        origin.Y + 20f));

                ImGui.TextColored(
                    MutedText,
                    "Have an invite code?");

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        leftX,
                        origin.Y + 51f));

                var leftButtonWidth = 94f;
                var leftInputWidth =
                    columnWidth -
                    leftButtonWidth -
                    10f;

                ImGui.SetNextItemWidth(
                    leftInputWidth);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    8f)
                    .Push(
                        ImGuiStyleVar.FramePadding,
                        new Vector2(12f, 10f)))
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
                    ImGui.InputTextWithHint(
                        "##inviteCode",
                        "Paste invite code",
                        ref inviteCodeInput,
                        16);
                }

                ImGui.SameLine(0f, 10f);

                using (ImRaii.Disabled(
                    inviteCodeRedeeming ||
                    inviteCodeInput.Trim().Length == 0))
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
                        "Redeem",
                        new Vector2(
                            leftButtonWidth,
                            38f)))
                    {
                        inviteCodeRedeeming = true;
                        inviteCodeError = null;

                        var code =
                            inviteCodeInput.Trim();

                        var token =
                            session.Token;

                        _ = Task.Run(async () =>
                        {
                            var ok =
                                await friendsClient
                                    .RedeemInviteCodeAsync(
                                        token,
                                        code);

                            inviteCodeRedeeming = false;

                            inviteCodeError =
                                ok
                                    ? null
                                    : "Couldn't redeem that code - it may be wrong, expired, or already used.";

                            if (ok)
                            {
                                inviteCodeInput =
                                    string.Empty;

                                friendsDirty =
                                    true;
                            }
                        });
                    }
                }

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        leftX,
                        origin.Y + 105f));

                ImGui.SetWindowFontScale(0.82f);

                ImGui.TextColored(
                    MutedText,
                    "Ask a friend for their invite code.");

                ImGui.SetWindowFontScale(1f);

                // =================================================
                // RIGHT — search by Alpha Channel username
                // =================================================

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        rightX,
                        origin.Y + 20f));

                ImGui.TextColored(
                    MutedText,
                    "Or search by their Alpha Channel username");

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        rightX,
                        origin.Y + 51f));

                const float searchButtonWidth = 108f;

                var searchInputWidth =
                    columnWidth -
                    searchButtonWidth -
                    10f;

                ImGui.SetNextItemWidth(
                    searchInputWidth);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    8f)
                    .Push(
                        ImGuiStyleVar.FramePadding,
                        new Vector2(12f, 10f)))
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
                    if (ImGui.InputTextWithHint(
                        "##friendSearch",
                        "Type a name...",
                        ref friendSearchInput,
                        DisplayNameRules.MaxLength))
                    {
                        RequestFriendSearch(
                            session,
                            friendSearchInput);
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
                        "Search",
                        new Vector2(
                            searchButtonWidth,
                            38f)))
                    {
                        RequestFriendSearch(
                            session,
                            friendSearchInput);
                    }
                }

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        rightX,
                        origin.Y + 105f));

                ImGui.SetWindowFontScale(0.82f);

                ImGui.TextColored(
                    MutedText,
                    "Enter their full Alpha Channel username.");

                ImGui.SetWindowFontScale(1f);
            }
        }

        // ---------------------------------------------------------
        // Add-friend errors / live search results
        // ---------------------------------------------------------

        if (inviteCodeError is { Length: > 0 } codeError)
        {
            ImGui.Dummy(
                new Vector2(0f, 6f));

            ImGui.TextColored(
                Danger,
                codeError);
        }

        if (friendSearchLoading ||
            friendSearchResults.Length > 0 ||
            friendSearchQuery.Length >= DisplayNameRules.MinLength)
        {
            ImGui.Dummy(
                new Vector2(0f, 8f));

            DrawFriendSearchResults(
                session);
        }

        if (friendsError is { Length: > 0 } error)
        {
            ImGui.Dummy(
                new Vector2(0f, 6f));

            ImGui.TextColored(
                Danger,
                error);
        }

        // ---------------------------------------------------------
        // Horizontal divider
        // ---------------------------------------------------------

        ImGui.Dummy(
            new Vector2(0f, 16f));

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
        // Incoming requests
        // ---------------------------------------------------------

        if (friendRequests.Incoming.Length > 0)
        {
            ImGui.TextColored(
                Accent,
                $"Friend requests ({friendRequests.Incoming.Length})");

            ImGui.Dummy(
                new Vector2(0f, 8f));

            foreach (var request in friendRequests.Incoming)
            {
                ImGui.PushID(
                    request.Id);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.ChildRounding,
                    8f))
                using (ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    new Vector4(0.045f, 0.06f, 0.10f, 1f)))
                using (var requestRow = ImRaii.Child(
                    "##friendRequest",
                    new Vector2(-1f, Ui(50f)),
                    false,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
                {
                    if (requestRow)
                    {
                        var rowOrigin =
                            ImGui.GetCursorScreenPos();

                        ImGui.SetCursorScreenPos(
                            rowOrigin +
                            new Vector2(14f, 16f));

                        ImGui.TextUnformatted(
                            request.OtherDisplayName);

                        var declineSize =
                            new Vector2(84f, 30f);

                        var acceptSize =
                            new Vector2(84f, 30f);

                        var declineX =
                            rowOrigin.X +
                            ImGui.GetWindowWidth() -
                            declineSize.X -
                            12f;

                        var acceptX =
                            declineX -
                            acceptSize.X -
                            8f;

                        ImGui.SetCursorScreenPos(
                            new Vector2(
                                acceptX,
                                rowOrigin.Y + 10f));

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
                            if (ImGui.Button(
                                "Accept",
                                acceptSize))
                            {
                                var token =
                                    session.Token;

                                _ = Task.Run(async () =>
                                {
                                    await friendsClient
                                        .AcceptRequestAsync(
                                            token,
                                            request.Id);

                                    friendsDirty =
                                        true;
                                });
                            }
                        }

                        ImGui.SetCursorScreenPos(
                            new Vector2(
                                declineX,
                                rowOrigin.Y + 10f));

                        using (ImRaii.PushStyle(
                            ImGuiStyleVar.FrameRounding,
                            7f))
                        {
                            if (ImGui.Button(
                                "Decline",
                                declineSize))
                            {
                                var token =
                                    session.Token;

                                _ = Task.Run(async () =>
                                {
                                    await friendsClient
                                        .DeclineRequestAsync(
                                            token,
                                            request.Id);

                                    friendsDirty =
                                        true;
                                });
                            }
                        }
                    }
                }

                ImGui.PopID();

                ImGui.Dummy(
                    new Vector2(0f, 6f));
            }

            ImGui.Dummy(
                new Vector2(0f, 10f));
        }

        // ---------------------------------------------------------
        // Outgoing requests
        // ---------------------------------------------------------

        if (friendRequests.Outgoing.Length > 0)
        {
            ImGui.TextColored(
                MutedText,
                "Waiting for them to accept:");

            ImGui.Dummy(
                new Vector2(0f, 5f));

            foreach (var request in friendRequests.Outgoing)
            {
                ImGui.BulletText(
                    request.OtherDisplayName);
            }

            ImGui.Dummy(
                new Vector2(0f, 12f));
        }

        // ---------------------------------------------------------
        // Your friends
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            $"Your friends ({friends.Length})");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(
            new Vector2(0f, 10f));

        // ---------------------------------------------------------
        // Empty friends state
        // ---------------------------------------------------------

        if (friends.Length == 0)
        {
            const float emptyHeight = 235f;

            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                10f))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(0.045f, 0.06f, 0.10f, 1f)))
            using (var emptyCard = ImRaii.Child(
                "##friendsEmpty",
                new Vector2(-1f, emptyHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (emptyCard)
                {
                    var origin =
                        ImGui.GetCursorScreenPos();

                    var width =
                        ImGui.GetWindowWidth();

                    var centreX =
                        origin.X +
                        width * 0.5f;

                    // Icon circle
                    ImGui.GetWindowDrawList()
                        .AddCircleFilled(
                            new Vector2(
                                centreX,
                                origin.Y + 76f),
                            34f,
                            ImGui.GetColorU32(
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.10f)));

                    var iconText =
                        FontAwesomeIcon.Users.ToIconString();

                    Vector2 iconSize;

                    using (ImRaii.PushFont(
                        UiBuilder.IconFont))
                    {
                        iconSize =
                            ImGui.CalcTextSize(
                                iconText);

                        ImGui.GetWindowDrawList()
                            .AddText(
                                new Vector2(
                                    centreX -
                                    iconSize.X * 0.5f,
                                    origin.Y +
                                    76f -
                                    iconSize.Y * 0.5f),
                                ImGui.GetColorU32(
                                    Accent),
                                iconText);
                    }

                    const string emptyTitle =
                        "No friends yet";

                    var titleSize =
                        ImGui.CalcTextSize(
                            emptyTitle);

                    ImGui.GetWindowDrawList()
                        .AddText(
                            new Vector2(
                                centreX -
                                titleSize.X * 0.5f,
                                origin.Y + 126f),
                            ImGui.GetColorU32(
                                Vector4.One),
                            emptyTitle);

                    var emptyText =
                        friendsLoading
                            ? "Loading..."
                            : "Add someone above to get started.";

                    var emptyTextSize =
                        ImGui.CalcTextSize(
                            emptyText);

                    ImGui.GetWindowDrawList()
                        .AddText(
                            new Vector2(
                                centreX -
                                emptyTextSize.X * 0.5f,
                                origin.Y + 156f),
                            ImGui.GetColorU32(
                                MutedText),
                            emptyText);
                }
            }
        }
        else
        {
            // -----------------------------------------------------
            // Populated friend list
            // -----------------------------------------------------

            foreach (var friend in friends)
            {
                ImGui.PushID(
                    friend.AccountId);

                var rowHeight = Ui(62f);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.ChildRounding,
                    8f))
                using (ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    new Vector4(0.045f, 0.06f, 0.10f, 1f)))
                using (var row = ImRaii.Child(
                    "##friendRow",
                    new Vector2(-1f, rowHeight),
                    false,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
                {
                    if (row)
                    {
                        var rowOrigin =
                            ImGui.GetCursorScreenPos();

                        DrawAvatarChip(
                            friend.AvatarIcon,
                            friend.AvatarColorHex,
                            34,
                            friend.AvatarImageUrl);

                        ImGui.SameLine(
                            0f,
                            10f);

                        ImGui.BeginGroup();

                        if (ImGui.SmallButton(
                            friend.DisplayName))
                        {
                            OpenProfilePopup(
                                session,
                                friend.AccountId,
                                friend.DisplayName);
                        }

                        if (UserRoles.IsDeveloper(friend.DisplayName))
                        {
                            ImGui.SameLine(
                                0f,
                                6f);

                            DrawDeveloperBadge();
                        }

                        var detail =
                            friend.StatusMessage is { Length: > 0 } status
                                ? status
                                : friend.WatchingLabel is { Length: > 0 } watching
                                    ? watching
                                    : friend.Online
                                        ? "Online"
                                        : "Offline";

                        ImGui.TextColored(
                            friend.Online
                                ? Good
                                : MutedText,
                            detail);

                        ImGui.EndGroup();

                        var blockSize =
                            new Vector2(72f, 30f);

                        var removeSize =
                            new Vector2(76f, 30f);

                        var messageSize =
                            new Vector2(84f, 30f);

                        var right =
                            rowOrigin.X +
                            ImGui.GetWindowWidth() -
                            12f;

                        ImGui.SetCursorScreenPos(
                            new Vector2(
                                right -
                                blockSize.X,
                                rowOrigin.Y + 16f));

                        using (ImRaii.PushColor(
                            ImGuiCol.Text,
                            Danger))
                        {
                            if (ImGui.Button(
                                "Block",
                                blockSize))
                            {
                                var token =
                                    session.Token;

                                var accountId =
                                    friend.AccountId;

                                _ = Task.Run(async () =>
                                {
                                    await friendsClient
                                        .BlockAsync(
                                            token,
                                            accountId);

                                    friendsDirty =
                                        true;
                                });
                            }
                        }

                        ImGui.SetCursorScreenPos(
                            new Vector2(
                                right -
                                blockSize.X -
                                8f -
                                removeSize.X,
                                rowOrigin.Y + 16f));

                        if (ImGui.Button(
                            "Remove",
                            removeSize))
                        {
                            var token =
                                session.Token;

                            var accountId =
                                friend.AccountId;

                            _ = Task.Run(async () =>
                            {
                                await friendsClient
                                    .RemoveFriendAsync(
                                        token,
                                        accountId);

                                friendsDirty =
                                    true;
                            });
                        }

                        ImGui.SetCursorScreenPos(
                            new Vector2(
                                right -
                                blockSize.X -
                                removeSize.X -
                                messageSize.X -
                                16f,
                                rowOrigin.Y + 16f));

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
                                "Message",
                                messageSize))
                            {
                                StartOrOpenConversation(
                                    session,
                                    friend.AccountId,
                                    friend.DisplayName);
                            }
                        }
                    }
                }

                ImGui.PopID();

                ImGui.Dummy(
                    new Vector2(0f, 7f));
            }
        }

        // ---------------------------------------------------------
        // Blocked accounts
        // ---------------------------------------------------------

        if (blockedAccounts.Length > 0)
        {
            ImGui.Dummy(
                new Vector2(0f, 18f));

            ImGui.TextUnformatted(
                $"Blocked ({blockedAccounts.Length})");

            ImGui.Dummy(
                new Vector2(0f, 8f));

            foreach (var blocked in blockedAccounts)
            {
                ImGui.PushID(
                    blocked.Id);

                ImGui.TextUnformatted(
                    blocked.DisplayName);

                ImGui.SameLine();

                if (ImGui.SmallButton(
                    "Unblock"))
                {
                    var token =
                        session.Token;

                    var accountId =
                        blocked.Id;

                    _ = Task.Run(async () =>
                    {
                        await friendsClient
                            .UnblockAsync(
                                token,
                                accountId);

                        friendsDirty =
                            true;
                    });
                }

                ImGui.PopID();
            }
        }
    }

    private static void DrawDeveloperBadge()
    {
        const string badgeText =
            "Developer";

        var textSize =
            ImGui.CalcTextSize(
                badgeText);

        const float padX = 7f;
        const float padY = 3f;

        var min =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                textSize.X + padX * 2f,
                textSize.Y + padY * 2f);


        var drawList =
            ImGui.GetWindowDrawList();


        drawList.AddRectFilled(
            min,
            min + size,
            ImGui.GetColorU32(
                new Vector4(
                    0.55f,
                    0.30f,
                    1f,
                    0.28f)),
            6f);


        drawList.AddRect(
            min,
            min + size,
            ImGui.GetColorU32(
                new Vector4(
                    0.65f,
                    0.45f,
                    1f,
                    0.55f)),
            6f);


        drawList.AddText(
            min +
            new Vector2(
                padX,
                padY - 1f),
            ImGui.GetColorU32(
                Vector4.One),
            badgeText);


        ImGui.Dummy(
            size);
    }

    private void RequestFriendSearch(CharacterSession session, string query)
    {
        var trimmed = query.Trim();
        if (string.Equals(trimmed, friendSearchQuery, StringComparison.Ordinal))
        {
            return;
        }

        friendSearchQuery = trimmed;
        var ticket = Interlocked.Increment(ref friendSearchGeneration);
        if (trimmed.Length < DisplayNameRules.MinLength)
        {
            friendSearchResults = [];
            friendSearchLoading = false;
            return;
        }

        friendSearchLoading = true;
        var token = session.Token;
        _ = Task.Run(async () =>
        {
            var results = await friendsClient.SearchAsync(token, trimmed);
            if (Interlocked.Read(ref friendSearchGeneration) != ticket)
            {
                return;
            }

            friendSearchResults = results ?? [];
            friendSearchLoading = false;
        });
    }

    private void DrawFriendSearchResults(CharacterSession session)
    {
        if (friendSearchLoading)
        {
            ImGui.TextColored(MutedText, "Searching...");
            return;
        }

        if (friendSearchQuery.Length >= DisplayNameRules.MinLength && friendSearchResults.Length == 0)
        {
            ImGui.TextColored(MutedText, "No one found with that name.");
            return;
        }

        foreach (var result in friendSearchResults)
        {
            ImGui.PushID(result.AccountId);
            DrawAvatarChip(result.AvatarIcon, result.AvatarColorHex, 20, result.AvatarImageUrl);
            ImGui.SameLine();
            ImGui.Text(result.DisplayName);
            ImGui.SameLine();
            DrawFriendSearchAction(session, result);
            ImGui.PopID();
        }
    }

    private void DrawFriendSearchAction(CharacterSession session, FriendSearchResultDto result)
    {
        switch (result.Relation)
        {
            case FriendSearchRelation.Friends:
                ImGui.TextColored(MutedText, "Already friends");
                return;
            case FriendSearchRelation.Pending:
                ImGui.TextColored(MutedText, "Request pending");
                return;
        }

        using (ImRaii.Disabled(friendSearchSendingIds.Contains(result.AccountId)))
        {
            if (!ImGui.SmallButton("Add"))
            {
                return;
            }

            friendSearchSendingIds.Add(result.AccountId);
            var token = session.Token;
            var accountId = result.AccountId;
            var displayName = result.DisplayName;
            _ = Task.Run(async () =>
            {
                var ok = await friendsClient.SendRequestAsync(token, displayName);
                friendSearchSendingIds.Remove(accountId);
                friendsError = ok ? null : "Couldn't send that request - you may already be friends.";
                if (!ok)
                {
                    return;
                }

                friendsDirty = true;
                for (var index = 0; index < friendSearchResults.Length; index++)
                {
                    if (friendSearchResults[index].AccountId == accountId)
                    {
                        friendSearchResults[index] = friendSearchResults[index] with { Relation = FriendSearchRelation.Pending };
                    }
                }
            });
        }
    }

    // Fired from StreamClient's receive loop (a background thread) - updates `friends` in place
    // rather than setting friendsDirty, since a full REST round-trip for a single online/watching
    // change would be wasteful and would visibly lag behind the push. Same unsynchronized
    // cross-thread field access already used throughout this plugin (e.g. StreamClient.Roster).
    private void ApplyPresenceUpdate(SocialControl update)
    {
        if (update.AccountId is not { Length: > 0 } accountId)
        {
            return;
        }

        for (var index = 0; index < friends.Length; index++)
        {
            if (friends[index].AccountId != accountId)
            {
                continue;
            }

            friends[index] = friends[index] with { Online = update.Online ?? false, WatchingLabel = update.WatchingLabel };
            break;
        }
    }

    private void RefreshFriends(string bearerToken)
    {
        friendsDirty = false;
        friendsLoading = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var friendsTask = friendsClient.GetFriendsAsync(bearerToken);
                var requestsTask = friendsClient.GetRequestsAsync(bearerToken);
                var blocksTask = friendsClient.GetBlocksAsync(bearerToken);
                await Task.WhenAll(friendsTask, requestsTask, blocksTask);

                friends = await friendsTask ?? [];
                friendRequests = await requestsTask ?? new FriendRequestsPage([], []);
                blockedAccounts = await blocksTask ?? [];
            }
            finally
            {
                friendsLoading = false;
            }
        });
    }
}
