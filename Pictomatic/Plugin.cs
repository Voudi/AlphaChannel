using Pictomatic.Common;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using ImGuiNET;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using SharpDX.Direct3D11;
using FFXIVClientStructs.Interop.Generated;
using static FFXIVClientStructs.FFXIV.Client.UI.Misc.GroupPoseModule;
using System.IO;
using ImGuiScene;
using System.Drawing;
using Honorific;
using System.Reflection.Metadata;
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

	private RenderProcess? _renderProcess;
	private Services _services;

	public unsafe Plugin(IDalamudPluginInterface pluginInterface)
	{
		// init services
		_services = pluginInterface.Create<Services>()!;

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

		MainWindow = new MainWindow(this);
		WindowSystem.AddWindow(MainWindow);
		IpcProvider.Init(this);

		pluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
	}

	// Required for LivePluginLoader support
	public string AssemblyLocation { get; } = Assembly.GetExecutingAssembly().Location;
	public string Name => "Pictomatic";

	public void Dispose()
	{
		IpcProvider.DeInit();

		WindowSystem.RemoveAllWindows();

		_renderProcess?.Dispose();

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

		_renderProcess = new RenderProcess(pid, _pluginDir, _pluginConfigDir, _dependencyManager, Services.PluginLog);
		_renderProcess.Rpc.RendererReady += msg =>
		{
			if (!msg.HasDxSharedTexturesSupport)
			{
				Services.PluginLog.Error("Could not initialize shared textures transport. Browsingway will not work.");
				return;
			}
		};

		_renderProcess.Rpc.UpdateTexture += msg =>
		{
			Services.Framework.RunOnFrameworkThread(() =>
			{
				MainWindow.UpdateSharedDXTexture((IntPtr) msg.TextureHandle);
			});
		};

		_renderProcess.Rpc.AddSubProcess += msg =>
		{
			Services.Framework.RunOnFrameworkThread(() =>
			{
				this.MainWindow.AddSubProcess(msg.ProcessId);
			});
		};
		_renderProcess.Start();

		// Hook up the remote command
		Services.CommandManager.AddHandler(_commandRemote, new CommandInfo(HandleCommand) { HelpMessage = "Toggles the Remote Window", ShowInHelp = true });
	}

	private void Render()
	{
		_dependencyManager.Render();

		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));

		_renderProcess?.EnsureRenderProcessIsAlive();

		this.MainWindow?.RefreshTVs();
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
		_renderProcess.Rpc.Navigate(Guid.Empty, "kill");
	}

	public void NavigatePictomaticWindow(string url)
	{
		_renderProcess.Rpc.Navigate(Guid.Empty, url);
	}

	public void ToggleExpandPictomaticWindow()
	{
		_renderProcess.Rpc.Zoom(Guid.Empty, 0);
	}

	internal void UpdateTitle(uint entityId, TitleData titleData)
	{
		this.MainWindow.UpdateTitle(entityId, titleData.Title);
	}
}