using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ImGuiNET;
using AlphaChannel.Renderer;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;

namespace AlphaChannel;

public class Plugin : IDalamudPlugin
{
	public readonly WindowSystem WindowSystem = new("AlphaChannel");
	private ControlWindow _mainWindow;

	private const string _commandRemote = "/aremote";

	private readonly DependencyManager _dependencyManager;
	private readonly string _pluginConfigDir;
	private readonly string _pluginDir;

	private CaptureProcess? _capture;
	private WebView2Client? _webView2Client;

	public Plugin(IDalamudPluginInterface pluginInterface)
	{
		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault(false);

		// init services
		pluginInterface.Create<Services>();

		_pluginDir = pluginInterface.AssemblyLocation.DirectoryName ?? "";
		if (String.IsNullOrEmpty(_pluginDir))
		{
			throw new Exception("Could not determine plugin directory");
		}

		_pluginConfigDir = pluginInterface.GetPluginConfigDirectory();

		_dependencyManager = new DependencyManager(_pluginDir, _pluginConfigDir);
		_dependencyManager.DependenciesReady += (_, _) => DependenciesReady();
		_dependencyManager.Initialise();

		// Spin up DX handling from the plugin interface
		DxHandler.Initialise(Services.PluginInterface);

		// Hook up render hook
		pluginInterface.UiBuilder.Draw += Render;

		IpcProvider.Init(this);

		// Create Main Window
		_mainWindow = new ControlWindow(this);
		WindowSystem.AddWindow(_mainWindow);

		pluginInterface.UiBuilder.OpenConfigUi += ToggleMainUI;
		pluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
	}


	// Required for LivePluginLoader support
	public string AssemblyLocation { get; } = Assembly.GetExecutingAssembly().Location;
	public string Name => "AlphaChannel";

	public void Dispose()
	{
		TerminateAlphaWindow();

		IpcProvider.DeInit();

		WindowSystem.RemoveAllWindows();

		_mainWindow?.Dispose();

		DxHandler.Shutdown();

		_dependencyManager.Dispose();
	}

	private void DependenciesReady()
	{

		// Hook up the remote command
		Services.CommandManager.AddHandler(_commandRemote, new CommandInfo(HandleCommand) { HelpMessage = "Toggles the Remote Window", ShowInHelp = true });
	}

	public bool _webViewInitialized = false;
	private void InitializeWebView(string url, string handle)
	{
		if (_webViewInitialized)
		{
			_webView2Client?.Navigate(url);
			return;
		}
		_webViewInitialized = true;

		Thread capturestaThread = new Thread(() =>
		{
			int pid = Process.GetCurrentProcess().Id;
			try
			{
				var webViewHandle = _webView2Client?.handle;
				if (webViewHandle.HasValue)
				{
					_capture = new CaptureProcess(pid, _pluginDir, webViewHandle.Value, handle);
					_capture.Start();
				}
			}
			catch { }
		});

		capturestaThread.SetApartmentState(ApartmentState.STA);

		Thread staThread = new(() => {
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			var adBlocknames = new Dictionary<string, string>
            {
                { "uBlock", _dependencyManager.GetDependencyPathFor("ublock") }
            };
            _webView2Client = new WebView2Client(_mainWindow, adBlocknames, Path.Combine(_pluginConfigDir, "webview-cache"), url);
			capturestaThread.Start();
			Application.Run(_webView2Client);
		});

		staThread.SetApartmentState(ApartmentState.STA);
		staThread.Start();
	}

	private void Render()
	{
		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(5, 5));

		_capture?.EnsureRenderProcessIsAlive();

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


	public void TerminateAlphaWindow()
	{
		_webViewInitialized = false;
		_webView2Client?.RemoveWindow();

		_capture?.Dispose();
	}

	public void NavigateAlphaWindow(string url, string sharedHandle)
	{
		InitializeWebView(url, sharedHandle);
	}

	public void ToggleExpandAlphaWindow()
	{
		_webView2Client?.ToggleResize();
	}

    public void PollWebviewWindow()
    {
        _webView2Client?.PollMainwindow();
    }

    public void Play()
    {
        _webView2Client?.TryPlay();
    }

    public void Fullscreen()
    {
        _webView2Client?.TryFullscreen();
    }

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
			IpcProvider.Init(this);
    }
}