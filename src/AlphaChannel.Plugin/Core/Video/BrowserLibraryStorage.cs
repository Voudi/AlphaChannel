using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlphaChannel.Plugin.Video;

internal sealed record BrowserPageEntry(string Title, string Url);

internal sealed record BrowserLibraryLoadResult(
    List<BrowserPageEntry> Favourites,
    List<BrowserPageEntry> History,
    string? RecoveryMessage = null);

internal sealed class BrowserLibraryStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex CompleteObjectPattern = new(@"\{[^{}]*\}", RegexOptions.Compiled);
    private readonly string path = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Browser", "library.json");
    private string BackupPath => path + ".bak";

    internal BrowserLibraryLoadResult Load()
    {
        if (!File.Exists(path))
        {
            if (TryReadValid(BackupPath, out var backup))
            {
                try { WriteAtomic(backup, keepPreviousBackup: false); }
                catch (Exception exception)
                {
                    AepLog.Warning($"[BROWSER] Loaded backup but could not recreate the main library file: {exception.Message}");
                }
                return new(backup.Favourites, backup.History,
                    "Your browser library was restored from its backup because the main library file was missing.");
            }

            return new([], []);
        }

        if (TryReadValid(path, out var data))
            return new(data.Favourites, data.History);

        try
        {
            var brokenText = File.ReadAllText(path);
            var preservedPath = PreserveBrokenFile(path);

            if (TryReadValid(BackupPath, out var backup))
            {
                WriteAtomic(backup, keepPreviousBackup: false);
                return new(backup.Favourites, backup.History,
                    $"Your browser library was damaged and has been restored from backup. The broken file was preserved as {Path.GetFileName(preservedPath)}.");
            }

            var recovered = RecoverEntries(brokenText);
            WriteAtomic(recovered, keepPreviousBackup: false);
            var recoveredCount = recovered.Favourites.Count + recovered.History.Count;
            return new(recovered.Favourites, recovered.History, recoveredCount > 0
                ? $"Your browser library was damaged. {recoveredCount} entr{(recoveredCount == 1 ? "y was" : "ies were")} recovered and the broken file was preserved as {Path.GetFileName(preservedPath)}."
                : $"Your browser library could not be read, so a fresh library was created. The broken file was preserved as {Path.GetFileName(preservedPath)}.");
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[BROWSER] Failed to repair favourites/history: {exception.Message}");
            return new([], [], "Your browser library could not be loaded. The original file has been left untouched.");
        }
    }

    internal void Save(List<BrowserPageEntry> favourites, List<BrowserPageEntry> history)
    {
        try
        {
            WriteAtomic(new Data(Normalize(favourites), Normalize(history)), keepPreviousBackup: true);
        }
        catch (Exception exception) { AepLog.Warning($"[BROWSER] Failed to save favourites/history: {exception.Message}"); }
    }

    private void WriteAtomic(Data data, bool keepPreviousBackup)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(data, JsonOptions));
                writer.Flush();
                stream.Flush(true);
            }

            if (!TryReadValid(temporaryPath, out _))
                throw new InvalidDataException("The temporary browser library did not pass validation.");

            if (keepPreviousBackup && TryReadValid(path, out _))
                File.Copy(path, BackupPath, true);

            File.Move(temporaryPath, path, true);
            if (!File.Exists(BackupPath)) File.Copy(path, BackupPath, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static bool TryReadValid(string candidatePath, out Data data)
    {
        data = new([], []);
        try
        {
            if (!File.Exists(candidatePath)) return false;
            var parsed = JsonSerializer.Deserialize<Data>(File.ReadAllText(candidatePath));
            if (parsed is null || parsed.Favourites is null || parsed.History is null ||
                !parsed.Favourites.All(entry => TryNormalize(entry, out _)) ||
                !parsed.History.All(entry => TryNormalize(entry, out _))) return false;
            data = new(Normalize(parsed.Favourites), Normalize(parsed.History));
            return true;
        }
        catch { return false; }
    }

    private static List<BrowserPageEntry> Normalize(IEnumerable<BrowserPageEntry> entries)
    {
        var result = new List<BrowserPageEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            if (TryNormalize(entry, out var normalized) && seen.Add(normalized.Url)) result.Add(normalized);
        return result;
    }

    private static bool TryNormalize(BrowserPageEntry? entry, out BrowserPageEntry normalized)
    {
        normalized = new("", "");
        if (entry is null || !Uri.TryCreate(entry.Url?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) return false;
        normalized = new(string.IsNullOrWhiteSpace(entry.Title) ? uri.Host : entry.Title.Trim(), uri.AbsoluteUri);
        return true;
    }

    private static Data RecoverEntries(string text)
    {
        var favourites = new List<BrowserPageEntry>();
        var history = new List<BrowserPageEntry>();
        var historyMarker = text.IndexOf("\"History\"", StringComparison.OrdinalIgnoreCase);
        foreach (Match match in CompleteObjectPattern.Matches(text))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<BrowserPageEntry>(match.Value);
                if (!TryNormalize(entry, out var normalized)) continue;
                (historyMarker >= 0 && match.Index > historyMarker ? history : favourites).Add(normalized);
            }
            catch { }
        }
        return new(Normalize(favourites), Normalize(history));
    }

    private static string PreserveBrokenFile(string sourcePath)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var destination = Path.Combine(Path.GetDirectoryName(sourcePath)!,
            $"{Path.GetFileNameWithoutExtension(sourcePath)}.corrupt-{timestamp}{Path.GetExtension(sourcePath)}");
        File.Move(sourcePath, destination);
        return destination;
    }

    private sealed record Data(List<BrowserPageEntry> Favourites, List<BrowserPageEntry> History);
}
