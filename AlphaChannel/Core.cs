using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using InteropGenerator.Runtime;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace AlphaChannel;

public class Core : IDisposable
{
	private MpvRenderer? _currentMpvRenderer;
	private CancellationTokenSource _renderCancellation = new CancellationTokenSource();
	private DateTime _lastLoadYT = DateTime.MinValue;
	private static readonly Regex _ytRegex = new Regex(@"^\w+://[^/]*youtube\.\w+/|^\w+://youtu\.be/", RegexOptions.Compiled);
	private static bool IsYTURL(string url) => _ytRegex.IsMatch(url);
	public event Action? VideoEnded;

	private readonly Dictionary<uint, IntPtr> _companionOwners = []; //Playerpointer, CompanionDrawpointer
	private readonly Texture2D _screenTexture;
	private bool _screenTextureLoaded;
	private uint _activeEntityId;
	private uint _playingEntityId;

	private static Texture2DDescription _texture2dDescription = new Texture2DDescription
	{
		Width = Plugin.ResolutionWidth,
		Height = Plugin.ResolutionHeight,
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
		//INIT TEXTURE
		_screenTexture = new Texture2D(DxHandler.Device, _texture2dDescription);
		using SharpDX.DXGI.Resource resource = _screenTexture.QueryInterface<SharpDX.DXGI.Resource>();
		ClearTexture();

		//INIT HOOKS
		_getResourceSyncHook = Services.InteropProvider.HookFromAddress<ResourceManager.Delegates.GetResourceSync>(ResourceManager.Addresses.GetResourceSync.Value, GetResourceSyncDetour);
		_textureOnLoadHook = Services.InteropProvider.HookFromAddress<Texture.Delegates.InitializeContents>(Texture.Addresses.InitializeContents.Value, TexOnLoadDetour);
		nint actorVfxCreateAddress = Services.SigScanner.ScanText(ActorVfxCreateSig);
		_actorVfxCreate = Marshal.GetDelegateForFunctionPointer<ActorVfxCreateDelegate>(actorVfxCreateAddress);
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
		_currentMpvRenderer?.Stop();
		_playingEntityId = 0;
		_activeEntityId = 0;
	}

	public void PlayVideo(string url, double playbackPosition = 0, bool isPlaying = true)
	{
		if (_currentMpvRenderer != null && _currentMpvRenderer.GetCurrentUrl() == url && !_currentMpvRenderer.IsIdle())
		{
			return;
		}

		int sleepTime = 0;
		if (IsYTURL(url))
		{
			var elapsed = DateTime.Now - _lastLoadYT;
			if (elapsed.TotalSeconds < 5)
			{
				sleepTime = Math.Min(Math.Max((int)(5000 - elapsed.TotalMilliseconds), 0), 5000); //Add some sleep time to avoid hitting rate limits
			}

			_lastLoadYT = DateTime.Now;
		}

		Task.Run(() =>
		{
			Thread.Sleep(sleepTime);
			
			if (_currentMpvRenderer != null)
			{
				_currentMpvRenderer.Play(url, playbackPosition, isPlaying);
				return;
			}
			try
			{
				_currentMpvRenderer = new MpvRenderer();
				_currentMpvRenderer.Initialize(Plugin.ResolutionWidth, Plugin.ResolutionHeight, _screenTexture, _renderCancellation);
				_currentMpvRenderer.Play(url, playbackPosition, isPlaying);
				while (true)
				{
					if (!_currentMpvRenderer.RenderFrame())
					{
						break;
					}
				}
				VideoEnded?.Invoke();
			}
			catch (Exception e)
			{
				Services.Log.Error($"[MPV] Generic error: {e.Message} {e.StackTrace}");
			}
		});
	}

	public void TogglePause()
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			_currentMpvRenderer?.TogglePause();
		}
	}

	public bool? IsIdle()
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			return _currentMpvRenderer?.IsIdle();
		}

		return true;
	}

	public bool GetPaused()
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			return _currentMpvRenderer?.GetPaused() ?? false;
		}

		return false;
	}

	public double[] GetInfo()
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			return _currentMpvRenderer?.GetProperties() ?? [0, 0, 0];
		}

		return [0, 0, 0];
	}

	public void SeekPlayer(int seconds)
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			_currentMpvRenderer?.Seek(seconds);
		}
	}

	public void VolumePlayer(int vol)
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			_currentMpvRenderer?.SetVolume(vol);
		}
	}

	public string GetMediaTitle()
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			return _currentMpvRenderer?.GetMediaTitle() ?? "";
		}

		return "";
	}

	public unsafe Tuple<bool, bool> ScanForCompanions()
	{
		uint? playerId = Services.Objects.LocalPlayer?.EntityId;
		bool modenabled = true;
		bool showWarningMessage = false;

		bool hookEnabled = !_getResourceSyncHook.IsDisposed && _getResourceSyncHook.IsEnabled;
		if (hookEnabled) //Only check for stuff while the hook is activated, which is outside from duties
		{
			List<uint> visitedTvs = [];

			foreach (var item in Services.Objects.Where(x => x is IBattleNpc))
			{
				if (item.Name.TextValue == "Carbuncle")
				{
					if (item.Address == IntPtr.Zero)
					{
						continue;
					}

					var character = (Character*)item.Address;
					if (character != null && character->DrawObject != null)
					{
						if (character->DrawObject->GetObjectType() == ObjectType.CharacterBase)
						{
							try
							{
								var tvDraw = (CharacterBase*)character->DrawObject;
								uint ownerId = character->CompanionOwnerId;
								if (tvDraw->Models[0] is not null)
								{
									if (tvDraw->Models[0]->MaterialCount >= 1)
									{
										if (tvDraw->Models[0]->Materials[0] is not null)
										{
											if (tvDraw->Models[0]->Materials[0]->TextureCount >= 4)
											{
												if (tvDraw->Models[0]->Materials[0]->Textures[3].Texture is not null)
												{
													if (tvDraw->Models[0]->Materials[0]->Textures[3].Texture->Texture is not null)
													{
														if (tvDraw->Models[0]->Materials[0]->Textures[3].Texture->Texture->ActualHeight == 1024
															&& tvDraw->Models[0]->Materials[0]->Textures[3].Texture->Texture->ActualWidth == 1024)
														{
															if (playerId == ownerId)
															{
																modenabled = false;
															}

															visitedTvs.Add(ownerId);
															CheckoutCompanion((IntPtr)tvDraw, ownerId, item.Address);
															continue;
														}
													}
												}
											}
										}
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
			{
				;
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
			if (_activeEntityId != 0)
			{
				StopVideo();
			}
			if (!_getResourceSyncHook.IsDisposed)
			{
				_getResourceSyncHook.Disable();
			}
		}
		else if (!dutyStarted && !hookEnabled)
		{
			if (!_getResourceSyncHook.IsDisposed)
			{
				_getResourceSyncHook.Enable();
			}
		}

		return new Tuple<bool, bool>(modenabled, showWarningMessage);
	}

	private void CheckoutCompanion(IntPtr tvDraw, uint ownerId, nint companionMemoryAddress)
	{
		IntPtr ptr = tvDraw;
		bool tvAddrFound = _companionOwners.TryGetValue(ownerId, out nint tvAddr);
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
			nint? playerMemoryAddress = Services.Objects.LocalPlayer?.Address;
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
			_actorVfxCreate?.Invoke(VFXPath, addrCaster, addrTarget, -1, (char)0, 0, (char)0);
		}
	}

	//https://github.com/0ceal0t/Dalamud-VFXEditor/blob/main/VFXEditor/Interop/Constants.cs
	private const string ActorVfxCreateSig = "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";
	private delegate IntPtr ActorVfxCreateDelegate(string path, IntPtr a2, IntPtr a3, float a4, char a5, ushort a6, char a7);
	private ActorVfxCreateDelegate _actorVfxCreate;

	private Hook<ResourceManager.Delegates.GetResourceSync> _getResourceSyncHook;
	private Hook<Texture.Delegates.InitializeContents> _textureOnLoadHook;

	private const string TEXPath = "chara/monster/m7002/obj/body/b0001/vfx/texture/screentex.atex";
	private unsafe ResourceHandle* GetResourceSyncDetour(ResourceManager* thisPtr, ResourceCategory* category, uint* type, uint* hash, CStringPointer path, void* unknown, void* unkDebugPtr, uint unkDebugInt)
	{
		if (path.ToString().Contains(TEXPath))
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
			{
				return _textureOnLoadHook.Original(thisPtr, contents);
			}

			uint w, h;
			try
			{
				w = thisPtr->ActualWidth;
				h = thisPtr->ActualHeight;
			}
			catch { return _textureOnLoadHook.Original(thisPtr, contents); }

			if (w != 1920 || h != 1080)
			{
				return _textureOnLoadHook.Original(thisPtr, contents);
			}

			bool tex = _textureOnLoadHook.Original(thisPtr, contents);
			if (!tex)
			{
				return tex;
			}

			lock (_screenTextureLock)
			{
				if (_screenTexture is not { IsDisposed: false })
				{
					return tex;
				}

				if (DxHandler.Device is not { IsDisposed: false })
				{
					return tex;
				}

				var view = new ShaderResourceView(DxHandler.Device, _screenTexture,
					new ShaderResourceViewDescription
					{
						Format = _screenTexture.Description.Format,
						Dimension = ShaderResourceViewDimension.Texture2D,
						Texture2D = { MipLevels = _screenTexture.Description.MipLevels }
					});

				thisPtr->D3D11Texture2D = (void*)_screenTexture.NativePointer;
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
		{
			return;
		}

		var rtv = new RenderTargetView(DxHandler.Device, _screenTexture);
		var clearColor = new RawColor4(0.3f, 0.3f, 0.3f, 1);
		DxHandler.Device?.ImmediateContext.ClearRenderTargetView(rtv, clearColor);
	}

	public void Dispose()
	{
		_currentMpvRenderer?.StopRender();

		_textureOnLoadHook.Disable();
		_textureOnLoadHook.Dispose();
		_getResourceSyncHook.Dispose();

		Services.CommandManager.ProcessCommand("/honorific force clear");
		GC.SuppressFinalize(this);
	}

}
