using System.Globalization;
using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Avatar rendering + the curated icon/color picker - shared by Settings' profile editor and every
// place an avatar chip shows up (Friends list, Alpha Chat, Tweeter, the profile popup). Custom
// uploaded pictures (AvatarImageUrl) take priority when the texture is loaded; icon+color is the
// fallback while loading or when no picture is set.
internal sealed partial class MainWindow
{
    private static readonly string[] AvatarIcons =
    [
        "Cat", "Dog", "Dragon", "Star", "Heart", "Gamepad", "Music", "Camera",
        "Ghost", "Crown", "Fish", "Feather", "Moon", "Sun", "Bolt", "Fire",
        "Leaf", "Snowflake", "Skull", "Gem", "Anchor", "Rocket", "Robot", "Paw",
        "Bug", "Frog", "Hippo", "Otter", "Spider", "Dove", "Crow", "Horse",
        "Dice", "Magic", "Bell", "Trophy",
    ];

    private static readonly string[] AvatarColors =
    [
        "#9966FA", "#FF6B6B", "#4ECDC4", "#FFD93D", "#6BCB77", "#4D96FF",
        "#FF922B", "#F783AC", "#A0A0A0", "#00C2A8",
    ];

    private static Vector4 ParseAvatarColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return new Vector4(0.6f, 0.4f, 1f, 1f);
        }

        var trimmed = hex.TrimStart('#');

        if (trimmed.Length != 6 ||
            !uint.TryParse(
                trimmed,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return new Vector4(0.6f, 0.4f, 1f, 1f);
        }

        return new Vector4(
            ((value >> 16) & 0xFF) / 255f,
            ((value >> 8) & 0xFF) / 255f,
            (value & 0xFF) / 255f,
            1f);
    }

    private string? ResolveAvatarUrl(string? relativeOrAbsolute)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
        {
            return null;
        }

        if (relativeOrAbsolute.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            relativeOrAbsolute.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return relativeOrAbsolute;
        }

        var baseUrl = Plugin.Cfg.RelayServerUrl.TrimEnd('/');
        return relativeOrAbsolute.StartsWith('/')
            ? baseUrl + relativeOrAbsolute
            : $"{baseUrl}/{relativeOrAbsolute}";
    }

    // Draws a filled circle (+ optional custom image) at the current cursor, then reserves layout
    // space with Dummy. Custom pictures load through ThumbnailCache (same as video thumbs).
    private void DrawAvatarChip(string? iconName, string? colorHex, float diameter, string? imageUrl = null)
    {
        var topLeft = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var size = new Vector2(diameter, diameter);
        var center = topLeft + size / 2f;
        var radius = diameter / 2f;

        var absoluteUrl = ResolveAvatarUrl(imageUrl);
        var texture = absoluteUrl is null ? null : thumbnails.Get(absoluteUrl);
        if (texture is not null)
        {
            var (uv0, uv1) = CoverUvs(texture.Width, texture.Height, diameter, diameter);
            drawList.AddImageRounded(texture.Handle, topLeft, topLeft + size, uv0, uv1,
                ImGui.GetColorU32(Vector4.One), radius);
            drawList.AddCircle(center, radius, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.12f)), 0, 1.25f);
            ImGui.Dummy(size);
            return;
        }

        drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(ParseAvatarColor(colorHex)));

        if (iconName is { Length: > 0 } && Enum.TryParse<FontAwesomeIcon>(iconName, out var icon))
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = icon.ToIconString();
                var textSize = ImGui.CalcTextSize(glyph);
                drawList.AddText(center - textSize / 2, ImGui.GetColorU32(Vector4.One), glyph);
            }
        }

        ImGui.Dummy(size);
    }

    // Draws an avatar at an explicit screen position without changing
    // ImGui cursor/layout state.
    //
    // Use this inside custom rows that manage their own positioning,
    // such as Watch Party chat and reaction activity rows.
    private void DrawAvatarAt(
        Vector2 topLeft,
        string? iconName,
        string? colorHex,
        float diameter,
        string? imageUrl = null)
    {
        var drawList =
            ImGui.GetWindowDrawList();

        var size =
            new Vector2(
                diameter,
                diameter);

        var center =
            topLeft +
            size * 0.5f;

        var radius =
            diameter * 0.5f;

        var absoluteUrl =
            ResolveAvatarUrl(
                imageUrl);

        var texture =
            absoluteUrl is null
                ? null
                : thumbnails.Get(
                    absoluteUrl);

        if (texture is not null)
        {
            var (uv0, uv1) =
                CoverUvs(
                    texture.Width,
                    texture.Height,
                    diameter,
                    diameter);

            drawList.AddImageRounded(
                texture.Handle,
                topLeft,
                topLeft + size,
                uv0,
                uv1,
                ImGui.GetColorU32(
                    Vector4.One),
                radius);

            drawList.AddCircle(
                center,
                radius,
                ImGui.GetColorU32(
                    new Vector4(
                        1f,
                        1f,
                        1f,
                        0.12f)),
                0,
                1.25f);

            return;
        }

        drawList.AddCircleFilled(
            center,
            radius,
            ImGui.GetColorU32(
                ParseAvatarColor(
                    colorHex)));

        if (iconName is { Length: > 0 } &&
            Enum.TryParse<FontAwesomeIcon>(
                iconName,
                out var icon))
        {
            using (ImRaii.PushFont(
                UiBuilder.IconFont))
            {
                var glyph =
                    icon.ToIconString();

                var textSize =
                    ImGui.CalcTextSize(
                        glyph);

                drawList.AddText(
                    center -
                    textSize * 0.5f,
                    ImGui.GetColorU32(
                        Vector4.One),
                    glyph);
            }
        }
    }

    // Wraps into rows of 9 rather than relying on ImGui's automatic wrapping (which needs per-item
    // width math anyway) - simpler to just count and force a newline.
    private static bool DrawIconPicker(ref string? selectedIcon)
    {
        var changed = false;

        const float buttonSize = 32f;
        const float gap = 8f;

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var columns =
            Math.Max(
                1,
                (int)((availableWidth + gap) /
                      (buttonSize + gap)));

        for (var index = 0; index < AvatarIcons.Length; index++)
        {
            var icon =
                AvatarIcons[index];

            if (index % columns != 0)
            {
                ImGui.SameLine(0f, gap);
            }

            var isSelected =
                selectedIcon == icon;

            var origin =
                ImGui.GetCursorScreenPos();

            var size =
                new Vector2(
                    buttonSize,
                    buttonSize);

            ImGui.PushID(
                $"avatarIcon_{icon}");

            var clicked =
                ImGui.InvisibleButton(
                    "##icon",
                    size);

            var hovered =
                ImGui.IsItemHovered();

            ImGui.PopID();

            var drawList =
                ImGui.GetWindowDrawList();

            var fill =
                isSelected
                    ? Accent
                    : hovered
                        ? CardBgHover
                        : FrameBg;

            drawList.AddCircleFilled(
                origin +
                size * 0.5f,
                buttonSize * 0.5f,
                ImGui.GetColorU32(fill));

            if (isSelected)
            {
                drawList.AddCircle(
                    origin +
                    size * 0.5f,
                    buttonSize * 0.5f,
                    ImGui.GetColorU32(
                        new Vector4(
                            1f,
                            1f,
                            1f,
                            0.22f)),
                    0,
                    1.25f);
            }

            if (Enum.TryParse<FontAwesomeIcon>(
                icon,
                out var faIcon))
            {
                using (ImRaii.PushFont(
                    UiBuilder.IconFont))
                {
                    var glyph =
                        faIcon.ToIconString();

                    var glyphSize =
                        ImGui.CalcTextSize(
                            glyph);

                    drawList.AddText(
                        origin +
                        (size - glyphSize) * 0.5f,
                        ImGui.GetColorU32(
                            Vector4.One),
                        glyph);
                }
            }

            if (clicked)
            {
                selectedIcon =
                    icon;

                changed =
                    true;
            }
        }

        return changed;
    }

    private static bool DrawColorPicker(
    ref string selectedColor)
    {
        var changed = false;

        const float buttonSize = 32f;
        const float gap = 8f;

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var columns =
            Math.Max(
                1,
                (int)((availableWidth + gap) /
                      (buttonSize + gap)));

        for (var index = 0;
             index < AvatarColors.Length;
             index++)
        {
            var color =
                AvatarColors[index];

            if (index % columns != 0)
            {
                ImGui.SameLine(
                    0f,
                    gap);
            }

            var isSelected =
                selectedColor == color;

            var origin =
                ImGui.GetCursorScreenPos();

            var size =
                new Vector2(
                    buttonSize,
                    buttonSize);

            ImGui.PushID(
                $"avatarColor_{color}");

            var clicked =
                ImGui.InvisibleButton(
                    "##color",
                    size);

            var hovered =
                ImGui.IsItemHovered();

            ImGui.PopID();

            var drawList =
                ImGui.GetWindowDrawList();

            var parsedColor =
                ParseAvatarColor(
                    color);

            var center =
                origin +
                size * 0.5f;

            var radius =
                buttonSize * 0.5f;

            drawList.AddCircleFilled(
                center,
                radius,
                ImGui.GetColorU32(
                    parsedColor));

            if (hovered || isSelected)
            {
                drawList.AddCircle(
                    center,
                    radius,
                    ImGui.GetColorU32(
                        isSelected
                            ? Vector4.One
                            : new Vector4(
                                1f,
                                1f,
                                1f,
                                0.35f)),
                    0,
                    isSelected
                        ? 2f
                        : 1f);
            }

            if (isSelected)
            {
                using (ImRaii.PushFont(
                    UiBuilder.IconFont))
                {
                    var glyph =
                        FontAwesomeIcon.Check
                            .ToIconString();

                    var glyphSize =
                        ImGui.CalcTextSize(
                            glyph);

                    drawList.AddText(
                        origin +
                        (size - glyphSize) * 0.5f,
                        ImGui.GetColorU32(
                            Vector4.One),
                        glyph);
                }
            }

            if (clicked)
            {
                selectedColor =
                    color;

                changed =
                    true;
            }
        }

        return changed;
    }
}
