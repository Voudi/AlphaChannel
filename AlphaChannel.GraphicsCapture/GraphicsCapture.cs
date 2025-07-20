using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

using SharpDX.Direct3D11;

using System.Drawing;

namespace AlphaChannel.GraphicsCapture
{
    public class GraphicsCapture
    {
        private Direct3D11CaptureFramePool _captureFramePool;
        private GraphicsCaptureItem _captureItem;
        private GraphicsCaptureSession _captureSession;
		private IDirect3DDevice _device;
		private Point _itemSize;
		private Texture2D _textureSource;

		public GraphicsCapture(IntPtr sharedHandle)
        {
            IsCapturing = false;
			_textureSource = DxHandler.Device?.OpenSharedResource<Texture2D>(sharedHandle);
		}

        public bool IsCapturing { get; private set; }

        public void Dispose()
        {
            StopCapture();
        }

        public void StartCapture(IntPtr cHandle)
        {
			var captureHandle = cHandle;

			if (captureHandle == IntPtr.Zero)
                return;

            _captureItem = CreateItemForWindow(captureHandle);

            if (_captureItem == null)
                return;

			_captureItem.Closed += CaptureItemOnClosed;

			var dxgiDevice = DxHandler.Device.QueryInterface<SharpDX.DXGI.Device>();

			var hr = NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var pUnknown);
            if (hr != 0)
            {
                StopCapture();
                return;
            }

			_device = (IDirect3DDevice) Marshal.GetObjectForIUnknown(pUnknown);
            Marshal.Release(pUnknown);

			_itemSize = new Point(_captureItem.Size.Width, _captureItem.Size.Height);
			
			_captureFramePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _captureItem.Size);

			_captureSession = _captureFramePool.CreateCaptureSession(_captureItem);

            _captureSession.IsCursorCaptureEnabled = false;

            _captureFramePool.FrameArrived += (pool, e) => Program.Set();

			_captureSession.StartCapture();

			IsCapturing = true;
		}
		
		public bool TryRecreateFrame()
		{
			try
			{
				if (_itemSize.X != _captureItem.Size.Width || _itemSize.Y != _captureItem.Size.Height)
				{
					_captureSession?.Dispose();
					_captureFramePool?.Dispose();
					_itemSize = new Point(_captureItem.Size.Width, _captureItem.Size.Height);
					_captureFramePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _captureItem.Size);
					_captureSession = _captureFramePool.CreateCaptureSession(_captureItem);
                    _captureSession.IsCursorCaptureEnabled = false;
                    _captureFramePool.FrameArrived += (pool, e) => Program.Set();
					_captureSession.StartCapture();
				}
			}
			catch(ArgumentException)
			{
				return false;
			}
			return true;
		}

        public bool PollFrame()
        {
			if(_captureItem == null)
			{
				StopCapture();
				return false;
			}

			if (!TryRecreateFrame()) return false;

			try
			{
				using var frame = _captureFramePool?.TryGetNextFrame();

				if (frame == null)
					return true;

				var surfaceDxgiInterfaceAccess = (IDirect3DDxgiInterfaceAccess) frame.Surface;
				var pResource = surfaceDxgiInterfaceAccess.GetInterface(new Guid("dc8e63f3-d12b-4952-b47b-5e45026a862d"));
			
				using var surfaceTexture = new Texture2D(pResource); // shared resource

				var copyRegion = new ResourceRegion(
					0,  // X position in source texture
					0,  // Y position in source texture
					0,  // Z position (for 3D textures)
					Math.Min(surfaceTexture.Description.Width, 1920), // X + width of crop region
					Math.Min(surfaceTexture.Description.Height, 1080),// Y + height of crop region
					1   // Depth (for 3D textures)
				);

				DxHandler.Device.ImmediateContext.CopySubresourceRegion(surfaceTexture, 0, copyRegion, _textureSource, 0, 0, 0, 0);
				return true;
			}
			catch (Exception ex)
			{
				Console.Out.WriteLine("Stopping Capture, reason: " + ex.Message);
				StopCapture();
				return false;
			}
		}

        public void StopCapture() // ...or release resources
        {
            _captureSession?.Dispose();
            _captureFramePool?.Dispose();
            _captureSession = null;
            _captureFramePool = null;
            _captureItem = null;
            IsCapturing = false;
        }

        // ReSharper disable once SuspiciousTypeConversion.Global
        private static GraphicsCaptureItem CreateItemForWindow(IntPtr hWnd)
        {
            var factory = WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem));
            var interop = (IGraphicsCaptureItemInterop) factory;
            var pointer = interop.CreateForWindow(hWnd, typeof(GraphicsCaptureItem).GetInterface("IGraphicsCaptureItem").GUID);
            var capture = Marshal.GetObjectForIUnknown(pointer) as GraphicsCaptureItem;
            Marshal.Release(pointer);

            return capture;
        }

        private void CaptureItemOnClosed(GraphicsCaptureItem sender, object args)
        {
			StopCapture();
        }
    }
}