using System.Numerics;
using Pictomatic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using ImGuiNET;
using SharpDX.Direct3D11;
using SharpDX.Direct3D;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.Interop;
using NAudio.CoreAudioApi;
using Dalamud.Utility;
using Dalamud.Interface;
using NAudio.CoreAudioApi.Interfaces;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Dalamud.Hooking;
using static FFXIVClientStructs.FFXIV.Client.Graphics.Render.ModelRenderer;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using static FFXIVClientStructs.FFXIV.Client.UI.Misc.GroupPoseModule;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic;
using FFXIVClientStructs.FFXIV.Client.System.File;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Data;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using SharpDX.DXGI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Graphics.Vfx;
using System.Reflection.Metadata;

public class MainWindow : Window, IDisposable
{
	private readonly Dictionary<uint, IntPtr> _currentOwners = []; //Playerpointer, CompanionDrawpointer
	private readonly Dictionary<uint, String> _currentURLs = []; //Playerpointer, URL
	private readonly Dictionary<uint, String> _currentTitles = []; //Playerpointer, Title
	private Texture2D _currentSharedTexture;
	private uint _currentToggle; //Playerpointer (wether TV toggled or not)
	private uint _currentActivatedTV = 0; //Playerpointer (wether TV toggled or not)
	private nint _currentActiveTVTexturePointer = 0; //TexturePointer
	private uint _currentAudioProcess;
	private uint _currentSubProcess;
	private unsafe List<IntPtr> _currentVFXTextures = new List<IntPtr>();

	private static readonly byte[] _blankCanvas = new byte[16777216];

	private Plugin _plugin;

	public delegate void URLShortenerCallback(string result);
	public delegate void URLShortenerErrorCallback(string result);
	public delegate void URLFetchCallback(string result);

	//Render Vars
	private String buttonExpandWindow = " >Expand TV<";
	bool TVTurnedOn = false;
	private String _inputURL = "";
	private String _shortenedURL = "";
	private String _lastURL = "";
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
		initHook();
	}

	private unsafe void CheckAllTVs()
	{
		RefreshVolume();

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
												CheckOutPossibleTV(tvDraw, ownerId);
											}
						}
						catch(Exception e){}
					}
				}
			}
		}

		//Remove unvisited TVs
		_currentOwners.Where(owner => !visitedTvs.Contains(owner.Key)).Select(owner => owner.Key).ToList().ForEach(ownerId =>
		{;
			if (_currentToggle == ownerId)
				TurnOffTV();
			_currentOwners.Remove(ownerId);
		});
	}

	private unsafe void CheckOutPossibleTV(CharacterBase* tvDraw, uint ownerId)
	{
		

		IntPtr ptr = (IntPtr)tvDraw;
		var tvAddrFound = _currentOwners.TryGetValue(ownerId, out var tvAddr);
		if (!tvAddrFound)
		{
			_currentOwners.Add(ownerId, ptr);
			tvAddr = ptr;
		}
		else if (tvAddrFound && tvAddr != ptr)
		{
			//Texturepointer has changed, assume no TV is running for it to get reassigned
			_currentActivatedTV = 0;

			_currentOwners[ownerId] = ptr;
		}
		
		if (_currentToggle == ownerId) //This TV is supposed to be active...
		{
			if (_currentActivatedTV != _currentToggle) //...But it's not active
			{
				if (ReassignTextureForTV(tvAddr))
				{
					_currentActivatedTV = ownerId;
				}
			}
		}
	}

	private unsafe bool ReassignTextureForTV(nint tvAddr)
	{
		if (_currentSharedTexture != null)
		{
			var TV = (CharacterBase*)tvAddr;
			var textureSource = _currentSharedTexture;
			ShaderResourceView view = new(DxHandler.Device, textureSource, new ShaderResourceViewDescription { Format = textureSource.Description.Format, Dimension = ShaderResourceViewDimension.Texture2D, Texture2D = { MipLevels = textureSource.Description.MipLevels } });

			_currentActiveTVTexturePointer = view.NativePointer;

			// Obtain the native pointers
			void* D3D11Texture2D = (void*)textureSource.NativePointer;
			//TV->Models[0]->Materials[1]->Textures[3].Texture->Texture->D3D11Texture2D = D3D11Texture2D;
			void* D3D11ShaderResourceView = (void*)view.NativePointer;
			//TV->Models[0]->Materials[1]->Textures[3].Texture->Texture->D3D11ShaderResourceView = D3D11ShaderResourceView;

			foreach(var currentTexturePointer in _currentVFXTextures.ToList())
			{
				if(currentTexturePointer == IntPtr.Zero)
				{
					_currentVFXTextures.Remove(currentTexturePointer);
					continue;
				}
				var tex = ((Texture*)currentTexturePointer);
				if(tex != null)
				{
					tex->D3D11Texture2D = D3D11Texture2D;
					tex->D3D11ShaderResourceView = D3D11ShaderResourceView;
				}
			}
			return true;
		}
		else
		{
			return false;
		}
	}

	private unsafe void BlankOutTV(nint TV)
	{
		fixed (byte* Bptr = _blankCanvas) //Put a Black Canvas on the TV
		{
			var TVdraw = (CharacterBase*)TV;
			try
			{

				TVdraw->Models[0]->Materials[1]->Textures[3].Texture->Texture->InitializeContents((void*)Bptr);
			}
			catch (Exception)
			{
				//Ignore Repaint
			}
		}
	}

	public void UpdateSharedDXTexture(IntPtr handle)
	{
		Texture2D? textureSource = DxHandler.Device?.OpenSharedResource<Texture2D>(handle);
		if (textureSource != null)
		{
			_currentSharedTexture = textureSource;
			//Texture has changed, assume no TV is running
			_currentActivatedTV = 0;
		}
	}

	public void Dispose()
	{
		_deviceCreateTexture2DHook.Dispose();
		Services.CommandManager.ProcessCommand("/honorific force clear");
	}

	private void TurnOnTV(uint entityId)
	{
		var isPlayer = entityId == Services.ClientState?.LocalPlayer?.EntityId;
		if (isPlayer)
		{
			if(ValidateURL(out Uri? url) && url != null)
			{
				_currentToggle = entityId;
				
				_plugin.NavigatePictomaticWindow(url.ToString());
				ShareTitle(url.ToString());
			}
		}
		else
		{
			if (_currentURLs.TryGetValue(entityId, out var url))
			{
				_currentToggle = entityId;
				_plugin.NavigatePictomaticWindow(url);
			}
		}
	}

	private void TurnOffTV()
	{
		if(_currentToggle == Services.ClientState?.LocalPlayer?.EntityId)
		{
			Services.CommandManager?.ProcessCommand("/honorific force clear");
		}
		_currentSubProcess = 0;
		_currentAudioProcess = 0;
		VisitedAudioProcesses.Clear();
		_currentToggle = 0;
		_plugin.TerminatePictomaticWindow();
		volumeEnabled = false;
		Services.CommandManager?.ProcessCommand("/penumbra redraw carbuncle");
	}

	private bool ValidateURL(out Uri? url)
	{
		var formattedUrl = _inputURL;

		if (!formattedUrl.StartsWith("http://") && !formattedUrl.StartsWith("https://"))
			formattedUrl = "https://" + formattedUrl;

		return Uri.TryCreate(formattedUrl, UriKind.Absolute, out url)
			   && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && url.Host.Contains(".") && !url.Host.EndsWith(".") && Uri.CheckHostName(url.Host) == UriHostNameType.Dns; ;
	}


	private bool isFocused = false;
	private String placeHolderURL = String.Empty;
	public override void Draw()
	{
		if (_currentToggle != 0)
		{
			ImGui.Text("");

			if (volumeEnabled)
			{
				ImGui.Text("  -");
				ImGui.SameLine();
				if (ImGui.SliderFloat("##Volume", ref volume, 0.0f, 1.0f, "Volume"))
				{
					SetVolume(volume);
				}
				ImGui.SameLine();
				ImGui.Text("+");
				ImGui.Text("");
			}
		}
		ImGui.Text(" Available TVs:");
		var npcList = Services.ObjectTable.Where(x => x is IPlayerCharacter).Cast<IPlayerCharacter>().OrderBy(x => x.Name.TextValue);
		foreach (var item in npcList)
		{
			var isPlayer = item.EntityId == Services.ClientState?.LocalPlayer?.EntityId;
			if (_currentOwners.TryGetValue(item.EntityId, out _))
			{
				var toggle = _currentToggle == item.EntityId;
				var url = String.Empty;
				bool urlExists = false;

				
				if (isPlayer)
				{
					urlExists = ValidateURL(out _);
				}
				else
				{
					if(urlExists = _currentURLs.TryGetValue(item.EntityId, out var tempUrl))
					{
						url = tempUrl;
					}
				}

				if (toggle)
				{
					Vector4 textColor = isPlayer ? new Vector4(1.0f, 0.0f, 0.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f); ;
					ImGui.PushStyleColor(ImGuiCol.Text, textColor);
				}
				else if (!urlExists)
				{
					Vector4 textColor = new Vector4(0.5f, 0.5f, 0.5f, 1.0f); ;
					ImGui.PushStyleColor(ImGuiCol.Text, textColor);
				}

				ImGui.PushFont(UiBuilder.IconFont);
				if (ImGui.Button((toggle ? 
					(!string.IsNullOrEmpty(_inputURL) && isPlayer && urlExists ? FontAwesomeIcon.Repeat.ToIconString() 
						: FontAwesomeIcon.Stop.ToIconString()
					)
					: FontAwesomeIcon.Play.ToIconString()) + "##" + item.EntityId))
				{
					if (!toggle || (toggle && !string.IsNullOrEmpty(_inputURL) && isPlayer && urlExists))
					{
						if(!toggle) //If its toggled already, its the Refresh Button, do not turn off TV!
						{ 
							TurnOffTV(); //Turn off TV before its turned on, as to reset the Textures of any currently running TVs
						}

						System.Threading.Tasks.Task.Run(async () =>
						{
							await System.Threading.Tasks.Task.Delay(250);
							TurnOnTV(item.EntityId);
							if (isPlayer)
							{
								placeHolderURL = _inputURL;
								_inputURL = String.Empty;
							}
						});
					}
					else
					{
						TurnOffTV();
						if(isPlayer && string.IsNullOrEmpty(_inputURL))
						{
							_inputURL = placeHolderURL;
						}
					}
				}
				ImGui.PopFont();

				if (toggle || !urlExists) ImGui.PopStyleColor();

				ImGui.SameLine();

				ImGui.Text(isPlayer ? "YOU" : " " + item.Name.TextValue);

				ImGui.SameLine();

				if (toggle) {
					ImGui.PushFont(UiBuilder.IconFont);
					if (ImGui.Button(FontAwesomeIcon.WindowRestore.ToIconString()))
					{
						_plugin.ToggleExpandPictomaticWindow();
					}
					ImGui.PopFont();
				}

				ImGui.SameLine();

				ImGui.PushFont(UiBuilder.IconFont);
				if (ImGui.Button(FontAwesomeIcon.Clipboard.ToIconString()))
				{
					ImGui.SetClipboardText(isPlayer ? (string.IsNullOrEmpty(_inputURL) && toggle ? placeHolderURL : _inputURL) : (url ?? String.Empty));
				}
				ImGui.PopFont();

				ImGui.SameLine();

				if (isPlayer)
				{
					ImGui.InputText("##URL", ref _inputURL, 1000, ImGuiInputTextFlags.NoHorizontalScroll);

					// Detect if the input is focused
					if (ImGui.IsItemActive())
						isFocused = true;
					else if (ImGui.IsItemDeactivated())
						isFocused = false;

					// Render placeholder if input is empty and unfocused
					if (!isFocused && string.IsNullOrEmpty(_inputURL) && toggle)
					{
						var pos = ImGui.GetItemRectMin();
						var max = ImGui.GetItemRectMax();

						float maxWidth = max.X - pos.X;

						string placeholder = placeHolderURL;

						Vector2 textSize = ImGui.CalcTextSize(placeholder);

						while (textSize.X > maxWidth && placeholder.Length > 0)
						{
							placeholder = placeholder.Substring(0, placeholder.Length - 1);
							textSize = ImGui.CalcTextSize(placeholder + "........");
						}

						if (!placeholder.Equals(placeHolderURL)) placeholder += "...";

						ImGui.GetWindowDrawList().AddText(new Vector2(pos.X + 7, pos.Y), ImGui.GetColorU32(new Vector4(0.3f, 0.8f, 0.3f, 1.0f)), placeholder);
					}

				}
				else
				{
					if(toggle)
						ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.3f, 1.0f), url);
					else
						ImGui.Text(url);
				}
			}
		}
	}

	private const string DeviceCreateTexture2DAddress = "E8 ?? ?? ?? ?? 48 89 07 48 8D 7F 20";
	private unsafe delegate Texture* DeviceCreateTexture2D(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device* thisPtr, int* size, byte mipLevel, uint textureFormat, uint flags, uint unk);
	private Hook<DeviceCreateTexture2D> _deviceCreateTexture2DHook;


	private unsafe void initHook()
	{
		var hm = new HookManager(Services.InteropProvider);
		_deviceCreateTexture2DHook = hm.CreateHook<DeviceCreateTexture2D>("Pictomatic.Device.CreateTexture2D", DeviceCreateTexture2DAddress, DeviceCreateTexture2DDetour, true).Result;
	}

	private unsafe Texture* DeviceCreateTexture2DDetour(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device* thisPtr, int* size, byte mipLevel, uint textureFormat, uint flags, uint unk)
	{
		if (size[0] == 1920 && size[1] == 1080 && mipLevel == 9)
		{
			var currentVFXTexture = _deviceCreateTexture2DHook.Original(thisPtr, size, mipLevel, textureFormat, flags, unk);
			Services.Log.Debug("Spotted new VFX TV Texture");
			_currentVFXTextures.Add((IntPtr) currentVFXTexture);
			return currentVFXTexture;
		}
		else
		{
			return _deviceCreateTexture2DHook.Original(thisPtr, size, mipLevel, textureFormat, flags, unk);
		}
	}

	internal async void UpdateTitle(uint entityId, string title)
	{
		if (entityId == Services.ClientState?.LocalPlayer?.EntityId) return;
		if (!_currentTitles.TryGetValue(entityId, out var oldTitle) || oldTitle != title)
		{
			_currentTitles[entityId] = title;

			if (title.Length < 7 || !title.StartsWith("picto:"))
			{
				if(_currentURLs.TryGetValue(entityId, out _))
				{
					_currentURLs.Remove(entityId);
				}
					
				if (_currentToggle == entityId)
					TurnOffTV();
			}
			else
			{
				var url = "https://is.gd/" + title.Substring("picto:".Length);
				await FetchURLData(url, response => {
					_currentURLs[entityId] = response;
					if (_currentToggle == entityId)
						_plugin.NavigatePictomaticWindow(response);
				});
			}
		}
	}

	private long _lastMilliSecond = 0;
	public void RefreshTVs()
	{
		//Check for Texture Updates once per sec
		if (_lastMilliSecond + 500 < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
			_lastMilliSecond = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			CheckAllTVs();
		}
	}

	private void CheckTitles() //not required anymore, btw causes issues with title/name switching, needs to get checked
	{
		unsafe
		{
			var ratkm = Framework.Instance()->GetUIModule()->GetRaptureAtkModule();
			
			for (var i = 0; i < 50 && i < ratkm->NameplateInfoCount; i++)
			{
				var npi = ratkm->NamePlateInfoEntries.GetPointer(i);
				if (npi->ObjectId.ObjectId == 0 || npi->ClassJobId == 0) continue;
				var cleanTitle = MemoryHelper.ReadSeString(&npi->DisplayTitle).TextValue;
				if (cleanTitle.Length > 2)
				{
					cleanTitle = cleanTitle.Substring(1, cleanTitle.Length - 2);
					UpdateTitle(npi->ObjectId.ObjectId, cleanTitle);
				}
			}
		}
	}

	private async void ShareTitle(string url)
	{
		if (url == _lastURL)
		{
			Services.CommandManager.ProcessCommand("/honorific force set picto:" + _shortenedURL + "|silent");
		}
		else
		{
			await ShortenURL(url, result =>
			{
				_lastURL = url;
				_shortenedURL = result.Split("/").Last();
				Services.CommandManager.ProcessCommand("/honorific force set picto:" + _shortenedURL + "|silent");
			}, error => {
				TurnOffTV(); //TODO: Proper Error Handling!
			});
		}
	}

	public async System.Threading.Tasks.Task ShortenURL(string inputURL, URLShortenerCallback callback, URLShortenerErrorCallback error)
	{
		using (HttpClient client = new HttpClient())
		{
			try
			{
				HttpResponseMessage response = await client.GetAsync("https://is.gd/create.php?format=simple&url=" + Uri.EscapeDataString(inputURL));
				response.EnsureSuccessStatusCode();
				string responseBody = await response.Content.ReadAsStringAsync();

				if (responseBody.Contains("error") || responseBody.Contains("Error"))
				{
					Services.Log.Debug("Request exception: Could not create Shortlink: " + responseBody);
					error(String.Empty);
				}
				else
				{
					callback(responseBody);
				}
			}
			catch (HttpRequestException e)
			{
				Services.Log.Debug("Request exception: Could not create Shortlink.");
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

	private List<uint> VisitedAudioProcesses = new List<uint>();
	private void RefreshVolume()
	{
		if (!volumeEnabled)
		{
			var enumerator = new MMDeviceEnumerator();
			var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
			var sessionManager = device.AudioSessionManager;
			var sessionsCount = sessionManager.Sessions.Count;

			if (sessionManager == null) return;

			for (int i = 0; i < sessionsCount; i++)
			{

				var session = sessionManager.Sessions[i];
				var sessionId = session.GetProcessID;

				if (!VisitedAudioProcesses.Contains(sessionId))
				{
					VisitedAudioProcesses.Add(sessionId);
					var parent = CheckProcessParent(sessionId);
					if (_currentSubProcess == parent && _currentSubProcess != 0 && parent != 0)
					{
						_currentAudioProcess = sessionId;
						volume = session.SimpleAudioVolume.Volume;
						volumeEnabled = true;
						return;
					}
				}
			}
		}
	}

	private void SetVolume(float volumeLevel)
	{
		var enumerator = new MMDeviceEnumerator();
		var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
		var sessionManager = device.AudioSessionManager;
		var sessionsCount = sessionManager.Sessions.Count;

		for (int i = 0; i < sessionsCount; i++)
		{
			var session = sessionManager.Sessions[i];
			if (_currentAudioProcess == (int) session.GetProcessID)
			{
				session.SimpleAudioVolume.Volume = volumeLevel;
			}
		}
	}

	public void AddSubProcess(int processId)
	{
		_currentSubProcess = (uint) processId;
	}

	private static uint CheckProcessParent(uint pid)
	{
		var query = $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}";
		try
		{
			var searcher = new System.Management.ManagementObjectSearcher(query);
			return Convert.ToUInt32(searcher.Get().Cast<System.Management.ManagementObject>().FirstOrDefault()["ParentProcessId"]);
		}
		catch
		{ return 0; }
	}
}