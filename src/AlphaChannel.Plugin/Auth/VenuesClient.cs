using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed class VenuesClient(Configuration configuration)
{
    private HttpClient Http(string bearerToken)
    {
        return PluginHttpClients.CreateApiClient(configuration, bearerToken);
    }

    internal async Task<VenueDto?> CreateAsync(string bearerToken, CreateVenueRequest request)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.PostAsJsonAsync("/venues", request).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Venues", "Create venue", response)
                ? await response.Content.ReadFromJsonAsync<VenueDto>().ConfigureAwait(false)
                : null;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Venues", "Create venue", exception);
            return null;
        }
    }

    internal Task<VenueDto[]?> GetMineAsync(string bearerToken) => GetAsync<VenueDto[]>(bearerToken, "/venues/mine");

    // Null means "not friends" (server 404s), distinct from an empty array meaning "friends, but no
    // venues saved" - see VenueService.GetFriendVenuesAsync's own doc comment.
    internal Task<VenueDto[]?> GetFriendVenuesAsync(string bearerToken, string accountId) =>
        GetAsync<VenueDto[]>(bearerToken, $"/friends/{accountId}/venues");

    internal async Task<bool> DeleteAsync(string bearerToken, string venueId)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.DeleteAsync($"/venues/{venueId}").ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Venues", "Delete venue", response);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Venues", "Delete venue", exception);
            return false;
        }
    }

    private async Task<T?> GetAsync<T>(string bearerToken, string path)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.GetAsync(path).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Venues", $"Request {path}", response)
                ? await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false)
                : default;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Venues", $"Request {path}", exception);
            return default;
        }
    }
}
