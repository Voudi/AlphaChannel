using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed class ActivityClient(Configuration configuration)
{
    internal async Task<ActivityPage?> GetFeedAsync(string bearerToken, long? before)
    {
        using var http = PluginHttpClients.CreateApiClient(configuration, bearerToken);
        try
        {
            var query = before is { } cursor ? $"/activity?before={cursor}" : "/activity";
            var response = await http.GetAsync(query).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Activity", "Fetch activity", response)
                ? await response.Content.ReadFromJsonAsync<ActivityPage>().ConfigureAwait(false)
                : null;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Activity", "Fetch activity", exception);
            return null;
        }
    }

    internal async Task<int> GetUnreadCountAsync(string bearerToken)
    {
        using var http = PluginHttpClients.CreateApiClient(configuration, bearerToken);
        try
        {
            var response = await http.GetAsync("/activity/unread-count").ConfigureAwait(false);
            var result = NetworkFailureClassifier.IsSuccess("Activity", "Fetch unread count", response)
                ? await response.Content.ReadFromJsonAsync<UnreadCountResponse>().ConfigureAwait(false)
                : null;
            return result?.Count ?? 0;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Activity", "Fetch unread count", exception);
            return 0;
        }
    }

    internal async Task MarkReadAsync(string bearerToken, long upToUnix)
    {
        using var http = PluginHttpClients.CreateApiClient(configuration, bearerToken);
        try
        {
            using var response = await http.PostAsJsonAsync("/activity/read", new MarkActivityReadRequest(upToUnix)).ConfigureAwait(false);
            _ = NetworkFailureClassifier.IsSuccess("Activity", "Mark activity read", response);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Activity", "Mark activity read", exception);
        }
    }
}
