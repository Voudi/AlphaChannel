using AlphaChannel.Contracts;

namespace AlphaChannel.Server.Live;

// Icecast ingest for Music/DJ. Icecast itself has one source-password per process; isolation is
// the per-account mount (/radio/{accountId}). The password is only returned on POST /radio/me.
internal sealed class RadioService(IConfiguration configuration)
{
    public RadioCredentialsDto GetMine(Guid accountId, bool includePassword)
    {
        var host = configuration["RELAY_DOMAIN"]?.Trim() is { Length: > 0 } domain
            ? domain
            : "localhost";
        var port = 8000;
        var mount = $"/radio/{accountId}";
        var listenUrl = $"http://{host}:{port}{mount}";
        return new RadioCredentialsDto(
            listenUrl,
            host,
            port,
            mount,
            "source",
            includePassword ? configuration["ICECAST_SOURCE_PASSWORD"] : null);
    }
}
