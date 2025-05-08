using System;
using System.Diagnostics;

namespace AlphaChannel.Renderer;

internal class CaptureProcess : IDisposable
{
	public event EventHandler? Crashed;

	private readonly string _keepAliveHandleName;
	private readonly string _pluginDir;

	private Process _process;
	private bool _running;
	private IntPtr _wHandle;
	private string _sharedHandle;

	public CaptureProcess(int pid,
		string pluginDir, IntPtr wHandle, string sharedHandle
	)
	{
		_wHandle = wHandle;
		_keepAliveHandleName = $"AlphaChannelCaptureKeepAlive{pid}";
		_pluginDir = pluginDir;
		_sharedHandle = sharedHandle;

		_process = SetupProcess();
	}

	public void Dispose()
	{
		Stop();

		_process.Dispose();
	}

	public void Start()
	{
		if (_running)
		{
			return;
		}

		_process.Start();
		_process.BeginOutputReadLine();
		_process.BeginErrorReadLine();

		_running = true;
	}

	private int _restarting = 0; // This needs to be a numeric type for Interlocked.Exchange

	public void EnsureRenderProcessIsAlive()
	{
		if (!_running || !HasProcessExited())
		{
			return;
		}

		Task.Run(() =>
		{
			if (_hasExited && 0 == Interlocked.Exchange(ref _restarting, 1))
			{
				try
				{
					// process crashed, restart
					Console.WriteLine("ERROR: Capture process crashed - will restart asap");
					_process = SetupProcess();
					_process.Start();
					_process.BeginOutputReadLine();
					_process.BeginErrorReadLine();

					// notify everyone that we have to reinit
					OnProcessCrashed();

					// reset the process exit flag
					_hasExited = false;
				}
				catch (Exception)
				{
					Console.WriteLine("ERROR: Failed to restart capture process");
				}
				finally
				{
					Interlocked.Exchange(ref _restarting, 0);
				}
			}
		});
	}

	public void Stop()
	{
		if (!_running) { return; }

		_running = false;

		// Grab the handle the process is waiting on and open it up
		EventWaitHandle handle = new(false, EventResetMode.ManualReset, _keepAliveHandleName);
		handle.Set();
		handle.Dispose();

		//			Process.Start("taskkill", $"/F /PID {_process.Id}");
		// Give the process a sec to gracefully shut down, then kill it
		_process.WaitForExit(1000);
		try { _process.Kill(); }
		catch (InvalidOperationException) { }
	}

	private bool _hasExited = false;
	private int _checkingExited = 0; // This needs to be a numeric type for Interlocked.Exchange

	private bool HasProcessExited()
	{
		// Process.HasExited can be an expensive call (on some systems?), so it's
		// offloaded to a Task, here. This could be related to Riot's Vanguard
		// kernel anti-cheat. The performance bottleneck occurs in ntdll, so this
		// is difficult to isolate and debug.
		Task.Run(() =>
		{
			if (!_hasExited && 0 == Interlocked.Exchange(ref _checkingExited, 1))
			{
				try
				{
					_hasExited = _process.HasExited;
				}
				catch (Exception e)
				{
					Console.WriteLine("Failed to get process exit status: " + e.Message + e.StackTrace);
				}
				finally
				{
					Interlocked.Exchange(ref _checkingExited, 0);
				}
			}
		});

		return _hasExited;
	}

	private Process SetupProcess()
	{
		string processArgs = DxHandler.AdapterLuid.ToString("X") + " " + _wHandle.ToString("X") + " " + Process.GetCurrentProcess().Id + " " + _keepAliveHandleName + " " + _sharedHandle;

		Process process = new()
		{
			StartInfo = new ProcessStartInfo
			{
				WorkingDirectory = Path.Combine(_pluginDir, "capture"),
				FileName = Path.Combine(_pluginDir, "capture", "AlphaChannel.GraphicsCapture.exe"),
				Arguments = processArgs,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			}
		};
		process.OutputDataReceived += (_, args) =>
		{
			Console.WriteLine($"[Capture]: {args.Data}");
		};
		process.ErrorDataReceived += (_, args) => Console.WriteLine($"[CaptureERROR]: {args.Data}");

		return process;
	}

	private void OnProcessCrashed()
	{
		Crashed?.Invoke(this, EventArgs.Empty);
	}
}