using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed class TwitchClient(
    Configuration configuration)
{
    internal async Task<TwitchStreamDto[]> GetTrendingAsync(
        string bearerToken)
    {
        using var http =
            PluginHttpClients.CreateApiClient(
                configuration,
                bearerToken);

        try
        {
            using var response =
                await http.GetAsync(
                        "/twitch/trending")
                    .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                NetworkFailureClassifier.FromResponse("Twitch", "Fetch trending streams", response);
                var responseText =
                    await response.Content
                        .ReadAsStringAsync()
                        .ConfigureAwait(false);

                AepLog.Warning(
                    "[Twitch] Trending endpoint returned " +
                    $"HTTP {(int)response.StatusCode} " +
                    $"{response.ReasonPhrase}. " +
                    $"Response: {responseText}");

                return [];
            }

            var streams =
                await response.Content
                    .ReadFromJsonAsync<TwitchStreamDto[]>()
                    .ConfigureAwait(false) ??
                [];

            NetworkFailureClassifier.Clear("Twitch");

            if (streams.Length == 0)
            {
                AepLog.Warning(
                    "[Twitch] Trending endpoint returned HTTP 200 " +
                    "with an empty stream cache. The server may be " +
                    "missing Twitch credentials or its Helix refresh " +
                    "may be failing.");
            }
            else
            {
                AepLog.Info(
                    $"[Twitch] Trending endpoint returned " +
                    $"{streams.Length} streams.");
            }

            return streams;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Twitch", "Fetch trending streams", exception);

            return [];
        }
    }
}
