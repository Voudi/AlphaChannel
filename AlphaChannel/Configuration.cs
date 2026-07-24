using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;

namespace AlphaChannel;

[Serializable]
public class ScreenPositionPreset
{
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float RotationDegrees { get; set; }
    public float Scale { get; set; } = 1.0f;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; }

    public Dictionary<Snes9xInput, string> KeyMappings { get; set; } = [];
    public List<string> RecentPaths { get; set; } = [];
    public List<ScreenPositionPreset> ScreenPresets { get; set; } = [];
    public string RelayUrl { get; set; } = "";

    public int YoutubeMaxQuality { get; set; } = 1080; //0 = best available, otherwise a max height cap in px
    public int YoutubeDefaultVolume { get; set; } = 25;
    public bool YoutubeHardwareDecoding { get; set; }
    public bool YoutubeDisableTlsVerify { get; set; }

    public bool HideNearbyNameplates { get; set; } = true;

    public Snes9xEffect SnesEffect { get; set; } = Snes9xEffect.CrtScanlines;
    public float SnesEffectMaskStrength { get; set; } = 0.30f;
    public float SnesEffectScanBeam { get; set; } = 2.5f;

	[NonSerialized] private IDalamudPluginInterface _pi = null!;

    public void Initialize(IDalamudPluginInterface pi) => _pi = pi;
    public void Save() => _pi.SavePluginConfig(this);
}