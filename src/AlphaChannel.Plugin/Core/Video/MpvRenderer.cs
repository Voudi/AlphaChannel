using System.Runtime.InteropServices;
using SharpDX.Direct3D11;

namespace AlphaChannel.Plugin.Video
{
	internal class MpvRenderer : IDisposable
	{
		private const string DLL = "libmpv-2";
		private static Resources? _resources;
		public static void Setup(Resources resources)
		{
			_resources = resources;
		}
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr ctx);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_option_string(IntPtr ctx, string name, string data);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr ctx, string[] args);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_render_context_create(ref IntPtr res, IntPtr ctx, IntPtr parms);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_render_context_render(IntPtr ctx, IntPtr parms);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_render_context_free(IntPtr ctx);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_render_context_set_update_callback(IntPtr ctx, MpvRenderUpdateFn callback, IntPtr callback_ctx);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern ulong mpv_render_context_update(IntPtr ctx);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_request_log_messages(IntPtr ctx, string min_level);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr ctx);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_get_property(IntPtr ctx, string name, int format, out double data);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_get_property(IntPtr ctx, string name, int format, IntPtr data);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_get_property_string(IntPtr ctx, string name);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_free(IntPtr data);

		[StructLayout(LayoutKind.Sequential)]
		private struct MpvRenderParam { public int Type; public IntPtr Data; }

		public delegate void MpvRenderUpdateFn(IntPtr callback_ctx);

		private const string RenderKey = "mpv";

		private IntPtr _mpvCtx;
		private IntPtr _mpvRenderCtx;
		private IntPtr _bufferPtr;
		private IntPtr _snapA, _snapB;
		private bool _useSnapA = true;
		private int _frameBytes;
		private int _width, _height;
		private CancellationTokenSource? _cancelToken;
		private IntPtr _renderParamsPtr;
		private IntPtr _sizePtr, _stridePtr, _formatPtr;
		private Texture2D? _targetTexture;
		private ManualResetEventSlim _frameReady = new ManualResetEventSlim(false);
		private MpvRenderUpdateFn? _updateCallback;
		private GCHandle _updateCallbackHandle;
		private bool _closed = true;
		private Thread? _eventThread;
        private float _smoothedAudioLevel;
        //Set by VideoEngine right after construction - the event loop below runs on its own
        //background thread, so this is the only path an async mpv-side failure (a bad yt-dlp
        //resolve, a codec/network error reported well after Play() already returned) has to reach
        //VideoEngine.LastError. Fires from _eventThread, not the caller's own thread.
        internal Action<string>? OnError;

        internal Action? OnFrameRendered;
        internal Action? OnMediaLoaded;

        private readonly Lock _snapshotLock = new();
		private IntPtr _latestSnapshot;

		public void Initialize(int width, int height, Texture2D? targetTexture, CancellationTokenSource cancelToken,
			bool hardwareDecoding = false, int maxQualityHeight = 1080, bool allowInsecureDirectUrls = false,
			int initialVolume = 60, string? cookiesPath = null, bool useFirefoxCookies = false)
		{
			_width = width;
			_height = height;
			_cancelToken = cancelToken;
			_targetTexture = targetTexture;

			_frameBytes = width * height * 4;
			_bufferPtr = Marshal.AllocHGlobal(_frameBytes);
			_snapA = Marshal.AllocHGlobal(_frameBytes);
			_snapB = Marshal.AllocHGlobal(_frameBytes);

			_mpvCtx = mpv_create();
			_ = mpv_set_option_string(_mpvCtx, "vo", "libmpv");
			// Not measured on this project's Wine/RADV setup - mpv has no GPU render path here
			// either way, only decode could benefit. Off is the safe default; read fresh here so a
			// settings change takes effect on the next video, not the current one.
			_ = mpv_set_option_string(_mpvCtx, "hwdec", hardwareDecoding ? "auto-safe" : "no");
			_ = mpv_set_option_string(_mpvCtx, "profile", "sw-fast");
			_ = mpv_set_option_string(_mpvCtx, "ytdl", "yes");
			_ = mpv_set_option_string(_mpvCtx, "script-opts", $"ytdl_hook-ytdl_path={_resources?.GetLocationYTDLP()}");
			_ = mpv_set_option_string(_mpvCtx, "ytdl-format", $"bestvideo[height<={maxQualityHeight}][ext=mp4]+bestaudio/best[height<={maxQualityHeight}]");
			_ = mpv_set_option_string(_mpvCtx, "terminal", "yes");
			_ = mpv_set_option_string(_mpvCtx, "volume", initialVolume.ToString(System.Globalization.CultureInfo.InvariantCulture));
			_ = mpv_set_option_string(_mpvCtx, "msg-level", "all=warn,ffmpeg=error");
			// force-ipv4 used to be set here too, but it only affects yt-dlp's own resolve
			// request - not mpv/ffmpeg's later fetch of the resolved URL, which has no
			// equivalent option. On a dual-stack system that pins yt-dlp to IPv4 while mpv's own
			// fetch still prefers IPv6 by default, so the CDN sees a request from a different IP
			// than the one baked into the signed URL and returns 403 on every single playback.
			// Leaving IP family unforced keeps both sides on the same OS-chosen default instead.
			// YouTube's SABR-only rollout means web/web_safari/mweb/ios/tv_simply now require a
			// GVS PO token yt-dlp doesn't supply out of the box - even when they resolve *a* URL
			// it 403s on first fetch, or the video has no non-PO-token formats at all ("Only
			// images are available"). android is the one client still handing out a working,
			// PO-token-free progressive stream (itag 18, capped ~360p) confirmed against real
			// videos end-to-end (resolve + actual curl fetch), so pin extraction to it.
			var ytdlRawOptions = "hls-use-mpegts=,extractor-args=youtube:player_client=android";
			if (useFirefoxCookies && FindFirefoxProfile() is { } firefoxProfile)
			{
				// Best-effort: reads cookies straight out of a local Firefox profile instead of a
				// manually exported file. Untested against a Flatpak-sandboxed Firefox specifically
				// (non-standard profile path) combined with yt-dlp itself running as a Windows
				// binary under Wine reaching across into that Linux-side profile - if this silently
				// doesn't work, cookiesPath (a manually exported cookies.txt) is the reliable
				// fallback.
				ytdlRawOptions += $",cookies-from-browser=firefox:{firefoxProfile}";
			}
			else if (!string.IsNullOrEmpty(cookiesPath))
			{
				// Lets yt-dlp play age-restricted videos using a real logged-in session - the file
				// itself is never touched by us beyond handing its path to yt-dlp here.
				ytdlRawOptions += $",cookies={cookiesPath}";
			}

			_ = mpv_set_option_string(_mpvCtx, "ytdl-raw-options", ytdlRawOptions);
			_ = mpv_set_option_string(_mpvCtx, "idle", "yes");
			_ = mpv_set_option_string(_mpvCtx, "keep-open", "yes");

            // Live HLS streams such as MediaMTX need mpv to keep
            // refreshing the playlist instead of treating the current
            // playlist window like a short finite media file.
            _ = mpv_set_option_string(
                _mpvCtx,
                "demuxer-lavf-o",
                "live_start_index=-1");

            // Measure the actual decoded audio level.
            //
            // "alphavol" is the filter label used later with
            // af-metadata/alphavol/... to retrieve RMS volume.
            //
            // reset=1 makes astats calculate a fresh value for each
            // incoming audio frame instead of accumulating statistics
            // across the entire track.
            _ = mpv_set_option_string(
                _mpvCtx,
                "af",
                "@alphavol:lavfi=[astats=metadata=1:reset=1:measure_perchannel=none:measure_overall=RMS_level]");

            // Wine's own certificate store is essentially empty by default - only disabling
            // verification worked around it on this project's Wine setup. Never applies on real
            // Windows, and only when the user has explicitly opted in.
            if (WineEnvironment.IsWine && allowInsecureDirectUrls)
			{
				_ = mpv_set_option_string(_mpvCtx, "tls-verify", "no");
			}
			_ = mpv_request_log_messages(_mpvCtx, "warn");
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
			_updateCallbackHandle = GCHandle.Alloc(_updateCallback);
			mpv_render_context_set_update_callback(_mpvRenderCtx, _updateCallback, IntPtr.Zero);

			_eventThread = new Thread(EventLoop)
			{
				IsBackground = true,
				Name = "mpv-events"
			};

			_eventThread.Start();

			_closed = false;

			AepLog.Debug("[MPV] Video Player started");
		}

		// Best-effort discovery of a Flatpak-sandboxed Firefox profile (this environment's actual
		// install location, not the standard ~/.mozilla/firefox yt-dlp's own auto-detect expects) -
		// see the caveats noted where this is called from. Returns a Wine Z:-drive path, since this
		// whole process runs as a Windows binary even though the underlying files are on Linux.
		private static string? FindFirefoxProfile()
		{
			try
			{
				var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				var flatpakRoot = Path.Combine(home, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox");
				if (!Directory.Exists(flatpakRoot))
				{
					return null;
				}

				return Directory.GetDirectories(flatpakRoot, "*.default*").FirstOrDefault();
			}
			catch (Exception exception)
			{
				AepLog.Warning($"[MPV] Failed to locate a Firefox profile: {exception.Message}");
				return null;
			}
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
				AepLog.Debug("[MPV] Video Player stopped");
				return false;
			}
			if (_closed || _cancelToken!.Token.IsCancellationRequested)
			{ AepLog.Debug("[MPV] Video Player stopped"); return false; }

            // Everything below touches state (_mpvRenderCtx, _bufferPtr, _snapA/_snapB,
            // _targetTexture) that StopRender frees/nulls under the same _renderLock.
            // Holding the lock for the whole render+enqueue - and rechecking these
            // fields after acquiring it - prevents a queued UpdateSubresource closure
            // from outliving the buffers/texture it captured: either this runs fully
            // before StopRender (which then cancels the just-queued work before freeing),
            // or StopRender wins the lock first and this bails out on the null/zero check.
            //
            // This is a separate lock from _mpvLock (which guards _mpvCtx command/property
            // calls) on purpose - RenderFrame runs once per mpv frame and holds this for
            // the actual native render+copy, so sharing it with _mpvLock would make every
            // UI-thread property poll (GetProperties, IsEofReached, ...) queue up behind
            // that native call and stall the game's own frame rate.
            lock (_renderLock)
			{
				if (_closed || _mpvRenderCtx == IntPtr.Zero)
				{
					return false;
				}

				ulong flags = mpv_render_context_update(_mpvRenderCtx);
				if ((flags & 1) == 0)
				{
					return true;
				}

				try
				{
					int rc = mpv_render_context_render(_mpvRenderCtx, _renderParamsPtr);

					if (_closed || _cancelToken!.Token.IsCancellationRequested)
					{
						return false;
					}

					if (rc == 0 && _targetTexture != null && _bufferPtr != IntPtr.Zero &&
						_snapA != IntPtr.Zero && _snapB != IntPtr.Zero)
					{
						IntPtr snapshot = _useSnapA ? _snapA : _snapB;
						_useSnapA = !_useSnapA;

						unsafe
						{
							System.Buffer.MemoryCopy((void*)_bufferPtr, (void*)snapshot, _frameBytes, _frameBytes);
						}

						lock (_snapshotLock)
						{
							_latestSnapshot = snapshot;
						}

						Texture2D texture = _targetTexture;
						int width = _width;
                        DxHandler.RunOnRenderThread(RenderKey, () =>
                        {
                            DxHandler.Device?.ImmediateContext.UpdateSubresource(texture, 0, null, snapshot, width * 4, 0);
                        });

                        OnFrameRendered?.Invoke();

                        return true;
                    }
					else
					{
						AepLog.Warning($"[MPV] Error rendering frame: RC: {rc} Texture: {_targetTexture}");
					}
				}
				catch (Exception e)
				{
					AepLog.Warning($"[MPV] Error rendering frame: {e.Message} {e.StackTrace}");
				}
				return false;
			}
		}
		private readonly Lock _mpvLock = new();
		// Guards _mpvRenderCtx/_bufferPtr/_snapA/_snapB/_targetTexture specifically - see the
		// comment in RenderFrame for why this is kept separate from _mpvLock.
		private readonly Lock _renderLock = new();
        public void StopRender()
        {
            _closed = true;

            _cancelToken?.Cancel();

            // Wake RenderFrame if it is currently blocked in _frameReady.Wait().
            try
            {
                _frameReady.Set();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by an earlier cleanup.
            }

            lock (_snapshotLock)
            {
                _latestSnapshot = IntPtr.Zero;
            }

            // This MUST be synchronous.
            //
            // RenderFrame queues UpdateSubresource callbacks while holding
            // _renderLock. Taking the same lock here guarantees that either:
            //
            // 1. RenderFrame finishes queueing first, then we cancel that work; or
            // 2. shutdown gets the lock first, after which RenderFrame sees
            //    _closed / the cleared render context and exits.
            //
            // Most importantly, StopRender cannot return while a queued GPU upload
            // still references the texture or native snapshot buffers.
            lock (_renderLock)
            {
                DxHandler.CancelRenderThreadWork(RenderKey);

                if (_mpvRenderCtx != IntPtr.Zero)
                {
                    mpv_render_context_free(_mpvRenderCtx);
                    _mpvRenderCtx = IntPtr.Zero;
                }

                if (_updateCallbackHandle.IsAllocated)
                {
                    _updateCallbackHandle.Free();
                }

                if (_bufferPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_bufferPtr);
                    _bufferPtr = IntPtr.Zero;
                }

                if (_snapA != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_snapA);
                    _snapA = IntPtr.Zero;
                }

                if (_snapB != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_snapB);
                    _snapB = IntPtr.Zero;
                }

                if (_sizePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_sizePtr);
                    _sizePtr = IntPtr.Zero;
                }

                if (_stridePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_stridePtr);
                    _stridePtr = IntPtr.Zero;
                }

                if (_formatPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_formatPtr);
                    _formatPtr = IntPtr.Zero;
                }

                if (_renderParamsPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_renderParamsPtr);
                    _renderParamsPtr = IntPtr.Zero;
                }

                _targetTexture = null;
            }

            lock (_mpvLock)
            {
                if (_mpvCtx != IntPtr.Zero)
                {
                    mpv_terminate_destroy(_mpvCtx);
                    _mpvCtx = IntPtr.Zero;
                }
            }

            if (_eventThread is not null &&
                _eventThread != Thread.CurrentThread)
            {
                _eventThread.Join(2000);
            }

            _eventThread = null;
        }

        public void Play(string url, double playbackPosition, bool isPlaying)
        {
            if (!_closed)
            {
                _smoothedAudioLevel = 0f;
                AepLog.Debug("Playing New Video at " + playbackPosition + " | " + isPlaying);

                lock (_mpvLock)
                {
                    if (url == string.Empty)
                    {
                        Stop();
                    }
                    else if (playbackPosition > 0)
                    {
                        string startStr = ((int)playbackPosition)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture);

                        string pauseStr = !isPlaying ? ",pause=yes" : string.Empty;

                        _ = mpv_command(
                            _mpvCtx,
                            ["loadfile", url, "replace", "0", $"start={startStr}{pauseStr}", null!]);
                    }
                    else if (!isPlaying)
                    {
                        _ = mpv_command(
                            _mpvCtx,
                            ["loadfile", url, "replace", "0", "pause=yes", null!]);
                    }
                    else
                    {
                        _ = mpv_command(
                            _mpvCtx,
                            ["loadfile", url, "replace", "0", null!]);
                    }
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
					_closed = true;
					_frameReady?.Set();
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
		// A managed copy of the latest decoded frame - for CPU-side consumers (the debug window
		// and the plain screen-window fallback) alongside the GPU texture upload RenderFrame
		// already does. Not on the hot path: only copied when actually asked for.
		public byte[]? TryGetFrame(out int width, out int height)
		{
			width = _width;
			height = _height;
			lock (_snapshotLock)
			{
				if (_latestSnapshot == IntPtr.Zero || _frameBytes == 0)
				{
					return null;
				}

				var frame = new byte[_frameBytes];
				Marshal.Copy(_latestSnapshot, frame, 0, _frameBytes);
				return frame;
			}
		}

		public double[] GetProperties()
		{
			if (_closed)
			{
				return [0, 0, 100, 0, 0];
			}

			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero)
				{
					return [0, 0, 100, 0, 0];
				}

				_ = mpv_get_property(_mpvCtx, "time-pos", 5, out double position);
				_ = mpv_get_property(_mpvCtx, "duration", 5, out double duration);
				_ = mpv_get_property(_mpvCtx, "volume", 5, out double volume);
				// dwidth/dheight are the stream's actual decoded resolution (post yt-dlp format
				// selection), not this renderer's fixed offscreen texture size - what the Player
				// tab shows so players can tell what quality they're actually getting.
				_ = mpv_get_property(_mpvCtx, "dwidth", 5, out double streamWidth);
				_ = mpv_get_property(_mpvCtx, "dheight", 5, out double streamHeight);
				return [position, duration, volume, streamWidth, streamHeight];
			}
		}

		public void Pause(bool pause)
		{
			if (!_closed)
			{
				lock (_mpvLock)
				{
					_ = mpv_command(_mpvCtx, ["set", "pause", pause ? "yes" : "no", null!]);
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
			if (_closed)
			{
				AepLog.Debug($"[MPV] Seek to {seconds}s ignored: player closed");
				return;
			}

			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero)
				{
					AepLog.Debug($"[MPV] Seek to {seconds}s ignored: no mpv context");
					return;
				}

				int rc = mpv_command(_mpvCtx, ["seek", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute", null!]);
				if (rc < 0)
				{
					AepLog.Warning($"[MPV] Seek to {seconds}s failed: rc={rc}");
				}
			}
		}

        public bool HasVideoTrack()
        {
            if (_closed)
            {
                return false;
            }

            lock (_mpvLock)
            {
                if (_mpvCtx == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr ptr =
                    mpv_get_property_string(
                        _mpvCtx,
                        "vid");

                if (ptr == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    var value =
                        Marshal.PtrToStringUTF8(ptr);

                    return !string.IsNullOrWhiteSpace(value) &&
                           !value.Equals(
                               "no",
                               StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    mpv_free(ptr);
                }
            }
        }

        public bool HasAudioTrack()
        {
            if (_closed)
            {
                return false;
            }

            lock (_mpvLock)
            {
                if (_mpvCtx == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr ptr =
                    mpv_get_property_string(
                        _mpvCtx,
                        "aid");

                if (ptr == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    var value =
                        Marshal.PtrToStringUTF8(ptr);

                    return !string.IsNullOrWhiteSpace(value) &&
                           !value.Equals(
                               "no",
                               StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    mpv_free(ptr);
                }
            }
        }

        public float GetAudioLevel()
        {
            if (_closed)
            {
                return 0f;
            }

            lock (_mpvLock)
            {
                if (_mpvCtx == IntPtr.Zero)
                {
                    return 0f;
                }

                IntPtr ptr =
                    mpv_get_property_string(
                        _mpvCtx,
                        "af-metadata/alphavol/by-key/lavfi.astats.Overall.RMS_level");

                if (ptr == IntPtr.Zero)
                {
                    // Metadata may not have arrived yet.
                    // Let the previous value decay naturally instead
                    // of snapping the visualizer instantly to zero.
                    _smoothedAudioLevel *= 0.88f;

                    return _smoothedAudioLevel;
                }

                try
                {
                    string? value =
                        Marshal.PtrToStringUTF8(ptr);

                    if (string.IsNullOrWhiteSpace(value) ||
                        value.Equals(
                            "-inf",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _smoothedAudioLevel *= 0.82f;

                        return _smoothedAudioLevel;
                    }

                    if (!double.TryParse(
                            value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double db))
                    {
                        return _smoothedAudioLevel;
                    }

                    //
                    // astats returns RMS in dB.
                    //
                    // For the visualizer:
                    //
                    // -60 dB = effectively silent
                    //   0 dB = maximum
                    //
                    float target =
                        (float)Math.Clamp(
                            (db + 60.0) / 60.0,
                            0.0,
                            1.0);

                    //
                    // Fast attack, slower release.
                    //
                    // Loud transients should make the bars jump quickly,
                    // while drops should fall smoothly rather than flicker.
                    //
                    float smoothing =
                        target > _smoothedAudioLevel
                            ? 0.45f
                            : 0.12f;

                    _smoothedAudioLevel +=
                        (target - _smoothedAudioLevel) *
                        smoothing;

                    return _smoothedAudioLevel;
                }
                finally
                {
                    mpv_free(ptr);
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
						return Marshal.PtrToStringUTF8(ptr);
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

		public bool IsEofReached()
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
					int rc = mpv_get_property(_mpvCtx, "eof-reached", 3, ptr);
					if (rc < 0)
					{
						return false;
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
            StopRender();

            try
            {
                _frameReady.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Dispose may be reached more than once during shutdown.
            }

            GC.SuppressFinalize(this);
        }

        private void EventLoop()
		{
			
            AepLog.Verbose("[MPV] event loop started");
            try
            {
                while (!_closed)
                {
                    IntPtr ev = mpv_wait_event(_mpvCtx, 1);
                    if (ev == IntPtr.Zero) {continue;}

                    int eventId = Marshal.ReadInt32(ev);

                    
                    switch (eventId)
                    {
                        
                        case 0: // MPV_EVENT_NONE (Timeout)
                            continue;

                        case 1: // MPV_EVENT_SHUTDOWN
                            AepLog.Verbose("[MPV] SHUTDOWN");
                            return;

                        case 2: // MPV_EVENT_LOG_MESSAGE
                            {
                                IntPtr dataPtr2 = Marshal.ReadIntPtr(ev + 16);
                                if (dataPtr2 != IntPtr.Zero && dataPtr2.ToInt64() > 65536)
                                {
									string? prefix = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(dataPtr2));
									string? level  = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(dataPtr2 + 8));
									string? text   = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(dataPtr2 + 16));
                                    if ((level == "error" ||
     level == "warn") &&
    text != null)
                                    {
										// Every other mpv/ytdl error (bot-check, network stall, format
										// unavailable, ...) used to only reach AepLog.Verbose below, which
										// is filtered out of the visible log by default - a real failure
										// looked identical to "still buffering" from the outside, forever.
										AepLog.Warning($"[MPV/{prefix}/{level}] {text.Trim()}");
										OnError?.Invoke(text.Trim());
									}
                                    AepLog.Verbose($"[MPV/{prefix}/{level}] {text?.Trim()}");
                                }
                                break;
                            }

                        case 3:  AepLog.Verbose("[MPV] GET_PROPERTY_REPLY"); break;
                        case 4:  AepLog.Verbose("[MPV] SET_PROPERTY_REPLY"); break;
                        case 5:  AepLog.Verbose("[MPV] COMMAND_REPLY");      break;
                        case 6:  AepLog.Verbose("[MPV] START_FILE");         break;

                        case 7: // MPV_EVENT_END_FILE
                            AepLog.Warning(
                                "[MPV] END_FILE received.");
                            break;

                        case 8: // MPV_EVENT_FILE_LOADED
                            AepLog.Verbose("[MPV] FILE_LOADED");
                            OnMediaLoaded?.Invoke();
                            break;
                        case 14: AepLog.Verbose("[MPV] CLIENT_MESSAGE");   break;
                        case 15: AepLog.Verbose("[MPV] VIDEO_RECONFIG");   break;
                        case 16: AepLog.Verbose("[MPV] AUDIO_RECONFIG");   break;
                        case 17: AepLog.Verbose("[MPV] SEEK");             break;
                        case 18: AepLog.Verbose("[MPV] PLAYBACK_RESTART"); break;
                        case 19: AepLog.Verbose("[MPV] PROPERTY_CHANGE");  break;
                        case 22: AepLog.Verbose("[MPV] HOOK");             break;

                        default:
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                AepLog.Verbose($"[MPV] event loop crashed: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                AepLog.Verbose("[MPV] event loop ended");
            }
            
		}
	}
}
