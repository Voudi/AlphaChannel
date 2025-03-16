using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
namespace Pictomatic.WebViewForms;

public partial class WebViewForm : Form
{
	public Texture2D _capturedTexture;
	private WebView2 _webView;
	private CoreWebView2CompositionController _controller; //Unused for now, needed for capture
	private SharpDX.Direct3D11.Device _d3dDevice;

	public WebViewForm()
	{

		// Create a SharpDX Direct3D11 Device
		var device = new SharpDX.Direct3D11.Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);

		// Initialize Direct3D device and texture
		_d3dDevice = device; //DxHandler.Device;

		var textureDesc = new Texture2DDescription
		{
			Width = 1920,
			Height = 1080,
			MipLevels = 1,
			ArraySize = 1,
			Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
			SampleDescription = new SampleDescription(1, 0),
			Usage = ResourceUsage.Default,
			BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
			CpuAccessFlags = CpuAccessFlags.None,
			OptionFlags = ResourceOptionFlags.Shared
		};

		_capturedTexture = new Texture2D(_d3dDevice, textureDesc);

		Width = 500;
		Height = 500;
		Show();
		Update();
		Console.WriteLine("Capturing...");
		if (this.InvokeRequired)
		{
			this.Invoke(new Action(() =>
			{
				CreateWebView2ContentAsync();
			}));
		}
		else
		{
			CreateWebView2ContentAsync();
		}
		Nagivate();
	}

	private async void CreateWebView2ContentAsync()
	{
		_webView = new WebView2();
		_webView.Dock = DockStyle.Fill;
		Controls.Add(_webView);
		
		var environment = await CoreWebView2Environment.CreateAsync();
		
		_controller = await environment.CreateCoreWebView2CompositionControllerAsync(Handle);

		if (_controller != null)
		{
			_controller.Bounds = new Rectangle(0, 0, _capturedTexture.Description.Width, _capturedTexture.Description.Height);
			_controller.DefaultBackgroundColor = System.Drawing.Color.Red;
			// Assuming _controller.RootVisualTarget is set up correctly

			
			var compositor = new Compositor();
			var rootVisual = compositor.CreateContainerVisual();
			_controller.RootVisualTarget = rootVisual;
			InitializeDirect3DAndFramePool(GraphicsCaptureItem.CreateFromVisual(rootVisual));
			
		}
		else
		{
			Console.WriteLine("Controller is null...");
		}
		
		// Ensure WebView2 runtime is installed and initialize the WebView
		await _webView.EnsureCoreWebView2Async(null); //TODO CHANGE ENVIRONMENT TO TEXTURE
	}
	
	private Direct3D11CaptureFramePool _framePool;
	private GraphicsCaptureSession _captureSession;

	private void InitializeDirect3DAndFramePool(GraphicsCaptureItem captureItem)
	{
		/*
		_framePool = Direct3D11CaptureFramePool.Create(
			device,
			DirectXPixelFormat.B8G8R8A8UIntNormalized,
			1,  // Number of buffers
			captureItem.Size);
		
		_captureSession = _framePool.CreateCaptureSession(captureItem);
		_captureSession.StartCapture();
		*/
	}

	public void CopyCapturedFrameToSharpDXTexture(Direct3D11CaptureFrame captureFrame)
	{
		// Get the ID3D11Texture2D from the captured frame (this is a shared texture)
		var surface = captureFrame.Surface;  // Assuming Surface is of type IDirect3DSurface

		if (surface == null)
		{
			throw new InvalidOperationException("Captured frame does not contain a valid surface.");
		}

		// Create a context for the D3D11 device to perform the copy operation
		using (var context = _d3dDevice.ImmediateContext)
		{
			// Perform the texture copy
			//context.CopyResource(captureFrameTexture, _capturedTexture);
		}

		// The sharedTexture now contains the content of the captured frame
	}
	
	private void Nagivate()
	{
		//_controller.DefaultBackgroundColor = System.Drawing.Color.Red;
		// Lade eine Webseite
		_controller?.CoreWebView2.Navigate("https://www.google.com");
		_webView.Source = new Uri("https://www.google.com");
		Console.WriteLine("Navigating to URL...");
	}
}