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

public class MainWindow : Window, IDisposable
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
	private bool _firstTimeTVOn = true;

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

	public MainWindow(Plugin plugin)
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

	public void initTexture()
	{
		var texture2dDescription = new Texture2DDescription
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
		_currentSharedTexture = new Texture2D(DxHandler.Device, texture2dDescription);
		var rtv = new RenderTargetView(DxHandler.Device, _currentSharedTexture);
		var clearColor = new RawColor4(0, 0, 0, 1);
		DxHandler.Device?.ImmediateContext.ClearRenderTargetView(rtv, clearColor);
		using SharpDX.DXGI.Resource resource = _currentSharedTexture.QueryInterface<SharpDX.DXGI.Resource>();
		currentSharedTextureResourceHandle = ((ulong)resource.SharedHandle).ToString();
	}
	public void clearTexture()
	{
		var rtv = new RenderTargetView(DxHandler.Device, _currentSharedTexture);
		var clearColor = new RawColor4(0, 0, 0, 1);
		DxHandler.Device?.ImmediateContext.ClearRenderTargetView(rtv, clearColor);
	}

	private unsafe void CheckAllTVs()
	{
		RefreshVolume();

		//CheckTitles(); CAUTION UNSAFE
		
		List<uint> visitedTvs = new List<uint>();
		var npcList = Services.Objects.Where(x => x is IBattleNpc).Cast<IBattleNpc>().OrderBy(x => x.YalmDistanceX);
		foreach(var item in npcList)
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
						catch (Exception e) { }
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
			if (_currentActivatedTV != _currentToggle) //...But it's not active
			{
				if (ReassignTextureForTV(tvAddr))
				{
					
					currentVFXActive = true;
					currentVFXObjAddress = address;
					var playeraddr = Services.ClientState?.LocalPlayer?.Address;
					currentVFXPlrAddress = playeraddr.HasValue ? playeraddr.Value : currentVFXObjAddress;
					
					_currentActivatedTV = ownerId;
					Services.Log.Debug("Turning on new TV...");
				}
			}
			else
			{
				//This TV is active, refresh its VFX
				if (currentVFXActive)
				{
					RefreshActorVFX();
				}
			}
		}
	}

	[DllImport("kernel32.dll")]
	static extern bool IsBadReadPtr(IntPtr lp, uint ucb);

	private unsafe bool ReassignTextureForTV(nint tvAddr)
	{
		/*
		Services.Log.Debug("TV Tex redraw attempt");
		var TV = (CharacterBase*)tvAddr;
		var textureSource = _currentSharedTexture;
		ShaderResourceView view = new(DxHandler.Device, textureSource, new ShaderResourceViewDescription { Format = textureSource.Description.Format, Dimension = ShaderResourceViewDimension.Texture2D, Texture2D = { MipLevels = textureSource.Description.MipLevels } });

		var tex = TV->Models[0]->Materials[1]->Textures[3].Texture->Texture;

		// Obtain the native pointers
		tex->D3D11Texture2D = (void*)textureSource.NativePointer;
		tex->D3D11ShaderResourceView = (void*)view.NativePointer;

		Services.Log.Debug("Successs redraw TV Tex");
		*/
		return true;
		/*
		if (_currentSharedTexture != null)
		{
			Services.Log.Debug("Attempting a redraw of the TV");
			var TV = (CharacterBase*)tvAddr;
			var textureSource = _currentSharedTexture;
			ShaderResourceView view = new(DxHandler.Device, textureSource, new ShaderResourceViewDescription { Format = textureSource.Description.Format, Dimension = ShaderResourceViewDimension.Texture2D, Texture2D = { MipLevels = textureSource.Description.MipLevels } });

			// Obtain the native pointers
			void* D3D11Texture2D = (void*)textureSource.NativePointer;
			void* D3D11ShaderResourceView = (void*)view.NativePointer;

			var success = false;

			var changingFlag = _currentSharedTexture.NativePointer != _oldSharedTexture;
			foreach (var currentTexturePointer in _currentVFXTextures.ToList())
			{
				var ptr = currentTexturePointer.Key;

				if (IsBadReadPtr(ptr, (uint) sizeof(Texture))) 
				{
					_currentVFXTextures.Remove(ptr);
					continue;
				}
				else
				{
					var tex = ((Texture*)ptr);
					if (IsBadReadPtr((IntPtr) tex->D3D11Texture2D, (uint) sizeof(void*)))
					{
						_currentVFXTextures.Remove(ptr);
						continue;
					}
					else
					{
						if (!currentTexturePointer.Value) 
						{
							//Completely new Texture arrived from VFX, cant tell if texture is valid or not, just write pointers
							tex->D3D11Texture2D = D3D11Texture2D;
							tex->D3D11ShaderResourceView = D3D11ShaderResourceView;

							_currentVFXTextures[ptr] = true;
							success = true;
						}
						else
						{
							if ((IntPtr)tex->D3D11Texture2D == _oldSharedTexture)
							{
								//Old texture spotted, refresh texture if necessary, otherwise do nothing to it
								if (changingFlag)
								{
									tex->D3D11Texture2D = D3D11Texture2D;
									tex->D3D11ShaderResourceView = D3D11ShaderResourceView;
								}
								success = true;
							}
							else
							{
								//Even though texture has been set once, theres a mismatch in pointers, it might have been disposed, so remove it
								_currentVFXTextures.Remove(ptr);
							}
						}
					}
				}
			}
			if (changingFlag)
			{
				_oldSharedTexture = _currentSharedTexture.NativePointer;
			}

			Services.Log.Debug("TV Redraw Successful");
			return success;
		}
		else
		{
			return false;
		}
		*/
	}

	public void UpdateSharedDXTexture(IntPtr handle)
	{
		Services.Log.Error("THIS SHOULD NOT EXECUTE!");
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
		//_deviceCreateTexture2DHook?.Dispose();
		//_textureOnLoadHook.Dispose();
		//_readSqpackHook.Dispose();
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
		clearTexture();
	}

	private bool ValidateURL(out Uri? url)
	{
		var formattedUrl = _inputURL;

		if (!formattedUrl.StartsWith("http://") && !formattedUrl.StartsWith("https://"))
			formattedUrl = "https://" + formattedUrl;

		return Uri.TryCreate(formattedUrl, UriKind.Absolute, out url)
			   && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && url.Host.Contains(".") && !url.Host.EndsWith(".") && Uri.CheckHostName(url.Host) == UriHostNameType.Dns;
	}


	private bool isFocused = false;
	private String placeHolderURL = String.Empty;
	public override async void Draw()
	{
		if (_signalShareTitle)
		{
			Services.CommandManager.ProcessCommand("/honorific force set picto:" + _shortenedURL + "|silent");
			_signalShareTitle = false;
		}
		
		ImGui.Text(" Available TVs:");
		var npcList = Services.Objects.Where(x => x is IPlayerCharacter).Cast<IPlayerCharacter>().OrderBy(x => x.Name.TextValue);
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
					try
					{
						if (!toggle || (toggle && !string.IsNullOrEmpty(_inputURL) && isPlayer && urlExists))
						{
							/*
							if (!toggle) //If its toggled already, its the Refresh Button, do not turn off TV!
							{
								TurnOffTV(); //Turn off TV before its turned on, as to reset the Textures of any currently running TVs
								//TODO: CHECK IF YOU CAN SWAP TV INSTEAD OF TURNING OFF AND ON
							}
							*/
							TurnOnTV(item.EntityId);
							if (isPlayer)
							{
								placeHolderURL = _inputURL;
								_inputURL = String.Empty;
							}
						}
						else
						{
							TurnOffTV();
							if (isPlayer && string.IsNullOrEmpty(_inputURL))
							{
								_inputURL = placeHolderURL;
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

	private string VFXPath = "chara/monster/m7002/obj/body/b0001/vfx/eff/carbuncleemittor.avfx";
	
	private bool currentVFXActive = false;
	private nint currentVFXObjAddress = 0;
	private nint currentVFXPlrAddress = 0;

	private void RefreshActorVFX()
	{
		if (currentVFXActive)
		{
			try
			{
				ActorVfxCreate(VFXPath, currentVFXPlrAddress, currentVFXObjAddress, -1, (char)0, 0, (char)0);
			}
			catch (Exception e)
			{
				Services.Log.Error(e.ToString());
			}
		}
	}

	
	
	//private const string DeviceCreateTexture2DSig = "E8 ?? ?? ?? ?? 48 89 07 48 8D 7F 20 ?? ?? ?? ??";
	//public const string ReadSqpackSig = "40 56 41 56 48 83 EC ?? 0F BE 02";
	
	public const string ActorVfxCreateSig = "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";

	public const string GetResourceSyncSig = "E8 ?? ?? ?? ?? 48 8B D8 8B C7";
	public const string GetResourceAsyncSig = "E8 ?? ?? ?? 00 48 8B D8 EB ?? F0 FF 83 ?? ?? 00 00";

	public delegate IntPtr ActorVfxCreateDelegate(string path, IntPtr a2, IntPtr a3, float a4, char a5, ushort a6, char a7);
	public ActorVfxCreateDelegate ActorVfxCreate;

	/*
	//private Hook<Device.Delegates.CreateTexture2D> _deviceCreateTexture2DHook;

	[StructLayout(LayoutKind.Explicit)]
	public unsafe struct SeFileDescriptor
	{
		[FieldOffset(0x00)]
		public FileMode FileMode;

		[FieldOffset(0x30)]
		public void* FileDescriptor;

		[FieldOffset(0x50)]
		public ResourceHandle* ResourceHandle;

		[FieldOffset(0x70)]
		public char Utf16FileName;
	}

	public unsafe delegate byte ReadSqpackPrototype(IntPtr fileHandler, SeFileDescriptor* fileDesc, int priority, bool isSync);

	public unsafe delegate void* GetResourceSyncPrototype(IntPtr resourceManager, uint* categoryId, uint* resourceType,
		int* resourceHash, byte* path, void* resParams);

	public unsafe delegate void* GetResourceAsyncPrototype(IntPtr resourceManager, uint* categoryId, uint* resourceType,
		int* resourceHash, byte* path, void* resParams, bool isUnknown);

	private Hook<ReadSqpackPrototype> _readSqpackHook;
	*/
	private Hook<ResourceManager.Delegates.GetResourceSync> _getResourceSyncHook;
	private Hook<Texture.Delegates.InitializeContents> _textureOnLoadHook;

	private unsafe void initHook()
	{
		//var deviceCreateTexture2DAddress = Services.SigScanner.ScanText(DeviceCreateTexture2DSig);
		//_deviceCreateTexture2DHook = Services.InteropProvider.HookFromAddress<Device.Delegates.CreateTexture2D>(deviceCreateTexture2DAddress, DeviceCreateTexture2DDetour);
		//_deviceCreateTexture2DHook.Enable();

		Services.Log.Debug("Init Hooks");
		//_readSqpackHook = Services.InteropProvider.HookFromSignature<ReadSqpackPrototype>(ReadSqpackSig, ReadSqpackDetour);
		//_readSqpackHook.Enable();

		_getResourceSyncHook = Services.InteropProvider.HookFromSignature<ResourceManager.Delegates.GetResourceSync>(GetResourceSyncSig, GetResourceSyncDetour);
		_getResourceSyncHook.Enable();
		_textureOnLoadHook = Services.InteropProvider.HookFromAddress<Texture.Delegates.InitializeContents>(Texture.Addresses.InitializeContents.Value, TexOnLoadDetour);
		var actorVfxCreateAddress = Services.SigScanner.ScanText(ActorVfxCreateSig);
		ActorVfxCreate = Marshal.GetDelegateForFunctionPointer<ActorVfxCreateDelegate>(actorVfxCreateAddress);
		Services.Log.Debug("Init Hooks success");
	}

	private unsafe ResourceHandle* GetResourceSyncDetour(ResourceManager* thisPtr, ResourceCategory* category, uint* type, uint* hash, CStringPointer path, void* unknown)
	{
		if(path.ToString().Contains("chara/monster/m7002/obj/body/b0001/vfx/texture/screentex.atex"))
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
					Services.Log.Debug("TV VFX redraw attempt");
					var tex = _textureOnLoadHook.Original(thisPtr, contents);

					if(thisPtr->D3D11Texture2D != (void*)_currentSharedTexture.NativePointer)
					{
						ShaderResourceView view = new(DxHandler.Device, _currentSharedTexture, new ShaderResourceViewDescription { Format = _currentSharedTexture.Description.Format, Dimension = ShaderResourceViewDimension.Texture2D, Texture2D = { MipLevels = _currentSharedTexture.Description.MipLevels } });
						thisPtr->D3D11Texture2D = (void*)_currentSharedTexture.NativePointer;
						thisPtr->D3D11ShaderResourceView = (void*)view.NativePointer;
						Services.Log.Debug("Successs redraw TV VFX");
					}
					else
					{
						Services.Log.Debug("TV VFX already redrawn");
					}

					return tex;
				}
			}

		}
		catch (Exception ex) { Services.Log.Error(ex.ToString()); }

		return _textureOnLoadHook.Original(thisPtr, contents);
	}
	/*
	private unsafe byte ReadSqpackDetour(IntPtr fileHandler, SeFileDescriptor* fileDesc, int priority, bool isSync)
	{
		if( fileDesc->ResourceHandle == null ) return _readSqpackHook.Original( fileHandler, fileDesc, priority, isSync );
		if (fileDesc->ResourceHandle->FileName.ToString().Contains("/carbunclefinalp12/chara/monster/m7002/obj/body/b0001/vfx/eff/carbuncleemittor.avfx"))
		{
			_textureOnLoadHook.Enable();
			var original = _readSqpackHook.Original(fileHandler, fileDesc, priority, isSync);
			_textureOnLoadHook.Disable();
			return original;
		}
		return _readSqpackHook.Original(fileHandler, fileDesc, priority, isSync);
	}

	private unsafe Texture* DeviceCreateTexture2DDetour(Device* thisPtr, int* size, byte mipLevel, TextureFormat textureFormat, TextureFlags flags, uint unk)
	{
		var currentVFXTexture = _deviceCreateTexture2DHook.Original(thisPtr, size, mipLevel, textureFormat, flags, unk);
		try
		{
			if (mipLevel == 11)
			{
				if(size[0] == 1920)
				{
					if (size[1] == 1080)
					{
						Services.Log.Debug("Spotted new VFX TV Texture" + (IntPtr)currentVFXTexture);
						_currentVFXTextures.TryAdd((IntPtr)currentVFXTexture, false);
					}
				}
			}
		}
		catch(Exception ex)
		{
			Services.Log.Error(ex.ToString());
		}
		return currentVFXTexture;
	}
	*/

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
		//Check for Texture Updates once per sec
		if (_lastMilliSecond + 1000 < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
			_lastMilliSecond = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			CheckAllTVs();
		}
	}

	/*
	private void CheckTitles() //not required anymore, btw causes issues with title/name switching, needs to get checked
	{
		unsafe
		{
			var ratkm = Framework.Instance()->GetUIModule()->GetRaptureAtkModule();
			
			for (var i = 0; i < 50 && i < ratkm->NameplateInfoCount; i++)
			{
				var npi = ratkm->NamePlateInfoEntries[i];
				if (npi.ObjectId == 0 || npi.ClassJobId == 0) continue;
				var cleanTitle = npi.DisplayTitle.ToString();
				if (cleanTitle.Length > 2)
				{
					cleanTitle = cleanTitle.Substring(1, cleanTitle.Length - 2);
					UpdateTitle(npi.ObjectId.ObjectId, cleanTitle);
				}
			}
		}
	}
	*/
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