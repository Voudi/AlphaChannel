using System.Diagnostics;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Provides the shared yt-dlp configuration required for automatic
/// YouTube PO-token generation.
///
/// All PO-token-specific paths and yt-dlp arguments live here so this
/// experiment can be removed without affecting the playback engine.
///
/// Expected directory layout beside the active yt-dlp.exe:
///
/// yt-dlp-*/
/// ├─ yt-dlp.exe
/// └─ po-token/
///    ├─ node/
///    │  └─ node.exe
///    ├─ plugins/
///    │  └─ bgutil-ytdlp-pot-provider.zip
///    └─ provider/
///       └─ server/
///          ├─ build/
///          │  └─ main.js
///          └─ node_modules/
///
/// The provider/server directory must already have been prepared with:
///
///     npm ci
///     npx tsc
///
/// This first implementation uses the provider's on-demand script mode.
/// It does not leave a background HTTP server running.
/// </summary>
internal static class YouTubePoTokenSupport
{

    private const string ConfigFileName =
        "alphachannel-pot.conf";

    private const string ProviderPluginFileName =
        "bgutil-ytdlp-pot-provider.zip";

    private static readonly object SyncRoot =
        new();

    private static string? preparedForYtDlp;

    internal static bool IsAvailable(
        Resources resources)
    {
        return TryGetInstallation(
            resources,
            out _);
    }

    /// <summary>
    /// Builds mpv's yt-dlp options for either the normal Android route
    /// or PO-token compatibility mode.
    /// </summary>
    internal static string BuildMpvRawOptions(
    Resources resources,
    bool usePoTokens)
    {
        if (!usePoTokens)
        {
            AepLog.Info(
                "[YouTube/Policy] Using the Android YouTube client.");

            return
                "hls-use-mpegts=," +
                "extractor-args=youtube:player_client=android";
        }

        if (!TryPrepareConfiguration(
                resources,
                out var configPath))
        {
            AepLog.Warning(
                "[YouTube/PO] Provider files were not found. " +
                "Falling back to the Android YouTube client.");

            return
                "hls-use-mpegts=," +
                "extractor-args=youtube:player_client=android";
        }

        AepLog.Info(
            "[YouTube/PO] Using the mweb client with the " +
            "BgUtils PO-token provider.");

        return
            "hls-use-mpegts=," +
            $"config-locations={ToForwardSlashes(configPath)}";
    }

    /// <summary>
    /// Adds the same PO-token configuration to a directly launched
    /// yt-dlp process, such as WebMediaUrlResolver's fallback process.
    ///
    /// Returns false when the provider is unavailable. The caller may
    /// then apply its existing Android fallback.
    /// </summary>
    internal static bool AddProcessArguments(
        Resources resources,
        ProcessStartInfo startInfo)
    {
        if (!TryPrepareConfiguration(
                resources,
                out var configPath))
        {
            return false;
        }

        startInfo.ArgumentList.Add(
            "--config-locations");

        startInfo.ArgumentList.Add(
            configPath);

        return true;
    }

    private static bool TryPrepareConfiguration(
        Resources resources,
        out string configPath)
    {
        configPath =
            string.Empty;

        if (!TryGetInstallation(
                resources,
                out var installation))
        {
            return false;
        }

        configPath =
            installation.ConfigPath;

        lock (SyncRoot)
        {
            if (string.Equals(
                    preparedForYtDlp,
                    installation.YtDlpPath,
                    StringComparison.OrdinalIgnoreCase) &&
                File.Exists(
                    configPath))
            {
                return true;
            }

            try
            {
                Directory.CreateDirectory(
                    installation.RootDirectory);

                var configuration =
                    BuildConfiguration(
                        installation);

                File.WriteAllText(
                    configPath,
                    configuration);

                preparedForYtDlp =
                    installation.YtDlpPath;

                AepLog.Info(
                    $"[YouTube/PO] Prepared yt-dlp configuration: {configPath}");

                return true;
            }
            catch (Exception exception)
            {
                AepLog.Warning(
                    "[YouTube/PO] Could not prepare the provider " +
                    $"configuration: {exception.Message}");

                configPath =
                    string.Empty;

                return false;
            }
        }
    }

    private static string BuildConfiguration(
        Installation installation)
    {
        //
        // These are deliberately separate --extractor-args entries.
        //
        // The first chooses YouTube's mweb client, which supports
        // provider-generated GVS tokens.
        //
        // The second tells the provider where its compiled generation
        // script lives. Keeping these inside a yt-dlp config file avoids
        // mpv's comma-separated ytdl-raw-options syntax corrupting them.
        //
        return string.Join(
            Environment.NewLine,
            [
                $"--plugin-dirs \"{installation.PluginDirectory}\"",
                $"--js-runtimes \"node:{installation.NodePath}\"",
                "--extractor-args \"youtube:player_client=mweb\"",
                "--extractor-args " +
                $"\"youtubepot-bgutilscript:server_home={installation.ServerDirectory}\"",
                string.Empty
            ]);
    }

    private static bool TryGetInstallation(
        Resources resources,
        out Installation installation)
    {
        installation =
            default;

        var ytDlpPath =
            resources.GetLocationYTDLP();

        if (string.IsNullOrWhiteSpace(
                ytDlpPath) ||
            !File.Exists(
                ytDlpPath))
        {
            return false;
        }

        var ytDlpDirectory =
            Path.GetDirectoryName(
                ytDlpPath);

        if (string.IsNullOrWhiteSpace(
                ytDlpDirectory))
        {
            return false;
        }

        //
        // Store the provider in Alpha Channel's stable configuration
        // directory, not inside the versioned yt-dlp-* directory.
        //
        var configurationDirectory =
            Directory.GetParent(
                ytDlpDirectory)?.FullName;

        if (string.IsNullOrWhiteSpace(
                configurationDirectory))
        {
            return false;
        }

        var rootDirectory =
            Path.Combine(
                configurationDirectory,
                "youtube-po-token");

        var nodePath =
            Path.Combine(
                rootDirectory,
                "node",
                "node.exe");

        var pluginDirectory =
            Path.Combine(
                rootDirectory,
                "plugins");

        var pluginPath =
            Path.Combine(
                pluginDirectory,
                ProviderPluginFileName);

        var serverDirectory =
            Path.Combine(
                rootDirectory,
                "provider",
                "server");

        var serverScript =
            Path.Combine(
                serverDirectory,
                "build",
                "main.js");

        var configPath =
            Path.Combine(
                rootDirectory,
                ConfigFileName);

        if (!File.Exists(nodePath))
        {
            AepLog.Debug(
                $"[YouTube/PO] Node was not found: {nodePath}");

            return false;
        }

        if (!File.Exists(pluginPath))
        {
            AepLog.Debug(
                $"[YouTube/PO] Provider plugin was not found: {pluginPath}");

            return false;
        }

        if (!File.Exists(serverScript))
        {
            AepLog.Debug(
                $"[YouTube/PO] Compiled provider script was not found: {serverScript}");

            return false;
        }

        installation =
            new Installation(
                ytDlpPath,
                rootDirectory,
                nodePath,
                pluginDirectory,
                serverDirectory,
                configPath);

        return true;
    }

    private static string ToForwardSlashes(
        string path)
    {
        return path.Replace(
            '\\',
            '/');
    }

    private readonly record struct Installation(
        string YtDlpPath,
        string RootDirectory,
        string NodePath,
        string PluginDirectory,
        string ServerDirectory,
        string ConfigPath);
}