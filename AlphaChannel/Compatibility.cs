using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Win32;
using System.Net.Sockets;
using AlphaChannel;

class Compatibility
{
    public static bool IsWebView2Installed()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients"))
            {
                if (key == null)
                    return false;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    if (subKeyName.Contains("Microsoft Edge WebView2"))
                        return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static void InstallWebView2()
    {
        if (IsWebView2Installed())
        {
            Services.Log.Debug("WebView2 already installed.");
            return;
        }

        string url = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        string installerPath = "MicrosoftEdgeWebView2Setup.exe";

        using (var httpClient = new HttpClient()) //Only runs once outside the UI Thread, so we can afford to block here
        {
            Services.Log.Debug("Downloading WebView2 installer...");

            var data = httpClient.GetByteArrayAsync(url).GetAwaiter().GetResult();
            File.WriteAllBytes(installerPath, data);
        }

        Services.Log.Debug("Running installer...");
        string fullPath = Path.GetFullPath(installerPath);

        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = $"/c \"{fullPath}\"",
                UseShellExecute = false,       // needed for cmd
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                WorkingDirectory = Path.GetDirectoryName(fullPath)
            }
        };
        
        proc.Start();
        proc.WaitForExit();

        Services.Log.Debug($"Installer finished with code {proc.ExitCode}");
    }

    public static bool IsRunningUnderWine()
    {
        // Wine sets this registry key
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"Software\Wine"))
            {
                return key != null;
            }
        }
        catch
        {
            return false;
        }
    }
    
    public static bool BinaryExists(string name)
    {
        var p = new Process();
        p.StartInfo.FileName = "/bin/sh";
        p.StartInfo.Arguments = $"-c \"command -v {name}\"";
        p.StartInfo.RedirectStandardOutput = true;
        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.UseShellExecute = false;

        p.Start();
        string result = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        return !string.IsNullOrWhiteSpace(result);
    }

    public static void ConnectToWebkit()
    {
        
        var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var endPoint = new UnixDomainSocketEndPoint("/tmp/webkit_helper.sock");
        // Use Wine path mapping: Z:\tmp\webkit_helper.sock → /tmp/webkit_helper.sock
        client.Connect(endPoint);

        string msg = "{\"action\":\"load\",\"url\":\"https://example.com\"}";
        client.Send(Encoding.UTF8.GetBytes(msg));
    }

/*
    private static string[] mpvCandidates = {
        "mpv",
        "/usr/bin/mpv",
        "/usr/local/bin/mpv",
        "/bin/mpv",
        "flatpak run io.mpv.Mpv",
        "/var/lib/flatpak/exports/bin/io.mpv.Mpv",
        $"{Environment.GetEnvironmentVariable("HOME")}/.local/share/flatpak/exports/bin/io.mpv.Mpv",
        "/snap/bin/mpv",
    };

    private static string[] ytdlpCandidates = {
        "yt-dlp",
        "/usr/bin/yt-dlp",
        "/usr/local/bin/yt-dlp",
        "/bin/yt-dlp",
        $"{Environment.GetEnvironmentVariable("HOME")}/.local/bin/yt-dlp",
        $"{Environment.GetEnvironmentVariable("HOME")}/.local/pipx/venvs/yt-dlp/bin/yt-dlp",
        "flatpak run io.github.yt_dlp.yt-dlp",
        "/snap/bin/yt-dlp",
    };
*/
    public static bool MPVExists()
    {
        return BinaryExists("mpv");
    }
    public static bool YTDLPExists()
    {
        return BinaryExists("yt-dlp");
    }
}