using AlphaChannel;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Dalamud.Bindings.ImGui;
using SharpDX.Direct3D11;
using SharpDX.Direct3D;
using Dalamud.Game.ClientState.Objects.Types;
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
public class Core
{
    private readonly Dictionary<uint, IntPtr> _currentOwners = []; //Playerpointer, CompanionDrawpointer
	private readonly Texture2D _currentSharedTexture;
	private bool _textureLoaded = false;
    public string currentSharedTextureResourceHandle;
	private uint _currentToggle; //Playerpointer (whether TV toggled or not)
	private uint _currentActivatedTV = 0; //Playerpointer (whether TV toggled or not)

    private Plugin _plugin;

	private static Texture2DDescription _texture2dDescription = new Texture2DDescription
	{
		Width = Plugin._resolutionWidth,
		Height = Plugin._resolutionHeight,
		MipLevels = 1,
		ArraySize = 1,
		Format = Format.B8G8R8A8_UNorm,
		BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
		CpuAccessFlags = CpuAccessFlags.None,
		SampleDescription = new SampleDescription(1, 0),
		Usage = ResourceUsage.Default,
		OptionFlags = ResourceOptionFlags.Shared
	};

    public unsafe Core(Plugin plugin)
    {
        _plugin = plugin;

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
    }

    public void ClearTexture()
	{
		if (_currentSharedTexture == null || DxHandler.Device == null)
			return;
		var rtv = new RenderTargetView(DxHandler.Device, _currentSharedTexture);
		var clearColor = new RawColor4(0.3f, 0.3f, 0.3f, 1);
		DxHandler.Device?.ImmediateContext.ClearRenderTargetView(rtv, clearColor);
    }

    public bool IsTVTurnedOff()
    {
        return _currentToggle == 0;
    }

    public bool IsPlayerTVOn()
    {
        return _currentToggle == Services.Objects.LocalPlayer?.EntityId;
    }
    public bool IsEntityTVOn(uint entityId)
    {
        return _currentToggle == entityId;
    }

    public bool TextureExists()
    {
        return _textureLoaded;
    }

    public bool TVExistsForEntity(uint entityId)
    {
        return _currentOwners.TryGetValue(entityId, out _);
    }

    public void SetCurrentTV(uint entityId)
    {
        _currentToggle = entityId;
    }

	public void StopVideo()
	{
		ClearTexture();
		_plugin.StopPlayer();
		_currentActivatedTV = 0;
		_currentToggle = 0;
	}

    public void PlayVideo(string url)
    {
        _plugin.StartMPV(url, _currentSharedTexture);
    }

    public unsafe bool[] ScanTVs()
	{
        var playerId = Services.Objects.LocalPlayer?.EntityId;
        var modenabled = true;
        var showWarningMessage = false;

        bool hookEnabled = !_getResourceSyncHook.IsDisposed && _getResourceSyncHook.IsEnabled;
        if (hookEnabled) //Only check for stuff while the hook is activated, which is outside from duties
		{
			List<uint> visitedTvs = [];

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
																modenabled = false;

                                                            visitedTvs.Add(ownerId);
															DetectTV((IntPtr)tvDraw, ownerId, item.Address);
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

			//Remove unvisited TVs
			_currentOwners.Where(owner => !visitedTvs.Contains(owner.Key)).Select(owner => owner.Key).ToList().ForEach(ownerId =>
			{;
				if (_currentToggle == ownerId)
				{
                    StopVideo();
				}
				_currentOwners.Remove(ownerId);
			});
        }

		//Disable hook during duties
        bool dutyStarted = Services.DutyState.IsDutyStarted;
        if (dutyStarted && hookEnabled)
		{
			if(_currentToggle != 0) {
                StopVideo();
            }
            if(!_getResourceSyncHook.IsDisposed)
				_getResourceSyncHook.Disable();
		}
		else if (!dutyStarted && !hookEnabled)
        {
            if (!_getResourceSyncHook.IsDisposed)
                _getResourceSyncHook.Enable();
		}

        return [modenabled, showWarningMessage];
	}

	private void DetectTV(IntPtr tvDraw, uint ownerId, nint address)
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
	private unsafe ResourceHandle* GetResourceSyncDetour(ResourceManager* thisPtr, ResourceCategory* category, uint* type, uint* hash, CStringPointer path, void* unknown, void* unkDebugPtr, uint unkDebugInt)
	{
		if(path.ToString().Contains(TEXPath))
		{
			_textureOnLoadHook.Enable();
			var ret = _getResourceSyncHook.Original(thisPtr, category, type, hash, path, unknown, unkDebugPtr, unkDebugInt);
			_textureOnLoadHook.Disable();
			
			return ret;
		}
		else
		{
			return _getResourceSyncHook.Original(thisPtr, category, type, hash, path, unknown, unkDebugPtr, unkDebugInt);
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
    public void Dispose()
	{
        _textureOnLoadHook.Disable();
        _textureOnLoadHook.Dispose();
        _getResourceSyncHook.Dispose();

		Services.CommandManager.ProcessCommand("/honorific force clear");
	}

}