using System.Collections.Concurrent;
using Dalamud.Interface.Textures.TextureWraps;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Video;

// Downloads a thumbnail URL once and keeps the decoded GPU texture around for as long as the
// plugin runs - queue entries and search results share this cache by URL, so scrolling past the
// same video twice (e.g. it's both in search results and already queued) doesn't refetch it.
// A null cache entry means "download in flight, nothing to draw yet" - Get callers just skip
// drawing an image for that frame, no placeholder texture needed for v1.
// ConcurrentDictionary, not a plain Dictionary - LoadAsync's continuation after the awaits below
// resumes on an arbitrary thread pool thread, not necessarily the main thread Get() is called
// from every frame, so this is a real cross-thread read/write, not just a style preference.
internal sealed class ThumbnailCache : IDisposable
{
    private readonly ConcurrentDictionary<string, IDalamudTextureWrap?> cache = new();
    private readonly ConcurrentDictionary<string, Task> pendingLoads = new();
    private readonly HttpClient http =
        PluginHttpClients.CreateMetadataClient();
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    public IDalamudTextureWrap? Get(string? url)
    {
        if (disposed || string.IsNullOrEmpty(url))
        {
            return null;
        }

        if (cache.TryGetValue(url, out var wrap))
        {
            return wrap;
        }

        cache[url] = null;
        var load =
            LoadAsync(url, lifetime.Token);

        pendingLoads[url] = load;
        _ = load.ContinueWith(
            completedTask => pendingLoads.TryRemove(url, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return null;
    }

    public void Invalidate(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        if (cache.TryRemove(url, out var wrap))
        {
            wrap?.Dispose();
        }
    }

    private async Task LoadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var sourceBytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Thumbnail URLs (YouTube, Twitch) are JPEG. Dalamud's CreateFromImageAsync only
            // documents/reliably supports .tex and .png - handing it a JPEG directly fails with
            // "the file is not a TexFile" every time. Re-encode to PNG ourselves first via
            // ImageSharp, already a dependency for the title-banner texture rendering.
            using var image = Image.Load(sourceBytes);
            using var pngStream = new MemoryStream();
            await image.SaveAsync(pngStream, new PngEncoder(), cancellationToken).ConfigureAwait(false);
            pngStream.Position = 0;

            var wrap = await Plugin.TextureProvider.CreateFromImageAsync(pngStream.ToArray()).ConfigureAwait(false);

            if (disposed || cancellationToken.IsCancellationRequested)
            {
                wrap.Dispose();
                return;
            }

            cache[url] = wrap;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal during plugin shutdown.
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[Thumbnail] Failed to load {url}: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetime.Cancel();
        http.Dispose();

        try
        {
            Task.WaitAll(
                pendingLoads.Values.ToArray(),
                TimeSpan.FromSeconds(3));
        }
        catch (Exception exception)
        {
            AepLog.Debug(
                $"[Thumbnail] Pending load cleanup warning: {exception.Message}");
        }

        foreach (var wrap in cache.Values)
        {
            wrap?.Dispose();
        }

        cache.Clear();
        pendingLoads.Clear();
        lifetime.Dispose();
    }
}
