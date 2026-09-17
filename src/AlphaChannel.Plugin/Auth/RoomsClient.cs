using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed class RoomsClient(Configuration configuration)
{
    internal NetworkFailureInfo? LastFailure =>
        NetworkFailureClassifier.GetLast("Rooms");

    private HttpClient Http(string bearerToken)
    {
        return PluginHttpClients.CreateApiClient(configuration, bearerToken);
    }

    internal async Task<RoomDirectoryDto[]> ListAsync(string bearerToken, RoomKind? kind = null)
    {
        var path = kind is { } k ? $"/rooms?kind={k}" : "/rooms";
        using var http = Http(bearerToken);
        try
        {
            var response = await http.GetAsync(path).ConfigureAwait(false);
            if (!NetworkFailureClassifier.IsSuccess("Rooms", "List rooms", response))
            {
                return [];
            }

            return await response.Content.ReadFromJsonAsync<RoomDirectoryDto[]>().ConfigureAwait(false) ?? [];
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Rooms", "List rooms", exception);
            return [];
        }
    }
}
