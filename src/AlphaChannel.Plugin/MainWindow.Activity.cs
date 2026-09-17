using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Activity Channel: a friends-only feed of "X started watching", "X joined a watch-along", "X
// accepted your friend request" - refreshed on open and again whenever StreamClient's
// OnActivityNew ping fires (see AlphaChannel.Contracts.SocialSignalType, the feed itself is always
// fetched via REST, the socket only ever says "something changed, refetch").
//
// Layout identity: vertical timeline rail — not card tiles.
internal sealed partial class MainWindow
{
    private bool activityDirty = true;
    private bool activityLoading;
    private ActivityEventDto[] activityItems = [];
    private string? activityNextCursor;
    private bool activityUnreadDirty = true;
    private int activityUnreadCount;

    private void DrawActivity()
    {
        if (CurrentSession is not { } session)
        {
            DrawPlainEmpty("Activity needs a signed-in account.", "Open Settings",
                () => currentPage = HomePage.Settings);
            return;
        }

        if (activityDirty && !activityLoading)
        {
            RefreshActivity(session.Token, reset: true);
        }

        if (activityItems.Length == 0)
        {
            DrawPlainEmpty(
                activityLoading ? "Loading…" : "Quiet for now. Friend watches and joins show up here.",
                activityLoading ? null : "Find friends",
                activityLoading ? null : () => currentPage = HomePage.Friends);
            return;
        }

        foreach (var item in activityItems)
        {
            DrawTimelineRow($"act{item.CreatedAtUnix}{item.ActorDisplayName}", ActivityLabel(item));
        }

        if (activityNextCursor is { Length: > 0 } cursor)
        {
            ImGui.Spacing();
            using (ImRaii.Disabled(activityLoading))
            {
                if (ImGui.Button("Load older", UiVec(-1, 32)))
                {
                    RefreshActivity(session.Token, reset: false, before: long.Parse(cursor));
                }
            }
        }
    }

    private void RefreshActivity(string bearerToken, bool reset, long? before = null)
    {
        activityDirty = false;
        activityLoading = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var page = await activityClient.GetFeedAsync(bearerToken, before);
                if (page is null)
                {
                    return;
                }

                var visibleItems =
                    page.Items
                        .Where(item => !IsRemovedPostActivity(item))
                        .ToArray();

                activityItems = reset ? visibleItems : [.. activityItems, .. visibleItems];
                activityNextCursor = page.NextCursor;

                if (page.Items.Length > 0 && reset)
                {
                    await activityClient.MarkReadAsync(bearerToken, page.Items[0].CreatedAtUnix);
                    activityUnreadCount = 0;
                }
            }
            finally
            {
                activityLoading = false;
            }
        });
    }

    private static bool IsRemovedPostActivity(ActivityEventDto item) =>
        item.Type is "PostLiked" or "PostReplied" or "Mentioned";
}
