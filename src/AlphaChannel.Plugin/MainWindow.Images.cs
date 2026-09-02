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

    private readonly string[] slideshowTransitions =
    [
        "Instant",
        "Fade"
    ];


    private void DrawImagesSlideshows()
    {
        //
        // =========================================================
        // Header
        // =========================================================
        //

        ImGui.SetWindowFontScale(
            1.18f);

        ImGui.TextColored(
            Vector4.One,
            "Images / Slideshows");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                2f));

        ImGui.SetWindowFontScale(
            0.78f);

        ImGui.TextColored(
            MutedText,
            "Show a still image or create a looping slideshow on your Alpha Channel TV.");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));


        //
        // =========================================================
        // Main card
        // =========================================================
        //

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    14f,
                    14f)))
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
                        Ui(600f)),
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
                new Vector2(
                    0f,
                    12f));

            ImGui.Separator();

            ImGui.Dummy(
                new Vector2(
                    0f,
                    12f));

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

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        DrawImageSyncInfo();
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
                42f));

        ImGui.SameLine(
            0f,
            gap);

        DrawImageModeTab(
            FontAwesomeIcon.Images,
            "Slideshow",
            ImageDisplayMode.Slideshow,
            new Vector2(
                width,
                42f));
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
                    2f),
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

        const float textGap =
            8f;

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
            drawList.AddText(
                start,
                ImGui.GetColorU32(
                    selected
                        ? Accent
                        : MutedText),
                iconText);
        }

        drawList.AddText(
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
        ImGui.SetWindowFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Still Image");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                2f));

        ImGui.SetWindowFontScale(
            0.75f);

        ImGui.TextColored(
            MutedText,
            "Display one image continuously on the TV.");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                18f));

        ImGui.TextColored(
            MutedText,
            "IMAGE URL");

        ImGui.Dummy(
            new Vector2(
                0f,
                5f));

        DrawImageUrlInput(
            "##stillImageUrl",
            ref imageUrlInput);

        ImGui.Dummy(
            new Vector2(
                0f,
                9f));

        ImGui.SetWindowFontScale(
            0.73f);

        ImGui.TextColored(
            MutedText,
            "Images must be available from a public URL.");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                22f));

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
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
                string.IsNullOrWhiteSpace(
                    imageUrlInput)))
        {
            ImGui.Button(
                "Show on TV",
                new Vector2(
                    -1f,
                    40f));
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        ImGui.SetWindowFontScale(
            0.72f);

        ImGui.TextColored(
            MutedText,
            "Playback wiring will be connected after the page design is finalized.");

        ImGui.SetWindowFontScale(
            1f);
    }


    private void DrawStillImagePreview()
    {
        ImGui.SetWindowFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Preview");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                2f));

        ImGui.SetWindowFontScale(
            0.75f);

        ImGui.TextColored(
            MutedText,
            "Check the image before displaying it.");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                14f));

        DrawImagePreviewSurface(
            imageUrlInput,
            new Vector2(
                -1f,
                330f));
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

        using (
            var left =
                ImRaii.Child(
                    "##slideshowImagesPanel",
                    new Vector2(
                        leftWidth,
                        0f),
                    false))
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
                        0f),
                    false))
        {
            if (right)
            {
                DrawSlideshowPreviewAndSettings();
            }
        }
    }


    private void DrawSlideshowImagesPanel()
    {
        ImGui.SetWindowFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Images");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                2f));

        ImGui.SetWindowFontScale(
            0.75f);

        ImGui.TextColored(
            MutedText,
            "Add up to 5 images and arrange the order.");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));

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
                    new Vector2(
                        0f,
                        5f));
            }
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        var hasEmptySlot =
            Array.Exists(
                slideshowImageUrls,
                string.IsNullOrWhiteSpace);

        using (
            ImRaii.Disabled(
                !hasEmptySlot))
        {
            DrawImageUrlInput(
                "##slideshowAddUrl",
                ref imageUrlInput);
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                7f));

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
                !hasEmptySlot ||
                string.IsNullOrWhiteSpace(
                    imageUrlInput)))
        {
            if (ImGui.Button(
                    "Add Image",
                    new Vector2(
                        -1f,
                        36f)))
            {
                AddSlideshowImage();
            }
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                6f));

        ImGui.SetWindowFontScale(
            0.70f);

        ImGui.TextColored(
            MutedText,
            "Images are loaded directly from their URLs. Maximum 5 images.");

        ImGui.SetWindowFontScale(
            1f);
    }


    private void DrawSlideshowImageSlot(
        int index)
    {
        const float rowHeight =
            58f;

        const float thumbnailWidth =
            72f;

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
            new Vector2(
                5f,
                5f);

        var thumbnailMax =
            new Vector2(
                thumbnailMin.X +
                thumbnailWidth,
                origin.Y +
                rowHeight -
                5f);

        if (populated)
        {
            var thumbnail =
                thumbnails.Get(
                    url);

            if (thumbnail is not null)
            {
                drawList.AddImageRounded(
                    thumbnail.Handle,
                    thumbnailMin,
                    thumbnailMax,
                    Vector2.Zero,
                    Vector2.One,
                    uint.MaxValue,
                    6f);
            }
            else
            {
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
            drawList.AddText(
                new Vector2(
                    contentX,
                    origin.Y +
                    12f),
                ImGui.GetColorU32(
                    Vector4.One),
                $"Image {index + 1}");

            drawList.AddText(
                new Vector2(
                    contentX,
                    origin.Y +
                    33f),
                ImGui.GetColorU32(
                    MutedText),
                TruncateImageUrl(
                    url,
                    42));
        }
        else
        {
            drawList.AddText(
                new Vector2(
                    contentX,
                    origin.Y +
                    20f),
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.62f)),
                $"Image {index + 1} - Empty");
        }


        //
        // Invisible row selection area
        //

        ImGui.SetCursorScreenPos(
            origin);

        if (ImGui.InvisibleButton(
                $"##slideshowSlot_{index}",
                size) &&
            populated)
        {
            selectedSlideshowImage =
                index;
        }


        //
        // Buttons
        //

        if (populated)
        {
            const float buttonSize =
                28f;

            const float buttonGap =
                5f;

            var trashX =
                origin.X +
                width -
                buttonSize -
                7f;

            var downX =
                trashX -
                buttonSize -
                buttonGap;

            var upX =
                downX -
                buttonSize -
                buttonGap;

            ImGui.SetCursorScreenPos(
                new Vector2(
                    upX,
                    origin.Y +
                    15f));

            using (
                ImRaii.Disabled(
                    index == 0))
            using (
                ImRaii.PushFont(
                    UiBuilder.IconFont))
            {
                if (ImGui.Button(
                        $"{FontAwesomeIcon.ArrowUp.ToIconString()}##imageUp{index}",
                        new Vector2(
                            buttonSize,
                            buttonSize)))
                {
                    MoveSlideshowImage(
                        index,
                        index - 1);
                }
            }

            ImGui.SetCursorScreenPos(
                new Vector2(
                    downX,
                    origin.Y +
                    15f));

            using (
                ImRaii.Disabled(
                    index >=
                    MaxSlideshowImages - 1 ||
                    string.IsNullOrWhiteSpace(
                        slideshowImageUrls[index + 1])))
            using (
                ImRaii.PushFont(
                    UiBuilder.IconFont))
            {
                if (ImGui.Button(
                        $"{FontAwesomeIcon.ArrowDown.ToIconString()}##imageDown{index}",
                        new Vector2(
                            buttonSize,
                            buttonSize)))
                {
                    MoveSlideshowImage(
                        index,
                        index + 1);
                }
            }

            ImGui.SetCursorScreenPos(
                new Vector2(
                    trashX,
                    origin.Y +
                    15f));

            using (
                ImRaii.PushFont(
                    UiBuilder.IconFont))
            {
                if (ImGui.Button(
                        $"{FontAwesomeIcon.Trash.ToIconString()}##imageDelete{index}",
                        new Vector2(
                            buttonSize,
                            buttonSize)))
                {
                    RemoveSlideshowImage(
                        index);
                }
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

            drawList.AddText(
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
        ImGui.SetWindowFontScale(
            1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Preview");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                2f));

        ImGui.SetWindowFontScale(
            0.75f);

        ImGui.TextColored(
            MutedText,
            "See how the selected slide will look.");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                10f));

        var previewUrl =
            slideshowImageUrls[
                Math.Clamp(
                    selectedSlideshowImage,
                    0,
                    MaxSlideshowImages - 1)];

        DrawImagePreviewSurface(
            previewUrl,
            new Vector2(
                -1f,
                178f));

        ImGui.Dummy(
            new Vector2(
                0f,
                5f));

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
            new Vector2(
                0f,
                10f));


        //
        // Settings card
        //

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    12f,
                    12f)))
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
                        Ui(235f)),
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
                new Vector2(
                    0f,
                    3f));

            ImGui.SetWindowFontScale(
                0.72f);

            ImGui.TextColored(
                MutedText,
                "Control how your slideshow plays.");

            ImGui.SetWindowFontScale(
                1f);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    11f));

            ImGui.SetWindowFontScale(
                0.76f);

            ImGui.TextColored(
                MutedText,
                "Change image every");

            ImGui.SetWindowFontScale(
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
                new Vector2(
                    0f,
                    8f));

            ImGui.SetWindowFontScale(
                0.76f);

            ImGui.TextColored(
                MutedText,
                "Transition");

            ImGui.SetWindowFontScale(
                1f);

            ImGui.SetNextItemWidth(
                -1f);

            ImGui.Combo(
                "##slideshowTransition",
                ref slideshowTransition,
                slideshowTransitions,
                slideshowTransitions.Length);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    8f));

            ImGui.Checkbox(
                "Loop slideshow",
                ref slideshowLoop);

            ImGui.Dummy(
                new Vector2(
                    0f,
                    11f));

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
                ImGui.Button(
                    "Start Slideshow",
                    new Vector2(
                        -1f,
                        36f));
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
            var thumbnail =
                thumbnails.Get(
                    url);

            if (thumbnail is not null)
            {
                drawList.AddImageRounded(
                    thumbnail.Handle,
                    origin +
                    new Vector2(
                        5f,
                        5f),
                    origin +
                    size -
                    new Vector2(
                        5f,
                        5f),
                    Vector2.Zero,
                    Vector2.One,
                    uint.MaxValue,
                    7f);
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

            drawList.AddText(
                new Vector2(
                    origin.X +
                    (size.X -
                     iconSize.X) *
                    0.5f,
                    origin.Y +
                    size.Y *
                    0.5f -
                    27f),
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

        drawList.AddText(
            new Vector2(
                origin.X +
                (size.X -
                 messageSize.X) *
                0.5f,
                origin.Y +
                size.Y *
                0.5f +
                7f),
            ImGui.GetColorU32(
                MutedText),
            message);
    }


    private void DrawImageUrlInput(
        string id,
        ref string value)
    {
        ImGui.SetNextItemWidth(
            -52f);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f)
                .Push(
                    ImGuiStyleVar.FramePadding,
                    new Vector2(
                        10f,
                        9f)))
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
                    new Vector2(
                        44f,
                        0f)))
            {
                var clipboard =
                    ImGui.GetClipboardText();

                if (!string.IsNullOrWhiteSpace(
                        clipboard))
                {
                    value =
                        clipboard.Trim();
                }
            }
        }
    }


    private void DrawImageSyncInfo()
    {
        const float height =
            68f;

        var origin =
            ImGui.GetCursorScreenPos();

        var width =
            ImGui.GetContentRegionAvail().X;

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
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.075f)),
            8f);

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.22f)),
            8f);

        var icon =
            FontAwesomeIcon.Lightbulb
                .ToIconString();

        using (
            ImRaii.PushFont(
                UiBuilder.IconFont))
        {
            drawList.AddText(
                origin +
                new Vector2(
                    18f,
                    23f),
                ImGui.GetColorU32(
                    Accent),
                icon);
        }

        drawList.AddText(
            origin +
            new Vector2(
                48f,
                13f),
            ImGui.GetColorU32(
                Vector4.One),
            "How slideshow syncing works");

        drawList.AddText(
            origin +
            new Vector2(
                48f,
                35f),
            ImGui.GetColorU32(
                MutedText),
            "The next image can preload before the timer ends, then every viewer changes together.");

        ImGui.Dummy(
            size);
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
}