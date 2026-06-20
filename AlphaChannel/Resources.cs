using System.Runtime.InteropServices;
using System.Text.Json;
using Penumbra.Api.IpcSubscribers;
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
		foreach(var paths in _tempGamePaths)
		{
			foreach(string ingamePath in paths.Keys)
			{
				string path = paths[ingamePath];
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
		}
		Directory.GetFiles(Path.Combine(_pluginDir, "resources"), "alphachannelscreentex_*.atex").ToList().ForEach(File.Delete);
		Directory.GetFiles(Path.Combine(_pluginDir, "resources"), "alphachannelscreen_*.avfx").ToList().ForEach(File.Delete);
		Directory.GetFiles(Path.Combine(_pluginDir, "resources"), "bsnesscreentex_*.atex").ToList().ForEach(File.Delete);
		Directory.GetFiles(Path.Combine(_pluginDir, "resources"), "bsnesscreen_*.avfx").ToList().ForEach(File.Delete);
		GC.SuppressFinalize(this);
	}

	//TO BE REMOVED JUST FOR DEBUG
    private List<Dictionary<string, string>> _tempGamePaths = [];
    private Dictionary<string, string> TempCopyGamePaths(Dictionary<string, string> gamePaths)
    {
		var finalPaths = new Dictionary<string, string>();
		
        var getDir = new GetModDirectory(Services.PluginInterface);
        string dir = getDir.Invoke();
        string alphachanneltempdir = Path.Combine(dir, "AlphaChannelTemp");
        Directory.CreateDirectory(Path.Combine(dir, "AlphaChannelTemp"));
        foreach(string ingamePath in gamePaths.Keys)
        {
			string realPath = gamePaths[ingamePath];
			string newPath = Path.Combine(alphachanneltempdir, Path.GetFileName(realPath));
			if (!File.Exists(newPath))
			{
            	File.Copy(realPath, newPath);
			}
			finalPaths.Add(ingamePath, newPath);
        }
		_tempGamePaths.Add(finalPaths);

		return finalPaths;
    }

	public Dictionary<string, string> LoadPenumbraScreenResources()
	{
		Dictionary<string, string> paths = [];

		string oldPath = Path.Combine(_pluginDir, "resources", "alphachannelscreentex.atex");
		if (File.Exists(oldPath))
		{
			string path = Path.Combine(_pluginDir, "resources", "alphachannelscreentex_"+Plugin.PluginSessionGUID+".atex");
			File.Copy(oldPath, path);
			paths.Add("chara/monster/m7002/obj/body/b0001/vfx/texture/alphachannelscreentex.atex", path);
		}
		else
		{
			throw new FileNotFoundException($"Required resource not found: {oldPath}");
		}

		oldPath = Path.Combine(_pluginDir, "resources", "alphachannelscreen.avfx");
		if (File.Exists(oldPath))
		{
			string path = Path.Combine(_pluginDir, "resources", "alphachannelscreen_"+Plugin.PluginSessionGUID+".avfx");
			File.Copy(oldPath, path);
			paths.Add("chara/monster/m7002/obj/body/b0001/vfx/texture/alphachannelscreen_"+Plugin.PluginSessionGUID+".avfx", path);
		}
		else
		{
			throw new FileNotFoundException($"Required resource not found: {oldPath}");
		}

		oldPath = Path.Combine(_pluginDir, "resources", "bsnesscreen.avfx");
		if (File.Exists(oldPath))
		{
			string path = Path.Combine(_pluginDir, "resources", "bsnesscreen_"+Plugin.PluginSessionGUID+".avfx");
			File.Copy(oldPath, path);
			paths.Add("chara/monster/m7002/obj/body/b0001/vfx/texture/bsnesscreen_"+Plugin.PluginSessionGUID+".avfx", path);
		}
		else
		{
			throw new FileNotFoundException($"Required resource not found: {oldPath}");
		}

		oldPath = Path.Combine(_pluginDir, "resources", "bsnesscreentex.atex");
		if (File.Exists(oldPath))
		{
			string path = Path.Combine(_pluginDir, "resources", "bsnesscreentex_"+Plugin.PluginSessionGUID+".atex");
			File.Copy(oldPath, path);
			paths.Add("chara/monster/m7002/obj/body/b0001/vfx/texture/bsnesscreentex.atex", path);
		}
		else
		{
			throw new FileNotFoundException($"Required resource not found: {oldPath}");
		}

		return TempCopyGamePaths(paths); //just return paths after the fix
	}
	public Dictionary<string, string> LoadPenumbraModResources()
	{
		Dictionary<string, string> paths = new () {
			{"chara/monster/m7002/animation/a0001/bt_common/resident/monster.pap", "carbuncle/monster.pap"}, //Carbuncle Files
			{"chara/monster/m7002/obj/body/b0001/material/v0001/mt_m7002b0001_a.mtrl", "carbuncle/mt_m7002b0001_a.mtrl"},
			{"chara/monster/m7002/obj/body/b0001/material/v0002/mt_m7002b0001_a.mtrl", "carbuncle/mt_m7002b0001_a.mtrl"},
			{"chara/monster/m7002/obj/body/b0001/material/v0003/mt_m7002b0001_a.mtrl", "carbuncle/mt_m7002b0001_a.mtrl"},
			{"chara/monster/m7002/obj/body/b0001/material/v0004/mt_m7002b0001_a.mtrl", "carbuncle/mt_m7002b0001_a.mtrl"},
			{"chara/monster/m7002/obj/body/b0001/material/v0005/mt_m7002b0001_a.mtrl", "carbuncle/mt_m7002b0001_a.mtrl"},
			{"chara/monster/m7002/obj/body/b0001/material/v0006/mt_m7002b0001_a.mtrl", "carbuncle/mt_m7002b0001_a.mtrl"},
			{"chara/monster/m7002/obj/body/b0001/model/m7002b0001.mdl", "carbuncle/m7002b0001.mdl"},
			{"chara/monster/m7002/obj/body/b0001/texture/tv_d.tex", "carbuncle/tv_d.tex"},
			{"chara/monster/m7002/obj/body/b0001/texture/tv_id.tex", "carbuncle/tv_id.tex"},
			{"chara/monster/m7002/obj/body/b0001/texture/tv_n.tex", "carbuncle/tv_n.tex"},
			{"chara/monster/m7002/obj/body/b0001/texture/tv_s.tex", "carbuncle/tv_id.tex"},
			{"chara/monster/m7002/obj/body/b0001/vfx/texture/flas001bt.atex", "carbuncle/flas001bt.atex"},
			{"chara/monster/m7002/obj/body/b0001/vfx/texture/pk_x001a_h.atex", "carbuncle/pk_x001a_h.atex"},
			{"chara/monster/m7002/obj/body/b0001/vfx/texture/glow002bf.atex", "carbuncle/glow002bf.atex"},
			{"chara/monster/m7002/obj/body/b0001/vfx/texture/flas001ct.atex", "carbuncle/flas001bt.atex"}
		};
		foreach(string key in paths.Keys)
		{
			string fullPath = Path.Combine(_pluginDir, "resources", paths[key]);
			if (!File.Exists(fullPath))
			{
				throw new FileNotFoundException($"Required resource not found: {fullPath}");
			}
			else
			{
				paths[key] = Path.Combine(_pluginDir, "resources", paths[key]);
			}
		}

		return TempCopyGamePaths(paths); //just return paths after the fix
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
		try{
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
		catch
		{
			return [string.Empty, string.Empty];
		}
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

	internal static class NativeLoader
	{
		private static Plugin? _plugin;

		public static void Register(Plugin plugin)
		{
			_plugin = plugin;
			NativeLibrary.SetDllImportResolver(typeof(NativeLoader).Assembly, Resolve);
		}

		private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
		{
			switch (name)
			{
				case "libmpv-2":
					return TryLoad(_plugin?.AssemblyLocationMPV, "MPV");
				default:
					return IntPtr.Zero;
			}
		}

		private static IntPtr TryLoad(string? location, string tag)
		{
			if (location != null && NativeLibrary.TryLoad(location, out nint handle))
			{
				return handle;
			}
			Services.Log.Error($"[{tag}] Failed to load native lib from: {location}");
			return IntPtr.Zero;
		}
	}
}
