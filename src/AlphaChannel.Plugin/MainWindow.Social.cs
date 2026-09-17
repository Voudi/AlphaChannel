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
    private bool friendsMessageIsSuccess;
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

    private string friendListSearchInput =
      string.Empty;

    //
    // 0 = All
    // 1 = Online
    // 2 = Offline
    //
    private int friendListStatusFilter;

    private string? friendsLoadError;

    // Called from Plugin.cs's right-click "Add Friend" context-menu handler - surfaces the result
    // the same way the in-page "Add a friend" flow does (friendsError + a refreshed request list),
    // and jumps straight to Friends so the outcome is actually visible instead of silent.
    internal void HandleAddFriendByCharacterResult(
        FriendsClient.FriendRequestOutcome outcome, string characterName)
    {
        friendsDirty = true;
        friendsMessageIsSuccess = outcome == FriendsClient.FriendRequestOutcome.Sent;
        friendsError = outcome switch
        {
            FriendsClient.FriendRequestOutcome.Sent => $"Friend request sent to {characterName}.",
            FriendsClient.FriendRequestOutcome.AlreadyFriends => $"You and {characterName} are already Alpha Channel friends.",
            FriendsClient.FriendRequestOutcome.AlreadyPending => $"A friend request with {characterName} is already pending.",
            FriendsClient.FriendRequestOutcome.NotFound => $"Couldn't find an Alpha Channel account linked to {characterName}.",
            _ => $"Couldn't send a friend request to {characterName}.",
        };
        currentPage = HomePage.Friends;
        SetMinimized(false);
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

            ImGui.Dummy(UiVec(0f, 10f));
        }

        // ---------------------------------------------------------
        // Connect with friends
        // ---------------------------------------------------------

        SetUiFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Connect with friends");

        SetUiFontScale(
            1f);

        if (friendsLoading)
        {
            ImGui.SameLine(
                0f,
                9f);

            ImGui.TextColored(
                MutedText,
                "Loading...");
        }

        ImGui.Dummy(
            UiVec(0f, 10f));

        var connectCardGap = Ui(12f);

        var connectAreaWidth =
            ImGui.GetContentRegionAvail().X;

        var connectCardWidth =
            (
                connectAreaWidth -
                connectCardGap *
                2f
            ) /
            3f;

        // =========================================================
        // Your invite code
        // =========================================================

        DrawFriendConnectCard(
            "##ownInviteCodeCard",
            FontAwesomeIcon.UserFriends,
            "Friend Code",
            "Share this code with a friend.",
            connectCardWidth,
            () =>
            {
                using (ImRaii.PushStyle(
      ImGuiStyleVar.ChildRounding,
      7f))
                using (ImRaii.PushStyle(
                    ImGuiStyleVar.ChildBorderSize,
                    1f))
                using (ImRaii.PushColor(
                    ImGuiCol.Border,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.38f)))
                using (ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    new Vector4(
                        0.055f,
                        0.07f,
                        0.115f,
                        1f)))
                using (var codeBox =
                    ImRaii.Child(
                        "##ownFriendCode",
                        UiVec(-1f, 42f),
                        true,
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse))
                {
                    if (codeBox)
                    {
                        SetUiFontScale(
                            2f);

                        var codeSize =
                            ImGui.CalcTextSize(
                                session.InviteCode);

                        ImGui.SetCursorPosX(
                            (
                                ImGui.GetWindowWidth() -
                                codeSize.X
                            ) *
                            0.5f);

                        ImGui.SetCursorPosY(
                            7f);

                        ImGui.TextColored(
                            Vector4.One,
                            session.InviteCode);

                        SetUiFontScale(
                            1f);
                    }
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "Your friend code can be used instead of your username " +
                        "for friends to add you. The code will re-generate after each use.");
                }

                ImGui.Dummy(
                    UiVec(0f, 7f));

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    7f))
                using (ImRaii.PushColor(
                    ImGuiCol.Button,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.18f))
                    .Push(
                        ImGuiCol.ButtonHovered,
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.30f))
                    .Push(
                        ImGuiCol.ButtonActive,
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.40f))
                    .Push(
                        ImGuiCol.Border,
                        Accent))
                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameBorderSize,
                    1f))
                {
                    if (ImGui.Button(
                            "Copy",
                            UiVec(-1f, 34f)))
                    {
                        ImGui.SetClipboardText(
                            session.InviteCode);
                    }
                }
            });

        ImGui.SameLine(
            0f,
            connectCardGap);

        // =========================================================
        // Redeem an invite
        // =========================================================

        DrawFriendConnectCard(
            "##redeemInviteCard",
            FontAwesomeIcon.TicketAlt,
            "Enter friend code",
            "Enter a friend's friend code.",
            connectCardWidth,
            () =>
            {
                ImGui.SetNextItemWidth(
                    -1f);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    7f))
                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameBorderSize,
                    1f))
                using (ImRaii.PushColor(
                    ImGuiCol.Border,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.48f)))
                using (ImRaii.PushColor(
                    ImGuiCol.FrameBg,
                    new Vector4(
                        0.07f,
                        0.085f,
                        0.135f,
                        1f))
                    .Push(
                        ImGuiCol.FrameBgHovered,
                        new Vector4(
                            0.085f,
                            0.105f,
                            0.165f,
                            1f))
                    .Push(
                        ImGuiCol.FrameBgActive,
                        new Vector4(
                            0.09f,
                            0.115f,
                            0.18f,
                            1f)))
                {
                    ImGui.InputTextWithHint(
                        "##inviteCode",
                        "Paste friend code",
                        ref inviteCodeInput,
                        16);
                }

                ImGui.Dummy(
                    UiVec(0f, 7f));

                using (ImRaii.Disabled(
                    inviteCodeRedeeming ||
                    inviteCodeInput.Trim().Length == 0))
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
                            inviteCodeRedeeming
                                ? "Adding..."
                                : "Add friend",
                            UiVec(-1f, 34f)))
                    {
                        inviteCodeRedeeming =
                            true;

                        inviteCodeError =
                            null;

                        var code =
                            inviteCodeInput.Trim();

                        var token =
                            session.Token;

                        _ = Task.Run(
                            async () =>
                            {
                                var ok =
                                    await friendsClient
                                        .RedeemInviteCodeAsync(
                                            token,
                                            code);

                                inviteCodeRedeeming =
                                    false;

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
            });

        ImGui.SameLine(
            0f,
            connectCardGap);

        // =========================================================
        // Find by username
        // =========================================================

        DrawFriendConnectCard(
            "##findFriendCard",
            FontAwesomeIcon.Search,
            "Find by username",
            "Enter their full Alpha Channel username.",
            connectCardWidth,
            () =>
            {
                ImGui.SetNextItemWidth(
                    -1f);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    7f))
                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameBorderSize,
                    1f))
                using (ImRaii.PushColor(
                    ImGuiCol.Border,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.48f)))
                using (ImRaii.PushColor(
                    ImGuiCol.FrameBg,
                    new Vector4(
                        0.07f,
                        0.085f,
                        0.135f,
                        1f))
                    .Push(
                        ImGuiCol.FrameBgHovered,
                        new Vector4(
                            0.085f,
                            0.105f,
                            0.165f,
                            1f))
                    .Push(
                        ImGuiCol.FrameBgActive,
                        new Vector4(
                            0.09f,
                            0.115f,
                            0.18f,
                            1f)))
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

                ImGui.Dummy(
                    UiVec(0f, 7f));

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
                            "Search",
                            UiVec(-1f, 34f)))
                    {
                        RequestFriendSearch(
                            session,
                            friendSearchInput);
                    }
                }
            });

        // ---------------------------------------------------------
        // Add-friend errors / live search results
        // ---------------------------------------------------------

        if (inviteCodeError is { Length: > 0 } codeError)
        {
            ImGui.Dummy(
                UiVec(0f, 6f));

            ImGui.TextColored(
                Danger,
                codeError);
        }

        if (friendSearchLoading ||
            friendSearchResults.Length > 0 ||
            friendSearchQuery.Length >= DisplayNameRules.MinLength)
        {
            ImGui.Dummy(
                UiVec(0f, 8f));

            DrawFriendSearchResults(
                session);
        }

        if (friendsError is { Length: > 0 } error)
        {
            ImGui.Dummy(
                UiVec(0f, 6f));

            ImGui.TextColored(
                friendsMessageIsSuccess ? Good : Danger,
                error);
        }

        // ---------------------------------------------------------
        // Horizontal divider
        // ---------------------------------------------------------

        ImGui.Dummy(
            UiVec(0f, 16f));

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
                Ui(18f)));

        // ---------------------------------------------------------
        // Incoming requests
        // ---------------------------------------------------------

        if (friendRequests.Incoming.Length > 0)
        {
            ImGui.TextColored(
                Accent,
                $"Friend requests ({friendRequests.Incoming.Length})");

            ImGui.Dummy(
                UiVec(0f, 8f));

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
                            UiVec(14f, 16f));

                        ImGui.TextUnformatted(
                            request.OtherDisplayName);

                        var declineSize =
                            UiVec(84f, 30f);

                        var acceptSize =
                            UiVec(84f, 30f);

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
                                rowOrigin.Y + Ui(10f)));

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
                                rowOrigin.Y + Ui(10f)));

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
                    UiVec(0f, 6f));
            }

            ImGui.Dummy(
                UiVec(0f, 10f));
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
                UiVec(0f, 5f));

            foreach (var request in friendRequests.Outgoing)
            {
                ImGui.BulletText(
                    request.OtherDisplayName);
            }

            ImGui.Dummy(
                UiVec(0f, 12f));
        }

        // ---------------------------------------------------------
        // Your friends
        // ---------------------------------------------------------

        var visibleFriends =
            friends
                .Where(
                    friend =>
                        friendListStatusFilter switch
                        {
                            1 =>
                                friend.Online,

                            2 =>
                                !friend.Online,

                            _ =>
                                true
                        })
                .Where(
                    friend =>
                        string.IsNullOrWhiteSpace(
                            friendListSearchInput) ||
                        friend.DisplayName.Contains(
                            friendListSearchInput.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(
                    friend =>
                        friend.Online)
                .ThenBy(
                    friend =>
                        friend.DisplayName,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        SetUiFontScale(
            1.15f);

        ImGui.TextColored(
            Vector4.One,
            $"Your friends ({friends.Length})");

        SetUiFontScale(
            1f);

        var friendSearchWidth = Ui(250f);

        var allFilterWidth = Ui(48f);

        var statusFilterWidth = Ui(66f);

        var filterGap = Ui(6f);

        var friendControlsWidth =
            friendSearchWidth +
            10f +
            allFilterWidth +
            statusFilterWidth *
            2f +
            filterGap *
            2f;

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            ImGui.GetWindowContentRegionMax().X -
            friendControlsWidth);

        ImGui.SetNextItemWidth(
            friendSearchWidth);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            7f))
        using (ImRaii.PushColor(
            ImGuiCol.FrameBg,
            new Vector4(
                0.035f,
                0.045f,
                0.075f,
                1f)))
        {
            ImGui.InputTextWithHint(
                "##friendListSearch",
                "Search friends...",
                ref friendListSearchInput,
                64);
        }

        ImGui.SameLine(
            0f,
            10f);

        DrawFriendListFilterButton(
            "All",
            0,
            new Vector2(
                allFilterWidth,
                Ui(30f)));

        ImGui.SameLine(
            0f,
            filterGap);

        DrawFriendListFilterButton(
            "Online",
            1,
            new Vector2(
                statusFilterWidth,
                Ui(30f)));

        ImGui.SameLine(
            0f,
            filterGap);

        DrawFriendListFilterButton(
            "Offline",
            2,
            new Vector2(
                statusFilterWidth,
                Ui(30f)));

        ImGui.Dummy(
            UiVec(0f, 12f));

        if (friendsLoadError is { Length: > 0 } loadError)
        {
            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f))
            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildBorderSize,
                1f))
            using (ImRaii.PushColor(
                ImGuiCol.Border,
                new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.45f)))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.07f)))
            using (var errorCard =
                ImRaii.Child(
                    "##friendsLoadError",
                    UiVec(-1f, 48f),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (errorCard)
                {
                    ImGui.TextColored(
                        Danger,
                        loadError);

                    ImGui.SameLine();

                    if (ImGui.SmallButton(
                            "Retry"))
                    {
                        friendsLoadError =
                            null;

                        friendsDirty =
                            true;
                    }
                }
            }

            ImGui.Dummy(
                UiVec(0f, 8f));
        }

        // ---------------------------------------------------------
        // Empty friends state
        // ---------------------------------------------------------

        if (visibleFriends.Length == 0)
        {
            var emptyHeight = Ui(235f);

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
                                origin.Y + Ui(76f)),
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
                            .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                                new Vector2(
                                    centreX -
                                    iconSize.X * 0.5f,
                                    origin.Y +
                                    Ui(76f) -
                                    iconSize.Y * 0.5f),
                                ImGui.GetColorU32(
                                    Accent),
                                iconText);
                    }

                    var emptyTitle =
                        friends.Length == 0
                            ? "No friends yet"
                            : "No matching friends";

                    var titleSize =
                        ImGui.CalcTextSize(
                            emptyTitle);

                    ImGui.GetWindowDrawList()
                        .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                            new Vector2(
                                centreX -
                                titleSize.X * 0.5f,
                                origin.Y + Ui(126f)),
                            ImGui.GetColorU32(
                                Vector4.One),
                            emptyTitle);

                    var emptyText =
                        friendsLoading
                            ? "Loading..."
                            : friends.Length == 0
                                ? "Add someone above to get started."
                                : string.IsNullOrWhiteSpace(
                                    friendListSearchInput)
                                    ? "No friends match this filter."
                                    : "No friends match that search.";

                    var emptyTextSize =
                        ImGui.CalcTextSize(
                            emptyText);

                    ImGui.GetWindowDrawList()
                        .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                            new Vector2(
                                centreX -
                                emptyTextSize.X * 0.5f,
                                origin.Y + Ui(156f)),
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

            foreach (var friend in visibleFriends)
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

                        var friendAvatarSize =
                            Ui(48f);

                        ImGui.SetCursorScreenPos(
                            new Vector2(
                                rowOrigin.X +
                                Ui(12f),
                                rowOrigin.Y +
                                MathF.Max(
                                    0f,
                                    (
                                        rowHeight -
                                        friendAvatarSize
                                    ) *
                                    0.5f -
                                    Ui(4f))));

                        DrawAvatarChip(
                            friend.AvatarIcon,
                            friend.AvatarColorHex,
                            friendAvatarSize,
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

                        var detail =
         friend.WatchingLabel is { Length: > 0 } watching
             ? watching
             : friend.StatusMessage is { Length: > 0 } status
                 ? status
                 : friend.Online
                     ? "Online"
                     : "Offline";

                        var statusLineStart =
                            ImGui.GetCursorScreenPos();

                        var statusLineHeight =
                            ImGui.GetTextLineHeight();

                        ImGui.GetWindowDrawList()
                            .AddCircleFilled(
                                new Vector2(
                                    statusLineStart.X +
                                    Ui(4f),
                                    statusLineStart.Y +
                                    statusLineHeight *
                                    0.5f),
                                3.5f,
                                ImGui.GetColorU32(
                                    friend.Online
                                        ? Good
                                        : MutedText));

                        ImGui.Dummy(
                            new Vector2(
                                Ui(10f),
                                statusLineHeight));

                        ImGui.SameLine(
                            0f,
                            3f);

                        ImGui.TextColored(
                            friend.Online
                                ? Good
                                : MutedText,
                            detail);

                        ImGui.EndGroup();

                        var menuSize =
     UiVec(38f, 30f);

                        var messageSize =
                            UiVec(84f, 30f);

                        var profileSize =
                            UiVec(94f, 30f);

                        var joinSize =
                            UiVec(70f, 30f);

                        var actionGap = Ui(8f);

                        var right =
                            rowOrigin.X +
                            ImGui.GetWindowWidth() -
                            12f;

                        // -----------------------------------------
                        // More-actions menu
                        // -----------------------------------------

                        ImGui.SetCursorScreenPos(
                            new Vector2(
                                right -
                                menuSize.X,
                                rowOrigin.Y +
                                Ui(16f)));

                        using (ImRaii.PushStyle(
                            ImGuiStyleVar.FrameRounding,
                            7f))
                        using (ImRaii.PushColor(
                            ImGuiCol.Button,
                            new Vector4(
                                0.055f,
                                0.065f,
                                0.11f,
                                1f))
                            .Push(
                                ImGuiCol.ButtonHovered,
                                CardBgHover)
                            .Push(
                                ImGuiCol.ButtonActive,
                                AccentActive))
                        {
                            if (ImGui.Button(
                                    "...##friendActions",
                                    menuSize))
                            {
                                ImGui.OpenPopup(
                                    "##friendActionsPopup");
                            }
                        }

                        if (ImGui.BeginPopup(
                                "##friendActionsPopup"))
                        {
                            if (ImGui.MenuItem(
                                    "Remove friend"))
                            {
                                var token =
                                    session.Token;

                                var accountId =
                                    friend.AccountId;

                                _ = Task.Run(
                                    async () =>
                                    {
                                        await friendsClient
                                            .RemoveFriendAsync(
                                                token,
                                                accountId);

                                        friendsDirty =
                                            true;
                                    });
                            }

                            using (ImRaii.PushColor(
                                ImGuiCol.Text,
                                Danger))
                            {
                                if (ImGui.MenuItem(
                                        "Block"))
                                {
                                    var token =
                                        session.Token;

                                    var accountId =
                                        friend.AccountId;

                                    _ = Task.Run(
                                        async () =>
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

                            ImGui.EndPopup();
                        }

                        // -----------------------------------------
                        // Message
                        // -----------------------------------------

                        var messageX =
                            right -
                            menuSize.X -
                            actionGap -
                            messageSize.X;

                        ImGui.SetCursorScreenPos(
                            new Vector2(
                                messageX,
                                rowOrigin.Y +
                                Ui(16f)));

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
                                    "Message",
                                    messageSize))
                            {
                                StartOrOpenConversation(
                                    session,
                                    friend.AccountId,
                                    friend.DisplayName);
                            }
                        }

                        // -----------------------------------------
                        // View profile
                        // -----------------------------------------

                        var profileX =
                            messageX -
                            actionGap -
                            profileSize.X;

                        ImGui.SetCursorScreenPos(
                            new Vector2(
                                profileX,
                                rowOrigin.Y +
                                Ui(16f)));

                        using (ImRaii.PushStyle(
                            ImGuiStyleVar.FrameRounding,
                            7f))
                        using (ImRaii.PushColor(
                            ImGuiCol.Button,
                            new Vector4(
                                0.055f,
                                0.065f,
                                0.11f,
                                1f))
                            .Push(
                                ImGuiCol.ButtonHovered,
                                CardBgHover)
                            .Push(
                                ImGuiCol.ButtonActive,
                                AccentActive))
                        {
                            if (ImGui.Button(
                                    "View Profile",
                                    profileSize))
                            {
                                OpenProfilePopup(
                                    session,
                                    friend.AccountId,
                                    friend.DisplayName);
                            }
                        }

                        // -----------------------------------------
                        // Join Watch Party
                        // -----------------------------------------

                        if (friend.HostingJoinableWatchParty)
                        {
                            var joinX =
                                profileX -
                                actionGap -
                                joinSize.X;

                            ImGui.SetCursorScreenPos(
                                new Vector2(
                                    joinX,
                                    rowOrigin.Y +
                                    Ui(16f)));

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
                                        "Join",
                                        joinSize))
                                {
                                    OpenPlayerAndJoin(
                                        friend.DisplayName);
                                }
                            }
                        }
                    }
                }

                ImGui.PopID();

                ImGui.Dummy(
                    UiVec(0f, 7f));
            }
        }



        // ---------------------------------------------------------
        // Blocked accounts
        // ---------------------------------------------------------

        if (blockedAccounts.Length > 0)
        {
            ImGui.Dummy(
                UiVec(0f, 18f));

            ImGui.TextUnformatted(
                $"Blocked ({blockedAccounts.Length})");

            ImGui.Dummy(
                UiVec(0f, 8f));

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
                friendsMessageIsSuccess = false;
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

    private void DrawFriendListFilterButton(
    string label,
    int filter,
    Vector2 size)
    {
        var selected =
            friendListStatusFilter ==
            filter;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            7f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            selected
                ? Accent
                : new Vector4(
                    0.035f,
                    0.045f,
                    0.075f,
                    1f))
            .Push(
                ImGuiCol.ButtonHovered,
                selected
                    ? AccentHover
                    : CardBgHover)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            if (ImGui.Button(
                    $"{label}##friendStatusFilter_{filter}",
                    size))
            {
                friendListStatusFilter =
                    filter;
            }
        }
    }

    private void DrawFriendConnectCard(
    string id,
    FontAwesomeIcon icon,
    string title,
    string description,
    float width,
    Action drawContents)
    {
        var cardHeight = Ui(174f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            9f))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildBorderSize,
            1f))
        using (ImRaii.PushColor(
            ImGuiCol.Border,
            new Vector4(
                Accent.X,
                Accent.Y,
                Accent.Z,
                0.48f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(
                0.035f,
                0.045f,
                0.075f,
                0.92f)))
        using (var card =
            ImRaii.Child(
                id,
                new Vector2(
                    width,
                    cardHeight),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card)
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

            ImGui.SameLine(
                0f,
                9f);

            ImGui.TextColored(
                Vector4.One,
                title);

            ImGui.Dummy(
                UiVec(0f, 9f));

            drawContents();

            ImGui.Dummy(
                UiVec(0f, 7f));

            SetUiFontScale(
                0.80f);

            ImGui.TextColored(
                MutedText,
                description);

            SetUiFontScale(
                1f);
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

            friends[index] =
    friends[index] with
    {
        Online =
            update.Online ??
            false,

        WatchingLabel =
            update.WatchingLabel,

        HostingJoinableWatchParty =
            update.HostingJoinableWatchParty ??
            false
    };
            break;
        }
    }

    private void RefreshFriends(
    string bearerToken)
    {
        friendsDirty =
            false;

        friendsLoading =
            true;

        friendsLoadError =
            null;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var friendsTask =
                        friendsClient.GetFriendsAsync(
                            bearerToken);

                    var requestsTask =
                        friendsClient.GetRequestsAsync(
                            bearerToken);

                    var blocksTask =
                        friendsClient.GetBlocksAsync(
                            bearerToken);

                    await Task.WhenAll(
                        friendsTask,
                        requestsTask,
                        blocksTask);

                    var loadedFriends =
                        await friendsTask;

                    var loadedRequests =
                        await requestsTask;

                    var loadedBlocks =
                        await blocksTask;

                    if (loadedFriends is null ||
                        loadedRequests is null ||
                        loadedBlocks is null)
                    {
                        if (friendsClient.LastAccessDeniedReason is null)
                        {
                            friendsLoadError =
                                "Friends could not be loaded.";
                        }

                        return;
                    }

                    friends =
                        loadedFriends;

                    friendRequests =
                        loadedRequests;

                    blockedAccounts =
                        loadedBlocks;
                }
                catch (Exception exception)
                {
                    friendsLoadError =
                        "Friends could not be loaded.";

                    AepLog.Warning(
                        $"[Friends] Failed to refresh: {exception.Message}");
                }
                finally
                {
                    friendsLoading =
                        false;
                }
            });
    }
}
