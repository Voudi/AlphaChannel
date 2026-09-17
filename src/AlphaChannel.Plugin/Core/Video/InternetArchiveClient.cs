using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Video;

internal sealed record InternetArchiveEpisode(
    string Title,
    string VideoUrl,
    TimeSpan? Duration);

internal sealed record InternetArchiveItem(
    string Identifier,
    string Title,
    string Creator,
    string Description,
    int? Year,
    long Downloads,
    DateTime? PublicDate,
    string VideoUrl,
    string? ThumbnailUrl,
    TimeSpan? Duration,
    IReadOnlyList<InternetArchiveEpisode> Episodes);

internal sealed class InternetArchiveClient : IDisposable
{
    private const string AdvancedSearchUrl = "https://archive.org/advancedsearch.php";
    private const string MetadataUrl = "https://archive.org/metadata/";
    private const string DownloadUrl = "https://archive.org/download/";

    private readonly HttpClient http = PluginHttpClients.CreateMetadataClient();
    private static readonly TimeSpan NoVideoCacheLifetime = TimeSpan.FromDays(7);
    private readonly SemaphoreSlim metadataSlots = new(10, 10);
    private readonly ConcurrentDictionary<string, InternetArchiveItem?> resolvedItems =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> noVideoItems =
        new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    internal async Task<IReadOnlyList<InternetArchiveItem>> SearchAsync(
        string archiveQuery,
        string sort,
        int resultCount,
        CancellationToken cancellationToken,
        Action<InternetArchiveItem>? itemResolved = null,
        int? priorityCandidateCount = null,
        int page = 1)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var requestedRows = Math.Clamp(resultCount * 2, 12, 50);
        const string videoFormats =
            "(format:\"512Kb MPEG4\" OR format:\"h.264\" OR format:\"MPEG4\" OR format:\"WebM\")";
        var query = string.IsNullOrWhiteSpace(archiveQuery)
            ? $"mediatype:movies AND {videoFormats}"
            : $"mediatype:movies AND ({archiveQuery}) AND {videoFormats}";
        var fields = new[]
        {
            "identifier", "title", "description", "creator", "date", "publicdate", "downloads",
        };

        var parameters = new List<string>
        {
            "q=" + Uri.EscapeDataString(query),
            "rows=" + requestedRows.ToString(CultureInfo.InvariantCulture),
            "page=" + Math.Max(1, page).ToString(CultureInfo.InvariantCulture),
            "output=json",
            "sort[]=" + Uri.EscapeDataString(sort),
        };
        parameters.AddRange(fields.Select(field => "fl[]=" + Uri.EscapeDataString(field)));

        using var response = await http.GetAsync(
            AdvancedSearchUrl + "?" + string.Join("&", parameters),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("response", out var searchResponse) ||
            !searchResponse.TryGetProperty("docs", out var docs) ||
            docs.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<InternetArchiveItem>();
        }

        var candidates = docs.EnumerateArray()
            .Select(ParseSearchDocument)
            .Where(candidate => candidate is not null)
            .Cast<SearchCandidate>()
            .ToArray();

        var publishedCount = 0;
        async Task<InternetArchiveItem?> ResolveAndPublishAsync(SearchCandidate candidate)
        {
            var item = await ResolveItemAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (item is not null && Interlocked.Increment(ref publishedCount) <= resultCount)
            {
                itemResolved?.Invoke(item);
            }
            return item;
        }

        var priorityCount = Math.Clamp(priorityCandidateCount ?? candidates.Length, 0, candidates.Length);
        var priorityResolved = await Task.WhenAll(candidates
            .Take(priorityCount)
            .Select(ResolveAndPublishAsync)).ConfigureAwait(false);
        var remainingResolved = await Task.WhenAll(candidates
            .Skip(priorityCount)
            .Select(ResolveAndPublishAsync)).ConfigureAwait(false);
        var resolved = priorityResolved.Concat(remainingResolved);

        return resolved
            .Where(item => item is not null)
            .Cast<InternetArchiveItem>()
            .Take(resultCount)
            .ToArray();
    }

    internal void SeedResolvedItems(IEnumerable<InternetArchiveItem> items)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Identifier) && item.Episodes.Count > 0)
                resolvedItems.TryAdd(item.Identifier, item);
        }
    }

    internal void SeedNoVideoItems(IEnumerable<KeyValuePair<string, DateTime>> items)
    {
        var cutoff = DateTime.UtcNow - NoVideoCacheLifetime;
        foreach (var (identifier, recordedUtc) in items)
        {
            if (!string.IsNullOrWhiteSpace(identifier) && recordedUtc >= cutoff)
                noVideoItems[identifier] = recordedUtc.Kind == DateTimeKind.Utc
                    ? recordedUtc
                    : recordedUtc.ToUniversalTime();
        }
    }

    internal Dictionary<string, DateTime> SnapshotNoVideoItems()
    {
        var cutoff = DateTime.UtcNow - NoVideoCacheLifetime;
        foreach (var (identifier, recordedUtc) in noVideoItems)
        {
            if (recordedUtc < cutoff) noVideoItems.TryRemove(identifier, out _);
        }
        return noVideoItems.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    internal static string BuildTextSearchQuery(string searchText)
    {
        var safe = searchText
            .Trim()
            .Replace("\\", " ", StringComparison.Ordinal)
            .Replace("\"", " ", StringComparison.Ordinal);
        return $"(title:(\"{safe}\") OR description:(\"{safe}\") OR creator:(\"{safe}\") OR subject:(\"{safe}\"))";
    }

    private async Task<InternetArchiveItem?> ResolveItemAsync(
        SearchCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (resolvedItems.TryGetValue(candidate.Identifier, out var cached))
        {
            return cached is null ? null : RefreshSearchMetadata(cached, candidate);
        }
        if (noVideoItems.TryGetValue(candidate.Identifier, out var noVideoRecordedUtc))
        {
            if (DateTime.UtcNow - noVideoRecordedUtc < NoVideoCacheLifetime) return null;
            noVideoItems.TryRemove(candidate.Identifier, out _);
        }

        await metadataSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (resolvedItems.TryGetValue(candidate.Identifier, out cached))
            {
                return cached is null ? null : RefreshSearchMetadata(cached, candidate);
            }

            using var response = await http.GetAsync(
                MetadataUrl + Uri.EscapeDataString(candidate.Identifier),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array)
            {
                noVideoItems[candidate.Identifier] = DateTime.UtcNow;
                return null;
            }

            string? thumbnailName = null;
            var videoFiles = new List<FileCandidate>();
            var fileIndex = 0;

            foreach (var file in files.EnumerateArray())
            {
                var name = GetFlexibleString(file, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var format = GetFlexibleString(file, "format") ?? string.Empty;
                if (IsThumbnail(name, format))
                {
                    thumbnailName ??= name;
                }

                var score = ScoreVideoFile(name, format);
                if (score < 0)
                {
                    continue;
                }

                var size = GetLong(file, "size");
                var duration = ParseDuration(GetFlexibleString(file, "length"));
                videoFiles.Add(new FileCandidate(
                    name,
                    score,
                    size,
                    duration,
                    GetFlexibleString(file, "source") ?? string.Empty,
                    GetFlexibleString(file, "original"),
                    GetFlexibleString(file, "title"),
                    fileIndex));
                fileIndex++;
            }

            if (videoFiles.Count == 0)
            {
                noVideoItems[candidate.Identifier] = DateTime.UtcNow;
                return null;
            }

            var root = DownloadUrl + Uri.EscapeDataString(candidate.Identifier) + "/";
            var episodeGroups = videoFiles
                .GroupBy(GetEpisodeGroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var best = group.Aggregate((current, next) => IsBetterVideo(next, current) ? next : current);
                    var explicitTitle = group
                        .Select(file => file.Title)
                        .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));
                    var canonicalName = group
                        .Select(file => file.OriginalName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key;
                    return new EpisodeCandidate(
                        string.IsNullOrWhiteSpace(explicitTitle)
                            ? CleanEpisodeTitle(canonicalName, candidate.Identifier)
                            : NormalizeEpisodeTitle(explicitTitle),
                        root + EscapePath(best.Name),
                        best.Duration,
                        group.Min(file => file.Index));
                })
                .OrderBy(episode => episode.Order)
                .ToArray();

            var episodes = episodeGroups
                .Select((episode, index) => new InternetArchiveEpisode(
                    episodeGroups.Length == 1
                        ? candidate.Title
                        : string.IsNullOrWhiteSpace(episode.Title)
                            ? $"Episode {index + 1}"
                            : episode.Title,
                    episode.VideoUrl,
                    episode.Duration))
                .ToArray();
            var firstEpisode = episodes[0];
            var item = new InternetArchiveItem(
                candidate.Identifier,
                candidate.Title,
                candidate.Creator,
                candidate.Description,
                candidate.Year,
                candidate.Downloads,
                candidate.PublicDate,
                firstEpisode.VideoUrl,
                thumbnailName is null ? null : root + EscapePath(thumbnailName),
                firstEpisode.Duration,
                episodes);
            resolvedItems[candidate.Identifier] = item;
            return item;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[InternetArchive] Could not resolve {candidate.Identifier}: {exception.Message}");
            return null;
        }
        finally
        {
            metadataSlots.Release();
        }
    }

    private static InternetArchiveItem RefreshSearchMetadata(
        InternetArchiveItem cached,
        SearchCandidate candidate) =>
        cached with
        {
            Title = candidate.Title,
            Creator = candidate.Creator,
            Description = candidate.Description,
            Year = candidate.Year,
            Downloads = candidate.Downloads,
            PublicDate = candidate.PublicDate,
        };

    private static SearchCandidate? ParseSearchDocument(JsonElement document)
    {
        var identifier = GetFlexibleString(document, "identifier");
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var title = GetFlexibleString(document, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = identifier;
        }

        return new SearchCandidate(
            identifier,
            title,
            GetFlexibleString(document, "creator") ?? "Internet Archive",
            GetFlexibleString(document, "description") ?? string.Empty,
            ParseYear(GetFlexibleString(document, "date")),
            GetLong(document, "downloads"),
            ParseDate(GetFlexibleString(document, "publicdate")));
    }

    private static string? GetFlexibleString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Array => value.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            _ => null,
        };
    }

    private static long GetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return long.TryParse(GetFlexibleString(element, propertyName), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out number) ? number : 0;
    }

    private static int? ParseYear(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed.Year
            : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
                ? year
                : null;

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration))
        {
            return duration;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private static bool IsThumbnail(string name, string format) =>
        name.Equals("__ia_thumb.jpg", StringComparison.OrdinalIgnoreCase) ||
        format.Contains("Thumbnail", StringComparison.OrdinalIgnoreCase) ||
        format.Contains("Item Tile", StringComparison.OrdinalIgnoreCase);

    private static int ScoreVideoFile(string name, string format)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not (".mp4" or ".m4v" or ".webm" or ".ogv" or ".mkv"))
        {
            return -1;
        }

        if (name.Contains("sample", StringComparison.OrdinalIgnoreCase) ||
            format.Contains("sample", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        var score = extension switch
        {
            ".mp4" => 300,
            ".m4v" => 250,
            ".webm" => 180,
            ".ogv" => 140,
            _ => 100,
        };
        if (format.Contains("512Kb MPEG4", StringComparison.OrdinalIgnoreCase)) score += 220;
        else if (format.Contains("h.264", StringComparison.OrdinalIgnoreCase)) score += 180;
        else if (format.Contains("MPEG4", StringComparison.OrdinalIgnoreCase)) score += 140;
        if (name.Contains("512kb", StringComparison.OrdinalIgnoreCase)) score += 100;
        return score;
    }

    private static bool IsBetterVideo(FileCandidate candidate, FileCandidate current)
    {
        if (candidate.Score != current.Score)
        {
            return candidate.Score > current.Score;
        }

        if (candidate.Size <= 0) return false;
        if (current.Size <= 0) return true;
        return candidate.Size < current.Size;
    }

    private static string GetEpisodeGroupKey(FileCandidate file)
    {
        if (!string.IsNullOrWhiteSpace(file.OriginalName))
        {
            return file.OriginalName;
        }

        if (file.Source.Equals("original", StringComparison.OrdinalIgnoreCase))
        {
            return file.Name;
        }

        var stem = Path.GetFileNameWithoutExtension(file.Name);
        return Regex.Replace(
            stem,
            @"(?:[\s_.-]+(?:512k|512kb|h\.?264|mpeg-?4|webm|ogv|derivative|download))+$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string CleanEpisodeTitle(string fileName, string itemIdentifier)
    {
        var decoded = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(fileName));
        decoded = Regex.Replace(
            decoded,
            @"(?:[\s_.-]+(?:512k|512kb|h\.?264|mpeg-?4|webm|ogv|derivative|download))+$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        decoded = NormalizeEpisodeTitle(decoded.Replace('_', ' ').Replace('.', ' '));
        var normalizedIdentifier = NormalizeEpisodeTitle(
            itemIdentifier.Replace('_', ' ').Replace('.', ' ').Replace('-', ' '));
        if (decoded.StartsWith(normalizedIdentifier + " ", StringComparison.OrdinalIgnoreCase))
        {
            decoded = decoded[(normalizedIdentifier.Length + 1)..].TrimStart('-', ' ');
        }
        return decoded;
    }

    private static string NormalizeEpisodeTitle(string value) =>
        Regex.Replace(value, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static string EscapePath(string path) =>
        string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        http.Dispose();
    }

    private sealed record SearchCandidate(
        string Identifier,
        string Title,
        string Creator,
        string Description,
        int? Year,
        long Downloads,
        DateTime? PublicDate);

    private sealed record FileCandidate(
        string Name,
        int Score,
        long Size,
        TimeSpan? Duration,
        string Source,
        string? OriginalName,
        string? Title,
        int Index);

    private sealed record EpisodeCandidate(
        string Title,
        string VideoUrl,
        TimeSpan? Duration,
        int Order);
}
