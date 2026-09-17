using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Modern friend-profile overlay. Room and recent-activity cards are
// added separately after the new shell has been compiled and tested.
internal sealed partial class MainWindow
{
    private bool profilePopupOpen;
    private string? profilePopupAccountId;
    private string? profilePopupFallbackName;
    private AccountProfileDto? profilePopupData;
    private bool profilePopupLoading;
    private bool profilePopupNotAvailable;

    private bool profileDetailsLoading;
    private RoomDirectoryDto? profilePopupRoom;
    private LiveFriendDto? profilePopupLive;
    private ActivityEventDto[] profilePopupRecentActivity = [];
    private long profilePopupLoadGeneration;

    private ProfileFriendConfirmationAction profileFriendConfirmation;
    private bool profileFriendConfirmationPendingOpen;
    private bool profileFriendActionBusy;

    private enum ProfileFriendConfirmationAction
    {
        None,
        RemoveFriend,
        BlockUser,
    }

    private void OpenProfilePopup(
          CharacterSession session,
      string accountId,
      string fallbackDisplayName)
    {
        var loadGeneration =
            Interlocked.Increment(
                ref profilePopupLoadGeneration);

        profilePopupAccountId =
            accountId;

        profilePopupFallbackName =
            fallbackDisplayName;

        profilePopupData =
            null;

        profilePopupNotAvailable =
            false;

        profilePopupLoading =
            true;

        profileDetailsLoading =
            true;

        profilePopupRoom =
            null;

        profilePopupLive =
            null;

        profilePopupRecentActivity =
      [];

        profileFriendConfirmation =
            ProfileFriendConfirmationAction.None;

        profileFriendConfirmationPendingOpen =
            false;

        profileFriendActionBusy =
            false;

        profilePopupOpen =
            true;

        var token =
            session.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var profile =
                        await authClient
                            .GetProfileAsync(
                                token,
                                accountId)
                            .ConfigureAwait(false);

                    if (!IsCurrentProfileLoad(
                            loadGeneration))
                    {
                        return;
                    }

                    profilePopupData =
                        profile;

                    profilePopupNotAvailable =
                        profile is null;

                    await LoadProfileDetailsAsync(
                            token,
                            accountId,
                            loadGeneration)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (IsCurrentProfileLoad(
                            loadGeneration))
                    {
                        profilePopupNotAvailable =
                            true;
                    }

                    AepLog.Warning(
                        $"[Profile] Loading failed: {exception.Message}");
                }
                finally
                {
                    if (IsCurrentProfileLoad(
                            loadGeneration))
                    {
                        profilePopupLoading =
                            false;

                        profileDetailsLoading =
                            false;
                    }
                }
            });
    }

    private async Task LoadProfileDetailsAsync(
     string bearerToken,
     string accountId,
     long loadGeneration)
    {
        RoomDirectoryDto? loadedRoom =
            null;

        LiveFriendDto? loadedLive =
            null;

        ActivityEventDto[] loadedActivity =
            [];

        try
        {
            var rooms =
                await roomsClient
                    .ListAsync(
                        bearerToken)
                    .ConfigureAwait(false);

            if (!IsCurrentProfileLoad(
                    loadGeneration))
            {
                return;
            }

            loadedRoom =
                rooms.FirstOrDefault(
                    room =>
                        string.Equals(
                            room.HostAccountId,
                            accountId,
                            StringComparison.Ordinal));
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Profile] Room lookup failed: {exception.Message}");
        }

        if (!IsCurrentProfileLoad(
                loadGeneration))
        {
            return;
        }

        try
        {
            var liveFriends =
                await liveClient
                    .GetFriendsLiveAsync(
                        bearerToken)
                    .ConfigureAwait(false);

            if (!IsCurrentProfileLoad(
                    loadGeneration))
            {
                return;
            }

            loadedLive =
                liveFriends.FirstOrDefault(
                    live =>
                        string.Equals(
                            live.AccountId,
                            accountId,
                            StringComparison.Ordinal));
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Profile] Live status lookup failed: {exception.Message}");
        }

        if (!IsCurrentProfileLoad(
                loadGeneration))
        {
            return;
        }

        try
        {
            const int maximumInspected =
                100;

            const int maximumDisplayed =
                3;

            var matchingEvents =
                new List<ActivityEventDto>();

            var inspected =
                0;

            long? before =
                null;

            while (inspected <
                   maximumInspected)
            {
                var page =
                    await activityClient
                        .GetFeedAsync(
                            bearerToken,
                            before)
                        .ConfigureAwait(false);

                if (!IsCurrentProfileLoad(
                        loadGeneration))
                {
                    return;
                }

                if (page is null ||
                    page.Items.Length == 0)
                {
                    break;
                }

                var remaining =
                    maximumInspected -
                    inspected;

                foreach (var item in
                         page.Items.Take(
                             remaining))
                {
                    if (IsRemovedPostActivity(item))
                    {
                        continue;
                    }

                    if (!string.Equals(
                            item.ActorAccountId,
                            accountId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    matchingEvents.Add(
                        item);

                    if (matchingEvents.Count >=
                        maximumDisplayed)
                    {
                        break;
                    }
                }

                inspected +=
                    Math.Min(
                        page.Items.Length,
                        remaining);

                if (matchingEvents.Count >=
                        maximumDisplayed ||
                    string.IsNullOrWhiteSpace(
                        page.NextCursor) ||
                    !long.TryParse(
                        page.NextCursor,
                        out var nextCursor))
                {
                    break;
                }

                before =
                    nextCursor;
            }

            loadedActivity =
                matchingEvents
                    .OrderByDescending(
                        item =>
                            item.CreatedAtUnix)
                    .Take(
                        maximumDisplayed)
                    .ToArray();
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Profile] Activity lookup failed: {exception.Message}");
        }

        if (!IsCurrentProfileLoad(
                loadGeneration))
        {
            return;
        }

        //
        // Commit the supplemental results together only while this is
        // still the same open profile request.
        //

        profilePopupRoom =
            loadedRoom;

        profilePopupLive =
            loadedLive;

        profilePopupRecentActivity =
            loadedActivity;
    }

    private bool IsCurrentProfileLoad(
    long loadGeneration)
    {
        return
            profilePopupOpen &&
            Volatile.Read(
                ref profilePopupLoadGeneration) ==
            loadGeneration;
    }

    private void CloseProfilePopup()
    {
        profilePopupOpen =
            false;

        //
        // Invalidate every outstanding profile fetch. The requests may
        // finish in the background, but their results will be discarded.
        //

        Interlocked.Increment(
            ref profilePopupLoadGeneration);
    }

    private void DrawProfilePopup()
    {
        if (!profilePopupOpen)
        {
            return;
        }

        //
        // The parent at this point is the Alpha Channel window.
        // Drawing a transparent overlay exactly over it prevents the
        // popup from dimming the rest of the game.
        //

        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupWidth =
            Math.Min(
                760f,
                parentSize.X - 40f);

        var popupHeight =
            Math.Min(
                720f,
                parentSize.Y - 40f);

        var popupPos =
            new Vector2(
                parentPos.X +
                (
                    parentSize.X -
                    popupWidth
                ) * 0.5f,

                parentPos.Y +
                (
                    parentSize.Y -
                    popupHeight
                ) * 0.5f);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!ImGui.Begin(
                "##profileOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        //
        // Subtle darkening confined to Alpha Channel.
        //

        drawList.AddRectFilled(
            parentPos,
            parentPos +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.48f)));

        ImGui.SetCursorScreenPos(
            popupPos);
        { 
        using var popupStyle =
            ImRaii.PushStyle(
                    ImGuiStyleVar.ChildRounding,
                    14f)
                .Push(
                    ImGuiStyleVar.ChildBorderSize,
                    1f)
                .Push(
                    ImGuiStyleVar.WindowPadding,
                    UiVec(26f, 22f))
                .Push(
                    ImGuiStyleVar.ItemSpacing,
                    UiVec(10f, 9f))
                .Push(
                    ImGuiStyleVar.FrameRounding,
                    8f);

        using var popupColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    new Vector4(
                        0.025f,
                        0.03f,
                        0.06f,
                        0.99f))
                .Push(
                    ImGuiCol.Border,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.8f));

        if (ImGui.BeginChild(
                "##profilePanel",
                new Vector2(
                    popupWidth,
                    popupHeight),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            DrawProfilePopupHeader();

            ImGui.Dummy(
                UiVec(0f, 8f));

            ImGui.Separator();

            ImGui.Dummy(
                UiVec(0f, 14f));

            if (profilePopupLoading)
            {
                DrawProfileLoadingState();
            }
            else if (profilePopupNotAvailable ||
                     profilePopupData is null)
            {
                DrawProfileUnavailableState();
            }
            else
            {
                DrawProfileIdentity(
                    profilePopupData);
            }

            DrawProfilePopupFooter();
        }

            ImGui.EndChild();
        }

        //
        // Draw any friend-action confirmation after the main profile
        // child has ended, keeping it inside the Alpha Channel overlay.
        //

        DrawProfileFriendConfirmation(
            popupPos,
            popupWidth,
            popupHeight);

        ImGui.End();
    }

    private void DrawProfileFriendConfirmation(
    Vector2 profilePosition,
    float profileWidth,
    float profileHeight)
    {
        if (profileFriendConfirmationPendingOpen)
        {
            ImGui.OpenPopup(
                "Confirm action##profileFriendConfirmation");

            profileFriendConfirmationPendingOpen =
                false;
        }

        if (profileFriendConfirmation ==
            ProfileFriendConfirmationAction.None)
        {
            return;
        }

        //
        // Darken the profile itself without dimming the rest of the game.
        //

        ImGui.GetWindowDrawList()
            .AddRectFilled(
                profilePosition,
                profilePosition +
                new Vector2(
                    profileWidth,
                    profileHeight),
                ImGui.GetColorU32(
                    new Vector4(
                        0f,
                        0f,
                        0f,
                        0.52f)),
                14f);

        var blocking =
            profileFriendConfirmation ==
            ProfileFriendConfirmationAction.BlockUser;

        var confirmationSize =
            blocking
                ? UiVec(480f, 255f)
                : UiVec(430f, 205f);

        ImGui.SetNextWindowPos(
            profilePosition +
            new Vector2(
                profileWidth * 0.5f,
                profileHeight * 0.5f),
            ImGuiCond.Always,
            new Vector2(
                0.5f,
                0.5f));

        ImGui.SetNextWindowSize(
            confirmationSize,
            ImGuiCond.Always);

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoDocking;

        using var popupStyle =
            ImRaii.PushStyle(
                    ImGuiStyleVar.PopupRounding,
                    12f)
                .Push(
                    ImGuiStyleVar.PopupBorderSize,
                    1f)
                .Push(
                    ImGuiStyleVar.WindowPadding,
                    UiVec(22f, 20f))
                .Push(
                    ImGuiStyleVar.FrameRounding,
                    8f);

        using var popupColors =
            ImRaii.PushColor(
                    ImGuiCol.PopupBg,
                    new Vector4(
                        0.035f,
                        0.04f,
                        0.075f,
                        1f))
                .Push(
                    ImGuiCol.Border,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.8f));

        if (!ImGui.BeginPopup(
                "Confirm action##profileFriendConfirmation",
                flags))
        {
            //
            // A normal popup can be dismissed by clicking outside it.
            // Clear the state so the profile is not left permanently dimmed.
            //

            profileFriendConfirmation =
                ProfileFriendConfirmationAction.None;

            return;
        }

        ImGui.TextColored(
            new Vector4(
                0.94f,
                0.92f,
                1f,
                1f),
            blocking
                ? "Block user?"
                : "Remove friend?");

        ImGui.Dummy(
            UiVec(0f, 7f));

        ImGui.Separator();

        ImGui.Dummy(
            UiVec(0f, 10f));

        if (blocking)
        {
            ImGui.TextWrapped(
                "Blocked users will not be able to communicate with you. " +
                "Messages from them in Watch Party chat will be hidden from you. " +
                "In parties that you host, they will automatically be kicked " +
                "from the room.");
        }
        else
        {
            ImGui.TextWrapped(
                $"Are you sure you want to remove " +
                $"{profilePopupFallbackName ?? "this user"} from your friends?");
        }

        var cancelSize =
            UiVec(100f, 34f);

        var confirmSize =
            new Vector2(
                blocking
                    ? Ui(116f)
                    : Ui(138f),
                Ui(34f));

        var buttonY =
            ImGui.GetWindowHeight() -
            confirmSize.Y -
            20f;

        var buttonRight =
            ImGui.GetWindowWidth() -
            22f;

        ImGui.SetCursorPos(
            new Vector2(
                buttonRight -
                confirmSize.X -
                cancelSize.X -
                Ui(10f),
                buttonY));

        using (ImRaii.Disabled(
                   profileFriendActionBusy))
        {
            if (DrawProfileActionButton(
                    "##cancelProfileFriendAction",
                    FontAwesomeIcon.Times,
                    "Cancel",
                    cancelSize,
                    new Vector4(
                        0.08f,
                        0.09f,
                        0.15f,
                        1f)))
            {
                profileFriendConfirmation =
                    ProfileFriendConfirmationAction.None;

                ImGui.CloseCurrentPopup();
            }

            ImGui.SetCursorPos(
                new Vector2(
                    buttonRight -
                    confirmSize.X,
                    buttonY));

            if (DrawProfileActionButton(
                    "##confirmProfileFriendAction",
                    blocking
                        ? FontAwesomeIcon.Ban
                        : FontAwesomeIcon.UserMinus,
                    blocking
                        ? "Block user"
                        : "Remove friend",
                    confirmSize,
                    Danger))
            {
                ExecuteProfileFriendAction();
            }
        }

        ImGui.EndPopup();
    }

    private void ExecuteProfileFriendAction()
    {
        if (profileFriendActionBusy ||
            CurrentSession is not { } session ||
            string.IsNullOrWhiteSpace(
                profilePopupAccountId) ||
            profileFriendConfirmation ==
            ProfileFriendConfirmationAction.None)
        {
            return;
        }

        var action =
            profileFriendConfirmation;

        var token =
            session.Token;

        var accountId =
            profilePopupAccountId;

        profileFriendActionBusy =
            true;

        ImGui.CloseCurrentPopup();

        CloseProfilePopup();

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var succeeded =
                        action ==
                        ProfileFriendConfirmationAction.BlockUser
                            ? await friendsClient
                                .BlockAsync(
                                    token,
                                    accountId)
                                .ConfigureAwait(false)
                            : await friendsClient
                                .RemoveFriendAsync(
                                    token,
                                    accountId)
                                .ConfigureAwait(false);

                    if (!succeeded)
                    {
                        AepLog.Warning(
                            action ==
                            ProfileFriendConfirmationAction.BlockUser
                                ? "[Profile] Failed to block user."
                                : "[Profile] Failed to remove friend.");
                    }

                    friendsDirty =
                        true;
                }
                finally
                {
                    profileFriendActionBusy =
                        false;

                    profileFriendConfirmation =
                        ProfileFriendConfirmationAction.None;
                }
            });
    }

    private void DrawProfilePopupHeader()
    {
        ImGui.TextColored(
            new Vector4(
                0.86f,
                0.82f,
                1f,
                1f),
            "Profile");

        var closeSize =
            UiVec(34f, 28f);

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            ImGui.GetWindowWidth() -
            closeSize.X -
            22f);

        using var closeColors =
            ImRaii.PushColor(
                    ImGuiCol.Button,
                    Vector4.Zero)
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(
                        0.18f,
                        0.13f,
                        0.29f,
                        1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(
                        0.28f,
                        0.18f,
                        0.44f,
                        1f));

        if (ImGui.Button(
                "×##closeProfile",
                closeSize))
        {
            CloseProfilePopup();
        }
    }

    private static void DrawProfileLoadingState()
    {
        var available =
            ImGui.GetContentRegionAvail();

        ImGui.SetCursorPosY(
            ImGui.GetCursorPosY() +
            Math.Max(
                0f,
                available.Y * 0.35f));

        var text =
            "Loading profile...";

        ImGui.SetCursorPosX(
            Math.Max(
                ImGui.GetCursorPosX(),
                (
                    ImGui.GetWindowWidth() -
                    ImGui.CalcTextSize(text).X
                ) * 0.5f));

        ImGui.TextColored(
            MutedText,
            text);
    }

    private void DrawProfileUnavailableState()
    {
        var available =
            ImGui.GetContentRegionAvail();

        ImGui.SetCursorPosY(
            ImGui.GetCursorPosY() +
            Math.Max(
                0f,
                available.Y * 0.35f));

        var text =
            $"{profilePopupFallbackName ?? "This user"}'s profile isn't available.";

        ImGui.SetCursorPosX(
            Math.Max(
                ImGui.GetCursorPosX(),
                (
                    ImGui.GetWindowWidth() -
                    ImGui.CalcTextSize(text).X
                ) * 0.5f));

        ImGui.TextColored(
            MutedText,
            text);
    }

    private void DrawProfileIdentity(
        AccountProfileDto profile)
    {
        var friend =
            friends.FirstOrDefault(
                candidate =>
                    string.Equals(
                        candidate.AccountId,
                        profilePopupAccountId,
                        StringComparison.Ordinal));

        var start =
            ImGui.GetCursorScreenPos();

        var avatarSize = Ui(112f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                start.X + Ui(4f),
                start.Y + Ui(2f)));

        DrawAvatarChip(
            profile.AvatarIcon,
            profile.AvatarColorHex,
            avatarSize,
            profile.AvatarImageUrl);

        //
        // Add the thin purple profile ring from the mockup.
        //

        ImGui.GetWindowDrawList()
            .AddCircle(
                new Vector2(
                    start.X +
                    Ui(4f) +
                    avatarSize * 0.5f,

                    start.Y +
                    Ui(2f) +
                    avatarSize * 0.5f),
                avatarSize * 0.5f -
                1f,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.9f)),
                64,
                2f);

        DrawProfileStatusBubble(
                    profile.StatusMessage,
            start,
            avatarSize);

        var detailsX =
                    start.X +
            avatarSize +
            28f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                detailsX,
                start.Y + Ui(2f)));

        ImGui.BeginGroup();

        SetUiFontScale(
            1.35f);

        ImGui.TextColored(
            new Vector4(
                0.94f,
                0.94f,
                1f,
                1f),
            profile.DisplayName);

        SetUiFontScale(
            1f);

        if (!string.IsNullOrWhiteSpace(profile.CharacterName))
        {
            ImGui.Dummy(
                UiVec(0f, 2f));

            var characterIdentity = string.IsNullOrWhiteSpace(profile.CharacterWorld)
                ? profile.CharacterName
                : $"{profile.CharacterName}  •  {profile.CharacterWorld}";

            ImGui.TextColored(
                MutedText,
                characterIdentity);
        }

        ImGui.Dummy(
            UiVec(0f, 3f));

        DrawProfilePresence(
            friend);

        if (profile.FriendsSinceUnix is { } friendsSince)
        {
            ImGui.Dummy(
                UiVec(0f, 7f));

            var since =
                DateTimeOffset
                    .FromUnixTimeSeconds(
                        friendsSince)
                    .LocalDateTime;

            ImGui.TextColored(
                MutedText,
                $"Friends since {since:MMM d, yyyy}");
        }

        ImGui.EndGroup();

        DrawProfilePrimaryActions(
            friend);

        ImGui.SetCursorScreenPos(
            new Vector2(
                start.X,
                start.Y +
                avatarSize +
                Ui(28f)));

        DrawProfileAboutCard(
            profile);

        ImGui.Dummy(
            UiVec(0f, 14f));

        DrawProfileDetailCards(
            friend);
    }

    private void DrawProfilePresence(
     FriendDto? friend)
    {
        var viewingOwnProfile =
            CurrentSession is { } session &&
            string.Equals(
                session.AccountId,
                profilePopupAccountId,
                StringComparison.Ordinal);

        //
        // Opening your own preview requires a current authenticated
        // session, so the local account is necessarily online.
        //

        var online =
            viewingOwnProfile ||
            friend?.Online == true;

        var statusColor =
            online
                ? new Vector4(
                    0.15f,
                    0.9f,
                    0.42f,
                    1f)
                : MutedText;

        ImGui.TextColored(
            statusColor,
            online
                ? "● Online"
                : "● Offline");

        if (friend?.WatchingLabel is { Length: > 0 } activity)
        {
            ImGui.SameLine();

            ImGui.TextColored(
                MutedText,
                $"— {activity}");
        }
    }

    private static void DrawProfileStatusBubble(
    string? status,
    Vector2 avatarOrigin,
    float avatarSize)
    {
        if (string.IsNullOrWhiteSpace(
                status))
        {
            return;
        }

        var trimmedStatus =
            status.Trim();

        //
        // Keep very long statuses compact in the profile header.
        // The complete text remains available in the tooltip.
        //

        var displayedStatus =
            trimmedStatus.Length > 32
                ? $"{trimmedStatus[..31]}…"
                : trimmedStatus;

        var textSize =
            ImGui.CalcTextSize(
                displayedStatus);

        var bubbleSize =
            new Vector2(
                Math.Clamp(
                    textSize.X + Ui(24f),
                    Ui(90f),
                    Ui(235f)),
                Ui(32f));

        var bubblePosition =
       new Vector2(
           avatarOrigin.X +
           Ui(10f),

           avatarOrigin.Y +
           avatarSize -
           Ui(10f));

        var bubbleMaximum =
            bubblePosition +
            bubbleSize;

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            bubblePosition,
            bubbleMaximum,
            ImGui.GetColorU32(
                new Vector4(
                    0.11f,
                    0.075f,
                    0.19f,
                    0.98f)),
            16f);

        drawList.AddRect(
            bubblePosition,
            bubbleMaximum,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.75f)),
            16f,
            ImDrawFlags.RoundCornersAll,
            1f);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                bubblePosition.X +
                Ui(12f),

                bubblePosition.Y +
                (
                    bubbleSize.Y -
                    textSize.Y
                ) * 0.5f),
            ImGui.GetColorU32(
                new Vector4(
                    0.92f,
                    0.89f,
                    1f,
                    1f)),
            displayedStatus);

        //
        // Create an invisible hover target over the hand-drawn bubble.
        //

        ImGui.SetCursorScreenPos(
            bubblePosition);

        ImGui.InvisibleButton(
            "##profileStatusBubble",
            bubbleSize);

        if (ImGui.IsItemHovered() &&
            displayedStatus != trimmedStatus)
        {
            ImGui.SetTooltip(
                trimmedStatus);
        }
    }

    private void DrawProfilePrimaryActions(
      FriendDto? friend)
    {
        if (CurrentSession is not { } session ||
            friend is null)
        {
            return;
        }

        var buttonWidth = Ui(154f);

        var buttonHeight = Ui(38f);

        var right =
            ImGui.GetWindowPos().X +
            ImGui.GetWindowWidth() -
            26f;

        var top =
            ImGui.GetCursorScreenPos().Y -
            92f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                right - buttonWidth,
                top));

        if (DrawProfileActionButton(
                "##profileMessage",
                FontAwesomeIcon.Envelope,
                "Message",
                new Vector2(
                    buttonWidth,
                    buttonHeight),
                Accent))
        {
            StartOrOpenConversation(
                session,
                friend.AccountId,
                friend.DisplayName);

            CloseProfilePopup();
        }

        if (!friend.HostingJoinableWatchParty)
        {
            return;
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                right - buttonWidth,
                top +
                buttonHeight +
                Ui(10f)));

        if (DrawProfileActionButton(
                "##profileJoin",
                FontAwesomeIcon.Users,
                "Join Watch Party",
                new Vector2(
                    buttonWidth,
                    buttonHeight),
                new Vector4(
                    0.08f,
                    0.09f,
                    0.15f,
                    1f)))
        {
            OpenPlayerAndJoin(
                friend.DisplayName);

            CloseProfilePopup();
        }
    }

    private static bool DrawProfileActionButton(
    string id,
    FontAwesomeIcon icon,
    string text,
    Vector2 size,
    Vector4 background)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton(
            id,
            size);

        var hovered =
            ImGui.IsItemHovered();

        var held =
            ImGui.IsItemActive();

        var clicked =
            ImGui.IsItemClicked();

        var buttonColor =
            held
                ? AccentActive
                : hovered
                    ? AccentHover
                    : background;

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                buttonColor),
            8f);

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    hovered ? 0.72f : 0.32f)),
            8f,
            ImDrawFlags.RoundCornersAll,
            1f);

        var glyph =
            icon.ToIconString();

        Vector2 glyphSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            glyphSize =
                ImGui.CalcTextSize(
                    glyph);
        }

        var textSize =
            ImGui.CalcTextSize(
                text);

        const float gap =
            9f;

        var hasText =
            !string.IsNullOrWhiteSpace(
                text);

        var totalWidth =
            glyphSize.X +
            (hasText
                ? gap + textSize.X
                : 0f);

        var contentX =
            origin.X +
            (size.X - totalWidth) * 0.5f;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    contentX,
                    origin.Y +
                    (size.Y - glyphSize.Y) * 0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                glyph);
        }

        if (hasText)
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    contentX +
                    glyphSize.X +
                    gap,
                    origin.Y +
                    (size.Y - textSize.Y) * 0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                text);
        }

        return clicked;
    }

    private static void DrawProfileAboutCard(
        AccountProfileDto profile)
    {
        var cardWidth =
            ImGui.GetContentRegionAvail().X;

        var cardHeight =
            94f;

        using var childStyle =
            ImRaii.PushStyle(
                    ImGuiStyleVar.ChildRounding,
                    9f)
                .Push(
                    ImGuiStyleVar.ChildBorderSize,
                    1f)
                .Push(
                    ImGuiStyleVar.WindowPadding,
                    UiVec(16f, 13f));

        using var childColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
new Vector4(
    0.055f,
    0.06f,
    0.10f,
    1f))
                .Push(
                    ImGuiCol.Border,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.52f));

        if (ImGui.BeginChild(
                "ProfileAboutCard",
                new Vector2(
                    cardWidth,
                    cardHeight),
                true,
                ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.TextColored(
                new Vector4(
                    0.88f,
                    0.86f,
                    0.98f,
                    1f),
                "About");

            ImGui.Dummy(
                UiVec(0f, 5f));

            if (profile.Bio is { Length: > 0 } bio)
            {
                ImGui.TextWrapped(
                    bio);
            }
            else
            {
                ImGui.TextColored(
                    MutedText,
                    "No bio added.");
            }
        }

        ImGui.EndChild();
    }

    private void DrawProfileDetailCards(
    FriendDto? friend)
    {
        const float gap =
            12f;

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var cardWidth =
            (
                availableWidth -
                gap
            ) * 0.5f;

        var cardHeight =
            250f;

        DrawProfileCurrentRoomCard(
            friend,
            cardWidth,
            cardHeight);

        ImGui.SameLine(
            0f,
            gap);

        DrawProfileRecentActivityCard(
            cardWidth,
            cardHeight);
    }

    private void DrawProfileCurrentRoomCard(
        FriendDto? friend,
        float width,
        float height)
    {
        using var cardStyle =
            ImRaii.PushStyle(
                    ImGuiStyleVar.ChildRounding,
                    9f)
                .Push(
                    ImGuiStyleVar.ChildBorderSize,
                    1f)
                .Push(
                    ImGuiStyleVar.WindowPadding,
                    UiVec(14f, 12f));

        using var cardColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
new Vector4(
    0.055f,
    0.06f,
    0.10f,
    1f))
                .Push(
                    ImGuiCol.Border,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.52f));

        if (ImGui.BeginChild(
                "##profileCurrentRoom",
                new Vector2(
                    width,
                    height),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(
                new Vector4(
                    0.88f,
                    0.86f,
                    0.98f,
                    1f),
                "Current room");

            if (profileDetailsLoading)
            {
                ImGui.Dummy(
                    UiVec(0f, 16f));

                ImGui.TextColored(
                    MutedText,
                    "Loading activity...");
            }
            else if (profilePopupRoom is { } room)
            {
                DrawProfileRoomContents(
                    room);
            }
            else if (profilePopupLive is { } live)
            {
                DrawProfileLiveContents(
                    live);
            }
            else if (friend?.WatchingLabel is { Length: > 0 } presence)
            {
                ImGui.Dummy(
                    UiVec(0f, 14f));

                ImGui.TextWrapped(
                    presence);
            }
            else
            {
                ImGui.Dummy(
                    UiVec(0f, 16f));

                ImGui.TextColored(
                    MutedText,
                    "Not currently in a room.");
            }
        }

        ImGui.EndChild();
    }

    private void DrawProfileRoomContents(
     RoomDirectoryDto room)
    {
        EnsurePartyDirectoryMediaLoaded(
            room);

        var media =
            GetPartyDirectoryMedia(
                room);

        TryReadWatchPartyMetadata(
            room,
            out _,
            out var adultOnly,
            out var serverName,
            out var description);

        var origin =
            ImGui.GetCursorScreenPos() +
            UiVec(0f, 8f);

        var drawList =
            ImGui.GetWindowDrawList();

        var contentWidth =
            ImGui.GetContentRegionAvail().X;

        var thumbnailWidth = Ui(112f);

        var thumbnailHeight = Ui(76f);

        var thumbnailOrigin =
            new Vector2(
                origin.X,
                origin.Y + Ui(31f));

        var thumbnailSize =
            new Vector2(
                thumbnailWidth,
                thumbnailHeight);

        //
        // Category medallion in the card's top-right corner.
        //

        var categoryRadius = Ui(17f);

        var categoryCenter =
            new Vector2(
                origin.X +
                contentWidth -
                categoryRadius,

                origin.Y +
                categoryRadius);

        drawList.AddCircleFilled(
            categoryCenter,
            categoryRadius,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.20f)),
            32);

        drawList.AddCircle(
            categoryCenter,
            categoryRadius,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.72f)),
            32,
            1f);

        var categoryGlyph =
            WatchPartyCategoryIcon(
                    room)
                .ToIconString();

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            var glyphSize =
                ImGui.CalcTextSize(
                    categoryGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                categoryCenter -
                glyphSize * 0.5f,
                ImGui.GetColorU32(
                    new Vector4(
                        0.92f,
                        0.88f,
                        1f,
                        1f)),
                categoryGlyph);
        }

        //
        // Visibility and hosting state.
        //

        var visibilityColor =
            WatchPartyVisibilityColor(
                room.Kind);

        drawList.AddCircleFilled(
            new Vector2(
                origin.X + Ui(5f),
                origin.Y + Ui(10f)),
            5f,
            ImGui.GetColorU32(
                visibilityColor),
            16);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X + Ui(15f),
                origin.Y + Ui(2f)),
            ImGui.GetColorU32(
                visibilityColor),
            WatchPartyVisibilityText(
                room.Kind).ToUpperInvariant());

        var visibilityTextWidth =
            ImGui.CalcTextSize(
                WatchPartyVisibilityText(
                    room.Kind).ToUpperInvariant()).X;

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X +
                Ui(22f) +
                visibilityTextWidth,
                origin.Y + Ui(2f)),
            ImGui.GetColorU32(
                Good),
            "Hosting");

        //
        // Media thumbnail.
        //

        var thumbnail =
            room.Kind != RoomKind.Locked &&
            !string.IsNullOrWhiteSpace(
                media.ThumbnailUrl)
                ? thumbnails.Get(
                    media.ThumbnailUrl)
                : null;

        thumbnail ??=
            homeHero;

        if (thumbnail is not null)
        {
            var (uv0, uv1) =
                CoverUvs(
                    thumbnail.Width,
                    thumbnail.Height,
                    thumbnailSize.X,
                    thumbnailSize.Y);

            drawList.AddImageRounded(
                thumbnail.Handle,
                thumbnailOrigin,
                thumbnailOrigin +
                thumbnailSize,
                uv0,
                uv1,
                uint.MaxValue,
                7f);
        }
        else
        {
            drawList.AddRectFilled(
                thumbnailOrigin,
                thumbnailOrigin +
                thumbnailSize,
                ImGui.GetColorU32(
                    new Vector4(
                        0.075f,
                        0.08f,
                        0.14f,
                        1f)),
                7f);
        }

        drawList.AddRect(
            thumbnailOrigin,
            thumbnailOrigin +
            thumbnailSize,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.38f)),
            7f,
            ImDrawFlags.RoundCornersAll,
            1f);

        //
        // Room information beside the thumbnail.
        //

        var informationX =
            thumbnailOrigin.X +
            thumbnailWidth +
            13f;

        var informationWidth =
            Math.Max(
                70f,
                origin.X +
                contentWidth -
                informationX);

        var roomName =
            $"{room.HostDisplayName}'s Watch Party";

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                informationX,
                thumbnailOrigin.Y),
            ImGui.GetColorU32(
                new Vector4(
                    0.94f,
                    0.93f,
                    1f,
                    1f)),
            PartyDirectoryTrimToWidth(
                roomName,
                informationWidth));

        var location =
            WatchPartyLocation(
                room);

        if (!string.IsNullOrWhiteSpace(
                location))
        {
            var locationGlyph =
                FontAwesomeIcon
                    .MapMarkerAlt
                    .ToIconString();

            Vector2 locationGlyphSize;

            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                locationGlyphSize =
                    ImGui.CalcTextSize(
                        locationGlyph);

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    new Vector2(
                        informationX,
                        thumbnailOrigin.Y + Ui(23f)),
                    ImGui.GetColorU32(
                        AccentHover),
                    locationGlyph);
            }

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    informationX +
                    locationGlyphSize.X +
                    Ui(6f),
                    thumbnailOrigin.Y + Ui(23f)),
                ImGui.GetColorU32(
                    MutedText),
                PartyDirectoryTrimToWidth(
                    location,
                    Math.Max(
                        40f,
                        informationWidth -
                        locationGlyphSize.X -
                        6f)));
        }

        if (!string.IsNullOrWhiteSpace(
                description))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    informationX,
                    thumbnailOrigin.Y + Ui(46f)),
                ImGui.GetColorU32(
                    MutedText),
                PartyDirectoryTrimToWidth(
                    description,
                    informationWidth));
        }

        //
        // Media title and viewer count beneath the thumbnail.
        //

        var mediaTitle =
            room.Kind == RoomKind.Locked
                ? "Content hidden"
                : media.Title;

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                thumbnailOrigin.X,
                thumbnailOrigin.Y +
                thumbnailHeight +
                Ui(9f)),
            ImGui.GetColorU32(
                new Vector4(
                    0.91f,
                    0.89f,
                    1f,
                    1f)),
            PartyDirectoryTrimToWidth(
                mediaTitle,
                contentWidth - 110f));

        var viewerGlyph =
            FontAwesomeIcon.Users
                .ToIconString();

        Vector2 viewerGlyphSize;

        using (ImRaii.PushFont(
                   UiBuilder.IconFont))
        {
            viewerGlyphSize =
                ImGui.CalcTextSize(
                    viewerGlyph);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    thumbnailOrigin.X,
                    thumbnailOrigin.Y +
                    thumbnailHeight +
                    Ui(32f)),
                ImGui.GetColorU32(
                    MutedText),
                viewerGlyph);
        }

        var totalWatching =
            room.ViewerCount + 1;

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                thumbnailOrigin.X +
                viewerGlyphSize.X +
                Ui(6f),
                thumbnailOrigin.Y +
                thumbnailHeight +
                Ui(32f)),
            ImGui.GetColorU32(
                MutedText),
            totalWatching == 1
                ? "1 watching"
                : $"{totalWatching} watching");

        //
        // Reuse the Party Directory tag renderer and metadata parser.
        //

        var tagsY =
            thumbnailOrigin.Y +
            thumbnailHeight +
            57f;

        var tagX =
            thumbnailOrigin.X;

        tagX +=
            DrawWatchPartyTag(
                drawList,
                new Vector2(
                    tagX,
                    tagsY),
                WatchPartyVisibilityIcon(
                    room.Kind),
                WatchPartyVisibilityText(
                    room.Kind),
                WatchPartyVisibilityColor(
                    room.Kind)) +
            6f;

        tagX +=
            DrawWatchPartyTag(
                drawList,
                new Vector2(
                    tagX,
                    tagsY),
                WatchPartyCategoryIcon(
                    room),
                WatchPartyCategoryText(
                    room),
                AccentHover) +
            6f;

        if (!string.IsNullOrWhiteSpace(
                serverName))
        {
            tagX +=
                DrawWatchPartyTag(
                    drawList,
                    new Vector2(
                        tagX,
                        tagsY),
                    FontAwesomeIcon.Server,
                    serverName.ToUpperInvariant(),
                    new Vector4(
                        0.35f,
                        0.65f,
                        1f,
                        1f)) +
                6f;
        }

        if (adultOnly)
        {
            DrawWatchPartyTag(
                drawList,
                new Vector2(
                    tagX,
                    tagsY),
                FontAwesomeIcon.ExclamationTriangle,
                "18+",
                new Vector4(
                    1f,
                    0.28f,
                    0.32f,
                    1f));
        }

        //
        // Join remains hidden while previewing your own profile.
        //

        var viewingOwnProfile =
            CurrentSession is { } session &&
            string.Equals(
                session.AccountId,
                profilePopupAccountId,
                StringComparison.Ordinal);

        if (!viewingOwnProfile)
        {
            var buttonSize =
                UiVec(108f, 30f);

            ImGui.SetCursorScreenPos(
                new Vector2(
                    origin.X +
                    contentWidth -
                    buttonSize.X,
                    thumbnailOrigin.Y +
                    thumbnailHeight +
                    Ui(24f)));

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
                        "Join Room##profileRoomJoin",
                        buttonSize))
                {
                    CloseProfilePopup();

                    JoinPartyDirectoryRoom(
                        room);
                }
            }
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                origin.X,
                tagsY + Ui(27f)));
    }

    private static void DrawProfileLiveContents(
        LiveFriendDto live)
    {
        ImGui.Dummy(
            UiVec(0f, 9f));

        ImGui.TextColored(
            new Vector4(
                0.15f,
                0.9f,
                0.42f,
                1f),
            "● LIVE NOW");

        ImGui.Dummy(
            UiVec(0f, 7f));

        ImGui.TextUnformatted(
            $"{live.DisplayName}'s live stream");

        ImGui.Dummy(
            UiVec(0f, 4f));

        var elapsed =
            DateTimeOffset.UtcNow -
            DateTimeOffset.FromUnixTimeSeconds(
                live.StartedAtUnix);

        if (elapsed < TimeSpan.Zero)
        {
            elapsed =
                TimeSpan.Zero;
        }

        ImGui.TextColored(
            MutedText,
            $"Live for {FormatProfileDuration(elapsed)}");
    }

    private void DrawProfileRecentActivityCard(
        float width,
        float height)
    {
        using var cardStyle =
            ImRaii.PushStyle(
                    ImGuiStyleVar.ChildRounding,
                    9f)
                .Push(
                    ImGuiStyleVar.ChildBorderSize,
                    1f)
                .Push(
                    ImGuiStyleVar.WindowPadding,
                    UiVec(14f, 12f));

        using var cardColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
new Vector4(
    0.055f,
    0.06f,
    0.10f,
    1f))
                .Push(
                    ImGuiCol.Border,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.52f));

        if (ImGui.BeginChild(
                "##profileRecentActivity",
                new Vector2(
                    width,
                    height),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(
                new Vector4(
                    0.88f,
                    0.86f,
                    0.98f,
                    1f),
                "Recent activity");

            ImGui.Dummy(
                UiVec(0f, 7f));

            if (profileDetailsLoading)
            {
                ImGui.TextColored(
                    MutedText,
                    "Loading activity...");
            }
            else if (profilePopupRecentActivity.Length == 0)
            {
                ImGui.TextColored(
                    MutedText,
                    "No recent activity found.");
            }
            else
            {
                for (var index = 0;
      index < profilePopupRecentActivity.Length;
      index++)
                {
                    DrawProfileActivityEntry(
                        profilePopupRecentActivity[index],
                        index ==
                        profilePopupRecentActivity.Length - 1);
                }
            }
        }

        ImGui.EndChild();
    }

    private void DrawProfileActivityEntry(
    ActivityEventDto item,
    bool isLast)
    {
        var entryHeight = Ui(48f);

        var nodeSize = Ui(30f);

        var origin =
            ImGui.GetCursorScreenPos();

        var nodeCenter =
            new Vector2(
                origin.X +
                nodeSize * 0.5f,

                origin.Y +
                nodeSize * 0.5f);

        var drawList =
            ImGui.GetWindowDrawList();

        //
        // Continue the timeline rail between activity entries.
        //

        if (!isLast)
        {
            drawList.AddLine(
                new Vector2(
                    nodeCenter.X,
                    nodeCenter.Y +
                    nodeSize * 0.5f),

                new Vector2(
                    nodeCenter.X,
                    origin.Y +
                    entryHeight),

                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.45f)),
                2f);
        }

        if (item.Type ==
            "StartedWatching")
        {
            //
            // Circular activity node with a play triangle.
            //

            drawList.AddCircleFilled(
                nodeCenter,
                nodeSize * 0.5f,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.22f)),
                24);

            var triangleCenter =
                nodeCenter +
                new Vector2(
                    1f,
                    0f);

            drawList.AddTriangleFilled(
                new Vector2(
                    triangleCenter.X - Ui(4f),
                    triangleCenter.Y - Ui(6f)),

                new Vector2(
                    triangleCenter.X - Ui(4f),
                    triangleCenter.Y + Ui(6f)),

                new Vector2(
                    triangleCenter.X + Ui(6f),
                    triangleCenter.Y),

                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        1f)));
        }
        else if (item.Type ==
           "JoinedWatchAlong")
        {
            drawList.AddCircleFilled(
                nodeCenter,
                nodeSize * 0.5f,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.22f)),
                24);

            var joinedGlyph =
                FontAwesomeIcon
                    .UserFriends
                    .ToIconString();

            using (ImRaii.PushFont(
                       UiBuilder.IconFont))
            {
                SetUiFontScale(
                    0.72f);

                var glyphSize =
                    ImGui.CalcTextSize(
                        joinedGlyph);

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    nodeCenter -
                    glyphSize * 0.5f,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            1f)),
                    joinedGlyph);

                SetUiFontScale(
                    1f);
            }
        }
        else
        {
            drawList.AddCircleFilled(
                nodeCenter,
                5f,
                ImGui.GetColorU32(
                    Accent),
                16);
        }

        var textX =
            origin.X +
            nodeSize +
            12f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                textX,
                origin.Y));

        ImGui.TextUnformatted(
            ProfileActivityLabel(
                item));

        ImGui.SetCursorScreenPos(
            new Vector2(
                textX,
                origin.Y + Ui(19f)));

        ImGui.TextColored(
            MutedText,
            FormatProfileRelativeTime(
                item.CreatedAtUnix));

        ImGui.SetCursorScreenPos(
            new Vector2(
                origin.X,
                origin.Y +
                entryHeight));
    }

    private static string ProfileActivityLabel(
    ActivityEventDto item)
    {
        return item.Type switch
        {
            "StartedWatching"
                when item.Metadata is { Length: > 0 } title =>
                    $"Started watching {title}",

            "StartedWatching" =>
                "Started watching",

            "JoinedWatchAlong"
                when item.Metadata is { Length: > 0 } host =>
                    $"Joined {host}'s Watch Party",

            "JoinedWatchAlong" =>
                "Joined a Watch Party",

            "FriendAccepted" =>
                "Accepted a friend request",

            "VenueSaved"
                when item.Metadata is { Length: > 0 } venue =>
                    $"Saved {venue}",

            "VenueSaved" =>
                "Saved a venue",

            "WentLive" =>
                "Went live",

            _ =>
                item.Type
        };
    }

    private static string FormatProfileRelativeTime(
        long timestampUnix)
    {
        var elapsed =
            DateTimeOffset.UtcNow -
            DateTimeOffset.FromUnixTimeSeconds(
                timestampUnix);

        if (elapsed < TimeSpan.Zero)
        {
            elapsed =
                TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1d)
        {
            return "Just now";
        }

        if (elapsed.TotalHours < 1d)
        {
            var minutes =
                Math.Max(
                    1,
                    (int)elapsed.TotalMinutes);

            return minutes == 1
                ? "1 minute ago"
                : $"{minutes} minutes ago";
        }

        if (elapsed.TotalDays < 1d)
        {
            var hours =
                Math.Max(
                    1,
                    (int)elapsed.TotalHours);

            return hours == 1
                ? "1 hour ago"
                : $"{hours} hours ago";
        }

        if (elapsed.TotalDays < 7d)
        {
            var days =
                Math.Max(
                    1,
                    (int)elapsed.TotalDays);

            return days == 1
                ? "Yesterday"
                : $"{days} days ago";
        }

        return DateTimeOffset
            .FromUnixTimeSeconds(
                timestampUnix)
            .LocalDateTime
            .ToString(
                "MMM d, yyyy");
    }

    private static string FormatProfileDuration(
        TimeSpan duration)
    {
        if (duration.TotalDays >= 1d)
        {
            return
                $"{(int)duration.TotalDays}d {duration.Hours}h";
        }

        if (duration.TotalHours >= 1d)
        {
            return
                $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1d)
        {
            return
                $"{(int)duration.TotalMinutes}m";
        }

        return
            $"{Math.Max(0, duration.Seconds)}s";
    }

    private void DrawProfilePopupFooter()
    {
        var closeSize =
            UiVec(104f, 36f);

        var menuSize =
            UiVec(44f, 36f);

        var windowPosition =
            ImGui.GetWindowPos();

        var windowBottom =
            windowPosition.Y +
            ImGui.GetWindowHeight();

        var right =
            windowPosition.X +
            ImGui.GetWindowWidth() -
            26f;

        var footerY =
            windowBottom -
            closeSize.Y -
            20f;

        var friend =
            friends.FirstOrDefault(
                candidate =>
                    string.Equals(
                        candidate.AccountId,
                        profilePopupAccountId,
                        StringComparison.Ordinal));

        if (CurrentSession is { } session &&
            friend is not null)
        {
            ImGui.SetCursorScreenPos(
                new Vector2(
                    right -
                    closeSize.X -
                    menuSize.X -
                    Ui(10f),
                    footerY));

            if (DrawProfileActionButton(
                    "##profileFriendMenu",
                    FontAwesomeIcon.EllipsisH,
                    string.Empty,
                    menuSize,
                    new Vector4(
                        0.08f,
                        0.09f,
                        0.15f,
                        1f)))
            {
                ImGui.OpenPopup(
                    "##profileFriendActions");
            }

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.PopupRounding,
                       8f)
                       .Push(
                           ImGuiStyleVar.WindowPadding,
                           UiVec(8f, 8f)))
            using (ImRaii.PushColor(
                       ImGuiCol.PopupBg,
                       new Vector4(
                           0.045f,
                           0.05f,
                           0.085f,
                           1f))
                       .Push(
                           ImGuiCol.Border,
                           new Vector4(
                               Accent.X,
                               Accent.Y,
                               Accent.Z,
                               0.55f)))
            {
                if (ImGui.BeginPopup(
                        "##profileFriendActions"))
                {
                    if (ImGui.MenuItem(
          "Remove friend"))
                    {
                        profileFriendConfirmation =
                            ProfileFriendConfirmationAction.RemoveFriend;

                        profileFriendConfirmationPendingOpen =
                            true;
                    }

                    using (ImRaii.PushColor(
                               ImGuiCol.Text,
                               Danger))
                    {
                        if (ImGui.MenuItem(
         "Block user"))
                        {
                            profileFriendConfirmation =
                                ProfileFriendConfirmationAction.BlockUser;

                            profileFriendConfirmationPendingOpen =
                                true;
                        }
                    }

                    ImGui.EndPopup();
                }
            }
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                right -
                closeSize.X,
                footerY));

        if (DrawProfileActionButton(
                "##profileFooterClose",
                FontAwesomeIcon.Times,
                "Close",
                closeSize,
                new Vector4(
                    0.08f,
                    0.09f,
                    0.15f,
                    1f)))
        {
            CloseProfilePopup();
        }
    }
}
