using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Bindings.ImGui;
using System.Numerics;
using System.Reflection;
using SharpDX.Direct3D11;

namespace AlphaChannel;

public class Plugin : IDalamudPlugin
{
	// Required for LivePluginLoader support
	public string? AssemblyLocationMPV { get; set; }
	public string? AssemblyLocationYTDLP { get; set; }
	// Required for LivePluginLoader support
	public string Name => "AlphaChannel";

	public readonly WindowSystem WindowSystem = new("AlphaChannel");
	private const string _commandRemote = "/aremote";

	public static readonly int _resolutionWidth = 1920;
	public static readonly int _resolutionHeight = 1080;
	private ControlWindow _mainWindow;
	private readonly string _pluginConfigDir;
	private readonly string _pluginDir;
	public Resources LibResources { get; }
	public static readonly HttpClient HTTPCLIENT = new HttpClient();
    public static readonly HttpClient NOREDIRECTHTTPCLIENT = new HttpClient(
		new HttpClientHandler { AllowAutoRedirect = false }
	);

	public Plugin(IDalamudPluginInterface pluginInterface)
	{
		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault(false);

		string title = "AlphaChannel Remote ";

		#if IS_TEST
			title += " (Test)";
		#endif

        // init services
        pluginInterface.Create<Services>();

		_pluginDir = pluginInterface.AssemblyLocation.DirectoryName ?? "";
		if (String.IsNullOrEmpty(_pluginDir))
		{
			throw new Exception("Could not determine plugin directory");
		}

		_pluginConfigDir = pluginInterface.GetPluginConfigDirectory();

		LibResources = new Resources(_pluginDir);

        // Spin up DX handling from the plugin interface
        DxHandler.Initialise(Services.PluginInterface);

		// Hook up render hook
		pluginInterface.UiBuilder.Draw += Render;

		//IpcProvider.Init(this);

		MpvRenderer.Setup(this);

		// Create Main Window
		_mainWindow = new ControlWindow(this, title);
		WindowSystem.AddWindow(_mainWindow);

		pluginInterface.UiBuilder.OpenConfigUi += ToggleMainUI;
		pluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;

        Services.CommandManager.AddHandler(_commandRemote, new CommandInfo(HandleCommand) { HelpMessage = "Toggles the Remote Window", ShowInHelp = true });
    }

	public void Dispose()
	{
		IpcProvider.DeInit();

		WindowSystem.RemoveAllWindows();

		_mainWindow?.Dispose();

		DxHandler.Shutdown();
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
		if (_commandRemote.Equals(command))
			ToggleMainUI();
	}

	private void DrawUI() => WindowSystem.Draw();
	public void ToggleMainUI() => _mainWindow?.Toggle();

    internal void UpdateTitle(uint entityId, TitleData titleData)
	{
		if (titleData?.Title != null)
			_mainWindow?.UpdateTitle(entityId, titleData.Title);
	}

	internal string GetModPath()
	{
		return Path.Combine(_pluginDir, "resources\\AlphaChannelTV.pmp");
	}

    internal void CheckURLHook()
    {
		if (!IpcProvider.Initialized)
		{
            IpcProvider.Init(this);
        }
	}

}