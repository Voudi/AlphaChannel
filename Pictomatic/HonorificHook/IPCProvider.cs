using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Ipc;
using Newtonsoft.Json;
using System.Reflection;

namespace Pictomatic;

public static class IpcProvider
{
	public const uint MajorVersion = 3;
	public const uint MinorVersion = 1;

	public static bool Initialized = false;

	private static ICallGateProvider<int, string, object>? SetCharacterTitle;
	private static Action<int, string>? OldDelegate;

	internal static void Init(Plugin plugin)
	{
		SetCharacterTitle = Services.PluginInterface.GetIpcProvider<int, string, object>($"Honorific.{nameof(SetCharacterTitle)}");
		
		try
		{
			var ipcCallgateChannelMeta = SetCharacterTitle?.GetType()?.BaseType?.GetField("<Channel>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
			var ipcCallgateChannelObj = ipcCallgateChannelMeta?.GetValue(SetCharacterTitle);
			var ipcCallgateChannelActionMeta = ipcCallgateChannelObj?.GetType().GetField("<Action>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
			var ipcCallgateChannelActionMetaObj = (Delegate?)ipcCallgateChannelActionMeta?.GetValue(ipcCallgateChannelObj);
			if(ipcCallgateChannelActionMetaObj is not null)
			{
				OldDelegate = (Action<int, string>)ipcCallgateChannelActionMetaObj;
				if (OldDelegate is null)
					Services.Log.Warning("OldDelegate is null");
				var proxyAction = new Action<int, string>((characterI, titleDataJson) =>
				{
					try
					{
						var character = Services.Objects.Length > characterI && characterI >= 0 ? Services.Objects[characterI] : null;
						if (character is not IPlayerCharacter playerCharacter) return;
						if (titleDataJson == string.Empty) return;
						var titleData = JsonConvert.DeserializeObject<TitleData>(titleDataJson);
						if (titleData == null) return;
						plugin.UpdateTitle(playerCharacter.EntityId, titleData);
					}
					catch (Exception ex)
					{
						Services.Log.Error(ex, $"Error handling {nameof(SetCharacterTitle)} IPC.");
					}

					ipcCallgateChannelActionMetaObj?.DynamicInvoke(characterI, titleDataJson);

					// Optionally do something after the original action
				});

				SetCharacterTitle?.RegisterAction(proxyAction);
			}
			else
			{
				Services.Log.Error("Could not Intercept Hook, has Dalamud been updated? IPCProvider.cs L34");
			}
			
		}
		catch(Exception ex)
		{
			Services.Log.Error(ex.Message + ex.StackTrace);
		}
		Initialized = true;
	}
	internal static void DeInit()
	{
		if(OldDelegate is not null)
		SetCharacterTitle?.RegisterAction(OldDelegate);
	}
}