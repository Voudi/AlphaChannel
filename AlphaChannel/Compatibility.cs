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
    private static bool? _isFlatpak;
    private static string? _flatpakPath;

    public static bool IsRunningInFlatpak()
    {
        return File.Exists("/.flatpak-info");
    }

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
    
    public static Process? StartBinary(string name, string arguments)
    {
        string? path = FindBinary(name);
        string args = $"/c start /unix "+name+" "+arguments;
        if (_isFlatpak.HasValue && _isFlatpak.Value && _flatpakPath != null)
        {
            args = $"/c start /unix {_flatpakPath} --host {name} {arguments}";
        }
        
        try
        {

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = @"C:\windows\system32\cmd.exe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            
            proc.Start();
            
            Services.Log.Debug($"Started {name} (pid {proc.Id}) from {path}");
            return proc;
        }
        catch (Exception e)
        {
            Services.Log.Error($"Failed to start {name}: {e.Message}");
            return null;
        }
    }
   public static string? FindBinary(string name)
    {
        if(name != "flatpak-spawn" && !_isFlatpak.HasValue)
        {
            _flatpakPath = FindBinary("flatpak-spawn");
            _isFlatpak = _flatpakPath != null;
        }

        var paths = new[] {
                @"Z:/run/host/usr/bin",
                @"Z:/run/host/usr/local/bin",
                @"Z:/run/host/bin",
                @"Z:/usr/bin",
                @"Z:/usr/local/bin",
                @"Z:/bin",
                @"/run/host/usr/bin",
                @"/run/host/usr/local/bin",
                @"/run/host/bin",
                @"/usr/bin",
                @"/usr/local/bin",
                @"/bin",
                @"/snap/bin",
                @"Z:\run\host\usr\bin",
                @"Z:\run\host\usr\local\bin",
                @"Z:\run\host\bin",
                @"Z:\usr\bin",
                @"Z:\usr\local\bin",
                @"Z:\bin",
                @"\run\host\usr\bin",
                @"\run\host\usr\local\bin",
                @"\run\host\bin",
                @"\usr\bin",
                @"\usr\local\bin",
                @"\bin",
                @"\snap\bin"
            };
            
        foreach (var dir in paths)
        {
            string path = Path.Combine(dir, name);
            
            if (dir.Contains("\\"))
            {
                path = path.Replace('/', '\\');
            }
            else
            {
                path = path.Replace('\\', '/');
            }
            
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    public static bool BinaryExists(string name)
    {

        return FindBinary(name) != null;
    }

    public static bool MPVExists()
    {
        return BinaryExists("mpv");
    }

    public static bool YTDLPExists()
    {
        return BinaryExists("yt-dlp");
    }

    public static bool FlatpakSpawnExists()
    {
        return (_isFlatpak.HasValue && _isFlatpak.Value && _flatpakPath != null) || BinaryExists("flatpak-spawn");
    }
}