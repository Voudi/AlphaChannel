using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Sign-in/link UI for the XIVAuth device flow (see Auth/SignInFlow.cs for the actual network
// orchestration - this file is purely presentation plus the small bit of state needed to poll it
// from Draw() each frame). Reachable from the Settings page's Account section.
internal sealed partial class MainWindow
{
    private enum SignInState
    {
        Idle,
        Requesting,
        AwaitingBrowser,
        Onboarding,
        Succeeded,
        Failed,
    }

    private static readonly string[] KnownRaces =
        ["Hyur", "Elezen", "Lalafell", "Miqo'te", "Roegadyn", "Au Ra", "Hrothgar", "Viera"];

    private SignInState signInState = SignInState.Idle;
    private string signInVerificationUri = string.Empty;
    private string signInUserCode = string.Empty;
    private string signInStatusMessage = string.Empty;
    private bool signInModalPending;
    private CancellationTokenSource? signInCts;
    private CharacterSession? pendingOnboardingSession;
    private readonly HashSet<string> onboardingRaces = [];
    private bool onboardingWantsToSeeLalafellContent = true;

    private LinkedCharacterDto[]? myLinkedCharacters;
    private string displayNameInput = string.Empty;
    private string? lastDisplayNameSyncedFor;
    private string? displayNameError;
    private bool inviteCodeRefreshing;
    private string onboardingNameInput = string.Empty;
    private string? onboardingNameError;
    private bool onboardingNameSubmitting;

    private string? lastProfileSyncedFor;
    private string? profileIconInput;
    private string profileColorInput = "#9966FA";
    private string? profileImageUrl;
    private string profileAvatarPathInput = string.Empty;
    private string? profileAvatarError;
    private bool profileAvatarBusy;

    private readonly FileDialogManager profileAvatarFileDialog =
        new();
    private string profileBioInput = string.Empty;
    private string profileStatusInput = string.Empty;
    private bool profileSaving;
    private string? profileError;

    private enum ProfileAvatarMode
    {
        Image,
        Icon,
    }

    private ProfileAvatarMode profileAvatarMode = ProfileAvatarMode.Icon;

    private void DrawAccountSettings()
    {
        if (CurrentSession is { } session)
        {
            // Sync once per session change so editing isn't reset every frame.
            if (lastDisplayNameSyncedFor != session.AccountId)
            {
                displayNameInput = session.DisplayName;
                lastDisplayNameSyncedFor = session.AccountId;
            }

            var displayNameValid =
                DisplayNameRules.IsValid(displayNameInput);

            // ---------------------------------------------------------
            // Account card
            // ---------------------------------------------------------

            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                10f))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(0.045f, 0.06f, 0.10f, 1f)))
            using (var accountCard = ImRaii.Child(
                "##accountSettingsCard",
                new Vector2(-1f, 238f),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (accountCard)
                {
                    // -------------------------------------------------
                    // Signed-in status
                    // -------------------------------------------------

                    ImGui.SetCursorPos(
                        new Vector2(16f, 14f));

                    ImGui.TextColored(
                        Good,
                        "SIGNED IN");

                    ImGui.SetCursorPos(
                        new Vector2(16f, 39f));

                    ImGui.TextColored(
                        Vector4.One,
                        session.DisplayName);

                    // -------------------------------------------------
                    // Username
                    // -------------------------------------------------

                    ImGui.SetCursorPos(
                        new Vector2(16f, 70f));

                    ImGui.SetWindowFontScale(0.80f);

                    ImGui.TextColored(
                        MutedText,
                        "Username");

                    ImGui.SetWindowFontScale(1f);

                    ImGui.SetCursorPos(
                        new Vector2(16f, 91f));

                    ImGui.SetNextItemWidth(
                        ImGui.GetWindowWidth() - 126f);

                    using (displayNameValid
                               ? default
                               : ImRaii.PushColor(
                                   ImGuiCol.Text,
                                   Danger))
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        8f)
                        .Push(
                            ImGuiStyleVar.FramePadding,
                            new Vector2(12f, 8f)))
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
                            "##displayName",
                            ref displayNameInput,
                            DisplayNameRules.MaxLength);
                    }

                    ImGui.SameLine(0f, 8f);

                    using (ImRaii.Disabled(
                        !displayNameValid ||
                        displayNameInput.Trim() == session.DisplayName))
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
                            "Save",
                            new Vector2(86f, 34f)))
                        {
                            var token =
                                session.Token;

                            var newName =
                                displayNameInput.Trim();

                            _ = Task.Run(async () =>
                            {
                                var outcome =
                                    await authClient
                                        .UpdateDisplayNameAsync(
                                            token,
                                            newName);

                                if (outcome.Account is { } updated)
                                {
                                    session.DisplayName =
                                        updated.DisplayName;

                                    onSessionChanged(
                                        session);

                                    displayNameError =
                                        null;
                                }
                                else
                                {
                                    displayNameError =
                                        outcome.NameTaken
                                            ? "That name's already taken - try another."
                                            : outcome.InvalidFormat
                                                ? "That name doesn't fit the rules below."
                                                : "Couldn't save that name.";
                                }
                            });
                        }
                    }

                    ImGui.SetCursorPos(
                        new Vector2(16f, 130f));

                    ImGui.SetWindowFontScale(0.76f);

                    ImGui.TextColored(
                        MutedText,
                        $"{DisplayNameRules.MinLength}-{DisplayNameRules.MaxLength} characters  •  letters, numbers, spaces, _ or -");

                    ImGui.SetWindowFontScale(1f);

                    // -------------------------------------------------
                    // Invite code
                    // -------------------------------------------------

                    ImGui.SetCursorPos(
                        new Vector2(16f, 158f));

                    ImGui.SetWindowFontScale(0.80f);

                    ImGui.TextColored(
                        MutedText,
                        "Invite code");

                    ImGui.SetWindowFontScale(1f);

                    var inviteCodeDisplay =
                        session.InviteCode;

                    ImGui.SetCursorPos(
                        new Vector2(16f, 179f));

                    ImGui.SetNextItemWidth(130f);

                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        8f)
                        .Push(
                            ImGuiStyleVar.FramePadding,
                            new Vector2(12f, 8f)))
                    using (ImRaii.PushColor(
                        ImGuiCol.FrameBg,
                        new Vector4(0.055f, 0.07f, 0.115f, 1f)))
                    {
                        ImGui.InputText(
                            "##inviteCode",
                            ref inviteCodeDisplay,
                            16,
                            ImGuiInputTextFlags.ReadOnly);
                    }

                    ImGui.SameLine(0f, 8f);

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
                            "Copy",
                            new Vector2(76f, 34f)))
                        {
                            ImGui.SetClipboardText(
                                session.InviteCode);
                        }
                    }

                    ImGui.SameLine(0f, 8f);

                    using (ImRaii.Disabled(
                        inviteCodeRefreshing))
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
                            inviteCodeRefreshing
                                ? "..."
                                : "Refresh",
                            new Vector2(82f, 34f)))
                        {
                            inviteCodeRefreshing =
                                true;

                            var token =
                                session.Token;

                            _ = Task.Run(async () =>
                            {
                                var summary =
                                    await authClient
                                        .GetMeAsync(token);

                                inviteCodeRefreshing =
                                    false;

                                if (summary is not null)
                                {
                                    session.InviteCode =
                                        summary.InviteCode;

                                    session.AvatarIcon =
                                        summary.AvatarIcon;

                                    session.AvatarColorHex =
                                        summary.AvatarColorHex;

                                    session.AvatarImageUrl =
                                        summary.AvatarImageUrl;

                                    session.Bio =
                                        summary.Bio;

                                    session.StatusMessage =
                                        summary.StatusMessage;

                                    onSessionChanged(
                                        session);
                                }
                            });
                        }
                    }
                }
            }

            // ---------------------------------------------------------
            // Errors / warnings
            // ---------------------------------------------------------

            if (session.DisplayName == session.Handle)
            {
                ImGui.Dummy(
                    new Vector2(0f, 7f));

                ImGui.TextColored(
                    Danger,
                    "Pick a username so friends can find and add you.");
            }

            if (displayNameError is { Length: > 0 } nameError)
            {
                ImGui.Dummy(
                    new Vector2(0f, 6f));

                ImGui.TextColored(
                    Danger,
                    nameError);
            }

            ImGui.Dummy(
                new Vector2(0f, 8f));

            ImGui.SetWindowFontScale(0.78f);

            ImGui.TextColored(
                MutedText,
                "Invite codes rotate automatically after they're redeemed.");

            ImGui.SetWindowFontScale(1f);

            ImGui.Dummy(
                new Vector2(0f, 10f));

            // ---------------------------------------------------------
            // Account actions
            // ---------------------------------------------------------

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
                    "Show linked characters",
                    new Vector2(160f, 34f)))
                {
                    var token =
                        session.Token;

                    _ = Task.Run(
                        async () =>
                            myLinkedCharacters =
                                await authClient
                                    .GetMyCharactersAsync(
                                        token));
                }
            }

            ImGui.SameLine(0f, 8f);

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(0.16f, 0.055f, 0.07f, 1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(0.22f, 0.07f, 0.09f, 1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(0.25f, 0.08f, 0.10f, 1f)))
            {
                if (ImGui.Button(
                    "Sign out",
                    new Vector2(94f, 34f)))
                {
                    _ = authClient.RevokeAsync(
                        session.Token);

                    onSessionChanged(
                        null);
                }
            }

            // ---------------------------------------------------------
            // Linked characters
            // ---------------------------------------------------------

            if (myLinkedCharacters is { } characters)
            {
                ImGui.Dummy(
                    new Vector2(0f, 10f));

                foreach (var character in characters)
                {
                    ImGui.TextColored(
                        MutedText,
                        $"{character.CharacterName} @ {character.World}" +
                        (character.IsPrimary
                            ? "  •  Primary"
                            : string.Empty));
                }
            }

            
        }
        else
        {
            // ---------------------------------------------------------
            // Signed-out state
            // ---------------------------------------------------------

            ImGui.TextColored(
                MutedText,
                "Sign in to use Friends, Messages, Activity, and Watch-along.");

            ImGui.Dummy(
                new Vector2(0f, 10f));

            var canSignIn =
                !string.IsNullOrEmpty(CurrentCharacterName) &&
                !string.IsNullOrEmpty(CurrentWorldName);

            using (ImRaii.Disabled(
                !canSignIn ||
                signInState is
                    SignInState.Requesting or
                    SignInState.AwaitingBrowser))
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
                    "Sign in with XIVAuth",
                    new Vector2(160f, 36f)))
                {
                    StartSignIn(
                        linkUsing: null);
                }
            }

            if (Plugin.Cfg.CharacterSessions.Values
                .FirstOrDefault() is { } existing)
            {
                ImGui.SameLine(0f, 8f);

                using (ImRaii.Disabled(
                    signInState is
                        SignInState.Requesting or
                        SignInState.AwaitingBrowser))
                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    8f))
                {
                    if (ImGui.Button(
                        $"Link to @{existing.Handle}",
                        new Vector2(150f, 36f)))
                    {
                        StartSignIn(
                            existing);
                    }
                }
            }
        }

        if (signInState == SignInState.Failed &&
            signInStatusMessage is { Length: > 0 } failure)
        {
            ImGui.Dummy(
                new Vector2(0f, 8f));

            ImGui.TextColored(
                Danger,
                failure);
        }
    }

    private void DrawProfileEditor(CharacterSession session)
    {
        if (lastProfileSyncedFor != session.AccountId)
        {
            profileIconInput = session.AvatarIcon;
            profileColorInput = session.AvatarColorHex;
            profileImageUrl = session.AvatarImageUrl;
            profileAvatarPathInput = string.Empty;
            profileAvatarError = null;
            profileBioInput = session.Bio ?? string.Empty;
            profileStatusInput = session.StatusMessage ?? string.Empty;

            profileAvatarMode =
                string.IsNullOrEmpty(session.AvatarImageUrl)
                    ? ProfileAvatarMode.Icon
                    : ProfileAvatarMode.Image;

            lastProfileSyncedFor = session.AccountId;
        }

        // =========================================================
        // PROFILE PICTURE
        // =========================================================

        var pictureCardHeight =
            profileAvatarMode == ProfileAvatarMode.Image
                ? 410f
                : 760f;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f))
            .Push(
                ImGuiCol.Border,
                BorderSubtle))
        using (var pictureCard = ImRaii.Child(
            "##profilePictureCard",
            new Vector2(-1f, pictureCardHeight),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (pictureCard)
            {
                // -----------------------------------------------------
                // Heading
                // -----------------------------------------------------

                ImGui.SetWindowFontScale(1.10f);

                ImGui.TextColored(
                    Vector4.One,
                    "Profile picture");

                ImGui.SetWindowFontScale(1f);

                ImGui.Dummy(new Vector2(0f, 3f));

                ImGui.TextColored(
                    MutedText,
                    "Choose an image or a styled icon.");

                ImGui.Dummy(new Vector2(0f, 14f));

                // -----------------------------------------------------
                // Image / Icon selector
                // -----------------------------------------------------

                const float selectorWidth = 132f;
                const float selectorHeight = 38f;

                DrawProfileModeButton(
                    ProfileAvatarMode.Image,
                    FontAwesomeIcon.Image,
                    "Image",
                    selectorWidth,
                    selectorHeight);

                ImGui.SameLine(0f, 10f);

                DrawProfileModeButton(
                    ProfileAvatarMode.Icon,
                    FontAwesomeIcon.Smile,
                    "Icon",
                    selectorWidth,
                    selectorHeight);

                ImGui.Dummy(new Vector2(0f, 16f));

                // -----------------------------------------------------
                // Divider
                // -----------------------------------------------------

                var dividerOrigin =
                    ImGui.GetCursorScreenPos();

                var dividerWidth =
                    ImGui.GetContentRegionAvail().X;

                ImGui.GetWindowDrawList()
                    .AddRectFilled(
                        dividerOrigin,
                        dividerOrigin +
                        new Vector2(dividerWidth, 1f),
                        ImGui.GetColorU32(BorderSubtle));

                ImGui.Dummy(
                    new Vector2(dividerWidth, 16f));

                // =====================================================
                // IMAGE MODE
                // =====================================================

                if (profileAvatarMode == ProfileAvatarMode.Image)
                {
                    DrawAvatarChip(
                        profileIconInput,
                        profileColorInput,
                        96,
                        profileImageUrl);

                    ImGui.SameLine(0f, 18f);

                    ImGui.BeginGroup();

                    ImGui.SetWindowFontScale(1.05f);

                    ImGui.TextColored(
                        Vector4.One,
                        "Current image");

                    ImGui.SetWindowFontScale(1f);

                    ImGui.Dummy(new Vector2(0f, 5f));

                    if (profileImageUrl is { Length: > 0 })
                    {
                        ImGui.TextColored(
                            MutedText,
                            "Custom profile image");

                        ImGui.Dummy(new Vector2(0f, 7f));

                        ImGui.TextColored(
                            Good,
                            "Custom picture active.");
                    }
                    else
                    {
                        ImGui.TextColored(
                            MutedText,
                            "No custom image selected.");

                        ImGui.Dummy(new Vector2(0f, 7f));

                        ImGui.TextColored(
                            MutedText,
                            "Upload an image below to use one.");
                    }

                    ImGui.EndGroup();

                    ImGui.Dummy(new Vector2(0f, 18f));

                    // -------------------------------------------------
                    // Image actions
                    // -------------------------------------------------

                    const float actionGap = 10f;
                    const float horizontalInset = 8f;

                    var fullActionWidth =
                        ImGui.GetContentRegionAvail().X;

                    var actionWidth =
                        (fullActionWidth -
                         (horizontalInset * 2f) -
                         (actionGap * 2f)) / 3f;

                    ImGui.SetCursorPosX(
                        ImGui.GetCursorPosX() +
                        horizontalInset);

                    using (ImRaii.Disabled(profileAvatarBusy))
                    {
                        if (DrawProfileActionButton(
                            FontAwesomeIcon.FolderOpen,
                            "Newest image",
                            "In Downloads",
                            Accent,
                            width: actionWidth))
                        {
                            var found =
                                FindImageInDownloads();

                            if (found is null)
                            {
                                profileAvatarError =
                                    "No image found in Downloads.";
                            }
                            else
                            {
                                profileAvatarPathInput =
                                    found;

                                UploadProfileAvatar(
                                    session,
                                    found);
                            }
                        }

                        ImGui.SameLine(0f, actionGap);

                        if (DrawProfileActionButton(
                            FontAwesomeIcon.Upload,
                            "Upload image",
                            "Choose a file",
                            Hex(0x38BDF8),
                            width: actionWidth))
                        {
                            profileAvatarError = null;

                            profileAvatarFileDialog.OpenFileDialog(
                                "Select Profile Picture",
                                ".png,.jpg,.jpeg,.webp",
                                (success, path) =>
                                {
                                    if (!success ||
                                        string.IsNullOrWhiteSpace(path))
                                    {
                                        return;
                                    }

                                    profileAvatarPathInput = path;

                                    UploadProfileAvatar(
                                        session,
                                        path);
                                });
                        }
                        ImGui.SameLine(0f, actionGap);

                        if (DrawProfileActionButton(
                            FontAwesomeIcon.Trash,
                            "Remove image",
                            "Revert to icon",
                            Hex(0xF87171),
                            disabled:
                                string.IsNullOrEmpty(profileImageUrl),
                            width: actionWidth))
                        {
                            ClearProfileAvatar(session);

                            profileAvatarMode =
                                ProfileAvatarMode.Icon;
                        }
                    }

                    profileAvatarFileDialog.Draw();

                    ImGui.Dummy(new Vector2(0f, 14f));

                    ImGui.SetWindowFontScale(0.78f);

                    ImGui.TextColored(
                        MutedText,
                        "Recommended: square PNG, JPG or WebP. Max 1 MB.");

                    ImGui.SetWindowFontScale(1f);

                    if (profileAvatarError is { Length: > 0 } avatarError)
                    {
                        ImGui.Dummy(new Vector2(0f, 7f));

                        ImGui.TextColored(
                            Danger,
                            avatarError);
                    }
                }

                // =====================================================
                // ICON MODE
                // =====================================================

                else
                {
                    DrawAvatarChip(
                        profileIconInput,
                        profileColorInput,
                        96,
                        null);

                    ImGui.SameLine(0f, 18f);

                    ImGui.BeginGroup();

                    ImGui.SetWindowFontScale(1.05f);

                    ImGui.TextColored(
                        Vector4.One,
                        "Styled icon");

                    ImGui.SetWindowFontScale(1f);

                    ImGui.Dummy(new Vector2(0f, 5f));

                    ImGui.TextColored(
                        MutedText,
                        "Choose an icon and colour for your avatar.");

                    if (profileImageUrl is { Length: > 0 })
                    {
                        ImGui.Dummy(new Vector2(0f, 7f));

                        ImGui.TextColored(
                            MutedText,
                            "Your custom image is currently active.");

                        ImGui.Dummy(new Vector2(0f, 8f));

                        using (ImRaii.Disabled(profileAvatarBusy))
                        using (ImRaii.PushStyle(
                            ImGuiStyleVar.FrameRounding,
                            7f))
                        using (ImRaii.PushColor(
                            ImGuiCol.Button,
                            new Vector4(
                                0.055f,
                                0.07f,
                                0.115f,
                                1f))
                            .Push(
                                ImGuiCol.ButtonHovered,
                                new Vector4(
                                    0.075f,
                                    0.095f,
                                    0.15f,
                                    1f)))
                        {
                            if (ImGui.Button(
                                "Use icon instead",
                                new Vector2(130f, 30f)))
                            {
                                ClearProfileAvatar(session);
                            }
                        }
                    }

                    ImGui.EndGroup();

                    ImGui.Dummy(new Vector2(0f, 18f));

                    // -------------------------------------------------
                    // Icon picker
                    // -------------------------------------------------

                    ImGui.TextColored(
                        Vector4.One,
                        "Icon");

                    ImGui.Dummy(new Vector2(0f, 7f));

                    using (ImRaii.PushStyle(
    ImGuiStyleVar.ChildRounding,
    8f)
    .Push(
        ImGuiStyleVar.WindowPadding,
        new Vector2(12f, 12f)))
                    using (ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        new Vector4(
                            0.035f,
                            0.045f,
                            0.075f,
                            1f))
                        .Push(
                            ImGuiCol.Border,
                            BorderSubtle))
                    using (var iconChild = ImRaii.Child(
                        "##profileIconPicker",
                        new Vector2(-1f, 178f),
                        true,
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse))
                    {
                        if (iconChild)
                        {
                            DrawIconPicker(
                                ref profileIconInput);
                        }
                    }

                    ImGui.Dummy(new Vector2(0f, 14f));

                    // -------------------------------------------------
                    // Color
                    // -------------------------------------------------

                    ImGui.TextColored(
                        Vector4.One,
                        "Color");

                    ImGui.Dummy(new Vector2(0f, 7f));

                    // Give the colour controls their own padded area.
                    using (ImRaii.PushStyle(
    ImGuiStyleVar.ChildRounding,
    8f)
    .Push(
        ImGuiStyleVar.WindowPadding,
        new Vector2(14f, 14f)))
                    using (ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        new Vector4(
                            0.035f,
                            0.045f,
                            0.075f,
                            1f))
                        .Push(
                            ImGuiCol.Border,
                            BorderSubtle))
                    using (var colorChild = ImRaii.Child(
                        "##profileColorPicker",
                        new Vector2(-1f, 78f),
                        true,
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse))
                    {
                        if (colorChild)
                        {
                            DrawColorPicker(
                                ref profileColorInput);
                        }
                    }
                }
            }
        }

        ImGui.Dummy(new Vector2(0f, 14f));

        // =========================================================
        // ABOUT YOU
        // =========================================================

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            new Vector4(0.045f, 0.06f, 0.10f, 1f))
            .Push(
                ImGuiCol.Border,
                BorderSubtle))
        using (var aboutCard = ImRaii.Child(
            "##profileAboutCard",
            new Vector2(-1f, 465f),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (aboutCard)
            {
                ImGui.SetWindowFontScale(1.10f);

                ImGui.TextColored(
                    Vector4.One,
                    "About you");

                ImGui.SetWindowFontScale(1f);

                ImGui.Dummy(new Vector2(0f, 2f));

                ImGui.TextColored(
                    MutedText,
                    "Let others know a bit about you.");

                ImGui.Dummy(new Vector2(0f, 10f));

                // -----------------------------------------------------
                // Status
                // -----------------------------------------------------

                ImGui.TextColored(
                    Vector4.One,
                    "Status");

                ImGui.SameLine();

                ImGui.SetWindowFontScale(0.76f);

                ImGui.TextColored(
                    MutedText,
                    "Short message shown beside your name.");

                ImGui.SetWindowFontScale(1f);

                ImGui.Dummy(new Vector2(0f, 5f));

                ImGui.SetNextItemWidth(-1f);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    8f)
                    .Push(
                        ImGuiStyleVar.FramePadding,
                        new Vector2(12f, 9f)))
                using (ImRaii.PushColor(
                    ImGuiCol.FrameBg,
                    new Vector4(
                        0.055f,
                        0.07f,
                        0.115f,
                        1f))
                    .Push(
                        ImGuiCol.FrameBgHovered,
                        new Vector4(
                            0.07f,
                            0.09f,
                            0.145f,
                            1f))
                    .Push(
                        ImGuiCol.FrameBgActive,
                        new Vector4(
                            0.07f,
                            0.09f,
                            0.145f,
                            1f)))
                {
                    ImGui.InputTextWithHint(
                        "##status",
                        "What are you up to?",
                        ref profileStatusInput,
                        64);
                }

                ImGui.SetWindowFontScale(0.72f);

                var statusCounter =
                    $"{profileStatusInput.Length} / 64";

                var statusCounterWidth =
                    ImGui.CalcTextSize(statusCounter).X;

                ImGui.SetCursorPosX(
                    ImGui.GetWindowWidth() -
                    statusCounterWidth -
                    22f);

                ImGui.TextColored(
                    MutedText,
                    statusCounter);

                ImGui.SetWindowFontScale(1f);

                ImGui.Dummy(new Vector2(0f, 9f));

                // -----------------------------------------------------
                // Bio
                // -----------------------------------------------------

                ImGui.TextColored(
                    Vector4.One,
                    "Bio");

                ImGui.SameLine();

                ImGui.SetWindowFontScale(0.76f);

                ImGui.TextColored(
                    MutedText,
                    "A little about yourself. Shown on your profile.");

                ImGui.SetWindowFontScale(1f);

                ImGui.Dummy(new Vector2(0f, 5f));

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    8f)
                    .Push(
                        ImGuiStyleVar.FramePadding,
                        new Vector2(12f, 10f)))
                using (ImRaii.PushColor(
                    ImGuiCol.FrameBg,
                    new Vector4(
                        0.055f,
                        0.07f,
                        0.115f,
                        1f))
                    .Push(
                        ImGuiCol.FrameBgHovered,
                        new Vector4(
                            0.07f,
                            0.09f,
                            0.145f,
                            1f))
                    .Push(
                        ImGuiCol.FrameBgActive,
                        new Vector4(
                            0.07f,
                            0.09f,
                            0.145f,
                            1f)))
                {
                    ImGui.InputTextMultiline(
                        "##bio",
                        ref profileBioInput,
                        160,
                        new Vector2(-1f, 145f));
                }

                ImGui.SetWindowFontScale(0.72f);

                var bioCounter =
                    $"{profileBioInput.Length} / 160";

                var bioCounterWidth =
                    ImGui.CalcTextSize(bioCounter).X;

                ImGui.SetCursorPosX(
                    ImGui.GetWindowWidth() -
                    bioCounterWidth -
                    22f);

                ImGui.TextColored(
                    MutedText,
                    bioCounter);

                ImGui.SetWindowFontScale(1f);
            }
        }

        ImGui.Dummy(new Vector2(0f, 14f));

        // =========================================================
        // SAVE PROFILE
        // =========================================================

        const float saveWidth = 138f;

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() +
            ImGui.GetContentRegionAvail().X -
            saveWidth);

        using (ImRaii.Disabled(profileSaving))
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
                profileSaving
                    ? "Saving..."
                    : "Save profile",
                new Vector2(
                    saveWidth,
                    40f)))
            {
                SaveProfile(session);
            }
        }

        if (profileError is { Length: > 0 } error)
        {
            ImGui.Dummy(new Vector2(0f, 8f));

            ImGui.TextColored(
                Danger,
                error);
        }
    }

    private void DrawProfileModeButton(
    ProfileAvatarMode mode,
    FontAwesomeIcon icon,
    string label,
    float width,
    float height)
    {
        var selected =
            profileAvatarMode == mode;

        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(width, height);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
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
                    : new Vector4(
                        0.055f,
                        0.07f,
                        0.11f,
                        1f))
            .Push(
                ImGuiCol.ButtonActive,
                selected
                    ? AccentActive
                    : new Vector4(
                        0.065f,
                        0.08f,
                        0.125f,
                        1f))
            .Push(
                ImGuiCol.Text,
                new Vector4(0f, 0f, 0f, 0f)))
        {
            if (ImGui.Button(
                $"##profileMode_{mode}",
                size))
            {
                profileAvatarMode = mode;
            }
        }

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                selected
                    ? Accent
                    : BorderSubtle),
            8f,
            ImDrawFlags.None,
            selected
                ? 1.5f
                : 1f);

        var glyph =
            icon.ToIconString();

        Vector2 glyphSize;

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            glyphSize =
                ImGui.CalcTextSize(glyph);
        }

        var labelSize =
            ImGui.CalcTextSize(label);

        const float gap = 8f;

        var totalWidth =
            glyphSize.X +
            gap +
            labelSize.X;

        var contentX =
            origin.X +
            (size.X - totalWidth) * 0.5f;

        var glyphY =
            origin.Y +
            (size.Y - glyphSize.Y) * 0.5f;

        var labelY =
            origin.Y +
            (size.Y - labelSize.Y) * 0.5f;

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            drawList.AddText(
                new Vector2(
                    contentX,
                    glyphY),
                ImGui.GetColorU32(
                    selected
                        ? Vector4.One
                        : MutedText),
                glyph);
        }

        drawList.AddText(
            new Vector2(
                contentX +
                glyphSize.X +
                gap,
                labelY),
            ImGui.GetColorU32(
                selected
                    ? Vector4.One
                    : MutedText),
            label);
    }

    // Same tile language as theme / background swatches — icon disc + title + muted subtitle.
    private static bool DrawProfileActionButton(
    FontAwesomeIcon icon,
    string title,
    string subtitle,
    Vector4 color,
    bool disabled = false,
    float width = 128f)
    {
        var size =
            new Vector2(
                width,
                50f);

        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.PushID(title);

        var clicked = false;

        using (ImRaii.Disabled(disabled))
        {
            clicked =
                ImGui.InvisibleButton(
                    "##profileAction",
                    size);
        }

        var hovered =
            ImGui.IsItemHovered();

        ImGui.PopID();

        var fill =
            disabled
                ? new Vector4(
                    CardBg.X,
                    CardBg.Y,
                    CardBg.Z,
                    CardBg.W * 0.55f)
                : hovered
                    ? CardBgHover
                    : CardBg;

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(fill),
            9f);

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                hovered && !disabled
                    ? new Vector4(
                        color.X,
                        color.Y,
                        color.Z,
                        0.45f)
                    : BorderSubtle),
            9f,
            ImDrawFlags.None,
            1f);

        const float disc = 30f;

        var discOrigin =
            origin +
            new Vector2(
                10f,
                (size.Y - disc) * 0.5f);

        drawList.AddRectFilled(
            discOrigin,
            discOrigin +
            new Vector2(disc, disc),
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    disabled
                        ? 0.10f
                        : 0.20f)),
            8f);

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            var glyph =
                icon.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            drawList.AddText(
                discOrigin +
                new Vector2(disc, disc) * 0.5f -
                glyphSize * 0.5f,
                ImGui.GetColorU32(
                    disabled
                        ? new Vector4(
                            color.X,
                            color.Y,
                            color.Z,
                            0.35f)
                        : color),
                glyph);
        }

        var titleColor =
            disabled
                ? new Vector4(
                    1f,
                    1f,
                    1f,
                    0.35f)
                : Vector4.One;

        var subtitleColor =
            disabled
                ? new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.35f)
                : MutedText;

        drawList.AddText(
            origin +
            new Vector2(50f, 9f),
            ImGui.GetColorU32(
                titleColor),
            title);

        drawList.AddText(
            origin +
            new Vector2(50f, 27f),
            ImGui.GetColorU32(
                subtitleColor),
            subtitle);

        return clicked &&
               !disabled;
    }


    private void ApplyAvatarSummary(CharacterSession session, AccountSummary updated)
    {
        // Same /avatars/{id}.ext URL after a replace — drop the cached GPU texture so the new bytes load.
        thumbnails.Invalidate(ResolveAvatarUrl(session.AvatarImageUrl));
        thumbnails.Invalidate(ResolveAvatarUrl(updated.AvatarImageUrl));

        session.AvatarIcon = updated.AvatarIcon;
        session.AvatarColorHex = updated.AvatarColorHex;
        session.AvatarImageUrl = updated.AvatarImageUrl;
        session.Bio = updated.Bio;
        session.StatusMessage = updated.StatusMessage;
        profileIconInput = updated.AvatarIcon;
        profileColorInput = updated.AvatarColorHex;
        profileImageUrl = updated.AvatarImageUrl;
        onSessionChanged(session);
    }

    private void UploadProfileAvatar(CharacterSession session, string rawPath)
    {
        var path = rawPath.Trim().Trim('"');
        if (path.Length == 0 || !File.Exists(path))
        {
            profileAvatarError = "Pick an existing png, jpg, or webp file.";
            return;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp"))
        {
            profileAvatarError = "Use a png, jpg, or webp image.";
            return;
        }

        var info = new FileInfo(path);
        if (info.Length > 1024 * 1024)
        {
            profileAvatarError = "Keep it under 1 MB.";
            return;
        }

        profileAvatarBusy = true;
        profileAvatarError = null;
        var token = session.Token;
        _ = Task.Run(async () =>
        {
            var updated = await authClient.UploadAvatarAsync(token, path);
            profileAvatarBusy = false;
            if (updated is null)
            {
                profileAvatarError = "Couldn't upload that picture.";
                return;
            }

            ApplyAvatarSummary(session, updated);
        });
    }

    private void ClearProfileAvatar(CharacterSession session)
    {
        profileAvatarBusy = true;
        profileAvatarError = null;
        var token = session.Token;
        _ = Task.Run(async () =>
        {
            var updated = await authClient.ClearAvatarAsync(token);
            profileAvatarBusy = false;
            if (updated is null)
            {
                profileAvatarError = "Couldn't remove the picture.";
                return;
            }

            ApplyAvatarSummary(session, updated);
        });
    }

    private void SaveProfile(CharacterSession session)
    {
        var token = session.Token;
        var request = new UpdateProfileRequest(null, profileIconInput, profileColorInput, profileBioInput, profileStatusInput);

        profileSaving = true;
        profileError = null;
        _ = Task.Run(async () =>
        {
            var outcome = await authClient.UpdateProfileAsync(token, request);
            profileSaving = false;
            if (outcome.Account is { } updated)
            {
                ApplyAvatarSummary(session, updated);
            }
            else
            {
                profileError = "Couldn't save your profile.";
            }
        });
    }

    private void StartSignIn(CharacterSession? linkUsing)
    {
        var characterName = CurrentCharacterName;
        var world = CurrentWorldName;
        if (string.IsNullOrEmpty(characterName) || string.IsNullOrEmpty(world))
        {
            return;
        }

        signInCts?.Cancel();
        signInCts = new CancellationTokenSource();
        var cancellationToken = signInCts.Token;

        signInState = SignInState.Requesting;
        signInVerificationUri = string.Empty;
        signInUserCode = string.Empty;
        signInStatusMessage = string.Empty;
        signInModalPending = true;

        var isLalafell = CurrentIsLalafell;
        _ = Task.Run(async () =>
        {
            var result = await signInFlow.RunAsync(characterName, world, isLalafell, linkUsing?.Token, start =>
            {
                signInVerificationUri = start.VerificationUriComplete ?? start.VerificationUri;
                signInUserCode = start.UserCode;
                signInState = SignInState.AwaitingBrowser;
            }, cancellationToken);

            switch (result.Outcome)
            {
                case SignInOutcome.Success when result.IsNewAccount:
                    // Don't persist the session yet for a brand-new account - onboarding still
                    // needs to run, and CurrentSession flipping non-null here would let the
                    // sign-in UI disappear mid-onboarding (DrawAccountSettings only shows it when
                    // CurrentSession is null). SubmitOnboarding below both submits the answers and
                    // is what actually calls onSessionChanged.
                    pendingOnboardingSession = result.Session;
                    onboardingRaces.Clear();
                    onboardingWantsToSeeLalafellContent = true;
                    onboardingNameInput = string.Empty;
                    onboardingNameError = null;
                    onboardingNameSubmitting = false;
                    signInState = SignInState.Onboarding;
                    break;
                case SignInOutcome.Success:
                    onSessionChanged(result.Session);
                    signInState = SignInState.Succeeded;
                    break;
                case SignInOutcome.Cancelled:
                    signInState = SignInState.Idle;
                    break;
                default:
                    signInStatusMessage = result.Message ?? "Sign-in failed.";
                    signInState = SignInState.Failed;
                    break;
            }
        }, cancellationToken);
    }

    private void SubmitOnboarding()
    {
        if (pendingOnboardingSession is not { } session)
        {
            return;
        }

        var name = onboardingNameInput.Trim();
        if (!DisplayNameRules.IsValid(name))
        {
            onboardingNameError = "Pick a username name so friends can find you.";
            return;
        }

        var races = onboardingRaces.ToArray();
        var wantsToSeeLalafellContent = onboardingWantsToSeeLalafellContent;
        onboardingNameError = null;
        onboardingNameSubmitting = true;

        _ = Task.Run(async () =>
        {
            // The gamer tag is what other players actually search/add by (see FriendService.
            // FindAccountByDisplayNameAsync), so it has to be reserved before onboarding can finish -
            // unlike races/Lalafell-visibility below, which are fire-and-forget preferences.
            var outcome = await authClient.UpdateDisplayNameAsync(session.Token, name);
            if (outcome.Account is not { } updated)
            {
                onboardingNameError = outcome.NameTaken
                    ? "That name's already taken - try another."
                    : outcome.InvalidFormat
                        ? "That name doesn't fit the rules above."
                        : "Couldn't save that name, try again.";
                onboardingNameSubmitting = false;
                return;
            }

            session.DisplayName = updated.DisplayName;
            await authClient.SubmitOnboardingAsync(session.Token, races, wantsToSeeLalafellContent);

            signInState = SignInState.Succeeded;
            onSessionChanged(session);
            pendingOnboardingSession = null;
            onboardingNameSubmitting = false;
        });
    }

    private void DrawSignInModal()
    {
        if (signInModalPending)
        {
            ImGui.OpenPopup("Sign in with XIVAuth");
            signInModalPending = false;
        }

        ImGui.SetNextWindowSize(new Vector2(360, 0));
        if (!ImGui.BeginPopupModal("Sign in with XIVAuth", ImGuiWindowFlags.NoResize))
        {
            return;
        }

        switch (signInState)
        {
            case SignInState.Requesting:
                ImGui.TextWrapped("Starting sign-in...");
                break;

            case SignInState.AwaitingBrowser:
                ImGui.TextWrapped("A browser window should have opened. If it didn't, open this link:");
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputText("##verificationUri", ref signInVerificationUri, 256, ImGuiInputTextFlags.ReadOnly);
                if (ImGui.SmallButton("Copy link"))
                {
                    ImGui.SetClipboardText(signInVerificationUri);
                }

                ImGui.Spacing();
                ImGui.TextWrapped("Code:");
                ImGui.TextColored(Accent, signInUserCode);
                ImGui.SameLine();
                if (ImGui.SmallButton("Copy code"))
                {
                    ImGui.SetClipboardText(signInUserCode);
                }

                ImGui.Spacing();
                ImGui.TextColored(MutedText, "Waiting for confirmation...");
                break;

            case SignInState.Onboarding:
                ImGui.TextColored(Good, "Signed in! A couple of quick questions:");
                ImGui.Spacing();
                ImGui.TextWrapped("Pick a username - this is what friends search for and see everywhere");
                var onboardingNameValid = DisplayNameRules.IsValid(onboardingNameInput);
                ImGui.SetNextItemWidth(-1f);
                using (ImRaii.Disabled(onboardingNameSubmitting))
                using (onboardingNameValid || onboardingNameInput.Length == 0 ? default : ImRaii.PushColor(ImGuiCol.Text, Danger))
                {
                    ImGui.InputText("##onboardingName", ref onboardingNameInput, DisplayNameRules.MaxLength);
                }

                ImGui.TextColored(MutedText,
                    $"{DisplayNameRules.MinLength}-{DisplayNameRules.MaxLength} characters: letters, numbers, single spaces, _ or -.");
                if (onboardingNameError is { Length: > 0 } nameOnboardError)
                {
                    ImGui.TextColored(Danger, nameOnboardError);
                }

                ImGui.Spacing();
                ImGui.TextWrapped("Which races do you play? (optional)");
                foreach (var race in KnownRaces)
                {
                    var selected = onboardingRaces.Contains(race);
                    if (ImGui.Checkbox(race, ref selected))
                    {
                        if (selected)
                        {
                            onboardingRaces.Add(race);
                        }
                        else
                        {
                            onboardingRaces.Remove(race);
                        }
                    }
                }

                ImGui.Spacing();
                ImGui.TextWrapped("Do you want to see Lalafell content in Friends/Activity/etc.? (you can change this later in Settings)");
                ImGui.Checkbox("Yes, show me Lalafell content", ref onboardingWantsToSeeLalafellContent);

                ImGui.Spacing();
                using (ImRaii.Disabled(!onboardingNameValid || onboardingNameSubmitting))
                {
                    if (ImGui.Button(onboardingNameSubmitting ? "Saving..." : "Done"))
                    {
                        SubmitOnboarding();
                    }
                }

                break;

            case SignInState.Succeeded:
                ImGui.TextColored(Good, "Signed in!");
                if (ImGui.Button("Close"))
                {
                    signInState = SignInState.Idle;
                    ImGui.CloseCurrentPopup();
                }

                break;

            case SignInState.Failed:
                ImGui.TextColored(Danger, signInStatusMessage.Length > 0 ? signInStatusMessage : "Sign-in failed.");
                if (ImGui.Button("Close"))
                {
                    signInState = SignInState.Idle;
                    ImGui.CloseCurrentPopup();
                }

                break;

            default:
                ImGui.CloseCurrentPopup();
                break;
        }

        if (signInState is SignInState.Requesting or SignInState.AwaitingBrowser)
        {
            ImGui.Spacing();
            if (ImGui.Button("Cancel"))
            {
                signInCts?.Cancel();
                signInState = SignInState.Idle;
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.EndPopup();
    }
}
