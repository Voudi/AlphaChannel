using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlphaChannel.Plugin.Video;

internal sealed record GameLibraryRepairResult(List<GameLibraryEntry> Entries, int Recovered, int Missing, int Invalid,
    string? RecoveryMessage);

internal sealed class GameLibraryStorage(string root)
{
    private static readonly string[] SupportedExtensions = [".sfc", ".smc", ".gb", ".gbc", ".dmg", ".nes", ".gba", ".sms", ".sg", ".gg"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private string IndexPath => Path.Combine(root, "library-index.json");
    private string BackupPath => IndexPath + ".bak";

    internal static bool IsSnes(GameLibraryEntry entry) => entry.Extension is ".sfc" or ".smc";
    internal static bool IsNes(GameLibraryEntry entry) => entry.Extension == ".nes";
    internal static bool IsGameBoyAdvance(GameLibraryEntry entry) => entry.Extension == ".gba";
    internal static bool IsMasterSystem(GameLibraryEntry entry) => entry.Extension is ".sms" or ".sg";
    internal static bool IsGameGear(GameLibraryEntry entry) => entry.Extension == ".gg";

    internal string RomPath(GameLibraryEntry entry)
    {
        if (entry.Hash.Length != 64 || !entry.Hash.All(Uri.IsHexDigit) ||
            entry.Extension is not (".sfc" or ".smc" or ".gb" or ".gbc" or ".dmg" or ".nes" or ".gba" or ".sms" or ".sg" or ".gg"))
            throw new InvalidOperationException("Invalid library entry.");
        var folder = Path.Combine(Path.GetFullPath(root), entry.Hash);
        foreach (var path in new[] { Path.GetFullPath(root), folder, Path.Combine(folder, "game" + entry.Extension), Path.Combine(folder, "game.srm") })
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked files and folders cannot be used in the game library.");
        return Path.Combine(folder, "game" + entry.Extension);
    }

    internal GameLibraryEntry Import(string source, bool snes, IReadOnlyList<GameLibraryEntry> entries)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (!(snes ? extension is ".sfc" or ".smc" : extension is ".gb" or ".gbc" or ".dmg"))
            throw new InvalidOperationException("Choose a ROM for the selected system.");
        return ImportAtomic(source, extension, entries);
    }

    internal GameLibraryEntry ImportNes(string source, IReadOnlyList<GameLibraryEntry> entries)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension != ".nes") throw new InvalidOperationException("Choose an .nes ROM.");
        return ImportAtomic(source, extension, entries);
    }

    internal GameLibraryEntry ImportGameBoyAdvance(string source, IReadOnlyList<GameLibraryEntry> entries)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension != ".gba") throw new InvalidOperationException("Choose a .gba ROM.");
        return ImportAtomic(source, extension, entries);
    }

    internal GameLibraryEntry ImportMasterSystem(string source, IReadOnlyList<GameLibraryEntry> entries)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".sms" or ".sg"))
            throw new InvalidOperationException("Choose a .sms Master System or .sg SG-1000 ROM.");
        return ImportAtomic(source, extension, entries);
    }

    internal GameLibraryEntry ImportGameGear(string source, IReadOnlyList<GameLibraryEntry> entries)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension != ".gg") throw new InvalidOperationException("Choose a .gg Game Gear ROM.");
        return ImportAtomic(source, extension, entries);
    }

    internal GameLibraryRepairResult Repair(IReadOnlyCollection<GameLibraryEntry> configuredEntries)
    {
        Directory.CreateDirectory(root);
        var entries = configuredEntries.Where(IsMetadataValid).Select(NormalizeMetadata)
            .GroupBy(entry => entry.Hash, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        var invalid = configuredEntries.Count - entries.Count;
        string? message = null;

        var sidecar = LoadIndexWithRepair(out var sidecarMessage);
        if (sidecarMessage is not null) message = sidecarMessage;
        foreach (var entry in sidecar)
            if (IsMetadataValid(entry) && EntryRomIsValid(entry) &&
                entries.All(existing => !existing.Hash.Equals(entry.Hash, StringComparison.OrdinalIgnoreCase)))
                entries.Add(NormalizeMetadata(entry));

        var missing = entries.Count(entry => !File.Exists(SafeRomPath(entry)));
        var recovered = 0;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var hash = Path.GetFileName(directory);
            if (!IsHash(hash) || IsReparsePoint(directory)) { invalid++; continue; }
            foreach (var rom in Directory.EnumerateFiles(directory, "game.*", SearchOption.TopDirectoryOnly))
            {
                var extension = Path.GetExtension(rom).ToLowerInvariant();
                if (!SupportedExtensions.Contains(extension) || IsReparsePoint(rom)) continue;
                try
                {
                    using var stream = File.OpenRead(rom);
                    var actualHash = Convert.ToHexString(SHA256.HashData(stream));
                    if (!actualHash.Equals(hash, StringComparison.OrdinalIgnoreCase)) { invalid++; continue; }
                    if (entries.Any(entry => entry.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))) continue;
                    entries.Add(new GameLibraryEntry
                    {
                        Hash = hash.ToUpperInvariant(),
                        Extension = extension,
                        Name = $"Recovered {SystemName(extension)} {hash[..8].ToUpperInvariant()}"
                    });
                    recovered++;
                }
                catch { invalid++; }
            }
        }

        SaveIndex(entries);
        if (recovered > 0 || missing > 0 || invalid > 0)
        {
            var details = new List<string>();
            if (recovered > 0) details.Add($"recovered {recovered} game{(recovered == 1 ? "" : "s")}");
            if (missing > 0) details.Add($"found {missing} missing ROM entr{(missing == 1 ? "y" : "ies")}");
            if (invalid > 0) details.Add($"ignored {invalid} invalid item{(invalid == 1 ? "" : "s")}");
            var scanMessage = "Your game library was checked: " + string.Join(", ", details) + ". No save files were deleted.";
            message = message is null ? scanMessage : message + " " + scanMessage;
        }
        return new(entries, recovered, missing, invalid, message);
    }

    internal void SaveIndex(IReadOnlyCollection<GameLibraryEntry> entries) =>
        WriteIndexAtomic(entries.Where(IsMetadataValid).ToList(), keepPreviousBackup: true);

    private GameLibraryEntry ImportAtomic(string source, string extension, IReadOnlyList<GameLibraryEntry> entries)
    {
        using var input = File.OpenRead(source);
        if (input.Length == 0 || input.Length > 64L * 1024 * 1024)
            throw new InvalidOperationException("Choose a non-empty ROM smaller than 64 MB.");
        var expectedLength = input.Length;
        var hash = Convert.ToHexString(SHA256.HashData(input));
        var existing = entries.FirstOrDefault(entry => entry.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
        var entry = existing ?? new GameLibraryEntry
        {
            Hash = hash,
            Name = Path.GetFileNameWithoutExtension(source),
            Extension = extension
        };
        var destination = RomPath(entry);
        if (File.Exists(destination) && FileHashMatches(destination, hash)) return entry;

        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"game.import-{Guid.NewGuid():N}.tmp");
        try
        {
            input.Position = 0;
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            if (new FileInfo(temporaryPath).Length != expectedLength || !FileHashMatches(temporaryPath, hash))
                throw new InvalidDataException("The copied ROM did not pass verification.");
            File.Move(temporaryPath, destination, true);
        }
        finally { try { File.Delete(temporaryPath); } catch { } }

        CopySaveAtomic(Path.ChangeExtension(source, ".srm"), Path.ChangeExtension(destination, ".srm"));
        return entry;
    }

    private List<GameLibraryEntry> LoadIndexWithRepair(out string? message)
    {
        message = null;
        if (TryReadIndex(IndexPath, out var entries)) return entries;
        if (!File.Exists(IndexPath))
        {
            if (!TryReadIndex(BackupPath, out entries)) return [];
            WriteIndexAtomic(entries, keepPreviousBackup: false);
            message = "Your game library index was restored from backup.";
            return entries;
        }

        try
        {
            var preserved = PreserveBrokenFile(IndexPath);
            if (TryReadIndex(BackupPath, out entries))
            {
                WriteIndexAtomic(entries, keepPreviousBackup: false);
                message = $"Your game library index was damaged and restored from backup. The broken index was preserved as {Path.GetFileName(preserved)}.";
                return entries;
            }
            message = $"Your game library index was damaged. The broken index was preserved as {Path.GetFileName(preserved)} and installed ROMs will be scanned for recovery.";
        }
        catch (Exception exception) { AepLog.Warning($"[GAME LIBRARY] Failed to preserve damaged index: {exception.Message}"); }
        return [];
    }

    private void WriteIndexAtomic(List<GameLibraryEntry> entries, bool keepPreviousBackup)
    {
        Directory.CreateDirectory(root);
        var temporaryPath = IndexPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(entries, JsonOptions));
                writer.Flush();
                stream.Flush(true);
            }
            if (!TryReadIndex(temporaryPath, out _)) throw new InvalidDataException("The temporary game library index did not pass validation.");
            if (keepPreviousBackup && TryReadIndex(IndexPath, out _)) File.Copy(IndexPath, BackupPath, true);
            File.Move(temporaryPath, IndexPath, true);
            if (!File.Exists(BackupPath)) File.Copy(IndexPath, BackupPath, true);
        }
        finally { try { File.Delete(temporaryPath); } catch { } }
    }

    private static bool TryReadIndex(string path, out List<GameLibraryEntry> entries)
    {
        entries = [];
        try
        {
            if (!File.Exists(path)) return false;
            var parsed = JsonSerializer.Deserialize<List<GameLibraryEntry>>(File.ReadAllText(path));
            if (parsed is null || parsed.Any(entry => !IsMetadataValid(entry))) return false;
            entries = parsed;
            return true;
        }
        catch { return false; }
    }

    private string? SafeRomPath(GameLibraryEntry entry)
    {
        try { return RomPath(entry); } catch { return null; }
    }

    private bool EntryRomIsValid(GameLibraryEntry entry)
    {
        var romPath = SafeRomPath(entry);
        return romPath is not null && File.Exists(romPath) && FileHashMatches(romPath, entry.Hash);
    }

    private static bool IsMetadataValid(GameLibraryEntry? entry) => entry is not null && IsHash(entry.Hash) &&
        SupportedExtensions.Contains(entry.Extension?.ToLowerInvariant()) && !string.IsNullOrWhiteSpace(entry.Name);
    private static GameLibraryEntry NormalizeMetadata(GameLibraryEntry entry) => new()
    {
        Hash = entry.Hash.ToUpperInvariant(),
        Extension = entry.Extension.ToLowerInvariant(),
        Name = entry.Name.Trim(),
        Icon = entry.Icon
    };
    private static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool FileHashMatches(string path, string expected)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void CopySaveAtomic(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination)) return;
        var temporaryPath = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            if (!File.Exists(destination)) File.Move(temporaryPath, destination);
        }
        finally { try { File.Delete(temporaryPath); } catch { } }
    }

    private static string PreserveBrokenFile(string sourcePath)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var destination = Path.Combine(Path.GetDirectoryName(sourcePath)!,
            $"{Path.GetFileNameWithoutExtension(sourcePath)}.corrupt-{timestamp}{Path.GetExtension(sourcePath)}");
        File.Move(sourcePath, destination);
        return destination;
    }

    private static string SystemName(string extension) => extension switch
    {
        ".sfc" or ".smc" => "SNES",
        ".nes" => "NES",
        ".gba" => "GBA",
        ".sms" => "Master System",
        ".sg" => "SG-1000",
        ".gg" => "Game Gear",
        ".gbc" => "Game Boy Color",
        _ => "Game Boy"
    };

    internal void ClearSave(GameLibraryEntry entry) => File.Delete(Path.ChangeExtension(RomPath(entry), ".srm"));

    internal void Remove(GameLibraryEntry entry)
    {
        // Delete only known managed files; never recurse or follow an arbitrary saved path.
        var rom = RomPath(entry);
        ClearSave(entry);
        File.Delete(rom);
    }
}
