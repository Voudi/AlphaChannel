using System.Net;
using SixLabors.ImageSharp;

namespace AlphaChannel.Plugin.Video;

internal sealed record SafeImageDownloadResult(
    byte[]? Data,
    Uri? FinalUri,
    string? Error)
{
    internal bool IsSuccess =>
        Data is { Length: > 0 } &&
        FinalUri is not null &&
        string.IsNullOrWhiteSpace(Error);

    internal static SafeImageDownloadResult Failed(
        string error)
    {
        return new SafeImageDownloadResult(
            null,
            null,
            error);
    }
}

/// <summary>
/// Downloads images from approved hosts while enforcing network, response-size,
/// file-format and decoded-dimension restrictions.
/// </summary>
internal sealed class SafeImageDownloader : IDisposable
{
    private static readonly HashSet<string> AllowedContentTypes =
        new(
            StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png",
            "image/webp",
            "image/gif"
        };

    private static readonly HashSet<string> AllowedFormatNames =
        new(
            StringComparer.OrdinalIgnoreCase)
        {
            "JPEG",
            "PNG",
            "WEBP",
            "GIF"
        };

    private readonly HttpClient http;

    internal SafeImageDownloader()
    {
        var handler =
            new SocketsHttpHandler
            {
                //
                // Redirects are handled manually so every destination can be
                // checked against the hostname and network-address policy.
                //
                AllowAutoRedirect =
                    false,

                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli,

                //
                // Some approved image CDNs can take longer to establish a new
                // connection, particularly when Windows first attempts IPv6.
                //
                ConnectTimeout =
    TimeSpan.FromSeconds(15),

                PooledConnectionLifetime =
                    TimeSpan.FromMinutes(5)
            };

        http =
            new HttpClient(
                handler)
            {
                Timeout =
                    Timeout.InfiniteTimeSpan
            };

        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/131.0 Safari/537.36");
    }

    internal async Task<SafeImageDownloadResult> DownloadAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (!ImageHostPolicy.TryValidateUrl(
                url,
                out var currentUri,
                out var validationError))
        {
            return SafeImageDownloadResult.Failed(
                validationError ??
                "The image URL is not supported.");
        }

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(
            ImageHostPolicy.DownloadTimeout);

        try
        {
            for (var redirectCount = 0;
                 redirectCount <=
                     ImageHostPolicy.MaximumRedirects;
                 redirectCount++)
            {
                var addressError =
                    await ValidateResolvedAddressesAsync(
                            currentUri!,
                            timeout.Token)
                        .ConfigureAwait(false);

                if (addressError is not null)
                {
                    return SafeImageDownloadResult.Failed(
                        addressError);
                }

                using var request =
                    new HttpRequestMessage(
                        HttpMethod.Get,
                        currentUri);

                request.Headers.Accept.ParseAdd(
                    "image/jpeg,image/png,image/webp,image/gif");

                if (string.Equals(
        currentUri!.IdnHost,
        "myimgs.org",
        StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.Referrer =
                        new Uri(
                            "https://myimgs.org/");

                    request.Headers.TryAddWithoutValidation(
                        "Accept-Language",
                        "en-GB,en;q=0.9");
                }

                using var response =
                    await http.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token)
                        .ConfigureAwait(false);

                if (IsRedirect(
                        response.StatusCode))
                {
                    if (redirectCount >=
                        ImageHostPolicy.MaximumRedirects)
                    {
                        return SafeImageDownloadResult.Failed(
                            "The image URL redirected too many times.");
                    }

                    var location =
                        response.Headers.Location;

                    if (location is null)
                    {
                        return SafeImageDownloadResult.Failed(
                            "The image server returned an invalid redirect.");
                    }

                    var redirectedUri =
                        location.IsAbsoluteUri
                            ? location
                            : new Uri(
                                currentUri!,
                                location);

                    if (!ImageHostPolicy.TryValidateUrl(
                            redirectedUri.AbsoluteUri,
                            out currentUri,
                            out validationError))
                    {
                        return SafeImageDownloadResult.Failed(
                            "The image redirected to an unsupported location. " +
                            validationError);
                    }

                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return SafeImageDownloadResult.Failed(
                        $"The image server returned HTTP " +
                        $"{(int)response.StatusCode}.");
                }

                var contentType =
                    response.Content.Headers
                        .ContentType?
                        .MediaType;

                if (string.IsNullOrWhiteSpace(
                        contentType) ||
                    !AllowedContentTypes.Contains(
                        contentType))
                {
                    return SafeImageDownloadResult.Failed(
                        "The URL did not return a supported image content type.");
                }

                var contentLength =
                    response.Content.Headers
                        .ContentLength;

                if (contentLength is >
                    ImageHostPolicy.MaximumDownloadBytes)
                {
                    return SafeImageDownloadResult.Failed(
                        "The image is larger than the 15 MB limit.");
                }

                var data =
                    await ReadLimitedAsync(
                            response.Content,
                            timeout.Token)
                        .ConfigureAwait(false);

                if (data is null)
                {
                    return SafeImageDownloadResult.Failed(
                        "The image is larger than the 15 MB limit.");
                }

                var format =
                    Image.DetectFormat(
                        data);

                if (format is null ||
                    !AllowedFormatNames.Contains(
                        format.Name))
                {
                    return SafeImageDownloadResult.Failed(
                        "The downloaded file is not a supported JPEG, PNG, WebP or GIF image.");
                }

                var imageInfo =
                    Image.Identify(
                        data);

                if (imageInfo is null)
                {
                    return SafeImageDownloadResult.Failed(
                        "The downloaded image could not be identified.");
                }

                if (imageInfo.Width <= 0 ||
                    imageInfo.Height <= 0 ||
                    imageInfo.Width >
                        ImageHostPolicy.MaximumWidth ||
                    imageInfo.Height >
                        ImageHostPolicy.MaximumHeight ||
                    (long)imageInfo.Width *
                        imageInfo.Height >
                        ImageHostPolicy.MaximumPixels)
                {
                    return SafeImageDownloadResult.Failed(
                        "The image dimensions exceed the safety limit.");
                }

                return new SafeImageDownloadResult(
                    data,
                    currentUri,
                    null);
            }

            return SafeImageDownloadResult.Failed(
                "The image URL redirected too many times.");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return SafeImageDownloadResult.Failed(
                "The image download timed out.");
        }
        catch (OperationCanceledException)
        {
            return SafeImageDownloadResult.Failed(
                "The image download was cancelled.");
        }
        catch (HttpRequestException exception)
        {
            var rootException =
                exception.GetBaseException();

            AepLog.Warning(
                $"[Image] HTTP download failed for {currentUri}: " +
                $"{exception}");

            return SafeImageDownloadResult.Failed(
                "The image could not be downloaded: " +
                rootException.Message);
        }
        catch (UnknownImageFormatException)
        {
            return SafeImageDownloadResult.Failed(
                "The downloaded file is not a supported image.");
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Image] Download validation failed: {exception.Message}");

            return SafeImageDownloadResult.Failed(
                "The image could not be safely loaded.");
        }
    }

    private static async Task<string?> ValidateResolvedAddressesAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;

        try
        {
            addresses =
                await Dns.GetHostAddressesAsync(
                        uri.DnsSafeHost,
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is
                  System.Net.Sockets.SocketException or
                  ArgumentException)
        {
            return
                "The image hostname could not be resolved.";
        }

        if (addresses.Length == 0)
        {
            return
                "The image hostname did not resolve to an address.";
        }

        if (addresses.Any(
                address =>
                    !ImageHostPolicy.IsPublicAddress(
                        address)))
        {
            return
                "The image hostname resolved to a restricted network address.";
        }

        return null;
    }

    private static async Task<byte[]?> ReadLimitedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var source =
            await content
                .ReadAsStreamAsync(
                    cancellationToken)
                .ConfigureAwait(false);

        using var destination =
            new MemoryStream();

        var buffer =
            new byte[32 * 1024];

        while (true)
        {
            var read =
                await source.ReadAsync(
                        buffer.AsMemory(
                            0,
                            buffer.Length),
                        cancellationToken)
                    .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            if (destination.Length + read >
                ImageHostPolicy.MaximumDownloadBytes)
            {
                return null;
            }

            destination.Write(
                buffer,
                0,
                read);
        }

        return destination.ToArray();
    }

    private static bool IsRedirect(
        HttpStatusCode statusCode)
    {
        return statusCode is
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;
    }

    public void Dispose()
    {
        http.Dispose();
    }
}