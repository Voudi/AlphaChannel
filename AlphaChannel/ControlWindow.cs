using AlphaChannel;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Dalamud.Bindings.ImGui;
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
using System.Numerics;
using SharpDX.Mathematics.Interop;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;

public class ControlWindow : Window, IDisposable
{
   
    private const string URL_WHITELIST = "https://pastebin.com/raw/iBatAtHg";
	List<string?> whitelistedNames = new List<string?>();
    private readonly Dictionary<uint, IntPtr> _currentOwners = []; //Playerpointer, CompanionDrawpointer
	private readonly Dictionary<uint, String> _currentURLs = []; //Playerpointer, URL
	private readonly Dictionary<uint, String> _currentTitles = []; //Playerpointer, Title
	private Texture2D _currentSharedTexture;
	private bool _textureLoaded = false;
	private bool _domContentloaded = false;
    public string currentSharedTextureResourceHandle;
	private uint _currentToggle; //Playerpointer (whether TV toggled or not)
	private uint _currentActivatedTV = 0; //Playerpointer (whether TV toggled or not)
	private uint _currentAudioProcess;
	private uint _currentSubProcess;
	private bool _refreshAudio = false;
	private Dictionary<IntPtr, bool> _currentVFXTextures = new Dictionary<IntPtr, bool>(); //Texturepointer, Flag (whether overridden once)
    private bool _signalShareTitle = false;
	private bool _signalToggleShare = false;
    private bool _modexists = false;
    private bool _modenabled = false;
    private bool _installWarningMessage = false;
    private bool _installedmod = false;
	private bool _syncPlayToggle = true;
	private bool isSyncPlay(Uri? url)
	{
		if (url != null)
			return _syncPlayToggle && !url.Host.EndsWith(".opentogethertube.com", StringComparison.OrdinalIgnoreCase)
					 && !string.Equals(url.Host, "opentogethertube.com", StringComparison.OrdinalIgnoreCase);
		else return false;
	}
	private bool _adBlockToggle = true;

    //Render Vars
    private String _inputURL = "";
    private String _shortenedURL = "";
    private String _lastURL = "";
    float volume = 0.5f;
    private bool volumeEnabled = false;

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

    public static readonly HttpClient HTTPCLIENT = new HttpClient();
    public static readonly HttpClient NOREDIRECTHTTPCLIENT = new HttpClient(
		new HttpClientHandler { AllowAutoRedirect = false }
	);
	public bool HasAdBlock => _adBlockToggle;

	private readonly OTTApi _OTTApi;
    public unsafe ControlWindow(Plugin plugin, string title)
        : base(title, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _OTTApi = new OTTApi(this);

        SizeConstraints = new WindowSizeConstraints
		{
			MinimumSize = new Vector2(325, 200),
			MaximumSize = new Vector2(1800, 900)
		};

		this._plugin = plugin;

		//INIT TEXTURE
		_currentSharedTexture = new Texture2D(DxHandler.Device, _texture2dDescription);
		using SharpDX.DXGI.Resource resource = _currentSharedTexture.QueryInterface<SharpDX.DXGI.Resource>();
        currentSharedTextureResourceHandle = ((ulong)resource.SharedHandle).ToString();
		ClearTexture();

		//INIT HOOK
		_getResourceSyncHook = Services.InteropProvider.HookFromAddress<ResourceManager.Delegates.GetResourceSync>(ResourceManager.Addresses.GetResourceSync.Value, GetResourceSyncDetour);
		_textureOnLoadHook = Services.InteropProvider.HookFromAddress<Texture.Delegates.InitializeContents>(Texture.Addresses.InitializeContents.Value, TexOnLoadDetour);
		var actorVfxCreateAddress = Services.SigScanner.ScanText(ActorVfxCreateSig);
		ActorVfxCreate = Marshal.GetDelegateForFunctionPointer<ActorVfxCreateDelegate>(actorVfxCreateAddress);
		_getResourceSyncHook.Enable();

        // Retrieve whitelist
        HttpClient client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bot MTM5NTg5NjIzMzk5MzcwMzYzNg.GmAEen.SPgodjzUP_wPQhZ5wnlJWIudMNPuhV--7lVCDI");
        var apiTask = Task.Run(() => {
            try
            {
                var getTask = client.GetAsync("https://discord.com/api/channels/1395896063629463645/messages");
				getTask.Wait();
                return getTask.Result.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Services.Log.Debug("An exception has occured while trying to call discord API for whitelist : " + ex.Message);
                return null;
            }
        });
        apiTask.Wait();
        // Request completed (if not null, if null, already handled by the catch above)
        if (apiTask.Result != null)
        {
            JsonNode? jsonResult = JsonSerializer.Deserialize<JsonNode>(apiTask.Result);
            if (jsonResult?.GetType() != typeof(JsonArray))
            {
                // This is not the result we are expecting, most likely an API error (Triggered with editing the key and making it fail on purpose
                Services.Log.Error("Mismatched result from Discord API while retrieving the whitelist. Content : " + jsonResult?.ToString());
            }
            else
            {
                // We have the list
                whitelistedNames = ((JsonArray)jsonResult).Select(message => message?["content"]?.ToString()).ToList();
            }
        }

        _ = CheckTVMod();

        if (!_OTTApi.initialized)
            _OTTApi.Login();
    }

    private bool _canHost = false;
    private bool _checkedCanHost = false;
    private async void CheckIfCanHost(string? name, string? world)
	{
		if (_checkedCanHost)
			return;
        try
        {
			_canHost = whitelistedNames.Contains(name + " " + world);
        }
		catch (Exception) { }
        _checkedCanHost = true;
    }

    public void ClearTexture()
	{
		if (_currentSharedTexture == null)
			return;
		var rtv = new RenderTargetView(DxHandler.Device, _currentSharedTexture);
		var clearColor = new RawColor4(0.3f, 0.3f, 0.3f, 1);
		DxHandler.Device?.ImmediateContext.ClearRenderTargetView(rtv, clearColor);
    }

	private void CheckTitles()
	{
		if (_signalShareTitle &&  _signalToggleShare)
		{
			Services.CommandManager.ProcessCommand("/honorific force set alpha:" + _shortenedURL + "|silent");
			_signalShareTitle = false;
		}
	}

	private unsafe void CheckAllTVs()
	{
		
        var playerId = Services.Objects.LocalPlayer?.EntityId;

        bool hookEnabled = _getResourceSyncHook.IsEnabled && !_getResourceSyncHook.IsDisposed;
        if (hookEnabled) //Only check for stuff while the hook is activated, which is outside from duties
		{
			RefreshVolume();

			CheckTitles();
		
			List<uint> visitedTvs = new List<uint>();
			_playerList = Services.Objects.Where(x => x is IPlayerCharacter).OrderBy(x => (x.EntityId == Services.Objects.LocalPlayer?.EntityId) ? "@" : x.Name.TextValue);

			var showWarningMessage = false;
			foreach (var item in Services.Objects.Where(x => x is IBattleNpc))
			{
				if (item.Name.TextValue == "Carbuncle")
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
								var ownerId = character->CompanionOwnerId;
								if (playerId == ownerId)
									CheckIfCanHost(Services.Objects.LocalPlayer?.Name.TextValue, Services.Objects.LocalPlayer?.HomeWorld.Value.Name.ToString());
								if (tvDraw->Models[0] is not null)
									if (tvDraw->Models[0]->MaterialCount >= 1)
										if (tvDraw->Models[0]->Materials[0] is not null)
											if (tvDraw->Models[0]->Materials[0]->TextureCount >= 4)
												if (tvDraw->Models[0]->Materials[0]->Textures[3].Texture is not null)
													if (tvDraw->Models[0]->Materials[0]->Textures[3].Texture->Texture is not null)
													{
														if(tvDraw->Models[0]->Materials[0]->Textures[3].Texture->Texture->ActualHeight == 1024
															&& tvDraw->Models[0]->Materials[0]->Textures[3].Texture->Texture->ActualWidth == 1024)
														{
															if (playerId == ownerId)
																_modenabled = false;

                                                            visitedTvs.Add(ownerId);
															CheckOutPossibleTV((IntPtr)tvDraw, ownerId, item.Address);
															continue;
														}
													}

								if (playerId == ownerId)
								{
									showWarningMessage = true;
								}
							}
							catch (Exception) { }
						}
					}
				}
			}

			_installWarningMessage = showWarningMessage;

			//Remove unvisited TVs
			_currentOwners.Where(owner => !visitedTvs.Contains(owner.Key)).Select(owner => owner.Key).ToList().ForEach(ownerId =>
			{;
				if (_currentToggle == ownerId)
				{
					if(playerId == ownerId)
						Services.CommandManager?.ProcessCommand("/honorific force clear"); //In case Players EntityId changed due to sudden teleport
					TurnOffTV();
				}
				if (playerId == ownerId && !_canHost)
				{
					_checkedCanHost = false; //Retry
				}
				_currentOwners.Remove(ownerId);
			});
        }

		//Disable hook during duties
        bool dutyStarted = Services.DutyState.IsDutyStarted;
        if (dutyStarted && hookEnabled)
		{
			if(_currentToggle != 0) {
				if(playerId == _currentToggle)
					Services.CommandManager?.ProcessCommand("/honorific force clear"); //In case Player vanishes into duty
			
				TurnOffTV();
            }
            if(!_getResourceSyncHook.IsDisposed)
				_getResourceSyncHook.Disable();
		}
		else if (!dutyStarted && !hookEnabled)
        {
            if (!_getResourceSyncHook.IsDisposed)
                _getResourceSyncHook.Enable();
		}
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
			var playeraddr = Services.Objects.LocalPlayer?.Address;
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
        _textureOnLoadHook.Disable();
        _textureOnLoadHook.Dispose();
        _getResourceSyncHook.Dispose();

		Services.CommandManager.ProcessCommand("/honorific force clear");
	}

	private void TurnOnTV(uint entityId, bool isSyncRefresh)
	{
		var player = Services.Objects.LocalPlayer;
		var isPlayer = entityId == player?.EntityId;
		if (isPlayer)
		{
			if(ValidateURL(out Uri? url) && url != null)
			{
				_currentToggle = entityId;

				if (isSyncRefresh && isSyncPlay(url))
					ForceSyncPlay(); //Just send a sync play signal if sync is on, no need to refresh webpage
				else
					_plugin.NavigateAlphaWindow(url.ToString(), currentSharedTextureResourceHandle);

				ShareTitle(url.ToString());
			}
			else
			{
				return;
			}
		}
		else
		{
			if (_currentURLs.TryGetValue(entityId, out var url))
			{
				_currentToggle = entityId;
				_plugin.NavigateAlphaWindow(url, currentSharedTextureResourceHandle);
			}
            else
            {
                return;
            }
        }

		//Assume no TV is running
		_currentActivatedTV = 0;
        //Reset audio counter to try to fetch the process for the first 5 seconds only
        _secondsCounter = 0;
        //Assume site hasnt been loaded up
        _domContentloaded = false;
    }

	private void TurnOffTV()
	{
		ClearTexture();
		volumeEnabled = false;
		VisitedAudioProcesses.Clear();
		_plugin.TerminateAlphaWindow();
		_currentSubProcess = 0;
		_currentAudioProcess = 0;
		_currentActivatedTV = 0;
		_currentToggle = 0;
	}

	private bool ValidateURL(out Uri? url)
	{
		var formattedUrl =  _inputURL;

		if (!formattedUrl.StartsWith("http://") && !formattedUrl.StartsWith("https://"))
			formattedUrl = "https://" + formattedUrl;

		var result = Uri.TryCreate(formattedUrl, UriKind.Absolute, out url) && (url?.Scheme == Uri.UriSchemeHttp || url?.Scheme == Uri.UriSchemeHttps) && url.Host.Contains('.') && !url.Host.EndsWith('.') && Uri.CheckHostName(url.Host) == UriHostNameType.Dns;

		if (!result)
			return false;

		if (isSyncPlay(url))
		{
            _OTTApi.CheckURL(formattedUrl);

			result &= _OTTApi.LastCheckSuccessful;

			formattedUrl = _OTTApi.GetRoomURL;

            result &= Uri.TryCreate(formattedUrl, UriKind.Absolute, out url) && (url?.Scheme == Uri.UriSchemeHttp || url?.Scheme == Uri.UriSchemeHttps) && url.Host.Contains('.') && !url.Host.EndsWith('.') && Uri.CheckHostName(url.Host) == UriHostNameType.Dns;
        }

		return result;
    }

    private async Task<bool> CheckTVMod()
	{
		var apiUrl = "http://localhost:42069/api";

        try
        {
            var responseMods = await HTTPCLIENT.GetAsync(apiUrl + "/mods");

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

    private async void InstallTVMod()
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
                var responseInstall = await HTTPCLIENT.PostAsync(apiUrl + "/installmod", content);

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

	private bool _isFocused = false;
	private String _placeHolderURL = String.Empty;
	private IEnumerable<IGameObject> _playerList = [];
	public override void Draw()
	{
		var playerIsRunningTV = _currentToggle == Services.Objects.LocalPlayer?.EntityId;

        if (Services.DutyState.IsDutyStarted)
		{
            ImGui.Text("AlphaChannel is deactivated during a duty.");
			return;
        }
        if (_currentToggle != 0 && !_textureLoaded && _domContentloaded)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " Unable to fetch the TV screen! Has the plugin been deactivated during play? Please make sure to: ");
            ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " 1. Restart the game client with the plugin turned on");
            ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " 2. Make sure the AlphaChannelTV Penumbra mod is enabled");
        }
        if (_currentActivatedTV != 0 && volumeEnabled)
		{
			if(ImGui.SliderFloat("Volume", ref volume, 0.0f, 1.0f))
			{
				SetVolume(volume);
            }
        }
        if (!_modexists)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " Please install the AlphaChannelTV Penumbra mod before continuing:");
            if (ImGui.Button("Step 1 - Install"))
            {
                InstallTVMod();
            }
            return;
        }
        if (!_modenabled && _installedmod)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " Please enable the AlphaChannelTV Penumbra mod before continuing:");
            if (ImGui.Button("Step 2 - Enable"))
            {
                Services.CommandManager?.ProcessCommand("/penumbra reload");
                Services.CommandManager?.ProcessCommand("/penumbra mod enable Default | AlphaChannelTV");
                Services.CommandManager?.ProcessCommand("/penumbra redraw carbuncle");
                _modenabled = true;
                _installedmod = false;
            }
            return;
        }
        ImGui.Text(" Host Settings:");

        Vector4 textColor = !_canHost ? new Vector4(0.5f, 0.5f, 0.5f, 1.0f) : (_signalToggleShare ? new Vector4(0.0f, 0.29f, 1.0f, 1.0f) : new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);

        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button((_signalToggleShare ? FontAwesomeIcon.EyeSlash.ToIconString() : FontAwesomeIcon.Eye.ToIconString()) + "##eye"))
        {
            if (_canHost)
            {
                _signalToggleShare = !_signalToggleShare;
				if(playerIsRunningTV)
				{
                    if (_signalToggleShare)
                    {
                        //Reapply sharing
                        _signalShareTitle = true;
                    }
                    else
                    {
                        _signalShareTitle = false;
                        Services.CommandManager?.ProcessCommand("/honorific force clear");
                    }
                }
            }
        }
        ImGui.PopFont();

        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text(!_canHost && _checkedCanHost ? "Unable to share TV. You have not been whitelisted." : (!_canHost ? "Please summon your TV first."  : (_signalToggleShare ? "Currently sharing TV with others, press to stop sharing URL." : "Currently not sharing TV with others, press to share URL.")));
            ImGui.EndTooltip();
        }

        
        ImGui.SameLine();

        textColor = _syncPlayToggle ? new Vector4(0.0f, 0.29f, 1.0f, 1.0f) : new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);

        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.Link.ToIconString() + "##sync"))
        {
            if (!_OTTApi.initialized)
                _OTTApi.Login();

            _syncPlayToggle = !_syncPlayToggle;

            if (playerIsRunningTV) //If currently hosting, turn it off, no matter what
			{
                Services.CommandManager?.ProcessCommand("/honorific force clear");
                TurnOffTV();
                if (string.IsNullOrEmpty(_inputURL))
                {
                    _inputURL = _placeHolderURL;
                }
            }
        }
        ImGui.PopFont();

        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text(_syncPlayToggle ? "Currently using video sync. Click to deactivate." : "Currently not using video sync. Click to activate.");
            ImGui.EndTooltip();
        }

		/*
        ImGui.SameLine();

        textColor = _adBlockToggle ? new Vector4(0.8f, 0.8f, 0.3f, 1.0f) : new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);

        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button(FontAwesomeIcon.ShieldCat.ToIconString() + "##adblock"))
        {
            _adBlockToggle = !_adBlockToggle;
        }
        ImGui.PopFont();

        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text(_adBlockToggle ? "Deactivate Adblock" : "Activate Adblock");
            ImGui.EndTooltip();
        }
		*/

        ImGui.Text(" Available TVs:");

        foreach (var item in _playerList)
		{
            var isPlayer = item.EntityId == Services.Objects.LocalPlayer?.EntityId;
            
			if(isPlayer && _installWarningMessage && _modenabled)
			{
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " Searching for TV...");
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " Please enable the AlphaChannelTV Penumbra mod and make sure it has the highest priority.");
			}
            if ((isPlayer && _installWarningMessage) || _currentOwners.TryGetValue(item.EntityId, out _)) //Checks if players carbuncle exists OR other players TV exists
			{
				var isTheRunningTV = _currentToggle == item.EntityId;
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

                var refreshNeeded = isTheRunningTV && !string.IsNullOrEmpty(_inputURL) && isPlayer && urlExists;

                ImGui.Text(isPlayer ? "YOU" : " " + item.Name.TextValue);

                ImGui.SameLine();
				
				if (isTheRunningTV)
				{
					textColor = isPlayer ? new Vector4(1.0f, 0.0f, 0.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f); ;
					ImGui.PushStyleColor(ImGuiCol.Text, textColor);
				}
				else if(isPlayer && _installWarningMessage)
				{
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.0f, 1.0f, 0.0f, 1.0f));
                }
				else if (!urlExists)
				{
					ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1.0f));
				}


				ImGui.PushFont(UiBuilder.IconFont);
				if (ImGui.Button((isTheRunningTV ?
					(!string.IsNullOrEmpty(_inputURL) && isPlayer && urlExists ?
						FontAwesomeIcon.Repeat.ToIconString()
						: FontAwesomeIcon.Stop.ToIconString()
					)
					: ((isPlayer && _installWarningMessage) ?
							FontAwesomeIcon.Download.ToIconString()
						 : FontAwesomeIcon.Play.ToIconString()
					)) + "##play" + item.EntityId))
				{
					try
					{
                        if ((isPlayer && _installWarningMessage))
                        {
                            if (!_installedmod)
                            {
								if (_modexists)
								{
                                    Services.CommandManager?.ProcessCommand("/penumbra reload");
                                    Services.CommandManager?.ProcessCommand("/penumbra mod enable Default | AlphaChannelTV");
                                    Services.CommandManager?.ProcessCommand("/penumbra redraw carbuncle");
									_modenabled = true;
								}
                            }
                        }
						else
						{
                            if (!isTheRunningTV || refreshNeeded)
                            {
								if(urlExists)
									TurnOnTV(item.EntityId, refreshNeeded && _syncPlayToggle);

								if (refreshNeeded) 
								{
									//Update title share if player is changing url
                                    if (_signalToggleShare)
                                    {
                                        //Reapply sharing
                                        _signalShareTitle = true;
                                    }
                                }

                                if (isPlayer)
                                {
                                    _placeHolderURL = _inputURL;
                                    _inputURL = String.Empty;
                                }
                            }
                            else
                            {
                                if (isPlayer)
                                {
                                    Services.CommandManager?.ProcessCommand("/honorific force clear"); //When turning off the players TV
                                }
                                TurnOffTV();
                                if (isPlayer && string.IsNullOrEmpty(_inputURL))
                                {
                                    _inputURL = _placeHolderURL;
                                }
                            }
                        }
					}
					catch (Exception ex)
					{
						Services.Log.Error("FATAL ERROR: " + ex.ToString());
					}
				}
				ImGui.PopFont();

				if (isTheRunningTV || !urlExists || (isPlayer && _installWarningMessage)) ImGui.PopStyleColor();

                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text(

                        (isTheRunningTV ?
							(!string.IsNullOrEmpty(_inputURL) && isPlayer && urlExists ? "Visit new URL"
							 : "Stop"
                        )
						: "Play")
					);
                    ImGui.EndTooltip();
                }

                ImGui.SameLine();

                if (isTheRunningTV)
                {
                    ImGui.PushFont(UiBuilder.IconFont);
                    if (ImGui.Button(FontAwesomeIcon.PlayCircle.ToIconString() + "##forceplay" + item.EntityId))
                    {
						if (_syncPlayToggle && isPlayer)
							ForceSyncPlay();
						else
							_plugin.Play();
                    }
                    ImGui.PopFont();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text("Force-Play Video - BETA");
                        ImGui.EndTooltip();
                    }
                }

                ImGui.SameLine();

                if (isTheRunningTV)
                {
                    ImGui.PushFont(UiBuilder.IconFont);
                    if (ImGui.Button(FontAwesomeIcon.ExpandArrowsAlt.ToIconString() + "##expand" + item.EntityId))
                    {
                        _plugin.Fullscreen();
                    }
                    ImGui.PopFont();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text("Toggle Fullscreen - BETA");
                        ImGui.EndTooltip();
                    }
                }

                ImGui.SameLine();

				if (isTheRunningTV) {
					ImGui.PushFont(UiBuilder.IconFont);
					if (ImGui.Button(FontAwesomeIcon.WindowRestore.ToIconString() + "##restore" + item.EntityId))
					{
						_plugin.ToggleExpandAlphaWindow();
					}
					ImGui.PopFont();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text("Take control of the underlying browser window");
                        ImGui.EndTooltip();
                    }
                }

                ImGui.SameLine();

				if (urlExists || isPlayer)
				{
                    ImGui.PushFont(UiBuilder.IconFont);
                    if (ImGui.Button(FontAwesomeIcon.Clipboard.ToIconString() + "##clipboard" + item.EntityId))
                    {
                        ImGui.SetClipboardText(isPlayer ? (string.IsNullOrEmpty(_inputURL) && isTheRunningTV ? _placeHolderURL : _inputURL) : (url ?? String.Empty));
                    }
                    ImGui.PopFont();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text("Copy URL to clipboard");
                        ImGui.EndTooltip();
                    }

                    ImGui.SameLine();
                }

				if (isPlayer)
				{
					textColor = _syncPlayToggle ? 
						(_OTTApi.IsChecking ? new Vector4(0.7f, 0.7f, 0.2f, 1f) 
							: (urlExists || (isTheRunningTV && string.IsNullOrEmpty(_inputURL)) ? new Vector4(0.2f, 0.7f, 0.2f, 1f) 
								: new Vector4(0.7f, 0.2f, 0.2f, 1f))) 
						: (urlExists ? new Vector4(0.2f, 0.7f, 0.2f, 1f) 
							: new Vector4(0.7f, 0.2f, 0.2f, 1f));

					ImGui.PushStyleColor(ImGuiCol.Border, textColor); // red border
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
                    
					ImGui.InputText("##URL", ref _inputURL, 1000, ImGuiInputTextFlags.NoHorizontalScroll);

					ImGui.PopStyleVar();
					ImGui.PopStyleColor();

					// Detect if the input is focused
					if (ImGui.IsItemActive())
						_isFocused = true;
					else if (ImGui.IsItemDeactivated())
						_isFocused = false;

					// Render placeholder if input is empty and unfocused
					if (!_isFocused && string.IsNullOrEmpty(_inputURL) && isTheRunningTV)
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
					if(isTheRunningTV)
						ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.3f, 1.0f), url);
					else if(!urlExists)
                        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Not sharing anything");
                    else
						ImGui.Text(url);
                }
			}
			else
			{
				if(isPlayer)
					ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: You have not summoned your standard blue carbuncle.");
            }
        }
        if (!_canHost && _checkedCanHost)
        {
            ImGui.Text("");
            ImGui.Text(" Notice:");
			ImGui.Text("  You have not been whitelisted to share your URL.");
            ImGui.Text("  Other Players won't be able to view your TV.");
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
				if (thisPtr->ActualWidth == 1920 && thisPtr->ActualHeight == 1080)
				{
					var tex = _textureOnLoadHook.Original(thisPtr, contents);
					if(tex)
					{
						ShaderResourceView view = new(DxHandler.Device, _currentSharedTexture, new ShaderResourceViewDescription { Format = _currentSharedTexture.Description.Format, Dimension = ShaderResourceViewDimension.Texture2D, Texture2D = { MipLevels = _currentSharedTexture.Description.MipLevels } });
						thisPtr->D3D11Texture2D = (void*)_currentSharedTexture.NativePointer;
						thisPtr->D3D11ShaderResourceView = (void*)view.NativePointer;

						_textureLoaded = true;
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
		if (entityId == Services.Objects.LocalPlayer?.EntityId) return;
		if (!_currentTitles.TryGetValue(entityId, out var oldTitle) || oldTitle != title)
		{
			_currentTitles[entityId] = title;

			if (title.Length < 7 || !title.StartsWith("alpha:"))
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
				var url = "https://is.gd/" + title.Substring("alpha:".Length);
				await FetchURLData(url, response => {
					Services.Log.Debug("New URL: " + url);
					_currentURLs[entityId] = response;
					if (_currentToggle == entityId)
						_plugin.NavigateAlphaWindow(response, currentSharedTextureResourceHandle);
				});
			}
		}
	}

	private int _secondsCounter = 0;
	private long _lastMilliSecond = 0;
	public void Refresh()
	{
		//Check for Updates once per sec
		if (_lastMilliSecond + 1000 < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
			_lastMilliSecond = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			_secondsCounter++;

			CheckAllTVs();

			_plugin.PollWebviewWindow();

            _plugin.CheckURLHook();
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
        try
        {
            HttpResponseMessage response = await HTTPCLIENT.GetAsync("https://is.gd/create.php?format=simple&url=" + Uri.EscapeDataString(inputURL));
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

	private async Task FetchURLData(string url, URLFetchCallback callback)
	{
        try
        {
            HttpResponseMessage response = await NOREDIRECTHTTPCLIENT.GetAsync(url);

            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                // Get the Location header value
                if (response.Headers.Location != null)
                {
                    callback(response.Headers.Location.ToString());
                    return;
                }
            }
            Services.Log.Debug("Request exception: Shortlink returned 200-OK instead of a 300-redirect, which should not happen with Shortlinks");
        }
        catch (HttpRequestException e)
        {
            Services.Log.Debug("Request exception: " + e.Message + e.StackTrace);
        }
	}

	private List<uint> VisitedAudioProcesses = new List<uint>();
	private void RefreshVolume()
	{
		try {
            _ = Task.Run(() =>
            {
                if (!volumeEnabled && _refreshAudio && 0 < _secondsCounter && _secondsCounter < 30 && _secondsCounter % 4 == 0)
                {
                    var enumerator = new MMDeviceEnumerator();
                    var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    var sessionManager = device.AudioSessionManager;
                    var sessionsCount = sessionManager.Sessions.Count;

                    if (sessionManager == null) return;

                    Dictionary<uint, AudioSessionControl> sessionPIDs = [];
                    for (int i = 0; i < sessionManager.Sessions.Count; i++)
                    {
                        var session = sessionManager.Sessions[i];
                        sessionPIDs.Add(session.GetProcessID, session);
                    }
                    var parents = GetProcessParentMapFiltered(sessionPIDs.Keys);
                    var unvisitedAudioProcesses = sessionPIDs.Where(pid => !VisitedAudioProcesses.Contains(pid.Key));
                    foreach (var pid in unvisitedAudioProcesses)
                    {
                        var parent = parents[pid.Key];
                        if (_currentSubProcess == parent && _currentSubProcess != 0 && parent != 0)
                        {
                            _currentAudioProcess = pid.Key;
                            volume = pid.Value.SimpleAudioVolume.Volume;
                            volumeEnabled = true;
                            _refreshAudio = false;
                            return;
                        }
                    }
                }
            });
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
		_refreshAudio = true;
    }

    private static Dictionary<uint, uint> GetProcessParentMapFiltered(IEnumerable<uint> pids)
    {
        var pidList = pids.ToList();
        if (pidList.Count == 0)
            return new Dictionary<uint, uint>();

        // Construct the WHERE clause
        var filter = string.Join(" OR ", pidList.Select(pid => $"ProcessId = {pid}"));
        var query = $"SELECT ProcessId, ParentProcessId FROM Win32_Process WHERE {filter}";

        var result = new Dictionary<uint, uint>();

        try
        {
            var searcher = new System.Management.ManagementObjectSearcher(query);
            foreach (var obj in searcher.Get().Cast<System.Management.ManagementObject>())
            {
                if (obj["ProcessId"] != null && obj["ParentProcessId"] != null)
                {
                    uint pid = Convert.ToUInt32(obj["ProcessId"]);
                    uint ppid = Convert.ToUInt32(obj["ParentProcessId"]);
                    result[pid] = ppid;
                }
            }
        }
        catch
        {
            // Handle exceptions as needed
        }

        return result;
    }

    public void OnDOMContentLoaded()
    {
		_domContentloaded = true;
        Task.Delay(1000);
        ForceSyncPlay();
    }

    private void ForceSyncPlay()
    {
        if (_syncPlayToggle)
        {
            _OTTApi.ForcePlayVideo();
			Task.Delay(1000);
            _plugin.Play();
        }
    }
}