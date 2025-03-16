using Dalamud.Game;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Pictomatic;

public class Services
{
	[PluginService] 
	public static IObjectTable Objects { get; private set; } = null!;

	[PluginService] 
	public static IPluginLog Log { get; private set; } = null!;

	[PluginService]
	// ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
	public static ICommandManager CommandManager { get; private set; } = null!;

	[PluginService]
	// ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
	public static IChatGui Chat { get; private set; } = null!;

	[PluginService]
	// ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
	public static IPluginLog PluginLog { get; private set; } = null!;

	[PluginService]
	// ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
	public static ITextureProvider TextureProvider { get; private set; } = null!;

	[PluginService]
	// ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
	public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

	[PluginService]
	// ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
	public static IFramework Framework { get; private set; } = null!;

	[PluginService]
	// ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
	public static IClientState ClientState { get; set; } = null!;

	[PluginService] 
	public static IObjectTable ObjectTable { get; private set; } = null!;

	[PluginService]
	public static IGameInteropProvider InteropProvider { get; private set; } = null!;

	[PluginService]
	public static ISigScanner SigScanner { get; private set; } = null!;

	[PluginService]
	public static IDataManager DataManager { get; private set; } = null!;
}