using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin.Video;

internal sealed class ScreensaverTextureRenderer : IDisposable
{
    private const int CanvasWidth = 512;
    private const int CanvasHeight = 256;
    private Texture2D? texture;
    private ShaderResourceView? srv;
    private string lastStatus = string.Empty;

    internal ShaderResourceView? Srv => srv;

    internal void SetStatus(string status)
    {
        if (status == lastStatus && srv is not null)
            return;

        lastStatus = status;
        var pluginDirectory = Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty;
        var logoPath = Path.Combine(pluginDirectory, "Assets", "alphaicon.png");
        var fontPath = Path.Combine(pluginDirectory, "Fonts", "Inter-SemiBold.ttf");

        using var canvas = new Image<Bgra32>(CanvasWidth, CanvasHeight);
        if (File.Exists(logoPath))
        {
            using var logo = Image.Load<Bgra32>(logoPath);
            logo.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(136, 136),
                Mode = ResizeMode.Max,
            }));
            canvas.Mutate(x => x.DrawImage(logo, new Point((CanvasWidth - logo.Width) / 2, 10), 1f));
        }

        if (File.Exists(fontPath))
        {
            var collection = new FontCollection();
            var font = collection.Add(fontPath).CreateFont(30f, FontStyle.Regular);
            var options = new RichTextOptions(font)
            {
                Origin = new PointF(CanvasWidth / 2f, 174f),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            canvas.Mutate(x => x.DrawText(options, "ALPHA CHANNEL", Color.White));

            var statusFont = collection.Get(font.Family.Name).CreateFont(21f, FontStyle.Regular);
            var statusOptions = new RichTextOptions(statusFont)
            {
                Origin = new PointF(CanvasWidth / 2f, 218f),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            canvas.Mutate(x => x.DrawText(statusOptions, status, Color.FromRgb(190, 178, 220)));
        }

        Upload(canvas);
    }

    private unsafe void Upload(Image<Bgra32> image)
    {
        texture ??= new Texture2D(DxHandler.Device, new Texture2DDescription
        {
            Width = CanvasWidth,
            Height = CanvasHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            SampleDescription = new SampleDescription(1, 0),
        });

        var pixels = new Bgra32[CanvasWidth * CanvasHeight];
        image.CopyPixelDataTo(pixels);
        fixed (Bgra32* pointer = pixels)
        {
            DxHandler.Device?.ImmediateContext.UpdateSubresource(
                texture, 0, null, (nint)pointer, CanvasWidth * 4, 0);
        }

        srv ??= new ShaderResourceView(DxHandler.Device, texture);
    }

    public void Dispose()
    {
        srv?.Dispose();
        texture?.Dispose();
    }
}
