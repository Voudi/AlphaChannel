using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Runtime.InteropServices;
using WebView2 = Microsoft.Web.WebView2.WinForms.WebView2;

namespace AlphaChannel.Renderer;

public partial class WebView2Client : Form
{
	private readonly WebView2 _webView;
	public IntPtr handle;
	public ControlWindow _mainWindow;
	private readonly int _classicWidth, _classicHeight, _classicLeft, _classicTop;
	private readonly Dictionary<string, string> _adBlockDirs;
	private readonly string _cacheDir;
	private readonly string _initUrl;
	private bool _coreLoaded = false;

	protected override bool ShowWithoutActivation => true;

	[DllImport("user32.dll", SetLastError = true)]
	public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

	[DllImport("user32.dll")]
	public static extern bool SetForegroundWindow(IntPtr hWnd);

	private IntPtr _gameWindow = IntPtr.Zero;

	public WebView2Client(int res, ControlWindow mainWindow, Dictionary<string, string> adBlockDirs, string cacheDir, string initUrl)
	{
		_mainWindow = mainWindow;
		_initUrl = initUrl;
		_adBlockDirs = adBlockDirs;
		_cacheDir = cacheDir;

		Text = "AlphaChannelWebView2";
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
		_topMostTimer = new System.Windows.Forms.Timer();

		//SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

		_webView = new WebView2()
		{
			Dock = DockStyle.Fill
		};

		_gameWindow = GetGameWindow();

		Init();
	}

	public static IntPtr GetGameWindow()
	{
		var handle = IntPtr.Zero;
		while (true)
		{
			handle = FindWindowEx(IntPtr.Zero, handle, "FFXIVGAME", null);
			if (handle == IntPtr.Zero)
				break; //No more windows

			GetWindowThreadProcessId(handle, out var processId);
			if (processId == Environment.ProcessId) 
				break; //Found Process Window
		}
		return handle;
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

		_webView.CoreWebView2InitializationCompleted += WebViewCoreWebView2InitializationCompleted;
		
		this.handle = Handle;
		KeepOnTop();
		//StartTopMostEnforcer();
		if (_gameWindow != IntPtr.Zero)
			SetForegroundWindow(_gameWindow);
		await _webView.EnsureCoreWebView2Async(environment);
	}

	private void WebViewCoreWebView2InitializationCompleted(object? sender, EventArgs e)
	{
        _ = _webView.Invoke(async () =>
        {
            try
            {
                var extensions = await _webView.CoreWebView2.Profile.GetBrowserExtensionsAsync();
                foreach (var adblock in _adBlockDirs)
                {
                    var installed = false;
                    foreach (var extension in extensions)
                    {

                        if (extension.IsEnabled && extension.Name.Contains(adblock.Key))
                        {
                            installed = true;
                            Services.Log.Debug("Found browser extension: " + extension.Name + " | " + extension.IsEnabled);
                        }
                        else if (extension.Name.Contains(adblock.Key))
                        {
                            installed = true;
                            await extension.EnableAsync(true);
                            Services.Log.Debug("Enabling browser extension: " + extension.Name + " | " + extension.IsEnabled);
                        }
                    }
                    if (!installed)
                    {
                        Services.Log.Debug("Installing browser extension: " + adblock.Key + " | " + adblock.Value);
                        await _webView.CoreWebView2.Profile.AddBrowserExtensionAsync(adblock.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine(ex.ToString());
            }

            var processId = _webView?.CoreWebView2.BrowserProcessId;
            if (processId.HasValue)
            {
                _mainWindow?.AddSubProcess(processId.Value);
            }

            _webView.CoreWebView2.DOMContentLoaded += TryFullscreenAndPlay;

            if (_webView != null)
            {
                await Task.Delay(2000); //Wait two seconds for adblock to boot up - no other indicator of booting up exists
                _webView.Source = new Uri(_initUrl);
            }

            _coreLoaded = true;

        });
		
		//_webView.CoreWebView2.OpenDevToolsWindow();
	}

    internal void ShutDown()
	{
		_coreLoaded = false;
		_webView.Dispose();
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

	private bool _close_flag = false;
	private void ToggleFormClosing(object? sender, FormClosingEventArgs e)
	{
		if(!_close_flag) 
			ToggleResize();
		e.Cancel = !_close_flag;
	}

	public void RemoveWindow()
	{
		_close_flag = true;
		Close();
		ShutDown();
	}

	internal void Navigate(string url)
	{
		_webView.Invoke(() =>
		{
			_webView.Source = new Uri(url);
			Console.WriteLine("Navigating to URL " + url);
			return Task.CompletedTask;
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

	private readonly System.Windows.Forms.Timer _topMostTimer;

    public void TryPlay()
    {
        string scriptPlay = @"(function() {
								var title = document.querySelector(""title"").textContent;
								// OpenTogether Tube
								if(title.includes(""OpenTogetherTube"")) {
									var playButton = document.querySelector(
										""button[aria-label='Play/Pause'] > span[class='v-btn__content'] > i[class~='mdi-play']""
									);
									if (playButton !== null) {
										playButton.click();
									}

									var url = document.querySelector(""div[class='player'] > iframe"").src;
									document.querySelector(""div[class='player'] > iframe"").src = url.replace(""autoplay=0"", ""autoplay=1"");
								// Hyperbeam
								} else if (title.includes(""Hyperbeam"")) {
									// DO NOTHING
								}
								// Default
								else {
									//SCRIPTDEFAULT
									var video = document.querySelector('video');
									if (video) {
										video.play();
									}
								}
							})();
		";

        _webView.Invoke(async () =>
        {
            if (_coreLoaded)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync(scriptPlay); // Execute the JavaScript in the WebView2 control
            }
        });

    }

    public void TryFullscreen()
    {
        string scriptFullscreen = @"(function() {
								var title = document.querySelector(""title"").textContent;
								// OpenTogether Tube
								if(title.includes(""OpenTogetherTube"")) {
									document.querySelector(""i[class='mdi-fullscreen-exit mdi v-icon notranslate v-theme--dark v-icon--size-default']"")
										.click();
								// Hyperbeam
								} else if (title.includes(""Hyperbeam"")) {
										document.querySelector(""span[class*='layoutBtns']>button:last-child"").click();
									setTimeout(() => {
										document.querySelector(""div[class*='chatContainer']"").setAttribute(""style"", ""display:none;"");
									}, 500);
								}
								// Default
								else {
									//SCRIPTDEFAULT
									var video = document.querySelector('video');
									if (video) {
										if (video.requestFullscreen) {
											video.requestFullscreen();
										}
									}
								}
							})();
		";

        _webView.Invoke(async () =>
        {
            if (_coreLoaded)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync(scriptFullscreen); // Execute the JavaScript in the WebView2 control
            }
        });

    }

    private void TryFullscreenAndPlay(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
	{
        string scriptBoth = @"(function() {
							var title = document.querySelector(""title"").textContent;
							// OpenTogether Tube
							if(title.includes(""OpenTogetherTube"")) {
								document.querySelector(""i[class='mdi-fullscreen-exit mdi v-icon notranslate v-theme--dark v-icon--size-default']"")
									.click();

								var playButton = document.querySelector(
									""button[aria-label='Play/Pause'] > span[class='v-btn__content'] > i[class~='mdi-play']""
								);
								if (playButton !== null) {
									playButton.click();
								}

								var url = document.querySelector(""div[class='player'] > iframe"").src;
								document.querySelector(""div[class='player'] > iframe"").src = url.replace(""autoplay=0"", ""autoplay=1"");

								const style = document.createElement('style');
								style.innerHTML = "".fullscreen .video-container .video-subcontainer .video-controls-wrapper { display: none; }"";
								document.head.appendChild(style);
							// Hyperbeam
							} else if (title.includes(""Hyperbeam"")) {
								document.querySelector(""div[class='p-checkbox-box']"").click();
								setTimeout(() => document.querySelector(""button[class~='p-button']"").click(), 1000);
								setTimeout(() =>  {
									Array.from(document.querySelectorAll(""span[class='tu-btn-content']"")).find(btn => btn.textContent.toUpperCase().includes(""SKIP"")).click();
									document.querySelector(""span[class*='layoutBtns']>button:last-child"").click();
								}, 1500);

								setTimeout(() => {
									document.querySelector(""div[class*='chatContainer']"").setAttribute(""style"", ""display:none;"");
								}, 2000);
							}
							// Default
							else {
								//SCRIPTDEFAULT
								var video = document.querySelector('video');
								if (video) {
									video.play();
									if (video.requestFullscreen) {
										video.requestFullscreen();
									}
								}
							}
						})();
		";

        _webView.Invoke(async () =>
		{
			if(_coreLoaded)
			{
                await Task.Delay(1000); //Wait one second for elements to load up
                await _webView.CoreWebView2.ExecuteScriptAsync(scriptBoth); // Execute the JavaScript in the WebView2 control
            }
        });
       
	}
}