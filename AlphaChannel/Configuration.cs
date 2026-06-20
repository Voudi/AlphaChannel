using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;

namespace AlphaChannel;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; }
    int IPluginConfiguration.Version { get => Version; set => throw new NotImplementedException(); }

    public Dictionary<Snes9xInput, VirtualKey> KeyMappings { get; set; } = new();
	

	[NonSerialized] private IDalamudPluginInterface _pi = null!;

    public void Initialize(IDalamudPluginInterface pi) => _pi = pi;
    public void Save() => _pi.SavePluginConfig(this);
}