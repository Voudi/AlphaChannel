using System.Collections.Concurrent;
using System.Diagnostics;
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
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;

namespace AlphaChannel;

public class Core : IDisposable
{
	private MpvRenderer? _currentMpvRenderer;
	private Snes9xRenderer? _snesRenderer;
	private CancellationTokenSource _renderCancellation = new CancellationTokenSource();
	private DateTime _lastLoadYT = DateTime.MinValue;
	private static readonly Regex _ytRegex = new Regex(@"^\w+://[^/]*youtube\.\w+/|^\w+://youtu\.be/", RegexOptions.Compiled);
	private static bool IsYTURL(string url) => _ytRegex.IsMatch(url);

	private readonly Dictionary<uint, IGameObject> _tvOwners = []; //PlayerEntityID, Companion
	private readonly Dictionary<uint, IGameObject> _companionOwners = []; //PlayerEntityID, Companion
	private readonly Texture2D _screenTexture;
	private readonly Texture2D _snesScreenTexture;
	private readonly ConcurrentDictionary<nint, ShaderResourceView> _views = new();
	private uint _activeEntityId;
	private uint _playingEntityId;
	private uint? LocalEntityId => Services.Objects?.LocalPlayer?.EntityId;
	
	private bool _isPlayingSnes;
	private bool _snesControlsEnabled;
	private Plugin _plugin;

	// Snes Key Inputs
	public Dictionary<Snes9xInput, VirtualKey> SnesKeys { get; set; } = [];

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
	private static Texture2DDescription _snesTexture2dDescription = new Texture2DDescription
	{
		Width = Plugin.ResolutionWidth,
		Height = Plugin.ResolutionHeight,
		MipLevels = 1,
		ArraySize = 1,
		Format = Format.B5G6R5_UNorm,
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
		_snesScreenTexture = new Texture2D(DxHandler.Device, _snesTexture2dDescription);

		using SharpDX.DXGI.Resource resource = _screenTexture.QueryInterface<SharpDX.DXGI.Resource>();
		ClearTexture();

		using SharpDX.DXGI.Resource snesResource = _snesScreenTexture.QueryInterface<SharpDX.DXGI.Resource>();
		ClearTexture();

		//INIT HOOKS
		_getResourceSyncHook = Services.InteropProvider.HookFromAddress<ResourceManager.Delegates.GetResourceSync>(ResourceManager.Addresses.GetResourceSync.Value, GetResourceSyncDetour);
		_textureOnLoadHook = Services.InteropProvider.HookFromAddress<Texture.Delegates.InitializeContents>(Texture.Addresses.InitializeContents.Value, TexOnLoadDetour);
		nint actorVfxCreateAddress = Services.SigScanner.ScanText(ActorVfxCreateSig);
		_actorVfxCreate = Marshal.GetDelegateForFunctionPointer<ActorVfxCreateDelegate>(actorVfxCreateAddress);
		_getResourceSyncHook.Enable();


		List<Snes9xInput> keyOrder = [Snes9xInput.UP, Snes9xInput.DOWN, Snes9xInput.LEFT, Snes9xInput.RIGHT, Snes9xInput.A, Snes9xInput.B, Snes9xInput.X, Snes9xInput.Y, Snes9xInput.L, Snes9xInput.R, Snes9xInput.START, Snes9xInput.SELECT];
		foreach(Snes9xInput key in keyOrder)
		{
			if(plugin.Config.KeyMappings.TryGetValue(key, out VirtualKey virtualKey))
			{
				SnesKeys.Add(key, virtualKey);
			}
			else
			{
				SnesKeys.Add(key, VirtualKey.NO_KEY);
			}
		}

		
	}

	public bool IsTVTurnedOff()
	{
		return _activeEntityId == 0;
	}

	public bool IsLocalPlayerTVOn()
	{
		return _activeEntityId == LocalEntityId;
	}
	public bool IsEntityTVOn(uint entityId)
	{
		return _activeEntityId == entityId;
	}

	public bool TVExistsForEntity(uint entityId)
	{
		return _tvOwners.TryGetValue(entityId, out _);
	}

	public IGameObject? GetCompanion(uint entityId)
	{
		if(!_companionOwners.TryGetValue(entityId, out IGameObject? result))
		{
			Services.Log.Warning("Could not find companion for entity " + entityId);
		}
		return result;
	}

	public void SetCurrentTV(uint entityId)
	{
		_activeEntityId = entityId;
	}

	public async Task<bool> IsVideo(string url)
	{
		if (IsYTURL(url))
		{
			return true;
		}
		if(_plugin.AssemblyLocationYTDLP == null)
		{
			return false;
		}
			
		var psi = new ProcessStartInfo(_plugin.AssemblyLocationYTDLP, $"-j --no-playlist \"{url}\"")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using var proc = Process.Start(psi)!;
		await proc.WaitForExitAsync();
		return proc.ExitCode == 0;
	}

	public void StopVideo()
	{
		if (_isPlayingSnes)
		{
			_snesRenderer?.Unload();
			_isPlayingSnes = false;
		}
		_currentMpvRenderer?.Stop();
		_currentMpvRenderer?.Dispose();
		_currentMpvRenderer = null;
		_playingEntityId = 0;
		_activeEntityId = 0;
		ClearTexture();
	}

	public void PlayVideo(string url, int playbackPosition = 0, bool isPlaying = true)
	{
		if (_currentMpvRenderer != null && _currentMpvRenderer.GetCurrentUrl() == url && !_currentMpvRenderer.IsIdle())
		{
			return;
		}

		Task.Run(async () =>
		{
			/*
			bool checkVideo = await IsVideo(url);
			if(!checkVideo)
			{
				_plugin.ErrorPopup("No video found for url: " + url);
				StopVideo();
				return;
			}
			*/
			int sleepTime = 0;
			if (IsYTURL(url))
			{
				var elapsed = DateTime.Now - _lastLoadYT;
				if (elapsed.TotalSeconds < 5)
				{
					sleepTime = Math.Min(Math.Max((int)(7000 - elapsed.TotalMilliseconds), 0), 7000); //Add some sleep time to avoid hitting rate limits
				}

				_lastLoadYT = DateTime.Now;
			}

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
				Services.Log.Debug("Stopping Video Player");
			}
			catch (Exception e)
			{
				Services.Log.Error($"[MPV] Generic error: {e.Message} {e.StackTrace}");
			}
		});
	}

	public void Pause(bool pause)
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			_currentMpvRenderer?.Pause(pause);
		}
	}

	public bool IsIdle()
	{
		if (!_renderCancellation.Token.IsCancellationRequested)
		{
			return _currentMpvRenderer?.IsEofReached() ?? true;
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

	public string? GetCurrentUrl()
	{
		return _currentMpvRenderer?.GetCurrentUrl();
	}


	public bool IsPlayingSnes()
	{
		return _isPlayingSnes;
	}

	public bool IsSnesControlsEnabled()
	{
		return _snesControlsEnabled;
	}
	public void EnableSnesControls(bool enabled)
	{
		_snesControlsEnabled = enabled;
	}
	public bool PlayGame(string path)
	{
		if (_snesRenderer == null)
		{
			_snesRenderer = new Snes9xRenderer(_plugin);
		}
		try
		{
			if(Plugin.ROMSLocationSnesDir != null)
			{
				_snesControlsEnabled = true;
				_isPlayingSnes = _snesRenderer.Load(_snesScreenTexture, path);
			}
			Services.Log.Debug("Starting ROM");
		}
		catch (Exception e)
		{
			Services.Log.Error($"[SNES9X] Generic error: {e.Message} {e.StackTrace}");
		}

		return _isPlayingSnes;
	}
	public unsafe bool ScanForCompanions()
	{
		uint? playerId = LocalEntityId;
		bool playerCarbuncleFound = false;

		bool hookEnabled = !_getResourceSyncHook.IsDisposed && _getResourceSyncHook.IsEnabled;
		if (hookEnabled) //Only check for stuff while the hook is activated, which is outside from duties
		{
			List<uint> visitedTvs = [];
			List<uint> visitedCompanions = [];

			foreach (var item in Services.Objects.Where(x => x is ICharacter))
			{
				if (item.BaseId == 13498 && item.ObjectKind.Equals(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)) //Wanderers Campfire: (item.BaseId == 414 && item.ObjectKind.Equals(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion)) 
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
								if (_tvOwners.TryGetValue(ownerId, out _) && ownerId != LocalEntityId) //If entity has been recognized as TV once, keep it playing until its been removed or explicitly turned off to avoid 'sync holes', except for localplayer
								{
									visitedTvs.Add(ownerId);
									CheckoutCompanion(ownerId, item);
									continue;
								}

								if (playerId == ownerId)
								{
									playerCarbuncleFound = true;
								}
							}
							catch (Exception) { }
						}
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
		if (_activeEntityId == ownerId) //This TV is supposed to be active...
		{
			if (_playingEntityId != _activeEntityId) //...But it's not active, activate it
			{
				_playingEntityId = ownerId;
			}
			else
			{
				RefreshActorVFX(companion.Address, companion.Address); //This TV is active, play its VFX
			}
		}
	}

	private void RefreshActorVFX(nint addrCaster, nint addrTarget)
	{
		if (!PenumbraIPC.CheckTempMod("screenvfx"))
		{
			PenumbraIPC.ApplyTempMod("screenvfx", Services.Objects?.LocalPlayer?.ObjectIndex, _plugin.PenumbraTempScreenPaths);
		}
		lock (_screenTextureLock)
		{
			if(_isPlayingSnes)
			{
				_actorVfxCreate?.Invoke("chara/monster/m7002/obj/body/b0001/vfx/texture/snesscreen_"+Plugin.PluginSessionGUID+".avfx", addrCaster, addrTarget, -1, (char)0, 0, (char)0);
			}
			else
			{
				_actorVfxCreate?.Invoke("chara/monster/m7002/obj/body/b0001/vfx/texture/alphachannelscreen_"+Plugin.PluginSessionGUID+".avfx", addrCaster, addrTarget, -1, (char)0, 0, (char)0);
			}
		}
	}

	//https://github.com/0ceal0t/Dalamud-VFXEditor/blob/main/VFXEditor/Interop/Constants.cs
	private const string ActorVfxCreateSig = "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";
	private delegate IntPtr ActorVfxCreateDelegate(string path, IntPtr a2, IntPtr a3, float a4, char a5, ushort a6, char a7);
	private ActorVfxCreateDelegate _actorVfxCreate;


	private Hook<ResourceManager.Delegates.GetResourceSync> _getResourceSyncHook;
	private Hook<Texture.Delegates.InitializeContents> _textureOnLoadHook;

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
			_textureOnLoadHook.Enable(); //Enable Texturehook only for the duration of the Resource Load, as hooking Textures from Kernel is unsafe and expensive
			var ret = _getResourceSyncHook.Original(thisPtr, category, type, hash, path, unknown, unkDebugPtr, unkDebugInt);
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
		_snesRenderer?.Dispose();

		_textureOnLoadHook.Disable();
		_textureOnLoadHook.Dispose();
		_getResourceSyncHook.Dispose();
		_snesRenderer?.Dispose();

		//Do not clean up Texture2D and ShaderResourceView as they may still be part of the currently running VFX
		//Instead just let it stay in the game until it eventually closes, its not growing anyway
		_views.Clear();

		Services.CommandManager.ProcessCommand("/honorific force clear");
		GC.SuppressFinalize(this);
	}

	private readonly Dictionary<VirtualKey, bool> _heldState = new();

	internal void OnFrameworkUpdate()
	{
		HashSet<int> KeyUpEvents = _plugin.WindowKeyUpReader.Consume();
		
		if (!_isPlayingSnes || !_snesControlsEnabled)
		{
			return;
		}
		foreach(Snes9xInput key in SnesKeys.Keys)
		{
			if(SnesKeys.TryGetValue(key, out VirtualKey virtualKey) && virtualKey != VirtualKey.NO_KEY)
			{
				bool pressed = Services.KeyState[virtualKey];

				_heldState.TryGetValue(virtualKey, out bool held);
				if (pressed) { held = true; }
				if (KeyUpEvents.Contains((int)virtualKey)) { held = false; }
				_heldState[virtualKey] = held;

				_snesRenderer?.SetButton(0, (int)key, held);

				if (pressed)
				{
					Services.KeyState[virtualKey] = false; //Disable Key for Game
				}
			}
		}
		_snesRenderer?.OnFrameworkUpdate();
	}

	internal void VolumeSnes(int vol)
	{
		_snesRenderer?.SetVolume(vol);
	}

	//Reading KeyUp Events from the window itself since Dalamud is consuming the entire KeyState when disabling KeyDown
	public class WndProcKeyUpReader : IDisposable
	{
		[DllImport("user32.dll", SetLastError = true)]
		private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
		[DllImport("user32.dll")]
		private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

		private const int GWLP_WNDPROC = -4;
		private const uint WM_KEYUP = 0x0101;


		[UnmanagedFunctionPointer(CallingConvention.Winapi)]
		private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

		private readonly IntPtr _hwnd;
		private readonly WndProcDelegate _hook;
		private IntPtr _oldWndProc;
		private bool _installed;

		private readonly HashSet<int> _releasedKeys = new();
		private readonly Lock _lock = new();

		public WndProcKeyUpReader(IntPtr hwnd)
		{
			_hwnd = hwnd;
			_hook = WndProcHook;
			_oldWndProc = SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_hook));
			_installed = _oldWndProc != IntPtr.Zero;
		}

		private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
		{
			if (msg == WM_KEYUP)
			{
				lock (_lock) { _releasedKeys.Add((int)(wParam.ToInt64() & 0xFFFF)); }
			}

			return CallWindowProcW(_oldWndProc, hWnd, msg, wParam, lParam);
		}

		public HashSet<int> Consume()
		{
			lock (_lock) 
			{ 
				var result = _releasedKeys.ToHashSet();
				_releasedKeys.Clear();
				return result;
			}
		}

		public void Dispose()
		{
			if (_installed && _oldWndProc != IntPtr.Zero)
			{
				SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, _oldWndProc);
				_oldWndProc = IntPtr.Zero;
				_installed = false;
			}
			GC.SuppressFinalize(this);
		}
	}
}
