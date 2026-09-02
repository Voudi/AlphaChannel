namespace AlphaChannel.Plugin.Video;

[Serializable]
internal sealed class SavedQueueProfile
{
    public string Name { get; set; } = "";

    public string Icon { get; set; } = "📺";

    public List<VideoQueueRecord> Entries { get; set; } = new();
}