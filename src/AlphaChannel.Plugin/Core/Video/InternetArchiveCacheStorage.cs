using System.Text;
using System.Text.Json;

namespace AlphaChannel.Plugin.Video;

internal sealed record InternetArchiveCacheSnapshot(
    DateTime LastRefreshUtc,
    Dictionary<string, List<InternetArchiveItem>> Categories,
    List<InternetArchiveItem> SearchItems,
    Dictionary<string, DateTime> NoVideoItems,
    Dictionary<string, int> CategoryLastPages);

internal sealed class InternetArchiveCacheStorage
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object sync = new();
    private readonly string path = Path.Combine(
        Plugin.PluginInterface.ConfigDirectory.FullName,
        "InternetArchive",
        "results-cache.json");
    private string BackupPath => path + ".bak";

    internal InternetArchiveCacheSnapshot Load()
    {
        lock (sync)
        {
            if (TryRead(path, out var snapshot)) return snapshot;
            if (TryRead(BackupPath, out snapshot))
            {
                try { WriteAtomic(snapshot, keepPreviousBackup: false); }
                catch (Exception exception)
                {
                    AepLog.Warning($"[InternetArchive] Could not restore cache backup: {exception.Message}");
                }
                return snapshot;
            }

            return EmptySnapshot();
        }
    }

    internal void Save(InternetArchiveCacheSnapshot snapshot)
    {
        lock (sync)
        {
            try
            {
                WriteAtomic(Normalize(snapshot), keepPreviousBackup: true);
            }
            catch (Exception exception)
            {
                AepLog.Warning($"[InternetArchive] Could not save results cache: {exception.Message}");
            }
        }
    }

    private void WriteAtomic(InternetArchiveCacheSnapshot snapshot, bool keepPreviousBackup)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var data = new CacheData(
            CurrentVersion,
            snapshot.LastRefreshUtc,
            snapshot.Categories,
            snapshot.SearchItems,
            snapshot.NoVideoItems,
            snapshot.CategoryLastPages);
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(data, JsonOptions));
                writer.Flush();
                stream.Flush(true);
            }

            if (!TryRead(temporaryPath, out _))
                throw new InvalidDataException("The temporary Internet Archive cache did not pass validation.");
            if (keepPreviousBackup && TryRead(path, out _)) File.Copy(path, BackupPath, true);
            File.Move(temporaryPath, path, true);
            if (!File.Exists(BackupPath)) File.Copy(path, BackupPath, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static bool TryRead(string candidatePath, out InternetArchiveCacheSnapshot snapshot)
    {
        snapshot = EmptySnapshot();
        try
        {
            if (!File.Exists(candidatePath)) return false;
            var data = JsonSerializer.Deserialize<CacheData>(File.ReadAllText(candidatePath));
            if (data is null || data.Version != CurrentVersion || data.Categories is null || data.SearchItems is null)
                return false;
            snapshot = Normalize(new(
                data.LastRefreshUtc,
                data.Categories,
                data.SearchItems,
                data.NoVideoItems ?? new(StringComparer.OrdinalIgnoreCase),
                data.CategoryLastPages ?? new(StringComparer.Ordinal)));
            return true;
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[InternetArchive] Could not read {Path.GetFileName(candidatePath)}: {exception.Message}");
            return false;
        }
    }

    private static InternetArchiveCacheSnapshot Normalize(InternetArchiveCacheSnapshot snapshot)
    {
        var categories = new Dictionary<string, List<InternetArchiveItem>>(StringComparer.Ordinal);
        foreach (var (key, items) in snapshot.Categories)
        {
            if (string.IsNullOrWhiteSpace(key) || items is null) continue;
            categories[key] = NormalizeItems(items, 200);
        }

        var noVideoCutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
        var noVideoItems = snapshot.NoVideoItems
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value >= noVideoCutoff)
            .OrderByDescending(pair => pair.Value)
            .Take(2000)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Kind == DateTimeKind.Utc ? pair.Value : pair.Value.ToUniversalTime(),
                StringComparer.OrdinalIgnoreCase);
        var categoryLastPages = snapshot.CategoryLastPages
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0)
            .ToDictionary(
                pair => pair.Key,
                pair => Math.Clamp(pair.Value, 1, 10000),
                StringComparer.Ordinal);

        return new(
            snapshot.LastRefreshUtc == DateTime.MinValue
                ? DateTime.MinValue
                : snapshot.LastRefreshUtc.Kind == DateTimeKind.Utc
                ? snapshot.LastRefreshUtc
                : snapshot.LastRefreshUtc.ToUniversalTime(),
            categories,
            NormalizeItems(snapshot.SearchItems, 300),
            noVideoItems,
            categoryLastPages);
    }

    private static List<InternetArchiveItem> NormalizeItems(
        IEnumerable<InternetArchiveItem> items,
        int maximum)
    {
        var normalized = new List<InternetArchiveItem>();
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (normalized.Count >= maximum) break;
            if (!IsValid(item) || !identifiers.Add(item.Identifier)) continue;
            normalized.Add(item);
        }
        return normalized;
    }

    private static bool IsValid(InternetArchiveItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Identifier) ||
            string.IsNullOrWhiteSpace(item.Title) || item.Episodes is null || item.Episodes.Count == 0)
            return false;
        if (!IsArchiveUrl(item.VideoUrl)) return false;
        if (item.ThumbnailUrl is not null && !IsArchiveUrl(item.ThumbnailUrl)) return false;
        return item.Episodes.All(episode =>
            episode is not null && !string.IsNullOrWhiteSpace(episode.Title) && IsArchiveUrl(episode.VideoUrl));
    }

    private static bool IsArchiveUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("archive.org", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase));

    private static InternetArchiveCacheSnapshot EmptySnapshot() =>
        new(
            DateTime.MinValue,
            new(StringComparer.Ordinal),
            [],
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.Ordinal));

    private sealed record CacheData(
        int Version,
        DateTime LastRefreshUtc,
        Dictionary<string, List<InternetArchiveItem>> Categories,
        List<InternetArchiveItem> SearchItems,
        Dictionary<string, DateTime>? NoVideoItems,
        Dictionary<string, int>? CategoryLastPages);
}
