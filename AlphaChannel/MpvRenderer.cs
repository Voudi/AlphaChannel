using System.Runtime.InteropServices;
using SharpDX.Direct3D11;

namespace AlphaChannel
{
	public class MpvRenderer : IDisposable
	{
		private static Plugin? _pluginInstance;
		public static void Setup(Plugin plugin)
		{
			_pluginInstance = plugin;
			NativeLibrary.SetDllImportResolver(typeof(MpvRenderer).Assembly, (name, assembly, path) =>
			{
				if (name == "libmpv-2")
				{
					if (_pluginInstance.AssemblyLocationMPV != null && NativeLibrary.TryLoad(_pluginInstance.AssemblyLocationMPV, out nint handle))
					{
						return handle;
					}
					else
					{
						Services.Log.Error($"[MPV] Failed to load libmpv from path: {_pluginInstance.AssemblyLocationMPV}");
						return IntPtr.Zero;
					}
				}
				Services.Log.Error($"[MPV] Failed to resolve native library: {name}");
				return IntPtr.Zero;
			});
		}
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr ctx);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_option_string(IntPtr ctx, string name, string data);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr ctx, string[] args);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_render_context_create(ref IntPtr res, IntPtr ctx, IntPtr parms);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_render_context_render(IntPtr ctx, IntPtr parms);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_render_context_free(IntPtr ctx);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_render_context_set_update_callback(IntPtr ctx, MpvRenderUpdateFn callback, IntPtr callback_ctx);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern ulong mpv_render_context_update(IntPtr ctx);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_request_log_messages(IntPtr ctx, string min_level);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr ctx);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_get_property(IntPtr ctx, string name, int format, out double data);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_get_property(IntPtr ctx, string name, int format, IntPtr data);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_get_property_string(IntPtr ctx, string name);
		[DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_free(IntPtr data);

		[StructLayout(LayoutKind.Sequential)]
		private struct MpvRenderParam { public int Type; public IntPtr Data; }

		public delegate void MpvRenderUpdateFn(IntPtr callback_ctx);

		private IntPtr _mpvCtx;
		private IntPtr _mpvRenderCtx;
		private IntPtr _bufferPtr;
		private int _width, _height;
		private CancellationTokenSource? _cancelToken;
		private IntPtr _renderParamsPtr;
		private IntPtr _sizePtr, _stridePtr, _formatPtr;
		private Texture2D? _targetTexture;
		private ManualResetEventSlim _frameReady = new ManualResetEventSlim(false);
		private MpvRenderUpdateFn? _updateCallback;
		private bool _closed = true;
		private Thread? _eventThread;

		public void Initialize(int width, int height, Texture2D targetTexture, CancellationTokenSource cancelToken)
		{
			_width = width;
			_height = height;
			_cancelToken = cancelToken;
			using (SharpDX.DXGI.Resource resource = targetTexture.QueryInterface<SharpDX.DXGI.Resource>())
			{
				_targetTexture = DxHandler.DrawDevice?.OpenSharedResource<Texture2D>(resource.SharedHandle);
			}

			_bufferPtr = Marshal.AllocHGlobal(width * height * 4);

			_mpvCtx = mpv_create();
			_ = mpv_set_option_string(_mpvCtx, "vo", "libmpv");
			_ = mpv_set_option_string(_mpvCtx, "hwdec", "no");
			_ = mpv_set_option_string(_mpvCtx, "profile", "sw-fast");
			_ = mpv_set_option_string(_mpvCtx, "ytdl", "yes");
			_ = mpv_set_option_string(_mpvCtx, "script-opts", $"ytdl_hook-ytdl_path={_pluginInstance?.AssemblyLocationYTDLP}");
			_ = mpv_set_option_string(_mpvCtx, "ytdl-format", $"bestvideo[height<={Plugin.ResolutionHeight}][ext=mp4]+bestaudio/best[height<={Plugin.ResolutionHeight}]");
			_ = mpv_set_option_string(_mpvCtx, "terminal", "yes");
			_ = mpv_set_option_string(_mpvCtx, "volume", "25");
			_ = mpv_set_option_string(_mpvCtx, "msg-level", "all=warn,ffmpeg=error");
			_ = mpv_set_option_string(_mpvCtx, "ytdl-raw-options", "force-ipv4=");
			_ = mpv_set_option_string(_mpvCtx, "idle", "yes");
			_ = mpv_set_option_string(_mpvCtx, "keep-open", "no");
			_ = mpv_request_log_messages(_mpvCtx, "debug");
			_ = mpv_initialize(_mpvCtx);

			nint apiTypePtr = Marshal.StringToHGlobalAnsi("sw");

			IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvRenderParam>() * 2);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 1, Data = apiTypePtr }, paramsPtr, false);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 0, Data = IntPtr.Zero }, paramsPtr + 16, false);

			int rc = mpv_render_context_create(ref _mpvRenderCtx, _mpvCtx, paramsPtr);

			Marshal.FreeHGlobal(apiTypePtr);
			Marshal.FreeHGlobal(paramsPtr);

			_sizePtr = Marshal.AllocHGlobal(8);
			Marshal.WriteInt32(_sizePtr, _width);
			Marshal.WriteInt32(_sizePtr + 4, _height);

			_stridePtr = Marshal.AllocHGlobal(IntPtr.Size);
			Marshal.WriteIntPtr(_stridePtr, new IntPtr(_width * 4));

			_formatPtr = Marshal.StringToHGlobalAnsi("bgra");

			_renderParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvRenderParam>() * 5);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 17, Data = _sizePtr }, _renderParamsPtr, false);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 18, Data = _formatPtr }, _renderParamsPtr + 16, false);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 19, Data = _stridePtr }, _renderParamsPtr + 32, false);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 20, Data = _bufferPtr }, _renderParamsPtr + 48, false);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 0, Data = IntPtr.Zero }, _renderParamsPtr + 64, false);

			_updateCallback = (ctx) => _frameReady.Set();
			mpv_render_context_set_update_callback(_mpvRenderCtx, _updateCallback, IntPtr.Zero);

			_eventThread = new Thread(EventLoop)
			{
				IsBackground = true,
				Name = "mpv-events"
			};

			_eventThread.Start();

			_closed = false;

			Services.Log.Debug("[MPV] Video Player started");
		}

		public bool RenderFrame()
		{
			try
			{
				_frameReady.Wait();
				_frameReady.Reset();
			}
			catch
			{
				Services.Log.Debug("[MPV] Video Player stopped");
				return false;
			}
			if (_closed || _cancelToken!.Token.IsCancellationRequested)
			{ Services.Log.Debug("[MPV] Video Player stopped"); return false; }
			ulong flags = mpv_render_context_update(_mpvRenderCtx);
			if ((flags & 1) == 0)
			{
				return true;
			}

			try
			{
				int rc = mpv_render_context_render(_mpvRenderCtx, _renderParamsPtr);

				if (_closed || _cancelToken!.Token.IsCancellationRequested || DxHandler.DrawDevice?.ImmediateContext == null)
				{
					return false;
				}

				if (rc == 0 && _targetTexture != null)
				{
					DxHandler.DrawDevice?.ImmediateContext.UpdateSubresource(_targetTexture, 0, null, _bufferPtr, _width * 4, 0);
					DxHandler.DrawDevice?.ImmediateContext.Flush();
					return true;
				}
				else
				{
					Services.Log.Warning($"[MPV] Error rendering frame: RC: {rc} Texture: {_targetTexture}");
				}
			}
			catch (Exception e)
			{
				Services.Log.Warning($"[MPV] Error rendering frame: {e.Message} {e.StackTrace}");
			}
			return false;
		}
		private readonly Lock _mpvLock = new();
		public void StopRender()
		{
			_closed = true;
			_cancelToken!.Cancel();
			Task.Run(() =>
			{
				lock (_mpvLock)
				{
					if (_mpvRenderCtx != IntPtr.Zero)
					{
						mpv_render_context_free(_mpvRenderCtx);
						_mpvRenderCtx = IntPtr.Zero;
					}

					if (_mpvCtx != IntPtr.Zero)
					{
						mpv_terminate_destroy(_mpvCtx);
						_mpvCtx = IntPtr.Zero;
					}

					if (_bufferPtr != IntPtr.Zero)
					{
						Marshal.FreeHGlobal(_bufferPtr);
						_bufferPtr = IntPtr.Zero;
					}

					Marshal.FreeHGlobal(_sizePtr);
					Marshal.FreeHGlobal(_stridePtr);
					Marshal.FreeHGlobal(_formatPtr);
					Marshal.FreeHGlobal(_renderParamsPtr);

					_targetTexture?.Dispose();
				}
			});

			_eventThread?.Join(2000);
		}

		public void Play(string url, double playbackPosition, bool isPlaying)
		{
			if (!_closed)
			{
				Services.Log.Debug("Playing New Video at " + playbackPosition + " | " + isPlaying);
				lock (_mpvLock)
				{
					string startStr = ((int)playbackPosition).ToString(System.Globalization.CultureInfo.InvariantCulture);
					string pauseStr = !isPlaying ? ",pause=yes" : string.Empty;
					_ = mpv_command(_mpvCtx, ["loadfile", url, "replace", "0", $"start={startStr}{pauseStr}", null!]);
				}
			}
		}

		public void Stop()
		{
			if (!_closed)
			{
				lock (_mpvLock)
				{
					_ = mpv_command(_mpvCtx, ["stop", null!]);
				}
			}
		}

		public bool GetPaused()
		{
			if (_closed)
			{
				return true;
			}

			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero)
				{
					return true;
				}

				IntPtr ptr = Marshal.AllocHGlobal(4);
				try
				{
					_ = mpv_get_property(_mpvCtx, "pause", 3, ptr);
					return Marshal.ReadInt32(ptr) == 1;
				}
				finally
				{
					Marshal.FreeHGlobal(ptr);
				}
			}
		}
		public double[] GetProperties()
		{
			if (_closed)
			{
				return [0, 0, 100];
			}

			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero)
				{
					return [0, 0, 100];
				}

				_ = mpv_get_property(_mpvCtx, "time-pos", 5, out double position);
				_ = mpv_get_property(_mpvCtx, "duration", 5, out double duration);
				_ = mpv_get_property(_mpvCtx, "volume", 5, out double volume);
				return [position, duration, volume];
			}
		}

		public void TogglePause()
		{
			if (!_closed)
			{
				lock (_mpvLock)
				{
					_ = mpv_command(_mpvCtx, ["cycle", "pause", null!]);
				}
			}
		}

		public void SetVolume(int volume)
		{
			if (!_closed)
			{
				lock (_mpvLock)
				{
					_ = mpv_command(_mpvCtx, ["set", "volume", volume.ToString(System.Globalization.CultureInfo.InvariantCulture), null!]);
				}
			}
		}

		public void Seek(int seconds)
		{
			if (!_closed)
			{
				lock (_mpvLock)
				{	
					_ = mpv_command(_mpvCtx, ["seek", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute", null!]);
				}
			}
		}

		public string? GetMediaTitle()
		{
			if (_closed)
			{
				return null;
			}

			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero)
				{
					return null;
				}

				IntPtr ptr = mpv_get_property_string(_mpvCtx, "media-title");
				if (ptr != IntPtr.Zero)
				{
					try
					{
						return Marshal.PtrToStringAnsi(ptr);
					}
					finally
					{
						mpv_free(ptr);
					}
				}
				return null;
			}
		}

		public string? GetCurrentUrl()
		{
			if (_closed)
			{
				return null;
			}

			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero)
				{
					return null;
				}

				IntPtr ptr = mpv_get_property_string(_mpvCtx, "path");
				if (ptr == IntPtr.Zero)
				{
					return null;
				}

				try
				{
					return Marshal.PtrToStringAnsi(ptr);
				}
				finally
				{
					mpv_free(ptr);
				}
			}
		}

		public bool IsIdle()
		{
			if (_closed)
			{
				return true;
			}

			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero)
				{
					return true;
				}

				IntPtr ptr = Marshal.AllocHGlobal(4);
				try
				{
					int rc = mpv_get_property(_mpvCtx, "idle-active", 3, ptr);
					if (rc < 0)
					{
						return true;
					}

					return Marshal.ReadInt32(ptr) == 1;
				}
				finally
				{
					Marshal.FreeHGlobal(ptr);
				}
			}
		}

		public void Dispose()
		{
			_frameReady.Dispose();
			GC.SuppressFinalize(this);
		}

		private void EventLoop()
		{
			/*
            Services.Log.Debug("[mpv] event loop started");
            try
            {
                while (!_stopping)
                {
                    // 100ms Timeout, damit wir _stopping regelmäßig prüfen können
                    IntPtr ev = mpv_wait_event(_mpvCtx, 0.1);
                    if (ev == IntPtr.Zero) continue;

                    int eventId = Marshal.ReadInt32(ev);

                    
                    switch (eventId)
                    {
                        
                        case 0: // MPV_EVENT_NONE (Timeout)
                            continue;

                        case 1: // MPV_EVENT_SHUTDOWN
                            Services.Log.Debug("[mpv] SHUTDOWN");
                            return;

                        case 2: // MPV_EVENT_LOG_MESSAGE
                            {
                                IntPtr dataPtr = Marshal.ReadIntPtr(ev + 16);
                                if (dataPtr != IntPtr.Zero && dataPtr.ToInt64() > 65536)
                                {
                                    var prefix = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(dataPtr));
                                    var level  = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(dataPtr + 8));
                                    var text   = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(dataPtr + 16));
                                    Services.Log.Verbose($"[mpv/{prefix}/{level}] {text?.Trim()}");
                                }
                                break;
                            }

                        case 3:  Services.Log.Debug("[mpv] GET_PROPERTY_REPLY"); break;
                        case 4:  Services.Log.Debug("[mpv] SET_PROPERTY_REPLY"); break;
                        case 5:  Services.Log.Debug("[mpv] COMMAND_REPLY");      break;
                        case 6:  Services.Log.Debug("[mpv] START_FILE");         break;
                        
                        case 7: // MPV_EVENT_END_FILE
                            _endOfFile = true;
                            break;
                        
                        case 8:  Services.Log.Debug("[mpv] FILE_LOADED");      break;
                        case 14: Services.Log.Debug("[mpv] CLIENT_MESSAGE");   break;
                        case 15: Services.Log.Debug("[mpv] VIDEO_RECONFIG");   break;
                        case 16: Services.Log.Debug("[mpv] AUDIO_RECONFIG");   break;
                        case 17: Services.Log.Debug("[mpv] SEEK");             break;
                        case 18: Services.Log.Debug("[mpv] PLAYBACK_RESTART"); break;
                        case 19: Services.Log.Debug("[mpv] PROPERTY_CHANGE");  break;
                        case 21: Services.Log.Warning("[mpv] QUEUE_OVERFLOW"); break;
                        case 22: Services.Log.Debug("[mpv] HOOK");             break;

                        default:
                            Services.Log.Debug($"[mpv] Unknown event id={eventId}");
                            break;
                    }
                    *
                    }
                }
            }
            catch (Exception e)
            {
                Services.Log.Error($"[mpv] event loop crashed: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                Services.Log.Debug("[mpv] event loop ended");
            }
            */
		}
	}
}
