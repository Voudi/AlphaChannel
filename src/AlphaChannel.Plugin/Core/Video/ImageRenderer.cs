using SharpDX.Direct3D11;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin.Video;

internal sealed record ImageRenderResult(
    bool Success,
    string? Error)
{
    internal static ImageRenderResult Completed()
    {
        return new ImageRenderResult(
            true,
            null);
    }

    internal static ImageRenderResult Failed(
        string error)
    {
        return new ImageRenderResult(
            false,
            error);
    }
}

/// <summary>
/// Downloads and renders remote images into VideoEngine's existing BGRA screen
/// texture. Image decoding and resizing happen away from the render thread;
/// only the final GPU upload is queued onto Dalamud's render thread.
/// </summary>
internal sealed class ImageRenderer : IDisposable
{
    private const string RenderKey =
        "AlphaChannel.ImageRenderer.Upload";

    private const int MaximumCachedImages =
    8;

    private readonly object cacheLock =
        new();

    private readonly Dictionary<string, byte[]> renderedImageCache =
        new(
            StringComparer.Ordinal);

    private readonly Queue<string> renderedImageCacheOrder =
        new();

    private readonly SafeImageDownloader downloader =
        new();

    private bool disposed;

    internal async Task<ImageRenderResult> PreloadAsync(
    string imageUrl,
    int targetWidth,
    int targetHeight,
    CancellationToken cancellationToken)
    {
        var prepared =
            await PreparePixelsAsync(
                    imageUrl,
                    targetWidth,
                    targetHeight,
                    cancellationToken)
                .ConfigureAwait(false);

        return prepared.Pixels is not null
            ? ImageRenderResult.Completed()
            : ImageRenderResult.Failed(
                prepared.Error ??
                "The image could not be prepared.");
    }

    internal async Task<ImageRenderResult> LoadAsync(
        string imageUrl,
        Texture2D targetTexture,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken)
    {
        var prepared =
            await PreparePixelsAsync(
                    imageUrl,
                    targetWidth,
                    targetHeight,
                    cancellationToken)
                .ConfigureAwait(false);

        if (prepared.Pixels is null)
        {
            return ImageRenderResult.Failed(
                prepared.Error ??
                "The image could not be prepared.");
        }

        try
        {
            await UploadAsync(
                    targetTexture,
                    prepared.Pixels,
                    targetWidth,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ImageRenderResult.Failed(
                "The image load was cancelled.");
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Image] GPU upload failed: {exception.Message}");

            return ImageRenderResult.Failed(
                "The image could not be uploaded to the TV.");
        }

        return ImageRenderResult.Completed();
    }

    private async Task<(byte[]? Pixels, string? Error)> PreparePixelsAsync(
        string imageUrl,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken)
    {
        if (disposed)
        {
            return (
                null,
                "The image renderer has been disposed.");
        }

        if (targetWidth <= 0 ||
            targetHeight <= 0)
        {
            return (
                null,
                "The target image dimensions are invalid.");
        }

        var cacheKey =
            $"{targetWidth}x{targetHeight}|{imageUrl}";

        if (TryGetCachedImage(
                cacheKey,
                out var cachedPixels))
        {
            return (
                cachedPixels,
                null);
        }

        var download =
            await downloader
                .DownloadAsync(
                    imageUrl,
                    cancellationToken)
                .ConfigureAwait(false);

        if (!download.IsSuccess ||
            download.Data is null)
        {
            return (
                null,
                download.Error ??
                "The image could not be downloaded.");
        }

        byte[] pixels;

        try
        {
            pixels =
                await Task.Run(
                        () => DecodeAndLetterbox(
                            download.Data,
                            targetWidth,
                            targetHeight),
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (
                null,
                "The image load was cancelled.");
        }
        catch (UnknownImageFormatException)
        {
            return (
                null,
                "The downloaded file is not a supported image.");
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Image] Decode failed: {exception.Message}");

            return (
                null,
                "The image could not be decoded.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return (
                null,
                "The image load was cancelled.");
        }

        CacheImage(
            cacheKey,
            pixels);

        return (
            pixels,
            null);
    }

    private static byte[] DecodeAndLetterbox(
        byte[] sourceData,
        int targetWidth,
        int targetHeight)
    {
        using var source =
            Image.Load<Bgra32>(
                sourceData);

        //
        // Animated GIF playback is intentionally deferred. Keep only the first
        // frame so untrusted animations cannot consume continuous CPU time.
        //
        while (source.Frames.Count > 1)
        {
            source.Frames.RemoveFrame(
                source.Frames.Count - 1);
        }

        var scale =
            Math.Min(
                targetWidth /
                    (double)source.Width,
                targetHeight /
                    (double)source.Height);

        var renderedWidth =
            Math.Clamp(
                (int)Math.Round(
                    source.Width *
                    scale),
                1,
                targetWidth);

        var renderedHeight =
            Math.Clamp(
                (int)Math.Round(
                    source.Height *
                    scale),
                1,
                targetHeight);

        source.Mutate(
            context => context.Resize(
                new ResizeOptions
                {
                    Size =
                        new Size(
                            renderedWidth,
                            renderedHeight),

                    Mode =
                        ResizeMode.Stretch,

                    Sampler =
                        KnownResamplers.Bicubic
                }));

        //
        // ScreenTexture uses B8G8R8A8_UNorm. Bgra32 therefore matches the GPU
        // texture layout directly without another channel conversion.
        //
        using var canvas =
            new Image<Bgra32>(
                targetWidth,
                targetHeight,
                new Bgra32(
                    13,
                    3,
                    5,
                    255));

        var position =
            new Point(
                (targetWidth -
                 renderedWidth) / 2,
                (targetHeight -
                 renderedHeight) / 2);

        canvas.Mutate(
            context => context.DrawImage(
                source,
                position,
                1f));

        var pixels =
            new byte[
                targetWidth *
                targetHeight *
                4];

        canvas.CopyPixelDataTo(
            pixels);

        return pixels;
    }

    private bool TryGetCachedImage(
    string cacheKey,
    out byte[] pixels)
    {
        lock (cacheLock)
        {
            return renderedImageCache.TryGetValue(
                cacheKey,
                out pixels!);
        }
    }

    private void CacheImage(
        string cacheKey,
        byte[] pixels)
    {
        lock (cacheLock)
        {
            if (renderedImageCache.ContainsKey(
                    cacheKey))
            {
                return;
            }

            renderedImageCache[cacheKey] =
                pixels;

            renderedImageCacheOrder.Enqueue(
                cacheKey);

            while (renderedImageCache.Count >
                   MaximumCachedImages &&
                   renderedImageCacheOrder.TryDequeue(
                       out var oldestKey))
            {
                renderedImageCache.Remove(
                    oldestKey);
            }
        }
    }

    private static async Task UploadAsync(
        Texture2D targetTexture,
        byte[] pixels,
        int width,
        CancellationToken cancellationToken)
    {
        var completion =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

        using var cancellationRegistration =
            cancellationToken.Register(
                () => completion.TrySetCanceled(
                    cancellationToken));

        //
        // Discard an older image upload that has not run yet. This prevents a
        // slow previous slide from overwriting a newer selection.
        //
        DxHandler.CancelRenderThreadWork(
            RenderKey);

        DxHandler.RunOnRenderThread(
            RenderKey,
            () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(
                        cancellationToken);

                    return;
                }

                try
                {
                    unsafe
                    {
                        fixed (byte* pixelPointer =
                               pixels)
                        {
                            DxHandler.Device?
                                .ImmediateContext
                                .UpdateSubresource(
                                    targetTexture,
                                    0,
                                    null,
                                    (nint)pixelPointer,
                                    width * 4,
                                    0);
                        }
                    }

                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(
                        exception);
                }
            });

        await completion.Task
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed =
            true;

        DxHandler.CancelRenderThreadWork(
            RenderKey);

        lock (cacheLock)
        {
            renderedImageCache.Clear();
            renderedImageCacheOrder.Clear();
        }

        downloader.Dispose();
    }
}