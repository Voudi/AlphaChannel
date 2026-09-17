using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CefSharp;
using CefSharp.Handler;
using CefSharp.OffScreen;
using CefSharp.Structs;

const int width = 960;
const int height = 540;
if (args.Length < 3) return 2;
var pipeName = args[0];
var cacheDirectory = args[1];
var audioPipeName = args[2];
Directory.CreateDirectory(cacheDirectory);

var runtimeDirectory = AppContext.BaseDirectory;
var settings = new CefSettings
{
    CachePath = cacheDirectory,
    RootCachePath = cacheDirectory,
    BrowserSubprocessPath = Path.Combine(runtimeDirectory, "CefSharp.BrowserSubprocess.exe"),
    ResourcesDirPath = runtimeDirectory,
    LocalesDirPath = Path.Combine(runtimeDirectory, "locales"),
    WindowlessRenderingEnabled = true,
    LogSeverity = LogSeverity.Disable
};
settings.CefCommandLineArgs.Remove("mute-audio");
settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
// OSR already returns CPU BGRA frames to Alpha Channel. Keeping Chromium's GPU
// process disabled avoids a second D3D device competing with FFXIV's NVIDIA
// device while video-heavy pages such as YouTube are rendering.
settings.CefCommandLineArgs["disable-gpu"] = "1";
settings.CefCommandLineArgs["disable-gpu-compositing"] = "1";

if (!Cef.Initialize(settings, true, (IBrowserProcessHandler?)null)) return 3;
try
{
    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    using var audioPipe = new NamedPipeClientStream(".", audioPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
    pipe.Connect(10000);
    audioPipe.Connect(10000);
    using var transport = new BrowserTransport(pipe, audioPipe);
    using var browser = new ChromiumWebBrowser("https://www.google.com/",
        new BrowserSettings { WindowlessFrameRate = 20 }, automaticallyCreateBrowser: false)
    {
        Size = new System.Drawing.Size(width, height),
        AudioHandler = new HostAudioHandler(transport)
    };
    var compositor = new FrameCompositor(width, height, transport, () =>
        (browser.RenderHandler as DefaultRenderHandler));
    browser.Paint += compositor.OnPaint;
    var currentTitle = "Browser";
    void SendState() => transport.SendState(browser.Address, currentTitle, browser.IsLoading,
        browser.CanGoBack, browser.CanGoForward, browser.IsBrowserInitialized);
    browser.AddressChanged += (_, _) => SendState();
    browser.TitleChanged += (_, e) => { currentTitle = e.Title; SendState(); };
    browser.LoadingStateChanged += (_, _) => SendState();
    browser.LoadError += (_, e) => { if (e.ErrorCode != CefErrorCode.Aborted) transport.SendError(e.ErrorText); };
    browser.CreateBrowser();
    SendState();

    while (transport.ReadCommand(browser)) { }
    browser.GetBrowserHost()?.CloseBrowser(true);
}
finally
{
    Cef.Shutdown();
}
return 0;

file sealed class BrowserTransport(Stream pipe, Stream audioPipe) : IDisposable
{
    private readonly BinaryReader reader = new(pipe, Encoding.UTF8, true);
    private readonly BinaryWriter writer = new(pipe, Encoding.UTF8, true);
    private readonly object writeLock = new();
    private readonly object audioWriteLock = new();
    private readonly BinaryWriter audioWriter = new(audioPipe, Encoding.UTF8, true);
    private bool disposed;

    internal bool ReadCommand(ChromiumWebBrowser browser)
    {
        try
        {
            var type = reader.ReadByte();
            var length = reader.ReadInt32();
            var payload = reader.ReadBytes(length);
            using var memory = new MemoryStream(payload, false);
            using var command = new BinaryReader(memory, Encoding.UTF8);
            var host = browser.GetBrowserHost();
            switch (type)
            {
                case 10: browser.Load(Encoding.UTF8.GetString(payload)); break;
                case 11: if (browser.CanGoBack) browser.Back(); break;
                case 12: if (browser.CanGoForward) browser.Forward(); break;
                case 13: browser.Reload(); break;
                case 14: browser.Stop(); break;
                case 15: host?.SendMouseMoveEvent(command.ReadInt32(), command.ReadInt32(), false, CefEventFlags.None); break;
                case 16:
                    host?.SendMouseClickEvent(command.ReadInt32(), command.ReadInt32(),
                        (MouseButtonType)command.ReadInt32(), command.ReadBoolean(), 1, CefEventFlags.None);
                    break;
                case 17: host?.SendMouseWheelEvent(command.ReadInt32(), command.ReadInt32(), command.ReadInt32(), command.ReadInt32(), CefEventFlags.None); break;
                case 18:
                    var keyCode = command.ReadInt32();
                    var keyUp = command.ReadBoolean();
                    var modifiers = (CefEventFlags)command.ReadInt32();
                    host?.SendKeyEvent(new KeyEvent { Type = keyUp ? KeyEventType.KeyUp : KeyEventType.RawKeyDown,
                        WindowsKeyCode = keyCode, NativeKeyCode = keyCode, Modifiers = modifiers, FocusOnEditableField = true });
                    break;
                case 19:
                    var character = command.ReadChar();
                    var charModifiers = (CefEventFlags)command.ReadInt32();
                    host?.SendKeyEvent(new KeyEvent { Type = KeyEventType.Char, WindowsKeyCode = character,
                        NativeKeyCode = character, Modifiers = charModifiers, FocusOnEditableField = true });
                    break;
                case 20: host?.SetFocus(command.ReadBoolean()); break;
                case 21: return false;
                case 22:
                    SendYouTubeCookies(
                        YouTubeCookieExporter.Export(browser));
                    break;
            }
            return true;
        }
        catch (EndOfStreamException) { return false; }
        catch (IOException) { return false; }
    }

    internal void SendFrame(byte[] frame) => Send(1, frame);
    internal void SendAudio(byte[] pcm)
    {
        lock (audioWriteLock)
        {
            if (disposed) return;
            try { audioWriter.Write(pcm.Length); audioWriter.Write(pcm); audioWriter.Flush(); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    internal void SendError(string error) => Send(4, Encoding.UTF8.GetBytes(error));
    internal void SendYouTubeCookies(YouTubeCookieExportResult result) =>
        Send(5, JsonSerializer.SerializeToUtf8Bytes(result));
    internal void SendState(string address, string title, bool loading, bool back, bool forward, bool ready) =>
        Send(3, JsonSerializer.SerializeToUtf8Bytes(new BrowserState(address, title, loading, back, forward, ready)));

    private void Send(byte type, byte[] payload)
    {
        lock (writeLock)
        {
            if (disposed) return;
            try { writer.Write(type); writer.Write(payload.Length); writer.Write(payload); writer.Flush(); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    public void Dispose()
    {
        lock (writeLock) disposed = true;
        reader.Dispose();
        writer.Dispose();
        audioWriter.Dispose();
    }
    private sealed record BrowserState(string Address, string Title, bool IsLoading, bool CanGoBack, bool CanGoForward, bool IsReady);
}

file static class YouTubeCookieExporter
{
internal static YouTubeCookieExportResult Export(
    ChromiumWebBrowser browser)
{
    try
    {
        var cookieManager =
            browser.GetCookieManager();

        var cookies =
            cookieManager
                .VisitAllCookiesAsync()
                .GetAwaiter()
                .GetResult()
                .Where(cookie => IsYouTubeCookieDomain(cookie.Domain))
                .Where(cookie => cookie.Expires is null || cookie.Expires > DateTime.UtcNow)
                .ToArray();

        var authenticationCookieNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "LOGIN_INFO",
                "SID",
                "SAPISID",
                "__Secure-1PSID",
                "__Secure-3PSID",
                "__Secure-1PAPISID",
                "__Secure-3PAPISID"
            };

        if (!cookies.Any(cookie => authenticationCookieNames.Contains(cookie.Name)))
        {
            return new YouTubeCookieExportResult(
                false,
                null,
                "No signed-in YouTube session was found. Finish signing in, then try again.");
        }

        var output =
            new StringBuilder("# Netscape HTTP Cookie File\n");

        foreach (var cookie in cookies)
        {
            var domain =
                cookie.HttpOnly
                    ? "#HttpOnly_" + cookie.Domain
                    : cookie.Domain;

            var includeSubdomains =
                cookie.Domain.StartsWith(".", StringComparison.Ordinal)
                    ? "TRUE"
                    : "FALSE";

            var expires =
                cookie.Expires is { } expiry
                    ? new DateTimeOffset(
                            DateTime.SpecifyKind(expiry, DateTimeKind.Utc))
                        .ToUnixTimeSeconds()
                    : 0;

            output
                .Append(CleanCookieField(domain)).Append('\t')
                .Append(includeSubdomains).Append('\t')
                .Append(CleanCookieField(cookie.Path ?? "/")).Append('\t')
                .Append(cookie.Secure ? "TRUE" : "FALSE").Append('\t')
                .Append(expires).Append('\t')
                .Append(CleanCookieField(cookie.Name)).Append('\t')
                .Append(CleanCookieField(cookie.Value))
                .Append('\n');
        }

        return new YouTubeCookieExportResult(
            true,
            output.ToString(),
            null);
    }
    catch (Exception exception)
    {
        return new YouTubeCookieExportResult(
            false,
            null,
            $"Alpha Channel could not read the browser's YouTube session: {exception.Message}");
    }
}

private static bool IsYouTubeCookieDomain(string? domain)
{
    if (string.IsNullOrWhiteSpace(domain))
        return false;

    var normalised =
        domain.TrimStart('.');

    return normalised.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
           normalised.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
           normalised.Equals("google.com", StringComparison.OrdinalIgnoreCase) ||
           normalised.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase) ||
           normalised.Equals("googleapis.com", StringComparison.OrdinalIgnoreCase) ||
           normalised.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase) ||
           normalised.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
           normalised.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
           normalised.Equals("ytimg.com", StringComparison.OrdinalIgnoreCase) ||
           normalised.EndsWith(".ytimg.com", StringComparison.OrdinalIgnoreCase);
}

private static string CleanCookieField(string? value) =>
    (value ?? string.Empty)
        .Replace('\t', ' ')
        .Replace('\r', ' ')
        .Replace('\n', ' ');
}

file sealed record YouTubeCookieExportResult(
    bool Success,
    string? Cookies,
    string? Error);

file sealed class FrameCompositor(int width, int height, BrowserTransport transport,
    Func<DefaultRenderHandler?> getHandler)
{
    private readonly object sync = new();
    private readonly byte[] baseFrame = new byte[width * height * 4];
    private byte[]? popup;
    private int popupX, popupY, popupWidth, popupHeight;

    internal void OnPaint(object? sender, OnPaintEventArgs e)
    {
        lock (sync)
        {
            if (e.IsPopup)
            {
                popupWidth = e.Width; popupHeight = e.Height;
                var position = getHandler()?.PopupPosition ?? System.Drawing.Point.Empty;
                popupX = position.X; popupY = position.Y;
                popup = new byte[e.Width * e.Height * 4];
                Marshal.Copy(e.BufferHandle, popup, 0, popup.Length);
            }
            else
            {
                if (e.Width != width || e.Height != height) return;
                Marshal.Copy(e.BufferHandle, baseFrame, 0, baseFrame.Length);
            }
            var frame = new byte[baseFrame.Length];
            Buffer.BlockCopy(baseFrame, 0, frame, 0, frame.Length);
            if (getHandler()?.PopupOpen == true && popup is not null) Blend(frame);
            transport.SendFrame(frame);
        }
    }

    private void Blend(byte[] destination)
    {
        if (popup is null) return;
        for (var sy = 0; sy < popupHeight; sy++)
        {
            var dy = popupY + sy; if (dy < 0 || dy >= height) continue;
            for (var sx = 0; sx < popupWidth; sx++)
            {
                var dx = popupX + sx; if (dx < 0 || dx >= width) continue;
                var si = (sy * popupWidth + sx) * 4; var di = (dy * width + dx) * 4;
                var alpha = popup[si + 3]; if (alpha == 0) continue;
                var inverse = 255 - alpha;
                destination[di] = (byte)(popup[si] + destination[di] * inverse / 255);
                destination[di + 1] = (byte)(popup[si + 1] + destination[di + 1] * inverse / 255);
                destination[di + 2] = (byte)(popup[si + 2] + destination[di + 2] * inverse / 255);
                destination[di + 3] = 255;
            }
        }
    }
}

file sealed class HostAudioHandler(BrowserTransport transport) : AudioHandler
{
    private int channels = 2;
    protected override bool GetAudioParameters(IWebBrowser webBrowser, IBrowser browser, ref AudioParameters parameters)
    {
        parameters = new AudioParameters(CefSharp.Enums.ChannelLayout.LayoutStereo, 48000, 1024);
        return true;
    }
    protected override void OnAudioStreamStarted(IWebBrowser webBrowser, IBrowser browser, AudioParameters parameters, int channelCount) =>
        channels = Math.Max(1, channelCount);
    protected override void OnAudioStreamPacket(IWebBrowser webBrowser, IBrowser browser, IntPtr data, int frames, long pts)
    {
        var left = new float[frames]; var right = new float[frames];
        Marshal.Copy(Marshal.ReadIntPtr(data), left, 0, frames);
        Marshal.Copy(Marshal.ReadIntPtr(data, Math.Min(1, channels - 1) * IntPtr.Size), right, 0, frames);
        var pcm = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var l = (short)(Math.Clamp(left[i], -1f, 1f) * short.MaxValue);
            var r = (short)(Math.Clamp(right[i], -1f, 1f) * short.MaxValue);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4, 2), l);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2, 2), r);
        }
        transport.SendAudio(pcm);
    }
    protected override void OnAudioStreamError(IWebBrowser webBrowser, IBrowser browser, string errorMessage) => transport.SendError(errorMessage);
}
