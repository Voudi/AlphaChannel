using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AlphaChannel;

public class Services
{

	[PluginService]
	public static IObjectTable Objects { get; private set; } = null!;
	public static uint? LocalPlayerId => Objects.LocalPlayer?.EntityId;

	[PluginService]
	public static IPluginLog Log { get; private set; } = null!;

	[PluginService]
	public static ICommandManager CommandManager { get; private set; } = null!;

	[PluginService]
	public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

	[PluginService]
	public static IChatGui Chat { get; set; } = null!;

	[PluginService]
	public static IGameInteropProvider InteropProvider { get; private set; } = null!;

	[PluginService]
	public static ISigScanner SigScanner { get; private set; } = null!;

	[PluginService]
	public static IDutyState DutyState { get; private set; } = null!;

	[PluginService]
	public static IFramework Framework { get; private set; } = null!;

	[PluginService]
	public static IKeyState KeyState { get; private set; } = null!;

	[PluginService]
	public static IGamepadState GamepadState { get; private set; } = null!;
}
