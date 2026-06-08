using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace AlphaChannel;

public class Resources : IDisposable
{
	private readonly HttpClient _httpClient;
	private readonly string _pluginDir;

	public string[] MpvCheckResult { get; private set; } = [string.Empty, string.Empty];
	public string[] YtdlpCheckResult { get; private set; } = [string.Empty, string.Empty];

	public Resources(string pluginDir)
	{
		_httpClient = new HttpClient();
		_httpClient.DefaultRequestHeaders.Add("User-Agent", "AlphaChannelUpdater/1.0");
		_pluginDir = pluginDir;
	}

	public void Dispose()
	{
		_httpClient.Dispose();
		GC.SuppressFinalize(this);
	}

	public string? GetLocationMPV()
	{
		string filenameStart = "mpv-dev-lgpl-x86_64-";
		string? dir = Directory.GetDirectories(_pluginDir, $"{filenameStart}*").FirstOrDefault();
		if (dir != null)
		{
			return dir + "/libmpv-2.dll";
		}
		else
		{
			return null;
		}
	}
	public string? GetLocationYTDLP()
	{
		string filenameStart = "yt-dlp";
		string? dir = Directory.GetDirectories(_pluginDir, $"{filenameStart}*").FirstOrDefault();
		if (dir != null)
		{
			return dir + "/yt-dlp.exe";
		}
		else
		{
			return null;
		}
	}
	public async Task CheckMPVAsync()
	{
		string filenameStart = "mpv-dev-lgpl-x86_64-";
		string filenameEnd = ".7z";
		string url = "https://api.github.com/repos/zhongfly/mpv-winbuild/releases/latest";
		MpvCheckResult = await CheckForUpdateAsync(_pluginDir, filenameStart, filenameEnd, url);
	}
	public async Task CheckYTDLPAsync()
	{
		string filenameStart = "yt-dlp.exe";
		string filenameEnd = ".exe";
		string url = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
		YtdlpCheckResult = await CheckForUpdateAsync(_pluginDir, filenameStart, filenameEnd, url);
	}
	public async Task<bool> DownloadMPVAsync()
	{
		string filenameStart = "mpv-dev-lgpl-x86_64-";
		string filenameEnd = ".7z";
		string downloadURL = MpvCheckResult[0];
		string folderName = MpvCheckResult[1];
		return await UpdateAsync(_pluginDir, filenameStart, filenameEnd, downloadURL, folderName);
	}
	public async Task<bool> DownloadYTDLPAsync()
	{
		string filenameStart = "yt-dlp";
		string filenameEnd = ".exe";
		string downloadURL = YtdlpCheckResult[0];
		string folderName = YtdlpCheckResult[1];
		return await UpdateAsync(_pluginDir, filenameStart, filenameEnd, downloadURL, folderName);
	}
	private async Task<string[]> CheckForUpdateAsync(string pluginDir, string nameStartsWith, string nameEndsWith, string checkURL)
	{
		string json = await _httpClient.GetStringAsync(checkURL);
		var doc = JsonDocument.Parse(json);
		long remoteId = doc.RootElement.GetProperty("id").GetInt64();
		var asset = doc.RootElement.GetProperty("assets")
			.EnumerateArray()
			.First(a => a.GetProperty("name").GetString()!
				.StartsWith(nameStartsWith, StringComparison.Ordinal) &&
				a.GetProperty("name").GetString()!.EndsWith(nameEndsWith, StringComparison.Ordinal));

		string assetName = asset.GetProperty("name").GetString()!;
		string folderName = assetName.Replace(nameEndsWith, "") + "_" + remoteId;

		string localFolder = Path.Combine(pluginDir, folderName);

		if (Directory.Exists(localFolder))
		{
			return [string.Empty, folderName]; //Already up to date
		}

		string downloadURL = asset.GetProperty("browser_download_url").GetString()!;
		Services.Log.Warning("Found Update: " + downloadURL);
		return [downloadURL, folderName];
	}

	private async Task<bool> UpdateAsync(string pluginDir, string nameStartsWith, string nameEndsWith, string downloadURL, string folderName)
	{
		try
		{
			Services.Log.Debug("Downloading Update: " + downloadURL);
			string tempFile = Path.GetTempFileName() + nameEndsWith;
			var response = await _httpClient.GetAsync(downloadURL, HttpCompletionOption.ResponseHeadersRead);
			await using (var fs = File.OpenWrite(tempFile))
			{
				await response.Content.CopyToAsync(fs);
			}
			Services.Log.Debug("Finished Downloading " + downloadURL);
			if (nameEndsWith == ".7z")
			{
				string localFolder = Path.Combine(pluginDir, Path.GetRandomFileName());
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

				foreach (string dir in Directory.GetDirectories(pluginDir, $"{nameStartsWith}*"))
				{
					Directory.Delete(dir, recursive: true);
				}

				if (Directory.Exists(Path.Combine(pluginDir, folderName))) //Super weird but lets just do this to be safe
				{
					foreach (string file in Directory.GetFiles(localFolder, "*", SearchOption.AllDirectories))
					{
						string relative = Path.GetRelativePath(localFolder, file);
						string target = Path.Combine(Path.Combine(pluginDir, folderName), relative);
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
				foreach (string dir in Directory.GetDirectories(pluginDir, $"{nameStartsWith}*"))
				{
					Directory.Delete(dir, recursive: true);
				}

				string localFolder = Path.Combine(pluginDir, folderName);
				Directory.CreateDirectory(localFolder);

				string targetPath = Path.Combine(localFolder, nameStartsWith.EndsWith(nameEndsWith, StringComparison.Ordinal) ? nameStartsWith : nameStartsWith + nameEndsWith);
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
