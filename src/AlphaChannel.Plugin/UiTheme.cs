namespace AlphaChannel.Plugin;

internal enum UiTheme
{
    Purple = 0,
    Gold = 1,
    Green = 2,
    Red = 3,
}

// Window surfaces only — accent still comes from UiTheme.
internal enum UiBackground
{
    Theme = 0,   // keep the accent theme's built-in surfaces
    Midnight = 1,
    Void = 2,
    Slate = 3,
    Warm = 4,
    Carbon = 5,
    Custom = 6,  // user image from Settings
}

internal enum UiWindowSizePreset
{
    Design = 0,  // 1220×840 original canvas
    FullHd = 1,  // 1920×1080
    Qhd = 2,     // 2560×1440
    Uhd = 3,     // 3840×2160, clamped to game viewport
    Custom = 4,  // drag-resize
}

internal readonly record struct ThemeColors(
    Vector4 Accent,
    Vector4 AccentHover,
    Vector4 AccentActive,
    Vector4 BlueGlow,
    Vector4 MagentaGlow,
    Vector4 Gold,
    Vector4 GoldHover,
    Vector4 FrameBg,
    Vector4 FrameBgHover,
    Vector4 Danger,
    Vector4 Good,
    Vector4 WindowBg,
    Vector4 SidebarBg,
    Vector4 CardBg,
    Vector4 CardBgHover,
    Vector4 MutedText);

internal static class ThemeCatalog
{
    private static Vector4 Hex(int rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f,
        1f);

    internal static string Label(UiTheme theme) => theme switch
    {
        UiTheme.Purple => "Purple",
        UiTheme.Gold => "Gold",
        UiTheme.Green => "Green",
        UiTheme.Red => "Red",
        _ => theme.ToString(),
    };

    internal static string Label(UiBackground background) => background switch
    {
        UiBackground.Theme => "Theme",
        UiBackground.Midnight => "Midnight",
        UiBackground.Void => "Void",
        UiBackground.Slate => "Slate",
        UiBackground.Warm => "Warm",
        UiBackground.Carbon => "Carbon",
        UiBackground.Custom => "Custom",
        _ => background.ToString(),
    };

    internal static string Label(UiWindowSizePreset preset) => preset switch
    {
        UiWindowSizePreset.Design => "Design",
        UiWindowSizePreset.FullHd => "1080p",
        UiWindowSizePreset.Qhd => "1440p",
        UiWindowSizePreset.Uhd => "4K",
        UiWindowSizePreset.Custom => "Custom",
        _ => preset.ToString(),
    };

    internal static string SizeCaption(UiWindowSizePreset preset) => preset switch
    {
        UiWindowSizePreset.Design => "1220 × 840",
        UiWindowSizePreset.FullHd => "1920 × 1080",
        UiWindowSizePreset.Qhd => "2560 × 1440",
        UiWindowSizePreset.Uhd => "3840 × 2160",
        _ => string.Empty,
    };

    // Swatch shown in Settings for the background picker — match the real window tones.
    internal static Vector4 Swatch(UiBackground background) => background switch
    {
        UiBackground.Midnight => Hex(0x050816),
        UiBackground.Void => Hex(0x000000),
        UiBackground.Slate => Hex(0x1A2436),
        UiBackground.Warm => Hex(0x1C130E),
        UiBackground.Carbon => Hex(0x12151C),
        UiBackground.Custom => Hex(0x4A5568),
        _ => Hex(0x0A0E1A),
    };

    internal static ThemeColors Get(UiTheme theme, UiBackground background = UiBackground.Theme)
    {
        var colors = theme switch
        {
            UiTheme.Gold => Gold,
            UiTheme.Green => Green,
            UiTheme.Red => Red,
            _ => Purple,
        };

        // Custom uses Midnight panel tones under the user image.
        if (background is UiBackground.Theme)
        {
            return colors;
        }

        var surface = background == UiBackground.Custom ? UiBackground.Midnight : background;
        return WithBackground(colors, surface);
    }

    private static ThemeColors WithBackground(ThemeColors baseColors, UiBackground background)
    {
        // Presets are intentionally far apart so picking one reads immediately (not near-black siblings).
        var (window, sidebar, card, cardHover, frame, frameHover, muted) = background switch
        {
            // Pure black — flattest, highest contrast
            UiBackground.Void => (Hex(0x000000), Hex(0x080808), Hex(0x141414), Hex(0x1E1E1E),
                Hex(0x181818), Hex(0x262626), Hex(0xA0A0A0)),

            // Cool steel-blue, clearly lighter than Midnight
            UiBackground.Slate => (Hex(0x1A2436), Hex(0x222D42), Hex(0x2A364E), Hex(0x364560),
                Hex(0x253147), Hex(0x31405A), Hex(0xA8B4C8)),

            // Espresso / brown — warm hue you can spot at a glance
            UiBackground.Warm => (Hex(0x1C130E), Hex(0x261A12), Hex(0x322418), Hex(0x3E2E1E),
                Hex(0x2A1E14), Hex(0x38281A), Hex(0xC4B19A)),

            // Cool graphite with a blue-violet cast
            UiBackground.Carbon => (Hex(0x12151C), Hex(0x181C28), Hex(0x222836), Hex(0x2C3444),
                Hex(0x1C2230), Hex(0x282F40), Hex(0x9AA3B5)),

            // Deep indigo navy (default named preset / Custom underlay)
            _ => (Hex(0x050816), Hex(0x0A1022), Hex(0x121A30), Hex(0x1A2440),
                Hex(0x101828), Hex(0x182038), Hex(0x8E9AB4)), // Midnight
        };

        return baseColors with
        {
            WindowBg = window,
            SidebarBg = sidebar,
            CardBg = card,
            CardBgHover = cardHover,
            FrameBg = frame,
            FrameBgHover = frameHover,
            MutedText = muted,
        };
    }

    private static readonly ThemeColors Purple = new(
        Accent: Hex(0x8B5CF6),
        AccentHover: Hex(0xA78BFA),
        AccentActive: Hex(0x6D28D9),
        BlueGlow: Hex(0x22D3EE),
        MagentaGlow: Hex(0xE879F9),
        Gold: Hex(0xD4AF37),
        GoldHover: Hex(0xE8C547),
        FrameBg: Hex(0x111827),
        FrameBgHover: Hex(0x182235),
        Danger: Hex(0xEF4444),
        Good: Hex(0x22C55E),

        // Media-hub surfaces.
        WindowBg: Hex(0x050812),
        SidebarBg: Hex(0x080D18),
        CardBg: Hex(0x101725),
        CardBgHover: Hex(0x172033),

        MutedText: Hex(0x8F98AC));

    private static readonly ThemeColors Gold = new(
        Accent: Hex(0xD4AF37),
        AccentHover: Hex(0xE4C363),
        AccentActive: Hex(0xB8942A),
        BlueGlow: Hex(0xE8D48A),
        MagentaGlow: Hex(0xD4AF37),
        Gold: Hex(0xF0D060),
        GoldHover: Hex(0xFFE08A),
        FrameBg: Hex(0x16140F),
        FrameBgHover: Hex(0x1F1C16),
        Danger: Hex(0xEF4444),
        Good: Hex(0x22C55E),
        WindowBg: Hex(0x0A0907),
        SidebarBg: Hex(0x100E0A),
        CardBg: Hex(0x16130E),
        CardBgHover: Hex(0x1F1B14),
        MutedText: Hex(0xA89F8A));

    private static readonly ThemeColors Green = new(
        Accent: Hex(0x34D399),
        AccentHover: Hex(0x6EE7B7),
        AccentActive: Hex(0x10B981),
        BlueGlow: Hex(0x5EEAD4),
        MagentaGlow: Hex(0x34D399),
        Gold: Hex(0xF5D78A),
        GoldHover: Hex(0xFFE9A8),
        FrameBg: Hex(0x121816),
        FrameBgHover: Hex(0x1A221E),
        Danger: Hex(0xEF4444),
        Good: Hex(0x4ADE80),
        WindowBg: Hex(0x080B0A),
        SidebarBg: Hex(0x0C1210),
        CardBg: Hex(0x121A17),
        CardBgHover: Hex(0x1A2420),
        MutedText: Hex(0x8FA399));

    private static readonly ThemeColors Red = new(
        Accent: Hex(0xE11D48),
        AccentHover: Hex(0xFB7185),
        AccentActive: Hex(0xBE123C),
        BlueGlow: Hex(0xF87171),
        MagentaGlow: Hex(0xE11D48),
        Gold: Hex(0xFBBF24),
        GoldHover: Hex(0xFCD34D),
        FrameBg: Hex(0x181414),
        FrameBgHover: Hex(0x221A1A),
        Danger: Hex(0xF87171),
        Good: Hex(0x22C55E),
        WindowBg: Hex(0x0A0A0A),
        SidebarBg: Hex(0x100C0C),
        CardBg: Hex(0x161212),
        CardBgHover: Hex(0x1F1818),
        MutedText: Hex(0xA3A3A3));
}
