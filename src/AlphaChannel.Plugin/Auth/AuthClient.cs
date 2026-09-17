using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed record UpdateDisplayNameOutcome(AccountSummary? Account, bool NameTaken, bool InvalidFormat);
internal sealed record SessionValidationResult(AccountSummary? Account, NetworkFailureInfo? Failure);

// REST client for the Alpha Channel server's /auth/* and /me endpoints.
// Sign-in uses HTTP requests; StreamClient handles the persistent /rt WebSocket connection.
internal sealed class AuthClient(Configuration configuration)
{
    private const string NetworkArea = "Auth";
    private HttpClient Http => PluginHttpClients.CreateApiClient(configuration);

    internal NetworkFailureInfo? LastFailure =>
        NetworkFailureClassifier.GetLast(NetworkArea);

    private static bool IsSuccess(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            NetworkFailureClassifier.Clear(NetworkArea);
            return true;
        }

        NetworkFailureClassifier.FromResponse(NetworkArea, operation, response);
        return false;
    }

    private static void Failed(string operation, Exception exception) =>
        NetworkFailureClassifier.FromException(NetworkArea, operation, exception);

    // Used to pick up server-side changes the client didn't itself just cause - currently only the
    // invite code, which rotates whenever someone else redeems it (see FriendService.
    // RedeemInviteCodeAsync), so the Settings page's copy of it can go stale.
    internal async Task<AccountSummary?> GetMeAsync(string bearerToken)
    {
        using var http = Http;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        try
        {
            var response = await http.GetAsync("/me").ConfigureAwait(false);
            return IsSuccess(response, "Fetch account")
                ? await response.Content.ReadFromJsonAsync<AccountSummary>().ConfigureAwait(false)
                : null;
        }
        catch (Exception exception)
        {
            Failed("Fetch account", exception);
            return null;
        }
    }

    internal async Task<SessionValidationResult> ValidateSessionAsync(
        string bearerToken,
        CancellationToken cancellationToken)
    {
        using var http = Http;
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", bearerToken);

        try
        {
            using var response = await http
                .GetAsync("/me", cancellationToken)
                .ConfigureAwait(false);

            if (!IsSuccess(response, "Restore session"))
            {
                return new SessionValidationResult(null, LastFailure);
            }

            var account = await response.Content
                .ReadFromJsonAsync<AccountSummary>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (account is null)
            {
                Failed(
                    "Restore session",
                    new System.Text.Json.JsonException("The account response was empty."));
                return new SessionValidationResult(null, LastFailure);
            }

            return new SessionValidationResult(account, null);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException(
                NetworkArea,
                "Restore session",
                exception,
                cancellationToken.IsCancellationRequested);
            return new SessionValidationResult(null, LastFailure);
        }
    }

    internal Task<AuthStartResponse?> StartAsync(string characterName, string world, bool isLalafell) =>
        PostAsync<AuthStartResponse>("/auth/xivauth/start", new AuthStartRequest(characterName, world, isLalafell));

    internal Task<AuthPollResponse?> PollAsync(string flowId) =>
        PostAsync<AuthPollResponse>("/auth/xivauth/poll", new AuthPollRequest(flowId));

    internal Task<AuthStartResponse?> StartLinkAsync(string bearerToken, string characterName, string world, bool isLalafell) =>
        PostAsync<AuthStartResponse>("/auth/xivauth/link/start", new AuthStartRequest(characterName, world, isLalafell), bearerToken);

    internal Task<AuthPollResponse?> PollLinkAsync(string bearerToken, string flowId) =>
        PostAsync<AuthPollResponse>("/auth/xivauth/link/poll", new AuthPollRequest(flowId), bearerToken);

    internal async Task<bool> SubmitOnboardingAsync(string bearerToken, string[] races, bool wantsToSeeLalafellContent)
    {
        using var http = Http;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        try
        {
            var response = await http.PostAsJsonAsync("/me/onboarding", new OnboardingRequest(races, wantsToSeeLalafellContent)).ConfigureAwait(false);
            return IsSuccess(response, "Submit onboarding");
        }
        catch (Exception exception)
        {
            Failed("Submit onboarding", exception);
            return false;
        }
    }

    internal Task<UpdateDisplayNameOutcome> UpdateDisplayNameAsync(string bearerToken, string displayName) =>
        UpdateProfileAsync(bearerToken, new UpdateProfileRequest(displayName, null, null, null, null));

    internal async Task<UpdateDisplayNameOutcome> UpdateProfileAsync(string bearerToken, UpdateProfileRequest request)
    {
        using var http = Http;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        try
        {
            var response = await http.PatchAsJsonAsync("/me", request).ConfigureAwait(false);
            if (IsSuccess(response, "Update profile"))
            {
                return new UpdateDisplayNameOutcome(await response.Content.ReadFromJsonAsync<AccountSummary>().ConfigureAwait(false), false, false);
            }

            return new UpdateDisplayNameOutcome(null, response.StatusCode == System.Net.HttpStatusCode.Conflict,
                response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity);
        }
        catch (Exception exception)
        {
            Failed("Update profile", exception);
            return new UpdateDisplayNameOutcome(null, false, false);
        }
    }

    internal async Task<AccountSummary?> UploadAvatarAsync(string bearerToken, string filePath)
    {
        using var http = Http;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        try
        {
            await using var stream = File.OpenRead(filePath);
            using var content = new MultipartFormDataContent();
            var part = new StreamContent(stream);
            part.Headers.ContentType = new MediaTypeHeaderValue(GuessImageMime(filePath));
            content.Add(part, "file", Path.GetFileName(filePath));

            var response = await http.PostAsync("/me/avatar", content).ConfigureAwait(false);
            return IsSuccess(response, "Upload avatar")
                ? await response.Content.ReadFromJsonAsync<AccountSummary>().ConfigureAwait(false)
                : null;
        }
        catch (Exception exception)
        {
            Failed("Upload avatar", exception);
            return null;
        }
    }

    internal async Task<AccountSummary?> ClearAvatarAsync(string bearerToken)
    {
        using var http = Http;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        try
        {
            var response = await http.DeleteAsync("/me/avatar").ConfigureAwait(false);
            return IsSuccess(response, "Clear avatar")
                ? await response.Content.ReadFromJsonAsync<AccountSummary>().ConfigureAwait(false)
                : null;
        }
        catch (Exception exception)
        {
            Failed("Clear avatar", exception);
            return null;
        }
    }

    private static string GuessImageMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream",
    };

    // Null covers both "not found" and "not viewable" (not friends) - server returns 404 either
    // way, see FriendService.GetProfileAsync's own doc comment on why those stay indistinguishable.
    internal async Task<AccountProfileDto?> GetProfileAsync(string bearerToken, string accountId)
    {
        using var http = Http;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        try
        {
            var response = await http.GetAsync($"/accounts/{accountId}/profile").ConfigureAwait(false);
            return IsSuccess(response, "Fetch profile")
                ? await response.Content.ReadFromJsonAsync<AccountProfileDto>().ConfigureAwait(false)
                : null;
        }
        catch (Exception exception)
        {
            Failed("Fetch profile", exception);
            return null;
        }
    }

    internal async Task<LinkedCharacterDto[]?> GetMyCharactersAsync(string bearerToken)
    {
        using var http = Http;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        try
        {
            var response = await http.GetAsync("/me/characters").ConfigureAwait(false);
            return IsSuccess(response, "Fetch linked characters")
                ? await response.Content.ReadFromJsonAsync<LinkedCharacterDto[]>().ConfigureAwait(false)
                : null;
        }
        catch (Exception exception)
        {
            Failed("Fetch linked characters", exception);
            return null;
        }
    }

    internal async Task RevokeAsync(string bearerToken)
    {
        using var http = Http;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        try
        {
            using var response = await http.PostAsync("/auth/token/revoke", null).ConfigureAwait(false);
            _ = IsSuccess(response, "Revoke session");
        }
        catch (Exception exception)
        {
            Failed("Revoke session", exception);
        }
    }

    private async Task<T?> PostAsync<T>(string path, object body, string? bearerToken = null)
    {
        using var http = Http;
        if (bearerToken is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        try
        {
            var response = await http.PostAsJsonAsync(path, body).ConfigureAwait(false);
            return IsSuccess(response, $"Request {path}")
                ? await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false)
                : default;
        }
        catch (Exception exception)
        {
            Failed($"Request {path}", exception);
            return default;
        }
    }
}
