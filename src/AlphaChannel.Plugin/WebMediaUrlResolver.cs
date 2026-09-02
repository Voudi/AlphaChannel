using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Experimental resolver for turning a normal webpage URL into a
/// direct media URL.
///
/// Deliberately separate from VideoUrlResolver, which is Alpha Channel's
/// existing YouTube/YoutubeExplode resolver.
///
/// Resolution order:
/// 1. Already-direct media URL
/// 2. yt-dlp
/// 3. Lightweight HTML inspection
/// </summary>
internal static class WebMediaUrlResolver
{
    private static readonly HttpClient Http =
        CreateHttpClient();


    private static HttpClient CreateHttpClient()
    {
        var client =
            new HttpClient
            {
                Timeout =
                    TimeSpan.FromSeconds(
                        20)
            };

        client.DefaultRequestHeaders
            .TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/124.0 Safari/537.36");

        return client;
    }


    internal static async Task<(
        string? Url,
        string? Error,
        string? Method)> ResolveAsync(
        Resources resources,
        string inputUrl,
        CancellationToken token)
    {
        if (!Uri.TryCreate(
                inputUrl,
                UriKind.Absolute,
                out var inputUri) ||
            inputUri.Scheme is not ("http" or "https"))
        {
            return (
                null,
                "The URL must begin with http:// or https://.",
                null);
        }


        //
        // The URL may LOOK like a direct media link because its path
        // ends in .mp4/.webm/etc, but some sites use media-looking
        // URLs for HTML player pages.
        //
        // Verify the response Content-Type before treating it as a
        // direct media file.
        //

        if (LooksLikeDirectMediaUrl(
                inputUri) &&
            await IsActualDirectMediaAsync(
                    inputUri,
                    token)
                .ConfigureAwait(false))
        {
            return (
                inputUri.ToString(),
                null,
                "Direct URL");
        }


        //
        // First try lightweight HTML inspection.
        //
        // A lot of ordinary video pages expose the actual media URL
        // directly through og:video, twitter:player:stream, <video>,
        // <source>, or JSON metadata.
        //
        // Doing this before yt-dlp also avoids unnecessary anti-bot
        // failures on sites where the media URL was already sitting
        // in the page source.
        //

        var htmlResult =
            await TryResolveFromHtmlAsync(
                inputUri,
                token)
            .ConfigureAwait(false);

        if (htmlResult is not null)
        {
            return (
                htmlResult,
                null,
                "HTML");
        }


        //
        // If the page itself didn't expose anything useful, fall back
        // to Alpha Channel's existing yt-dlp binary.
        //

        var ytDlpResult =
            await TryResolveWithYtDlpAsync(
                resources,
                inputUri.ToString(),
                token)
            .ConfigureAwait(false);

        if (ytDlpResult.Url is not null)
        {
            return (
                ytDlpResult.Url,
                null,
                "yt-dlp");
        }


        var error =
            ytDlpResult.Error;

        if (string.IsNullOrWhiteSpace(
                error))
        {
            error =
                "No playable video URL was found on this page.";
        }

        return (
            null,
            error,
            null);
    }


    // ---------------------------------------------------------
    // yt-dlp
    // ---------------------------------------------------------

    private static async Task<(
        string? Url,
        string? Error)> TryResolveWithYtDlpAsync(
        Resources resources,
        string pageUrl,
        CancellationToken token)
    {
        var ytDlpPath =
            resources.GetLocationYTDLP();

        if (string.IsNullOrWhiteSpace(
                ytDlpPath) ||
            !File.Exists(
                ytDlpPath))
        {
            return (
                null,
                "yt-dlp is not installed.");
        }


        try
        {
            using var process =
                new Process();

            process.StartInfo =
                new ProcessStartInfo
                {
                    FileName =
                        ytDlpPath,

                    UseShellExecute =
                        false,

                    RedirectStandardOutput =
                        true,

                    RedirectStandardError =
                        true,

                    CreateNoWindow =
                        true
                };


            //
            // We deliberately request one self-contained/best stream.
            // This experimental button only wants a URL that can be
            // handed straight back to the existing player.
            //

            process.StartInfo.ArgumentList.Add(
                "--no-playlist");

            process.StartInfo.ArgumentList.Add(
                "--no-warnings");

            process.StartInfo.ArgumentList.Add(
                "--socket-timeout");

            process.StartInfo.ArgumentList.Add(
                "15");

            process.StartInfo.ArgumentList.Add(
                "--format");

            process.StartInfo.ArgumentList.Add(
                "best");

            process.StartInfo.ArgumentList.Add(
                "--get-url");

            process.StartInfo.ArgumentList.Add(
                "--extractor-args");

            process.StartInfo.ArgumentList.Add(
                "generic:impersonate");

            process.StartInfo.ArgumentList.Add(
                pageUrl);


            if (!process.Start())
            {
                return (
                    null,
                    "yt-dlp could not be started.");
            }


            var stdoutTask =
                process.StandardOutput
                    .ReadToEndAsync();

            var stderrTask =
                process.StandardError
                    .ReadToEndAsync();


            try
            {
                await process
                    .WaitForExitAsync(
                        token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(
                            true);
                    }
                }
                catch
                {
                    // Best effort only.
                }

                throw;
            }


            var stdout =
                await stdoutTask
                    .ConfigureAwait(false);

            var stderr =
                await stderrTask
                    .ConfigureAwait(false);


            if (process.ExitCode != 0)
            {
                var message =
                    string.IsNullOrWhiteSpace(
                        stderr)
                        ? "yt-dlp could not resolve this page."
                        : LastUsefulLine(
                            stderr);

                return (
                    null,
                    message);
            }


            //
            // --get-url normally gives us one URL with -f best.
            // Still handle multiple lines defensively and take the
            // first valid HTTP(S) candidate.
            //

            foreach (
                var line in stdout.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries))
            {
                if (Uri.TryCreate(
                        line,
                        UriKind.Absolute,
                        out var resolved) &&
                    resolved.Scheme is
                        "http" or "https")
                {
                    AepLog.Debug(
                        $"[WebResolver] yt-dlp resolved " +
                        $"{pageUrl} -> {resolved}");

                    return (
                        resolved.ToString(),
                        null);
                }
            }


            return (
                null,
                "yt-dlp completed but did not return a playable URL.");
        }
        catch (OperationCanceledException)
        {
            return (
                null,
                "Resolve cancelled.");
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[WebResolver] yt-dlp failed: " +
                exception.Message);

            return (
                null,
                $"yt-dlp failed: {exception.Message}");
        }
    }


    // ---------------------------------------------------------
    // HTML fallback
    // ---------------------------------------------------------

    private static async Task<string?> TryResolveFromHtmlAsync(
        Uri pageUri,
        CancellationToken token)
    {
        try
        {
            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    pageUri);

            using var response =
                await Http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseContentRead,
                        token)
                    .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }


            var html =
                await response.Content
                    .ReadAsStringAsync(
                        token)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(
                    html))
            {
                return null;
            }


            var finalPageUri =
                response.RequestMessage?
                    .RequestUri ??
                pageUri;


            //
            // =========================================================
            // INTERNET ARCHIVE EXACT-FILE RESOLUTION
            // =========================================================
            //
            // Internet Archive item pages can contain many videos, and
            // their generic OpenGraph metadata can point at the FIRST
            // video rather than the specifically selected file.
            //
            // When the URL explicitly selects a file:
            //
            //   /details/{identifier}/{filename.mp4}
            //
            // preserve that exact filename.
            //
            // For MP4 files, prefer Archive's stream-friendly .ia.mp4
            // derivative when it exists. The original Archive file may
            // technically reach FILE_LOADED in MPV but still fail to
            // produce usable playback.
            //
            // If the matching .ia.mp4 derivative does not exist, fall
            // back to the exact original file.
            //

            if (finalPageUri.Host.Equals(
                    "archive.org",
                    StringComparison.OrdinalIgnoreCase) ||
                finalPageUri.Host.EndsWith(
                    ".archive.org",
                    StringComparison.OrdinalIgnoreCase))
            {
                var pathSegments =
                    finalPageUri.AbsolutePath
                        .Split(
                            '/',
                            StringSplitOptions.RemoveEmptyEntries);


                //
                // Expected selected-file URL:
                //
                // /details/{identifier}/{filename}
                //

                if (pathSegments.Length >= 3 &&
                    pathSegments[0].Equals(
                        "details",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var identifier =
                        Uri.UnescapeDataString(
                            pathSegments[1]);


                    var requestedFileName =
                        Uri.UnescapeDataString(
                            string.Join(
                                "/",
                                pathSegments.Skip(2)));


                    if (!string.IsNullOrWhiteSpace(
                            identifier) &&
                        !string.IsNullOrWhiteSpace(
                            requestedFileName))
                    {
                        var encodedIdentifier =
                            Uri.EscapeDataString(
                                identifier);


                        string EncodeArchivePath(
                            string fileName)
                        {
                            return string.Join(
                                "/",
                                fileName
                                    .Split('/')
                                    .Select(
                                        Uri.EscapeDataString));
                        }


                        //
                        // ---------------------------------------------------------
                        // Prefer the matching Archive .ia.mp4 derivative.
                        // ---------------------------------------------------------
                        //
                        // Example:
                        //
                        // Hazbin_Hotel_S02E04_The_Deal.mp4
                        //
                        // becomes:
                        //
                        // Hazbin_Hotel_S02E04_The_Deal.ia.mp4
                        //
                        // Crucially, we derive this from the REQUESTED filename,
                        // so Episode 4 can never accidentally become Episode 1.
                        //

                        if (requestedFileName.EndsWith(
                                ".mp4",
                                StringComparison.OrdinalIgnoreCase) &&
                            !requestedFileName.EndsWith(
                                ".ia.mp4",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            var derivativeFileName =
                                requestedFileName[..^4] +
                                ".ia.mp4";


                            var archiveDerivativeUri =
                                new Uri(
                                    $"https://archive.org/download/" +
                                    $"{encodedIdentifier}/" +
                                    EncodeArchivePath(
                                        derivativeFileName));


                            AepLog.Info(
                                $"[WebResolver] Checking Internet Archive derivative: " +
                                $"{archiveDerivativeUri}");


                            if (await IsActualDirectMediaAsync(
                                    archiveDerivativeUri,
                                    token)
                                .ConfigureAwait(false))
                            {
                                AepLog.Info(
                                    $"[WebResolver] Internet Archive matching derivative found: " +
                                    $"{archiveDerivativeUri}");


                                return archiveDerivativeUri
                                    .ToString();
                            }


                            AepLog.Debug(
                                $"[WebResolver] Internet Archive matching derivative was not available: " +
                                $"{archiveDerivativeUri}");
                        }


                        //
                        // ---------------------------------------------------------
                        // Fall back to the exact original requested file.
                        // ---------------------------------------------------------
                        //
                        // Do not use another media candidate from the Archive
                        // page here. The filename must continue to match the file
                        // the user explicitly selected.
                        //

                        var archiveDirectUri =
                            new Uri(
                                $"https://archive.org/download/" +
                                $"{encodedIdentifier}/" +
                                EncodeArchivePath(
                                    requestedFileName));


                        if (LooksLikeDirectMediaUrl(
                                archiveDirectUri))
                        {
                            AepLog.Info(
                                $"[WebResolver] Internet Archive exact original-file fallback: " +
                                $"{archiveDirectUri}");


                            return archiveDirectUri
                                .ToString();
                        }
                    }
                }
            }



            //
            // HTML entities are common in query strings.
            //

            html =
                WebUtility.HtmlDecode(
                    html);


            //
            // <video src="...">
            // <source src="...">
            //

            foreach (
                Match match in
                VideoSourcePattern
                    .Matches(
                        html))
            {
                var candidate =
                    NormaliseCandidate(
                        match.Groups["url"].Value,
                        finalPageUri);

                if (candidate is not null)
                {
                    return candidate;
                }
            }


            //
            // OpenGraph:
            //
            // <meta property="og:video"
            //       content="...">
            //

            foreach (
                Match match in
                MetaTagPattern
                    .Matches(
                        html))
            {
                var tag =
                    match.Value;

                var isVideoMeta =
                 tag.Contains(
                     "og:video",
                     StringComparison.OrdinalIgnoreCase) ||

                 tag.Contains(
                     "twitter:player:stream",
                     StringComparison.OrdinalIgnoreCase);


                if (!isVideoMeta)
                {
                    continue;
                }

                var contentMatch =
                    ContentAttributePattern
                        .Match(
                            tag);

                if (!contentMatch.Success)
                {
                    continue;
                }

                var candidate =
              NormaliseCandidate(
                  contentMatch
                      .Groups["url"]
                      .Value,
                  finalPageUri,
                  requireMediaExtension:
                      true);

                if (candidate is not null)
                {
                    return candidate;
                }
            }


            //
            // JSON-LD VideoObject:
            //
            // "contentUrl": "https://..."
            //

            foreach (
                Match match in
                ContentUrlPattern
                    .Matches(
                        html))
            {
                var candidate =
                    NormaliseCandidate(
                        match.Groups["url"].Value,
                        finalPageUri,
                        requireMediaExtension:
                            false);

                if (candidate is not null)
                {
                    return candidate;
                }
            }


            //
            // Last ditch:
            // obvious .mp4/.webm/.m3u8 URLs embedded somewhere in
            // scripts or page data.
            //

            foreach (
                Match match in
                EmbeddedMediaPattern
                    .Matches(
                        html))
            {
                var candidate =
                    NormaliseCandidate(
                        match.Groups["url"].Value,
                        finalPageUri);

                if (candidate is not null)
                {
                    return candidate;
                }
            }


            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[WebResolver] HTML fallback failed: " +
                exception.Message);

            return null;
        }
    }


    private static string? NormaliseCandidate(
        string raw,
        Uri pageUri,
        bool requireMediaExtension = true)
    {
        if (string.IsNullOrWhiteSpace(
                raw))
        {
            return null;
        }


        var value =
            raw.Trim()
                .Replace(
                    "\\/",
                    "/",
                    StringComparison.Ordinal);


        if (!Uri.TryCreate(
                value,
                UriKind.Absolute,
                out var uri))
        {
            if (!Uri.TryCreate(
                    pageUri,
                    value,
                    out uri))
            {
                return null;
            }
        }


        if (uri.Scheme is not
            ("http" or "https"))
        {
            return null;
        }


        if (requireMediaExtension &&
            !LooksLikeDirectMediaUrl(
                uri))
        {
            return null;
        }


        AepLog.Debug(
            $"[WebResolver] HTML resolved " +
            $"{pageUri} -> {uri}");

        return uri.ToString();
    }

    private static async Task<bool> IsActualDirectMediaAsync(
    Uri uri,
    CancellationToken token)
    {
        try
        {
            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    uri);


            //
            // We only need the response headers.
            // ResponseHeadersRead prevents HttpClient from downloading
            // the entire video just to determine what it is.
            //

            using var response =
                await Http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        token)
                    .ConfigureAwait(false);


            if (!response.IsSuccessStatusCode)
            {
                return false;
            }


            var contentType =
                response.Content.Headers
                    .ContentType?
                    .MediaType;


            if (string.IsNullOrWhiteSpace(
                    contentType))
            {
                //
                // No useful Content-Type was supplied.
                // Let the normal HTML/yt-dlp resolution path decide.
                //

                return false;
            }


            AepLog.Debug(
                $"[WebResolver] Media probe for {uri}: " +
                $"{contentType}");


            //
            // Explicit HTML means this is a webpage/player page,
            // regardless of whether its URL happens to end in .mp4.
            //

            if (contentType.StartsWith(
                    "text/html",
                    StringComparison.OrdinalIgnoreCase) ||
                contentType.StartsWith(
                    "application/xhtml",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }


            //
            // Common direct video/audio MIME types.
            //

            if (contentType.StartsWith(
                    "video/",
                    StringComparison.OrdinalIgnoreCase) ||
                contentType.StartsWith(
                    "audio/",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }


            //
            // HLS manifests.
            //

            if (contentType.Equals(
                    "application/vnd.apple.mpegurl",
                    StringComparison.OrdinalIgnoreCase) ||
                contentType.Equals(
                    "application/x-mpegurl",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }


            //
            // Some CDNs serve downloadable media generically.
            // Since LooksLikeDirectMediaUrl() already verified the
            // extension, allow binary responses too.
            //

            if (contentType.Equals(
                    "application/octet-stream",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }


            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            AepLog.Debug(
                $"[WebResolver] Media probe failed for " +
                $"{uri}: {exception.Message}");

            return false;
        }
    }

    private static bool LooksLikeDirectMediaUrl(
        Uri uri)
    {
        var path =
            uri.AbsolutePath;

        return
            path.EndsWith(
                ".mp4",
                StringComparison.OrdinalIgnoreCase) ||

            path.EndsWith(
                ".m4v",
                StringComparison.OrdinalIgnoreCase) ||

            path.EndsWith(
                ".webm",
                StringComparison.OrdinalIgnoreCase) ||

            path.EndsWith(
                ".mov",
                StringComparison.OrdinalIgnoreCase) ||

            path.EndsWith(
                ".m3u8",
                StringComparison.OrdinalIgnoreCase);
    }


    private static string LastUsefulLine(
        string text)
    {
        var lines =
            text.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);

        if (lines.Length == 0)
        {
            return
                "yt-dlp could not resolve this page.";
        }

        return lines[^1];
    }


    // ---------------------------------------------------------
    // Regex patterns
    // ---------------------------------------------------------

    private static readonly Regex VideoSourcePattern =
        new(
            @"<(?:video|source)\b[^>]*?\bsrc\s*=\s*[""'](?<url>[^""']+)[""']",
            RegexOptions.IgnoreCase |
            RegexOptions.Compiled);


    private static readonly Regex MetaTagPattern =
        new(
            @"<meta\b[^>]*>",
            RegexOptions.IgnoreCase |
            RegexOptions.Compiled);


    private static readonly Regex ContentAttributePattern =
        new(
            @"\bcontent\s*=\s*[""'](?<url>[^""']+)[""']",
            RegexOptions.IgnoreCase |
            RegexOptions.Compiled);


    private static readonly Regex ContentUrlPattern =
        new(
            @"""contentUrl""\s*:\s*""(?<url>[^""]+)""",
            RegexOptions.IgnoreCase |
            RegexOptions.Compiled);


    private static readonly Regex EmbeddedMediaPattern =
        new(
            @"(?<url>https?:\\?/\\?/[^""'\s<>]+?\.(?:mp4|m4v|webm|mov|m3u8)(?:\?[^""'\s<>]*)?)",
            RegexOptions.IgnoreCase |
            RegexOptions.Compiled);
}