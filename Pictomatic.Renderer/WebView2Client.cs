using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;
using WebView2 = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Pictomatic.Renderer;

public partial class WebView2Client : Form
{
	private WebView2 _webView;
	private TableLayoutPanel _tableLayoutPanel;
	private int _resolution;
	//private GraphicsCapture _capture;
	public IntPtr handle;
	public uint processId;
	private Common.RendererRpc _rpc;
	private int _classicWidth, _classicHeight, _classicLeft, _classicTop;
	private string _adBlockDir;
	private string _cacheDir;
	private string _initUrl;

	protected override bool ShowWithoutActivation
	{
		get { return true; }
	}

	public WebView2Client(int res, Common.RendererRpc rpc, string adBlockDir, string cacheDir, string initUrl)
	{
		_initUrl = initUrl;
		_rpc = rpc;
		_adBlockDir = adBlockDir;
		_cacheDir = cacheDir;
		Text = "PictomaticWebView2";
		StartPosition = FormStartPosition.Manual;
		FormBorderStyle = FormBorderStyle.None;
		AllowTransparency = false;
		ShowInTaskbar = false;
		TopMost = false;
		Enabled = false;
		this.FormClosing += ToggleFormClosing;
		Width = CalculateResolution(res);
		_classicWidth = Width;
		Height = (1080 / (1920 / Width) );
		_classicHeight = Height;
		Location = new Point(GetRightMostCoord().X - 1, GetRightMostCoord().Y - 1);
		_classicLeft = Location.X;
		_classicTop = Location.Y;
		//SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

		_webView = new WebView2()
		{
			Dock = DockStyle.Fill
		};

		Init();
	}

	private Point GetRightMostCoord()
	{
		var left = 0;
		var top = 0;
		foreach (Screen screen in Screen.AllScreens)
		{
			var screenX = screen.Bounds.X;
			var screenY = screen.Bounds.Y;
			var screenWidth = screen.Bounds.Width;
			var screenHeight = screen.Bounds.Height;

			using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
			{
				float dpiX = g.DpiX; // Get DPI scaling for X
				float dpiY = g.DpiY; // Get DPI scaling for Y

				float scaleFactorX = dpiX / 96f; // 96 DPI is standard 100%
				float scaleFactorY = dpiY / 96f;

				screenWidth = (int)(screenWidth / scaleFactorX);
				screenHeight = (int)(screenHeight / scaleFactorY);
			}

			if (screenX + screenWidth > left)
			{
				left = screenX + screenWidth;
				top = screenY + screenHeight;
			}
			else if (screenX + screenWidth == left && screenY + screenHeight > top)
			{
				top = screenY + screenHeight;
			}
		}

		return new Point(left, top);
	}
	private int CalculateResolution(int res)
	{
		var maxWidth = 480; //Start at smallest width 360p
		foreach (Screen screen in Screen.AllScreens)
		{
			maxWidth = screen.Bounds.Width > maxWidth ? Math.Min(screen.Bounds.Width, 1920) : maxWidth;
		}
		if (maxWidth >= 480 && maxWidth < 960)
			maxWidth = 480;
		if (maxWidth >= 960 && maxWidth < 1920)
			maxWidth = 960;
		if (maxWidth >= 1920)
			maxWidth = 1920;
		return res == -1 ? maxWidth : res;
		
		
	}

	private async void Init()
	{
		var options = new CoreWebView2EnvironmentOptions()
		{
			AreBrowserExtensionsEnabled = true
		};
		var environment = CoreWebView2Environment.CreateAsync(null, _cacheDir, options).Result;
		
		Controls.Add(_webView);
		//_tableLayoutPanel.Controls.Add(_webView, 0, 0);
		//Controls.Add(_tableLayoutPanel);
		_webView.CoreWebView2InitializationCompleted += WebViewCoreWebView2InitializationCompleted;
		this.handle = Handle;
		KeepOnTop();
		StartTopMostEnforcer();
		await _webView.EnsureCoreWebView2Async(environment);
	}

	private void WebViewCoreWebView2InitializationCompleted(object sender, EventArgs e)
	{
		_webView.Invoke(async () =>
		{

			
			try {
				await _webView.CoreWebView2.Profile.AddBrowserExtensionAsync(_adBlockDir + "\\uBlock0.chromium");
			}
			catch (Exception ex) {
				Console.Out.WriteLine(ex.ToString());
			}

			processId = _webView.CoreWebView2.BrowserProcessId;

			_ = _rpc.AddSubProcess((int)processId);

			_webView.Source = new Uri(_initUrl);
		});
		
		//_webView.CoreWebView2.OpenDevToolsWindow();
	}

	internal void ShutDown()
	{
		_webView.Invoke(async () =>
		{
			_webView.Dispose();
		});
	}

	private bool _resized = false;
	internal void ToggleResize()
	{
		if (!_resized)
		{
			FormBorderStyle = FormBorderStyle.SizableToolWindow;
			TopMost = true;
			Enabled = true;
			Width = (int)(_classicWidth / 1.5);
			Height = (int)(_classicHeight / 1.5);
			Location = new Point(Cursor.Position.X-_classicWidth/4, Cursor.Position.Y);
		}
		else
		{
			FormBorderStyle = FormBorderStyle.None;
			Location = new Point(_classicLeft, _classicTop);
			TopMost = false;
			Enabled = false;
			Width = _classicWidth;
			Height = _classicHeight;
		}
		
		_resized = !_resized;
	}

	protected override void WndProc(ref Message m)
	{
		const int WM_NCLBUTTONDBLCLK = 0x00A3; // Double-click on title bar

		if (m.Msg == WM_NCLBUTTONDBLCLK)
			return; // Ignore the message, stopping maximize

		base.WndProc(ref m);
	}

	private bool CLOSE_FLAG = false;
	private void ToggleFormClosing(object sender, FormClosingEventArgs e)
	{
		if(!CLOSE_FLAG) 
			ToggleResize();
		e.Cancel = !CLOSE_FLAG;
	}

	public void RemoveWindow()
	{
		CLOSE_FLAG = true;
		Close();
	}

	internal void Navigate(string url)
	{
		_webView.Invoke(async () =>
		{
			_webView.Source = new Uri(url);
			Console.WriteLine("Navigating to URL " + url);
		});
	}

	private const int HWND_TOPMOST = -1;
	private const int SWP_NOSIZE = 0x0001;
	private const int SWP_NOMOVE = 0x0002;
	private const int SWP_SHOWWINDOW = 0x0040;

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(
		IntPtr hWnd, IntPtr hWndInsertAfter,
		int X, int Y, int cx, int cy,
		uint uFlags
	);

	private void KeepOnTop()
	{
		SetWindowPos(this.Handle, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0,
			SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
	}

	private System.Windows.Forms.Timer _topMostTimer;

	private void StartTopMostEnforcer()
	{
		_topMostTimer = new System.Windows.Forms.Timer();
		_topMostTimer.Interval = 1000; // Check every second
		_topMostTimer.Tick += (s, e) => KeepOnTop();
		_topMostTimer.Start();
	}
}