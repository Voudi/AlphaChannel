// Legacy Wine/Linux cookie extraction reference.
//
// Alpha Channel now reads the session from its embedded CefSharp browser.
// Keep this compilation-disabled copy until that browser has been verified on
// Wine/Linux. If CefSharp cannot run there, these are the platform-specific
// pieces needed to restore native Linux browser extraction without reviving
// the former Windows external-browser/profile system.
#if false
using System.Diagnostics;
using System.Text;

namespace AlphaChannel.Plugin.Video;

internal static class LegacyWineYouTubeCookieExport
{
    private const string CookiesFileName = "youtube-cookies.txt";
    private const string ScriptFileName = "export-youtube-cookies.py";

    internal static string Export(
        string browserId,
        string linuxProfilePath)
    {
        var unixHome = FindUnixHome() ??
            throw new InvalidOperationException(
                "Could not find the Linux home directory from Wine.");
        var python = FindUnixPython() ??
            throw new InvalidOperationException(
                "Python and the native yt-dlp module are required for Linux browser cookie extraction.");

        var destinationDirectory =
            UnixJoin(unixHome, ".local", "share", "alphachannel");
        var windowsDestinationDirectory =
            ToWinePath(destinationDirectory);
        Directory.CreateDirectory(windowsDestinationDirectory);

        var unixScript = UnixJoin(destinationDirectory, ScriptFileName);
        var unixCookies = UnixJoin(destinationDirectory, CookiesFileName);
        var unixStatus = UnixJoin(destinationDirectory, "youtube-cookies.status");

        File.WriteAllText(
            ToWinePath(unixScript),
            ExportScript,
            new UTF8Encoding(false));
        TryDelete(ToWinePath(unixStatus));
        TryDelete(ToWinePath(unixCookies));

        RunUnix(
            python,
            [unixScript, browserId, linuxProfilePath, unixCookies, unixStatus]);

        var statusPath = ToWinePath(unixStatus);
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!File.Exists(statusPath) && DateTime.UtcNow < deadline)
            Thread.Sleep(150);

        if (!File.Exists(statusPath))
            throw new InvalidOperationException("Timed out copying Linux browser cookies.");

        var status = File.ReadAllText(statusPath);
        if (!status.StartsWith("ok", StringComparison.Ordinal))
            throw new InvalidOperationException(status.Replace("err\n", string.Empty).Trim());

        return ToWinePath(unixCookies);
    }

    private static string? FindUnixHome()
    {
        if (Directory.Exists(@"Z:\home"))
        {
            foreach (var directory in Directory.GetDirectories(@"Z:\home"))
            {
                if (Directory.Exists(Path.Combine(directory, ".var")) ||
                    Directory.Exists(Path.Combine(directory, ".local")))
                    return directory.Replace(@"Z:\", "/").Replace('\\', '/');
            }
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        return !string.IsNullOrWhiteSpace(home) && home.StartsWith('/')
            ? home
            : null;
    }

    private static string? FindUnixPython()
    {
        foreach (var path in new[]
                 {
                     "/usr/bin/python3",
                     "/usr/bin/python3.14",
                     "/usr/bin/python3.13",
                     "/usr/bin/python3.12"
                 })
        {
            if (File.Exists(ToWinePath(path)))
                return path;
        }

        return null;
    }

    private static void RunUnix(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(ToWinePath(executable))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("The native Linux cookie exporter could not be started.");
        if (!process.WaitForExit(45_000))
            process.Kill(entireProcessTree: true);
    }

    private static string UnixJoin(string root, params string[] parts) =>
        root.TrimEnd('/') + "/" +
        string.Join("/", parts.Select(part => part.Trim('/')));

    private static string ToWinePath(string unixPath) =>
        unixPath.StartsWith('/')
            ? @"Z:\" + unixPath.TrimStart('/').Replace('/', '\\')
            : unixPath;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
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
            jar = extract_cookies_from_browser(browser, path)
            jar.save(dest, ignore_discard=True, ignore_expires=True)
            write_status("ok\n")
        except Exception:
            write_status("err\n" + traceback.format_exc())
            sys.exit(1)
        """;
}
#endif
