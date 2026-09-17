using System.Text;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Owns the explicit connection between Alpha Channel's embedded Chromium
/// profile and yt-dlp. The browser remains the source of truth; this class
/// stores only a filtered Netscape cookie jar after the player has chosen
/// "Use this account".
/// </summary>
internal static class YouTubeEmbeddedBrowserSession
{
    private const string DirectoryName =
        "YouTube";

    private const string FileName =
        "embedded-browser-cookies.txt";

    private static readonly HashSet<string> AuthenticationCookieNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "LOGIN_INFO",
            "SID",
            "SAPISID",
            "__Secure-1PSID",
            "__Secure-3PSID",
            "__Secure-1PAPISID",
            "__Secure-3PAPISID"
        };

    internal static string CookieFilePath =>
        Path.Combine(
            Plugin.PluginInterface.ConfigDirectory.FullName,
            DirectoryName,
            FileName);

    internal static bool IsConnected(Configuration configuration) =>
        configuration.YouTubeEmbeddedBrowserSessionEnabled &&
        LooksUsable(CookieFilePath);

    internal static string Save(string cookieText)
    {
        var filtered =
            ValidateAndFilter(cookieText);

        var path =
            CookieFilePath;

        var directory =
            Path.GetDirectoryName(path) ??
            throw new InvalidOperationException(
                "The YouTube session directory could not be resolved.");

        Directory.CreateDirectory(directory);

        var temporaryPath =
            path + ".tmp";

        try
        {
            File.WriteAllText(
                temporaryPath,
                filtered,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));

            File.Move(
                temporaryPath,
                path,
                overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup after an interrupted atomic write.
            }
        }

        return path;
    }

    internal static void Disconnect(Configuration configuration)
    {
        configuration.YouTubeEmbeddedBrowserSessionEnabled =
            false;

        configuration.Save();

        try
        {
            if (File.Exists(CookieFilePath))
                File.Delete(CookieFilePath);
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                "[YouTube/Account] Could not delete the managed browser session: " +
                exception.Message);
        }
    }

    internal static bool LooksUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path))
        {
            return false;
        }

        try
        {
            var text =
                File.ReadAllText(path);

            _ = ValidateAndFilter(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ValidateAndFilter(string cookieText)
    {
        if (string.IsNullOrWhiteSpace(cookieText))
        {
            throw new InvalidDataException(
                "The browser did not return any YouTube cookies.");
        }

        var output =
            new StringBuilder(
                "# Netscape HTTP Cookie File\n");

        var foundAuthenticationCookie =
            false;

        foreach (var sourceLine in cookieText.Split('\n'))
        {
            var line =
                sourceLine.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line) ||
                line.StartsWith('#') &&
                !line.StartsWith("#HttpOnly_", StringComparison.Ordinal))
            {
                continue;
            }

            var fields =
                line.Split('\t');

            if (fields.Length != 7)
                continue;

            var domain =
                fields[0]
                    .Replace("#HttpOnly_", string.Empty, StringComparison.Ordinal)
                    .TrimStart('.');

            if (!IsAllowedDomain(domain))
                continue;

            if (AuthenticationCookieNames.Contains(fields[5]))
                foundAuthenticationCookie = true;

            output.Append(line).Append('\n');
        }

        if (!foundAuthenticationCookie)
        {
            throw new InvalidDataException(
                "No signed-in YouTube session was found. Finish signing in, then try again.");
        }

        return output.ToString();
    }

    private static bool IsAllowedDomain(string domain) =>
        domain.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
        domain.Equals("google.com", StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase) ||
        domain.Equals("googleapis.com", StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase) ||
        domain.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
        domain.Equals("ytimg.com", StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith(".ytimg.com", StringComparison.OrdinalIgnoreCase);
}
