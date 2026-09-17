using System.Net;
using System.Net.Http.Headers;

namespace AlphaChannel.Plugin.Net;

/// <summary>
/// Owns the plugin's reusable HTTP connection pools. API calls get a small
/// disposable HttpClient wrapper so request-specific authorization and a
/// Debug server switch cannot leak into concurrent requests. Disposing that
/// wrapper does not dispose the shared handler or its pooled connections.
/// </summary>
internal static class PluginHttpClients
{
    private static readonly SocketsHttpHandler ApiHandler = CreateHandler();
    private static readonly SocketsHttpHandler GeneralHandler = CreateHandler();

    internal static HttpClient CreateApiClient(
        Configuration configuration,
        string? bearerToken = null)
    {
        var client = new HttpClient(ApiHandler, disposeHandler: false)
        {
            BaseAddress = new Uri(configuration.RelayServerUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return client;
    }

    internal static HttpClient CreateMetadataClient() =>
        new(GeneralHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

    internal static HttpClient CreateDownloadClient() =>
        new(GeneralHandler, disposeHandler: false)
        {
            // Dependency downloads supply their own cancellation and may take
            // several minutes on slower connections.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    internal static HttpClient CreateProbeClient() =>
        new(GeneralHandler, disposeHandler: false)
        {
            // Live endpoint probes use short linked cancellation tokens rather
            // than a global client timeout.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private static SocketsHttpHandler CreateHandler() =>
        new()
        {
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 16,
        };
}
