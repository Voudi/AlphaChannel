using System.Reflection;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Ipc;
using Newtonsoft.Json;

namespace AlphaChannel;

public static class IpcProvider
{
	public static bool Initialized { get; private set; }

	private static ICallGateProvider<int, string, object>? _setCharacterTitle;
	private static Action<int, string>? _oldDelegate;

	internal static void Init(Plugin plugin)
	{
		_setCharacterTitle = Services.PluginInterface.GetIpcProvider<int, string, object>($"Honorific.SetCharacterTitle");

		try
		{
			var ipcCallgateChannelMeta = _setCharacterTitle?.GetType()?.BaseType?.GetField("<Channel>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
			object? ipcCallgateChannelObj = ipcCallgateChannelMeta?.GetValue(_setCharacterTitle);
			var ipcCallgateChannelActionMeta = ipcCallgateChannelObj?.GetType().GetField("<Action>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
			var ipcCallgateChannelActionMetaObj = (Delegate?)ipcCallgateChannelActionMeta?.GetValue(ipcCallgateChannelObj);
			if (ipcCallgateChannelActionMetaObj is not null)
			{
				_oldDelegate = (Action<int, string>)ipcCallgateChannelActionMetaObj;
				if (_oldDelegate is null)
				{
					Services.Log.Warning("[HH] OldDelegate is null");
				}

				var proxyAction = new Action<int, string>((characterI, titleDataJson) =>
				{
					try
					{
						var character = Services.Objects.Length > characterI && characterI >= 0 ? Services.Objects[characterI] : null;
						if (character is not IPlayerCharacter playerCharacter)
						{
							return;
						}

						if (titleDataJson == string.Empty)
						{
							return;
						}

						var titleData = JsonConvert.DeserializeObject<TitleData>(titleDataJson);
						if (titleData == null)
						{
							return;
						}

						plugin.UpdateTitle(playerCharacter.EntityId, titleData);
					}
					catch (Exception ex)
					{
						Services.Log.Error(ex, $"Error handling {nameof(_setCharacterTitle)} IPC.");
					}

					ipcCallgateChannelActionMetaObj?.DynamicInvoke(characterI, titleDataJson);

					// Optionally do something after the original action
				});

				Services.Log.Debug("[HH] Successfully registered Honorific Hook");
				_setCharacterTitle?.RegisterAction(proxyAction);

				Initialized = true;
			}
			else
			{
				Services.Log.Error("[HH] Could not Intercept Honorific Hook, has Dalamud been updated?");
			}

		}
		catch (Exception ex)
		{
			Services.Log.Error(ex.Message + ex.StackTrace);
		}
	}
	internal static void DeInit()
	{
		if (_oldDelegate is not null)
		{
			_setCharacterTitle?.RegisterAction(_oldDelegate);
		}
	}
}
