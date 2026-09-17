using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed class LiveClient(Configuration configuration)
{
    internal NetworkFailureInfo? LastFailure =>
        NetworkFailureClassifier.GetLast("Live");

    private HttpClient Http(string bearerToken)
    {
        return PluginHttpClients.CreateApiClient(configuration, bearerToken);
    }

    // Regenerating instantly invalidates any previous key - an OBS session still pushing with the
    // old one starts failing its next publish-auth check (see Server/Live/LiveService.cs).
    internal async Task<string?> RotateKeyAsync(string bearerToken)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.PostAsync("/live/key/rotate", null).ConfigureAwait(false);
            if (!NetworkFailureClassifier.IsSuccess("Live", "Rotate stream key", response))
            {
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<RotateStreamKeyResponse>().ConfigureAwait(false);
            return result?.StreamKey;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Live", "Rotate stream key", exception);
            return null;
        }
    }

    internal Task<LiveStatusDto?> GetMyStatusAsync(string bearerToken) => GetAsync<LiveStatusDto>(bearerToken, "/live/mine");

    internal async Task<LiveFriendDto[]> GetFriendsLiveAsync(string bearerToken) =>
        await GetAsync<LiveFriendDto[]>(bearerToken, "/live/friends").ConfigureAwait(false) ?? [];

    private async Task<T?> GetAsync<T>(string bearerToken, string path)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.GetAsync(path).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Live", $"Request {path}", response)
                ? await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false)
                : default;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Live", $"Request {path}", exception);
            return default;
        }
    }
}
