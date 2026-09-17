using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed class ReportClient(Configuration configuration)
{
    internal async Task<bool> SubmitAsync(
        string bearerToken, string category, string? note, string? targetAccountId, string? targetMessageId,
        string? revealedBody, string? frankingKeyBase64)
    {
        using var http = PluginHttpClients.CreateApiClient(configuration, bearerToken);
        try
        {
            var response = await http.PostAsJsonAsync("/reports",
                new SubmitReportRequest(category, note, targetAccountId, targetMessageId, revealedBody, frankingKeyBase64)).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Reports", "Submit report", response);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Reports", "Submit report", exception);
            return false;
        }
    }
}
