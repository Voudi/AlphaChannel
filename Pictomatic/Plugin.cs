using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ImGuiNET;
using Pictomatic.Renderer;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Security.Policy;

namespace Pictomatic;

public class Plugin : IDalamudPlugin
{
	public readonly WindowSystem WindowSystem = new("Pictomatic");
	private MainWindow MainWindow { get; init; }

	private const string _commandRemote = "/premote";

	private readonly DependencyManager _dependencyManager;
	private readonly string _pluginConfigDir;
	private readonly string _pluginDir;

	private CaptureProcess? _capture;
	private WebView2Client? _webView2Client;

	public unsafe Plugin(IDalamudPluginInterface pluginInterface)
	{
		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault(false);

		// init services
		pluginInterface.Create<Services>();

		MainWindow = new MainWindow(this);

		_pluginDir = pluginInterface.AssemblyLocation.DirectoryName ?? "";
		if (String.IsNullOrEmpty(_pluginDir))
		{
			throw new Exception("Could not determine plugin directory");
		}

		_pluginConfigDir = pluginInterface.GetPluginConfigDirectory();

		_dependencyManager = new DependencyManager(_pluginDir, _pluginConfigDir);
		_dependencyManager.DependenciesReady += (_, _) => DependenciesReady();
		_dependencyManager.Initialise();

		// Hook up render hook
		pluginInterface.UiBuilder.Draw += Render;
		
		WindowSystem.AddWindow(MainWindow);
		IpcProvider.Init(this);

		pluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
	}

	// Required for LivePluginLoader support
	public string AssemblyLocation { get; } = Assembly.GetExecutingAssembly().Location;
	public string Name => "Pictomatic";

	public void Dispose()
	{
		TerminatePictomaticWindow();

		IpcProvider.DeInit();

		WindowSystem.RemoveAllWindows();

		MainWindow.Dispose();

		DxHandler.Shutdown();

		_dependencyManager.Dispose();
	}

	private void DependenciesReady()
	{
		// Spin up DX handling from the plugin interface
		DxHandler.Initialise(Services.PluginInterface);

		// Boot the render process. This has to be done before initialising settings to prevent a
		// race condition overlays receiving a null reference.
		int pid = Process.GetCurrentProcess().Id;

		MainWindow.initTexture();

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
				_capture = new CaptureProcess(pid, _pluginDir, _webView2Client.handle, handle);
				_capture.Start();
			}
			catch { }
		});

		capturestaThread.SetApartmentState(ApartmentState.STA);

		Thread staThread = new Thread(() => {
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			_webView2Client = new WebView2Client(-1, MainWindow, _dependencyManager.GetDependencyPathFor("ublock"), Path.Combine(_pluginConfigDir, "webview-cache"), url);
			capturestaThread.Start();
			Application.Run(_webView2Client);
		});

		staThread.SetApartmentState(ApartmentState.STA);
		staThread.Start();
	}

	private void Render()
	{
		_dependencyManager.Render();

		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));

		_capture?.EnsureRenderProcessIsAlive();

		this.MainWindow?.Refresh();
		DrawUI();

		ImGui.PopStyleVar();
	}

	private void HandleCommand(string command, string rawArgs)
	{
		if (_commandRemote.Equals(command))
			MainWindow.Toggle();
	}

	private void DrawUI() => WindowSystem.Draw();
	public void ToggleMainUI() => MainWindow.Toggle();


	public void TerminatePictomaticWindow()
	{
		_webViewInitialized = false;
		_webView2Client?.RemoveWindow();

		_capture?.Dispose();
	}

	public void NavigatePictomaticWindow(string url, string sharedHandle)
	{
		InitializeWebView(url, sharedHandle);
	}

	public void ToggleExpandPictomaticWindow()
	{
		_webView2Client?.ToggleResize();
	}

	internal void UpdateTitle(uint entityId, TitleData titleData)
	{
		this.MainWindow.UpdateTitle(entityId, titleData.Title);
	}
}