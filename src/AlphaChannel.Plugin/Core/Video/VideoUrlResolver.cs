using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;
using YoutubeExplode.Channels;

namespace AlphaChannel.Plugin.Video;

internal sealed record VideoMetadata(string Title, string Source, TimeSpan? Duration, string? ThumbnailUrl);

internal sealed record VideoSearchEntry(
    string Title,
    string Url,
    string ChannelName,
    TimeSpan? Duration,
    string? ThumbnailUrl,
    long? ViewCount = null,
    DateTimeOffset? UploadDate = null,
    string? ChannelId = null);

internal sealed record ResolvedStream(string VideoUrl, string? AudioUrl, string QualityLabel);

// Stage 4, deliberately NOT a port of AlphaChannel's yt-dlp path. AlphaChannel downloads a
// yt-dlp binary at runtime and hands mpv's own ytdl_hook script a URL to resolve internally
// (see docs/video-pipeline.md §5 in the AlphaChannel repo) - AlphaChannel's C# code never
// touches yt-dlp's output directly. Aetherphone already depends on YoutubeExplode (managed,
// no external process) for the Music app's own YouTube resolution (Core/Songs/SongSearchService,
// SongPlayer). Reusing it here removes an entire runtime-downloaded-binary dependency and its
// failure modes (network required on first use, download hangs, binary goes missing).
//
// YouTube only serves muxed (single-file, audio+video together) streams up to 720p - anything
// higher only exists as separate video-only and audio-only streams. This resolves adaptive
// streams first (best video-only <= the requested cap, paired with the best audio-only track)
// and only falls back to a muxed stream if no adaptive pair is available, so quality above 720p
// is actually reachable rather than the dropdown just listing numbers that silently clamp.
internal sealed class VideoUrlResolver
{
    // YouTube's own practical ceiling for muxed streams. Below this, always prefer muxed even if
    // a particular video's own muxed ceiling happens to be lower than what adaptive could offer
    // at the same cap - the adaptive (external audio-file) path is new and its track-selection
    // behavior under Wine isn't fully verified yet, so it's scoped to only the quality tier that
    // has no muxed option at all, rather than opportunistically replacing muxed playback that
    // was already known to work.
    private const int MuxedCeiling = 720;

    private readonly YoutubeClient youtube = new();

    // Global limit for YouTube metadata/discovery work.
    // All resolver operations should eventually pass through this gate.
    private readonly SemaphoreSlim youtubeRequestGate = new(3, 3);

    // Keep the most recently encountered YouTube video metadata.
    // Old entries are pushed out once the cache reaches 100 videos.
    private const int YouTubeMetadataCacheLimit = 1000;

    private readonly object youtubeMetadataCacheLock = new();

    private bool TryGetCachedVideo(
    string url,
    out VideoSearchEntry? entry)
    {
        lock (youtubeMetadataCacheLock)
        {
            if (!youtubeMetadataCache.TryGetValue(
                    url,
                    out var cached))
            {
                entry = null;
                return false;
            }

            // Refresh its LRU position.
            youtubeMetadataCacheOrder.Remove(url);
            youtubeMetadataCacheOrder.AddLast(url);

            entry = cached;
            return true;
        }
    }

    private void CacheVideo(
        VideoSearchEntry entry)
    {
        lock (youtubeMetadataCacheLock)
        {
            var key = entry.Url;

            // Refresh an existing entry instead of duplicating it.
            if (youtubeMetadataCache.ContainsKey(key))
            {
                youtubeMetadataCache[key] = entry;

                youtubeMetadataCacheOrder.Remove(key);
                youtubeMetadataCacheOrder.AddLast(key);

                return;
            }

            youtubeMetadataCache[key] = entry;
            youtubeMetadataCacheOrder.AddLast(key);

            while (youtubeMetadataCache.Count >
                   YouTubeMetadataCacheLimit)
            {
                var oldest =
                    youtubeMetadataCacheOrder.First;

                if (oldest is null)
                {
                    break;
                }

                youtubeMetadataCacheOrder.RemoveFirst();
                youtubeMetadataCache.Remove(oldest.Value);
            }
        }
    }

    private async Task<VideoSearchEntry?> GetOrCreateMetadataRequestAsync(
    string url,
    Func<Task<VideoSearchEntry?>> factory)
    {
        Task<VideoSearchEntry?> request;

        lock (youtubeMetadataCacheLock)
        {
            if (!youtubeMetadataRequests.TryGetValue(
                    url,
                    out request!))
            {
                request = factory();

                youtubeMetadataRequests[url] = request;
            }
        }

        try
        {
            var result =
                await request.ConfigureAwait(false);

            if (result is not null)
            {
                CacheVideo(result);
            }

            return result;
        }
        finally
        {
            lock (youtubeMetadataCacheLock)
            {
                youtubeMetadataRequests.Remove(url);
            }
        }
    }

    private readonly Dictionary<string, VideoSearchEntry> youtubeMetadataCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly LinkedList<string> youtubeMetadataCacheOrder = new();

    private readonly Dictionary<string, Task<VideoSearchEntry?>> youtubeMetadataRequests =
    new(StringComparer.OrdinalIgnoreCase);

    public static bool IsYouTubeUrl(string url) => VideoId.TryParse(url) is not null;

    public async Task<(ResolvedStream? Stream, string? Error)> ResolveAsync(string url, int maxHeight,
        CancellationToken token)
    {
        try
        {
            var manifest = await youtube.Videos.Streams.GetManifestAsync(url, token).ConfigureAwait(false);

            var video = manifest.GetVideoOnlyStreams()
                .Where(stream => stream.VideoQuality.MaxHeight <= maxHeight)
                .OrderByDescending(stream => stream.VideoQuality.MaxHeight)
                .ThenByDescending(stream => stream.Bitrate)
                .FirstOrDefault();
            var audio = manifest.GetAudioOnlyStreams().OrderByDescending(stream => stream.Bitrate).FirstOrDefault();

            var muxed = manifest.GetMuxedStreams().Where(stream => stream.VideoQuality.MaxHeight <= maxHeight)
                .OrderByDescending(stream => stream.VideoQuality.MaxHeight).FirstOrDefault();

            // Only reach for adaptive when the requested quality is above what muxed can ever
            // offer - not just above what this particular video's muxed ceiling happens to be.
            if (false && maxHeight > MuxedCeiling && video is not null && audio is not null)
            {
                var label = video.VideoQuality.Label;
                AepLog.Debug($"[Video] Resolved {url} -> video={video.Url} audio={audio.Url} ({label}, adaptive)");
                return (new ResolvedStream(video.Url, audio.Url, label), null);
            }

            muxed ??= manifest.GetMuxedStreams().OrderBy(stream => stream.VideoQuality.MaxHeight).FirstOrDefault();
            if (muxed is not null)
            {
                AepLog.Debug($"[Video] Resolved {url} -> {muxed.Url} ({muxed.VideoQuality.Label}, muxed)");
                return (new ResolvedStream(muxed.Url, null, muxed.VideoQuality.Label), null);
            }

            return (null, "No playable stream found for this video.");
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
        catch (Exception exception)
        {
            return (null, $"Failed to resolve YouTube URL: {exception.Message}");
        }
    }

    // ---------------------------------------------------------
    // Channel uploads
    //
    // Used by AlphaChannel's plugin-managed subscriptions.
    // Takes a stable YouTube channel ID and returns recent
    // uploads enriched with full video metadata.
    // ---------------------------------------------------------

    public async Task<List<VideoSearchEntry>> GetChannelUploadsAsync(
    string channelId,
    int maxResults,
    CancellationToken token)
    {
        var results =
            new List<VideoSearchEntry>();

        var gateAcquired = false;

        try
        {
            var parsedChannelId =
                ChannelId.TryParse(
                    channelId);

            if (parsedChannelId is null)
            {
                return results;
            }

            // Channel enumeration is a YouTube request, so it shares
            // the global two-request limit with the other resolver work.
            await youtubeRequestGate
                .WaitAsync(token)
                .ConfigureAwait(false);

            gateAcquired = true;

            await foreach (
                var upload in youtube.Channels
                    .GetUploadsAsync(
                        parsedChannelId.Value,
                        token)
                    .ConfigureAwait(false))
            {
                if (results.Count >= maxResults)
                {
                    break;
                }

                try
                {
                    var thumbnail =
                        upload.Thumbnails
                            .OrderByDescending(
                                t => t.Resolution.Area)
                            .FirstOrDefault();

                    results.Add(
                        new VideoSearchEntry(
                            upload.Title,
                            upload.Url,
                            upload.Author.ChannelTitle,
                            upload.Duration,
                            thumbnail?.Url,
                            ChannelId: upload.Author.ChannelId.Value));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    AepLog.Warning(
                        $"[Subscriptions] Failed to process upload " +
                        $"{upload.Url}: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation.
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Subscriptions] Failed to load channel " +
                $"{channelId}: {exception.Message}");
        }
        finally
        {
            if (gateAcquired)
            {
                youtubeRequestGate.Release();
            }
        }

        return results;
    }

    public async Task<string?> GetChannelNameAsync(
    string channelId,
    CancellationToken token)
    {
        var gateAcquired = false;

        try
        {
            var parsedChannelId =
                ChannelId.TryParse(
                    channelId);

            if (parsedChannelId is null)
            {
                return null;
            }

            await youtubeRequestGate
                .WaitAsync(token)
                .ConfigureAwait(false);

            gateAcquired = true;

            var channel =
                await youtube.Channels
                    .GetAsync(
                        parsedChannelId.Value,
                        token)
                    .ConfigureAwait(false);

            return channel.Title;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Subscriptions] Failed to resolve channel name " +
                $"{channelId}: {exception.Message}");

            return null;
        }
        finally
        {
            if (gateAcquired)
            {
                youtubeRequestGate.Release();
            }
        }
    }




    public async Task<List<VideoSearchEntry>> SearchLatestAggregatedAsync(
    IReadOnlyList<string> queries,
    int maxResults,
    CancellationToken token)
    {
        // ---------------------------------------------------------
        // Run all search queries concurrently.
        // ---------------------------------------------------------

        var searchTasks =
            queries.Select(
                async query =>
                {
                    try
                    {
                        return await SearchAsync(
                                query,
                                6,
                                token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return [];
                    }
                    catch (Exception exception)
                    {
                        AepLog.Warning(
                            $"[Video] FFXIV aggregate search failed for " +
                            $"'{query}': {exception.Message}");

                        return [];
                    }
                })
            .ToArray();

        var searchResults =
            await Task.WhenAll(searchTasks)
                .ConfigureAwait(false);

        // ---------------------------------------------------------
        // Deduplicate results shared between queries.
        // ---------------------------------------------------------

        var candidates =
            new Dictionary<string, VideoSearchEntry>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var resultSet in searchResults)
        {
            foreach (var result in resultSet)
            {
                candidates.TryAdd(
                    result.Url,
                    result);
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        // ---------------------------------------------------------
        // Fetch detailed metadata through the shared cache-aware
        // enrichment pipeline.
        // ---------------------------------------------------------

        var metadataTasks =
            candidates.Values
                .Select(
                    result =>
                        EnrichSearchResultAsync(
                            result,
                            token))
                .ToArray();

        var enriched =
            await Task.WhenAll(metadataTasks)
                .ConfigureAwait(false);

        // ---------------------------------------------------------
        // We now have upload dates, so newest first.
        // ---------------------------------------------------------

        return enriched
            .OrderByDescending(
                item =>
                    item.UploadDate ??
                    DateTimeOffset.MinValue)
            .Take(maxResults)
            .ToList();
    }


    public async Task<VideoMetadata?> ResolveMetadataAsync(
    string url,
    CancellationToken token)
    {
        var gateAcquired = false;

        try
        {
            await youtubeRequestGate
                .WaitAsync(token)
                .ConfigureAwait(false);

            gateAcquired = true;

            var video =
                await youtube.Videos
                    .GetAsync(
                        url,
                        token)
                    .ConfigureAwait(false);

            var thumbnail =
                video.Thumbnails
                    .OrderByDescending(
                        t => t.Resolution.Area)
                    .FirstOrDefault();

            return new VideoMetadata(
                video.Title,
                video.Author.ChannelTitle,
                video.Duration,
                thumbnail?.Url);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Video] Failed to fetch metadata for {url}: " +
                $"{exception.Message}");

            return null;
        }
        finally
        {
            if (gateAcquired)
            {
                youtubeRequestGate.Release();
            }
        }
    }

    public async Task<VideoSearchEntry?> GetVideoEntryAsync(
    string url,
    CancellationToken token)
    {
        if (TryGetCachedVideo(
                url,
                out var cached) &&
            cached is not null)
        {
            return cached;
        }

        return await GetOrCreateMetadataRequestAsync(
                url,
                async () =>
                {
                    var gateAcquired = false;

                    try
                    {
                        await youtubeRequestGate
                            .WaitAsync(token)
                            .ConfigureAwait(false);

                        gateAcquired = true;

                        // Check again after waiting.
                        if (TryGetCachedVideo(
                                url,
                                out var existing) &&
                            existing is not null)
                        {
                            return existing;
                        }

                        var video =
                            await youtube.Videos
                                .GetAsync(
                                    url,
                                    token)
                                .ConfigureAwait(false);

                        var thumbnail =
                            video.Thumbnails
                                .OrderByDescending(
                                    t => t.Resolution.Area)
                                .FirstOrDefault();

                        return new VideoSearchEntry(
                            video.Title,
                            url,
                            video.Author.ChannelTitle,
                            video.Duration,
                            thumbnail?.Url,
                            video.Engagement.ViewCount,
                            video.UploadDate,
                            video.Author.ChannelId.Value);
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                    catch (Exception exception)
                    {
                        AepLog.Warning(
                            $"[Video] Failed to fetch featured video metadata " +
                            $"for {url}: {exception.Message}");

                        return null;
                    }
                    finally
                    {
                        if (gateAcquired)
                        {
                            youtubeRequestGate.Release();
                        }
                    }
                })
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------
    // YouTube search with full metadata.
    //
    // Home Trending and Browse Videos need ViewCount,
    // UploadDate and ChannelId, which the lightweight search
    // result alone does not fully provide.
    // ---------------------------------------------------------

    public async Task<List<VideoSearchEntry>> SearchWithMetadataAsync(
     string query,
     int maxResults,
     CancellationToken token)
    {
        var basicResults =
            await SearchAsync(
                    query,
                    maxResults,
                    token)
                .ConfigureAwait(false);

        if (basicResults.Count == 0)
        {
            return [];
        }

        var metadataTasks =
            basicResults
                .Select(
                    result =>
                        EnrichSearchResultAsync(
                            result,
                            token))
                .ToArray();

        var enriched =
            await Task.WhenAll(metadataTasks)
                .ConfigureAwait(false);

        return enriched.ToList();
    }

    public async Task<VideoSearchEntry> EnrichSearchResultAsync(
    VideoSearchEntry result,
    CancellationToken token)
    {
        if (TryGetCachedVideo(
                result.Url,
                out var cached) &&
            cached is not null)
        {
            return cached;
        }

        var enriched =
            await GetOrCreateMetadataRequestAsync(
                result.Url,
                async () =>
                {
                    var gateAcquired = false;

                    try
                    {
                        await youtubeRequestGate
                            .WaitAsync(token)
                            .ConfigureAwait(false);

                        gateAcquired = true;

                        // Check cache again after entering the request.
                        if (TryGetCachedVideo(
                                result.Url,
                                out var existing) &&
                            existing is not null)
                        {
                            return existing;
                        }

                        var video =
                            await youtube.Videos
                                .GetAsync(
                                    result.Url,
                                    token)
                                .ConfigureAwait(false);

                        var thumbnail =
                            video.Thumbnails
                                .OrderByDescending(
                                    t => t.Resolution.Area)
                                .FirstOrDefault();

                        return result with
                        {
                            Title =
                                video.Title,

                            ChannelName =
                                video.Author.ChannelTitle,

                            Duration =
                                video.Duration,

                            ThumbnailUrl =
                                thumbnail?.Url ??
                                result.ThumbnailUrl,

                            ViewCount =
                                video.Engagement.ViewCount,

                            UploadDate =
                                video.UploadDate,

                            ChannelId =
                                video.Author.ChannelId.Value
                        };
                    }
                    catch (OperationCanceledException)
                    {
                        return result;
                    }
                    catch (Exception exception)
                    {
                        AepLog.Warning(
                            $"[Video] Failed to enrich search result " +
                            $"{result.Url}: {exception.Message}");

                        return result;
                    }
                    finally
                    {
                        if (gateAcquired)
                        {
                            youtubeRequestGate.Release();
                        }
                    }
                })
            .ConfigureAwait(false);

        return enriched ?? result;
    }

    // YoutubeExplode's own search (scrapes YouTube's search results) - no API key needed, same
    // dependency already used for playback resolution and metadata enrichment above.
    public async Task<List<VideoSearchEntry>> SearchAsync(
    string query,
    int maxResults,
    CancellationToken token)
    {
        var results =
            new List<VideoSearchEntry>();

        var gateAcquired = false;

        try
        {
            await youtubeRequestGate
                .WaitAsync(token)
                .ConfigureAwait(false);

            gateAcquired = true;

            await foreach (
                var video in youtube.Search
                    .GetVideosAsync(
                        query,
                        token)
                    .ConfigureAwait(false))
            {
                if (results.Count >= maxResults)
                {
                    break;
                }

                var thumbnail =
                    video.Thumbnails
                        .OrderByDescending(
                            t => t.Resolution.Area)
                        .FirstOrDefault();

                results.Add(
                    new VideoSearchEntry(
                        video.Title,
                        video.Url,
                        video.Author.ChannelTitle,
                        video.Duration,
                        thumbnail?.Url,
                        ChannelId: video.Author.ChannelId.Value));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation.
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Video] YouTube search failed for '{query}': " +
                $"{exception.Message}");
        }
        finally
        {
            if (gateAcquired)
            {
                youtubeRequestGate.Release();
            }
        }

        return results;
    }
}
