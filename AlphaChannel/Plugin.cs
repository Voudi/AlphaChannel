using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace AlphaChannel;

public class Plugin : IDalamudPlugin
{
	// Required for LivePluginLoader support
	public string? AssemblyLocationMPV { get; set; }
	public string? AssemblyLocationYTDLP { get; set; }
	public Dictionary<string, string> PenumbraTempModPaths { get; set;}

	// Required for LivePluginLoader support — interface member cannot be static
	public string Name => "AlphaChannel";

	public WindowSystem WindowSystem { get; } = new("AlphaChannel");
	private const string CommandRemote = "/aremote";

	public static readonly int ResolutionWidth = 1920;
	public static readonly int ResolutionHeight = 1080;
	private ControlWindow _mainWindow;
	private readonly string _pluginDir;
	public Resources LibResources { get; }
	public static readonly System.Net.Http.HttpClient HttpClient = new();
	public static readonly System.Net.Http.HttpClient NoRedirectHttpClient = new(
		new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false }
	);

	public Plugin(IDalamudPluginInterface pluginInterface)
	{
		// init services
		pluginInterface.Create<Services>();

		_pluginDir = pluginInterface.AssemblyLocation.DirectoryName ?? "";
		if (string.IsNullOrEmpty(_pluginDir))
		{
			throw new InvalidOperationException("Could not determine plugin directory");
		}

		LibResources = new Resources(_pluginDir);
		PenumbraTempModPaths = LibResources.LoadPenumbraModResources();

		// Spin up DX handling from the plugin interface
		DxHandler.Initialise(Services.PluginInterface);

		// Hook up render hook
		pluginInterface.UiBuilder.Draw += Render;

		//IpcProvider.Init(this);

		MpvRenderer.Setup(this);

		// Create Main Window
		string title = "AlphaChannel Remote ";
		#if IS_TEST
				title += " (Test)";
		#endif
		_mainWindow = new ControlWindow(this, title);
		WindowSystem.AddWindow(_mainWindow);

		pluginInterface.UiBuilder.OpenConfigUi += ToggleMainUI;
		pluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;

		Services.CommandManager.AddHandler(CommandRemote, new CommandInfo(HandleCommand) { HelpMessage = "Toggles the Remote Window", ShowInHelp = true });
	}

	public void Dispose()
	{
		WindowSystem.RemoveAllWindows();

		PenumbraIPC.Dispose();

		_mainWindow?.Dispose();

		DxHandler.Shutdown();

		GC.SuppressFinalize(this);
	}

	private void Render()
	{
		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(5, 5));

		_mainWindow?.Refresh();

		DrawUI();

		ImGui.PopStyleVar();
	}

	private void HandleCommand(string command, string rawArgs)
	{
		if (CommandRemote.Equals(command, StringComparison.Ordinal))
		{
			ToggleMainUI();
		}
	}

	private void DrawUI() => WindowSystem.Draw();
	private void ToggleMainUI() => _mainWindow?.Toggle();

	internal string GetModPath()
	{
		return Path.Combine(_pluginDir, "resources\\AlphaChannelTV.pmp");
	}

	internal string? OnIPCGetLocalState()
	{
		return _mainWindow.GetStateInfo();
	}

	internal void OnIPCSetState(nint addr, string s)
	{
		_mainWindow.UpdateOtherPlayer(addr, s);
	}

	internal void OnIPCApplyStateUpdate(nint addr, string s)
	{
		_mainWindow.UpdateOtherPlayerSeek(addr, s);
	}

	internal void OnIPCClearState(nint addr)
	{
		_mainWindow.RemoveOtherPlayer(addr);
	}

	internal void UpdateIPCState()
	{
		ApiProvider.NotifyStateChange(_mainWindow.GetStateInfo(), null);
	}
	
	internal void UpdateIPCPartialState(string? partialState = null)
	{
		ApiProvider.NotifyStateChange(_mainWindow.GetStateInfo(), partialState);
	}
}
