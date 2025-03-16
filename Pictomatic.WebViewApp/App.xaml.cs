using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Web.WebView2.Core;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Composition;
using Windows.UI.Core;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Pictomatic.WebViewApp
{
	/// <summary>
	/// Provides application-specific behavior to supplement the default Application class.
	/// </summary>
	public partial class App : Application
	{
		public Texture2D _capturedTexture;
		private WebView2 _webView;
		private CoreWebView2CompositionController _controller; //Unused for now, needed for capture
		private SharpDX.Direct3D11.Device _d3dDevice;

		/// <summary>
		/// Initializes the singleton application object.  This is the first line of authored code
		/// executed, and as such is the logical equivalent of main() or WinMain().
		/// </summary>
		public App()
		{
			this.InitializeComponent();
		}

		/// <summary>
		/// Invoked when the application is launched.
		/// </summary>
		/// <param name="args">Details about the launch request and process.</param>
		protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
		{
			try
			{
				// Create a new window for the application
				var window = new Window();

				// Create the WebView2 control
				var webView2 = new WebView2();

				var textureDesc = new Texture2DDescription
				{
					Width = 1920,
					Height = 1080,
					MipLevels = 1,
					ArraySize = 1,
					Format = Format.B8G8R8A8_UNorm,
					SampleDescription = new SampleDescription(1, 0),
					Usage = ResourceUsage.Default,
					BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
					CpuAccessFlags = CpuAccessFlags.None,
					OptionFlags = ResourceOptionFlags.Shared
				};

				/*
				// Initialize Direct3D device and texture
				_d3dDevice = DxHandler.Device;

				_capturedTexture = new Texture2D(_d3dDevice, textureDesc);
				*/
				var hWnd = WindowNative.GetWindowHandle(window);


				var environment = await CoreWebView2Environment.CreateAsync();

				/*
				var controller = await environment.CreateCoreWebView2CompositionControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hWnd));
				controller.Bounds = new Rect(0, 0, _capturedTexture.Description.Width, _capturedTexture.Description.Height);
				*/
				var compositor = new Compositor();
				var rootVisual = compositor.CreateContainerVisual();
				//controller.RootVisualTarget = rootVisual;


				await webView2.EnsureCoreWebView2Async(); //TODO CHANGE ENVIRONMENT TO THE TEXTURE: await webView2.EnsureCoreWebView2Async(environment);



				// Set WebView2 as the content of the window
				window.Content = webView2;

				// Activate the window (display it)
				window.Activate();




				webView2.Source = new Uri("https://www.google.com");
			}catch(Exception e)
			{
				System.IO.File.AppendAllText("C:/Users/Voudi/Desktop/Pictomatic/Pictomatic.WebViewApp/log.txt", e.Message+"\N");
			}
			
		}
	}
}
