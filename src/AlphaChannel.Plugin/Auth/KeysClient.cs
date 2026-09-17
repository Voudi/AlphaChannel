using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed class KeysClient(Configuration configuration)
{
    internal async Task<bool> UploadPublicKeyAsync(string bearerToken, string publicKeyBase64)
    {
        using var http = PluginHttpClients.CreateApiClient(configuration, bearerToken);
        try
        {
            var response = await http.PutAsJsonAsync("/keys/me", new UploadPublicKeyRequest(publicKeyBase64)).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Keys", "Upload public key", response);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Keys", "Upload public key", exception);
            return false;
        }
    }

    internal async Task<string?> GetPublicKeyAsync(string bearerToken, string accountId)
    {
        using var http = PluginHttpClients.CreateApiClient(configuration, bearerToken);
        try
        {
            var response = await http.GetAsync($"/keys/users/{accountId}").ConfigureAwait(false);
            if (!NetworkFailureClassifier.IsSuccess("Keys", "Fetch public key", response))
            {
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<PublicKeyDto>().ConfigureAwait(false);
            return dto?.PublicKeyBase64;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Keys", "Fetch public key", exception);
            return null;
        }
    }
}
