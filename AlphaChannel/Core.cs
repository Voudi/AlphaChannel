using AlphaChannel;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using SharpDX.Direct3D11;
using SharpDX.Direct3D;
using Dalamud.Game.ClientState.Objects.Types;
using System.Runtime.InteropServices;
using SharpDX.DXGI;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using InteropGenerator.Runtime;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using SharpDX.Mathematics.Interop;
public class Core
{
    private Plugin _plugin;
    private readonly Dictionary<uint, IntPtr> _companionOwners = []; //Playerpointer, CompanionDrawpointer
	private readonly Texture2D _screenTexture;
	private bool _screenTextureLoaded = false;
	private uint _activeEntityId; //Playerpointer (whether TV toggled or not)
	private uint _playingEntityId = 0; //Playerpointer (whether TV toggled or not)

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
		_screenTexture = new Texture2D(DxHandler.Device, _texture2dDescription);
		using SharpDX.DXGI.Resource resource = _screenTexture.QueryInterface<SharpDX.DXGI.Resource>();
		ClearTexture();

		//INIT HOOKS
		_getResourceSyncHook = Services.InteropProvider.HookFromAddress<ResourceManager.Delegates.GetResourceSync>(ResourceManager.Addresses.GetResourceSync.Value, GetResourceSyncDetour);
		_textureOnLoadHook = Services.InteropProvider.HookFromAddress<Texture.Delegates.InitializeContents>(Texture.Addresses.InitializeContents.Value, TexOnLoadDetour);
		var actorVfxCreateAddress = Services.SigScanner.ScanText(ActorVfxCreateSig);
		ActorVfxCreate = Marshal.GetDelegateForFunctionPointer<ActorVfxCreateDelegate>(actorVfxCreateAddress);
		_getResourceSyncHook.Enable();
    }

    public bool IsTVTurnedOff()
    {
        return _activeEntityId == 0;
    }

    public bool IsPlayerTVOn()
    {
        return _activeEntityId == Services.Objects.LocalPlayer?.EntityId;
    }
    public bool IsEntityTVOn(uint entityId)
    {
        return _activeEntityId == entityId;
    }

    public bool TextureExists()
    {
        return _screenTextureLoaded;
    }

    public bool TVExistsForEntity(uint entityId)
    {
        return _companionOwners.TryGetValue(entityId, out _);
    }

    public void SetCurrentTV(uint entityId)
    {
        _activeEntityId = entityId;
    }

	public void StopVideo()
	{
		ClearTexture();
		_plugin.StopPlayer();
		_playingEntityId = 0;
		_activeEntityId = 0;
	}

    public void PlayVideo(string url)
    {
        _plugin.StartMPV(url, _screenTexture);
    }

    public unsafe Tuple<bool, bool> ScanForCompanions()
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
															CheckoutCompanion((IntPtr)tvDraw, ownerId, item.Address);
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
			_companionOwners.Where(owner => !visitedTvs.Contains(owner.Key)).Select(owner => owner.Key).ToList().ForEach(ownerId =>
			{;
				if (_activeEntityId == ownerId)
				{
                    StopVideo();
				}
				_companionOwners.Remove(ownerId);
			});
        }

		//Disable hook during duties
        bool dutyStarted = Services.DutyState.IsDutyStarted;
        if (dutyStarted && hookEnabled)
		{
			if(_activeEntityId != 0) {
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

        return new Tuple<bool, bool>(modenabled, showWarningMessage);
	}

	private void CheckoutCompanion(IntPtr tvDraw, uint ownerId, nint companionMemoryAddress)
	{
        _plugin.CheckURLHook();
		IntPtr ptr = tvDraw;
		var tvAddrFound = _companionOwners.TryGetValue(ownerId, out var tvAddr);
		if (!tvAddrFound)
		{
			_companionOwners.Add(ownerId, ptr);
		}
		else if (tvAddrFound && tvAddr != ptr)
		{
			_playingEntityId = 0; //Texturepointer has changed, assume no TV is running for it to get reassigned
			_companionOwners[ownerId] = ptr;
		}
		if (_activeEntityId == ownerId) //This TV is supposed to be active...
		{
			var playerMemoryAddress = Services.Objects.LocalPlayer?.Address;
			if (_playingEntityId != _activeEntityId) //...But it's not active
			{
				_playingEntityId = ownerId;
			}
			else
			{
				RefreshActorVFX(playerMemoryAddress ?? companionMemoryAddress, companionMemoryAddress); //This TV is active, play its VFX
			}
		}
	}

    private const string VFXPath = "chara/monster/m7002/obj/body/b0001/vfx/eff/carbuncleemittor.avfx";

	private void RefreshActorVFX(nint addrCaster, nint addrTarget)
	{
        lock (_screenTextureLock)
        {
		    ActorVfxCreate?.Invoke(VFXPath, addrCaster, addrTarget, -1, (char)0, 0, (char)0);
        }
	}

	//https://github.com/0ceal0t/Dalamud-VFXEditor/blob/main/VFXEditor/Interop/Constants.cs
	private const string ActorVfxCreateSig = "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";
	private delegate IntPtr ActorVfxCreateDelegate(string path, IntPtr a2, IntPtr a3, float a4, char a5, ushort a6, char a7);
	private ActorVfxCreateDelegate ActorVfxCreate;

	private Hook<ResourceManager.Delegates.GetResourceSync> _getResourceSyncHook;
	private Hook<Texture.Delegates.InitializeContents> _textureOnLoadHook;

	private const string TEXPath = "chara/monster/m7002/obj/body/b0001/vfx/texture/screentex.atex";
	private unsafe ResourceHandle* GetResourceSyncDetour(ResourceManager* thisPtr, ResourceCategory* category, uint* type, uint* hash, CStringPointer path, void* unknown, void* unkDebugPtr, uint unkDebugInt)
	{
		if(path.ToString().Contains(TEXPath))
		{
			_textureOnLoadHook.Enable(); //Enable Texturehook only for the duration of the Resource Load, as hooking Textures from Kernel is unsafe and expensive
			var ret = _getResourceSyncHook.Original(thisPtr, category, type, hash, path, unknown, unkDebugPtr, unkDebugInt);
			_textureOnLoadHook.Disable();
			
			return ret;
		}
		else
		{
			return _getResourceSyncHook.Original(thisPtr, category, type, hash, path, unknown, unkDebugPtr, unkDebugInt);
		}
	}
    private readonly Lock _screenTextureLock = new();
    private unsafe bool TexOnLoadDetour(Texture* thisPtr, void* contents)
    {
        try
        {
            if (thisPtr == null) 
                return _textureOnLoadHook.Original(thisPtr, contents);

            uint w, h;
            try
            {
                w = thisPtr->ActualWidth;
                h = thisPtr->ActualHeight;
            }
            catch { return _textureOnLoadHook.Original(thisPtr, contents); }

            if (w != 1920 || h != 1080)
                return _textureOnLoadHook.Original(thisPtr, contents);

            var tex = _textureOnLoadHook.Original(thisPtr, contents);
            if (!tex) return tex;

            lock (_screenTextureLock)
            {
                if (_screenTexture is not { IsDisposed: false }) return tex;
                if (DxHandler.Device is not { IsDisposed: false }) return tex;

                var view = new ShaderResourceView(DxHandler.Device, _screenTexture,
                    new ShaderResourceViewDescription
                    {
                        Format = _screenTexture.Description.Format,
                        Dimension = ShaderResourceViewDimension.Texture2D,
                        Texture2D = { MipLevels = _screenTexture.Description.MipLevels }
                    });

                thisPtr->D3D11Texture2D          = (void*)_screenTexture.NativePointer;
                thisPtr->D3D11ShaderResourceView = (void*)view.NativePointer;
                _screenTextureLoaded = true;
            }

            return tex;
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex.ToString());
            return false;
        }
    }
    private void ClearTexture()
	{
		if (_screenTexture == null || DxHandler.Device == null)
			return;
		var rtv = new RenderTargetView(DxHandler.Device, _screenTexture);
		var clearColor = new RawColor4(0.3f, 0.3f, 0.3f, 1);
		DxHandler.Device?.ImmediateContext.ClearRenderTargetView(rtv, clearColor);
    }

    public void Dispose()
	{
        _textureOnLoadHook.Disable();
        _textureOnLoadHook.Dispose();
        _getResourceSyncHook.Dispose();

		Services.CommandManager.ProcessCommand("/honorific force clear");
	}

}