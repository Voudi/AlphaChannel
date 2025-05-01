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
using NAudio.CoreAudioApi;
using Dalamud.Interface;
using System.Runtime.InteropServices;
using SharpDX.DXGI;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using InteropGenerator.Runtime;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using SharpDX.Mathematics.Interop;

public class ControlWindow : Window, IDisposable
{
	private readonly Dictionary<uint, IntPtr> _currentOwners = []; //Playerpointer, CompanionDrawpointer
	private readonly Dictionary<uint, String> _currentURLs = []; //Playerpointer, URL
	private readonly Dictionary<uint, String> _currentTitles = []; //Playerpointer, Title
	private Texture2D _currentSharedTexture;
	public string currentSharedTextureResourceHandle;
	private IntPtr _oldSharedTexture = IntPtr.Zero;
	private uint _currentToggle; //Playerpointer (whether TV toggled or not)
	private uint _currentActivatedTV = 0; //Playerpointer (whether TV toggled or not)
	private uint _currentAudioProcess;
	private uint _currentSubProcess;
	private Dictionary<IntPtr, bool> _currentVFXTextures = new Dictionary<IntPtr, bool>(); //Texturepointer, Flag (whether overridden once)
	private bool _signalShareTitle = false;

	private static readonly byte[] _blankCanvas = new byte[16777216];
	private static Texture2DDescription _texture2dDescription = new Texture2DDescription
	{
		Width = 1920,
		Height = 1080,
		MipLevels = 1,
		ArraySize = 1,
		Format = Format.B8G8R8A8_UNorm,
		BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
		CpuAccessFlags = CpuAccessFlags.None,
		SampleDescription = new SampleDescription(1, 0),
		Usage = ResourceUsage.Default,
		OptionFlags = ResourceOptionFlags.Shared

	};

	private Plugin _plugin;

	public delegate void URLShortenerCallback(string result);
	public delegate void URLShortenerErrorCallback(string result);
	public delegate void URLFetchCallback(string result);

	//Render Vars
	private String _inputURL = "";
	private String _shortenedURL = "";
	private String _lastURL = "";
	float volume = 0.5f;
	private bool volumeEnabled = false;

	public unsafe ControlWindow(Plugin plugin)
        : base("Pictomatic remote", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {

		SizeConstraints = new WindowSizeConstraints
		{
			MinimumSize = new Vector2(225, 100),
			MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
		};

		this._plugin = plugin;

		//INIT TEXTURE
		_currentSharedTexture = new Texture2D(DxHandler.Device, _texture2dDescription);
		using SharpDX.DXGI.Resource resource = _currentSharedTexture.QueryInterface<SharpDX.DXGI.Resource>();
		currentSharedTextureResourceHandle = ((ulong)resource.SharedHandle).ToString();
		clearTexture();

		//INIT HOOK
		_getResourceSyncHook = Services.InteropProvider.HookFromAddress<ResourceManager.Delegates.GetResourceSync>(ResourceManager.Addresses.GetResourceSync.Value, GetResourceSyncDetour);
		_textureOnLoadHook = Services.InteropProvider.HookFromAddress<Texture.Delegates.InitializeContents>(Texture.Addresses.InitializeContents.Value, TexOnLoadDetour);
		var actorVfxCreateAddress = Services.SigScanner.ScanText(ActorVfxCreateSig);
		ActorVfxCreate = Marshal.GetDelegateForFunctionPointer<ActorVfxCreateDelegate>(actorVfxCreateAddress);
		_getResourceSyncHook.Enable();
	}

	public void clearTexture()
	{
		if (_currentSharedTexture == null)
			return;
		var rtv = new RenderTargetView(DxHandler.Device, _currentSharedTexture);
		var clearColor = new RawColor4(0, 0, 0, 1);
		DxHandler.Device?.ImmediateContext.ClearRenderTargetView(rtv, clearColor);
	}

	private void CheckTitles()
	{
		if (_signalShareTitle)
		{
			Services.CommandManager.ProcessCommand("/honorific force set picto:" + _shortenedURL + "|silent");
			_signalShareTitle = false;
		}
	}

	private List<IBattleNpc> _npcList = [];
	private unsafe void CheckAllTVs()
	{
		RefreshVolume();

		CheckTitles();
		
		List<uint> visitedTvs = new List<uint>();
		_playerList = Services.Objects.Where(x => x is IPlayerCharacter).Cast<IPlayerCharacter>().OrderBy(x => x.Name.TextValue).ToList();
		_npcList = Services.Objects.Where(x => x is IBattleNpc).Cast<IBattleNpc>().OrderBy(x => x.YalmDistanceX).ToList();
		foreach(var item in _npcList)
		{
			if(item.Name.TextValue == "Carbuncle")
			{
				if (item.Address == IntPtr.Zero)
					continue;
				var character = (Character*)item.Address;
				if (character != null && character->DrawObject != null)
				{
					if (character->DrawObject->GetObjectType() == ObjectType.CharacterBase)
					{
						try
						{
							var tvDraw = (CharacterBase*)character->DrawObject;
							if (tvDraw->Models[0] is not null)
								if (tvDraw->Models[0]->MaterialCount >= 2)
									if (tvDraw->Models[0]->Materials[1] is not null)
										if (tvDraw->Models[0]->Materials[1]->TextureCount >= 4)
											if (tvDraw->Models[0]->Materials[1]->Textures[3].Texture is not null)
												if (tvDraw->Models[0]->Materials[1]->Textures[3].Texture->Texture is not null)
												{
													var ownerId = character->CompanionOwnerId;
													visitedTvs.Add(ownerId);
													CheckOutPossibleTV((IntPtr)tvDraw, ownerId, item.Address);
												}
						}
						catch (Exception) { }
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

	private void CheckOutPossibleTV(IntPtr tvDraw, uint ownerId, nint address)
	{
		IntPtr ptr = tvDraw;
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
			var playeraddr = Services.ClientState?.LocalPlayer?.Address;
			if (_currentActivatedTV != _currentToggle) //...But it's not active
			{
				_currentActivatedTV = ownerId;
			}
			else
			{
				//This TV is active, refresh its VFX
				RefreshActorVFX(playeraddr.HasValue ? playeraddr.Value : address, address);
			}
		}
	}

	public void Dispose()
	{
		_getResourceSyncHook.Dispose();
		_textureOnLoadHook.Dispose();
		Services.CommandManager.ProcessCommand("/honorific force clear");
	}

	private void TurnOnTV(uint entityId)
	{
		var player = Services.ClientState?.LocalPlayer;
		var isPlayer = entityId == player?.EntityId;
		if (isPlayer)
		{
			if(ValidateURL(out Uri? url) && url != null)
			{
				_currentToggle = entityId;
				
				_plugin.NavigatePictomaticWindow(url.ToString(), currentSharedTextureResourceHandle);
				ShareTitle(url.ToString());
			}
		}
		else
		{
			if (_currentURLs.TryGetValue(entityId, out var url))
			{
				_currentToggle = entityId;
				_plugin.NavigatePictomaticWindow(url, currentSharedTextureResourceHandle);
			}
		}

		//Assume no TV is running
		_currentActivatedTV = 0;
	}

	private void TurnOffTV()
	{
		clearTexture();
		volumeEnabled = false;
		VisitedAudioProcesses.Clear();
		_plugin.TerminatePictomaticWindow();
		if (_currentToggle == Services.ClientState?.LocalPlayer?.EntityId)
		{
			Services.CommandManager?.ProcessCommand("/honorific force clear");
		}
		_currentSubProcess = 0;
		_currentAudioProcess = 0;
		_currentActivatedTV = 0;
		_currentToggle = 0;
	}

	private bool ValidateURL(out Uri? url)
	{
		var formattedUrl = _inputURL;

		if (!formattedUrl.StartsWith("http://") && !formattedUrl.StartsWith("https://"))
			formattedUrl = "https://" + formattedUrl;

		return Uri.TryCreate(formattedUrl, UriKind.Absolute, out url)
			   && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && url.Host.Contains(".") && !url.Host.EndsWith(".") && Uri.CheckHostName(url.Host) == UriHostNameType.Dns;
	}


	private bool _isFocused = false;
	private String _placeHolderURL = String.Empty;
	private List<IPlayerCharacter> _playerList = [];
	public override void Draw()
	{
		if(_currentActivatedTV != 0 && volumeEnabled)
		{
			if(ImGui.SliderFloat("Volume", ref volume, 0.0f, 1.0f))
			{
				SetVolume(volume);
			}
		}

		ImGui.Text(" Available TVs:");
		foreach (var item in _playerList)
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
					try
					{
						if (!toggle || (toggle && !string.IsNullOrEmpty(_inputURL) && isPlayer && urlExists))
						{
							TurnOnTV(item.EntityId);
							if (isPlayer)
							{
								_placeHolderURL = _inputURL;
								_inputURL = String.Empty;
							}
						}
						else
						{
							TurnOffTV();
							if (isPlayer && string.IsNullOrEmpty(_inputURL))
							{
								_inputURL = _placeHolderURL;
							}
						}
					}
					catch (Exception ex)
					{
						Services.Log.Error("FATAL ERROR: " + ex.ToString());
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
					ImGui.SetClipboardText(isPlayer ? (string.IsNullOrEmpty(_inputURL) && toggle ? _placeHolderURL : _inputURL) : (url ?? String.Empty));
				}
				ImGui.PopFont();

				ImGui.SameLine();

				if (isPlayer)
				{
					ImGui.InputText("##URL", ref _inputURL, 1000, ImGuiInputTextFlags.NoHorizontalScroll);

					// Detect if the input is focused
					if (ImGui.IsItemActive())
						_isFocused = true;
					else if (ImGui.IsItemDeactivated())
						_isFocused = false;

					// Render placeholder if input is empty and unfocused
					if (!_isFocused && string.IsNullOrEmpty(_inputURL) && toggle)
					{
						var pos = ImGui.GetItemRectMin();
						var max = ImGui.GetItemRectMax();

						float maxWidth = max.X - pos.X;

						string placeholder = _placeHolderURL;

						Vector2 textSize = ImGui.CalcTextSize(placeholder);

						while (textSize.X > maxWidth && placeholder.Length > 0)
						{
							placeholder = placeholder.Substring(0, placeholder.Length - 1);
							textSize = ImGui.CalcTextSize(placeholder + "........");
						}

						if (!placeholder.Equals(_placeHolderURL)) placeholder += "...";

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

	private const string VFXPath = "chara/monster/m7002/obj/body/b0001/vfx/eff/carbuncleemittor.avfx";

	private void RefreshActorVFX(nint addrCaster, nint addrTarget)
	{
		ActorVfxCreate?.Invoke(VFXPath, addrCaster, addrTarget, -1, (char)0, 0, (char)0);
	}

	//https://github.com/0ceal0t/Dalamud-VFXEditor/blob/main/VFXEditor/Interop/Constants.cs
	public const string ActorVfxCreateSig = "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";
	public delegate IntPtr ActorVfxCreateDelegate(string path, IntPtr a2, IntPtr a3, float a4, char a5, ushort a6, char a7);
	public ActorVfxCreateDelegate ActorVfxCreate;

	private Hook<ResourceManager.Delegates.GetResourceSync> _getResourceSyncHook;
	private Hook<Texture.Delegates.InitializeContents> _textureOnLoadHook;

	private const string TEXPath = "chara/monster/m7002/obj/body/b0001/vfx/texture/screentex.atex";
	private unsafe ResourceHandle* GetResourceSyncDetour(ResourceManager* thisPtr, ResourceCategory* category, uint* type, uint* hash, CStringPointer path, void* unknown)
	{
		if(path.ToString().Contains(TEXPath))
		{
			_textureOnLoadHook.Enable();
			var ret = _getResourceSyncHook.Original(thisPtr, category, type, hash, path, unknown);
			_textureOnLoadHook.Disable();
			
			return ret;
		}
		else
		{
			return _getResourceSyncHook.Original(thisPtr, category, type, hash, path, unknown);
		}
	}

	private unsafe bool TexOnLoadDetour(Texture* thisPtr, void* contents)
	{
		try
		{
			if (thisPtr != null && (IntPtr) thisPtr != IntPtr.Zero)
			{
				if (thisPtr->ActualWidth == 4096 && thisPtr->ActualHeight == 4096)
				{
					var tex = _textureOnLoadHook.Original(thisPtr, contents);
					if(tex)
					{
						ShaderResourceView view = new(DxHandler.Device, _currentSharedTexture, new ShaderResourceViewDescription { Format = _currentSharedTexture.Description.Format, Dimension = ShaderResourceViewDimension.Texture2D, Texture2D = { MipLevels = _currentSharedTexture.Description.MipLevels } });
						thisPtr->D3D11Texture2D = (void*)_currentSharedTexture.NativePointer;
						thisPtr->D3D11ShaderResourceView = (void*)view.NativePointer;
					}

					return tex;
				}
			}
		}
		catch (Exception ex) { Services.Log.Error(ex.ToString()); }

		return _textureOnLoadHook.Original(thisPtr, contents);
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
						_plugin.NavigatePictomaticWindow(response, currentSharedTextureResourceHandle);
				});
			}
		}
	}

	private long _lastMilliSecond = 0;
	public void Refresh()
	{
		//Check for Updates once per sec
		if (_lastMilliSecond + 1000 < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
			_lastMilliSecond = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			CheckAllTVs();
		}
	}

	private async void ShareTitle(string url)
	{
		if (url == _lastURL)
		{
			_signalShareTitle = true;
		}
		else
		{
			await ShortenURL(url, result =>
			{
				_lastURL = url;
				_shortenedURL = result.Split("/").Last();
				_signalShareTitle = true; //Shortened URL will be updated on main thread
				
			}, error => {
				TurnOffTV(); //TODO: Proper Error Handling!
			});
		}
	}

	public async Task ShortenURL(string inputURL, URLShortenerCallback callback, URLShortenerErrorCallback error)
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
			catch (HttpRequestException)
			{
				Services.Log.Debug("Request exception: Could not create Shortlink.");
			}
		}
	}

	private async Task FetchURLData(string url, URLFetchCallback callback)
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
					Services.Log.Debug("Request exception: Shortlink returned 200-OK instead of a 300-redirect, which should happen with Shortlinks");
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
		try {
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
		catch {  }
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

	public void AddSubProcess(uint processId)
	{
		_currentSubProcess = processId;
	}

	private static uint CheckProcessParent(uint pid)
	{
		var query = $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}";
		try
		{
			var searcher = new System.Management.ManagementObjectSearcher(query);
			return Convert.ToUInt32(searcher?.Get()?.Cast<System.Management.ManagementObject>()?.FirstOrDefault()?["ParentProcessId"]);
		}
		catch
		{ return 0; }
	}
}