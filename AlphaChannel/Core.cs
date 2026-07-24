using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.NamePlate;
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
using NoireLib;

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
		OptionFlags = ResourceOptionFlags.None
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
		OptionFlags = ResourceOptionFlags.None
	};
	private CancellationTokenSource _renderCancellation = new();

	private DateTime _lastLoadYT = DateTime.MinValue;
	private static readonly Regex _ytRegex = new(@"^\w+://[^/]*youtube\.\w+/|^\w+://youtu\.be/", RegexOptions.Compiled);
	private static bool IsYTURL(string url) => _ytRegex.IsMatch(url);
	
	private bool _isPlayingSnes;
	private bool _snesControlsEnabled;
	internal InputManager Input { get; }

	internal CoopClient Coop { get; } = new();
	internal bool CoopJoinActive { get; set; }

	internal APIHelper? APIHelper { get; set; }

	private bool _lastIdle = true;
	private readonly List<string> _recentSnesPaths = [];

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

		_recentSnesPaths.AddRange(plugin.Config.RecentPaths);

		Coop.OnRemoteInput += (port, id, pressed) => _snesRenderer?.SetButton(port, id, pressed);

		Services.NamePlateGui.OnNamePlateUpdate += OnNamePlateUpdate;

		HideNearbyNameplates = plugin.Config.HideNearbyNameplates;
		SnesEffect = plugin.Config.SnesEffect;
		SnesEffectMaskStrength = plugin.Config.SnesEffectMaskStrength;
		SnesEffectScanBeam = plugin.Config.SnesEffectScanBeam;
	}

	private bool _hideNearbyNameplates = true;
	internal bool HideNearbyNameplates
	{
		get => _hideNearbyNameplates;
		set
		{
			_hideNearbyNameplates = value;
			_plugin.Config.HideNearbyNameplates = value;
			_plugin.Config.Save();
		}
	}
	private const float NameplateHideRadius = 3.0f;

	private void OnNamePlateUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
	{
		if (!HideNearbyNameplates || _tvOwners.Count == 0) { return; }

		foreach (INamePlateUpdateHandler handler in handlers)
		{
			IGameObject? obj = handler.GameObject;
			if (obj == null) { continue; }

			foreach (IGameObject companion in _tvOwners.Values)
			{
				if (Vector3.Distance(obj.Position, companion.Position) <= NameplateHideRadius)
				{
					handler.VisibilityFlags = 0;
					handler.RemoveName();
					handler.RemoveTitle();
					handler.RemoveFreeCompanyTag();
					handler.RemoveStatusPrefix();
					handler.RemoveTargetSuffix();
					handler.RemoveLevelPrefix();
					break;
				}
			}
		}
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
		if (TVIsActive(Services.LocalPlayerId) && !IsPlayingSnes())
		{
			APIHelper?.OnVideoStopped();
		}

		StopVideoSilent();
	}

	internal void StopVideoSilent()
	{
		_activeEntityId = 0;
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
		PenumbraIPC.RemoveTempMod("screenvfx");
	}

	internal void PlayVideo(uint entityId, string url, int playbackPosition = 0, bool isPlaying = true)
	{
		if (_mpvRenderer != null && _mpvRenderer.GetCurrentUrl() == url && !_mpvRenderer.IsIdle())
		{
			return;
		}

		if (entityId == Services.LocalPlayerId)
		{
			APIHelper?.OnVideoStarted(url, playbackPosition, isPlaying);
		}

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
		if (TVIsActive(Services.LocalPlayerId) && !IsPlayingSnes())
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
		if (TVIsActive(Services.LocalPlayerId) && !IsPlayingSnes())
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
			_snesRenderer ??= new Snes9xRenderer(_plugin);

			if(_plugin.ROMSLocationSnesDir != null)
			{
				AddSnesPath(path);
				_snesControlsEnabled = true;
				_isPlayingSnes = _snesRenderer.Load(_snesScreenTexture, path);
				_snesRenderer.ApplyEffect(SnesEffect, SnesEffectMaskStrength, SnesEffectScanBeam);
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

	internal Snes9xEffect SnesEffect { get; private set; } = Snes9xEffect.CrtScanlines;
	internal float SnesEffectMaskStrength { get; private set; } = 0.30f;
	internal float SnesEffectScanBeam { get; private set; } = 2.5f;

	internal void SetSnesEffect(Snes9xEffect effect, float maskStrength, float scanBeam)
	{
		SnesEffect = effect;
		SnesEffectMaskStrength = maskStrength;
		SnesEffectScanBeam = scanBeam;
		_snesRenderer?.ApplyEffect(effect, maskStrength, scanBeam);

		_plugin.Config.SnesEffect = effect;
		_plugin.Config.SnesEffectMaskStrength = maskStrength;
		_plugin.Config.SnesEffectScanBeam = scanBeam;
		_plugin.Config.Save();
	}

	internal void RemoveSnesPath(string path)
	{
		_recentSnesPaths.Remove(path);
	}
	private void AddSnesPath(string path)
	{
		_recentSnesPaths.Remove(path);
		_recentSnesPaths.Insert(0, path);
		if (_recentSnesPaths.Count > 6)
		{
			_recentSnesPaths.RemoveAt(_recentSnesPaths.Count - 1);
		}
		_plugin.Config.RecentPaths = _recentSnesPaths;
		_plugin.Config.Save();
	}
	internal List<string> GetRecentSnesPaths()
	{
		return [.. _recentSnesPaths];
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
	internal event Action? OnLocalVideoIdle;

	internal void OnFrameworkUpdate()
	{
		if (Services.LocalPlayerExists && TVIsActive(Services.LocalPlayerId) && !IsPlayingSnes())
		{
			bool idle = GetIdle();
			if (idle && !_lastIdle)
			{
				APIHelper?.OnIdleReached();
				OnLocalVideoIdle?.Invoke();
			}
			_lastIdle = idle;
		}
		else
		{
			_lastIdle = true;
		}

		HashSet<int> keyUpEvents = _plugin.WindowKeyUpReader.Consume();

		Input.OnFrameworkUpdate(_isPlayingSnes, _snesControlsEnabled, _snesRenderer, keyUpEvents);

		if (CoopJoinActive && Coop.IsPaired)
		{
			Input.OnFrameworkUpdateAsCoopJoiner(Coop, keyUpEvents);
		}
	}

	internal void RedrawIfNeeded()
	{
		if(_redrawScheduled)
		{
			_redrawScheduled = false;
			_=NoireService.Framework.RunOnTick(() =>
			{
				PenumbraIPC.Redraw(GetCompanionIndex(Services.LocalPlayerId));
			});
		}
	}

	private bool _redrawScheduled;
	internal void ScheduleRedraw()
	{
		_redrawScheduled = true;
	}

	internal unsafe bool ScanForCompanions()
	{
		uint? localPlayerId = Services.Objects.LocalPlayer?.EntityId;
		if(localPlayerId == null)
		{
			return false;
		}

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
				if (character != null)
				{
					uint ownerId = character->CompanionOwnerId;
					_companionOwners.TryAdd(ownerId, item);
					visitedCompanions.Add(ownerId);
					if(character->DrawObject != null)
					{
						if (character->DrawObject->GetObjectType() == ObjectType.CharacterBase)
						{
							try
							{ 
								var tvDraw = (CharacterBase*)character->DrawObject;
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
							catch (Exception) { }
						}
					}

					if (localPlayerId == ownerId)
					{
						playerCarbuncleFound = true;
					}

					if (_tvOwners.TryGetValue(ownerId, out _)) //If entity has been recognized as TV once, keep it playing until its been removed or explicitly turned off to avoid 'sync holes'
					{
						visitedTvs.Add(ownerId);
						CheckoutCompanion(ownerId, item);
						continue;
					}
				}
			}

			//Remove unvisited TVs
			_tvOwners.Where(owner => !visitedTvs.Contains(owner.Key)).Select(owner => owner.Key).ToList().ForEach(ownerId =>
			{
				if (_activeEntityId == ownerId)
				{
					Services.Log.Warning("Stopping Vid owner not found...");
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

	internal void RemoveCompanion()
	{
		_tvOwners.Remove(Services.LocalPlayerId);
	}

	private void CheckoutCompanion(uint ownerId, IGameObject companion)
	{
		if (!_tvOwners.TryGetValue(ownerId, out _))
		{
			_tvOwners.Add(ownerId, companion);
		}
		if (_activeEntityId == ownerId)
		{
			RefreshActorVFX(Services.LocalPlayerAddr, companion.Address); //This TV is active, play its VFX
		}
	}

	private void RefreshActorVFX(nint addrCaster, nint addrTarget)
	{
		if (!PenumbraIPC.CheckTempMod("screenvfx"))
		{
			PenumbraIPC.ApplyTempMod("screenvfx", _plugin.PenumbraTempScreenPaths);
		}
		else
		{
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
						Services.Log.Debug("New Texture detected, but our own texture is disposed, skipping");
						return tex;
					}
					
					if (DxHandler.Device is not { IsDisposed: false })
					{
						return tex;
					}

					nint key = (nint)thisPtr;

					if (_views.TryGetValue(key, out var oldView) && (nint)thisPtr->D3D11Texture2D == texture.NativePointer)
					{
						Services.Log.Debug("New Texture detected, but already hooked, skipping");
						//Detected view on this
						return tex;
					}

					Services.Log.Debug("New Texture detected, assigning...");
					
					var newView = new ShaderResourceView(DxHandler.Device, texture,
											new ShaderResourceViewDescription
											{
												Format = texture.Description.Format,
												Dimension = ShaderResourceViewDimension.Texture2D,
												Texture2D = { MipLevels = texture.Description.MipLevels }
											});

					_views[key] = newView;

					Marshal.AddRef(texture.NativePointer);
					Marshal.AddRef(newView.NativePointer);

					thisPtr->D3D11Texture2D = (void*)texture.NativePointer;
					thisPtr->D3D11ShaderResourceView = (void*)newView.NativePointer;
			}

			return tex;
		}
		catch (Exception ex)
		{
			Services.Log.Error(ex.ToString());
			return false;
		}
	}

	public void Dispose()
	{
		Services.NamePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate;

		PenumbraIPC.Dispose();
		uint localPlayerId = Services.LocalPlayerId;
		if(_tvOwners.TryGetValue(localPlayerId, out _))
		{
			PenumbraIPC.Redraw(GetCompanionIndex(localPlayerId)); //Special case: Redraw one last time after dispose
		}

		_mpvRenderer?.Dispose();
		_snesRenderer?.Dispose();
		Coop.Dispose();

		_textureOnLoadHook.Disable();
		_textureOnLoadHook.Dispose();
		_getResourceSyncHook.Dispose();

		//Do not clean up Texture2D and ShaderResourceView as they may still be part of the currently running VFX
		//Instead just let it stay in the game until it eventually closes, its not growing anyway
		_views.Clear();

		GC.SuppressFinalize(this);
	}
}
