using System.Runtime.InteropServices;
using SharpDX.Direct3D11;

namespace AlphaChannel
{
    public class MpvRenderer
    {
        private static string? _pluginDir;
        private static Plugin? _pluginInstance;
        public static void Setup(Plugin plugin)
        {
            _pluginInstance = plugin;
            NativeLibrary.SetDllImportResolver(typeof(MpvRenderer).Assembly, (name, assembly, path) =>
            {
                if (name == "libmpv-2")
                {
                    if (NativeLibrary.TryLoad(_pluginInstance.AssemblyLocationMPV, out var handle))
                    {
                        return handle;
                    }
                    else
                    {
                        Services.Log.Error($"Failed to load libmpv from path: {_pluginInstance.AssemblyLocationMPV}");
                        return IntPtr.Zero;
                    }
                }
                Services.Log.Error($"Failed to resolve native library: {name}");
                return IntPtr.Zero;
            });
        }
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_create();
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] static extern int mpv_initialize(IntPtr ctx);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] static extern int mpv_set_option_string(IntPtr ctx, string name, string data);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] static extern int mpv_command(IntPtr ctx, string[] args);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] static extern int mpv_render_context_create(ref IntPtr res, IntPtr ctx, IntPtr parms);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] static extern int mpv_render_context_render(IntPtr ctx, IntPtr parms);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] static extern void mpv_render_context_free(IntPtr ctx);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] static extern void mpv_render_context_set_update_callback(IntPtr ctx, MpvRenderUpdateFn callback, IntPtr callback_ctx);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] static extern ulong mpv_render_context_update(IntPtr ctx);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] static extern int mpv_request_log_messages(IntPtr ctx, string min_level);
        [DllImport("libmpv-2", CallingConvention = CallingConvention.Cdecl)] static extern void mpv_terminate_destroy(IntPtr ctx);

        [StructLayout(LayoutKind.Sequential)]
        struct MpvRenderParam { public int Type; public IntPtr Data; }

        public delegate void MpvRenderUpdateFn(IntPtr callback_ctx);

        IntPtr _mpvCtx;
        IntPtr _mpvRenderCtx;
        IntPtr _bufferPtr;
        int _width, _height;
        IntPtr _renderParamsPtr;
        IntPtr _sizePtr, _stridePtr, _formatPtr;
        Texture2D? _targetTexture;
        ManualResetEventSlim _frameReady = new ManualResetEventSlim(false);
        MpvRenderUpdateFn? _updateCallback;
        private bool _stopping = false;

        public void Initialize(int width, int height, string url, Texture2D targetTexture)
        {
            _width = width;
            _height = height;
            using(SharpDX.DXGI.Resource resource = targetTexture.QueryInterface<SharpDX.DXGI.Resource>())
            {
                _targetTexture = DxHandler.DrawDevice?.OpenSharedResource<Texture2D>(resource.SharedHandle);
            }

            _bufferPtr = Marshal.AllocHGlobal(width * height * 4);

            _mpvCtx = mpv_create();
            mpv_set_option_string(_mpvCtx, "vo", "libmpv");
            mpv_set_option_string(_mpvCtx, "hwdec", "no");
            mpv_set_option_string(_mpvCtx, "profile", "sw-fast");
            mpv_set_option_string(_mpvCtx, "ytdl", "yes");
            mpv_set_option_string(_mpvCtx, "script-opts", $"ytdl_hook-ytdl_path={_pluginInstance?.AssemblyLocationYTDLP}");
            mpv_set_option_string(_mpvCtx, "ytdl-format", $"bestvideo[height<={Plugin._resolutionHeight}][ext=mp4]+bestaudio/best[height<={Plugin._resolutionHeight}]");
            mpv_set_option_string(_mpvCtx, "terminal", "yes");
            mpv_set_option_string(_mpvCtx, "msg-level", "all=trace");
            mpv_request_log_messages(_mpvCtx, "debug");
            mpv_initialize(_mpvCtx);

            var apiTypePtr = Marshal.StringToHGlobalAnsi("sw");

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
            
#pragma warning disable CS8620 // Argument cannot be used for parameter due to differences in the nullability of reference types.
            mpv_command(_mpvCtx, new[] { "loadfile", url, null });
#pragma warning restore CS8620 // Argument cannot be used for parameter due to differences in the nullability of reference types.

            _updateCallback = (ctx) => _frameReady.Set();
            mpv_render_context_set_update_callback(_mpvRenderCtx, _updateCallback, IntPtr.Zero);

        }

        public bool RenderFrame(CancellationToken cancellationToken)
        {
            for(int i = 0; i<100;i++)
            {
                var ev = mpv_wait_event(_mpvCtx, 0);
                int eventId = Marshal.ReadInt32(ev);
                if(eventId != 2 && eventId != 7) 
                {
                    break; // No Event, break out of Loop
                }
                if (eventId == 7) // MPV_EVENT_END_FILE
                {
                    return false;
                }
                else if (eventId == 2) // MPV_EVENT_LOG_MESSAGE
                {
                    IntPtr dataPtr = Marshal.ReadIntPtr(ev + 16);
                    if (dataPtr != IntPtr.Zero && dataPtr.ToInt64() > 65536)
                    {
                        var prefix = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(dataPtr));
                        var level = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(dataPtr + 8));
                        var text = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(dataPtr + 16));
                        Services.Log.Verbose($"[mpv/{prefix}/{level}] {text?.Trim()}");
                    }
                }
            }
            try
            {
                _frameReady.Wait(cancellationToken);
            }
            catch 
            { 
                return false; 
            }
            
            if (_stopping) return false;
            _frameReady.Reset();

            ulong flags = mpv_render_context_update(_mpvRenderCtx);
            if ((flags & 1) == 0) return true;

            try
            {
                int rc = mpv_render_context_render(_mpvRenderCtx, _renderParamsPtr);

                if (rc == 0 && _targetTexture != null)
                {
                    DxHandler.DrawDevice?.ImmediateContext.UpdateSubresource(_targetTexture, 0, null, _bufferPtr, _width * 4, 0);
                    DxHandler.DrawDevice?.ImmediateContext.Flush();
                    return true;
                }
                else
                {
                    Services.Log.Error($"Error rendering frame: RC: {rc} Texture: {_targetTexture}");
                }
            }
            catch (Exception e)
            {
                Services.Log.Error($"Error rendering frame: {e.Message} {e.StackTrace}");
            }
            return false;
        }

        public void StopRender()
        {
            _stopping = true;
            
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

        public void TogglePause()
        {
            if(!_stopping)
                mpv_command(_mpvCtx, new[] { "cycle", "pause", null });
        }
    }
}