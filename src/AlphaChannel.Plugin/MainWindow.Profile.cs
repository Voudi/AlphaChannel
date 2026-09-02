using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

// "View profile" popup - reachable from Friends (MainWindow.Social.cs) and Alpha Chat
// (MainWindow.Messages.cs), fetches AccountProfileDto fresh each open rather than caching, since
// avatar/bio/status can change between visits and this isn't opened often enough for that round
// trip to matter.
internal sealed partial class MainWindow
{
    private bool profilePopupPending;
    private string? profilePopupAccountId;
    private string? profilePopupFallbackName;
    private AccountProfileDto? profilePopupData;
    private bool profilePopupLoading;
    private bool profilePopupNotAvailable;

    private void OpenProfilePopup(CharacterSession session, string accountId, string fallbackDisplayName)
    {
        profilePopupAccountId = accountId;
        profilePopupFallbackName = fallbackDisplayName;
        profilePopupData = null;
        profilePopupNotAvailable = false;
        profilePopupLoading = true;
        profilePopupPending = true;

        var token = session.Token;
        _ = Task.Run(async () =>
        {
            var profile = await authClient.GetProfileAsync(token, accountId);
            profilePopupData = profile;
            profilePopupNotAvailable = profile is null;
            profilePopupLoading = false;
        });
    }

    private void DrawProfilePopup()
    {
        if (profilePopupPending)
        {
            ImGui.OpenPopup("Profile##viewProfile");
            profilePopupPending = false;
        }

        ImGui.SetNextWindowSize(new Vector2(320, 0));
        if (!ImGui.BeginPopupModal("Profile##viewProfile", ImGuiWindowFlags.NoResize))
        {
            return;
        }

        if (profilePopupLoading)
        {
            ImGui.TextDisabled("Loading...");
        }
        else if (profilePopupNotAvailable || profilePopupData is null)
        {
            ImGui.TextColored(MutedText, $"{profilePopupFallbackName}'s profile isn't available.");
        }
        else
        {
            var profile = profilePopupData;
            DrawAvatarChip(profile.AvatarIcon, profile.AvatarColorHex, 56, profile.AvatarImageUrl);
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.Text(profile.DisplayName);
            if (profile.IsDeveloper)
            {
                ImGui.SameLine();
                ImGui.TextColored(Accent, "Developer");
            }

            if (profile.PatreonTier is PatreonTier.Tier1 or PatreonTier.Tier2 or PatreonTier.Tier3)
            {
                ImGui.TextColored(Gold, $"Patreon {profile.PatreonTier}");
            }
            if (profile.StatusMessage is { Length: > 0 } status)
            {
                ImGui.TextColored(MutedText, status);
            }

            ImGui.EndGroup();

            if (profile.Bio is { Length: > 0 } bio)
            {
                ImGui.Spacing();
                ImGui.TextWrapped(bio);
            }

            if (profile.FriendsSinceUnix is { } friendsSince)
            {
                ImGui.Spacing();
                var since = DateTimeOffset.FromUnixTimeSeconds(friendsSince).LocalDateTime;
                ImGui.TextColored(MutedText, $"Friends since {since:MMM d, yyyy}");
            }
        }

        ImGui.Spacing();
        ImGui.Spacing();
        if (ImGui.Button("Close"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
