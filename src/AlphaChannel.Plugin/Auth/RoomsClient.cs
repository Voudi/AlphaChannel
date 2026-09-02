using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin.Auth;

internal sealed class RoomsClient(Configuration configuration)
{
    private HttpClient Http(string bearerToken)
    {
        var http = new HttpClient { BaseAddress = new Uri(configuration.RelayServerUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return http;
    }

    internal async Task<RoomDirectoryDto[]> ListAsync(string bearerToken, RoomKind? kind = null)
    {
        var path = kind is { } k ? $"/rooms?kind={k}" : "/rooms";
        using var http = Http(bearerToken);
        try
        {
            var response = await http.GetAsync(path).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            return await response.Content.ReadFromJsonAsync<RoomDirectoryDto[]>().ConfigureAwait(false) ?? [];
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[Rooms] list failed: {exception.Message}");
            return [];
        }
    }
}
