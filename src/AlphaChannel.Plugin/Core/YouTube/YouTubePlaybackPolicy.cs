namespace AlphaChannel.Plugin.Video;

internal enum YouTubePlaybackRoute
{
    Android,
    PoToken,
    AndroidProbe
}

/// <summary>
/// Chooses between fast Android extraction and PO-token compatibility
/// mode.
///
/// Normal operation uses Android. A recognised YouTube bot check enables
/// PO-token mode for one hour. Once that period expires, the next video
/// probes Android while the HTTP provider remains ready.
/// </summary>
internal sealed class YouTubePlaybackPolicy
{
    private static readonly TimeSpan CompatibilityDuration =
        TimeSpan.FromHours(1);

    private static readonly TimeSpan ProviderStopDelay =
        TimeSpan.FromSeconds(10);

    private readonly Configuration configuration;
    private readonly YouTubePoTokenService poTokenService;
    private readonly object syncRoot =
        new();

    private bool probeInProgress;
    private int stateGeneration;

    internal YouTubePlaybackPolicy(
        Configuration configuration,
        YouTubePoTokenService poTokenService)
    {
        this.configuration =
            configuration;

        this.poTokenService =
            poTokenService;

        //
        // Remove an expired value left by a previous plugin session.
        // There is no provider process to retain after a full restart.
        //
        if (configuration.YouTubePoCompatibilityUntilUnixSeconds > 0 &&
            GetCompatibilityUntilUtc() <= DateTimeOffset.UtcNow)
        {
            configuration.YouTubePoCompatibilityUntilUnixSeconds =
                0;

            configuration.Save();
        }
    }

    internal bool CompatibilityModeActive
    {
        get
        {
            lock (syncRoot)
            {
                return
                    configuration
                        .PreferHighQualityYouTubeVideos ||
                    GetCompatibilityUntilUtc() >
                        DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// Chooses the route for a newly requested YouTube video.
    /// This does not start or stop currently playing media.
    /// </summary>
    internal YouTubePlaybackRoute SelectRoute()
    {
        lock (syncRoot)
        {
            //
            // The user's explicit quality preference takes priority over
            // the automatic Android/compatibility timer policy.
            //
            if (configuration
                .PreferHighQualityYouTubeVideos)
            {
                //
                // Start preparing the provider immediately. VideoEngine
                // will still await readiness before loading the video.
                //
                _ = poTokenService
                    .EnsureStartedAsync();

                AepLog.Debug(
                    "[YouTube/Policy] Higher-quality YouTube playback selected.");

                return
                    YouTubePlaybackRoute.PoToken;
            }

            var untilUtc =
                GetCompatibilityUntilUtc();

            if (untilUtc >
                DateTimeOffset.UtcNow)
            {
                //
                // Starting is fire-and-forget here. Callers performing a
                // retry can explicitly await EnsureProviderReadyAsync().
                //
                _ = poTokenService
                    .EnsureStartedAsync();

                return
                    YouTubePlaybackRoute.PoToken;
            }

            //
            // The compatibility timer has expired, but a provider started
            // during this plugin session remains alive. Keep it available
            // while one Android playback tests whether the restriction has
            // cleared.
            //
            if (poTokenService.IsRunning)
            {
                if (!probeInProgress)
                {
                    probeInProgress =
                        true;

                    AepLog.Info(
                        "[YouTube/Policy] Compatibility period expired. " +
                        "The next YouTube video will probe Android.");

                    return
                        YouTubePlaybackRoute.AndroidProbe;
                }

                //
                // Avoid running multiple Android probes simultaneously.
                // Other requests retain the dependable PO route until the
                // active probe produces a result.
                //
                return
                    YouTubePlaybackRoute.PoToken;
            }

            return
                YouTubePlaybackRoute.Android;
        }
    }

    /// <summary>
    /// Ensures the HTTP provider is ready before a PO-token retry.
    /// Script mode remains available if HTTP startup fails.
    /// </summary>
    internal Task<bool> EnsureProviderReadyAsync()
    {
        return poTokenService
            .EnsureStartedAsync();
    }

    /// <summary>
    /// Returns true only for errors that strongly indicate YouTube's
    /// automated traffic/bot verification.
    ///
    /// Generic HTTP failures are deliberately excluded because they may
    /// be ordinary CDN or network errors.
    /// </summary>
    internal static bool IsBotCheckError(
        string? message)
    {
        if (string.IsNullOrWhiteSpace(
                message))
        {
            return false;
        }

        var normalized =
            message.ToLowerInvariant();

        return
            normalized.Contains(
                "sign in to confirm you're not a bot") ||
            normalized.Contains(
                "sign in to confirm you’re not a bot") ||
            normalized.Contains(
                "confirm you are not a bot") ||
            normalized.Contains(
                "login_required") ||
            normalized.Contains(
                "this helps protect our community") ||
            normalized.Contains(
                "only images are available");
    }

    /// <summary>
    /// Identifies failures where a deliberately connected YouTube account can
    /// provide access. General bot checks, geo restrictions, unavailable
    /// videos and network failures are intentionally excluded.
    /// </summary>
    internal static bool IsAccountRequiredError(
        string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var normalized =
            message.ToLowerInvariant();

        return normalized.Contains("sign in to confirm your age") ||
               normalized.Contains("sign in to confirm age") ||
               normalized.Contains("age-restricted") ||
               normalized.Contains("age restricted") ||
               normalized.Contains("members-only") ||
               normalized.Contains("members only") ||
               normalized.Contains("channel members") ||
               normalized.Contains("join this channel") ||
               normalized.Contains("this video is private") ||
               normalized.Contains("private video") ||
               normalized.Contains("authentication required") ||
               normalized.Contains("login_required");
    }

    /// <summary>
    /// Activates or extends PO-token compatibility mode following a
    /// recognised Android bot check.
    /// </summary>
    internal async Task EnterCompatibilityModeAsync()
    {
        bool extending;
        DateTimeOffset untilUtc;

        lock (syncRoot)
        {
            extending =
                configuration
                    .YouTubePoCompatibilityUntilUnixSeconds > 0;

            untilUtc =
                DateTimeOffset.UtcNow +
                CompatibilityDuration;

            configuration.YouTubePoCompatibilityUntilUnixSeconds =
                untilUtc.ToUnixTimeSeconds();

            probeInProgress =
                false;

            stateGeneration++;

            configuration.Save();
        }

        AepLog.Info(
            "[YouTube/Policy] YouTube bot verification detected. " +
            $"PO-token compatibility mode enabled until {untilUtc:O}.");

        await poTokenService
            .EnsureStartedAsync()
            .ConfigureAwait(false);

        if (extending)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] YouTube verification is still active. " +
                "Compatibility mode has been extended for another 60 minutes.");
        }
        else
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] YouTube requested an additional verification check. " +
                "Alpha Channel will use compatibility mode for the next 60 minutes. " +
                "YouTube videos may take a few extra seconds to start.");
        }
    }

    /// <summary>
    /// Called only after the selected route has reached confirmed media
    /// playback, not merely after mpv accepted a load command.
    /// </summary>
    internal void ReportPlaybackSucceeded(
        YouTubePlaybackRoute route)
    {
        if (route !=
            YouTubePlaybackRoute.AndroidProbe)
        {
            return;
        }

        int successfulGeneration;

        lock (syncRoot)
        {
            probeInProgress =
                false;

            configuration.YouTubePoCompatibilityUntilUnixSeconds =
                0;

            successfulGeneration =
                ++stateGeneration;

            configuration.Save();
        }

        AepLog.Info(
            "[YouTube/Policy] Android probe succeeded. " +
            "Returning to normal YouTube playback.");

        //
        // Allow any in-flight token request to finish before stopping
        // the provider. A later bot check increments stateGeneration and
        // prevents this delayed stop.
        //
        _ = StopProviderAfterSuccessfulProbeAsync(
            successfulGeneration);
    }

    /// <summary>
    /// Clears an inconclusive probe without changing compatibility mode.
    /// This is used for failures unrelated to bot verification.
    /// </summary>
    internal void ReportProbeInconclusive()
    {
        lock (syncRoot)
        {
            probeInProgress =
                false;
        }

        AepLog.Info(
            "[YouTube/Policy] Android probe was inconclusive. " +
            "The PO-token provider will remain available.");
    }

    private async Task StopProviderAfterSuccessfulProbeAsync(
        int expectedGeneration)
    {
        try
        {
            await Task.Delay(
                    ProviderStopDelay)
                .ConfigureAwait(false);

            lock (syncRoot)
            {
                if (stateGeneration !=
                        expectedGeneration ||
                    GetCompatibilityUntilUtc() >
                        DateTimeOffset.UtcNow ||
                    probeInProgress)
                {
                    return;
                }
            }

            await poTokenService
                .StopAsync()
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AepLog.Debug(
                "[YouTube/Policy] Delayed provider shutdown failed: " +
                exception.Message);
        }
    }

    private DateTimeOffset GetCompatibilityUntilUtc()
    {
        var value =
            configuration
                .YouTubePoCompatibilityUntilUnixSeconds;

        if (value <= 0)
        {
            return
                DateTimeOffset.MinValue;
        }

        try
        {
            return
                DateTimeOffset.FromUnixTimeSeconds(
                    value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return
                DateTimeOffset.MinValue;
        }
    }
}
