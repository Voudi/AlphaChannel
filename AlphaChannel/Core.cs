using System.Collections.Concurrent;
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
using Dalamud.Game.ClientState.Objects.Enums;

namespace AlphaChannel;

internal sealed class Core : IDisposable
{
	private Plugin _plugin;

	private uint _activeEntityId; //Currently running TV PlayerId
	private readonly Dictionary<uint, IGameObject> _tvOwners = []; //PlayerEntityID, Companion
	private readonly Dictionary<uint, IGameObject> _companionOwners = []; //PlayerEntityID, Companion
	private readonly ConcurrentDictionary<nint, ShaderResourceView> _views = new();

	private MpvRenderer? _mpvRenderer;
	private Snes9xRenderer? _snesRenderer;
	private readonly Texture2D _screenTexture;
	private readonly Texture2D _snesScreenTexture;
	private static Texture2DDescription _texture2dDescription = new Texture2DDescription
	{
		Width = Plugin.ScreenWidth,
		Height = Plugin.ScreenHeight,
		MipLevels = 1,
		ArraySize = 1,
		Format = Format.B8G8R8A8_UNorm,
		BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
		CpuAccessFlags = CpuAccessFlags.None,
		SampleDescription = new SampleDescription(1, 0),
		Usage = ResourceUsage.Default,
		OptionFlags = ResourceOptionFlags.Shared
	};
	private static Texture2DDescription _snesTexture2dDescription = new Texture2DDescription
	{
		Width = Plugin.ScreenWidth,
		Height = Plugin.ScreenHeight,
		MipLevels = 1,
		ArraySize = 1,
		Format = Format.B5G6R5_UNorm,
		BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
		CpuAccessFlags = CpuAccessFlags.None,
		SampleDescription = new SampleDescription(1, 0),
		Usage = ResourceUsage.Default,
		OptionFlags = ResourceOptionFlags.Shared
	};
	private CancellationTokenSource _renderCancellation = new();

	private DateTime _lastLoadYT = DateTime.MinValue;
	private static readonly Regex _ytRegex = new(@"^\w+://[^/]*youtube\.\w+/|^\w+://youtu\.be/", RegexOptions.Compiled);
	private static bool IsYTURL(string url) => _ytRegex.IsMatch(url);
	
	private bool _isPlayingSnes;
	private bool _snesControlsEnabled;
	internal InputManager Input { get; }

	internal APIHelper? APIHelper { get; set; }

	private bool _lastIdle = true;

	internal unsafe Core(Plugin plugin)
	{
		_plugin = plugin;

		Input = new InputManager(plugin);

		_screenTexture = new Texture2D(DxHandler.Device, _texture2dDescription);
		_snesScreenTexture = new Texture2D(DxHandler.Device, _snesTexture2dDescription);

		_getResourceSyncHook = Services.InteropProvider.HookFromAddress<ResourceManager.Delegates.GetResourceSync>(ResourceManager.Addresses.GetResourceSync.Value, GetResourceSyncDetour);
		_textureOnLoadHook = Services.InteropProvider.HookFromAddress<Texture.Delegates.InitializeContents>(Texture.Addresses.InitializeContents.Value, TexOnLoadDetour);
		nint actorVfxCreateAddress = Services.SigScanner.ScanText(ActorVfxCreateSig);
		_actorVfxCreate = Marshal.GetDelegateForFunctionPointer<ActorVfxCreateDelegate>(actorVfxCreateAddress);

		_getResourceSyncHook.Enable();
	}

	internal bool TVIsActive(uint entityId)
	{
		return _activeEntityId == entityId;
	}

	internal bool TVIsVisible(uint entityId)
	{
		return _tvOwners.TryGetValue(entityId, out _);
	}

	internal ushort GetCompanionIndex(uint entityId)
	{
		if(!_companionOwners.TryGetValue(entityId, out IGameObject? result))
		{
			return ushort.MaxValue;
		}
		return result.ObjectIndex;
	}

	internal void StopVideo()
	{
		uint? localPlayerId = Services.LocalPlayerId;
		if (localPlayerId.HasValue && TVIsActive(localPlayerId.Value) && !IsPlayingSnes())
		{
			APIHelper?.OnVideoStopped();
		}

		StopVideoSilent();
	}

	internal void StopVideoSilent()
	{
		if (_isPlayingSnes)
		{
			_snesRenderer?.Unload();
			_isPlayingSnes = false;
		}
		else
		{
			_mpvRenderer?.Stop();
			_mpvRenderer = null;
		}
		_activeEntityId = 0;
	}

	internal void PlayVideo(uint entityId, string url, int playbackPosition = 0, bool isPlaying = true)
	{
		if (_mpvRenderer != null && _mpvRenderer.GetCurrentUrl() == url && !_mpvRenderer.IsIdle())
		{
			return;
		}

		uint? localPlayerId = Services.LocalPlayerId;
		if (entityId == localPlayerId)
		{
			APIHelper?.OnVideoStarted(url, playbackPosition, isPlaying);
		}

		ClearTexture(_screenTexture);

		Task.Run(async () =>
		{
			if (IsYTURL(url))
			{
				TimeSpan elapsed = DateTime.Now - _lastLoadYT;
				if (elapsed.TotalSeconds < 7)
				{
					int sleepTime = Math.Min(Math.Max((int)(7000 - elapsed.TotalMilliseconds), 0), 7000); //Add some sleep time to avoid hitting rate limits
					Thread.Sleep(sleepTime);
				}
				_lastLoadYT = DateTime.Now;
			}
			
			try
			{
				if (_mpvRenderer != null)
				{
					_mpvRenderer.Play(url, playbackPosition, isPlaying);
					_activeEntityId = entityId;
					return;
				}
				_mpvRenderer = new MpvRenderer();
				_mpvRenderer.Initialize(Plugin.ScreenWidth, Plugin.ScreenHeight, _screenTexture, _renderCancellation);
				_mpvRenderer.Play(url, playbackPosition, isPlaying);
				_activeEntityId = entityId;
				while (true)
				{
					if (!_mpvRenderer.RenderFrame())
					{
						break;
					}
				}
				Services.Log.Debug("Stopping Video Player");
			}
			catch (Exception e)
			{
				Services.Log.Error($"[MPV] Generic error: {e.Message} {e.StackTrace}");
			}
		});
	}

	internal void Pause(bool pause)
	{
		uint? localPlayerId = Services.LocalPlayerId;
		if (localPlayerId.HasValue && TVIsActive(localPlayerId.Value) && !IsPlayingSnes())
		{
			APIHelper?.OnPaused(pause);
		}

		PauseSilent(pause);
	}

	internal void PauseSilent(bool pause)
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			_mpvRenderer?.Pause(pause);
		}
	}

	internal bool GetIdle()
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			return _mpvRenderer?.IsEofReached() ?? true;
		}

		return true;
	}

	internal bool GetPaused()
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			return _mpvRenderer?.GetPaused() ?? false;
		}

		return false;
	}

	internal double[] GetInfo()
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			return _mpvRenderer?.GetProperties() ?? [0, 0, 0];
		}

		return [0, 0, 0];
	}

	internal void Seek(int seconds)
	{
		uint? localPlayerId = Services.LocalPlayerId;
		if (localPlayerId.HasValue && TVIsActive(localPlayerId.Value) && !IsPlayingSnes())
		{
			APIHelper?.OnSeeked(seconds);
		}

		SeekSilent(seconds);
	}

	internal void SeekSilent(int seconds)
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			_mpvRenderer?.Seek(seconds);
		}
	}

	internal void SetVolume(int vol)
	{
		if (_isPlayingSnes)
		{
			_snesRenderer?.SetVolume(vol);
		}
		else
		{
			if (!_renderCancellation.Token.IsCancellationRequested)
			{
				_mpvRenderer?.SetVolume(vol);
			}
		}

	}

	internal string GetMediaTitle()
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			return _mpvRenderer?.GetMediaTitle() ?? string.Empty;
		}
		return string.Empty;
	}

	internal string? GetCurrentUrl()
	{
		return _mpvRenderer?.GetCurrentUrl();
	}

	internal bool ValidateURL(string inputUrl, out Uri? url)
	{
		string formattedUrl = inputUrl;

		if (!formattedUrl.StartsWith("http://", StringComparison.Ordinal) && !formattedUrl.StartsWith("https://", StringComparison.Ordinal))
		{
			formattedUrl = "https://" + formattedUrl;
		}

		bool result = Uri.TryCreate(formattedUrl, UriKind.Absolute, out url) && (url?.Scheme == Uri.UriSchemeHttp || url?.Scheme == Uri.UriSchemeHttps) && url.Host.Contains('.') && !url.Host.EndsWith('.') && Uri.CheckHostName(url.Host) == UriHostNameType.Dns;

		if (!result)
		{
			return false;
		}

		return result;
	}

	internal bool PlaySnes(uint entityId, string path)
	{
		try
		{
			ClearTexture(_snesScreenTexture);

			_snesRenderer ??= new Snes9xRenderer(_plugin);

			if(_plugin.ROMSLocationSnesDir != null)
			{
				_snesControlsEnabled = true;
				_isPlayingSnes = _snesRenderer.Load(_snesScreenTexture, path);
				_activeEntityId = entityId;
			}
			Services.Log.Debug("Starting ROM");
		}
		catch (Exception e)
		{
			Services.Log.Error($"[SNES9X] Generic error: {e.Message} {e.StackTrace}");
		}

		return _isPlayingSnes;
	}
	internal bool IsPlayingSnes()
	{
		return _isPlayingSnes;
	}
	internal bool IsSnesControlsEnabled()
	{
		return _snesControlsEnabled;
	}
	internal void EnableSnesControls(bool enabled)
	{
		_snesControlsEnabled = enabled;
	}
	internal void OnFrameworkUpdate()
	{
		uint? localPlayerId = Services.LocalPlayerId;
		if (localPlayerId.HasValue && TVIsActive(localPlayerId.Value) && !IsPlayingSnes())
		{
			bool idle = GetIdle();
			if (idle && !_lastIdle)
			{
				APIHelper?.OnIdleReached();
			}
			_lastIdle = idle;
		}
		else
		{
			_lastIdle = true;
		}

		Input.OnFrameworkUpdate(_isPlayingSnes, _snesControlsEnabled, _snesRenderer);
	}

	internal unsafe bool ScanForCompanions()
	{
		uint? localPlayerId = Services.Objects.LocalPlayer?.EntityId;
		bool playerCarbuncleFound = false;

		bool hookEnabled = !_getResourceSyncHook.IsDisposed && _getResourceSyncHook.IsEnabled;
		if (hookEnabled) //Only check for stuff while the hook is activated, which is outside from duties
		{
			List<uint> visitedTvs = [];
			List<uint> visitedCompanions = [];

			foreach (var item in Services.Objects.Where(x => x is IBattleNpc && x.BaseId == 13498 && x.ObjectKind is ObjectKind.BattleNpc))
			{
				if (item.Address == IntPtr.Zero)
				{
					continue;
				}
				var character = (Character*)item.Address;
				if (character != null && character->DrawObject != null)
				{
					if (character->DrawObject->GetObjectType() == FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
					{
						try
						{ 
							var tvDraw = (CharacterBase*)character->DrawObject;
							uint ownerId = character->CompanionOwnerId;
							_companionOwners.TryAdd(ownerId, item);
							visitedCompanions.Add(ownerId);
							if (tvDraw->Models[0] is not null) //TODO: find a better checking method
							{ //Actually, its not so bad checking it like this, wysiwyg
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
														visitedTvs.Add(ownerId);
														CheckoutCompanion(ownerId, item);
														continue;
													}
												}
											}
										}
									}
								}
							}
							if (_tvOwners.TryGetValue(ownerId, out _) && ownerId != localPlayerId) //If entity has been recognized as TV once, keep it playing until its been removed or explicitly turned off to avoid 'sync holes', except for localplayer
							{
								visitedTvs.Add(ownerId);
								CheckoutCompanion(ownerId, item);
								continue;
							}

							if (localPlayerId == ownerId)
							{
								playerCarbuncleFound = true;
							}
						}
						catch (Exception) { }
					}
				}
			}

			//Remove unvisited TVs
			_tvOwners.Where(owner => !visitedTvs.Contains(owner.Key)).Select(owner => owner.Key).ToList().ForEach(ownerId =>
			{
				if (_activeEntityId == ownerId)
				{
					StopVideo();
				}
				_tvOwners.Remove(ownerId);
			});

			//Remove unvisited Companions
			_companionOwners.Where(owner => !visitedCompanions.Contains(owner.Key)).Select(owner => owner.Key).ToList().ForEach(ownerId =>
			{
				_companionOwners.Remove(ownerId);
				_tvOwners.Remove(ownerId);
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

		return playerCarbuncleFound;
	}

	private void CheckoutCompanion(uint ownerId, IGameObject companion)
	{
		if (!_tvOwners.TryGetValue(ownerId, out _))
		{
			_tvOwners.Add(ownerId, companion);
		}
		if (_activeEntityId == ownerId)
		{
			RefreshActorVFX(companion.Address, companion.Address); //This TV is active, play its VFX
		}
	}

	private void RefreshActorVFX(nint addrCaster, nint addrTarget)
	{
		if (!PenumbraIPC.CheckTempMod("screenvfx"))
		{
			PenumbraIPC.ApplyTempMod("screenvfx", Services.Objects.LocalPlayer?.ObjectIndex, _plugin.PenumbraTempScreenPaths);
		}
		lock (_screenTextureLock)
		{
			if(_isPlayingSnes)
			{
				_actorVfxCreate?.Invoke("chara/monster/m7002/obj/body/b0001/vfx/texture/snesscreen_"+_plugin.PluginSessionGUID+".avfx", addrCaster, addrTarget, -1, (char)0, 0, (char)0);
			}
			else
			{
				_actorVfxCreate?.Invoke("chara/monster/m7002/obj/body/b0001/vfx/texture/alphachannelscreen_"+_plugin.PluginSessionGUID+".avfx", addrCaster, addrTarget, -1, (char)0, 0, (char)0);
			}
		}
	}

	//https://github.com/0ceal0t/Dalamud-VFXEditor/blob/main/VFXEditor/Interop/Constants.cs
	private const string ActorVfxCreateSig = "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";
	private delegate IntPtr ActorVfxCreateDelegate(string path, IntPtr a2, IntPtr a3, float a4, char a5, ushort a6, char a7);
	private readonly ActorVfxCreateDelegate _actorVfxCreate;


	private readonly Hook<ResourceManager.Delegates.GetResourceSync> _getResourceSyncHook;
	private readonly Hook<Texture.Delegates.InitializeContents> _textureOnLoadHook;

	private unsafe ResourceHandle* GetResourceSyncDetour(ResourceManager* thisPtr, ResourceCategory* category, uint* type, uint* hash, CStringPointer path, void* unknown, void* unkDebugPtr, uint unkDebugInt)
	{
		if (path.ToString().Contains("chara/monster/m7002/obj/body/b0001/vfx/texture/alphachannelscreentex"))
		{
			_texCase = 1;
		}
		else if (path.ToString().Contains("chara/monster/m7002/obj/body/b0001/vfx/texture/snesscreentex"))
		{
			_texCase = 2;
		}
		if (_texCase > 0)
		{
			_textureOnLoadHook.Enable(); //Enable Texturehook only for the duration of the specific Resource Load, as hooking Textures from Kernel is unsafe and expensive
			ResourceHandle* ret = _getResourceSyncHook.Original(thisPtr, category, type, hash, path, unknown, unkDebugPtr, unkDebugInt);
			_textureOnLoadHook.Disable();
			Services.Log.Debug("Screen Texture load attempt:" + path.ToString());
			_texCase = 0;
			return ret;
		}
		else
		{
			return _getResourceSyncHook.Original(thisPtr, category, type, hash, path, unknown, unkDebugPtr, unkDebugInt);
		}
	}
	
	private readonly Lock _screenTextureLock = new();
	private int _texCase;
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
					var texture = _texCase == 2 ? _snesScreenTexture : _screenTexture;

					if (texture is not { IsDisposed: false })
					{
						return tex;
					}
					
					if (DxHandler.Device is not { IsDisposed: false })
					{
						return tex;
					}

					var newView = new ShaderResourceView(DxHandler.Device, texture,
											new ShaderResourceViewDescription
											{
												Format = texture.Description.Format,
												Dimension = ShaderResourceViewDimension.Texture2D,
												Texture2D = { MipLevels = texture.Description.MipLevels }
											});

					nint key = (nint)thisPtr;

					if (_views.TryGetValue(key, out var oldView))
					{
						oldView.Dispose();
						_views[key] = newView;
					}
					else
					{
						_views[key] = newView;
					}

					nint oldTexPtr = (nint)thisPtr->D3D11Texture2D;
					nint oldSrvPtr = (nint)thisPtr->D3D11ShaderResourceView;

					thisPtr->D3D11Texture2D = (void*)texture.NativePointer;
					thisPtr->D3D11ShaderResourceView = (void*)newView.NativePointer;

					//Release the old TX and SRV
					Marshal.AddRef(oldTexPtr);
					int texCount = Marshal.Release(oldTexPtr);
					Marshal.AddRef(oldSrvPtr);
					int srvCount = Marshal.Release(oldSrvPtr);

					if (texCount == 1) {
						Marshal.Release(oldTexPtr);
					}
					if (srvCount == 1) { 
						Marshal.Release(oldSrvPtr);
					}
			}

			return tex;
		}
		catch (Exception ex)
		{
			Services.Log.Error(ex.ToString());
			return false;
		}
	}

	private void ClearTexture(Texture2D texture)
	{
		DeviceContext? ctx = DxHandler.DrawDevice?.ImmediateContext;
		if (ctx == null || texture == null) { return; }

		int w = texture.Description.Width;
		int h = texture.Description.Height;

		byte[] gray = new byte[w * h * 4];
		for (int i = 0; i < gray.Length; i += 4)
		{
			gray[i] = 77;
			gray[i+1] = 77;
			gray[i+2] = 77;
			gray[i+3] = 255;
		}

		var handle = GCHandle.Alloc(gray, GCHandleType.Pinned);
		try
		{
			ctx.UpdateSubresource(texture, 0, null, handle.AddrOfPinnedObject(), w * 4, 0);
			ctx.Flush();
		}
		finally { handle.Free(); }
	}

	public void Dispose()
	{
		_mpvRenderer?.Dispose();
		_snesRenderer?.Dispose();

		_textureOnLoadHook.Disable();
		_textureOnLoadHook.Dispose();
		_getResourceSyncHook.Dispose();

		//Do not clean up Texture2D and ShaderResourceView as they may still be part of the currently running VFX
		//Instead just let it stay in the game until it eventually closes, its not growing anyway
		_views.Clear();

		GC.SuppressFinalize(this);
	}
}
