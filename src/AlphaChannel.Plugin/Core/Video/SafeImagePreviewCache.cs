using System.Collections.Concurrent;
using Dalamud.Interface.Textures.TextureWraps;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace AlphaChannel.Plugin.Video;

internal enum ImagePreviewState
{
    Empty,
    Loading,
    Ready,
    Failed
}

internal readonly record struct ImagePreviewResult(
    ImagePreviewState State,
    IDalamudTextureWrap? Texture,
    string? Error);

/// <summary>
/// Secure preview cache for user-supplied image URLs.
///
/// Unlike ThumbnailCache, every image passes through SafeImageDownloader
/// before being decoded or uploaded. Loading and failure are represented
/// separately so the UI does not show an endless loading state.
/// </summary>
internal sealed class SafeImagePreviewCache : IDisposable
{
    private sealed record CacheEntry(
        ImagePreviewState State,
        IDalamudTextureWrap? Texture,
        string? Error);

    private readonly ConcurrentDictionary<string, CacheEntry> cache =
        new(
            StringComparer.Ordinal);

    private readonly SafeImageDownloader downloader =
        new();

    private readonly CancellationTokenSource disposalCancellation =
        new();

    private bool disposed;

    internal ImagePreviewResult Get(
        string? url)
    {
        if (string.IsNullOrWhiteSpace(
                url))
        {
            return new ImagePreviewResult(
                ImagePreviewState.Empty,
                null,
                null);
        }

        var cleanUrl =
            url.Trim();

        if (!ImageHostPolicy.TryValidateUrl(
                cleanUrl,
                out _,
                out var validationError))
        {
            return new ImagePreviewResult(
                ImagePreviewState.Failed,
                null,
                validationError ??
                "This image URL is not supported.");
        }

        if (cache.TryGetValue(
                cleanUrl,
                out var existing))
        {
            return ToResult(
                existing);
        }

        var loading =
            new CacheEntry(
                ImagePreviewState.Loading,
                null,
                null);

        if (cache.TryAdd(
                cleanUrl,
                loading))
        {
            _ = LoadAsync(
                cleanUrl);
        }

        if (cache.TryGetValue(
                cleanUrl,
                out existing))
        {
            return ToResult(
                existing);
        }

        return new ImagePreviewResult(
            ImagePreviewState.Loading,
            null,
            null);
    }

    internal void Invalidate(
        string? url)
    {
        if (string.IsNullOrWhiteSpace(
                url))
        {
            return;
        }

        if (cache.TryRemove(
                url.Trim(),
                out var removed))
        {
            removed.Texture?.Dispose();
        }
    }

    private async Task LoadAsync(
        string url)
    {
        try
        {
            var download =
                await downloader
                    .DownloadAsync(
                        url,
                        disposalCancellation.Token)
                    .ConfigureAwait(false);

            if (!download.IsSuccess ||
                download.Data is null)
            {
                SetFailure(
                    url,
                    download.Error ??
                    "The image could not be downloaded.");

                return;
            }

            using var image =
                Image.Load(
                    download.Data);

            //
            // Animated GIF playback is not supported in previews yet.
            // Retain only its first frame before producing the texture.
            //
            while (image.Frames.Count >
                   1)
            {
                image.Frames.RemoveFrame(
                    image.Frames.Count -
                    1);
            }

            using var pngStream =
                new MemoryStream();

            await image
                .SaveAsync(
                    pngStream,
                    new PngEncoder(),
                    disposalCancellation.Token)
                .ConfigureAwait(false);

            var texture =
                await Plugin.TextureProvider
                    .CreateFromImageAsync(
                        pngStream.ToArray())
                    .ConfigureAwait(false);

            if (disposed ||
                disposalCancellation.IsCancellationRequested)
            {
                texture.Dispose();
                return;
            }

            var ready =
                new CacheEntry(
                    ImagePreviewState.Ready,
                    texture,
                    null);

            cache.AddOrUpdate(
                url,
                ready,
                (_, previous) =>
                {
                    previous.Texture?.Dispose();
                    return ready;
                });
        }
        catch (OperationCanceledException)
        {
            // Normal plugin shutdown.
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[ImagePreview] Failed to load {url}: " +
                $"{exception.Message}");

            SetFailure(
                url,
                "The image preview could not be created.");
        }
    }

    private void SetFailure(
        string url,
        string error)
    {
        if (disposed)
        {
            return;
        }

        var failed =
            new CacheEntry(
                ImagePreviewState.Failed,
                null,
                error);

        cache.AddOrUpdate(
            url,
            failed,
            (_, previous) =>
            {
                previous.Texture?.Dispose();
                return failed;
            });
    }

    private static ImagePreviewResult ToResult(
        CacheEntry entry)
    {
        return new ImagePreviewResult(
            entry.State,
            entry.Texture,
            entry.Error);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed =
            true;

        disposalCancellation.Cancel();

        foreach (var entry in cache.Values)
        {
            entry.Texture?.Dispose();
        }

        cache.Clear();

        disposalCancellation.Dispose();
        downloader.Dispose();
    }
}