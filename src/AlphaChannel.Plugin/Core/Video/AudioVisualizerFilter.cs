namespace AlphaChannel.Plugin.Video;

internal enum AudioVisualizerMode
{
    ClassicBars,
    FfmpegSpectrum,
    MirrorSpectrum,
    NeonWave,
    Waterfall,
    OrbitalScope
}

internal static class AudioVisualizerFilter
{
    internal static string GetGraph(
        AudioVisualizerMode mode,
        AudioVisualizerTheme theme)
    {
        return mode switch
        {
            AudioVisualizerMode.FfmpegSpectrum =>
                BuildFrequencyBarsGraph(theme),

            AudioVisualizerMode.MirrorSpectrum =>
                BuildMirrorSpectrumGraph(theme),

            AudioVisualizerMode.NeonWave =>
                BuildNeonWaveGraph(theme),

            AudioVisualizerMode.Waterfall =>
                BuildWaterfallGraph(theme),

            AudioVisualizerMode.OrbitalScope =>
BuildOrbitalScopeGraph(theme),

            // Classic Bars are drawn by ScreenPainter from the lightweight
            // astats level measurement. Leave lavfi-complex empty so MPV sends
            // live radio audio through its native output path.
            _ => string.Empty
        };
    }

    private static string BuildFrequencyBarsGraph(
        AudioVisualizerTheme theme)
    {
        return
            "[aid1]asplit[ao][visualizer];" +

            "[visualizer]" +
            "aformat=channel_layouts=mono," +

            "showfreqs=" +
            "size=1120x560:" +
            "rate=30:" +
            "mode=bar:" +
            "ascale=sqrt:" +
            "fscale=log:" +
            "win_size=256:" +
            "win_func=hann:" +
            "overlap=0.75:" +
            "averaging=3:" +
            BuildThemeFilter(theme) +
            "," +

            "pad=1280:720:80:80:color=0x05030D" +

            "[vo]";
    }

    private static string BuildMirrorSpectrumGraph(
        AudioVisualizerTheme theme)
    {
        return
            "[aid1]asplit[ao][visualizer];" +

            "[visualizer]" +
            "aformat=channel_layouts=mono," +

            "showfreqs=" +
            "size=1120x280:" +
            "rate=30:" +
            "mode=bar:" +
            "ascale=sqrt:" +
            "fscale=log:" +
            "win_size=256:" +
            "win_func=hann:" +
            "overlap=0.75:" +
            "averaging=3:" +
            BuildThemeFilter(theme) +

            "[spectrum];" +

            "[spectrum]" +
            "split=2" +
            "[upper][lower];" +

            "[lower]" +
            "vflip" +
            "[lowerFlipped];" +

            "[upper][lowerFlipped]" +
            "vstack=inputs=2" +
            "[mirrored];" +

            "[mirrored]" +
            "pad=1280:720:80:80:color=0x05030D" +

            "[vo]";
    }

    private static string BuildWaterfallGraph(
    AudioVisualizerTheme theme)
    {
        return
            "[aid1]asplit[ao][visualizer];" +

            "[visualizer]" +
            "aformat=channel_layouts=mono," +

            //
            // Create a scrolling grayscale spectrogram. New frequency data
            // enters from the right and previous history moves left.
            //
            "showspectrum=" +
            "size=800x360:" +
            "slide=scroll:" +
            "mode=combined:" +
            "color=intensity:" +
            "scale=sqrt:" +
            "fscale=log:" +
            "saturation=0:" +
            "win_func=hann:" +
            "orientation=vertical:" +
            "overlap=1:" +
           "gain=1:" +
            "fps=20:" +
            "legend=0," +

            //
            // Apply the same solid, gradient or rainbow theme used by the other
            // FFmpeg visualizers.
            //
            BuildVideoThemeFilter(theme) +
            "," +

            "scale=1120:560:flags=bilinear," +

            "pad=1280:720:80:80:color=0x05030D" +

            "[vo]";
    }

    private static string BuildThemeFilter(
    AudioVisualizerTheme theme)
    {
        //
        // showfreqs and showwaves first draw a white visualizer. The common
        // video-theme filter then colours that white intensity image.
        //
        return
            "colors=0xFFFFFF," +
            BuildVideoThemeFilter(theme);
    }

    private static string BuildVideoThemeFilter(
    AudioVisualizerTheme theme)
    {
        return theme switch
        {
            AudioVisualizerTheme.ElectricBlue =>
                BuildSolidColour(
                    59,
                    130,
                    246),

            AudioVisualizerTheme.NeonCyan =>
                BuildSolidColour(
                    34,
                    211,
                    238),

            AudioVisualizerTheme.Emerald =>
                BuildSolidColour(
                    52,
                    211,
                    153),

            AudioVisualizerTheme.HotPink =>
                BuildSolidColour(
                    244,
                    114,
                    182),

            AudioVisualizerTheme.SunsetOrange =>
                BuildSolidColour(
                    251,
                    146,
                    60),

            AudioVisualizerTheme.Crimson =>
                BuildSolidColour(
                    239,
                    68,
                    68),

            AudioVisualizerTheme.White =>
                BuildSolidColour(
                    248,
                    250,
                    252),

            AudioVisualizerTheme.PurplePink =>
                BuildGradient(
                    168, 85, 247,
                    244, 114, 182),

            AudioVisualizerTheme.BlueCyan =>
                BuildGradient(
                    59, 130, 246,
                    34, 211, 238),

            AudioVisualizerTheme.Sunset =>
                BuildGradient(
                    249, 115, 22,
                    250, 204, 21),

            AudioVisualizerTheme.Rainbow =>
                "format=rgb24," +
                "geq=" +
                "r='r(X,Y)*(127.5+127.5*sin(2*PI*X/W))/255':" +
                "g='g(X,Y)*(127.5+127.5*sin(2*PI*X/W+2*PI/3))/255':" +
                "b='b(X,Y)*(127.5+127.5*sin(2*PI*X/W+4*PI/3))/255'",

            _ =>
                BuildSolidColour(
                    168,
                    85,
                    247)
        };
    }

    private static string BuildNeonWaveGraph(
    AudioVisualizerTheme theme)
    {
        return
            "[aid1]asplit[ao][visualizer];" +

            "[visualizer]" +
            "aformat=channel_layouts=mono," +

            //
            // Generate the waveform at a lower resolution and frame rate.
            //
            "showwaves=" +
            "size=800x360:" +
            "rate=20:" +
            "mode=line:" +
            "scale=lin:" +
            BuildThemeFilter(theme) +

            "[waveRgb];" +

            //
            // Force a consistent planar RGB format before splitting. The
            // previous glow attempt allowed FFmpeg to negotiate incompatible
            // formats between the sharp and blurred branches.
            //
            "[waveRgb]" +
            "format=gbrp," +
            "split=2" +
            "[sharp][glowSource];" +

            //
            // A small Gaussian blur creates the glow. It is performed before
            // upscaling, keeping its CPU cost relatively low.
            //
            "[glowSource]" +
            "gblur=sigma=2:steps=1" +
            "[glow];" +

            //
            // Combine the blurred and sharp copies. Explicitly convert the
            // result back to RGB before scaling.
            //
            "[glow][sharp]" +
            "blend=all_mode=screen:all_opacity=0.55," +
            "format=rgb24," +

            "scale=1120:560:flags=bilinear," +

            "pad=1280:720:80:80:color=0x05030D" +

            "[vo]";
    }

    private static string BuildOrbitalScopeGraph(
    AudioVisualizerTheme theme)
    {
        return
            "[aid1]asplit[ao][visualizer];" +

            //
            // Orbital Scope compares the left and right audio channels, so it
            // must retain stereo input instead of converting the audio to mono.
            //
            "[visualizer]" +
            "aformat=channel_layouts=stereo," +

            //
            // Render a low-resolution polar vectorscope. The modest resolution
            // and 20 FPS rate keep its CPU use below the more elaborate effects.
            //
            "avectorscope=" +
            "size=800x360:" +
            "rate=20:" +
            "mode=polar:" +
            "draw=line:" +
            "scale=sqrt:" +
            "zoom=1.15:" +

            //
            // Draw a white source image so the normal theme-processing stage can
            // apply solid colours, gradients and rainbow colouring consistently.
            //
            "rc=255:" +
            "gc=255:" +
            "bc=255:" +
            "ac=255:" +

            //
            // Retain a short, inexpensive fading trail without using blur.
            //
            "rf=8:" +
            "gf=8:" +
            "bf=8:" +
            "af=8," +

            BuildVideoThemeFilter(theme) +
            "," +

            "scale=1120:560:flags=bilinear," +

            "pad=1280:720:80:80:color=0x05030D" +

            "[vo]";
    }

    private static string BuildSolidColour(
    int red,
    int green,
    int blue)
    {
        return
            "format=rgb24," +
            "geq=" +
            $"r='r(X,Y)*{red}/255':" +
            $"g='g(X,Y)*{green}/255':" +
            $"b='b(X,Y)*{blue}/255'";
    }

    private static string BuildGradient(
        int startR,
        int startG,
        int startB,
        int endR,
        int endG,
        int endB)
    {
        return
            "format=rgb24," +
            "geq=" +
            $"r='r(X,Y)*({startR}+({endR}-{startR})*X/W)/255':" +
            $"g='g(X,Y)*({startG}+({endG}-{startG})*X/W)/255':" +
            $"b='b(X,Y)*({startB}+({endB}-{startB})*X/W)/255'";
    }
}
