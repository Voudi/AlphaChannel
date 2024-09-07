using System.Numerics;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Pictomatic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using ImGuiNET;
using SharpDX.Direct3D11;
using SharpDX.Direct3D;
using static FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkHistory.Delegates;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.Interop;
using System.Collections.Immutable;
using Honorific;
using static System.Net.WebRequestMethods;
using System.Linq;
using System.Collections.Generic;
using Dalamud.Interface;
using System.ComponentModel.DataAnnotations;
using NAudio.Wasapi;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using System.Security.Cryptography;

public class MainWindow : Window, IDisposable
{
	private readonly Dictionary<uint, IntPtr> _currentOwners = []; //Playerpointer, CompanionDrawpointer
	private readonly Dictionary<uint, String> _currentURLs = []; //Playerpointer, URL
	private readonly Dictionary<string, uint> _currentOverlays = []; //InlayconfigName, Playerpointer
	private readonly Dictionary<string, Guid> _currentOverlayGuids = []; //InlayconfigName, Overlay GUID
	private readonly Dictionary<string, Texture2D> _currentSharedTextures = []; //InlayconfigName, SharedTexturePointer
	private readonly Dictionary<uint, bool> _currentToggle = []; //Playerpointer, Toggle
	private readonly HashSet<int> _currentSubProcesses = []; //ProcessId

	private static readonly byte[] _blankCanvas = new byte[16777216];

	private Plugin _plugin;

	public delegate void URLShortenerCallback(string result);
	public delegate void URLShortenerErrorCallback(string result);
	public delegate void URLFetchCallback(string result);

	//Render Vars
	private String buttonToggleTV => TVTurnedOn ? ">Turn Off<" : ">Turn On<";
	bool TVTurnedOn = false;
	private String inputURL = "https://w2g.tv/en/room/?w2g_init=1&w2g_nick=Guest&room_id=6cf6i1hlqwlv05y83n";
	private String shortenedInputURL = "";
	private String lastInputURL = "";
	private String lastInputCSS = "";
	private String inputCSS = "#video_container";//"#divid";
	float volume = 0.5f;
	private bool volumeEnabled = false;

	public unsafe MainWindow(Plugin plugin)
        : base("Pictomatic remote", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {

		SizeConstraints = new WindowSizeConstraints
		{
			MinimumSize = new Vector2(375, 330),
			MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
		};

		this._plugin = plugin;
	}

	private async void NavigateNewURL(uint ownerId, string title)
	{
		_currentURLs[ownerId] = title;
		if (ownerId != Services.ClientState.LocalPlayer.EntityId && (!_currentToggle.TryGetValue(ownerId, out var toggle) || !toggle))
		{
			return;
		}
		if (ownerId == Services.ClientState.LocalPlayer.EntityId && !TVTurnedOn)
		{
			return;
		}
		if (title.Length < 7 || !title.StartsWith("picto:"))
		{
			return;
		}
		var url = "https://is.gd/" + title.Substring("picto:".Length);
		await FetchURLData(url, response => {
			
			if (response.Contains("picto:"))
			{
				var trueUrl = response.Split("picto:")[0];
				var trueCSS = response.Split("picto:")[1];

				_plugin.NavigatePictomaticWindow("pictomatic"+ownerId, trueUrl, trueCSS);
			}
		});
	}


	private unsafe void CheckAllTVs()
	{
		RefreshOverlayVolume();

		CheckTitles();
		
		List<uint> visitedTvs = new List<uint>();
		var npcList = Services.ObjectTable.Where(x => x is IBattleNpc).Cast<IBattleNpc>().OrderBy(x => x.YalmDistanceX);
		foreach(var item in npcList)
		{
			if(item.Name.TextValue == "Carbuncle")
			{
				var character = (Character*)item.Address;
				if (character != null)
				{
					if(character->DrawObject != null)
					if (character->DrawObject->GetObjectType() == ObjectType.CharacterBase)
					{
						try {
							var tvDraw = (CharacterBase*)character->DrawObject;
							if (tvDraw->Models[0]->Materials[1] is not null)
								if (tvDraw->Models[0]->Materials[1]->TextureCount >= 4)
									if (tvDraw->Models[0]->Materials[1]->Textures[3].Texture is not null)
										if (tvDraw->Models[0]->Materials[1]->Textures[3].Texture->Texture is not null)
											{
												var ownerId = character->CompanionOwnerId;
												visitedTvs.Add(ownerId);
												CheckTV(tvDraw, ownerId);
											}
						}
						catch(Exception e){}
					}
				}
			}
		}

		//Remove unvisited TVs
		_currentOwners.Where(owner => !visitedTvs.Contains(owner.Key)).Select(owner => owner.Key).ToList().ForEach(ownerId =>
		{
			TryRemoveTV(ownerId);
		});
	}

	private unsafe void CheckTV(CharacterBase* tvDraw, uint ownerId)
	{
		IntPtr ptr = (IntPtr)tvDraw;
		if (_currentSharedTextures.TryGetValue("pictomatic" + ownerId, out _) && _currentOwners.TryGetValue(ownerId, out _))
		{
			AssignTextureToTV(ownerId);
		}
		else if(!_currentOwners.TryGetValue(ownerId, out _))
		{
			AddOtherPlayerTV(ptr, ownerId);
		}
	}
	private unsafe void AddOtherPlayerTV(IntPtr ptr, uint ownerId)
	{
		var TV = (CharacterBase*)ptr;
		//TV->Models[0]->Materials[1]->Textures[3].Texture->Texture->InitializeContents((void*)Bptr);

		InlayConfiguration? config = _plugin.AddPictomaticWindow(ownerId);
		_currentOverlays.Add(config.Name, ownerId);
		_currentOverlayGuids.Add(config.Name, config.Guid);
		_currentToggle[ownerId] = false;
		_currentOwners.Add(ownerId, ptr);
		_currentURLs.Add(ownerId, "");
	}

	private unsafe void ReassignTextureForTV(IntPtr TVaddr, Texture2D textureSource)
	{
		var TV = (CharacterBase*)TVaddr;

		ShaderResourceView view = new(DxHandler.Device, textureSource, new ShaderResourceViewDescription { Format = textureSource.Description.Format, Dimension = ShaderResourceViewDimension.Texture2D, Texture2D = { MipLevels = textureSource.Description.MipLevels } });

		// Obtain the native pointers
		void* D3D11Texture2D = (void*)textureSource.NativePointer;
		TV->Models[0]->Materials[1]->Textures[3].Texture->Texture->D3D11Texture2D = D3D11Texture2D;
		void* D3D11ShaderResourceView = (void*)view.NativePointer;
		TV->Models[0]->Materials[1]->Textures[3].Texture->Texture->D3D11ShaderResourceView = D3D11ShaderResourceView;
	}

	public unsafe void UpdateSharedDXTexture(Texture2D _textureSource, InlayConfiguration config)
	{
		_currentSharedTextures[config.Name] = _textureSource;
	}

	private void AssignTextureToTV(uint ownerId)
	{
		var fullName = "pictomatic" + ownerId;
		if (_currentOverlays.TryGetValue(fullName, out var owner))
		{
			if (_currentOwners.TryGetValue(owner, out var TVaddr))
			{
				//Check if theres a SharedTexture waiting in the Pipeline for this Owner
				if(_currentSharedTextures.TryGetValue(fullName, out var texture))
				{
					ReassignTextureForTV(TVaddr, texture);
					_currentSharedTextures.Remove(fullName);
				}
			}
		}
	}

	public void Dispose()
	{
		//TODO: Kill all Overlays
	}

	public unsafe override void Draw()
	{
		ImGui.Text("");
		var ownTVToggled = true;// TODO: only make it available for player pet
		if(ownTVToggled )
		{
			ImGui.Text("Start your TV: ");
			ImGui.SameLine();
			if (ImGui.Button(buttonToggleTV))
			{
				if (!TVTurnedOn)
				{
					TurnOnOwnTV();
				}
				else
				{
					TurnOffTV();
				}
			}
		}
		else ImGui.Text("");
		
		if (ownTVToggled)
		{
			ImGui.Text("Connection Settings:");
			if (TVTurnedOn)
			{
				ImGui.BeginDisabled();
			}
			ImGui.Text("  URL  ");
			ImGui.SameLine();
			ImGui.InputText("##URL", ref inputURL, 1000, ImGuiInputTextFlags.NoHorizontalScroll);
			ImGui.Text("  CSS  ");
			ImGui.SameLine();
			ImGui.InputText("##CSS", ref inputCSS, 1000, ImGuiInputTextFlags.NoHorizontalScroll);
			if (TVTurnedOn)
			{
				ImGui.EndDisabled();
			}
		}
		else
		{
			ImGui.Text("");
			ImGui.Text("");
			ImGui.Text("");
		}

		ImGui.Text("");
		ImGui.Text("");

		if (!volumeEnabled)
			ImGui.BeginDisabled();
		ImGui.Text("");
		ImGui.Text("  -");
		ImGui.SameLine();
		if (ImGui.SliderFloat("##Volume", ref volume, 0.0f, 1.0f, "Volume"))
		{
			SetOverlayVolume(volume);
		}
		ImGui.SameLine();
		ImGui.Text("+");
		ImGui.Text("");
		if (!volumeEnabled)
			ImGui.EndDisabled();

		ImGui.Text("TV List:");
		var npcList = Services.ObjectTable.Where(x => x is IPlayerCharacter).Cast<IPlayerCharacter>().OrderBy(x => x.YalmDistanceX);
		foreach (var item in npcList)
		{
			if (item.EntityId != Services.ClientState.LocalPlayer.EntityId && _currentOwners.TryGetValue(item.EntityId, out _))
			{
				if (!_currentURLs.TryGetValue(item.EntityId, out var url)) continue;
				var validUrl = url.StartsWith("picto:");
				ImGui.Text(item.Name.TextValue + " ");
				ImGui.SameLine();
				_currentToggle.TryGetValue(item.EntityId, out var toggle);
				if (!validUrl) { ImGui.BeginDisabled();  }
				if (ImGui.Button((validUrl ? (toggle ? ">Turn Off<##" : ">Turn On<##") : "-Turned Off By Host-##") + item.EntityId))
				{
					if (!toggle)
					{
						if (_currentURLs.TryGetValue(item.EntityId, out _))
						{
							_currentToggle[item.EntityId] = true;
							NavigateNewURL(item.EntityId, url);
						}
					}
					else
					{
						_currentToggle[item.EntityId] = false;
						_plugin.NavigatePictomaticWindow("pictomatic" + item.EntityId, "about:blank", "");
					}
				}
				if (!validUrl) { ImGui.EndDisabled(); }

			}
		}

		/*
		ImGui.Text("Debug info: " );
		foreach (var item in _currentSubProcesses)
		{
			ImGui.Text("Guid for " + item);
		}
		
		foreach (var item in _currentURLs)
		{
			ImGui.Text("URL for " + item.Key + " " + item.Value);
		}

		foreach (var item in _currentOwners)
		{
			ImGui.Text("Owner for " + item.Key + " " + item.Value);
		}

		foreach (var item in _currentToggle)
		{
			ImGui.Text("Toggle for " + item.Key + " " + item.Value);
		}

		
		var npcList = Services.ObjectTable.Where(x => x is IPlayerCharacter).Cast<IPlayerCharacter>().OrderBy(x => x.YalmDistanceX);
		foreach (var item in npcList)
		{
			ImGui.Text("NPC: " + item.Name);
			ImGui.Text("ID: " + item.GameObjectId); //Player ID
		}
		
		var carbList = Services.ObjectTable.Where(x => x is IBattleChara).Cast<IBattleChara>().OrderBy(x => x.YalmDistanceX);
		foreach (var item in carbList)
		{
			var character = (Character*)item.Address; 
			ImGui.Text("ID: " + character->CompanionOwnerId); //Carbuncle Owner ID
		}
		
		
		unsafe
		{
			var ratkm = Framework.Instance()->GetUIModule()->GetRaptureAtkModule();
			for (var i = 0; i < 100 && i < ratkm->NameplateInfoCount; i++)
			{
				var npi = ratkm->NamePlateInfoEntries.GetPointer(i);
				if (npi->ObjectId.ObjectId == 0) continue;
				ImGui.Text("Title: for " + npi->ObjectId.ObjectId + " " + MemoryHelper.ReadSeString(&npi->DisplayTitle));
			}
		}
		*/
	}

	private unsafe void TryRemoveTV(uint ownerId)
	{
		if(_currentOwners.TryGetValue(ownerId, out var TV))
		{
			_currentToggle.Remove(ownerId);
			if (_currentOverlays.TryGetValue("pictomatic" + ownerId, out _))
			{
				_plugin.RemovePictomaticWindow(ownerId);
				_currentOverlays.Remove("pictomatic" + ownerId);
				_currentOverlayGuids.Remove("pictomatic" + ownerId);
			}
			
			_currentOwners.Remove(ownerId);
			fixed (byte* Bptr = _blankCanvas) //Put a Black Canvas on the TV
			{
				var TVdraw = (CharacterBase*)TV;
				try
				{
					TVdraw->Models[0]->Materials[1]->Textures[3].Texture->Texture->InitializeContents((void*)Bptr);
				}
				catch(Exception)
				{ 
					//Ignore Repaint
				}
			}
		}
	}

	internal void UpdateTitle(uint entityId, string title)
	{
		var hasValue = _currentURLs.TryGetValue(entityId, out var titleOld);
		if (hasValue)
		{
			if(_currentToggle.TryGetValue(entityId, out var toggle)) //IF TV EXISTS
			{
				if(toggle) //IF TV IS ON
				{
					if (title != titleOld)
					{
						if (title.StartsWith("picto:")) 
						{
							NavigateNewURL(entityId, title); //CHANGE URL
						}
						else
						{
							TryRemoveTV(entityId); //COMPLETELY REMOVE
						}
					}
				}
				else //IF TV IS OFF
				{
					_currentURLs[entityId] = title;
				}
			}
			else //IF TV DOESNT EXIST
			{
				_currentURLs[entityId] = title;
			}
		} 
		else
		{
			_currentURLs[entityId] = title;
		}
	}

	private long _lastMilliSecond = 0;
	public void RefreshTVs()
	{
		//Check for Texture Updates once per sec
		if (_lastMilliSecond + 500 < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
			if (_lastMilliSecond == 0)
			{
				_plugin.ClearPictomaticWindows();
			}
			_lastMilliSecond = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			CheckAllTVs();
		}
	}

	private void CheckTitles()
	{
		unsafe
		{
			var ratkm = Framework.Instance()->GetUIModule()->GetRaptureAtkModule();

			for (var i = 0; i < 50 && i < ratkm->NameplateInfoCount; i++)
			{
				var npi = ratkm->NamePlateInfoEntries.GetPointer(i);
				if (npi->ObjectId.ObjectId == 0) continue;
				var cleanTitle = MemoryHelper.ReadSeString(&npi->DisplayTitle).TextValue;
				if (cleanTitle.Length > 2)
				{
					cleanTitle = cleanTitle.Substring(1, cleanTitle.Length - 2);
					if (_currentURLs.TryGetValue(npi->ObjectId.ObjectId, out var titleold))
					{
						if (titleold != cleanTitle)
						{
							UpdateTitle(npi->ObjectId.ObjectId, cleanTitle);
						}
					}
					else
					{
						UpdateTitle(npi->ObjectId.ObjectId, cleanTitle);
					}
				}
			}
		}
	}

	public void TurnOffTV()
	{
		var playerId = Services.ClientState.LocalPlayer.EntityId;
		Services.CommandManager.ProcessCommand("/honorific force clear");
		TVTurnedOn = false;
		_currentToggle[playerId] = false;
		_plugin.NavigatePictomaticWindow("pictomatic" + playerId, "about:blank", "");
	}

	private async void TurnOnOwnTV()
	{
		var playerId = Services.ClientState.LocalPlayer.EntityId;
		TVTurnedOn = true;
		if (inputURL == lastInputURL && inputCSS == lastInputCSS)
		{
			Services.CommandManager.ProcessCommand("/honorific force set picto:" + shortenedInputURL + "|silent");
			_currentToggle[playerId] = true;
			UpdateTitle(Services.ClientState.LocalPlayer.EntityId, "picto:" + shortenedInputURL);
		}
		else
		{
			await ShortenURL(inputURL, inputCSS, result =>
			{
				lastInputURL = inputURL;
				lastInputCSS = inputCSS;
				shortenedInputURL = result.Split("/").Last(); Services.CommandManager.ProcessCommand("/honorific force set picto:" + shortenedInputURL + "|silent");
				_currentToggle[playerId] = true;
				UpdateTitle(Services.ClientState.LocalPlayer.EntityId, "picto:" + shortenedInputURL);
			}, error => TVTurnedOn = false);
		}
		
	}
	public async System.Threading.Tasks.Task ShortenURL(string inputURL, string inputCSS, URLShortenerCallback callback, URLShortenerErrorCallback error)
	{
		using (HttpClient client = new HttpClient())
		{
			try
			{
				HttpResponseMessage response = await client.GetAsync("https://is.gd/create.php?format=simple&url=" + Uri.EscapeDataString(inputURL + "picto:" + inputCSS));
				response.EnsureSuccessStatusCode();
				string responseBody = await response.Content.ReadAsStringAsync();

				callback(responseBody);
			}
			catch (HttpRequestException e)
			{
				error(e.Message);
				Services.Log.Error(e.Message + e.StackTrace);
			}
		}
	}

	private async System.Threading.Tasks.Task FetchURLData(string url, URLFetchCallback callback)
	{
		using (HttpClientHandler handler = new HttpClientHandler())
		{
			handler.AllowAutoRedirect = false;

			using (HttpClient client = new HttpClient(handler))
			{
				try
				{
					HttpResponseMessage response = await client.GetAsync(url);

					if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
					{
						// Get the Location header value
						if (response.Headers.Location != null)
						{
							callback(response.Headers.Location.ToString());
							return;
						}
					}
					Services.Log.Debug("Request exception: Shortlink returned 200");
				}
				catch (HttpRequestException e)
				{
					Services.Log.Debug("Request exception: " + e.Message + e.StackTrace);
				}
			}
		}
	}

	private void RefreshOverlayVolume()
	{
		if (!volumeEnabled)
		{
			var AllOverlaySubProcesses = _currentSubProcesses.ToList();

			var enumerator = new MMDeviceEnumerator();
			var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
			var sessionManager = device.AudioSessionManager;
			var sessionsCount = sessionManager.Sessions.Count;

			for (int i = 0; i < sessionsCount; i++)
			{
				var session = sessionManager.Sessions[i];
				if (AllOverlaySubProcesses.Contains((int)session.GetProcessID))
				{
					volume = session.SimpleAudioVolume.Volume;
					volumeEnabled = true;
				}
			}
		}
	}

	private void SetOverlayVolume(float volumeLevel)
	{
		var AllOverlaySubProcesses = _currentSubProcesses.ToList();
		var visitedSubProcesses = new List<int>();
		
		var enumerator = new MMDeviceEnumerator();
		var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
		var sessionManager = device.AudioSessionManager;
		var sessionsCount = sessionManager.Sessions.Count;

		for (int i = 0; i < sessionsCount; i++)
		{
			var session = sessionManager.Sessions[i];
			if (AllOverlaySubProcesses.Contains((int) session.GetProcessID))
			{
				session.SimpleAudioVolume.Volume = volumeLevel;
				visitedSubProcesses.Add((int) session.GetProcessID);
			}
		}

		foreach (var item in AllOverlaySubProcesses)
		{
			if (!visitedSubProcesses.Contains(item))
			{
				_currentSubProcesses.Remove(item);
			}
		}
	}

	internal void AddSubProcess(Guid guid, int processId)
	{
		_currentSubProcesses.Add(processId);
	}
}

