using System.Diagnostics;
using System.Text.Json;

namespace AlphaChannel.Plugin.Video;

internal sealed record TwitchStreamInfo(
    string Title,
    string Url,
    string ChannelName,
    string? ThumbnailUrl);

internal sealed class TwitchChannelChecker
{
    private static readonly HashSet<string> ReservedRoutes =
        new(
            StringComparer.OrdinalIgnoreCase)
        {
            "bits",
            "clip",
            "clips",
            "collections",
            "directory",
            "downloads",
            "drops",
            "inventory",
            "jobs",
            "p",
            "products",
            "search",
            "settings",
            "subscriptions",
            "turbo",
            "videos",
            "wallet"
        };

    /// <summary>
    /// Converts a Twitch username or channel URL into a normalized,
    /// lower-case channel username.
    /// </summary>
    internal static bool TryNormalizeChannelName(
        string? input,
        out string channelName,
        out string? error)
    {
        channelName =
            string.Empty;

        error =
            null;

        if (string.IsNullOrWhiteSpace(
                input))
        {
            error =
                "Enter a Twitch channel name or URL.";

            return false;
        }

        var value =
            input.Trim();

        var looksLikeUrl =
            value.Contains(
                "://",
                StringComparison.Ordinal) ||
            value.StartsWith(
                "twitch.tv/",
                StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(
                "www.twitch.tv/",
                StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(
                "m.twitch.tv/",
                StringComparison.OrdinalIgnoreCase);

        string candidate;

        if (looksLikeUrl)
        {
            if (!value.Contains(
                    "://",
                    StringComparison.Ordinal))
            {
                value =
                    $"https://{value}";
            }

            if (!Uri.TryCreate(
                    value,
                    UriKind.Absolute,
                    out var uri))
            {
                error =
                    "That Twitch URL is not valid.";

                return false;
            }

            if (!string.Equals(
           uri.Scheme,
           Uri.UriSchemeHttp,
           StringComparison.OrdinalIgnoreCase) &&
       !string.Equals(
           uri.Scheme,
           Uri.UriSchemeHttps,
           StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "Only Twitch web links are supported.";

                return false;
            }

            var host =
                uri.IdnHost;

            var supportedHost =
                string.Equals(
                    host,
                    "twitch.tv",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    host,
                    "www.twitch.tv",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    host,
                    "m.twitch.tv",
                    StringComparison.OrdinalIgnoreCase);

            if (!supportedHost)
            {
                error =
                    "Enter a twitch.tv channel URL.";

                return false;
            }

            var segments =
                uri.AbsolutePath.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries);

            if (segments.Length == 0)
            {
                error =
                    "The Twitch URL does not contain a channel name.";

                return false;
            }

            candidate =
                segments[0];
        }
        else
        {
            candidate =
                value;

            if (candidate.Length > 0 &&
                candidate[0] == '@')
            {
                candidate =
                    candidate[1..];
            }

            candidate =
                candidate.Trim('/');
        }

        candidate =
            Uri.UnescapeDataString(
                candidate)
            .Trim()
            .ToLowerInvariant();

        if (candidate.Length is < 1 or > 25)
        {
            error =
                "Twitch channel names must be between 1 and 25 characters.";

            return false;
        }

        if (candidate.Any(
                character =>
                    !char.IsAsciiLetterOrDigit(
                        character) &&
                    character != '_'))
        {
            error =
                "Twitch channel names can only contain letters, numbers and underscores.";

            return false;
        }

        if (ReservedRoutes.Contains(
                candidate))
        {
            error =
                "That Twitch URL does not point to a channel.";

            return false;
        }

        channelName =
            candidate;

        return true;
    }

    public async Task<(
         TwitchStreamInfo? Stream,
         string? Error,
         bool IsOffline)>
     CheckLiveAsync(
         string ytdlpPath,
         string channelName,
         CancellationToken token)
    {
        if (!TryNormalizeChannelName(
                channelName,
                out var normalizedChannel,
                out var normalizationError))
        {
            return (
                null,
                normalizationError,
                false);
        }

        var url =
            $"https://www.twitch.tv/{normalizedChannel}";

        var startInfo =
            new ProcessStartInfo(
                ytdlpPath)
            {
                UseShellExecute =
                    false,

                RedirectStandardOutput =
                    true,

                RedirectStandardError =
                    true,

                CreateNoWindow =
                    true
            };

        startInfo.ArgumentList.Add(
            "-j");

        startInfo.ArgumentList.Add(
            "--no-warnings");

        startInfo.ArgumentList.Add(
            url);

        try
        {
            using var process =
                Process.Start(
                    startInfo);

            if (process is null)
            {
                return (
                    null,
                    "Could not start yt-dlp.",
                    false);
            }

            var stdoutTask =
                process.StandardOutput.ReadToEndAsync(
                    token);

            var stderrTask =
                process.StandardError.ReadToEndAsync(
                    token);

            await process.WaitForExitAsync(
                    token)
                .ConfigureAwait(false);

            var stdout =
                await stdoutTask.ConfigureAwait(false);

            var stderr =
                await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0 ||
      string.IsNullOrWhiteSpace(
          stdout))
            {
                var offline =
                    stderr.Contains(
                        "offline",
                        StringComparison.OrdinalIgnoreCase) ||
                    stderr.Contains(
                        "not live",
                        StringComparison.OrdinalIgnoreCase) ||
                    stderr.Contains(
                        "not currently live",
                        StringComparison.OrdinalIgnoreCase) ||
                    stderr.Contains(
                        "not currently streaming",
                        StringComparison.OrdinalIgnoreCase) ||
                    stderr.Contains(
                        "no live streams",
                        StringComparison.OrdinalIgnoreCase) ||
                    stderr.Contains(
                        "no streams found",
                        StringComparison.OrdinalIgnoreCase);

                var diagnostic =
                    stderr.Trim();

                if (diagnostic.Length > 500)
                {
                    diagnostic =
                        diagnostic[..500];
                }

                AepLog.Debug(
                    $"[Twitch] yt-dlp check for {normalizedChannel} " +
                    $"exited with code {process.ExitCode}: {diagnostic}");

                return (
                    null,
                    offline
                        ? $"{normalizedChannel} is not live right now."
                        : "Could not find that Twitch channel.",
                    offline);
            }

            using var document =
                JsonDocument.Parse(
                    stdout);

            var root =
     document.RootElement;

            var title =
     root.TryGetProperty(
         "description",
         out var descriptionProperty) &&
     !string.IsNullOrWhiteSpace(
         descriptionProperty.GetString())
         ? descriptionProperty.GetString()!.Trim()
         : root.TryGetProperty(
               "fulltitle",
               out var fullTitleProperty) &&
           !string.IsNullOrWhiteSpace(
               fullTitleProperty.GetString())
             ? fullTitleProperty.GetString()!.Trim()
             : normalizedChannel;

            var displayChannelName =
                root.TryGetProperty(
                    "uploader",
                    out var uploaderProperty) &&
                !string.IsNullOrWhiteSpace(
                    uploaderProperty.GetString())
                    ? uploaderProperty.GetString()!.Trim()
                    : normalizedChannel;

            var thumbnail =
                root.TryGetProperty(
                    "thumbnail",
                    out var thumbnailProperty)
                    ? thumbnailProperty.GetString()
                    : null;

            return (
                new TwitchStreamInfo(
                    title,
                    url,
                    displayChannelName,
                    thumbnail),
                null,
                false);
        }
        catch (OperationCanceledException)
        {
            return (
                null,
                null,
                false);
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Twitch] Check failed for {normalizedChannel}: " +
                $"{exception.Message}");

            return (
                null,
                "Something went wrong checking that channel.",
                false);
        }
    }
}