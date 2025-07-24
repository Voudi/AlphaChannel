using Dalamud.Hooking;
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

    private int ClassicWidth => CalculateResolution();
    private int ClassicHeight => (1080 / (1920 / ClassicWidth));

    private readonly Dictionary<string, string> _adBlockDirs;
    private readonly string _cacheDir;
    private readonly string _initUrl;
    private bool _coreLoaded = false;

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    const int SW_HIDE = 0;
    const int SW_SHOW = 5;
    const int SW_MINIMIZE = 6;
    const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private const int GWL_HWNDPARENT = -8;

    private const uint SWP_NOACTIVATE = 0x0010;

    private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

    protected override bool ShowWithoutActivation => true;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    private RECT GetWindowRectHelper(nint hWnd)
    {
        if (!GetWindowRect(hWnd, out RECT rect))
            Services.Log.Error("FATAL: Failed to get game window rect!");
        return rect;
    }

    private IntPtr _gameWindow = IntPtr.Zero;

    private RECT _gameWindowCoords => GetWindowRectHelper(_gameWindow);

    private int _gameWindowCoordsLeft => (_gameWindowCoords.Right - _gameWindowCoords.Left) > ClassicWidth + 25 ? _gameWindowCoords.Left + 20 : _gameWindowCoords.Left;

    private int _gameWindowCoordsTop => (_gameWindowCoords.Bottom - _gameWindowCoords.Top) > ClassicHeight + 25 ? _gameWindowCoords.Top + 20 : _gameWindowCoords.Top;

    public WebView2Client(ControlWindow mainWindow, Dictionary<string, string> adBlockDirs, string cacheDir, string initUrl)
    {
        _gameWindow = GetGameWindow();

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

        _topMostTimer = new System.Windows.Forms.Timer();

        //SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

        _webView = new WebView2()
        {
            Dock = DockStyle.Fill
        };

        Init();
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

        // Set the main window as the parent
        SetWindowLong(this.handle, GWL_HWNDPARENT, _gameWindow);

        // Optionally move it behind
        SetWindowPos(this.handle, HWND_BOTTOM, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        Bounds = new Rectangle(_gameWindowCoordsLeft, _gameWindowCoordsTop, ClassicWidth, ClassicHeight);

        //KeepOnTop();
        //StartTopMostEnforcer();
        if (_gameWindow != IntPtr.Zero)
            SetForegroundWindow(_gameWindow);

        await _webView.EnsureCoreWebView2Async(environment);
    }

    private bool minimized = false;
    public void PollMainwindow()
    {
        if (IsIconic(_gameWindow) || !IsWindowVisible(_gameWindow))
        {
            if (!minimized)
            {
                minimized = true;
                ShowWindow(handle, SW_HIDE);
            }
        }
        else
        {
            if (minimized)
            {
                minimized = false;
                ShowWindow(handle, SW_SHOW);
                if (!_resized)
                {
                    SetWindowPos(this.handle, HWND_BOTTOM, _gameWindowCoordsLeft, _gameWindowCoordsTop, ClassicWidth, ClassicHeight,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }
        }
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

    private int CalculateResolution()
    {
        var maxWidth = 480; //Start at smallest width 360p

        maxWidth = Math.Max(480, _gameWindowCoords.Right - _gameWindowCoords.Left);
        if (maxWidth >= 480 && maxWidth < 960)
            maxWidth = 480;
        if (maxWidth >= 960 && maxWidth < 1920)
            maxWidth = 960;
        if (maxWidth >= 1920)
            maxWidth = 1920;

        return maxWidth;
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
            Bounds = new Rectangle(Cursor.Position.X - ClassicWidth / 4, Cursor.Position.Y, (int)(ClassicWidth / 1.5), (int)(ClassicHeight / 1.5));
        }
        else
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = false;
            Enabled = false;

            Bounds = new Rectangle(_gameWindowCoordsLeft, _gameWindowCoordsTop, ClassicWidth, ClassicHeight);

            SetWindowPos(this.handle, HWND_BOTTOM, _gameWindowCoordsLeft, _gameWindowCoordsTop, ClassicWidth, ClassicHeight,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            Services.Log.Debug("Set width and height to " + Width + "x" + Height);
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
        if (!_close_flag)
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
        _mainWindow.OnDOMContentLoaded();

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
								var checkboxAge = document.querySelector(""div[class='p-checkbox-box']"");
								if(checkboxAge != null) {
									checkboxAge.click();
								}

								setTimeout(() => {
									var joinButton = document.querySelector(""button[class~='p-button']"");
									joinButton.click();
								}, 1000);
        
        
								setTimeout(() =>  {
									var skipButton = Array.from(document.querySelectorAll(""span[class='tu-btn-content']"")).find(btn => btn.textContent.toUpperCase().includes(""SKIP""));
									if(skipButton != null) {
										skipButton.click();
									}
									document.querySelector(""span[class*='layoutBtns']>button:last-child"").click();
								}, 3000);

								setTimeout(() => {
									document.querySelector(""div[class*='chatContainer']"").remove()
									setTimeout(() => document.querySelector(""button[class*='fsChatBtn']"").remove(), 500);
								}, 4000);
							}
						})();
		";

        _webView.Invoke(async () =>
        {
            if (_coreLoaded)
            {
                await Task.Delay(1000); //Wait one second for elements to load up
                await _webView.CoreWebView2.ExecuteScriptAsync(scriptBoth); // Execute the JavaScript in the WebView2 control
            }
        });

    }
}