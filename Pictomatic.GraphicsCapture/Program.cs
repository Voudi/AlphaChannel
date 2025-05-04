using System;
using System.Diagnostics;
using System.Threading;

namespace Pictomatic.GraphicsCapture
{
    public class Program
    {
		private static Thread _parentWatchThread;
		private static EventWaitHandle _waitHandle;
		private static GraphicsCapture _capture;
		public static EventWaitHandle _frameWaitHandle;

		public static void Set()
		{
			_frameWaitHandle?.Set();
		}

		[STAThread]
        public static void Main(string[] rawArgs)
        {
			if (!long.TryParse(rawArgs[0], System.Globalization.NumberStyles.HexNumber, null, out var luid) || !long.TryParse(rawArgs[1], System.Globalization.NumberStyles.HexNumber, null, out var wHandle) || !Int32.TryParse(rawArgs[2], out var pid) || !ulong.TryParse(rawArgs[4], out var tHandle))
			{
				Console.Out.WriteLine("CANNOT CAPTURE WINDOW, TERMINATING!");
				return;
			}

			DxHandler.Initialise(luid);

			var thandlePtr = new IntPtr((long)tHandle);
			_capture = new GraphicsCapture(thandlePtr);

			_capture.StartCapture((IntPtr)wHandle);

			_waitHandle = new EventWaitHandle(false, EventResetMode.ManualReset, rawArgs[3]);
			_frameWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, "pictomaticFrameWaitHandle");

			// Boot up a thread to make sure we shut down if parent dies
			_parentWatchThread = new Thread(WatchParentStatus);
			_parentWatchThread.Start(pid);

			while (true)
			{
				if (_frameWaitHandle.WaitOne(500))
				{
					if (!_capture.PollFrame()) return;
				}
				if (_waitHandle?.WaitOne(0) == true)
				{
					return;
				}
			}
		}
		private static void WatchParentStatus(object pid)
		{
			if(pid == null)
			{
				return;
			}
			Process process = Process.GetProcessById((int)(pid ?? 0));
			while (true)
			{
				if (_waitHandle?.WaitOne(8) == true)
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
}
