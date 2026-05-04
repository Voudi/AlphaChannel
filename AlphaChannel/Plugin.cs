using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Bindings.ImGui;
using System.Numerics;
using System.Reflection;
using SharpDX.Direct3D11;
using System.Text.RegularExpressions;

namespace AlphaChannel;

public class Plugin : IDalamudPlugin
{
	// Required for LivePluginLoader support
	public string AssemblyLocation { get; } = Assembly.GetExecutingAssembly().Location;
	// Required for LivePluginLoader support
	public string Name => "AlphaChannel";

	public readonly WindowSystem WindowSystem = new("AlphaChannel");
	private const string _commandRemote = "/aremote";

	public static readonly int _resolutionWidth = 1280;
	public static readonly int _resolutionHeight = 720;
	private ControlWindow _mainWindow;
	private readonly string _pluginConfigDir;
	private readonly string _pluginDir;
	private CancellationTokenSource _RenderCancellation = new CancellationTokenSource();

	public Plugin(IDalamudPluginInterface pluginInterface)
	{
		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault(false);

		string title = "AlphaChannel Remote ";

		#if IS_TEST
			title += pluginInterface.Manifest.TestingAssemblyVersion + " (Test Version)";
		#endif


        // init services
        pluginInterface.Create<Services>();

		_pluginDir = pluginInterface.AssemblyLocation.DirectoryName ?? "";
		if (String.IsNullOrEmpty(_pluginDir))
		{
			throw new Exception("Could not determine plugin directory");
		}

		_pluginConfigDir = pluginInterface.GetPluginConfigDirectory();

        // Spin up DX handling from the plugin interface
        DxHandler.Initialise(Services.PluginInterface);

		// Hook up render hook
		pluginInterface.UiBuilder.Draw += Render;

		//IpcProvider.Init(this);

		MpvRenderer.Setup(_pluginDir);

		// Create Main Window
		_mainWindow = new ControlWindow(this, title);
		WindowSystem.AddWindow(_mainWindow);

		pluginInterface.UiBuilder.OpenConfigUi += ToggleMainUI;
		pluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;

        Services.CommandManager.AddHandler(_commandRemote, new CommandInfo(HandleCommand) { HelpMessage = "Toggles the Remote Window", ShowInHelp = true });
    }

	public void Dispose()
	{
		TerminateAlphaWindow();

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


	public void TerminateAlphaWindow()
	{
		_RenderCancellation.Cancel();
	}

	private DateTime _lastLoadYT = DateTime.MinValue;
	private static readonly Regex _YTRegex = new Regex(@"^\w+://[^/]*youtube\.\w+/|^\w+://youtu\.be/", RegexOptions.Compiled);
	private static bool IsYTURL(string url) => _YTRegex.IsMatch(url);
	public int StartMPV(string url, Texture2D sharedTexture)
	{
		_RenderCancellation.Cancel();
		_RenderCancellation = new CancellationTokenSource();

		int sleepTime = 0;
		if(IsYTURL(url))
		{
			var elapsed = DateTime.Now - _lastLoadYT;
			if (elapsed.TotalSeconds < 10)
				sleepTime = Math.Min(Math.Max((int)(10000 - elapsed.TotalMilliseconds), 0), 10000);
			
			_lastLoadYT = DateTime.Now;
		}

		new Thread(() =>
		{
			Thread.Sleep(sleepTime);
			if (!_RenderCancellation.Token.IsCancellationRequested &&(Compatibility.IsRunningUnderWine() || true)) //For now, just always use the MPV player, even on native Windows
			{
				try
				{
						var mpvRenderer = new MpvRenderer();
						mpvRenderer.Initialize(_resolutionWidth, _resolutionHeight, url, sharedTexture);

						new Thread(() =>
						{
							Services.Log.Debug("Video Player started");
							while(!_RenderCancellation.Token.IsCancellationRequested)
							{
								if (!mpvRenderer.RenderFrame(_RenderCancellation.Token))
									break;
							}
							Services.Log.Debug("Video Player stopped");
							mpvRenderer.StopRender();
						}){IsBackground = true}.Start();
				}
				catch (Exception e)
				{
					Services.Log.Error($"Error: {e.Message} {e.StackTrace}");
				}
				return;
			}
			else
			{
				//TODO: Implement MPV player for non-Wine environments, potentially using a native DirectX rendering approach
			}
		}) { IsBackground = true }.Start();
		
		return sleepTime;
	}

    public void Play()
    {
        //TODO: Implement play/pause functionality for the mpv player
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
		{
            IpcProvider.Init(this);
			Services.Log.Error("Hook Initialization failed. Retrying...");
        }
	}
}