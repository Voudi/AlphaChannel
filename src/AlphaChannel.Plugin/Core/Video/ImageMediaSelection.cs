namespace AlphaChannel.Plugin.Video;

internal enum ImageMediaMode
{
    StillImage,
    Slideshow
}

internal enum ImageTransition
{
    Instant,
    Fade
}

/// <summary>
/// Encodes image and slideshow playback settings into a URL fragment.
///
/// The fragment is synchronization metadata used by AlphaChannel and is never
/// sent to the image host.
/// </summary>
internal sealed record ImageMediaSelection(
    ImageMediaMode Mode,
    IReadOnlyList<string> ImageUrls,
    int SecondsPerImage,
    ImageTransition Transition,
    bool Loop)
{
    internal const int MaximumImages =
        5;

    internal const int MinimumSecondsPerImage =
        2;

    internal const int MaximumSecondsPerImage =
        60;

    private const string MediaKey =
        "acmedia";

    internal string PrimaryImageUrl =>
        ImageUrls.Count > 0
            ? ImageUrls[0]
            : string.Empty;

    internal static bool IsImageMediaUrl(
        string? transmittedUrl)
    {
        return TryParse(
            transmittedUrl,
            out _);
    }

    internal static bool TryParse(
        string? transmittedUrl,
        out ImageMediaSelection? selection)
    {
        selection =
            null;

        if (string.IsNullOrWhiteSpace(
                transmittedUrl))
        {
            return false;
        }

        var fragmentIndex =
            transmittedUrl.IndexOf('#');

        if (fragmentIndex <= 0 ||
            fragmentIndex >=
                transmittedUrl.Length - 1)
        {
            return false;
        }

        var primaryUrl =
            transmittedUrl[..fragmentIndex];

        var values =
            transmittedUrl[
                    (fragmentIndex + 1)..]
                .Split(
                    '&',
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(
                    part => part.Split(
                        '=',
                        2,
                        StringSplitOptions.None))
                .Where(
                    parts => parts.Length == 2)
                .ToDictionary(
                    parts => parts[0],
                    parts => Uri.UnescapeDataString(
                        parts[1]),
                    StringComparer.OrdinalIgnoreCase);

        if (!values.TryGetValue(
                MediaKey,
                out var mediaValue))
        {
            return false;
        }

        var mode =
            mediaValue.ToLowerInvariant() switch
            {
                "still" =>
                    ImageMediaMode.StillImage,

                "slideshow" =>
                    ImageMediaMode.Slideshow,

                _ =>
                    (ImageMediaMode?)null
            };

        if (mode is null)
        {
            return false;
        }

        var imageUrls =
            new List<string>
            {
                primaryUrl
            };

        for (var index = 2;
             index <= MaximumImages;
             index++)
        {
            if (values.TryGetValue(
                    $"image{index}",
                    out var imageUrl) &&
                !string.IsNullOrWhiteSpace(
                    imageUrl))
            {
                imageUrls.Add(
                    imageUrl);
            }
        }

        var secondsPerImage =
            values.TryGetValue(
                "seconds",
                out var secondsValue) &&
            int.TryParse(
                secondsValue,
                out var parsedSeconds)
                ? Math.Clamp(
                    parsedSeconds,
                    MinimumSecondsPerImage,
                    MaximumSecondsPerImage)
                : 5;

        var transition =
            values.TryGetValue(
                "transition",
                out var transitionValue) &&
            transitionValue.Equals(
                "fade",
                StringComparison.OrdinalIgnoreCase)
                ? ImageTransition.Fade
                : ImageTransition.Instant;

        var loop =
            !values.TryGetValue(
                "loop",
                out var loopValue) ||
            loopValue != "0";

        selection =
            new ImageMediaSelection(
                mode.Value,
                imageUrls,
                secondsPerImage,
                transition,
                loop);

        return true;
    }

    internal static bool TryBuildStillImageUrl(
        string imageUrl,
        out string transmittedUrl,
        out string? error)
    {
        transmittedUrl =
            string.Empty;

        if (!ImageHostPolicy.TryValidateUrl(
                imageUrl,
                out var validatedUri,
                out error))
        {
            return false;
        }

        var cleanUrl =
            RemoveFragment(
                validatedUri!);

        transmittedUrl =
            $"{cleanUrl}#{MediaKey}=still";

        return true;
    }

    internal static bool TryBuildSlideshowUrl(
        IEnumerable<string> imageUrls,
        int secondsPerImage,
        ImageTransition transition,
        bool loop,
        out string transmittedUrl,
        out string? error)
    {
        transmittedUrl =
            string.Empty;

        error =
            null;

        var suppliedUrls =
            imageUrls
                .Where(
                    value => !string.IsNullOrWhiteSpace(
                        value))
                .Take(
                    MaximumImages)
                .ToArray();

        if (suppliedUrls.Length == 0)
        {
            error =
                "Add at least one image.";

            return false;
        }

        var validatedUrls =
            new List<string>(
                suppliedUrls.Length);

        foreach (var suppliedUrl in
                 suppliedUrls)
        {
            if (!ImageHostPolicy.TryValidateUrl(
                    suppliedUrl,
                    out var validatedUri,
                    out error))
            {
                return false;
            }

            validatedUrls.Add(
                RemoveFragment(
                    validatedUri!));
        }

        var fragmentParts =
            new List<string>
            {
                $"{MediaKey}=slideshow",
                $"seconds={Math.Clamp(
                    secondsPerImage,
                    MinimumSecondsPerImage,
                    MaximumSecondsPerImage)}",
                $"transition={GetTransitionId(
                    transition)}",
                $"loop={(loop ? 1 : 0)}"
            };

        for (var index = 1;
             index < validatedUrls.Count;
             index++)
        {
            fragmentParts.Add(
                $"image{index + 1}=" +
                Uri.EscapeDataString(
                    validatedUrls[index]));
        }

        transmittedUrl =
            validatedUrls[0] +
            "#" +
            string.Join(
                "&",
                fragmentParts);

        return true;
    }

    internal static string GetTransitionId(
        ImageTransition transition)
    {
        return transition switch
        {
            ImageTransition.Fade =>
                "fade",

            _ =>
                "instant"
        };
    }

    private static string RemoveFragment(
        Uri uri)
    {
        var builder =
            new UriBuilder(
                uri)
            {
                Fragment =
                    string.Empty
            };

        return builder.Uri.AbsoluteUri;
    }
}