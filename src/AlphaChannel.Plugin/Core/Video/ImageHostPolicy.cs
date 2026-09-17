using System.Net;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Applies the first layer of validation to remotely hosted images.
///
/// The image downloader must additionally validate DNS results, redirects,
/// response size, file signatures and decoded dimensions. Host approval alone
/// is not sufficient protection.
/// </summary>
internal static class ImageHostPolicy
{
    internal const long MaximumDownloadBytes =
        15L * 1024L * 1024L;

    internal const int MaximumWidth =
        8192;

    internal const int MaximumHeight =
        8192;

    internal const long MaximumPixels =
        40_000_000L;

    internal const int MaximumRedirects =
        4;

    internal static readonly TimeSpan DownloadTimeout =
        TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> AllowedHosts =
      new(
          StringComparer.OrdinalIgnoreCase)
      {
        "i.imgur.com",
        "i.postimg.cc",
        "myimgs.org",
        "cdn.myimgs.org",
        "cdn.imgpile.com"
      };

    internal static IReadOnlyCollection<string> SupportedHosts =>
        AllowedHosts;

    internal static bool TryValidateUrl(
        string? value,
        out Uri? uri,
        out string? error)
    {
        uri =
            null;

        error =
            null;

        if (string.IsNullOrWhiteSpace(
                value))
        {
            error =
                "Enter an image URL.";

            return false;
        }

        if (!Uri.TryCreate(
                value.Trim(),
                UriKind.Absolute,
                out var parsed))
        {
            error =
                "The image URL is not valid.";

            return false;
        }

        if (!string.Equals(
                parsed.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            error =
                "Image URLs must use HTTPS.";

            return false;
        }

        if (!string.IsNullOrEmpty(
                parsed.UserInfo))
        {
            error =
                "Image URLs cannot contain a username or password.";

            return false;
        }

        if (!parsed.IsDefaultPort &&
            parsed.Port != 443)
        {
            error =
                "Image URLs must use the standard HTTPS port.";

            return false;
        }

        if (IPAddress.TryParse(
                parsed.Host,
                out _))
        {
            error =
                "Direct IP-address image URLs are not supported.";

            return false;
        }

        if (!AllowedHosts.Contains(
          parsed.IdnHost))
        {
            error =
                "This image host is not supported. " +
                "Use MyImgs, ImgPile, Imgur, or Postimages.";

            return false;
        }

        if (string.Equals(
         parsed.IdnHost,
         "myimgs.org",
         StringComparison.OrdinalIgnoreCase))
        {
            const string suppliedPathPrefix =
                "/storage/images/";

            if (!parsed.AbsolutePath.StartsWith(
                    suppliedPathPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "Use the direct image link provided by MyImgs.";

                return false;
            }

            var imagePath =
                parsed.AbsolutePath[
                    suppliedPathPrefix.Length..];

            var cdnUriBuilder =
                new UriBuilder(
                    Uri.UriSchemeHttps,
                    "cdn.myimgs.org")
                {
                    Path =
                        $"/images/{imagePath}",

                    Query =
                        parsed.Query.TrimStart(
                            '?')
                };

            parsed =
                cdnUriBuilder.Uri;
        }
        else if (string.Equals(
                     parsed.IdnHost,
                     "cdn.myimgs.org",
                     StringComparison.OrdinalIgnoreCase) &&
                 !parsed.AbsolutePath.StartsWith(
                     "/images/",
                     StringComparison.OrdinalIgnoreCase))
        {
            error =
                "Use a direct MyImgs CDN image link.";

            return false;
        }

        if (string.Equals(
                parsed.IdnHost,
                "cdn.imgpile.com",
                StringComparison.OrdinalIgnoreCase) &&
            !parsed.AbsolutePath.StartsWith(
                "/f/",
                StringComparison.OrdinalIgnoreCase))
        {
            error =
                "Use the direct image link provided by ImgPile.";

            return false;
        }

        uri =
            parsed;

        return true;
    }

    /// <summary>
    /// Returns false for loopback, private, link-local, multicast and other
    /// addresses that a shared watch-party URL must never contact.
    /// </summary>
    internal static bool IsPublicAddress(
        IPAddress address)
    {
        if (IPAddress.IsLoopback(
                address))
        {
            return false;
        }

        if (address.AddressFamily ==
            System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes =
                address.GetAddressBytes();

            return bytes[0] switch
            {
                0 => false,
                10 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 168 => false,
                198 when bytes[1] is 18 or 19 => false,
                >= 224 => false,
                _ => true
            };
        }

        if (address.AddressFamily ==
            System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal ||
                address.IsIPv6Multicast ||
                address.IsIPv6SiteLocal)
            {
                return false;
            }

            var bytes =
                address.GetAddressBytes();

            // fc00::/7 — IPv6 unique-local addresses.
            if ((bytes[0] & 0xFE) ==
                0xFC)
            {
                return false;
            }

            // :: and ::1
            if (address.Equals(
                    IPAddress.IPv6None) ||
                address.Equals(
                    IPAddress.IPv6Loopback))
            {
                return false;
            }

            return true;
        }

        return false;
    }
}