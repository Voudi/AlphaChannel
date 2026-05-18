using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AlphaChannel;

internal sealed class Compatibility
{
	private readonly Plugin _plugin;
	public bool ModExists { get; private set; }
	public bool InstalledMod { get; private set; }

	public Compatibility(Plugin plugin)
	{
		_plugin = plugin;
	}

	public static bool IsRunningUnderWine()
	{
		// Wine sets this registry key
		try
		{
			using var key = Registry.LocalMachine.OpenSubKey(@"Software\Wine");
			return key != null;
		}
		catch
		{
			return false;
		}
	}

	public void CheckForUpdates()
	{
		string? mpvLocation = _plugin.LibResources.GetLocationMPV();
		if (mpvLocation != null)
		{
			_plugin.AssemblyLocationMPV = mpvLocation;
		}
		string? ytdlpLocation = _plugin.LibResources.GetLocationYTDLP();
		if (ytdlpLocation != null)
		{
			_plugin.AssemblyLocationYTDLP = ytdlpLocation;
		}

		_plugin.LibResources.CheckMPVAsync().ContinueWith(task =>
		{
			if (task.IsCompletedSuccessfully)
			{
				Services.Log.Debug("Success checking for MPV updates");
			}
			else
			{
				Services.Log.Error("Failed to check for MPV updates: " + task.Exception?.ToString());
			}
		});
		_plugin.LibResources.CheckYTDLPAsync().ContinueWith(task =>
		{
			if (task.IsCompletedSuccessfully)
			{
				Services.Log.Debug("Success checking for YTDLP updates");
			}
			else
			{
				Services.Log.Error("Failed to check for YTDLP updates: " + task.Exception?.ToString());
			}
		});
	}

	public async Task<bool> CheckTVMod()
	{
		string apiUrl = "http://localhost:42069/api";

		try
		{
			System.Net.Http.HttpResponseMessage responseMods = await Plugin.HttpClient.GetAsync(apiUrl + "/mods");

			responseMods.EnsureSuccessStatusCode();

			string responseModsBody = await responseMods.Content.ReadAsStringAsync();

			ModExists = responseModsBody.Contains("AlphaChannelTV");
		}
		catch (Exception ex)
		{
			Services.Log.Debug("Error:" + ex.Message);
		}

		return ModExists;
	}

	public async void InstallTVMod()
	{
		string apiUrl = "http://localhost:42069/api";

		try
		{
			if (!await CheckTVMod())
			{
				System.Net.Http.StringContent content = new(JsonSerializer.Serialize(
					new
					{
						Path = _plugin.GetModPath()
					}
				), Encoding.UTF8, "application/json");

				Services.Log.Debug("Installing mod: " + _plugin.GetModPath());
				System.Net.Http.HttpResponseMessage responseInstall = await Plugin.HttpClient.PostAsync(apiUrl + "/installmod", content);

				responseInstall.EnsureSuccessStatusCode();

				ModExists = true;
				InstalledMod = true;
			}
		}
		catch (Exception ex)
		{
			Services.Log.Debug("Error:" + ex.Message);
		}
	}
}
