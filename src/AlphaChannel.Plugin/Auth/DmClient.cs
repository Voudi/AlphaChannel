using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed class DmClient(Configuration configuration)
{
    private HttpClient Http(string bearerToken)
    {
        return PluginHttpClients.CreateApiClient(configuration, bearerToken);
    }

    // One member = 1:1 (resumes the existing conversation with that pair if there is one); two or
    // more always creates a new group - see DmService.CreateConversationAsync's own doc comment.
    internal async Task<string?> CreateConversationAsync(string bearerToken, string[] memberAccountIds, string? name = null)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.PostAsJsonAsync("/dm/conversations", new CreateConversationRequest(memberAccountIds, name)).ConfigureAwait(false);
            if (!NetworkFailureClassifier.IsSuccess("Messages", "Create conversation", response))
            {
                return null;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("conversationId", out var idEl) ? idEl.GetString() : null;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Messages", "Create conversation", exception);
            return null;
        }
    }

    internal Task<ConversationSummaryDto[]?> GetConversationsAsync(string bearerToken) =>
        GetAsync<ConversationSummaryDto[]>(bearerToken, "/dm/conversations");

    internal Task<MessagePage?> GetMessagesAsync(string bearerToken, string conversationId, long? before) =>
        GetAsync<MessagePage>(bearerToken, before is { } cursor
            ? $"/dm/conversations/{conversationId}/messages?before={cursor}"
            : $"/dm/conversations/{conversationId}/messages");

    internal async Task<MessageDto?> SendMessageAsync(string bearerToken, string conversationId, SendMessageRequest request)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.PostAsJsonAsync($"/dm/conversations/{conversationId}/messages", request).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Messages", "Send message", response)
                ? await response.Content.ReadFromJsonAsync<MessageDto>().ConfigureAwait(false)
                : null;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Messages", "Send message", exception);
            return null;
        }
    }

    internal async Task MarkReadAsync(string bearerToken, string conversationId)
    {
        using var http = Http(bearerToken);
        try
        {
            using var response = await http.PostAsync($"/dm/conversations/{conversationId}/read", null).ConfigureAwait(false);
            _ = NetworkFailureClassifier.IsSuccess("Messages", "Mark conversation read", response);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Messages", "Mark conversation read", exception);
        }
    }

    private async Task<T?> GetAsync<T>(string bearerToken, string path)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.GetAsync(path).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Messages", $"Request {path}", response)
                ? await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false)
                : default;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Messages", $"Request {path}", exception);
            return default;
        }
    }
}
