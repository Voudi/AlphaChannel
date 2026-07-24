using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Dalamud.Utility;
using Penumbra.Api.IpcSubscribers;
using SharpCompress.Archives;
using SharpCompress.Common;
using Newtonsoft.Json.Linq;

namespace AlphaChannel;

internal sealed class Resources : IDisposable
{
	private readonly HttpClient _httpClient;
	private readonly Plugin _plugin;

	internal string[] MpvCheckResult { get; private set; } = [string.Empty, string.Empty];
	internal string[] YtdlpCheckResult { get; private set; } = [string.Empty, string.Empty];
	private long _ntpTimeOffset;
	private long _sysTimeOffset;

	internal long CurrentTimeNTPNormalizedMilliseconds => _ntpTimeOffset > 0 ? _ntpTimeOffset + (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _sysTimeOffset) : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();


	internal Resources(Plugin plugin)
	{
		_httpClient = new HttpClient();
		_httpClient.DefaultRequestHeaders.Add("User-Agent", "AlphaChannelUpdater/1.0");
		_plugin = plugin;

		Initialize();
	}

	public void Dispose()
	{
		_httpClient.Dispose();

		GC.SuppressFinalize(this);
	}

	private void Initialize()
	{
		if(!Directory.Exists(Path.Combine(_plugin.ConfigDir, "roms")))
		{
			Directory.CreateDirectory(Path.Combine(_plugin.ConfigDir, "roms"));
		}
		_=GetNtpUtcAsync().ContinueWith(task =>
		{
			//Set NTP time
			if (task.IsCompletedSuccessfully)
			{
				_ntpTimeOffset = task.GetResultSafely();
				Services.Log.Debug("Received NTP Time Offset: " + (_ntpTimeOffset - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + " ms.");
			}
			_sysTimeOffset = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}).ContinueWith(_ =>
		{
			//Check for MPV Updates
			_plugin.LibResources.CheckMPVAsync().ContinueWith(task =>
			{
				if (!task.IsCompletedSuccessfully)
				{
					Services.Log.Error("Failed to check for MPV updates: " + task.Exception?.ToString());
				}
			});
		}).ContinueWith(_=>
		{
			//Check for YTDLP Updates
			_plugin.LibResources.CheckYTDLPAsync().ContinueWith(task =>
			{
				if (!task.IsCompletedSuccessfully)
				{
					Services.Log.Error("Failed to check for YTDLP updates: " + task.Exception?.ToString());
				}
			});
		});
	}

	internal string? GetLocationMPV()
	{
		string filenameStart = "mpv-dev-lgpl-x86_64-";
		string? dir = Directory.GetDirectories(_plugin.ConfigDir, $"{filenameStart}*").FirstOrDefault();
		if (dir != null)
		{
			return dir + "/libmpv-2.dll";
		}
		else
		{
			return null;
		}
	}

	internal string? GetLocationYTDLP()
	{
		string filenameStart = "yt-dlp";
		string? dir = Directory.GetDirectories(_plugin.ConfigDir, $"{filenameStart}*").FirstOrDefault();
		if (dir != null)
		{
			return dir + "/yt-dlp.exe";
		}
		else
		{
			return null;
		}
	}

	internal string? GetLocationSNES9X()
	{
		string directoryName = "snes9x";
		string? dir = Directory.GetDirectories(_plugin.ConfigDir, $"{directoryName}*").FirstOrDefault();
		if (dir != null)
		{
			string file = Path.Combine(_plugin.ConfigDir, directoryName, "snes9x_libretro.dll");
			if(File.Exists(file))
			{
				return file;
			}
		}
		else
		{
			Directory.CreateDirectory(Path.Combine(_plugin.ConfigDir, "snes9x"));
		}
		
		return null;
	}

	private async Task CheckMPVAsync()
	{
		string filenameStart = "mpv-dev-lgpl-x86_64-";
		string filenameEnd = ".7z";
		string url = "https://api.github.com/repos/zhongfly/mpv-winbuild/releases/latest";
		MpvCheckResult = await CheckForUpdateAsync(_plugin.ConfigDir, filenameStart, filenameEnd, url);
	}
	private async Task CheckYTDLPAsync()
	{
		string filenameStart = "yt-dlp.exe";
		string filenameEnd = ".exe";
		string url = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
		YtdlpCheckResult = await CheckForUpdateAsync(_plugin.ConfigDir, filenameStart, filenameEnd, url);
	}
	internal async Task<bool> DownloadMPVAsync()
	{
		string filenameStart = "mpv-dev-lgpl-x86_64-";
		string filenameEnd = ".7z";
		string downloadURL = MpvCheckResult[0];
		string folderName = MpvCheckResult[1];
		return await UpdateAsync(_plugin.ConfigDir, filenameStart, filenameEnd, downloadURL, folderName);
	}
	internal async Task<bool> DownloadYTDLPAsync()
	{
		string filenameStart = "yt-dlp";
		string filenameEnd = ".exe";
		string downloadURL = YtdlpCheckResult[0];
		string folderName = YtdlpCheckResult[1];
		return await UpdateAsync(_plugin.ConfigDir, filenameStart, filenameEnd, downloadURL, folderName);
	}
	private async Task<string[]> CheckForUpdateAsync(string configDir, string nameStartsWith, string nameEndsWith, string checkURL)
	{
		try{
			string json = await _httpClient.GetStringAsync(checkURL);
			var doc = JObject.Parse(json);
			long remoteId = doc["id"]!.Value<long>();
			var asset = doc["assets"]!
				.First(a => a["name"]!.Value<string>()!
					.StartsWith(nameStartsWith, StringComparison.Ordinal) &&
					a["name"]!.Value<string>()!.EndsWith(nameEndsWith, StringComparison.Ordinal));

			string assetName = asset["name"]!.Value<string>()!;
			string folderName = assetName.Replace(nameEndsWith, "") + "_" + remoteId;

			string localFolder = Path.Combine(configDir, folderName);

			if (Directory.Exists(localFolder))
			{
				return [string.Empty, folderName]; //Already up to date
			}

			string downloadURL = asset["browser_download_url"]!.Value<string>()!;
			Services.Log.Warning("Found Update: " + downloadURL);
			return [downloadURL, folderName];
		}
		catch
		{
			return [string.Empty, string.Empty];
		}
	}

	private async Task<bool> UpdateAsync(string configDir, string nameStartsWith, string nameEndsWith, string downloadURL, string folderName)
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
				string localFolder = Path.Combine(configDir, Path.GetRandomFileName());
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

				foreach (string dir in Directory.GetDirectories(configDir, $"{nameStartsWith}*"))
				{
					Directory.Delete(dir, recursive: true);
				}

				if (Directory.Exists(Path.Combine(configDir, folderName))) //Super weird but lets just do this to be safe
				{
					foreach (string file in Directory.GetFiles(localFolder, "*", SearchOption.AllDirectories))
					{
						string relative = Path.GetRelativePath(localFolder, file);
						string target = Path.Combine(Path.Combine(configDir, folderName), relative);
						Directory.CreateDirectory(Path.GetDirectoryName(target)!);
						File.Copy(file, target, overwrite: true);
					}
				}
				else
				{
					Directory.Move(localFolder, Path.Combine(configDir, folderName));
				}
			}
			else
			{
				foreach (string dir in Directory.GetDirectories(configDir, $"{nameStartsWith}*"))
				{
					Directory.Delete(dir, recursive: true);
				}

				string localFolder = Path.Combine(configDir, folderName);
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

	internal async Task<bool> DownloadSNES9XAsync()
	{
		try
		{
			string directoryName = "snes9x";
			string temp = Path.GetTempFileName() + ".zip";
			var response = await _httpClient.GetAsync("https://buildbot.libretro.com/nightly/windows/x86_64/latest/snes9x_libretro.dll.zip", HttpCompletionOption.ResponseHeadersRead);
			await using (var fs = File.OpenWrite(temp))
			{
				await response.Content.CopyToAsync(fs);
			}

			string localFolder = Path.Combine(_plugin.ConfigDir, directoryName);
			Directory.CreateDirectory(localFolder);
			using (var archive = ArchiveFactory.OpenArchive(temp))
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

			File.Delete(temp);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private async Task<long> GetNtpUtcAsync(string server = "pool.ntp.org")
	{
		try
		{
			byte[] ntpData = new byte[48];
			ntpData[0] = 0x1B;

			var addresses = await Dns.GetHostAddressesAsync(server);
			var ep = new IPEndPoint(addresses[0], 123);

			using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			socket.ReceiveTimeout = 3000;
			await socket.ConnectAsync(ep);
			await socket.SendAsync(ntpData);
			await socket.ReceiveAsync(ntpData);

			ulong intPart = ((ulong)ntpData[40] << 24) | ((ulong)ntpData[41] << 16) | ((ulong)ntpData[42] << 8) | ntpData[43];
			ulong fracPart = ((ulong)ntpData[44] << 24) | ((ulong)ntpData[45] << 16) | ((ulong)ntpData[46] << 8) | ntpData[47];
			ulong ms = intPart * 1000 + fracPart * 1000 / 0x100000000L;
			var dto = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds((long)ms);
        	return dto.ToUnixTimeMilliseconds();
		}
		catch
		{
			return 0;
		}
	}

	internal static class NativeLoader
	{
		private static Plugin? _plugin;

		internal static void Register(Plugin plugin)
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
