using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin.Auth;

internal sealed class RadioClient(Configuration configuration)
{
    private HttpClient Http(string bearerToken)
    {
        var http = new HttpClient { BaseAddress = new Uri(configuration.RelayServerUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return http;
    }

    internal async Task<RadioCredentialsDto?> IssueAsync(string bearerToken)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.PostAsync("/radio/me", null).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<RadioCredentialsDto>().ConfigureAwait(false)
                : null;
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[Radio] issue failed: {exception.Message}");
            return null;
        }
    }
}
