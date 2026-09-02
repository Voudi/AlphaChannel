using System.Diagnostics;
using System.Text;

namespace AlphaChannel.Plugin.Video;

// Wine yt-dlp.exe cannot decrypt Linux Chromium cookies (Opera GX, Chrome, …).
// Native Linux yt-dlp/python can. Copy them to a Netscape cookies.txt and hand
// that file to the Windows yt-dlp that mpv actually runs.
internal static class YouTubeCookieExport
{
    private const string CookiesFileName = "youtube-cookies.txt";
    private const string ScriptFileName = "export_yt_cookies.py";

    internal static volatile bool Busy;
    internal static volatile string? Status;
    internal static volatile string? LastError;

    internal static bool LooksSignedIn(string? cookiesPath)
    {
        if (string.IsNullOrWhiteSpace(cookiesPath) || !File.Exists(cookiesPath))
        {
            return false;
        }

        try
        {
            foreach (var line in File.ReadLines(cookiesPath))
            {
                if (line.Contains(".youtube.com", StringComparison.OrdinalIgnoreCase) &&
                    (line.Contains("\tLOGIN_INFO\t", StringComparison.Ordinal) ||
                     line.Contains("\tSID\t", StringComparison.Ordinal) ||
                     line.Contains("\tSAPISID\t", StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[YouTube] Could not read cookies file: {exception.Message}");
        }

        return false;
    }

    internal static bool NeedsNativeExport() =>
        File.Exists(@"Z:\usr\bin\python3") ||
        File.Exists(@"Z:\usr\bin\python3.14") ||
        File.Exists(@"Z:\usr\bin\yt-dlp");

    internal static void ApplyToPlayer(Configuration cfg, VideoPlayer video)
    {
        if (LooksSignedIn(cfg.YouTubeCookiesPath))
        {
            video.CookiesPath = YouTubeBrowserCookies.ToYtDlpPath(cfg.YouTubeCookiesPath!);
            video.CookiesBrowser = null;
            video.CookiesBrowserProfile = null;
            return;
        }

        video.CookiesPath = cfg.YouTubeCookiesPath;
        video.CookiesBrowser = cfg.YouTubeCookiesBrowser;
        video.CookiesBrowserProfile = cfg.YouTubeCookiesProfilePath;
    }

    internal static void DeleteManagedFile()
    {
        try
        {
            var path = ManagedCookiesWinePath();
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[YouTube] Could not delete cookies file: {exception.Message}");
        }
    }

    internal static void Ensure(Configuration cfg, VideoPlayer video)
    {
        if (Busy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(cfg.YouTubeCookiesBrowser) ||
            string.IsNullOrWhiteSpace(cfg.YouTubeCookiesProfilePath))
        {
            ApplyToPlayer(cfg, video);
            return;
        }

        if (!NeedsNativeExport())
        {
            ApplyToPlayer(cfg, video);
            return;
        }

        if (LooksSignedIn(cfg.YouTubeCookiesPath) &&
            File.GetLastWriteTimeUtc(cfg.YouTubeCookiesPath!) > DateTime.UtcNow.AddHours(-12))
        {
            ApplyToPlayer(cfg, video);
            return;
        }

        var profile = YouTubeBrowserCookies.Find(cfg.YouTubeCookiesBrowser, cfg.YouTubeCookiesProfilePath);
        Export(cfg, video, profile?.BrowserId ?? cfg.YouTubeCookiesBrowser!, profile?.ProfilePath ?? cfg.YouTubeCookiesProfilePath);
    }

    internal static void Export(Configuration cfg, VideoPlayer video, string browserId, string profilePath)
    {
        Busy = true;
        LastError = null;
        Status = "Copying YouTube cookies from your browser…";

        try
        {
            var unixHome = FindUnixHome();
            if (unixHome is null)
            {
                throw new InvalidOperationException("Could not find your Linux home directory from Wine.");
            }

            var python = FindUnixPython();
            if (python is null)
            {
                throw new InvalidOperationException(
                    "Install yt-dlp on Linux (`/usr/bin/yt-dlp`) so AlphaChannel can copy browser cookies. Wine cannot decrypt Opera/Chrome cookies itself.");
            }

            var destDir = UnixJoin(unixHome, ".local", "share", "alphachannel-dev");
            Directory.CreateDirectory(YouTubeBrowserCookies.ToYtDlpPath(destDir));

            var unixScript = UnixJoin(destDir, ScriptFileName);
            var unixCookies = UnixJoin(destDir, CookiesFileName);
            var unixStatus = UnixJoin(destDir, "youtube-cookies.status");
            var unixProfile = YouTubeBrowserCookies.ToUnixPath(profilePath);

            File.WriteAllText(
                YouTubeBrowserCookies.ToYtDlpPath(unixScript),
                ExportScript,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            TryDelete(YouTubeBrowserCookies.ToYtDlpPath(unixStatus));
            TryDelete(YouTubeBrowserCookies.ToYtDlpPath(unixCookies));

            RunUnix(python, [unixScript, browserId, unixProfile, unixCookies, unixStatus]);

            var statusWine = YouTubeBrowserCookies.ToYtDlpPath(unixStatus);
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!File.Exists(statusWine) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(150);
            }

            if (!File.Exists(statusWine))
            {
                throw new InvalidOperationException(
                    "Timed out copying cookies. Close the browser and try again, or use Advanced: cookies.txt.");
            }

            var statusText = File.ReadAllText(statusWine);
            if (!statusText.StartsWith("ok", StringComparison.Ordinal))
            {
                var detail = statusText.Replace("err\n", "", StringComparison.Ordinal).Trim();
                if (detail.Length > 280)
                {
                    detail = detail[..280];
                }

                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(detail)
                        ? "Could not read cookies from that browser."
                        : detail);
            }

            var wineCookies = YouTubeBrowserCookies.ToYtDlpPath(unixCookies);
            if (!LooksSignedIn(wineCookies))
            {
                throw new InvalidOperationException(
                    "That browser has no YouTube login yet. Sign in on youtube.com, then click Use again.");
            }

            cfg.YouTubeCookiesPath = wineCookies;
            cfg.Save();
            ApplyToPlayer(cfg, video);
            Status = null;
            AepLog.Info("[YouTube] Copied browser cookies for playback.");
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Status = null;
            AepLog.Warning($"[YouTube] Cookie export failed: {exception.Message}");
        }
        finally
        {
            Busy = false;
        }
    }

    private static string? ManagedCookiesWinePath()
    {
        var home = FindUnixHome();
        return home is null
            ? null
            : YouTubeBrowserCookies.ToYtDlpPath(UnixJoin(home, ".local", "share", "alphachannel-dev", CookiesFileName));
    }

    private static string? FindUnixHome()
    {
        if (Directory.Exists(@"Z:\home"))
        {
            foreach (var dir in Directory.GetDirectories(@"Z:\home"))
            {
                if (Directory.Exists(Path.Combine(dir, ".var")) ||
                    Directory.Exists(Path.Combine(dir, ".local")))
                {
                    return YouTubeBrowserCookies.ToUnixPath(dir);
                }
            }
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home) && home.StartsWith('/'))
        {
            return home;
        }

        return null;
    }

    private static string? FindUnixPython()
    {
        foreach (var path in new[]
                 {
                     "/usr/bin/python3",
                     "/usr/bin/python3.14",
                     "/usr/bin/python3.13",
                     "/usr/bin/python3.12",
                 })
        {
            if (File.Exists(YouTubeBrowserCookies.ToYtDlpPath(path)))
            {
                return path;
            }
        }

        return File.Exists(@"Z:\usr\bin\yt-dlp") ? "/usr/bin/python3" : null;
    }

    private static void RunUnix(string unixExecutable, IReadOnlyList<string> args)
    {
        var zExe = YouTubeBrowserCookies.ToYtDlpPath(unixExecutable);
        try
        {
            var direct = new ProcessStartInfo(zExe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                direct.ArgumentList.Add(arg);
            }

            using var process = Process.Start(direct);
            if (process is not null)
            {
                if (!process.WaitForExit(45_000))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                return;
            }
        }
        catch (Exception exception)
        {
            AepLog.Debug($"[YouTube] Direct unix spawn failed ({exception.Message}); trying start /unix.");
        }

        var quoted = string.Join(" ", args.Select(static arg => $"\"{arg}\""));
        var start = new ProcessStartInfo(@"C:\windows\system32\start.exe")
        {
            Arguments = $"/wait /unix {unixExecutable} {quoted}",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var launched = Process.Start(start);
        launched?.WaitForExit(45_000);
    }

    private static string UnixJoin(string root, params string[] parts)
    {
        var tail = string.Join("/", parts.Select(static part => part.Trim('/')));
        return root.TrimEnd('/') + "/" + tail;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }

    private const string ExportScript =
        """
        import os
        import sys
        import traceback

        from yt_dlp.cookies import extract_cookies_from_browser

        browser, profile, dest, status = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]

        def write_status(text):
            with open(status, "w", encoding="utf-8") as handle:
                handle.write(text)

        try:
            path = None if profile in ("", "-") else profile
            try:
                jar = extract_cookies_from_browser(browser, path)
            except Exception:
                if path:
                    jar = extract_cookies_from_browser(browser, os.path.dirname(path))
                else:
                    raise
            jar.save(dest, ignore_discard=True, ignore_expires=True)
            write_status("ok\n")
        except Exception:
            write_status("err\n" + traceback.format_exc())
            sys.exit(1)
        """;
}
