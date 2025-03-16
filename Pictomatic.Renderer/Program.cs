using Pictomatic.Common.Ipc;
using Pictomatic.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Pictomatic.Renderer;

internal static class Program
{
	private static Thread? _parentWatchThread;
	private static EventWaitHandle? _waitHandle;

	public static RendererRpc _rpc = null!;

	private static bool _isShuttingDown;
	private static readonly object _lockIpc = new();

	public static WebView2Client? _webView2Client;
	public static CaptureProcess? _capture;

	public static string? _currentSharedHandle;
	public static string? _pluginDir, _assemblyDir, _cacheDir;

	public static bool _webViewInitialized = false;


	private static void Main(string[] rawArgs)
	{
		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault(false);

		var args = RenderParamsSerializer.Deserialize(rawArgs[0]);
		_pluginDir = args.PluginDir;
		_assemblyDir = args.CefAssemblyDir;
		_cacheDir = args.CefCacheDir;

		Run(args);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void Run(RenderParams args)
	{
		_waitHandle = new EventWaitHandle(false, EventResetMode.ManualReset, args.KeepAliveHandleName);

		// Boot up a thread to make sure we shut down if parent dies
		_parentWatchThread = new Thread(WatchParentStatus);
		_parentWatchThread.Start(args.ParentPid);
		AppDomain.CurrentDomain.FirstChanceException += (_, e) => Console.Error.WriteLine(e.Exception.ToString());

		bool dxRunning = DxHandler.Initialise(args.DxgiAdapterLuid);
		InitializeIpc(args.IpcChannelName);

		// Notify plugin that render process is running
		_ = _rpc.RendererReady(dxRunning);
		_waitHandle.WaitOne();
		lock (_lockIpc)
		{
			_isShuttingDown = true;
		}

		_capture?.Dispose();
		DxHandler.Shutdown();

		Application.Exit();
	}

	private static void InitializeWebView(string url)
	{
		if (_webViewInitialized) {
			_webView2Client?.Navigate(url);
			return;
		}
		_webViewInitialized = true;
		Thread capturestaThread = new Thread(() =>
		{
			int pid = Process.GetCurrentProcess().Id;
			try
			{
				_capture = new CaptureProcess(pid, _pluginDir, _webView2Client.handle);
				_capture.Start();
			}
			catch { }
		});
		
		capturestaThread.SetApartmentState(ApartmentState.STA);
		
		Thread staThread = new Thread(() => {
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			_webView2Client = new WebView2Client(-1, _rpc, _assemblyDir, _cacheDir, url);
			capturestaThread.Start();
			Application.Run(_webView2Client);
		});
		staThread.SetApartmentState(ApartmentState.STA);
		staThread.Start();
	}

	private static void InitializeIpc(string channelName)
	{
		_rpc = new RendererRpc(channelName);
		_rpc.Navigate += RpcOnNavigate;
		_rpc.Zoom += RpcOnZoom;
	}

	private static void RpcOnZoom(ZoomMessage msg)
	{
		lock (_lockIpc)
		{
			if (_isShuttingDown)
				return;


			if (_capture != null)
			{
				_webView2Client?.ToggleResize();
			}
		}
	}

	private static void RpcOnNavigate(NavigateMessage msg)
	{
		lock (_lockIpc)
		{
			if (_isShuttingDown)
				return;
			if (msg.Url.Equals("kill"))
			{
				_webViewInitialized = false;
				_webView2Client?.RemoveWindow();
			}
			else
			{
				InitializeWebView(msg.Url);
			}
		}
	}

	private static void WatchParentStatus(object? pid)
	{
		Console.WriteLine($"Watching parent PID {pid}");
		Process process = Process.GetProcessById((int)(pid ?? 0));
		while (true)
		{
			if (_waitHandle?.WaitOne(10) == true)
			{
				return;
			}

			if (process.HasExited)
			{
				Process self = Process.GetCurrentProcess();
				self.WaitForExit(1000);
				try { self.Kill(); }
				catch (InvalidOperationException) { }
			}
		}
	}
}