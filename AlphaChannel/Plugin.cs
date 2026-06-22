using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SharpCompress.Compressors.ZStandard.Unsafe;

namespace AlphaChannel;

public class Plugin : IDalamudPlugin, IDisposable
{
	private const string PluginName = "AlphaChannel";
	private const string Command = "/aremote";

	internal const int ScreenWidth = 1920;
	internal const int ScreenHeight = 1080;

	public string Name => PluginName;

	internal Guid PluginSessionGUID { get; }
	internal string PluginDir { get; }
	internal string ConfigDir { get; }

	internal string AssemblyLocationMPV { get; set; }
	internal string AssemblyLocationYTDLP { get; set; }
	internal string AssemblyLocationSnes { get; set; }
	internal string ROMSLocationSnesDir { get => Path.Combine(ConfigDir, "snes"); }
	internal Dictionary<string, string> PenumbraTempModPaths { get; set;}
	internal Dictionary<string, string> PenumbraTempScreenPaths { get; set;}

	internal WindowSystem WindowSystem { get; } = new(PluginName);
	internal ControlWindow MainWindow { get; }
	internal Core Core { get; }
	internal Resources LibResources { get; }
	internal Configuration Config { get; }
	internal WndProcKeyUpReader WindowKeyUpReader { get; }

	public Plugin(IDalamudPluginInterface pluginInterface)
	{
		PluginSessionGUID = Guid.NewGuid();
		
		pluginInterface.Create<Services>();

		PluginDir = pluginInterface.AssemblyLocation.DirectoryName ?? "";
		if (string.IsNullOrEmpty(PluginDir))
		{
			throw new InvalidOperationException("Could not determine plugin directory");
		}

		Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
		Config.Initialize(pluginInterface);
		Config.Save();
		ConfigDir = pluginInterface.ConfigDirectory.FullName;

		LibResources = new Resources(this);
		AssemblyLocationMPV = LibResources.GetLocationMPV() ?? string.Empty;
		AssemblyLocationYTDLP = LibResources.GetLocationYTDLP() ?? string.Empty;
		AssemblyLocationSnes = LibResources.GetLocationSNES9X() ?? string.Empty;
		PenumbraTempModPaths = LibResources.LoadPenumbraModResources();
		PenumbraTempScreenPaths = LibResources.LoadPenumbraScreenResources();

		Resources.NativeLoader.Register(this);
		MpvRenderer.Setup(this);
		DxHandler.Initialise(Services.PluginInterface);
		

		Core = new Core(this);

		string title = "AlphaChannel Remote ";
		#if IS_TEST
				title += " (Test)";
		#endif
		MainWindow = new ControlWindow(this, Core, title);
		WindowSystem.AddWindow(MainWindow);
		
		ApiProvider.Init(this);

		Services.Framework.Update += OnFrameworkUpdate;

		WindowKeyUpReader = new WndProcKeyUpReader(pluginInterface.UiBuilder.WindowHandlePtr, Services.InteropProvider);

		Services.CommandManager.AddHandler(Command, new CommandInfo(HandleCommand) { HelpMessage = "Toggles the Remote Window", ShowInHelp = true });

		pluginInterface.UiBuilder.OpenConfigUi += ToggleMainUI;
		pluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
		pluginInterface.UiBuilder.Draw += Render;
	}

	private void HandleCommand(string command, string rawArgs)
	{
		if (Command.Equals(command, StringComparison.Ordinal))
		{
			ToggleMainUI();
		}
	}

	private void ToggleMainUI() => MainWindow?.Toggle();

	private void Render()
	{
		WindowSystem.Draw();
		DrawPopup();
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		MainWindow?.OnFrameworkUpdate();
		Core?.OnFrameworkUpdate();
	}

	public void Dispose()
	{
		MainWindow?.Dispose();

		DxHandler.Dispose();

		ApiProvider.DeInit();

		LibResources?.Dispose();

		PenumbraIPC.Dispose();

		WindowSystem?.RemoveAllWindows();

		WindowKeyUpReader?.Dispose();

		GC.SuppressFinalize(this);
	}

	private static bool _showError;
	private static string _errorMessage = "";
	private void DrawPopup()
	{
		if (_showError)
		{
			ImGui.SetNextWindowSizeConstraints(new Vector2(400, 150), new Vector2(800, 600));
			ImGui.OpenPopup(PluginName + " Error Message");
		}

		if (ImGui.BeginPopupModal(PluginName + " Error Message", ref _showError))
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
	internal static void ErrorPopup(string? message)
	{
		if(_showError || string.IsNullOrWhiteSpace(message))
		{
			return;
		}
		_errorMessage = message;
		_showError = true;
	}

	/* Sync Methods */
	internal string? OnIPCGetLocalState()
	{
		ControlWindow.IPCVideoState? state = MainWindow?.IPCGetState();
		return state is null ? null : JsonSerializer.Serialize(state);
	}

	internal void OnIPCSetState(nint addr, string s)
	{
		MainWindow?.IPCSetState(addr, s);
	}
	
	internal void OnIPCClearState(nint addr)
	{
		MainWindow?.RemoveOtherPlayer(addr);
	}

	internal void UpdateIPCState(ControlWindow.IPCVideoState? state)
	{
		string? IPCstate = state is null ? null : JsonSerializer.Serialize(state);
		ApiProvider.NotifyStateChange(IPCstate, IPCstate);
	}
}
