namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Owns the saved queue slots and coordinates switching the active
/// slot without interrupting the video that is currently playing.
/// </summary>
internal sealed class QueueManager
{
    internal const int StandardSlotCount = 3;

    private readonly Configuration configuration;
    private readonly AetherStreamQueue queue;

    internal QueueManager(
        Configuration configuration,
        AetherStreamQueue queue)
    {
        this.configuration = configuration;
        this.queue = queue;

        NormalizeConfiguration(configuration);
    }

    internal IReadOnlyList<SavedQueueProfile?> Slots =>
        configuration.SavedQueueProfiles;

    internal int ActiveSlotIndex =>
        configuration.ActiveQueueSlot;

    internal SavedQueueProfile? ActiveProfile
    {
        get
        {
            var index =
                configuration.ActiveQueueSlot;

            return IsValidSlot(index)
                ? configuration.SavedQueueProfiles[index]
                : null;
        }
    }

    /// <summary>
    /// Repairs older or incomplete configuration files before anything
    /// tries to read ActiveQueueSlot.
    ///
    /// The old single VideoQueue is migrated into slot zero when no
    /// saved profiles exist.
    /// </summary>
    internal static void NormalizeConfiguration(
        Configuration configuration)
    {
        var originalSlots =
            configuration.SavedQueueProfiles ??
            new List<SavedQueueProfile?>();

        //
        // Remember which profile was active before repairing its index.
        // In affected configurations, the active profile may currently
        // be at index 3, 6, 9, or even further into the expanded list.
        //
        SavedQueueProfile? previouslyActiveProfile =
            configuration.ActiveQueueSlot >= 0 &&
            configuration.ActiveQueueSlot <
            originalSlots.Count
                ? originalSlots[
                    configuration.ActiveQueueSlot]
                : null;

        //
        // Repair configurations affected by Newtonsoft appending saved
        // profiles after the old three-null property initializer.
        //
        // Preserve the first three real profiles in their original order.
        //
        var populatedProfiles =
            originalSlots
                .Where(profile => profile is not null)
                .Take(StandardSlotCount)
                .Cast<SavedQueueProfile?>()
                .ToList();

        while (populatedProfiles.Count <
               StandardSlotCount)
        {
            populatedProfiles.Add(null);
        }

        //
        // If no named profiles exist, migrate the legacy single queue.
        //
        if (populatedProfiles.All(
                profile => profile is null) &&
            configuration.VideoQueue.Count > 0)
        {
            var migratedProfile =
                new SavedQueueProfile
                {
                    Name = "My Queue",
                    Icon = "📺",
                    Entries =
                        new List<VideoQueueRecord>(
                            configuration.VideoQueue)
                };

            populatedProfiles[0] =
                migratedProfile;

            previouslyActiveProfile =
                migratedProfile;
        }

        configuration.SavedQueueProfiles =
            populatedProfiles;

        //
        // Restore the previously active profile at its repaired index.
        //
        var repairedActiveIndex =
            previouslyActiveProfile is not null
                ? populatedProfiles.IndexOf(
                    previouslyActiveProfile)
                : -1;

        if (repairedActiveIndex < 0)
        {
            repairedActiveIndex =
                populatedProfiles.FindIndex(
                    profile => profile is not null);
        }

        configuration.ActiveQueueSlot =
            repairedActiveIndex >= 0
                ? repairedActiveIndex
                : 0;

        //
        // Save immediately. This shrinks affected JSON configurations
        // back to exactly three slots.
        //
        configuration.Save();
    }

    internal bool Create(
        int slotIndex,
        string name,
        string icon)
    {
        if (!IsValidSlot(slotIndex) ||
            configuration.SavedQueueProfiles[slotIndex] is not null)
        {
            return false;
        }

        var trimmedName =
            name.Trim();

        if (trimmedName.Length == 0)
        {
            return false;
        }

        var hadExistingProfile =
            configuration.SavedQueueProfiles.Any(
                profile => profile is not null);

        var profile =
            new SavedQueueProfile
            {
                Name = trimmedName,
                Icon =
                    string.IsNullOrWhiteSpace(icon)
                        ? "📺"
                        : icon,
                Entries =
                    new List<VideoQueueRecord>()
            };

        configuration.SavedQueueProfiles[slotIndex] =
            profile;

        //
        // The first queue created becomes active automatically.
        //
        if (!hadExistingProfile)
        {
            configuration.ActiveQueueSlot =
                slotIndex;

            queue.ReplaceUpcomingEntries(
                profile.Entries);
        }

        configuration.Save();
        return true;
    }

    internal bool Activate(
        int slotIndex)
    {
        if (!IsValidSlot(slotIndex) ||
            configuration.SavedQueueProfiles[slotIndex] is not { } profile)
        {
            return false;
        }

        if (configuration.ActiveQueueSlot ==
            slotIndex)
        {
            return true;
        }

        //
        // Capture the current active slot before switching.
        // Queue mutations normally keep this synchronized, but doing it
        // here also protects changes made immediately before activation.
        //
        SaveActiveEntries();

        configuration.ActiveQueueSlot =
            slotIndex;

        //
        // Replace only upcoming entries. A video already playing remains
        // Current and continues uninterrupted. When it ends—or when Next
        // is pressed—the newly selected queue is used.
        //
        queue.ReplaceUpcomingEntries(
            profile.Entries);

        configuration.Save();
        return true;
    }

    internal bool UpdateDetails(
        int slotIndex,
        string name,
        string icon)
    {
        if (!IsValidSlot(slotIndex) ||
            configuration.SavedQueueProfiles[slotIndex] is not { } profile)
        {
            return false;
        }

        var trimmedName =
            name.Trim();

        if (trimmedName.Length == 0)
        {
            return false;
        }

        profile.Name =
            trimmedName;

        profile.Icon =
            string.IsNullOrWhiteSpace(icon)
                ? "📺"
                : icon;

        configuration.Save();
        return true;
    }

    internal bool Delete(
       int slotIndex)
    {
        if (!IsValidSlot(slotIndex) ||
            configuration.SavedQueueProfiles[slotIndex] is null)
        {
            return false;
        }

        var deletingActiveQueue =
            configuration.ActiveQueueSlot ==
            slotIndex;

        configuration.SavedQueueProfiles[slotIndex] =
            null;

        if (deletingActiveQueue)
        {
            var nextSlot =
                configuration.SavedQueueProfiles.FindIndex(
                    profile => profile is not null);

            configuration.ActiveQueueSlot =
                nextSlot >= 0
                    ? nextSlot
                    : 0;

            var replacementEntries =
                nextSlot >= 0
                    ? configuration
                        .SavedQueueProfiles[nextSlot]!
                        .Entries
                    : Enumerable.Empty<VideoQueueRecord>();

            //
            // Preserve the current playing item, but immediately switch
            // all upcoming queue behaviour to the replacement slot.
            //
            queue.ReplaceUpcomingEntries(
                replacementEntries);
        }

        configuration.Save();
        return true;
    }

    internal void SaveActiveEntries()
    {
        if (ActiveProfile is not { } profile)
        {
            return;
        }

        profile.Entries =
            queue.ExportEntries();

        //
        // Keep the legacy field synchronized during the migration period.
        //
        configuration.VideoQueue =
            new List<VideoQueueRecord>(
                profile.Entries);

        configuration.Save();
    }

    private bool IsValidSlot(
        int slotIndex) =>
        slotIndex >= 0 &&
        slotIndex <
        configuration.SavedQueueProfiles.Count;
}