using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;

namespace AlphaChannel;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; }

    public Dictionary<Snes9xInput, string> KeyMappings { get; set; } = [];
    public List<string> RecentPaths { get; set; } = [];

	[NonSerialized] private IDalamudPluginInterface _pi = null!;

    public void Initialize(IDalamudPluginInterface pi) => _pi = pi;
    public void Save() => _pi.SavePluginConfig(this);
}