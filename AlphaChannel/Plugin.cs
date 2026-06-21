using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SharpCompress.Compressors.ZStandard.Unsafe;

namespace AlphaChannel;

public class Plugin : IDalamudPlugin
{
	// Required for LivePluginLoader support
	public string? AssemblyLocationMPV { get; set; }
	public string? AssemblyLocationYTDLP { get; set; }
	public string? AssemblyLocationSnes { get; set; }
	public static string? ROMSLocationSnesDir { get; set; }
	

	public static Guid PluginSessionGUID { get; set;}
	public Dictionary<string, string> PenumbraTempModPaths { get; set;}
	public Dictionary<string, string> PenumbraTempScreenPaths { get; set;}
	

	// Required for LivePluginLoader support — interface member cannot be static
	public string Name => "AlphaChannel";

	public WindowSystem WindowSystem { get; } = new("AlphaChannel");
	private const string CommandRemote = "/aremote";

	public static readonly int ResolutionWidth = 1920;
	public static readonly int ResolutionHeight = 1080;
	private readonly ControlWindow _mainWindow;
	private readonly Core _core;
	private readonly string _pluginDir;
	public Resources LibResources { get; }
	public Configuration Config { get; }
	public WndProcKeyUpReader WindowKeyUpReader { get; }

	public Plugin(IDalamudPluginInterface pluginInterface)
	{
		PluginSessionGUID = Guid.NewGuid();
		// init services
		pluginInterface.Create<Services>();

		_pluginDir = pluginInterface.AssemblyLocation.DirectoryName ?? "";
		if (string.IsNullOrEmpty(_pluginDir))
		{
			throw new InvalidOperationException("Could not determine plugin directory");
		}

		Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
		Config.Initialize(pluginInterface);
		
		LibResources = new Resources(_pluginDir);
		PenumbraTempModPaths = LibResources.LoadPenumbraModResources();
		PenumbraTempScreenPaths = LibResources.LoadPenumbraScreenResources();
		
		ROMSLocationSnesDir = Path.Combine(_pluginDir, "resources", "snes")!;

		MpvRenderer.Setup(this);
		Resources.NativeLoader.Register(this);

		// Spin up DX handling from the plugin interface
		DxHandler.Initialise(Services.PluginInterface);

		// Hook up render hook
		pluginInterface.UiBuilder.Draw += Render;

		// Create Main Window
		string title = "AlphaChannel Remote ";
		#if IS_TEST
				title += " (Test)";
		#endif
		_core = new Core(this);
		_mainWindow = new ControlWindow(this, _core, title);
		WindowSystem.AddWindow(_mainWindow);

		pluginInterface.UiBuilder.OpenConfigUi += ToggleMainUI;
		pluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;

		Services.CommandManager.AddHandler(CommandRemote, new CommandInfo(HandleCommand) { HelpMessage = "Toggles the Remote Window", ShowInHelp = true });

		ApiProvider.Init(this);

		Services.Framework.Update += OnFrameworkUpdate;

		WindowKeyUpReader = new WndProcKeyUpReader(pluginInterface.UiBuilder.WindowHandlePtr, Services.InteropProvider);
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		_core?.OnFrameworkUpdate();
	}

	public void Dispose()
	{
		_mainWindow?.Dispose();

		DxHandler.Shutdown();

		ApiProvider.DeInit();

		LibResources?.Dispose();

		PenumbraIPC.Dispose();

		WindowSystem?.RemoveAllWindows();

		WindowKeyUpReader?.Dispose();

		GC.SuppressFinalize(this);
	}

	private void Render()
	{
		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(5, 5));

		_mainWindow?.Refresh();

		DrawUI();
		DrawPopup();

		ImGui.PopStyleVar();
	}

	private void HandleCommand(string command, string rawArgs)
	{
		if (CommandRemote.Equals(command, StringComparison.Ordinal))
		{
			ToggleMainUI();
		}
	}

	private static bool _showError;
	private static string _errorMessage = "";
	public static void ErrorPopup(string? message)
	{
		if(_showError || message == null)
		{
			return;
		}
		_errorMessage = message;
		_showError = true;
	}
	private void DrawPopup()
	{
		if (_showError)
		{
			ImGui.SetNextWindowSizeConstraints(new Vector2(400, 150), new Vector2(800, 600));
			ImGui.OpenPopup("AlphaChannel Error Message");
		}

		if (ImGui.BeginPopupModal("AlphaChannel Error Message", ref _showError))
		{
			ImGui.TextWrapped(_errorMessage);
			ImGui.Separator();
			ImGui.Text(string.Empty);
			float buttonWidth = 120f;
			ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - buttonWidth) / 2f);
			if (ImGui.Button("OK", new Vector2(buttonWidth, 0)))
			{
				_showError = false;
				ImGui.CloseCurrentPopup();
			}
			ImGui.EndPopup();
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
		ControlWindow.IPCVideoState? state = _mainWindow.IPCGetState();
		return state is null ? null : JsonSerializer.Serialize(state);
	}

	internal void OnIPCSetState(nint addr, string s)
	{
		_mainWindow.IPCSetState(addr, s);
	}
	
	internal void OnIPCClearState(nint addr)
	{
		_mainWindow.RemoveOtherPlayer(addr);
	}

	internal void UpdateIPCState(ControlWindow.IPCVideoState? state)
	{
		string? IPCstate = state is null ? null : JsonSerializer.Serialize(state);
		ApiProvider.NotifyStateChange(IPCstate, IPCstate);
	}
}
