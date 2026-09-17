using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum ImageDisplayMode
    {
        StillImage,
        Slideshow
    }

    private const int MaxSlideshowImages =
        5;

    private ImageDisplayMode imageDisplayMode =
        ImageDisplayMode.Slideshow;

    private readonly string[] slideshowImageUrls =
        new string[MaxSlideshowImages];

    private string imageUrlInput =
        string.Empty;

    private int selectedSlideshowImage;

    private int slideshowSeconds =
        5;

    private int slideshowTransition;

    private bool slideshowLoop =
        true;

    private string? imageMediaError;

    private readonly string[] slideshowTransitions =
    [
        "Instant",
    "Fade"
    ];

    private enum ImageGalleryContext
    {
        None,
        StillImage,
        Slideshow
    }

    private const int MaximumSavedImages =
        100;

    private Guid? pendingImageGalleryDeleteId;

    private ImageGalleryContext imageGalleryContext =
        ImageGalleryContext.None;

    private readonly List<Guid> selectedGalleryImageIds =
        new();

    private string imageGallerySearch =
        string.Empty;

    private string imageGalleryUrlInput =
        string.Empty;

    private string? imageGalleryError;

    private bool imageGalleryAddUrlOpen;

    private bool imageHostingGuideOpen;

    private int imageGallerySort;

    private readonly string[] imageGallerySortOptions =
    [
        "Newest first",
    "Oldest first",
    "Name"
    ];

    private void OpenImageGallery(
    ImageGalleryContext context)
    {
        imageGalleryContext =
            context;

        selectedGalleryImageIds.Clear();

        imageGalleryError =
            null;

        imageGalleryAddUrlOpen =
            false;
    }

    private void CloseImageGallery()
    {
        imageGalleryContext =
            ImageGalleryContext.None;

        selectedGalleryImageIds.Clear();

        imageGalleryError =
            null;

        imageGalleryAddUrlOpen =
            false;

        imageGalleryUrlInput =
            string.Empty;

        pendingImageGalleryDeleteId =
    null;
    }

    private bool IsImageSavedToGallery(
        string? url)
    {
        if (string.IsNullOrWhiteSpace(
                url))
        {
            return false;
        }

        return Plugin.Cfg.SavedImages.Any(
            savedImage =>
                string.Equals(
                    savedImage.Url,
                    url.Trim(),
                    StringComparison.OrdinalIgnoreCase));
    }

    private bool SaveImageToGallery(
        string? url)
    {
        imageGalleryError =
            null;

        if (string.IsNullOrWhiteSpace(
                url))
        {
            imageGalleryError =
                "Enter an image URL first.";

            return false;
        }

        var normalizedUrl =
            url.Trim();

        if (IsImageSavedToGallery(
                normalizedUrl))
        {
            imageGalleryError =
                "That image is already in your gallery.";

            return false;
        }

        if (Plugin.Cfg.SavedImages.Count >=
            MaximumSavedImages)
        {
            imageGalleryError =
                $"Your gallery can contain up to {MaximumSavedImages} images.";

            return false;
        }

        Plugin.Cfg.SavedImages.Add(
            new SavedImageRecord
            {
                Name =
                    CreateSavedImageName(
                        normalizedUrl),

                Url =
                    normalizedUrl,

                SavedAtUtc =
                    DateTime.UtcNow
            });

        Plugin.Cfg.Save();

        return true;
    }

    private static string CreateSavedImageName(
        string url)
    {
        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out var uri))
        {
            return "Saved image";
        }

        var finalSegment =
            uri.AbsolutePath.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .LastOrDefault();

        if (string.IsNullOrWhiteSpace(
                finalSegment))
        {
            return uri.Host;
        }

        finalSegment =
            Uri.UnescapeDataString(
                finalSegment);

        var extensionPosition =
            finalSegment.LastIndexOf(
                '.');

        if (extensionPosition > 0)
        {
            finalSegment =
                finalSegment[..extensionPosition];
        }

        finalSegment =
            finalSegment
                .Replace(
                    '-',
                    ' ')
                .Replace(
                    '_',
                    ' ')
                .Trim();

        if (string.IsNullOrWhiteSpace(
                finalSegment))
        {
            return uri.Host;
        }

        const int maximumNameLength =
            42;

        return finalSegment.Length >
               maximumNameLength
            ? $"{finalSegment[..(maximumNameLength - 1)]}…"
            : finalSegment;
    }

    private void RemoveImageFromGallery(
        Guid imageId)
    {
        Plugin.Cfg.SavedImages.RemoveAll(
            savedImage =>
                savedImage.Id ==
                imageId);

        selectedGalleryImageIds.Remove(
            imageId);

        pendingImageGalleryDeleteId =
            null;

        Plugin.Cfg.Save();
    }

    private int GetRemainingSlideshowSpaces()
    {
        return slideshowImageUrls.Count(
            string.IsNullOrWhiteSpace);
    }

    private void ToggleGalleryImageSelection(
        Guid imageId)
    {
        if (selectedGalleryImageIds.Remove(
                imageId))
        {
            return;
        }

        var maximumSelection =
            imageGalleryContext ==
            ImageGalleryContext.Slideshow
                ? GetRemainingSlideshowSpaces()
                : 1;

        if (maximumSelection <= 0)
        {
            imageGalleryError =
                "Your slideshow already contains 5 images.";

            return;
        }

        if (imageGalleryContext ==
            ImageGalleryContext.StillImage)
        {
            selectedGalleryImageIds.Clear();
        }
        else if (selectedGalleryImageIds.Count >=
                 maximumSelection)
        {
            imageGalleryError =
                maximumSelection == 1
                    ? "Only one slideshow space remains."
                    : $"Only {maximumSelection} slideshow spaces remain.";

            return;
        }

        selectedGalleryImageIds.Add(
            imageId);

        imageGalleryError =
            null;
    }

    private void UseSelectedGalleryImages()
    {
        var selectedImages =
            selectedGalleryImageIds
                .Select(
                    selectedId =>
                        Plugin.Cfg.SavedImages.FirstOrDefault(
                            savedImage =>
                                savedImage.Id ==
                                selectedId))
                .Where(
                    savedImage =>
                        savedImage is not null)
                .Cast<SavedImageRecord>()
                .ToList();

        if (selectedImages.Count == 0)
        {
            imageGalleryError =
                "Select an image first.";

            return;
        }

        //
        // Validate the complete selection before changing the still image or
        // slideshow. This makes gallery restoration atomic: if one selected
        // image is unavailable, none of the selection is added.
        //

        foreach (var savedImage in
                 selectedImages)
        {
            var preview =
                imagePreviews.Get(
                    savedImage.Url);

            if (preview.State ==
                ImagePreviewState.Loading)
            {
                imageGalleryError =
                    $"“{savedImage.Name}” is still loading. Try again shortly.";

                return;
            }

            if (preview.State !=
                ImagePreviewState.Ready)
            {
                imageGalleryError =
                    $"“{savedImage.Name}” is temporarily unavailable. " +
                    "Retry the image before using it.";

                return;
            }
        }

        imageGalleryError =
            null;

        if (imageGalleryContext ==
            ImageGalleryContext.StillImage)
        {
            imageUrlInput =
                selectedImages[0].Url;

            CloseImageGallery();

            return;
        }

        var firstAddedIndex =
            -1;

        foreach (var savedImage in
                 selectedImages)
        {
            var emptyIndex =
                Array.FindIndex(
                    slideshowImageUrls,
                    string.IsNullOrWhiteSpace);

            if (emptyIndex < 0)
            {
                break;
            }

            slideshowImageUrls[emptyIndex] =
                savedImage.Url;

            if (firstAddedIndex < 0)
            {
                firstAddedIndex =
                    emptyIndex;
            }
        }

        if (firstAddedIndex >= 0)
        {
            selectedSlideshowImage =
                firstAddedIndex;
        }

        CloseImageGallery();
    }

    private void DrawImageHostingGuideOverlay()
    {
        if (!imageHostingGuideOpen)
        {
            return;
        }

        var parentPosition =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var panelWidth =
            MathF.Min(
                Ui(690f),
                parentSize.X -
                Ui(40f));

        var panelHeight =
            MathF.Min(
                Ui(570f),
                parentSize.Y -
                Ui(40f));

        var panelPosition =
            parentPosition +
            new Vector2(
                (parentSize.X -
                 panelWidth) *
                0.5f,

                (parentSize.Y -
                 panelHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!ImGui.Begin(
                "##imageHostingGuideOverlay",
                overlayFlags))
        {
            ImGui.End();

            return;
        }

        var overlayDrawList =
            ImGui.GetWindowDrawList();

        overlayDrawList.AddRectFilled(
            parentPosition,
            parentPosition +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.58f)));

        ImGui.SetCursorScreenPos(
            panelPosition);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                Ui(14f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(
                    22f,
                    20f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    0.025f,
                    0.03f,
                    0.06f,
                    0.995f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.82f)))
        using (
            var panel =
                ImRaii.Child(
                    "##imageHostingGuidePanel",
                    new Vector2(
                        panelWidth,
                        panelHeight),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (panel)
            {
                DrawImageHostingGuideContents(
                    panelPosition,
                    panelWidth,
                    panelHeight);
            }
        }

        ImGui.End();
    }

    private void DrawImageHostingGuideContents(
        Vector2 panelPosition,
        float panelWidth,
        float panelHeight)
    {
        var contentOrigin =
            ImGui.GetCursorScreenPos();

        var closeSize =
            new Vector2(
                Ui(32f),
                Ui(32f));

        ImGui.SetCursorScreenPos(
            new Vector2(
                panelPosition.X +
                panelWidth -
                Ui(22f) -
                closeSize.X,

                panelPosition.Y +
                Ui(16f)));

        var closeOrigin =
     ImGui.GetCursorScreenPos();

        var closeClicked =
            ImGui.InvisibleButton(
                "##closeImageHostingGuide",
                closeSize);

        var closeHovered =
            ImGui.IsItemHovered();

        var closeDrawList =
            ImGui.GetWindowDrawList();

        closeDrawList.AddCircleFilled(
            closeOrigin +
            closeSize *
            0.5f,
            closeSize.X *
            0.5f,
            ImGui.GetColorU32(
                closeHovered
                    ? Accent
                    : new Vector4(
                        0.07f,
                        0.09f,
                        0.14f,
                        1f)));

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            var closeIcon =
                FontAwesomeIcon.Times
                    .ToIconString();

            var closeIconSize =
                ImGui.CalcTextSize(
                    closeIcon);

            ImGui.SetCursorScreenPos(
                closeOrigin +
                new Vector2(
                    (closeSize.X -
                     closeIconSize.X) *
                    0.5f,

                    (closeSize.Y -
                     closeIconSize.Y) *
                    0.5f -
                    Ui(1f)));

            ImGui.TextColored(
                Vector4.One,
                closeIcon);
        }

        if (closeClicked)
        {
            imageHostingGuideOpen =
                false;
        }

        ImGui.SetCursorScreenPos(
            contentOrigin);

        var icon =
            FontAwesomeIcon.BookOpen
                .ToIconString();

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            SetUiFontScale(
                1.55f);

            ImGui.TextColored(
                Accent,
                icon);

            SetUiFontScale(
                1f);
        }

        ImGui.SetCursorScreenPos(
            contentOrigin +
            new Vector2(
                Ui(48f),
                0f));

        SetUiFontScale(
            1.25f);

        ImGui.TextColored(
            Vector4.One,
            "Image Hosting Guide");

        SetUiFontScale(
            1f);

        ImGui.SetCursorScreenPos(
            contentOrigin +
            new Vector2(
                Ui(48f),
                Ui(29f)));

        SetUiFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "Where to upload images and how to use their direct links.");

        SetUiFontScale(
            1f);

        ImGui.SetCursorScreenPos(
            contentOrigin +
            new Vector2(
                0f,
                Ui(70f)));

        ImGui.Separator();

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(12f)));

        SetUiFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Supported image hosts");

        SetUiFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            "Open one of these websites to upload your image.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(10f)));

        var buttonGap =
            Ui(8f);

        var buttonWidth =
            (ImGui.GetContentRegionAvail().X -
             buttonGap) /
            2f;

        DrawDjActionButton(
            "##openMyImgs",
            FontAwesomeIcon.ExternalLinkAlt,
            "MyImgs",
            new Vector2(
                buttonWidth,
                Ui(38f)),
            false,
            () =>
                Dalamud.Utility.Util.OpenLink(
                    "https://myimgs.org/"));

        ImGui.SameLine(
            0f,
            buttonGap);

        DrawDjActionButton(
            "##openImgPile",
            FontAwesomeIcon.ExternalLinkAlt,
            "ImgPile",
            new Vector2(
                buttonWidth,
                Ui(38f)),
            false,
            () =>
                Dalamud.Utility.Util.OpenLink(
                    "https://imgpile.com/"));

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(8f)));

        buttonWidth =
            (ImGui.GetContentRegionAvail().X -
             buttonGap) /
            2f;

        DrawDjActionButton(
            "##openImgur",
            FontAwesomeIcon.ExternalLinkAlt,
            "Imgur",
            new Vector2(
                buttonWidth,
                Ui(38f)),
            false,
            () =>
                Dalamud.Utility.Util.OpenLink(
                    "https://imgur.com/upload"));

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Imgur is blocked in the UK, so only use it if all " +
                "watch party members are outside of the UK.");
        }

        ImGui.SameLine(
            0f,
            buttonGap);

        DrawDjActionButton(
            "##openPostimages",
            FontAwesomeIcon.ExternalLinkAlt,
            "Postimages",
            new Vector2(
                buttonWidth,
                Ui(38f)),
            false,
            () =>
                Dalamud.Utility.Util.OpenLink(
                    "https://postimages.org/"));

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(18f)));

        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    0.055f,
                    0.065f,
                    0.105f,
                    1f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                Ui(8f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(
                    14f,
                    12f)))
        using (
            var directLinkCard =
                ImRaii.Child(
                    "##directImageLinkHelp",
                    new Vector2(
                        ImGui.GetContentRegionAvail().X,
                        Ui(116f)),
                    false,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (directLinkCard)
            {
                ImGui.TextColored(
                    Accent,
                    "Use the direct image URL");

                ImGui.Dummy(
                    new Vector2(
                        0f,
                        Ui(5f)));

                SetUiFontScale(
                    0.84f);

                ImGui.TextWrapped(
                    "After uploading, copy the direct link to the image itself. " +
                    "Direct image links usually end in .jpg, .jpeg, .png, .webp or .gif.");

                ImGui.Dummy(
                    new Vector2(
                        0f,
                        Ui(5f)));

                ImGui.TextColored(
                    MutedText,
                    "Links to gallery pages, albums and image-viewing pages will not work.");

                SetUiFontScale(
                    1f);
            }
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(13f)));

        SetUiFontScale(
     0.84f);

        ImGui.TextColored(
            new Vector4(
                1f,
                0.72f,
                0.30f,
                1f),
            "Images from any other website will be rejected.");

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(8f)));

        var closeButtonWidth =
            Ui(120f);

        var footerButtonHeight =
            Ui(40f);

        var footerTop =
            panelPosition.Y +
            panelHeight -
            Ui(20f) -
            footerButtonHeight;

        var supportTextWidth =
     panelWidth -
     Ui(44f) -
     closeButtonWidth -
     Ui(18f);

        var supportTextLeft =
            ImGui.GetCursorPosX();

        ImGui.PushTextWrapPos(
            supportTextLeft +
            supportTextWidth);

        ImGui.TextWrapped(
            "If you have trouble submitting an image, or would like another " +
            "image-hosting website to be supported, reach out to the Alpha Channel team on Discord.");

        ImGui.PopTextWrapPos();

        SetUiFontScale(
            1f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                panelPosition.X +
                panelWidth -
                Ui(22f) -
                closeButtonWidth,
                footerTop));

        DrawDjActionButton(
            "##doneImageHostingGuide",
            FontAwesomeIcon.Times,
            "Close",
            new Vector2(
                closeButtonWidth,
                footerButtonHeight),
            false,
            () =>
                imageHostingGuideOpen =
                    false,
            true);
    }

    private void DrawImageGalleryOverlay()
    {
        if (imageGalleryContext ==
            ImageGalleryContext.None)
        {
            return;
        }

        var parentPosition =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var panelWidth =
            MathF.Min(
                Ui(800f),
                parentSize.X -
                Ui(40f));

        var galleryRowCount =
            Math.Clamp(
                (Plugin.Cfg.SavedImages.Count + 2) /
                3,
                1,
                3);

        var desiredPanelHeight =
            Ui(
                290f +
                galleryRowCount *
                198f +
(imageGalleryAddUrlOpen
    ? 125f
    : 0f));

        var panelHeight =
            MathF.Min(
                MathF.Min(
                    desiredPanelHeight,
                    Ui(680f)),
                parentSize.Y -
                Ui(40f));

        var panelPosition =
            parentPosition +
            new Vector2(
                (parentSize.X -
                 panelWidth) *
                0.5f,

                (parentSize.Y -
                 panelHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!ImGui.Begin(
                "##imageGalleryOverlay",
                overlayFlags))
        {
            ImGui.End();

            return;
        }

        var overlayDrawList =
            ImGui.GetWindowDrawList();

        overlayDrawList.AddRectFilled(
            parentPosition,
            parentPosition +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.58f)));

        ImGui.SetCursorScreenPos(
            panelPosition);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                Ui(14f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(
                    22f,
                    20f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    0.025f,
                    0.03f,
                    0.06f,
                    0.995f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.82f)))
        using (
            var panel =
                ImRaii.Child(
                    "##imageGalleryPanel",
                    new Vector2(
                        panelWidth,
                        panelHeight),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (panel)
            {
                DrawImageGalleryContents(
                    panelPosition,
                    panelWidth,
                    panelHeight);
            }
        }

        ImGui.End();
    }

    private void DrawImageGalleryContents(
        Vector2 panelPosition,
        float panelWidth,
        float panelHeight)
    {
        var contentOrigin =
            ImGui.GetCursorScreenPos();

        var closeSize =
            new Vector2(
                Ui(32f),
                Ui(32f));

        ImGui.SetCursorScreenPos(
            new Vector2(
                panelPosition.X +
                panelWidth -
                Ui(22f) -
                closeSize.X,
                panelPosition.Y +
                Ui(16f)));

        var galleryCloseOrigin =
       ImGui.GetCursorScreenPos();

        var galleryCloseClicked =
            ImGui.InvisibleButton(
                "##closeImageGallery",
                closeSize);

        var galleryCloseHovered =
            ImGui.IsItemHovered();

        var galleryCloseDrawList =
            ImGui.GetWindowDrawList();

        galleryCloseDrawList.AddCircleFilled(
            galleryCloseOrigin +
            closeSize *
            0.5f,
            closeSize.X *
            0.5f,
            ImGui.GetColorU32(
                galleryCloseHovered
                    ? Accent
                    : new Vector4(
                        0.07f,
                        0.09f,
                        0.14f,
                        1f)));

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            var galleryCloseIcon =
                FontAwesomeIcon.Times
                    .ToIconString();

            var galleryCloseIconSize =
                ImGui.CalcTextSize(
                    galleryCloseIcon);

            ImGui.SetCursorScreenPos(
                galleryCloseOrigin +
                new Vector2(
                    (closeSize.X -
                     galleryCloseIconSize.X) *
                    0.5f,

                    (closeSize.Y -
                     galleryCloseIconSize.Y) *
                    0.5f -
                    Ui(1f)));

            ImGui.TextColored(
                Vector4.One,
                galleryCloseIcon);
        }

        if (galleryCloseClicked)
        {
            CloseImageGallery();
        }

        ImGui.SetCursorScreenPos(
            contentOrigin);

        DrawImageGalleryHeading();

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(14f)));

        DrawImageGalleryToolbar();

        if (imageGalleryAddUrlOpen)
        {
            ImGui.Dummy(
                new Vector2(
                    0f,
                    Ui(10f)));

            DrawImageGalleryUrlEntry();
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(10f)));

        DrawImageGalleryStatusLine();

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(8f)));

        var gridOrigin =
    ImGui.GetCursorScreenPos();

        var footerButtonHeight =
            Ui(40f);

        var footerBottomPadding =
            Ui(18f);

        var footerTop =
            panelPosition.Y +
            panelHeight -
            footerBottomPadding -
            footerButtonHeight;

        var separatorY =
            footerTop -
            Ui(14f);

        var gridHeight =
            separatorY -
            gridOrigin.Y -
            Ui(8f);

        DrawImageGalleryGrid(
            MathF.Max(
                Ui(90f),
                gridHeight));

        //
        // Position the separator and footer explicitly so expanding the
        // Add-by-URL form can shrink the grid but never push actions outside
        // the popup.
        //

        ImGui.SetCursorScreenPos(
            new Vector2(
                contentOrigin.X,
                separatorY));

        ImGui.Separator();

        ImGui.SetCursorScreenPos(
            new Vector2(
                contentOrigin.X,
                footerTop));

        DrawImageGalleryFooter();
    }

    private void DrawImageGalleryHeading()
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var icon =
            FontAwesomeIcon.Images
                .ToIconString();

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            SetUiFontScale(
                1.55f);

            ImGui.TextColored(
                Accent,
                icon);

            SetUiFontScale(
                1f);
        }

        ImGui.SetCursorScreenPos(
            origin +
            new Vector2(
                Ui(48f),
                0f));

        SetUiFontScale(
            1.25f);

        ImGui.TextColored(
            Vector4.One,
            "Image Gallery");

        SetUiFontScale(
            1f);

        ImGui.SetCursorScreenPos(
            origin +
            new Vector2(
                Ui(48f),
                Ui(29f)));

        SetUiFontScale(
            0.82f);

        ImGui.TextColored(
            MutedText,
            imageGalleryContext ==
            ImageGalleryContext.Slideshow
                ? "Choose images to add to your slideshow."
                : "Save images for quick use in still images and slideshows.");

        SetUiFontScale(
            1f);

        ImGui.SetCursorScreenPos(
            origin +
            new Vector2(
                0f,
                Ui(50f)));
    }

    private void DrawImageGalleryToolbar()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float gap =
            8f;

        var sortWidth =
            Ui(142f);

        var addWidth =
            Ui(132f);

        var searchWidth =
            availableWidth -
            sortWidth -
            addWidth -
            gap *
            2f;

        ImGui.SetNextItemWidth(
            searchWidth);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                Ui(7f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FramePadding,
                UiVec(
                    10f,
                    9f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.FrameBg,
                new Vector4(
                    0.042f,
                    0.055f,
                    0.094f,
                    1f)))
        {
            ImGui.InputTextWithHint(
                "##imageGallerySearch",
                "Search saved images...",
                ref imageGallerySearch,
                200);
        }

        ImGui.SameLine(
            0f,
            gap);

        ImGui.SetNextItemWidth(
            sortWidth);

        ImGui.Combo(
            "##imageGallerySort",
            ref imageGallerySort,
            imageGallerySortOptions,
            imageGallerySortOptions.Length);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##toggleGalleryUrlEntry",
            FontAwesomeIcon.Plus,
            "Add by URL",
            new Vector2(
                addWidth,
                Ui(36f)),
            false,
            () =>
            {
                imageGalleryAddUrlOpen =
                    !imageGalleryAddUrlOpen;

                imageGalleryError =
                    null;
            },
            true);
    }

    private void DrawImageGalleryUrlEntry()
    {
        SetUiFontScale(
            0.75f);

        ImGui.TextColored(
            MutedText,
            "ADD AN IMAGE URL");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(4f)));

        var validation =
            DrawImageUrlInput(
                "##imageGalleryAddUrl",
                ref imageGalleryUrlInput);

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(7f)));

        var alreadySaved =
            IsImageSavedToGallery(
                imageGalleryUrlInput);

        DrawDjActionButton(
            "##saveGalleryUrl",
            FontAwesomeIcon.Save,
            alreadySaved
                ? "Already saved"
                : "Save Image",
           new Vector2(
    ImGui.GetContentRegionAvail().X,
    Ui(36f)),
            validation.State !=
                ImagePreviewState.Ready ||
            alreadySaved,
            () =>
            {
                if (SaveImageToGallery(
                        imageGalleryUrlInput))
                {
                    imageGalleryUrlInput =
                        string.Empty;

                    imageGalleryAddUrlOpen =
                        false;
                }
            },
            true);
    }

    private void DrawImageGalleryStatusLine()
    {
        if (!string.IsNullOrWhiteSpace(
                imageGalleryError))
        {
            SetUiFontScale(
                0.75f);

            ImGui.TextColored(
                new Vector4(
                    1f,
                    0.52f,
                    0.52f,
                    1f),
                imageGalleryError);

            SetUiFontScale(
                1f);

            return;
        }

        if (imageGalleryContext ==
            ImageGalleryContext.Slideshow)
        {
            var remainingSpaces =
                GetRemainingSlideshowSpaces();

            SetUiFontScale(
                0.75f);

            ImGui.TextColored(
                MutedText,
                remainingSpaces == 1
                    ? "1 slideshow space remaining"
                    : $"{remainingSpaces} slideshow spaces remaining");

            SetUiFontScale(
                1f);

            return;
        }

        SetUiFontScale(
            0.75f);

        ImGui.TextColored(
            MutedText,
            $"{Plugin.Cfg.SavedImages.Count} saved images");

        SetUiFontScale(
            1f);
    }

    private void DrawImageGalleryGrid(
        float height)
    {
        using (
            var grid =
                ImRaii.Child(
                    "##imageGalleryGrid",
                    new Vector2(
                        -1f,
                        height),
                    false,
                    ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            if (!grid)
            {
                return;
            }

            var query =
                imageGallerySearch.Trim();

            IEnumerable<SavedImageRecord> images =
                Plugin.Cfg.SavedImages;

            if (!string.IsNullOrWhiteSpace(
                    query))
            {
                images =
                    images.Where(
                        savedImage =>
                            savedImage.Name.Contains(
                                query,
                                StringComparison.OrdinalIgnoreCase) ||
                            savedImage.Url.Contains(
                                query,
                                StringComparison.OrdinalIgnoreCase) ||
                            GetSavedImageHost(
                                    savedImage.Url)
                                .Contains(
                                    query,
                                    StringComparison.OrdinalIgnoreCase));
            }

            images =
                imageGallerySort switch
                {
                    1 =>
                        images.OrderBy(
                            savedImage =>
                                savedImage.SavedAtUtc),

                    2 =>
                        images.OrderBy(
                            savedImage =>
                                savedImage.Name,
                            StringComparer.OrdinalIgnoreCase),

                    _ =>
                        images.OrderByDescending(
                            savedImage =>
                                savedImage.SavedAtUtc)
                };

            var visibleImages =
                images.ToList();

            if (visibleImages.Count == 0)
            {
                DrawEmptyImageGallery();

                return;
            }

            const int columnCount =
                3;

            const float gap =
                10f;

            var availableWidth =
                ImGui.GetContentRegionAvail().X -
                Ui(12f);

            var cardWidth =
                (availableWidth -
                 gap *
                 (columnCount - 1)) /
                columnCount;

            for (var index = 0;
                 index < visibleImages.Count;
                 index++)
            {
                DrawImageGalleryCard(
                    visibleImages[index],
                    cardWidth);

                if (index %
                    columnCount !=
                    columnCount - 1 &&
                    index <
                    visibleImages.Count - 1)
                {
                    ImGui.SameLine(
                        0f,
                        gap);
                }
                else
                {
                    ImGui.Dummy(
                        new Vector2(
                            0f,
                            Ui(8f)));
                }
            }
        }
    }

    private void DrawEmptyImageGallery()
    {
        var available =
            ImGui.GetContentRegionAvail();

        var origin =
            ImGui.GetCursorScreenPos();

        var center =
            origin +
            available *
            0.5f;

        var icon =
            FontAwesomeIcon.Images
                .ToIconString();

        Vector2 iconSize;

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            SetUiFontScale(
                1.8f);

            iconSize =
                ImGui.CalcTextSize(
                    icon);

            ImGui.GetWindowDrawList()
                .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    new Vector2(
                        center.X -
                        iconSize.X *
                        0.5f,
                        center.Y -
                        Ui(45f)),
                    ImGui.GetColorU32(
                        Accent),
                    icon);

            SetUiFontScale(
                1f);
        }

        const string heading =
            "Your gallery is empty";

        var headingSize =
            ImGui.CalcTextSize(
                heading);

        ImGui.GetWindowDrawList()
            .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    center.X -
                    headingSize.X *
                    0.5f,
                    center.Y),
                ImGui.GetColorU32(
                    Vector4.One),
                heading);

        const string description =
            "Save an image URL to use it again later.";

        var descriptionSize =
            ImGui.CalcTextSize(
                description);

        ImGui.GetWindowDrawList()
            .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    center.X -
                    descriptionSize.X *
                    0.5f,
                    center.Y +
                    Ui(25f)),
                ImGui.GetColorU32(
                    MutedText),
                description);

        ImGui.Dummy(
            available);
    }

    private void DrawUnavailableGalleryThumbnail(
    Vector2 requestedSize)
    {
        var width =
            requestedSize.X <= 0f
                ? ImGui.GetContentRegionAvail().X
                : requestedSize.X;

        var size =
            new Vector2(
                width,
                requestedSize.Y);

        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin +
            size,
            ImGui.GetColorU32(
                new Vector4(
                    0.075f,
                    0.082f,
                    0.105f,
                    1f)),
            Ui(7f));

        drawList.AddRect(
            origin,
            origin +
            size,
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.18f)),
            Ui(7f));

        var icon =
            FontAwesomeIcon.ExclamationTriangle
                .ToIconString();

        Vector2 iconSize;

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            SetUiFontScale(
                1.35f);

            iconSize =
                ImGui.CalcTextSize(
                    icon);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    origin.X +
                    (size.X -
                     iconSize.X) *
                    0.5f,

                    origin.Y +
                    Ui(28f)),
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.85f)),
                icon);

            SetUiFontScale(
                1f);
        }

        const string message =
            "Image unavailable";

        var messageSize =
            ImGui.CalcTextSize(
                message);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X +
                (size.X -
                 messageSize.X) *
                0.5f,

                origin.Y +
                Ui(72f)),
            ImGui.GetColorU32(
                MutedText),
            message);

        ImGui.Dummy(
            size);
    }

    private void DrawImageGalleryCard(
     SavedImageRecord savedImage,
     float width)
    {
        var selectedIndex =
            selectedGalleryImageIds.IndexOf(
                savedImage.Id);

        var selected =
            selectedIndex >= 0;

        var cardOrigin =
            ImGui.GetCursorScreenPos();

        var cardSize =
            new Vector2(
                width,
                Ui(190f));

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(
                    7f,
                    7f)))
        using (
     ImRaii.PushStyle(
         ImGuiStyleVar.ChildRounding,
         Ui(8f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildBorderSize,
                selected
                    ? Ui(2f)
                    : Ui(1f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    0.037f,
                    0.047f,
                    0.078f,
                    1f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                selected
                    ? Accent
                    : new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.16f)))
        using (
            var card =
                ImRaii.Child(
                    $"##savedImageCard{savedImage.Id}",
                    cardSize,
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!card)
            {
                return;
            }

            var preview =
                imagePreviews.Get(
                    savedImage.Url);

            if (preview.State ==
                ImagePreviewState.Failed)
            {
                DrawUnavailableGalleryThumbnail(
                    new Vector2(
                        -1f,
                        Ui(118f)));
            }
            else
            {
                DrawImagePreviewSurface(
                    savedImage.Url,
                    new Vector2(
                        -1f,
                        Ui(118f)));
            }

            if (preview.State ==
                    ImagePreviewState.Ready &&
                ImGui.IsItemClicked(
                    ImGuiMouseButton.Left))
            {
                ToggleGalleryImageSelection(
                    savedImage.Id);

                selectedIndex =
                    selectedGalleryImageIds.IndexOf(
                        savedImage.Id);

                selected =
                    selectedIndex >= 0;
            }

            ImGui.Dummy(
                new Vector2(
                    0f,
                    Ui(5f)));

            ImGui.TextColored(
                Vector4.One,
                TruncateImageUrl(
                    savedImage.Name,
                    25));

            SetUiFontScale(
                0.70f);

            ImGui.TextColored(
                MutedText,
                TruncateImageUrl(
                    GetSavedImageHost(
                        savedImage.Url),
                    28));

            SetUiFontScale(
                1f);

            var smallActionSize =
                new Vector2(
                    Ui(26f),
                    Ui(26f));

            var deleteX =
                cardOrigin.X +
                cardSize.X -
                Ui(9f) -
                smallActionSize.X;

            var actionY =
                cardOrigin.Y +
                cardSize.Y -
                Ui(8f) -
                smallActionSize.Y;

            if (preview.State ==
                ImagePreviewState.Failed)
            {
                ImGui.SetCursorScreenPos(
                    new Vector2(
                        deleteX -
                        smallActionSize.X -
                        Ui(5f),
                        actionY));

                DrawDjActionButton(
                    $"##retrySavedImage{savedImage.Id}",
                    FontAwesomeIcon.SyncAlt,
                    string.Empty,
                    smallActionSize,
                    false,
                    () =>
                    {
                        imageGalleryError =
                            null;

                        imagePreviews.Invalidate(
                            savedImage.Url);

                        imagePreviews.Get(
                            savedImage.Url);
                    });

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "Retry image");
                }
            }

            ImGui.SetCursorScreenPos(
                new Vector2(
                    deleteX,
                    actionY));

            DrawDjActionButton(
                $"##deleteSavedImage{savedImage.Id}",
                FontAwesomeIcon.Trash,
                string.Empty,
                smallActionSize,
                false,
                () =>
                {
                    pendingImageGalleryDeleteId =
                        savedImage.Id;

                    imageGalleryError =
                        null;
                });

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Remove from gallery");
            }

            //
            // Draw selection decoration from the card child's own draw list.
            // This places it above the child background and thumbnail.
            //

            if (selected)
            {
                var drawList =
                    ImGui.GetWindowDrawList();

            

                var badgeCenter =
                    cardOrigin +
                    UiVec(
                        22f,
                        22f);

                drawList.AddCircleFilled(
                    badgeCenter,
                    Ui(15f),
                    ImGui.GetColorU32(
                        Accent));

                var badgeText =
                    (selectedIndex + 1)
                    .ToString();

                var badgeTextSize =
                    ImGui.CalcTextSize(
                        badgeText);

                drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    badgeCenter -
                    badgeTextSize *
                    0.5f,
                    ImGui.GetColorU32(
                        Vector4.One),
                    badgeText);
            }
        }
    }

    private void DrawImageGalleryDeleteConfirmation(
     SavedImageRecord savedImage)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float gap =
            8f;

        var cancelWidth =
            Ui(104f);

        var removeWidth =
            Ui(112f);

        var actionWidth =
            cancelWidth +
            removeWidth +
            gap;

        var questionWidth =
            MathF.Max(
                Ui(120f),
                availableWidth -
                actionWidth -
                Ui(18f));

        var question =
            $"Remove “{savedImage.Name}” from the gallery?";

        ImGui.SetCursorScreenPos(
            new Vector2(
                origin.X,
                origin.Y +
                Ui(11f)));

        SetUiFontScale(
            0.80f);

        ImGui.TextColored(
            Vector4.One,
            TruncateImageUrl(
                question,
                Math.Max(
                    18,
                    (int)(
                        questionWidth /
                        MathF.Max(
                            1f,
                            ImGui.CalcTextSize(
                                "M").X)))));

        SetUiFontScale(
            1f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                origin.X +
                availableWidth -
                actionWidth,
                origin.Y));

        DrawDjActionButton(
            "##cancelGalleryImageRemoval",
            FontAwesomeIcon.Times,
            "Cancel",
            new Vector2(
                cancelWidth,
                Ui(40f)),
            false,
            () =>
                pendingImageGalleryDeleteId =
                    null);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##confirmGalleryImageRemoval",
            FontAwesomeIcon.Trash,
            "Remove",
            new Vector2(
                removeWidth,
                Ui(40f)),
            false,
            () =>
                RemoveImageFromGallery(
                    savedImage.Id),
            false,
            new Vector4(
                1f,
                0.46f,
                0.50f,
                1f));
    }

    private void DrawImageGalleryFooter()
    {
        if (pendingImageGalleryDeleteId is
            { } pendingDeleteId)
        {
            var pendingImage =
                Plugin.Cfg.SavedImages.FirstOrDefault(
                    savedImage =>
                        savedImage.Id ==
                        pendingDeleteId);

            if (pendingImage is null)
            {
                pendingImageGalleryDeleteId =
                    null;
            }
            else
            {
                DrawImageGalleryDeleteConfirmation(
                    pendingImage);

                return;
            }
        }

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float gap =
            8f;

        var cancelWidth =
            Ui(112f);

        var actionWidth =
            Ui(170f);

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() +
            MathF.Max(
                0f,
                availableWidth -
                cancelWidth -
                actionWidth -
                gap));

        DrawDjActionButton(
            "##cancelImageGallery",
            FontAwesomeIcon.Times,
            "Cancel",
            new Vector2(
                cancelWidth,
                Ui(40f)),
            false,
            CloseImageGallery);

        ImGui.SameLine(
            0f,
            gap);

        var selectionCount =
            selectedGalleryImageIds.Count;

        var actionLabel =
            imageGalleryContext ==
            ImageGalleryContext.Slideshow
                ? selectionCount == 0
                    ? "Add Selected"
                    : $"Add Selected ({selectionCount})"
                : "Use Image";

        DrawDjActionButton(
            "##useSelectedGalleryImages",
            imageGalleryContext ==
            ImageGalleryContext.Slideshow
                ? FontAwesomeIcon.Plus
                : FontAwesomeIcon.Image,
            actionLabel,
            new Vector2(
                actionWidth,
                Ui(40f)),
            selectionCount == 0,
            UseSelectedGalleryImages,
            true);
    }

    private static string GetSavedImageHost(
        string url)
    {
        return Uri.TryCreate(
            url,
            UriKind.Absolute,
            out var uri)
            ? uri.Host
            : url;
    }

    private void DrawImagesSlideshows()
    {
        //
        // =========================================================
        // Header
        // =========================================================
        //

        SetUiFontScale(
            1.18f);

        ImGui.TextColored(
            Vector4.One,
            "Images / Slideshows");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 2f));

        SetUiFontScale(
            0.78f);

        ImGui.TextColored(
            MutedText,
            "Show a still image or create a looping slideshow on your Alpha Channel TV.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 12f));


        //
        // =========================================================
        // Main card
        // =========================================================
        //

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(14f, 14f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                10f))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    0.028f,
                    0.038f,
                    0.068f,
                    1f)))
        using (
                    var main =
                ImRaii.Child(
                    "##imagesMainCard",
                new Vector2(
    -1f,
imageDisplayMode == ImageDisplayMode.Slideshow
    ? Ui(850f)
    : Ui(600f)),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!main)
            {
                return;
            }

            DrawImageModeTabs();

            ImGui.Dummy(
                UiVec(0f, 12f));

            ImGui.Separator();

            ImGui.Dummy(
                UiVec(0f, 12f));

            if (imageDisplayMode ==
                ImageDisplayMode.StillImage)
            {
                DrawStillImageMode();
            }
            else
            {
                DrawSlideshowMode();
            }
        }
        DrawImageGalleryOverlay();
        DrawImageHostingGuideOverlay();

    }


    private void DrawImageModeTabs()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float gap =
            8f;

        var width =
            (availableWidth - gap) /
            2f;

        DrawImageModeTab(
            FontAwesomeIcon.Image,
            "Still Image",
            ImageDisplayMode.StillImage,
            new Vector2(
                width,
                Ui(42f)));

        ImGui.SameLine(
            0f,
            gap);

        DrawImageModeTab(
            FontAwesomeIcon.Images,
            "Slideshow",
            ImageDisplayMode.Slideshow,
            new Vector2(
                width,
                Ui(42f)));
    }


    private void DrawImageModeTab(
        FontAwesomeIcon icon,
        string label,
        ImageDisplayMode mode,
        Vector2 size)
    {
        var selected =
            imageDisplayMode == mode;

        var origin =
            ImGui.GetCursorScreenPos();

        var background =
            selected
                ? new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.12f)
                : new Vector4(
                    FrameBg.X,
                    FrameBg.Y,
                    FrameBg.Z,
                    0.55f);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
        using (
            ImRaii.PushColor(
                ImGuiCol.Button,
                background)
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.18f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.24f)))
        {
            if (ImGui.Button(
                    $"##imageMode_{mode}",
                    size))
            {
                imageDisplayMode =
                    mode;
            }
        }

        var drawList =
            ImGui.GetWindowDrawList();

        if (selected)
        {
            drawList.AddRect(
                origin,
                origin + size,
                ImGui.GetColorU32(
                    Accent),
                7f,
                ImDrawFlags.None,
                1.4f);

            drawList.AddRectFilled(
                new Vector2(
                    origin.X,
                    origin.Y +
                    size.Y -
                    Ui(2f)),
                new Vector2(
                    origin.X +
                    size.X,
                    origin.Y +
                    size.Y),
                ImGui.GetColorU32(
                    Accent),
                2f);
        }

        var iconText =
            icon.ToIconString();

        Vector2 iconSize;

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            iconSize =
                ImGui.CalcTextSize(
                    iconText);
        }

        var textSize =
            ImGui.CalcTextSize(
                label);

        var textGap = Ui(8f);

        var totalWidth =
            iconSize.X +
            textGap +
            textSize.X;

        var start =
            new Vector2(
                origin.X +
                (size.X - totalWidth) *
                0.5f,
                origin.Y +
                (size.Y - textSize.Y) *
                0.5f);

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                start,
                ImGui.GetColorU32(
                    selected
                        ? Accent
                        : MutedText),
                iconText);
        }

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            start +
            new Vector2(
                iconSize.X +
                textGap,
                0f),
            ImGui.GetColorU32(
                selected
                    ? Vector4.One
                    : MutedText),
            label);
    }


    private void DrawStillImageMode()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float gap =
            16f;

        var leftWidth =
            availableWidth *
            0.48f;

        var rightWidth =
            availableWidth -
            leftWidth -
            gap;

        using (
            var left =
                ImRaii.Child(
                    "##stillImageControls",
                    new Vector2(
                        leftWidth,
                        0f),
                    false))
        {
            if (left)
            {
                DrawStillImageControls();
            }
        }

        ImGui.SameLine(
            0f,
            gap);

        using (
            var right =
                ImRaii.Child(
                    "##stillImagePreview",
                    new Vector2(
                        rightWidth,
                        0f),
                    false))
        {
            if (right)
            {
                DrawStillImagePreview();
            }
        }
    }


    private void DrawStillImageControls()
    {
        var headingOrigin =
       ImGui.GetCursorScreenPos();

        var headingWidth =
            ImGui.GetContentRegionAvail().X;

        var galleryButtonSize =
            new Vector2(
                Ui(144f),
                Ui(34f));

        ImGui.SetCursorScreenPos(
            headingOrigin);

        SetUiFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Still Image");

        SetUiFontScale(
            1f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                headingOrigin.X +
                headingWidth -
                galleryButtonSize.X,
                headingOrigin.Y));

        DrawDjActionButton(
            "##openStillImageGallery",
            FontAwesomeIcon.Images,
            "Open Gallery",
            galleryButtonSize,
            false,
            () =>
                OpenImageGallery(
                    ImageGalleryContext.StillImage));

        ImGui.SetCursorScreenPos(
            new Vector2(
                headingOrigin.X,
                headingOrigin.Y +
                galleryButtonSize.Y +
                Ui(4f)));

        SetUiFontScale(
            0.75f);

        ImGui.TextColored(
            MutedText,
            "Display one image continuously on the TV.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 18f));

        ImGui.TextColored(
            MutedText,
            "IMAGE URL");

        ImGui.Dummy(
            UiVec(0f, 5f));

        var stillImageValidation =
         DrawImageUrlInput(
             "##stillImageUrl",
             ref imageUrlInput);

        ImGui.Dummy(
            UiVec(0f, 9f));

        SetUiFontScale(
            0.73f);

        ImGui.TextColored(
            MutedText,
            "Images must be available from a public URL.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 22f));

        var stillActionWidth =
    ImGui.GetContentRegionAvail().X;

        var stillActionGap = Ui(8f);

        var saveButtonWidth =
            MathF.Min(
                Ui(154f),
                stillActionWidth *
                0.38f);

        var showButtonWidth =
            stillActionWidth -
            saveButtonWidth -
            stillActionGap;

        var stillImageReady =
            stillImageValidation.State ==
            ImagePreviewState.Ready;

        var stillImageAlreadySaved =
            IsImageSavedToGallery(
                imageUrlInput);

        DrawDjActionButton(
            "##showStillImageOnTv",
            FontAwesomeIcon.Image,
            "Show on TV",
            new Vector2(
                showButtonWidth,
                Ui(40f)),
            !stillImageReady,
            StartStillImage,
            true);

        ImGui.SameLine(
            0f,
            stillActionGap);

        DrawDjActionButton(
            "##saveStillImageToGallery",
            FontAwesomeIcon.Save,
            stillImageAlreadySaved
                ? "Saved"
                : "Save to Gallery",
            new Vector2(
                saveButtonWidth,
                Ui(40f)),
            !stillImageReady ||
            stillImageAlreadySaved,
            () =>
                SaveImageToGallery(
                    imageUrlInput));

        ImGui.Dummy(
            UiVec(0f, 12f));

        SetUiFontScale(
            0.72f);

        var stillImageVisibleError =
      imageMediaError ??
      imageGalleryError;

        if (!string.IsNullOrWhiteSpace(
                stillImageVisibleError))
        {
            ImGui.TextWrapped(
                stillImageVisibleError);
        }
        else
        {
            ImGui.TextColored(
                MutedText,
                "Supported hosts: MyImgs, ImgPile, Imgur and Postimages.");
        }
        ImGui.Dummy(
    new Vector2(
        0f,
        Ui(10f)));

        DrawDjActionButton(
            "##openStillImageHostingGuide",
            FontAwesomeIcon.BookOpen,
            "Image Hosting Guide",
            new Vector2(
                Ui(190f),
                Ui(34f)),
            false,
            () =>
                imageHostingGuideOpen =
                    true);
        SetUiFontScale(
            1f);
    }


    private void DrawStillImagePreview()
    {
        SetUiFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Preview");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 2f));

        SetUiFontScale(
            0.75f);

        ImGui.TextColored(
            MutedText,
            "Check the image before displaying it.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 14f));

        DrawImagePreviewSurface(
            imageUrlInput,
            UiVec(-1f, 330f));
    }


    private void DrawSlideshowMode()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const float gap =
            16f;

        var leftWidth =
            availableWidth *
            0.58f;

        var rightWidth =
            availableWidth -
            leftWidth -
            gap;

        //
        // Both panels have one fixed height. Nothing here depends on the
        // current window height, so resizing cannot reveal or hide controls.
        //
        var panelHeight =
            Ui(720f);

        using (
            var left =
                ImRaii.Child(
                    "##slideshowImagesPanel",
                    new Vector2(
                        leftWidth,
                        panelHeight),
                    false,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (left)
            {
                DrawSlideshowImagesPanel();
            }
        }

        ImGui.SameLine(
            0f,
            gap);

        using (
            var right =
                ImRaii.Child(
                    "##slideshowSettingsPanel",
                    new Vector2(
                        rightWidth,
                        panelHeight),
                    false,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (right)
            {
                DrawSlideshowPreviewAndSettings();
            }
        }
    }

    private void DrawSlideshowImagesPanel()
    {
        var headingOrigin =
    ImGui.GetCursorScreenPos();

        var headingWidth =
            ImGui.GetContentRegionAvail().X;

        var galleryButtonSize =
            new Vector2(
                Ui(144f),
                Ui(34f));

        ImGui.SetCursorScreenPos(
            headingOrigin);

        SetUiFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Images");

        SetUiFontScale(
            1f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                headingOrigin.X +
                headingWidth -
                galleryButtonSize.X,
                headingOrigin.Y));

        DrawDjActionButton(
            "##openSlideshowGallery",
            FontAwesomeIcon.Images,
            "Open Gallery",
            galleryButtonSize,
            false,
            () =>
                OpenImageGallery(
                    ImageGalleryContext.Slideshow));

        ImGui.SetCursorScreenPos(
            new Vector2(
                headingOrigin.X,
                headingOrigin.Y +
                galleryButtonSize.Y +
                Ui(4f)));

        SetUiFontScale(
            0.75f);

        ImGui.TextColored(
            MutedText,
            "Add up to 5 images and arrange the order.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 10f));

        for (var i = 0;
             i < MaxSlideshowImages;
             i++)
        {
            DrawSlideshowImageSlot(
                i);

            if (i <
                MaxSlideshowImages - 1)
            {
                ImGui.Dummy(
                    UiVec(0f, 5f));
            }
        }

        ImGui.Dummy(
            UiVec(0f, 12f));

        var hasEmptySlot =
            Array.Exists(
                slideshowImageUrls,
                string.IsNullOrWhiteSpace);

        ImagePreviewResult slideshowImageValidation;

        using (
            ImRaii.Disabled(
                !hasEmptySlot))
        {
            slideshowImageValidation =
                DrawImageUrlInput(
                    "##slideshowAddUrl",
                    ref imageUrlInput);
        }

        ImGui.Dummy(
            UiVec(0f, 7f));

        var slideshowActionWidth =
       ImGui.GetContentRegionAvail().X;

        var slideshowActionGap = Ui(8f);

        var slideshowSaveWidth =
            MathF.Min(
                Ui(154f),
                slideshowActionWidth *
                0.38f);

        var addImageWidth =
            slideshowActionWidth -
            slideshowSaveWidth -
            slideshowActionGap;

        var slideshowImageReady =
            slideshowImageValidation.State ==
            ImagePreviewState.Ready;

        var slideshowImageAlreadySaved =
            IsImageSavedToGallery(
                imageUrlInput);

        DrawDjActionButton(
            "##addSlideshowImage",
            FontAwesomeIcon.Plus,
            "Add Image",
            new Vector2(
                addImageWidth,
                Ui(36f)),
            !hasEmptySlot ||
            !slideshowImageReady,
            AddSlideshowImage,
            true);

        ImGui.SameLine(
            0f,
            slideshowActionGap);

        DrawDjActionButton(
            "##saveSlideshowImageToGallery",
            FontAwesomeIcon.Save,
            slideshowImageAlreadySaved
                ? "Saved"
                : "Save to Gallery",
            new Vector2(
                slideshowSaveWidth,
                Ui(36f)),
            !slideshowImageReady ||
            slideshowImageAlreadySaved,
            () =>
                SaveImageToGallery(
                    imageUrlInput));

        ImGui.Dummy(
            UiVec(0f, 6f));

        SetUiFontScale(
            0.70f);

        if (!string.IsNullOrWhiteSpace(
          imageGalleryError))
        {
            ImGui.TextWrapped(
                imageGalleryError);
        }
        else
        {
            ImGui.TextColored(
                MutedText,
                "Images are loaded directly from their URLs. Maximum 5 images.");
        }

        ImGui.Dummy(
    new Vector2(
        0f,
        Ui(8f)));

        DrawDjActionButton(
            "##openSlideshowImageHostingGuide",
            FontAwesomeIcon.BookOpen,
            "Image Hosting Guide",
            new Vector2(
                Ui(190f),
                Ui(34f)),
            false,
            () =>
                imageHostingGuideOpen =
                    true);

        SetUiFontScale(
            1f);
    }


    private void DrawSlideshowImageSlot(
        int index)
    {
        var rowHeight = Ui(58f);

        var thumbnailWidth = Ui(72f);

        var width =
            ImGui.GetContentRegionAvail().X;

        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                rowHeight);

        var url =
            slideshowImageUrls[index];

        var populated =
            !string.IsNullOrWhiteSpace(
                url);

        var selected =
            populated &&
            selectedSlideshowImage ==
            index;

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                selected
                    ? new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.09f)
                    : new Vector4(
                        0.040f,
                        0.052f,
                        0.086f,
                        1f)),
            8f);

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                selected
                    ? new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.75f)
                    : new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.12f)),
            8f,
            ImDrawFlags.None,
            selected
                ? 1.3f
                : 1f);


        //
        // Thumbnail
        //

        var thumbnailMin =
            origin +
            UiVec(5f, 5f);

        var thumbnailMax =
            new Vector2(
                thumbnailMin.X +
                thumbnailWidth,
                origin.Y +
                rowHeight -
                Ui(5f));

        if (populated)
        {
            var preview =
                imagePreviews.Get(
                    url);

            if (preview.State ==
                    ImagePreviewState.Ready &&
                preview.Texture is not null)
            {
                drawList.AddImageRounded(
                    preview.Texture.Handle,
                    thumbnailMin,
                    thumbnailMax,
                    Vector2.Zero,
                    Vector2.One,
                    uint.MaxValue,
                    6f);
            }
            else
            {
                //
                // Loading and failed images retain the standard placeholder in
                // the compact list. The larger preview surface displays details.
                //
                DrawEmptyImageThumbnail(
                    thumbnailMin,
                    thumbnailMax,
                    index + 1);
            }
        }
        else
        {
            DrawEmptyImageThumbnail(
                thumbnailMin,
                thumbnailMax,
                index + 1);
        }


        //
        // Text
        //

        var contentX =
            thumbnailMax.X +
            11f;

        if (populated)
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    contentX,
                    origin.Y +
                    Ui(12f)),
                ImGui.GetColorU32(
                    Vector4.One),
                $"Image {index + 1}");

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    contentX,
                    origin.Y +
                    Ui(33f)),
                ImGui.GetColorU32(
                    MutedText),
                TruncateImageUrl(
                    url,
                    42));
        }
        else
        {
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    contentX,
                    origin.Y +
                    Ui(20f)),
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.62f)),
                $"Image {index + 1} - Empty");
        }


        //
        // Button geometry
        //

        var buttonSize = Ui(28f);

        var buttonGap = Ui(5f);

        var buttonRightPadding = Ui(10f);

        var buttonY =
            origin.Y +
            (rowHeight -
             buttonSize) /
            2f;

        var trashX =
            origin.X +
            width -
            buttonSize -
            buttonRightPadding;

        var downX =
            trashX -
            buttonSize -
            buttonGap;

        var upX =
            downX -
            buttonSize -
            buttonGap;


        //
        // Row selection area
        //
        // This ends before the visible controls so it cannot consume their clicks.
        //

        var selectionWidth =
            populated
                ? Math.Max(
                    1f,
                    upX -
                    origin.X -
                    buttonGap)
                : width;

        ImGui.SetCursorScreenPos(
            origin);

        if (ImGui.InvisibleButton(
                $"##slideshowSlot_{index}",
                new Vector2(
                    selectionWidth,
                    rowHeight)) &&
            populated)
        {
            selectedSlideshowImage =
                index;
        }


        //
        // Up, down and delete controls
        //

        if (populated)
        {
            if (DrawSlideshowRowIconButton(
                    $"##imageUp{index}",
                    FontAwesomeIcon.ArrowUp,
                    new Vector2(
                        upX,
                        buttonY),
                    buttonSize,
                    index == 0))
            {
                MoveSlideshowImage(
                    index,
                    index - 1);
            }

            if (DrawSlideshowRowIconButton(
                    $"##imageDown{index}",
                    FontAwesomeIcon.ArrowDown,
                    new Vector2(
                        downX,
                        buttonY),
                    buttonSize,
                    index >=
                        MaxSlideshowImages - 1 ||
                    string.IsNullOrWhiteSpace(
                        slideshowImageUrls[index + 1])))
            {
                MoveSlideshowImage(
                    index,
                    index + 1);
            }

            if (DrawSlideshowRowIconButton(
                    $"##imageDelete{index}",
                    FontAwesomeIcon.Trash,
                    new Vector2(
                        trashX,
                        buttonY),
                    buttonSize,
                    false))
            {
                RemoveSlideshowImage(
                    index);
            }
        }

        //
        // Advance layout cursor
        //

        ImGui.SetCursorScreenPos(
            new Vector2(
                origin.X,
                origin.Y +
                rowHeight));

        ImGui.Dummy(
            new Vector2(
                width,
                1f));
    }

    private static bool DrawSlideshowRowIconButton(
    string id,
    FontAwesomeIcon icon,
    Vector2 position,
    float size,
    bool disabled)
    {
        ImGui.SetCursorScreenPos(
            position);

        bool clicked;
        Vector2 buttonMin;
        Vector2 iconSize;

        var iconText =
            icon.ToIconString();

        using (
            ImRaii.Disabled(
                disabled))
        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            //
            // The button label contains only its hidden ID. Drawing the icon
            // ourselves avoids FontAwesome's uneven glyph bearings affecting
            // ImGui's normal label alignment.
            //
            clicked =
                ImGui.Button(
                    id,
                    new Vector2(
                        size,
                        size));

            buttonMin =
                ImGui.GetItemRectMin();

            iconSize =
                ImGui.CalcTextSize(
                    iconText);

            var iconPosition =
                new Vector2(
                    buttonMin.X +
                        (size -
                         iconSize.X) /
                        2f,
                    buttonMin.Y +
                        (size -
                         iconSize.Y) /
                        2f +
                        Ui(1.5f));

            var iconColor =
                disabled
                    ? ImGui.GetStyle()
                        .Colors[
                            (int)ImGuiCol.TextDisabled]
                    : ImGui.GetStyle()
                        .Colors[
                            (int)ImGuiCol.Text];

            ImGui.GetWindowDrawList()
                .AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                    iconPosition,
                    ImGui.GetColorU32(
                        iconColor),
                    iconText);
        }

        return clicked &&
               !disabled;
    }

    private void DrawEmptyImageThumbnail(
        Vector2 min,
        Vector2 max,
        int number)
    {
        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    0.025f,
                    0.033f,
                    0.058f,
                    1f)),
            6f);

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.10f)),
            6f);

        var icon =
            FontAwesomeIcon.Image
                .ToIconString();

        Vector2 iconSize;

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            iconSize =
                ImGui.CalcTextSize(
                    icon);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    min.X +
                    ((max.X - min.X) -
                     iconSize.X) /
                    2f,
                    min.Y +
                    ((max.Y - min.Y) -
                     iconSize.Y) /
                    2f),
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.40f)),
                icon);
        }
    }


    private void DrawSlideshowPreviewAndSettings()
    {
        SetUiFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Preview");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 2f));

        SetUiFontScale(
            0.75f);

        ImGui.TextColored(
            MutedText,
            "See how the selected slide will look.");

        SetUiFontScale(
            1f);

        ImGui.Dummy(
            UiVec(0f, 10f));

        var previewUrl =
            slideshowImageUrls[
                Math.Clamp(
                    selectedSlideshowImage,
                    0,
                    MaxSlideshowImages - 1)];

        DrawImagePreviewSurface(
            previewUrl,
            UiVec(-1f, 178f));

        ImGui.Dummy(
            UiVec(0f, 5f));

        var imageCount =
            GetSlideshowImageCount();

        var displayIndex =
            imageCount == 0
                ? 0
                : Math.Min(
                    selectedSlideshowImage + 1,
                    imageCount);

        var counter =
            $"{displayIndex} / {imageCount}";

        var counterSize =
            ImGui.CalcTextSize(
                counter);

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() +
            MathF.Max(
                0f,
                (ImGui.GetContentRegionAvail().X -
                 counterSize.X) /
                2f));

        ImGui.TextColored(
            MutedText,
            counter);

        ImGui.Dummy(
            UiVec(0f, 10f));


        //
        // Settings card
        //

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(12f, 12f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    0.040f,
                    0.052f,
                    0.086f,
                    1f)))
        using (
  var settings =
    ImRaii.Child(
        "##slideshowSettings",
        new Vector2(
            -1f,
            -1f),
        true,
        ImGuiWindowFlags.NoScrollbar |
        ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (!settings)
            {
                return;
            }

            ImGui.TextColored(
                Vector4.One,
                "Slideshow Settings");

            ImGui.Dummy(
                UiVec(0f, 3f));

            SetUiFontScale(
                0.72f);

            ImGui.TextColored(
                MutedText,
                "Control how your slideshow plays.");

            SetUiFontScale(
                1f);

            ImGui.Dummy(
                UiVec(0f, 11f));

            SetUiFontScale(
                0.76f);

            ImGui.TextColored(
                MutedText,
                "Change image every");

            SetUiFontScale(
                1f);

            ImGui.SetNextItemWidth(
                -1f);

            ImGui.SliderInt(
                "##slideshowSeconds",
                ref slideshowSeconds,
                2,
                60,
                "%d sec");

            ImGui.Dummy(
                UiVec(0f, 8f));

            SetUiFontScale(
                0.76f);

            ImGui.TextColored(
                MutedText,
                "Transition");

            SetUiFontScale(
                1f);

            ImGui.SetNextItemWidth(
                -1f);

            ImGui.Combo(
                "##slideshowTransition",
                ref slideshowTransition,
                slideshowTransitions,
                slideshowTransitions.Length);

            ImGui.Dummy(
                UiVec(0f, 8f));

            ImGui.Checkbox(
                "Loop slideshow",
                ref slideshowLoop);

            ImGui.Dummy(
                UiVec(0f, 11f));

            using (
                ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    7f))
            using (
                ImRaii.PushColor(
                    ImGuiCol.Button,
                    Accent)
                    .Push(
                        ImGuiCol.ButtonHovered,
                        AccentHover)
                    .Push(
                        ImGuiCol.ButtonActive,
                        AccentActive))
            using (
                ImRaii.Disabled(
                    imageCount == 0))
            {
                if (ImGui.Button(
                        "Start Slideshow",
                        UiVec(-1f, 36f)))
                {
                    StartImageSlideshow();
                }
            }
        }
    }


    private void DrawImagePreviewSurface(
        string url,
        Vector2 requestedSize)
    {
        var width =
            requestedSize.X <= 0f
                ? ImGui.GetContentRegionAvail().X
                : requestedSize.X;

        var height =
            requestedSize.Y;

        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    0.018f,
                    0.026f,
                    0.048f,
                    1f)),
            8f);

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.13f)),
            8f);

        if (!string.IsNullOrWhiteSpace(
                url))
        {
            var preview =
                imagePreviews.Get(
                    url);

            if (preview.State ==
                    ImagePreviewState.Ready &&
                preview.Texture is not null)
            {
                drawList.AddImageRounded(
                    preview.Texture.Handle,
                    origin +
                    UiVec(5f, 5f),
                    origin +
                    size -
                    UiVec(5f, 5f),
                    Vector2.Zero,
                    Vector2.One,
                    uint.MaxValue,
                    7f);
            }
            else if (preview.State ==
                     ImagePreviewState.Failed)
            {
                DrawImagePreviewPlaceholder(
                    origin,
                    size,
                    preview.Error ??
                    "The image could not be loaded.");
            }
            else
            {
                DrawImagePreviewPlaceholder(
                    origin,
                    size,
                    "Loading image...");
            }
        }
        else
        {
            DrawImagePreviewPlaceholder(
                origin,
                size,
                "Select an image to preview");
        }

        ImGui.Dummy(
            size);
    }


    private void DrawImagePreviewPlaceholder(
        Vector2 origin,
        Vector2 size,
        string message)
    {
        var drawList =
            ImGui.GetWindowDrawList();

        var icon =
            FontAwesomeIcon.Image
                .ToIconString();

        Vector2 iconSize;

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            iconSize =
                ImGui.CalcTextSize(
                    icon);

            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                new Vector2(
                    origin.X +
                    (size.X -
                     iconSize.X) *
                    0.5f,
                    origin.Y +
                    size.Y *
                    0.5f -
                    Ui(27f)),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.70f)),
                icon);
        }

        var messageSize =
            ImGui.CalcTextSize(
                message);

        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
            new Vector2(
                origin.X +
                (size.X -
                 messageSize.X) *
                0.5f,
                origin.Y +
                size.Y *
                0.5f +
                Ui(7f)),
            ImGui.GetColorU32(
                MutedText),
            message);
    }


    private ImagePreviewResult DrawImageUrlInput(
     string id,
     ref string value)
    {
        //
        // Reserve room for the validation icon, spacing and paste button.
        //
        ImGui.SetNextItemWidth(
            -82f);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f)
                .Push(
                    ImGuiStyleVar.FramePadding,
                    UiVec(10f, 9f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.FrameBg,
                new Vector4(
                    0.042f,
                    0.055f,
                    0.094f,
                    1f))
                .Push(
                    ImGuiCol.FrameBgHovered,
                    new Vector4(
                        0.058f,
                        0.075f,
                        0.12f,
                        1f))
                .Push(
                    ImGuiCol.FrameBgActive,
                    new Vector4(
                        0.058f,
                        0.075f,
                        0.12f,
                        1f)))
        {
            ImGui.InputTextWithHint(
                id,
                "Image URL (.jpg, .png, .webp, .gif)",
                ref value,
                2000);
        }

        //
        // Calling Get starts validation asynchronously. Editing the URL creates
        // a different cache key, so a previous failure does not remain attached
        // to the new value.
        //
        var preview =
            imagePreviews.Get(
                value);

        ImGui.SameLine(
            0f,
            7f);

        var statusIcon =
            preview.State switch
            {
                ImagePreviewState.Loading =>
                    FontAwesomeIcon.CircleNotch,

                ImagePreviewState.Ready =>
                    FontAwesomeIcon.CheckCircle,

                ImagePreviewState.Failed =>
                    FontAwesomeIcon.ExclamationTriangle,

                _ =>
                    FontAwesomeIcon.Circle
            };

        var statusColor =
            preview.State switch
            {
                ImagePreviewState.Loading =>
                    Accent,

                ImagePreviewState.Ready =>
                    Good,

                ImagePreviewState.Failed =>
                    Danger,

                _ =>
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.25f)
            };

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            ImGui.TextColored(
                statusColor,
                statusIcon.ToIconString());
        }

        if (ImGui.IsItemHovered())
        {
            var tooltip =
                preview.State switch
                {
                    ImagePreviewState.Loading =>
                        "Checking the image URL...",

                    ImagePreviewState.Ready =>
                        "This image passed all safety checks.",

                    ImagePreviewState.Failed =>
                        preview.Error ??
                        "This image does not meet the requirements.",

                    _ =>
                        "Enter an image URL."
                };

            ImGui.SetTooltip(
                tooltip);
        }

        ImGui.SameLine(
            0f,
            7f);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
        using (
            ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(
                    0.070f,
                    0.080f,
                    0.13f,
                    1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(
                        0.095f,
                        0.11f,
                        0.17f,
                        1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(
                        0.095f,
                        0.11f,
                        0.17f,
                        1f)))
        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            if (ImGui.Button(
                    $"{FontAwesomeIcon.Clipboard.ToIconString()}##paste{id}",
                    UiVec(44f, 0f)))
            {
                var clipboard =
                    ImGui.GetClipboardText();

                if (!string.IsNullOrWhiteSpace(
                        clipboard))
                {
                    value =
                        clipboard.Trim();

                    preview =
                        imagePreviews.Get(
                            value);
                }
            }
        }

        if (preview.State ==
    ImagePreviewState.Failed)
        {
            ImGui.Dummy(
                UiVec(0f, 3f));

            using (
                ImRaii.PushStyle(
                    ImGuiStyleVar.FramePadding,
                    UiVec(3f, 2f)))
            using (
                ImRaii.PushColor(
                    ImGuiCol.Button,
                    Vector4.Zero)
                    .Push(
                        ImGuiCol.ButtonHovered,
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.14f))
                    .Push(
                        ImGuiCol.ButtonActive,
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.22f))
                    .Push(
                        ImGuiCol.Text,
                        Accent))
            {
                if (ImGui.SmallButton(
                        $"Retry image validation##retry_{id}"))
                {
                    //
                    // Remove the cached failure and immediately begin a completely
                    // new validation attempt for the same URL.
                    //
                    imagePreviews.Invalidate(
                        value);

                    preview =
                        imagePreviews.Get(
                            value);
                }
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Try downloading and validating this image again.");
            }
        }

        return preview;

    }


    


    private void AddSlideshowImage()
    {
        var url =
            imageUrlInput.Trim();

        if (url.Length == 0)
        {
            return;
        }

        for (var i = 0;
             i < MaxSlideshowImages;
             i++)
        {
            if (!string.IsNullOrWhiteSpace(
                    slideshowImageUrls[i]))
            {
                continue;
            }

            slideshowImageUrls[i] =
                url;

            selectedSlideshowImage =
                i;

            imageUrlInput =
                string.Empty;

            return;
        }
    }


    private void RemoveSlideshowImage(
        int index)
    {
        if (index < 0 ||
            index >=
            MaxSlideshowImages)
        {
            return;
        }

        for (var i = index;
             i <
             MaxSlideshowImages - 1;
             i++)
        {
            slideshowImageUrls[i] =
                slideshowImageUrls[i + 1];
        }

        slideshowImageUrls[
            MaxSlideshowImages - 1] =
            string.Empty;

        var count =
            GetSlideshowImageCount();

        if (count == 0)
        {
            selectedSlideshowImage =
                0;
        }
        else
        {
            selectedSlideshowImage =
                Math.Clamp(
                    selectedSlideshowImage,
                    0,
                    count - 1);
        }
    }


    private void MoveSlideshowImage(
        int from,
        int to)
    {
        if (from < 0 ||
            from >=
            MaxSlideshowImages ||
            to < 0 ||
            to >=
            MaxSlideshowImages)
        {
            return;
        }

        (
            slideshowImageUrls[from],
            slideshowImageUrls[to]
        ) =
        (
            slideshowImageUrls[to],
            slideshowImageUrls[from]
        );

        selectedSlideshowImage =
            to;
    }


    private int GetSlideshowImageCount()
    {
        var count =
            0;

        for (var i = 0;
             i < MaxSlideshowImages;
             i++)
        {
            if (!string.IsNullOrWhiteSpace(
                    slideshowImageUrls[i]))
            {
                count++;
            }
        }

        return count;
    }


    private static string TruncateImageUrl(
        string url,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(
                url) ||
            url.Length <=
            maxLength)
        {
            return url;
        }

        return url[
            ..Math.Max(
                0,
                maxLength - 1)]
            .TrimEnd() +
            "…";
    }
    private void StartStillImage()
    {
        imageMediaError =
            null;

        if (!ImageMediaSelection.TryBuildStillImageUrl(
                imageUrlInput,
                out var transmittedUrl,
                out var error))
        {
            imageMediaError =
                error;

            return;
        }

        var selection =
            ImageMediaSelection.TryParse(
                transmittedUrl,
                out var parsed)
                ? parsed
                : null;

        if (selection is null)
        {
            imageMediaError =
                "The image playback information could not be created.";

            return;
        }

        queue.PlayTransient(
            new Video.VideoQueueEntry(
                transmittedUrl,
                "Still Image",
                "Image",
                null,
                selection.PrimaryImageUrl,
                0d,
                true));

    }

    private void StartImageSlideshow()
    {
        imageMediaError =
            null;

        var transition =
            slideshowTransition == 1
                ? ImageTransition.Fade
                : ImageTransition.Instant;

        if (!ImageMediaSelection.TryBuildSlideshowUrl(
                slideshowImageUrls,
                slideshowSeconds,
                transition,
                slideshowLoop,
                out var transmittedUrl,
                out var error))
        {
            imageMediaError =
                error;

            return;
        }

        if (!ImageMediaSelection.TryParse(
                transmittedUrl,
                out var selection) ||
            selection is null)
        {
            imageMediaError =
                "The slideshow playback information could not be created.";

            return;
        }

        var imageCount =
            selection.ImageUrls.Count;

        TimeSpan? duration =
            selection.Loop
                ? null
                : TimeSpan.FromSeconds(
                    selection.SecondsPerImage *
                    imageCount);

        queue.PlayTransient(
            new Video.VideoQueueEntry(
                transmittedUrl,
                imageCount == 1
                    ? "Image Slideshow"
                    : $"Slideshow ({imageCount} images)",
                "Slideshow",
                duration,
                selection.PrimaryImageUrl,
                0d,
                true));

    }
}
