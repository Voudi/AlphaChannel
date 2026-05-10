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
	private CancellationTokenSource _RenderCancellation = new CancellationTokenSource();
	private MpvRenderer? _currentMpvRenderer;

	public Resources LibResources { get; }

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
		TerminatePlayer();

		IpcProvider.DeInit();

		WindowSystem.RemoveAllWindows();

		_mainWindow?.Dispose();

		_currentMpvRenderer?.StopRender();

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
	private DateTime _lastLoadYT = DateTime.MinValue;
	private static readonly Regex _YTRegex = new Regex(@"^\w+://[^/]*youtube\.\w+/|^\w+://youtu\.be/", RegexOptions.Compiled);
	private static bool IsYTURL(string url) => _YTRegex.IsMatch(url);
	public void StartMPV(string url, Texture2D sharedTexture)
	{
		if(_currentMpvRenderer != null && _currentMpvRenderer.GetCurrentUrl() == url && !_currentMpvRenderer.IsIdle())
			return;
			
		int sleepTime = 0;
		if(IsYTURL(url))
		{
			var elapsed = DateTime.Now - _lastLoadYT;
			if (elapsed.TotalSeconds < 10)
				sleepTime = Math.Min(Math.Max((int)(10000 - elapsed.TotalMilliseconds), 0), 10000);
			
			_lastLoadYT = DateTime.Now;
		}

		Task.Run(() =>
		{
			Thread.Sleep(sleepTime);
			
			if(_currentMpvRenderer != null)
				StartURL(url);
			try
			{
				if(_currentMpvRenderer == null)
				{
					_currentMpvRenderer = new MpvRenderer();
					_currentMpvRenderer.Initialize(_resolutionWidth, _resolutionHeight, url, sharedTexture, _RenderCancellation);
					Services.Log.Debug("Video Player started");
					
					while(true)
					{
						if (!_currentMpvRenderer.RenderFrame())
							break;
					}

					Services.Log.Debug("Video Player stopped");
					_mainWindow.TurnOffTV();
				}
			}
			catch (Exception e)
			{
				Services.Log.Error($"Error: {e.Message} {e.StackTrace}");
			}
			return;
		});
		
		return;
	}

	public void StartURL(string url)
	{
		_currentMpvRenderer?.Play(url);
	}

	public void StopPlayer()
	{
		_currentMpvRenderer?.Stop();
	}

	private void TerminatePlayer()
	{
		_currentMpvRenderer?.StopRender();
	}

    public void TogglePause()
    {
		if(!_RenderCancellation.Token.IsCancellationRequested)
		{
			_currentMpvRenderer?.TogglePause();
		}
    }

	public bool? IsIdle()
    {
		if(!_RenderCancellation.Token.IsCancellationRequested)
		{
			return _currentMpvRenderer?.IsIdle();
		}
		return true;
    }

	public bool GetPaused()
	{
		if (!_RenderCancellation.Token.IsCancellationRequested)
		{
			return _currentMpvRenderer?.GetPaused() ?? false;
		}
		return false;
	}
	public double[] GetPlayerInfos()
	{
		if (!_RenderCancellation.Token.IsCancellationRequested)
		{
			return _currentMpvRenderer?.GetProperties() ?? [0, 0, 0];
		}
		return [0, 0, 0];
	}

    public void SeekPlayer(int seconds)
    {
		if (!_RenderCancellation.Token.IsCancellationRequested)
		{
			_currentMpvRenderer?.Seek(seconds);
		}
    }

    public void VolumePlayer(int vol)
    {
		if (!_RenderCancellation.Token.IsCancellationRequested)
		{
			_currentMpvRenderer?.SetVolume(vol);
		}
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

    internal string GetMediaTitle()
    {
		if (!_RenderCancellation.Token.IsCancellationRequested)
		{
			return _currentMpvRenderer?.GetMediaTitle() ?? "";
		}
		return "";
    }
}