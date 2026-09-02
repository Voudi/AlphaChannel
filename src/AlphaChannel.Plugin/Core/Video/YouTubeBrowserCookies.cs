namespace AlphaChannel.Plugin.Video;

internal readonly record struct YouTubeBrowserProfile(
    string Key,
    string BrowserId,
    string Label,
    string ProfilePath);

internal readonly record struct YouTubeBrowserKind(
    string Key,
    string BrowserId,
    string Label,
    bool FirefoxFamily,
    string[] RelativeRoots);

// Discovers local browser profiles so yt-dlp can use cookies-from-browser.
// yt-dlp ids: brave, chrome, chromium, edge, firefox, opera, safari, vivaldi, whale.
// The plugin process is a Windows binary (Wine); Linux profiles are exposed as Z:\...
internal static class YouTubeBrowserCookies
{
    internal static IReadOnlyList<YouTubeBrowserKind> Catalog { get; } =
    [
        new("firefox", "firefox", "Firefox", true,
        [
            Path.Combine(".mozilla", "firefox"),
            Path.Combine(".var", "app", "org.mozilla.firefox", ".mozilla", "firefox"),
            Path.Combine("snap", "firefox", "common", ".mozilla", "firefox"),
            Path.Combine("AppData", "Roaming", "Mozilla", "Firefox", "Profiles"),
        ]),
        new("librewolf", "firefox", "LibreWolf", true,
        [
            Path.Combine(".librewolf"),
            Path.Combine(".var", "app", "io.gitlab.librewolf-community", ".librewolf"),
            Path.Combine("AppData", "Roaming", "librewolf", "Profiles"),
        ]),
        new("chrome", "chrome", "Chrome", false,
        [
            Path.Combine(".config", "google-chrome"),
            Path.Combine(".var", "app", "com.google.Chrome", "config", "google-chrome"),
            Path.Combine("AppData", "Local", "Google", "Chrome", "User Data"),
        ]),
        new("chrome-beta", "chrome", "Chrome Beta", false,
        [
            Path.Combine(".config", "google-chrome-beta"),
            Path.Combine("AppData", "Local", "Google", "Chrome Beta", "User Data"),
        ]),
        new("chromium", "chromium", "Chromium", false,
        [
            Path.Combine(".config", "chromium"),
            Path.Combine(".var", "app", "org.chromium.Chromium", "config", "chromium"),
            Path.Combine("AppData", "Local", "Chromium", "User Data"),
        ]),
        new("edge", "edge", "Edge", false,
        [
            Path.Combine(".config", "microsoft-edge"),
            Path.Combine(".config", "microsoft-edge-stable"),
            Path.Combine(".var", "app", "com.microsoft.Edge", "config", "microsoft-edge"),
            Path.Combine("AppData", "Local", "Microsoft", "Edge", "User Data"),
        ]),
        new("brave", "brave", "Brave", false,
        [
            Path.Combine(".config", "BraveSoftware", "Brave-Browser"),
            Path.Combine(".var", "app", "com.brave.Browser", "config", "BraveSoftware", "Brave-Browser"),
            Path.Combine("AppData", "Local", "BraveSoftware", "Brave-Browser", "User Data"),
        ]),
        new("opera", "opera", "Opera", false,
        [
            Path.Combine(".config", "opera"),
            Path.Combine(".var", "app", "com.opera.Opera", "config", "opera"),
            Path.Combine("AppData", "Roaming", "Opera Software", "Opera Stable"),
            Path.Combine("Opera Software", "Opera Stable"),
        ]),
        new("opera-gx", "opera", "Opera GX", false,
        [
            Path.Combine(".var", "app", "com.opera.opera-gx", "config", "opera-gx"),
            Path.Combine(".var", "app", "com.opera.OperaGX", "config", "opera"),
            Path.Combine(".config", "opera_gx"),
            Path.Combine(".config", "opera-gx"),
            Path.Combine("AppData", "Roaming", "Opera Software", "Opera GX Stable"),
            Path.Combine("Opera Software", "Opera GX Stable"),
        ]),
        new("vivaldi", "vivaldi", "Vivaldi", false,
        [
            Path.Combine(".config", "vivaldi"),
            Path.Combine(".var", "app", "com.vivaldi.Vivaldi", "config", "vivaldi"),
            Path.Combine("AppData", "Local", "Vivaldi", "User Data"),
        ]),
        new("whale", "whale", "Whale", false,
        [
            Path.Combine(".config", "naver-whale"),
            Path.Combine("AppData", "Local", "Naver", "Naver Whale", "User Data"),
        ]),
        new("safari", "safari", "Safari", false,
        [
            Path.Combine("Library", "Safari"),
            Path.Combine("Library", "Containers", "com.apple.Safari", "Data", "Library", "Safari"),
        ]),
    ];

    internal static IReadOnlyList<YouTubeBrowserProfile> Detect()
    {
        var found = new List<YouTubeBrowserProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kind in Catalog)
        {
            foreach (var home in CandidateHomes())
            {
                foreach (var rel in kind.RelativeRoots)
                {
                    var root = Path.Combine(home, rel);
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    if (kind.FirefoxFamily)
                    {
                        foreach (var profile in EnumerateFirefoxProfiles(root))
                        {
                            if (seen.Add(profile))
                            {
                                found.Add(new YouTubeBrowserProfile(
                                    kind.Key,
                                    kind.BrowserId,
                                    kind.Label,
                                    ToYtDlpPath(profile)));
                            }
                        }

                        continue;
                    }

                    if (kind.BrowserId == "safari")
                    {
                        if (seen.Add(root))
                        {
                            found.Add(new YouTubeBrowserProfile(
                                kind.Key,
                                kind.BrowserId,
                                kind.Label,
                                ToYtDlpPath(root)));
                        }

                        continue;
                    }

                    foreach (var profile in EnumerateChromiumProfiles(root))
                    {
                        if (seen.Add(profile))
                        {
                            found.Add(new YouTubeBrowserProfile(
                                kind.Key,
                                kind.BrowserId,
                                kind.Label,
                                ToYtDlpPath(profile)));
                            break;
                        }
                    }
                }

                if (found.Any(profile => profile.Key == kind.Key))
                {
                    break;
                }
            }
        }

        return found;
    }

    internal static YouTubeBrowserProfile? Find(string? keyOrBrowserId, string? profilePath = null)
    {
        var detected = Detect();
        if (!string.IsNullOrWhiteSpace(profilePath))
        {
            foreach (var profile in detected)
            {
                if (string.Equals(profile.ProfilePath, profilePath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(profile.ProfilePath, ToYtDlpPath(profilePath), StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(keyOrBrowserId))
        {
            return null;
        }

        foreach (var profile in detected)
        {
            if (string.Equals(profile.Key, keyOrBrowserId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(profile.BrowserId, keyOrBrowserId, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        return null;
    }

    internal static string? FindProfile(string browserId) =>
        Find(browserId)?.ProfilePath;

    internal static string YtdlArg(string browserId, string profilePath) =>
        $"{browserId}:{profilePath}";

    internal static string ToYtDlpPath(string path)
    {
        if (path.Length >= 2 && path[1] == ':')
        {
            return path;
        }

        if (path.StartsWith('/'))
        {
            return "Z:" + path.Replace('/', '\\');
        }

        return path;
    }

    internal static string ToUnixPath(string path)
    {
        if (path.StartsWith("Z:", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("z:", StringComparison.OrdinalIgnoreCase))
        {
            return path[2..].Replace('\\', '/');
        }

        return path.Replace('\\', '/');
    }

    private static IEnumerable<string> EnumerateFirefoxProfiles(string root)
    {
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.GetDirectories(root);
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (name.Contains(".default", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(dir, "cookies.sqlite")))
            {
                yield return dir;
            }
        }
    }

    private static IEnumerable<string> EnumerateChromiumProfiles(string root)
    {
        var direct = ResolveCookieProfile(root);
        if (direct is not null && HasChromiumCookies(direct))
        {
            yield return direct;
            yield break;
        }

        var nestedDefault = ResolveCookieProfile(Path.Combine(root, "Default"));
        if (nestedDefault is not null && HasChromiumCookies(nestedDefault))
        {
            yield return nestedDefault;
        }
    }

    private static bool HasChromiumCookies(string path) =>
        File.Exists(Path.Combine(path, "Cookies")) ||
        File.Exists(Path.Combine(path, "Network", "Cookies"));

    private static IEnumerable<string> CandidateHomes()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetEnvironmentVariable("HOME"),
                     Environment.GetEnvironmentVariable("WINEHOMEDIR"),
                 })
        {
            var home = NormalizeHome(raw);
            if (home is not null && seen.Add(home))
            {
                yield return home;
            }
        }

        if (Directory.Exists(@"Z:\home"))
        {
            foreach (var dir in Directory.GetDirectories(@"Z:\home"))
            {
                if (seen.Add(dir))
                {
                    yield return dir;
                }
            }
        }
    }

    private static string? NormalizeHome(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var path = raw.Trim();
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        if (path.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
        {
            path = path["unix:".Length..];
        }

        return Directory.Exists(path) ? path : null;
    }

    private static string? ResolveCookieProfile(string path)
    {
        if (HasChromiumCookies(path))
        {
            return path;
        }

        var nested = Path.Combine(path, "Default");
        if (HasChromiumCookies(nested))
        {
            return nested;
        }

        return Directory.Exists(path) ? path : null;
    }
}
