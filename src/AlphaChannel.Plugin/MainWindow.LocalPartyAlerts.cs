using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly TimeSpan LocalPartyAlertRefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LocalPartyAlertCooldown = TimeSpan.FromHours(2);
    private static readonly HashSet<string> LocalPartyLocationStopWords = new(StringComparer.Ordinal)
    {
        "area", "district", "estate", "house", "plot", "room", "steps", "the", "ward",
    };

    private readonly ConcurrentQueue<(RoomDirectoryDto[] Rooms, string Location, string World)>
        pendingLocalPartyAlertResults = new();
    private DateTime nextLocalPartyAlertCheckUtc;
    private DateTime lastLocalPartyAlertUtc = DateTime.MinValue;
    private bool localPartyAlertCheckInFlight;

    internal void UpdateLocalPartyAlerts()
    {
        while (pendingLocalPartyAlertResults.TryDequeue(out var result))
        {
            localPartyAlertCheckInFlight = false;
            if (!CanShowLocalPartyAlert())
                continue;

            var ownAccountId = CurrentSession?.AccountId;
            var count = result.Rooms.Count(room =>
                !string.IsNullOrWhiteSpace(room.Location) &&
                !string.Equals(room.HostAccountId, ownAccountId, StringComparison.Ordinal) &&
                TryReadRoomWorld(room.Description, out var roomWorld) &&
                string.Equals(roomWorld, result.World, StringComparison.OrdinalIgnoreCase) &&
                LocationsLikelyMatch(result.Location, room.Location!));

            if (count > 0 && DateTime.UtcNow - lastLocalPartyAlertUtc >= LocalPartyAlertCooldown)
            {
                lastLocalPartyAlertUtc = DateTime.UtcNow;
                Plugin.ChatGui.Print(
                    $"[Alpha Channel] {count} people are hosting watch parties in your area! Check out the open parties on Alpha Channel");
            }
        }

        if (localPartyAlertCheckInFlight || DateTime.UtcNow < nextLocalPartyAlertCheckUtc ||
            !CanShowLocalPartyAlert() || CurrentSession is not { } session ||
            string.IsNullOrWhiteSpace(CurrentWorldName))
        {
            return;
        }

        var location = GetCurrentWatchPartyLocation();
        if (string.IsNullOrWhiteSpace(location))
        {
            nextLocalPartyAlertCheckUtc = DateTime.UtcNow + LocalPartyAlertRefreshInterval;
            return;
        }

        var world = CurrentWorldName.Trim();
        var token = session.Token;
        localPartyAlertCheckInFlight = true;
        nextLocalPartyAlertCheckUtc = DateTime.UtcNow + LocalPartyAlertRefreshInterval;

        _ = Task.Run(async () =>
        {
            try
            {
                var rooms = await roomsClient.ListAsync(token).ConfigureAwait(false);
                pendingLocalPartyAlertResults.Enqueue((rooms, location, world));
            }
            catch (Exception exception)
            {
                AepLog.Warning($"[WatchParty] Local party alert refresh failed: {exception.Message}");
                localPartyAlertCheckInFlight = false;
            }
        });
    }

    private bool CanShowLocalPartyAlert()
    {
        var engine = screenController.Engine;
        return stream.Mode == StreamMode.None &&
               queue.Current is null &&
               !video.IsPlayingLocalVideo &&
               !engine.IsPlayingGame &&
               !engine.IsPlayingBrowser;
    }

    private bool WasLocalPartyAlertShownWithin(TimeSpan period) =>
        DateTime.UtcNow - lastLocalPartyAlertUtc < period;

    private static bool TryReadRoomWorld(string? description, out string world)
    {
        world = string.Empty;
        if (string.IsNullOrWhiteSpace(description))
            return false;

        var start = description.IndexOf("<#", StringComparison.Ordinal);
        var end = start < 0 ? -1 : description.IndexOf("#>", start + 2, StringComparison.Ordinal);
        if (start < 0 || end < 0)
            return false;

        var parts = description.Substring(start + 2, end - start - 2)
            .Split('#', StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[2]))
            return false;

        world = parts[2];
        return true;
    }

    private static bool LocationsLikelyMatch(string currentLocation, string roomLocation)
    {
        var currentCompact = NormalizeLocation(currentLocation, keepSpaces: false);
        var roomCompact = NormalizeLocation(roomLocation, keepSpaces: false);
        if (currentCompact.Length >= 4 && roomCompact.Length >= 4 &&
            (currentCompact.Contains(roomCompact, StringComparison.Ordinal) ||
             roomCompact.Contains(currentCompact, StringComparison.Ordinal)))
        {
            return true;
        }

        var currentWords = NormalizeLocation(currentLocation, keepSpaces: true)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 4 && !LocalPartyLocationStopWords.Contains(word))
            .ToHashSet(StringComparer.Ordinal);
        return NormalizeLocation(roomLocation, keepSpaces: true)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(word => word.Length >= 4 && !LocalPartyLocationStopWords.Contains(word) && currentWords.Contains(word));
    }

    private static string NormalizeLocation(string value, bool keepSpaces)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(character))
            {
                if (keepSpaces && pendingSpace && result.Length > 0)
                    result.Append(' ');
                result.Append(char.ToLowerInvariant(character));
                pendingSpace = false;
            }
            else if (keepSpaces)
            {
                pendingSpace = true;
            }
        }

        return result.ToString();
    }
}
