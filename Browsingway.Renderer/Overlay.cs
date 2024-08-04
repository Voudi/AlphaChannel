using Browsingway.Common.Ipc;
using CefSharp;
using CefSharp.OffScreen;
using CefSharp.Structs;
using BrowserSettings = CefSharp.BrowserSettings;
using RequestContext = CefSharp.RequestContext;
using RequestContextSettings = CefSharp.RequestContextSettings;
using Size = System.Drawing.Size;
using WindowInfo = CefSharp.WindowInfo;

namespace Browsingway.Renderer;

internal class Overlay : IDisposable
{
	private readonly string _id;
	private readonly int _framerate;
	public readonly TextureRenderHandler RenderHandler;
	private ChromiumWebBrowser? _browser;
	private string _url;
	private float _zoom;
	private bool _muted;
	private string _customCss;

	public Overlay(string id, string url, float zoom, bool muted, int framerate, string customCss,
		TextureRenderHandler renderHandler)
	{
		_id = id;
		_url = url;
		_zoom = zoom;
		_framerate = framerate;
		_muted = muted;
		_customCss = customCss;
		RenderHandler = renderHandler;
	}

	public void Dispose()
	{
		RenderHandler.Dispose();

		if (_browser is not null)
		{
			_browser.RenderHandler = null;
			_browser.Dispose();
		}
	}

	public void Initialise()
	{
		var requestContextSettings = new RequestContextSettings
		{
			CachePath = Path.Combine(CefHandler.RootCachePath, _id),
			PersistUserPreferences = true,
			PersistSessionCookies = true
		};
		var rc = new RequestContext(requestContextSettings);

		_browser = new ChromiumWebBrowser(_url, automaticallyCreateBrowser: false, requestContext: rc);
		_browser.RenderHandler = RenderHandler;
		_browser.MenuHandler = new CefMenuHandler();
		Rect size = RenderHandler.GetViewRect();

		// General _browser config
		WindowInfo windowInfo = new() {Width = size.Width, Height = size.Height};
		windowInfo.SetAsWindowless(IntPtr.Zero);

		// WindowInfo gets ignored sometimes, be super sure:
		_browser.BrowserInitialized += (_, _) =>
		{
			_browser.Size = new Size(size.Width, size.Height);
			Mute(_muted);
		};

		_browser.AddressChanged += (_, args) =>
		{
				_browser.SetZoomLevel(ScaleZoomLevel(_zoom));
				InjectUserCss(_customCss);
		};

		BrowserSettings browserSettings = new() {WindowlessFrameRate = _framerate};

		// Ready, boot up the _browser
		_browser.CreateBrowser(windowInfo, browserSettings);

		browserSettings.Dispose();
		windowInfo.Dispose();
	}

	public void InjectUserCss(string css)
	{
		if (css.Length == 0 && _customCss.Length == 0)
			return; // nothing to do

		_customCss = css; // to reapply correctly on load

		// escape rules
		// ` -> \` to prevent end of string
		// ${ -> \${ to prevent variable injection
		// Using a template string (``) instead of a quoted string ('') to not have to deal with javascript
		// newline weirdness (plus it behaves a bit like a verbatim string)
		css = css.Replace("`", @"\'");
		css = css.Replace("${", @"\${");

		//Css for custom frame overlay
		var overlayStyle = ":not(" + css + ") { visibility: collapse !important; } "+ css + "{ z-index: 2147483647; height: 1080px !important;  width: 1920px !important; left: 64px; top: 36px; position: fixed;} #picto-overlay{ position: fixed; width: -webkit-fill-available !important; height: -webkit-fill-available !important; backface-visibility: hidden; }";
		overlayStyle = overlayStyle.Replace("`", @"\'");
		overlayStyle = overlayStyle.Replace("${", @"\${");

		// (()=>{...})() self executable function to prevent scope issues
		_browser.GetMainFrame().ExecuteJavaScriptAsync(
				"document.addEventListener('DOMContentLoaded', function() {" +
				"				if(document.querySelector('#picto-overlay') == null){" +
				"					document.body.style.backgroundColor = 'transparent';" +
				"					var iframe = document.createElement('iframe');" +
				"					iframe.id = 'picto-overlay';" +
				"					iframe.setAttribute('src', '" + _url + "');" +
				"				    iframe.style.setProperty('visibility', 'visible', 'important');" +
				"					iframe.setAttribute('frameborder', '0');" +
				"					document.body.innerHTML = '';" +
				"					document.body.appendChild(iframe);" +
				"				}" +
				"				var cnt = 0;" +
				"				iframe.addEventListener('load', function() { " +
				"				" +
				"					var cnt = 0;" +
				"					function findElementInFrame(iframeDocument){" +
				"						if(!iframeDocument) return false;" +
				"						cnt++;" +
				"						if(iframeDocument.querySelector('#picto-overlay-css' + cnt) === null) {" +
				"							const stylef = document.createElement('style');" +
				"							stylef.id = 'picto-overlay-css' + cnt; " +
				"							stylef.textContent =`" + overlayStyle + " `;" +
				"							iframeDocument.head.append(stylef);" +
				"						}" +
				"						" +
				"						var targetElement = iframeDocument.querySelector(`" + css + "`);" +
				"						" +
				"						if(!targetElement){" +
				"							iframeDocument.querySelectorAll('iframe').forEach(function(nestedIframe, index) {" +
				"								if(findElementInFrame(nestedIframe.contentDocument)){" +
				"									targetElement = nestedIframe;" +
				"								}" +
				"							});" +
				"						}" +
				"						if(targetElement)" +
				"						{" +
				"							var parentElement = (iframeDocument.querySelector(`" + css + "`) == targetElement) ? targetElement.parentElement : targetElement;" +
				"							while (parentElement) {" +
				"								parentElement.style.setProperty('overflow', 'hidden', 'important');" +
				"								parentElement.style.setProperty('visibility', 'visible', 'important');" +
				"								parentElement = parentElement.parentElement;" +
				"							}" +
				"							return true;" +
				"						}" +
				"						return false;" +
				"					}" +
				"					" +
				"					var intervalId = setInterval(function(){" +
				"						cnt = 0;" +
				"						if(findElementInFrame(window.top.document))" +
				"							clearInterval(intervalId);" +
				"					}, 1000);" +
				"					" +
				"				});" +
				"});"
			);
	}

	public void Navigate(string newUrl)
	{
		// If navigating to the same _url, force a clean reload
		if (_browser?.Address == newUrl)
		{
			_browser.Reload(true);
			return;
		}

		// Otherwise load regularly
		_url = newUrl;
		_browser?.Load(newUrl);
	}

	public void Zoom(float zoom)
	{
		_zoom = zoom;
		_browser?.SetZoomLevel(ScaleZoomLevel(zoom));
	}

	public void Mute(bool mute)
	{
		_muted = mute;
		_browser?.GetBrowserHost().SetAudioMuted(mute);
	}

	public void Debug()
	{
		_browser.ShowDevTools();
	}

	public void HandleMouseEvent(MouseButtonMessage msg)
	{
		// If the _browser isn't ready yet, noop
		if (_browser == null || !_browser.IsBrowserInitialized) { return; }

		var cursor = DpiScaling.ScaleViewPoint(msg.X, msg.Y);

		// Update the renderer's concept of the mouse cursor
		RenderHandler.SetMousePosition(cursor.X, cursor.Y);

		MouseEvent evt = new(cursor.X, cursor.Y, DecodeInputModifier(msg.Modifier));

		IBrowserHost? host = _browser.GetBrowserHost();

		// Ensure the mouse position is up to date
		host.SendMouseMoveEvent(evt, msg.Leaving);

		// Fire any relevant click events
		List<MouseButtonType> doubleClicks = DecodeMouseButtons(msg.Double);
		DecodeMouseButtons(msg.Down)
			.ForEach(button => host.SendMouseClickEvent(evt, button, false, doubleClicks.Contains(button) ? 2 : 1));
		DecodeMouseButtons(msg.Up).ForEach(button => host.SendMouseClickEvent(evt, button, true, 1));

		// CEF treats the wheel delta as mode 0, pixels. Bump up the numbers to match typical in-_browser experience.
		int deltaMult = 100;
		host.SendMouseWheelEvent(evt, (int)msg.WheelX * deltaMult, (int)msg.WheelY * deltaMult);
	}

	public void HandleKeyEvent(KeyEventMessage request)
	{
		_browser.GetBrowserHost().SendKeyEvent(request.Msg, request.WParam, request.LParam);
	}

	public void Resize(Size size)
	{
		// Need to resize renderer first, the _browser will check it (and hence the texture) when _browser.Size is set.
		RenderHandler.Resize(size);
		if (_browser is not null)
		{
			_browser.Size = size;
		}
	}

	private List<MouseButtonType> DecodeMouseButtons(MouseButton buttons)
	{
		List<MouseButtonType> result = new();
		if ((buttons & MouseButton.Primary) == MouseButton.Primary) { result.Add(MouseButtonType.Left); }

		if ((buttons & MouseButton.Secondary) == MouseButton.Secondary) { result.Add(MouseButtonType.Right); }

		if ((buttons & MouseButton.Tertiary) == MouseButton.Tertiary) { result.Add(MouseButtonType.Middle); }

		return result;
	}

	private CefEventFlags DecodeInputModifier(InputModifier modifier)
	{
		CefEventFlags result = CefEventFlags.None;
		if ((modifier & InputModifier.Shift) == InputModifier.Shift) { result |= CefEventFlags.ShiftDown; }

		if ((modifier & InputModifier.Control) == InputModifier.Control) { result |= CefEventFlags.ControlDown; }

		if ((modifier & InputModifier.Alt) == InputModifier.Alt) { result |= CefEventFlags.AltDown; }

		return result;
	}

	private double ScaleZoomLevel(float zoom)
	{
		if (Math.Abs(zoom - 100f) < 0.5f)
		{
			return 0;
		}

		return (5.46149645 * Math.Log(_zoom)) - 25.12;
	}
}