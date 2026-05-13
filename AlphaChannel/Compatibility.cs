using AlphaChannel;
using Microsoft.Win32;
using System.Text;
using System.Text.Json;

class Compatibility
{
    private Plugin _plugin;
    public bool _modexists {get; private set;} = false;
	public bool _installedmod {get; private set;} = false;

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
		var mpvLocation = _plugin.LibResources.GetLocationMPV();
		if(mpvLocation != null)
		{
			_plugin.AssemblyLocationMPV = mpvLocation;
		}
		var ytdlpLocation = _plugin.LibResources.GetLocationYTDLP();
		if(ytdlpLocation != null)
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
		var apiUrl = "http://localhost:42069/api";

        try
        {
            var responseMods = await Plugin.HTTPCLIENT.GetAsync(apiUrl + "/mods");

            responseMods.EnsureSuccessStatusCode();

            var responseModsBody = await responseMods.Content.ReadAsStringAsync();

			_modexists = responseModsBody.Contains("AlphaChannelTV");
        }
        catch (Exception ex)
        {
            Services.Log.Debug("Error:" + ex.Message);
        }

		return _modexists;
    }

    public async void InstallTVMod()
	{
        var apiUrl = "http://localhost:42069/api";

        try
        {
            if (!await CheckTVMod())
            {
                var content = new StringContent(JsonSerializer.Serialize(
                    new
                    {
                        Path = _plugin.GetModPath()
                    }
                ), Encoding.UTF8, "application/json");

                Services.Log.Debug("Installing mod: " + _plugin.GetModPath());
                var responseInstall = await Plugin.HTTPCLIENT.PostAsync(apiUrl + "/installmod", content);

                responseInstall.EnsureSuccessStatusCode();

				_modexists = true;
                _installedmod = true;
            }
        }
        catch (Exception ex)
        {
            Services.Log.Debug("Error:" + ex.Message);

        }
    }
}