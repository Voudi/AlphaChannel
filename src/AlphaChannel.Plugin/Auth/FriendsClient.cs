using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

// REST wrapper around AlphaChannel.Server's /friends* + /accounts/by-handle endpoints. Every call
// needs the current character's session token - callers pass it in rather than this class holding
// a reference to Configuration/Plugin.Cfg, since which token is "current" can change out from under
// a long-lived object if the player switches characters mid-session.
internal sealed class FriendsClient(Configuration configuration)
{
    internal enum FriendRequestOutcome
    {
        Sent,
        NotFound,
        AlreadyFriends,
        AlreadyPending,
        Failed,
    }
    // Set whenever a call gets a 403 with a {"reason": "..."} body (LalafellGateFilter's shape) -
    // "lalafell_pending" or "lalafell_denied". Checked by the UI after a failed load to show a
    // specific message instead of a generic error. Not thread-safe beyond what a single "last call
    // result" field can be, which matches how these calls are already used (one Task.Run at a time
    // per UI action).
    internal string? LastAccessDeniedReason { get; private set; }

    private HttpClient Http(string bearerToken)
    {
        return PluginHttpClients.CreateApiClient(configuration, bearerToken);
    }

    internal Task<FriendDto[]?> GetFriendsAsync(string bearerToken) =>
        GetAsync<FriendDto[]>(bearerToken, "/friends");

    internal Task<FriendRequestsPage?> GetRequestsAsync(string bearerToken) =>
        GetAsync<FriendRequestsPage>(bearerToken, "/friends/requests");

    internal Task<AccountSummaryDto?> FindByDisplayNameAsync(string bearerToken, string displayName) =>
        GetAsync<AccountSummaryDto>(bearerToken, $"/accounts/by-handle/{Uri.EscapeDataString(displayName)}");

    // Fires on every keystroke from the Friends page's live search box - see FriendService.
    // SearchByDisplayNamePrefixAsync for the server-side matching/filtering.
    internal Task<FriendSearchResultDto[]?> SearchAsync(string bearerToken, string query) =>
        GetAsync<FriendSearchResultDto[]>(bearerToken, $"/friends/search?q={Uri.EscapeDataString(query)}");

    internal async Task<bool> SendRequestAsync(string bearerToken, string displayName)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.PostAsJsonAsync("/friends/requests", new SendFriendRequestRequest(displayName)).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Friends", "Send friend request", response);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Friends", "Send friend request", exception);
            return false;
        }
    }

    // Right-click "Add Friend" in-game (Plugin.cs's OnMenuOpened) - looks up by real character
    // identity instead of a chosen name, see FriendService.SendRequestByCharacterAsync.
    internal async Task<FriendRequestOutcome> SendRequestByCharacterAsync(string bearerToken, string characterName, string world)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.PostAsJsonAsync("/friends/requests/by-character",
                new SendFriendRequestByCharacterRequest(characterName, world)).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                NetworkFailureClassifier.Clear("Friends");
                return FriendRequestOutcome.Sent;
            }

            NetworkFailureClassifier.FromResponse("Friends", "Send friend request by character", response);

            var result = await response.Content.ReadFromJsonAsync<FriendRequestOutcomeDto>().ConfigureAwait(false);
            return result?.Outcome switch
            {
                "not_found" => FriendRequestOutcome.NotFound,
                "already_friends" => FriendRequestOutcome.AlreadyFriends,
                "already_pending" => FriendRequestOutcome.AlreadyPending,
                _ => FriendRequestOutcome.Failed,
            };
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Friends", "Send friend request by character", exception);
            return FriendRequestOutcome.Failed;
        }
    }

    internal Task<CharacterStreamDto?> FindJoinableStreamByCharacterAsync(
        string bearerToken, string characterName, string world) =>
        GetAsync<CharacterStreamDto>(bearerToken,
            $"/streams/by-character?characterName={Uri.EscapeDataString(characterName)}&world={Uri.EscapeDataString(world)}");

    internal async Task<bool> RedeemInviteCodeAsync(string bearerToken, string inviteCode)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.PostAsJsonAsync("/friends/invite/redeem", new RedeemInviteCodeRequest(inviteCode)).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Friends", "Redeem invite code", response);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Friends", "Redeem invite code", exception);
            return false;
        }
    }

    internal Task<bool> AcceptRequestAsync(string bearerToken, string requestId) =>
        PostAsync(bearerToken, $"/friends/requests/{requestId}/accept");

    internal Task<bool> DeclineRequestAsync(string bearerToken, string requestId) =>
        PostAsync(bearerToken, $"/friends/requests/{requestId}/decline");

    internal Task<AccountSummaryDto[]?> GetBlocksAsync(string bearerToken) =>
        GetAsync<AccountSummaryDto[]>(bearerToken, "/blocks");

    internal Task<bool> BlockAsync(string bearerToken, string accountId) =>
        PostAsync(bearerToken, $"/blocks/{accountId}");

    internal async Task<bool> UnblockAsync(string bearerToken, string accountId)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.DeleteAsync($"/blocks/{accountId}").ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Friends", "Unblock account", response);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Friends", "Unblock account", exception);
            return false;
        }
    }

    internal async Task<bool> RemoveFriendAsync(string bearerToken, string accountId)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.DeleteAsync($"/friends/{accountId}").ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Friends", "Remove friend", response);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Friends", "Remove friend", exception);
            return false;
        }
    }

    private async Task<bool> PostAsync(string bearerToken, string path)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.PostAsync(path, null).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Friends", $"Request {path}", response);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Friends", $"Request {path}", exception);
            return false;
        }
    }

    private async Task<T?> GetAsync<T>(string bearerToken, string path)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.GetAsync(path).ConfigureAwait(false);
            LastAccessDeniedReason = null;
            if (response.IsSuccessStatusCode)
            {
                NetworkFailureClassifier.Clear("Friends");
                return await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
            }

            NetworkFailureClassifier.FromResponse("Friends", $"Request {path}", response);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                LastAccessDeniedReason = await TryReadReasonAsync(response);
            }

            return default;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Friends", $"Request {path}", exception);
            return default;
        }
    }

    private static async Task<string?> TryReadReasonAsync(HttpResponseMessage response)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
