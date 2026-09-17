using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Dalamud.Utility;
using SharpCompress.Archives;
using SharpCompress.Common;
using Newtonsoft.Json.Linq;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Video;

internal sealed class Resources : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _configDir;
    private readonly YouTubePoTokenService _youtubePoTokenService;
    private readonly CancellationTokenSource shutdown = new();
    private readonly ConcurrentDictionary<int, Task> backgroundTasks = new();
    private int nextBackgroundTaskId;
    private volatile bool disposed;

    internal YouTubePlaybackPolicy YouTubePolicy
    {
        get;
    }

    internal string[] MpvCheckResult { get; private set; } = [string.Empty, string.Empty];
    internal string[] YtdlpCheckResult { get; private set; } = [string.Empty, string.Empty];
    private long _ntpTimeOffset;
    private long _sysTimeOffset;

    internal long CurrentTimeNTPNormalizedMilliseconds => _ntpTimeOffset > 0 ? _ntpTimeOffset + (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _sysTimeOffset) : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal string RomsDirectory => Path.Combine(_configDir, "roms");


    internal Resources()
    {
        _httpClient =
            PluginHttpClients.CreateDownloadClient();

        _httpClient.DefaultRequestHeaders.Add(
            "User-Agent",
            "AlphaChannelUpdater/1.0");

        _configDir =
            Plugin.PluginInterface
                .ConfigDirectory
                .FullName;

        _youtubePoTokenService =
            new YouTubePoTokenService();

        YouTubePolicy =
            new YouTubePlaybackPolicy(
                Plugin.Cfg,
                _youtubePoTokenService);



        Initialize();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        shutdown.Cancel();
        _httpClient.Dispose();

        try
        {
            Task.WaitAll(
                backgroundTasks.Values.ToArray(),
                TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            AepLog.Debug(
                $"[Resources] Background cleanup warning: {exception.Message}");
        }

        backgroundTasks.Clear();
        _youtubePoTokenService.Dispose();
        NativeLoader.Release(this);
        shutdown.Dispose();

        GC.SuppressFinalize(this);
    }

    private void Track(Task task)
    {
        if (disposed)
        {
            _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously |
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return;
        }

        var id =
            Interlocked.Increment(ref nextBackgroundTaskId);

        backgroundTasks[id] = task;
        _ = task.ContinueWith(
            completedTask => backgroundTasks.TryRemove(id, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Initialize()
    {


        if (!Directory.Exists(Path.Combine(_configDir, "roms")))
        {
            Directory.CreateDirectory(Path.Combine(_configDir, "roms"));
        }
        Track(GetNtpUtcAsync().ContinueWith(task =>
        {
            //Set NTP time
            if (task.IsCompletedSuccessfully)
            {
                _ntpTimeOffset = task.GetResultSafely();
            }
            _sysTimeOffset = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }).ContinueWith(_ =>
        {
            //Check for MPV Updates, then auto-download in the background if one was found - a
            //tester never has to visit Settings at all for mpv to become ready. The Settings
            //page's own button (see AetherStreamApp.Settings.cs) stays as a manual fallback for
            //when this attempt hits a network hiccup at plugin load.
            Track(CheckMPVAsync().ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    AepLog.Error("Failed to check for MPV updates: " + task.Exception?.ToString());
                    return;
                }

                if (MpvCheckResult[0].Length > 0)
                {
                    Track(DownloadMPVAsync());
                }
            }));
        }).ContinueWith(_ =>
        {
            //Check for YTDLP Updates - same auto-download reasoning as the MPV check above.
            Track(CheckYTDLPAsync().ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    AepLog.Error("Failed to check for YTDLP updates: " + task.Exception?.ToString());
                    return;
                }

                if (YtdlpCheckResult[0].Length > 0)
                {
                    Track(DownloadYTDLPAsync());
                }
            }));
        }));

        // SNES9x is downloaded lazily in the background if it is
        // not already installed. This keeps the native core out of
        // the plugin package itself.
        if (GetLocationSNES9X() is null)
        {
            Track(Task.Run(async () =>
            {
                AepLog.Info(
                    "[SNES9X] Core not found; downloading libretro core.");

                var installed = await DownloadSNES9XAsync();

                if (installed)
                {
                    AepLog.Info(
                        "[SNES9X] Core downloaded successfully.");
                }
                else
                {
                    AepLog.Warning(
                        "[SNES9X] Core download failed.");
                }
            }, shutdown.Token));
        }


        //
        // Game Boy / Game Boy Color - Gambatte libretro core.
        //
        // Like SNES9x, this is installed lazily outside the plugin package.
        //

        if (GetLocationGambatte() is null)
        {
            Track(Task.Run(async () =>
            {
                AepLog.Info(
                    "[GAMBATTE] Core not found; downloading libretro core.");

                var installed =
                    await DownloadGambatteAsync();

                if (installed)
                {
                    AepLog.Info(
                        "[GAMBATTE] Core downloaded successfully.");
                }
                else
                {
                    AepLog.Warning(
                        "[GAMBATTE] Core download failed.");
                }
            }, shutdown.Token));
        }

        // Nintendo Entertainment System - Nestopia libretro core.
        if (GetLocationNestopia() is null)
        {
            Track(Task.Run(async () =>
            {
                AepLog.Info("[NESTOPIA] Core not found; downloading libretro core.");
                var installed = await DownloadNestopiaAsync();
                if (installed) AepLog.Info("[NESTOPIA] Core downloaded successfully.");
                else AepLog.Warning("[NESTOPIA] Core download failed.");
            }, shutdown.Token));
        }

        // Game Boy Advance - mGBA libretro core.
        if (GetLocationMgba() is null)
        {
            Track(Task.Run(async () =>
            {
                AepLog.Info("[MGBA] Core not found; downloading libretro core.");
                var installed = await DownloadMgbaAsync();
                if (installed) AepLog.Info("[MGBA] Core downloaded successfully.");
                else AepLog.Warning("[MGBA] Core download failed.");
            }, shutdown.Token));
        }

        // Sega Master System / SG-1000 - Gearsystem libretro core.
        if (GetLocationGearsystem() is null)
        {
            Track(Task.Run(async () =>
            {
                AepLog.Info("[GEARSYSTEM] Core not found; downloading libretro core.");
                var installed = await DownloadGearsystemAsync();
                if (installed) AepLog.Info("[GEARSYSTEM] Core downloaded successfully.");
                else AepLog.Warning("[GEARSYSTEM] Core download failed.");
            }, shutdown.Token));
        }


        //
        // FFmpeg - used by Alpha Channel for local game broadcasting.
        //
        // Installed lazily outside the plugin package, just like the
        // emulator cores.
        //

        var ffmpegLocation =
    GetLocationFFmpeg();

        if (ffmpegLocation is null)
        {
            AepLog.Warning(
                "[FFMPEG] ffmpeg.exe NOT FOUND. Starting download.");

            Track(Task.Run(async () =>
            {
                var installed =
                    await DownloadFFmpegAsync();

                var installedLocation =
                    GetLocationFFmpeg();

                if (installed &&
       installedLocation is not null)
                {
                    AepLog.Info(
                        $"[FFMPEG] READY - ffmpeg.exe exists at: {installedLocation}");

                    TestFFmpeg();
                }
                else
                {
                    AepLog.Error(
                        "[FFMPEG] FAILED - ffmpeg.exe still does not exist after download.");
                }
            }, shutdown.Token));
        }
        else
        {
            AepLog.Info(
                $"[FFMPEG] READY - ffmpeg.exe exists at: {ffmpegLocation}");

            TestFFmpeg();
        }

    }

    



	internal string? GetLocationMPV()
    {
        const string filenameStart = "mpv-dev-lgpl-x86_64-";
        // Prefer the newest extracted build (name embeds the GitHub release id). Leaving older
        // folders behind is intentional when libmpv is still loaded and can't be deleted yet.
        foreach (var dir in Directory.GetDirectories(_configDir, $"{filenameStart}*")
                     .OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var dll = Path.Combine(dir, "libmpv-2.dll");
            if (File.Exists(dll))
            {
                return dll;
            }
        }

        return null;
    }

    internal string? GetLocationYTDLP()
    {
        const string filenameStart = "yt-dlp";

        foreach (var dir in Directory
                     .GetDirectories(_configDir, $"{filenameStart}*")
                     .OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var exe = Path.Combine(dir, "yt-dlp.exe");

            if (File.Exists(exe))
            {
                AepLog.Info($"[YTDLP] Using executable: {exe}");
                return exe;
            }
        }

        return null;
    }

    internal string? GetLocationSNES9X()
    {
        string directoryName = "snes9x";
        string? dir = Directory.GetDirectories(_configDir, $"{directoryName}*").FirstOrDefault();
        if (dir != null)
        {
            string file = Path.Combine(_configDir, directoryName, "snes9x_libretro.dll");
            if (File.Exists(file))
            {
                return file;
            }
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(_configDir, "snes9x"));
        }

        return null;
    }

    internal string? GetLocationGambatte()
    {
        const string directoryName =
            "gambatte";

        var directory =
            Path.Combine(
                _configDir,
                directoryName);

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(
                directory);

            return null;
        }

        var file =
            Path.Combine(
                directory,
                "gambatte_libretro.dll");

        return File.Exists(file)
            ? file
            : null;
    }

    internal string? GetLocationNestopia()
    {
        const string directoryName = "nestopia";
        var directory = Path.Combine(_configDir, directoryName);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            return null;
        }

        var file = Path.Combine(directory, "nestopia_libretro.dll");
        return File.Exists(file) ? file : null;
    }

    internal string? GetLocationMgba()
    {
        const string directoryName = "mgba";
        var directory = Path.Combine(_configDir, directoryName);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            return null;
        }

        var file = Path.Combine(directory, "mgba_libretro.dll");
        return File.Exists(file) ? file : null;
    }

    internal string? GetLocationGearsystem()
    {
        const string directoryName = "gearsystem";
        var directory = Path.Combine(_configDir, directoryName);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            return null;
        }

        var file = Path.Combine(directory, "gearsystem_libretro.dll");
        return File.Exists(file) ? file : null;
    }


    internal string? GetLocationFFmpeg()
    {
        const string directoryName =
            "ffmpeg";

        var directory =
            Path.Combine(
                _configDir,
                directoryName);

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(
                directory);

            return null;
        }

        var file =
            Path.Combine(
                directory,
                "ffmpeg.exe");

        return File.Exists(file)
            ? file
            : null;
    }


    internal void TestFFmpeg()
    {
        var ffmpeg =
            GetLocationFFmpeg();

        if (ffmpeg is null)
        {
            AepLog.Error(
                "[FFMPEG] TEST FAILED - ffmpeg.exe was not found.");

            return;
        }

        try
        {
            var startInfo =
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = "-version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

            using var process =
                new System.Diagnostics.Process
                {
                    StartInfo = startInfo
                };

            AepLog.Info(
                $"[FFMPEG] TEST - launching: {ffmpeg}");

            if (!process.Start())
            {
                AepLog.Error(
                    "[FFMPEG] TEST FAILED - Process.Start returned false.");

                return;
            }

            var stdout =
                process.StandardOutput.ReadToEnd();

            var stderr =
                process.StandardError.ReadToEnd();

            process.WaitForExit(
                5000);

            if (!process.HasExited)
            {
                try
                {
                    process.Kill(
                        entireProcessTree: true);
                }
                catch
                {
                    // Best-effort cleanup.
                }

                AepLog.Error(
                    "[FFMPEG] TEST FAILED - process did not exit within 5 seconds.");

                return;
            }

            var firstLine =
                stdout
                    .Split(
                        ['\r', '\n'],
                        StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();

            if (process.ExitCode == 0 &&
                !string.IsNullOrWhiteSpace(firstLine))
            {
                AepLog.Info(
                    $"[FFMPEG] TEST PASSED - {firstLine}");
            }
            else
            {
                AepLog.Error(
                    $"[FFMPEG] TEST FAILED - exit code {process.ExitCode}. " +
                    $"stderr: {stderr}");
            }
        }
        catch (Exception exception)
        {
            AepLog.Error(
                $"[FFMPEG] TEST FAILED - could not launch ffmpeg.exe: {exception}");
        }
    }

    internal async Task CheckMPVAsync()
    {
        string filenameStart = "mpv-dev-lgpl-x86_64-";
        string filenameEnd = ".7z";
        string url = "https://api.github.com/repos/zhongfly/mpv-winbuild/releases/latest";
        MpvCheckResult = await CheckForUpdateAsync(_configDir, filenameStart, filenameEnd, url);
    }
    internal async Task CheckYTDLPAsync()
    {
        // The 32-bit yt-dlp_x86.exe build saves ~4MB but fails to spawn as a subprocess ("Subprocess
        // failed: init" from mpv's ytdl_hook) under at least one real Wine setup this was tested on -
        // spawning a 32-bit child process from inside a Wine-hosted 64-bit game needs WoW64 support
        // that isn't guaranteed to be present. Not worth the risk for 4MB; use the 64-bit build.
        string filenameStart = "yt-dlp.exe";
        string filenameEnd = ".exe";
        string url = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
        YtdlpCheckResult = await CheckForUpdateAsync(_configDir, filenameStart, filenameEnd, url);
    }
    // downloadURL is empty either because CheckForUpdateAsync already found the local folder up
    // to date, or because the check itself failed (rate limit, no network yet at plugin load) and
    // fell back to its empty-result default - either way there is nothing to fetch, and calling
    // HttpClient.GetAsync with an empty URI throws. Callers (AetherStreamApp.Settings) already
    // re-run the check first when this is empty, but this guard stays as the actual line that
    // can never hand HttpClient an invalid request.
    internal async Task<bool> DownloadMPVAsync()
    {
        string filenameStart = "mpv-dev-lgpl-x86_64-";
        string filenameEnd = ".7z";
        string downloadURL = MpvCheckResult[0];
        string folderName = MpvCheckResult[1];
        if (downloadURL.Length == 0)
        {
            return false;
        }

        return await UpdateAsync(_configDir, filenameStart, filenameEnd, downloadURL, folderName);
    }
    internal async Task<bool> DownloadYTDLPAsync()
    {
        string filenameStart = "yt-dlp";
        string filenameEnd = ".exe";
        string downloadURL = YtdlpCheckResult[0];
        string folderName = YtdlpCheckResult[1];
        if (downloadURL.Length == 0)
        {
            return false;
        }

        return await UpdateAsync(_configDir, filenameStart, filenameEnd, downloadURL, folderName);
    }
    private async Task<string[]> CheckForUpdateAsync(string configDir, string nameStartsWith, string nameEndsWith, string checkURL)
    {
        try {
            string json = await _httpClient.GetStringAsync(checkURL, shutdown.Token);
            var doc = JObject.Parse(json);
            long remoteId = doc["id"]!.Value<long>();
            var asset = doc["assets"]!
                .First(a => a["name"]!.Value<string>()!
                    .StartsWith(nameStartsWith, StringComparison.Ordinal) &&
                    a["name"]!.Value<string>()!.EndsWith(nameEndsWith, StringComparison.Ordinal));

            string assetName = asset["name"]!.Value<string>()!;
            string folderName = assetName.Replace(nameEndsWith, "") + "_" + remoteId;

            string localFolder = Path.Combine(configDir, folderName);

            if (Directory.Exists(localFolder))
            {
                return [string.Empty, folderName]; //Already up to date
            }

            string downloadURL = asset["browser_download_url"]!.Value<string>()!;
            AepLog.Info("Found Update: " + downloadURL);
            return [downloadURL, folderName];
        }
        catch (Exception exception)
        {
            AepLog.Warning("Failed to check for update (" + checkURL + "): " + exception);
            return [string.Empty, string.Empty];
        }
    }

    private async Task<bool> UpdateAsync(string configDir, string nameStartsWith, string nameEndsWith, string downloadURL, string folderName)
    {
        string? tempFile = null;
        string? extractFolder = null;

        try
        {
            AepLog.Info("Downloading Update: " + downloadURL);
            tempFile = Path.Combine(
                Path.GetTempPath(),
                $"alphachannel-update-{Guid.NewGuid():N}{nameEndsWith}");
            using var response = await _httpClient.GetAsync(
                downloadURL,
                HttpCompletionOption.ResponseHeadersRead,
                shutdown.Token);
            await using (var fs = File.OpenWrite(tempFile))
            {
                await response.Content.CopyToAsync(fs, shutdown.Token);
            }
            AepLog.Info("Finished Downloading " + downloadURL);
            if (nameEndsWith == ".7z")
            {
                string targetFolder = Path.Combine(configDir, folderName);
                if (Directory.Exists(targetFolder))
                {
                    TryDeleteOldVersionFolders(configDir, nameStartsWith, keepFolder: targetFolder);
                    return true;
                }

                // Extract into a temp dir, then rename into place — never delete the currently-loaded
                // libmpv folder first (that throws Access Denied under Wine while the DLL is mapped).
                extractFolder = Path.Combine(configDir, Path.GetRandomFileName());
                Directory.CreateDirectory(extractFolder);
                using (var archive = ArchiveFactory.OpenArchive(tempFile))
                {
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        shutdown.Token.ThrowIfCancellationRequested();
                        entry.WriteToDirectory(extractFolder, new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                    }
                }

                Directory.Move(extractFolder, targetFolder);
                extractFolder = null;
                TryDeleteOldVersionFolders(configDir, nameStartsWith, keepFolder: targetFolder);
            }
            else
            {
                string localFolder = Path.Combine(configDir, folderName);
                Directory.CreateDirectory(localFolder);

                string targetPath = Path.Combine(localFolder, nameStartsWith.EndsWith(nameEndsWith, StringComparison.Ordinal) ? nameStartsWith : nameStartsWith + nameEndsWith);
                File.Copy(tempFile, targetPath, overwrite: true);
                TryDeleteOldVersionFolders(configDir, nameStartsWith, keepFolder: localFolder);
            }
            return true;
        }
        catch (Exception e)
        {
            AepLog.Error($"Error updating {nameStartsWith}: {e.Message}");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempFile) && File.Exists(tempFile))
            {
                try { File.Delete(tempFile); }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(extractFolder) && Directory.Exists(extractFolder))
            {
                try { Directory.Delete(extractFolder, recursive: true); }
                catch { }
            }
        }
    }

    // Best-effort cleanup. libmpv-2.dll stays mapped for the whole process lifetime, so the folder
    // that supplied the current handle often can't be removed until the next full game restart —
    // leave it and prefer the newest folder in GetLocationMPV instead of failing the whole update.
    private static void TryDeleteOldVersionFolders(string configDir, string nameStartsWith, string keepFolder)
    {
        foreach (string dir in Directory.GetDirectories(configDir, $"{nameStartsWith}*"))
        {
            if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(keepFolder), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AepLog.Warning($"Leaving old {nameStartsWith} folder in place (still in use): {Path.GetFileName(dir)}");
            }
        }
    }

    internal async Task<bool> DownloadSNES9XAsync()
    {
        const string downloadUrl =
            "https://buildbot.libretro.com/nightly/windows/x86_64/latest/snes9x_libretro.dll.zip";

        string directoryName = "snes9x";
        string? temp = null;

        try
        {
            AepLog.Info(
                $"[SNES9X] Download starting: {downloadUrl}");

            temp = Path.Combine(
                Path.GetTempPath(),
                $"alphachannel-snes9x-{Guid.NewGuid():N}.zip");

            using var response = await _httpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                shutdown.Token);

            AepLog.Info(
                $"[SNES9X] HTTP response: {(int)response.StatusCode} {response.StatusCode}");

            response.EnsureSuccessStatusCode();

            await using (var fs = File.Create(temp))
            {
                await response.Content.CopyToAsync(fs, shutdown.Token);
            }

            AepLog.Info(
                $"[SNES9X] Downloaded archive to: {temp}");

            string localFolder =
                Path.Combine(_configDir, directoryName);

            Directory.CreateDirectory(localFolder);

            using (var archive = ArchiveFactory.OpenArchive(temp))
            {
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    AepLog.Info(
                        $"[SNES9X] Extracting: {entry.Key}");

                    entry.WriteToDirectory(
                        localFolder,
                        new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                }
            }

            string expectedDll =
                Path.Combine(
                    localFolder,
                    "snes9x_libretro.dll");

            if (!File.Exists(expectedDll))
            {
                AepLog.Error(
                    $"[SNES9X] Download completed but DLL was not found at: {expectedDll}");

                return false;
            }

            AepLog.Info(
                $"[SNES9X] Core installed at: {expectedDll}");

            return true;
        }
        catch (Exception exception)
        {
            AepLog.Error(
                $"[SNES9X] Download/install exception: {exception}");

            return false;
        }
        finally
        {
            if (temp is not null && File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // Temporary cleanup failure is harmless.
                }
            }
        }
    }

    internal async Task<bool> DownloadGambatteAsync()
    {
        const string downloadUrl =
            "https://buildbot.libretro.com/nightly/windows/x86_64/latest/gambatte_libretro.dll.zip";

        const string directoryName =
            "gambatte";

        string? temp =
            null;

        try
        {
            AepLog.Info(
                $"[GAMBATTE] Download starting: {downloadUrl}");

            temp =
                Path.Combine(
                    Path.GetTempPath(),
                    $"alphachannel-gambatte-{Guid.NewGuid():N}.zip");

            using var response =
                await _httpClient.GetAsync(
                    downloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    shutdown.Token);

            AepLog.Info(
                $"[GAMBATTE] HTTP response: {(int)response.StatusCode} {response.StatusCode}");

            response.EnsureSuccessStatusCode();

            await using (var fs =
                         File.Create(temp))
            {
                await response.Content
                    .CopyToAsync(fs, shutdown.Token);
            }

            var localFolder =
                Path.Combine(
                    _configDir,
                    directoryName);

            Directory.CreateDirectory(
                localFolder);

            using (var archive =
                   ArchiveFactory.OpenArchive(temp))
            {
                foreach (var entry in
                         archive.Entries.Where(
                             entry => !entry.IsDirectory))
                {
                    AepLog.Info(
                        $"[GAMBATTE] Extracting: {entry.Key}");

                    entry.WriteToDirectory(
                        localFolder,
                        new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                }
            }

            var expectedDll =
                Path.Combine(
                    localFolder,
                    "gambatte_libretro.dll");

            if (!File.Exists(expectedDll))
            {
                AepLog.Error(
                    $"[GAMBATTE] Download completed but DLL was not found at: {expectedDll}");

                return false;
            }

            AepLog.Info(
                $"[GAMBATTE] Core installed at: {expectedDll}");

            return true;
        }
        catch (Exception exception)
        {
            AepLog.Error(
                $"[GAMBATTE] Core download failed: {exception}");

            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temp) &&
                File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }
    }

    internal async Task<bool> DownloadNestopiaAsync()
    {
        const string downloadUrl =
            "https://buildbot.libretro.com/nightly/windows/x86_64/latest/nestopia_libretro.dll.zip";
        const string directoryName = "nestopia";
        string? temp = null;

        try
        {
            AepLog.Info($"[NESTOPIA] Download starting: {downloadUrl}");
            temp = Path.Combine(Path.GetTempPath(), $"alphachannel-nestopia-{Guid.NewGuid():N}.zip");
            using var response = await _httpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                shutdown.Token);
            AepLog.Info($"[NESTOPIA] HTTP response: {(int)response.StatusCode} {response.StatusCode}");
            response.EnsureSuccessStatusCode();
            await using (var fs = File.Create(temp))
                await response.Content.CopyToAsync(fs, shutdown.Token);

            var localFolder = Path.Combine(_configDir, directoryName);
            Directory.CreateDirectory(localFolder);
            using (var archive = ArchiveFactory.OpenArchive(temp))
            {
                foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                    entry.WriteToDirectory(localFolder, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            }

            var expectedDll = Path.Combine(localFolder, "nestopia_libretro.dll");
            if (!File.Exists(expectedDll))
            {
                AepLog.Error($"[NESTOPIA] Download completed but DLL was not found at: {expectedDll}");
                return false;
            }

            AepLog.Info($"[NESTOPIA] Core installed at: {expectedDll}");
            return true;
        }
        catch (Exception exception)
        {
            AepLog.Error($"[NESTOPIA] Core download failed: {exception}");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temp) && File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch { }
            }
        }
    }

    internal async Task<bool> DownloadMgbaAsync()
    {
        const string downloadUrl =
            "https://buildbot.libretro.com/nightly/windows/x86_64/latest/mgba_libretro.dll.zip";
        const string directoryName = "mgba";
        string? temp = null;

        try
        {
            AepLog.Info($"[MGBA] Download starting: {downloadUrl}");
            temp = Path.Combine(Path.GetTempPath(), $"alphachannel-mgba-{Guid.NewGuid():N}.zip");
            using var response = await _httpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                shutdown.Token);
            AepLog.Info($"[MGBA] HTTP response: {(int)response.StatusCode} {response.StatusCode}");
            response.EnsureSuccessStatusCode();
            await using (var fs = File.Create(temp))
                await response.Content.CopyToAsync(fs, shutdown.Token);

            var localFolder = Path.Combine(_configDir, directoryName);
            Directory.CreateDirectory(localFolder);
            using (var archive = ArchiveFactory.OpenArchive(temp))
            {
                foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                    entry.WriteToDirectory(localFolder, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            }

            var expectedDll = Path.Combine(localFolder, "mgba_libretro.dll");
            if (!File.Exists(expectedDll))
            {
                AepLog.Error($"[MGBA] Download completed but DLL was not found at: {expectedDll}");
                return false;
            }

            AepLog.Info($"[MGBA] Core installed at: {expectedDll}");
            return true;
        }
        catch (Exception exception)
        {
            AepLog.Error($"[MGBA] Core download failed: {exception}");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temp) && File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch { }
            }
        }
    }

    internal async Task<bool> DownloadGearsystemAsync()
    {
        const string downloadUrl =
            "https://buildbot.libretro.com/nightly/windows/x86_64/latest/gearsystem_libretro.dll.zip";
        const string directoryName = "gearsystem";
        string? temp = null;

        try
        {
            AepLog.Info($"[GEARSYSTEM] Download starting: {downloadUrl}");
            temp = Path.Combine(Path.GetTempPath(), $"alphachannel-gearsystem-{Guid.NewGuid():N}.zip");
            using var response = await _httpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                shutdown.Token);
            AepLog.Info($"[GEARSYSTEM] HTTP response: {(int)response.StatusCode} {response.StatusCode}");
            response.EnsureSuccessStatusCode();
            await using (var fs = File.Create(temp))
                await response.Content.CopyToAsync(fs, shutdown.Token);

            var localFolder = Path.Combine(_configDir, directoryName);
            Directory.CreateDirectory(localFolder);
            using (var archive = ArchiveFactory.OpenArchive(temp))
            {
                foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                    entry.WriteToDirectory(localFolder,
                        new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            }

            var expectedDll = Path.Combine(localFolder, "gearsystem_libretro.dll");
            if (!File.Exists(expectedDll))
            {
                AepLog.Error($"[GEARSYSTEM] Download completed but DLL was not found at: {expectedDll}");
                return false;
            }

            AepLog.Info($"[GEARSYSTEM] Core installed at: {expectedDll}");
            return true;
        }
        catch (Exception exception)
        {
            AepLog.Error($"[GEARSYSTEM] Core download failed: {exception}");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temp) && File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch { }
            }
        }
    }

    internal async Task<bool> DownloadFFmpegAsync()
    {
        //
        // BtbN provides current Windows x64 FFmpeg builds.
        //
        // Use the LGPL static build so ffmpeg.exe is self-contained.
        // This avoids needing to install the shared FFmpeg DLLs beside
        // the executable.
        //
        const string downloadUrl =
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip";

        const string directoryName =
            "ffmpeg";

        string? temp =
            null;

        string? extractDirectory =
            null;

        try
        {
            AepLog.Info(
                $"[FFMPEG] Download starting: {downloadUrl}");

            temp =
                Path.Combine(
                    Path.GetTempPath(),
                    $"alphachannel-ffmpeg-{Guid.NewGuid():N}.zip");

            extractDirectory =
                Path.Combine(
                    Path.GetTempPath(),
                    $"alphachannel-ffmpeg-extract-{Guid.NewGuid():N}");

            using var response =
                await _httpClient.GetAsync(
                    downloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    shutdown.Token);

            AepLog.Info(
                $"[FFMPEG] HTTP response: {(int)response.StatusCode} {response.StatusCode}");

            response.EnsureSuccessStatusCode();

            await using (var fs =
                         File.Create(temp))
            {
                await response.Content
                    .CopyToAsync(fs, shutdown.Token);
            }

            Directory.CreateDirectory(
                extractDirectory);

            using (var archive =
                   ArchiveFactory.OpenArchive(temp))
            {
                foreach (var entry in
                         archive.Entries.Where(
                             entry => !entry.IsDirectory))
                {
                    entry.WriteToDirectory(
                        extractDirectory,
                        new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                }
            }

            //
            // The downloaded archive contains a versioned root directory,
            // so locate ffmpeg.exe rather than depending on that directory
            // name remaining stable.
            //

            var ffmpegExe =
                Directory.GetFiles(
                        extractDirectory,
                        "ffmpeg.exe",
                        SearchOption.AllDirectories)
                    .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(ffmpegExe) ||
                !File.Exists(ffmpegExe))
            {
                AepLog.Error(
                    "[FFMPEG] Download completed but ffmpeg.exe was not found in the archive.");

                return false;
            }

            var localFolder =
                Path.Combine(
                    _configDir,
                    directoryName);

            Directory.CreateDirectory(
                localFolder);

            var target =
                Path.Combine(
                    localFolder,
                    "ffmpeg.exe");

            File.Copy(
                ffmpegExe,
                target,
                overwrite: true);

            if (!File.Exists(target))
            {
                AepLog.Error(
                    $"[FFMPEG] Install failed; executable was not found at: {target}");

                return false;
            }

            AepLog.Info(
                $"[FFMPEG] Installed at: {target}");

            return true;
        }
        catch (Exception exception)
        {
            AepLog.Error(
                $"[FFMPEG] Download/install failed: {exception}");

            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temp) &&
                File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }

            if (!string.IsNullOrWhiteSpace(extractDirectory) &&
                Directory.Exists(extractDirectory))
            {
                try
                {
                    Directory.Delete(
                        extractDirectory,
                        recursive: true);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }
    }

    private async Task<long> GetNtpUtcAsync(string server = "pool.ntp.org")
    {
        try
        {
            byte[] ntpData = new byte[48];
            ntpData[0] = 0x1B;

            var addresses = await Dns.GetHostAddressesAsync(server, shutdown.Token);
            var ep = new IPEndPoint(addresses[0], 123);

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.ReceiveTimeout = 3000;
            await socket.ConnectAsync(ep, shutdown.Token);
            await socket.SendAsync(ntpData, SocketFlags.None, shutdown.Token);
            await socket.ReceiveAsync(ntpData, SocketFlags.None, shutdown.Token);

            ulong intPart = ((ulong)ntpData[40] << 24) | ((ulong)ntpData[41] << 16) | ((ulong)ntpData[42] << 8) | ntpData[43];
            ulong fracPart = ((ulong)ntpData[44] << 24) | ((ulong)ntpData[45] << 16) | ((ulong)ntpData[46] << 8) | ntpData[47];
            ulong ms = intPart * 1000 + fracPart * 1000 / 0x100000000L;
            var dto = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds((long)ms);
            return dto.ToUnixTimeMilliseconds();
        }
        catch
        {
            return 0;
        }
    }

	internal static class NativeLoader
	{
		private static Resources? _resources;
		private static bool _registered;

		internal static void Register(Resources resources)
		{
			_resources = resources;
			if (_registered)
			{
				return;
			}

			_registered = true;
			NativeLibrary.SetDllImportResolver(typeof(NativeLoader).Assembly, Resolve);
		}

        internal static void Release(Resources resources)
        {
            if (ReferenceEquals(_resources, resources))
            {
                _resources = null;
            }
        }

		private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
		{
			switch (name)
			{
				case "libmpv-2":
					// Queried fresh rather than cached at startup - mpv-winbuild may still be
					// downloading (see CheckMPVAsync/DownloadMPVAsync) the first time this
					// resolves.
					return TryLoad(_resources?.GetLocationMPV(), "MPV");
				default:
					return IntPtr.Zero;
			}
		}

		private static IntPtr TryLoad(string? location, string tag)
		{
			if (location != null && NativeLibrary.TryLoad(location, out nint handle))
			{
				return handle;
			}
			AepLog.Error($"[{tag}] Failed to load native lib from: {location}");
			return IntPtr.Zero;
		}
	}
}
