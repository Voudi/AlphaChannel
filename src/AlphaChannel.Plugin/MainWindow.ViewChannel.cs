using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// When clicking any youtube channel name, shows latest videos from that channel.
internal sealed partial class MainWindow
{
    // ---------------------------------------------------------
    // YouTube channel view
    // ---------------------------------------------------------


    private string? viewedChannelId;
    private string? viewedChannelName;


    private List<VideoSearchEntry>? viewedChannelVideos;
    private bool isLoadingViewedChannelVideos;

    private void OpenYouTubeChannel(
        string channelId,
        string channelName)
    {
        previousBrowseVideoSectionTab =
            browseVideoSectionTab;

        currentPage = HomePage.VideoGrid;

        viewedChannelId = channelId;
        viewedChannelName = channelName;

        viewedChannelVideos = null;

        _ = LoadViewedChannelVideosAsync();
    }
    private async Task LoadViewedChannelVideosAsync()
    {
        if (string.IsNullOrEmpty(viewedChannelId))
        {
            return;
        }

        try
        {
            isLoadingViewedChannelVideos = true;

            var videos =
                await searchResolver
                    .GetChannelUploadsAsync(
                        viewedChannelId,
                        15,
                        CancellationToken.None)
                    .ConfigureAwait(false);

            viewedChannelVideos =
                videos
                    .OrderByDescending(
                        video =>
                            video.UploadDate ??
                            DateTimeOffset.MinValue)
                    .Take(15)
                    .ToList();
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Channel View] Failed to load channel videos: {exception.Message}");

            viewedChannelVideos = [];
        }
        finally
        {
            isLoadingViewedChannelVideos = false;
        }
    }

    private void DrawYouTubeChannelPage()
    {
        // ---------------------------------------------------------
        // Back button
        // ---------------------------------------------------------

        if (ImGui.Button("← Back"))
        {
            viewedChannelId = null;
            viewedChannelName = null;
            viewedChannelVideos = null;

            browseVideoSectionTab =
                previousBrowseVideoSectionTab;

            return;
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));

        // ---------------------------------------------------------
        // Header
        // ---------------------------------------------------------

        using (ImRaii.PushFont(
            UiBuilder.IconFont))
        {
            ImGui.TextColored(
                AccentHover,
                FontAwesomeIcon.PlayCircle.ToIconString());
        }

        ImGui.SameLine(
            0f,
            8f);

        ImGui.SetWindowFontScale(
            1.1f);

        ImGui.TextColored(
            Vector4.One,
            viewedChannelName ?? "Channel");

        ImGui.SameLine(
    0f,
    12f);

        ImGui.SetWindowFontScale(
            0.85f);

        ImGui.TextColored(
            MutedText,
            "Showing last 15 uploads");

        ImGui.SetWindowFontScale(
            1f);

        ImGui.SetWindowFontScale(
            1f);

        ImGui.Dummy(
            new Vector2(
                0f,
                12f));


        // ---------------------------------------------------------
        // Loading
        // ---------------------------------------------------------

        if (isLoadingViewedChannelVideos &&
            viewedChannelVideos is null)
        {
            ImGui.TextColored(
                MutedText,
                "Loading channel videos...");

            return;
        }


        if (viewedChannelVideos is not
            { Count: > 0 } videos)
        {
            ImGui.TextColored(
                MutedText,
                "No videos found.");

            return;
        }


        // ---------------------------------------------------------
        // Video grid
        // ---------------------------------------------------------

        const int columns = 5;
        const float gap = 12f;
        const float rowGap = 16f;
        var cardHeight = Ui(224f);

        var contentWidth =
            ImGui.GetContentRegionAvail().X;

        var cardWidth =
            (contentWidth -
             gap * (columns - 1)) /
            columns;


        for (var index = 0;
             index < videos.Count;
             index++)
        {
            if (index > 0)
            {
                if (index % columns == 0)
                {
                    ImGui.Dummy(
                        new Vector2(
                            0f,
                            rowGap));
                }
                else
                {
                    ImGui.SameLine(
                        0f,
                        gap);
                }
            }

            ImGui.PushID(
                $"channelVideo_{index}");

            DrawHomeYouTubeCard(
                videos[index],
                cardWidth,
                cardHeight);

            ImGui.PopID();
        }
    }

}