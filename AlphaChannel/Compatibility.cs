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

	public static bool IsRunningUnderWine() //Will be used for windows hardware acceleration down the road
	{
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
}
