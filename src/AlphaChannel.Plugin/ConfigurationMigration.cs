using Dalamud.Plugin;
using Newtonsoft.Json;

namespace AlphaChannel.Plugin;

/// <summary>
/// Loads configuration before any plugin subsystem can consume it and owns
/// every persisted schema transition from the version-2 baseline onward.
/// </summary>
internal static class ConfigurationMigration
{
    internal const int BaselineVersion = 2;
    internal const int CurrentVersion = 3;

    // Add each future migration here. A migration changes only one schema
    // version and must set Configuration.Version to its target version.
    private static readonly IReadOnlyDictionary<int, Action<Configuration>>
        MigrationSteps =
            new Dictionary<int, Action<Configuration>>
            {
                [2] = MigrateVersion2To3,
            };

    private static void MigrateVersion2To3(
        Configuration configuration)
    {
        // Earlier experimental builds could retain paths to external browser
        // profiles or manually exported cookie files. Version 3 starts from
        // explicit consent to Alpha Channel's embedded-browser session.
        configuration.YouTubeEmbeddedBrowserSessionEnabled =
            false;
        configuration.Version =
            3;
    }

    internal static Configuration Load(
        IDalamudPluginInterface pluginInterface,
        out bool baselineReset)
    {
        baselineReset = false;

        var loaded =
            pluginInterface.GetPluginConfig() as Configuration;

        if (loaded is null)
        {
            var created = CreateCurrentConfiguration();
            created.Initialize(pluginInterface);
            created.Save();
            return created;
        }

        if (loaded.Version < BaselineVersion)
        {
            var backupPath =
                ArchiveConfiguration(
                    pluginInterface,
                    loaded,
                    $"pre-v{BaselineVersion}");

            var reset = CreateCurrentConfiguration();
            reset.Initialize(pluginInterface);
            reset.Save();

            baselineReset = true;

            AepLog.Info(
                $"[Configuration] Reset pre-version-{BaselineVersion} configuration and archived it at '{backupPath}'.");

            return reset;
        }

        if (loaded.Version > CurrentVersion)
        {
            AepLog.Error(
                $"[Configuration] Configuration version {loaded.Version} is newer than supported version {CurrentVersion}. " +
                "Using temporary defaults without overwriting the newer configuration.");

            var temporary = CreateCurrentConfiguration();
            temporary.Initialize(
                pluginInterface,
                enablePersistence: false);
            return temporary;
        }

        string? migrationBackupPath = null;
        if (loaded.Version < CurrentVersion)
        {
            migrationBackupPath =
                ArchiveConfiguration(
                    pluginInterface,
                    loaded,
                    $"v{loaded.Version}-before-v{CurrentVersion}");
        }

        var migrated = MigrateToCurrent(loaded);

        loaded.Initialize(pluginInterface);

        if (migrated)
        {
            loaded.Save();

            AepLog.Info(
                $"[Configuration] Migrated configuration to version {CurrentVersion}. " +
                $"The previous file was archived at '{migrationBackupPath}'.");
        }

        return loaded;
    }

    private static Configuration CreateCurrentConfiguration() =>
        new()
        {
            Version = CurrentVersion
        };

    private static bool MigrateToCurrent(
        Configuration configuration)
    {
        var changed = false;

        while (configuration.Version < CurrentVersion)
        {
            var previousVersion =
                configuration.Version;

            if (!MigrationSteps.TryGetValue(
                    previousVersion,
                    out var migrate))
            {
                throw new InvalidOperationException(
                    $"No configuration migration exists from version {previousVersion}.");
            }

            migrate(configuration);

            if (configuration.Version <= previousVersion)
            {
                throw new InvalidOperationException(
                    $"Configuration migration from version {previousVersion} did not advance the version.");
            }

            changed = true;
        }

        return changed;
    }

    private static string ArchiveConfiguration(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        string label)
    {
        Directory.CreateDirectory(
            pluginInterface.ConfigDirectory.FullName);

        var source =
            pluginInterface.ConfigFile.FullName;

        var originalName =
            Path.GetFileNameWithoutExtension(
                pluginInterface.ConfigFile.Name);

        var backupPath =
            Path.Combine(
                pluginInterface.ConfigDirectory.FullName,
                $"{originalName}.{label}.{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");

        if (File.Exists(source))
        {
            File.Copy(
                source,
                backupPath,
                overwrite: false);
        }
        else
        {
            // This is only a fallback for unusual hosts where a configuration
            // object was supplied without its backing file being present.
            File.WriteAllText(
                backupPath,
                JsonConvert.SerializeObject(
                    configuration,
                    Formatting.Indented));
        }

        return backupPath;
    }
}
