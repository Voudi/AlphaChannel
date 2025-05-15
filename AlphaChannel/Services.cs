using Dalamud.Game;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AlphaChannel;

public class Services
{
	[PluginService] 
	public static IObjectTable Objects { get; private set; } = null!;

	[PluginService] 
	public static IPluginLog Log { get; private set; } = null!;

	[PluginService]
	public static ICommandManager CommandManager { get; private set; } = null!;

	[PluginService]
	public static IPluginLog PluginLog { get; private set; } = null!;

	[PluginService]
	public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

	[PluginService]
	public static IFramework Framework { get; private set; } = null!;

	[PluginService]
	public static IClientState ClientState { get; set; } = null!;

	[PluginService]
	public static IGameInteropProvider InteropProvider { get; private set; } = null!;

    [PluginService]
    public static ISigScanner SigScanner { get; private set; } = null!;

    [PluginService]
    public static IDutyState DutyState { get; private set; } = null!;
}