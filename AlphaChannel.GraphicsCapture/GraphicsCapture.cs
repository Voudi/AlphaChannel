using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

using SharpDX.Direct3D11;

using System.Drawing;
using SharpDX.DXGI;
using SharpDX.Direct2D1;
using SharpDX.Mathematics.Interop;
using SharpDX;
using SharpDX.Direct3D;
using System.Threading;

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
        private Texture2D _doubleBuffer; //Technically a 1.5-Buffer in our use-case, since CopyResource() is clean

        public GraphicsCapture(IntPtr sharedHandle)
        {
            IsCapturing = false;
			_textureSource = DxHandler.Device?.OpenSharedResource<Texture2D>(sharedHandle);

            Texture2DDescription texture2dDescription = new Texture2DDescription
            {
                Width = 1920,
                Height = 1080,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CpuAccessFlags = CpuAccessFlags.None,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                OptionFlags = ResourceOptionFlags.None
            };
            _doubleBuffer = new Texture2D(DxHandler.Device, texture2dDescription);
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

			if (!TryRecreateFrame()) return true; //Skip this frame for the sake of capture booting up

            try
			{
				using var frame = _captureFramePool?.TryGetNextFrame();

				if (frame == null)
					return true;

				var surfaceDxgiInterfaceAccess = (IDirect3DDxgiInterfaceAccess) frame.Surface;
				var pResource = surfaceDxgiInterfaceAccess.GetInterface(new Guid("dc8e63f3-d12b-4952-b47b-5e45026a862d"));
			
				using var surfaceTexture = new Texture2D(pResource); // shared resource

                ScaleAndCopy(surfaceTexture, _doubleBuffer);
                DxHandler.Device.ImmediateContext.CopyResource(_doubleBuffer, _textureSource);

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

        private void ScaleAndCopy(Texture2D source, Texture2D target)
        {
            var d2dContext = DxHandler.Device2DContext;

            using var targetSurface = target.QueryInterface<Surface>();
            using var sourceSurface = source.QueryInterface<Surface>();

            var sourceBitmapProperties = new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Premultiplied),
                96, 96,
                BitmapOptions.Target);

            using var targetBitmap = new Bitmap1(d2dContext, targetSurface, sourceBitmapProperties);
            using var sourceBitmap = new Bitmap1(d2dContext, sourceSurface, sourceBitmapProperties);

            d2dContext.Target = targetBitmap;

            d2dContext.BeginDraw();

            var destRect = new RawRectangleF(0, 0, target.Description.Width, target.Description.Height);
            d2dContext.DrawBitmap(sourceBitmap, destRect, 1.0f, BitmapInterpolationMode.Linear);

            var result = d2dContext.TryEndDraw(out _, out _);
            d2dContext.Target = null;

            if (result.Failure)
                throw new SharpDXException(result, $"EndDraw failed: 0x{result.Code:X8}");
        }
    }
}