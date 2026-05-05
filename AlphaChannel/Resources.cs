using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;
using Windows.Media.Capture.Core;

namespace AlphaChannel;

public class Resources
{
    private readonly HttpClient httpClient;
    private readonly string pluginDir;
    
    public string[] mpvCheckResult = [string.Empty, string.Empty];
    public string[] ytdlpCheckResult = [string.Empty, string.Empty];
    public Resources(string pluginDir)
    {
        httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "AlphaChannelUpdater/1.0");
        this.pluginDir = pluginDir;
    }

    public string? GetLocationMPV()
    {
        var filenameStart = "mpv-dev-lgpl-x86_64-";
        var dir = Directory.GetDirectories(pluginDir, $"{filenameStart}*").FirstOrDefault();
        if(dir != null)
            return dir+"/libmpv-2.dll";
        else
            return null;
    }
    public string? GetLocationYTDLP()
    {
        var filenameStart = "yt-dlp";
        var dir = Directory.GetDirectories(pluginDir, $"{filenameStart}*").FirstOrDefault();
        if(dir != null)
            return dir+"/yt-dlp.exe";
        else
            return null;
    }    
    public async Task CheckMPVAsync()
    {
        var filenameStart = "mpv-dev-lgpl-x86_64-";
        var filenameEnd = ".7z";
        var url = "https://api.github.com/repos/zhongfly/mpv-winbuild/releases/latest";
        mpvCheckResult = await CheckForUpdateAsync(pluginDir, filenameStart, filenameEnd, url);
    }
    public async Task CheckYTDLPAsync()
    {
        var filenameStart = "yt-dlp.exe";
        var filenameEnd = ".exe";
        var url = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
        ytdlpCheckResult = await CheckForUpdateAsync(pluginDir, filenameStart, filenameEnd, url);
    }
    public async Task<bool> DownloadMPVAsync()
    {
        var filenameStart = "mpv-dev-lgpl-x86_64-";
        var filenameEnd = ".7z";
        var downloadURL = mpvCheckResult[0];
        var folderName = mpvCheckResult[1];
        return await UpdateAsync(pluginDir, filenameStart, filenameEnd, downloadURL, folderName);
    }
    public async Task<bool> DownloadYTDLPAsync()
    {
        var filenameStart = "yt-dlp";
        var filenameEnd = ".exe";
        var downloadURL = ytdlpCheckResult[0];
        var folderName = ytdlpCheckResult[1];
        return await UpdateAsync(pluginDir, filenameStart, filenameEnd, downloadURL, folderName);
    }
    private async Task<string[]> CheckForUpdateAsync(string pluginDir, string nameStartsWith, string nameEndsWith, string checkURL)
    {
        var json = await httpClient.GetStringAsync(checkURL);
        var doc = JsonDocument.Parse(json);
        var remoteId = doc.RootElement.GetProperty("id").GetInt64();
        var asset = doc.RootElement.GetProperty("assets")
            .EnumerateArray()
            .First(a => a.GetProperty("name").GetString()!
                .StartsWith(nameStartsWith) &&
                a.GetProperty("name").GetString()!.EndsWith(nameEndsWith));

        var assetName = asset.GetProperty("name").GetString()!;
        var folderName = assetName.Replace(nameEndsWith, "") + "_" + remoteId;

        var localFolder = Path.Combine(pluginDir, folderName);

        if (Directory.Exists(localFolder))
        {
            return [string.Empty, folderName]; //Already up to date
        }

        var downloadURL = asset.GetProperty("browser_download_url").GetString()!;
        return [downloadURL, folderName];
    }

    private async Task<bool> UpdateAsync(string pluginDir, string nameStartsWith, string nameEndsWith, string downloadURL, string folderName)
    {
        try
        {
            var tempFile = Path.GetTempFileName() + nameEndsWith;
            var response = await httpClient.GetAsync(downloadURL, HttpCompletionOption.ResponseHeadersRead);
            await using (var fs = File.OpenWrite(tempFile))
            {
                await response.Content.CopyToAsync(fs);
            }
            if(nameEndsWith == ".7z")
            {
                var tempFolderName = Path.GetTempFileName();
                File.Delete(tempFolderName);
                var localFolder = Path.Combine(pluginDir, tempFolderName);
                Directory.CreateDirectory(localFolder);
                using (var archive = ArchiveFactory.OpenArchive(tempFile))
                {
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        entry.WriteToDirectory(localFolder, new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                    }
                }

                File.Delete(tempFile);

                foreach (var dir in Directory.GetDirectories(pluginDir, $"{nameStartsWith}*"))
                {
                    Directory.Delete(dir, recursive: true);
                }

                if(Directory.Exists(Path.Combine(pluginDir, folderName))) //Super weird but lets just do this to be safe
                {
                    foreach (var file in Directory.GetFiles(localFolder, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(localFolder, file);
                        var target = Path.Combine(Path.Combine(pluginDir, folderName), relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: true);
                    }
                }
                else
                {
                    Directory.Move(localFolder, Path.Combine(pluginDir, folderName));
                }
            }
            else
            {
                foreach (var dir in Directory.GetDirectories(pluginDir, $"{nameStartsWith}*"))
                {
                    Directory.Delete(dir, recursive: true);
                }

                var localFolder = Path.Combine(pluginDir, folderName);
                Directory.CreateDirectory(localFolder);

                var targetPath = Path.Combine(localFolder, nameStartsWith.EndsWith(nameEndsWith) ? nameStartsWith : nameStartsWith + nameEndsWith);
                File.Copy(tempFile, targetPath, overwrite: true);
                File.Delete(tempFile);
            }
            return true;
        }
        catch (Exception e)
        {
            Services.Log.Error($"Error updating {nameStartsWith}: {e.Message} {e.StackTrace}");
            return false;
        }
    }
}