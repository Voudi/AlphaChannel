using System.Numerics;
using System.Text.RegularExpressions;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using static AlphaChannel.APIHelper;

namespace AlphaChannel;

internal sealed class Core : IDisposable
{
	private Plugin _plugin;

	private uint _activeEntityId; //Currently running TV PlayerId
	
	private MpvRenderer? _mpvRenderer;
	private Snes9xRenderer? _snesRenderer;
	private readonly ScreenPainter _screenPainter;
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

	//Default placement for a freshly (re)spawned screen: straight ahead of the local player.
	private const float DefaultScreenSpawnDistance = 2.0f;
	private const float DefaultScreenHeightOffset = 1.0f;

	private readonly List<ScreenPositionPreset> _screenPresets = [];
	internal Vector3 ScreenPosition { get; private set; }
	internal float ScreenYaw { get; private set; }
	internal float ScreenScale { get; private set; } = 1.0f;

	internal Core(Plugin plugin)
	{
		_plugin = plugin;

		Input = new InputManager(plugin);

		_screenTexture = new Texture2D(DxHandler.Device, _texture2dDescription);
		_snesScreenTexture = new Texture2D(DxHandler.Device, _snesTexture2dDescription);
		_screenPainter = new ScreenPainter();

		_recentSnesPaths.AddRange(plugin.Config.RecentPaths);
		_screenPresets.AddRange(plugin.Config.ScreenPresets);

		Coop.OnRemoteInput += (port, id, pressed) => _snesRenderer?.SetButton(port, id, pressed);

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

	internal bool TVIsActive(uint entityId)
	{
		return _activeEntityId == entityId;
	}

	internal bool TVIsVisible(uint entityId)
	{
		return APIHelper?.RemoteStates.TryGetValue(entityId, out _) ?? false;
	}

	internal void StopVideo()
	{
		if (Services.Objects.LocalPlayer?.EntityId is not null && TVIsActive(Services.LocalPlayerId) && !IsPlayingSnes())
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
		_screenPainter.SetTarget(null);
	}

	internal void PlayVideo(uint entityId, string url, int playbackPosition = 0, bool isPlaying = true)
	{
		if (_mpvRenderer != null && _mpvRenderer.GetCurrentUrl() == url && !_mpvRenderer.IsIdle())
		{
			return;
		}

		AssignScreenForSession(entityId, _screenTexture);

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
					_mpvRenderer?.Stop();
					_mpvRenderer = null;
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
				AssignScreenForSession(entityId, _snesScreenTexture);
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

	//Places the screen 2 units in front of (and slightly above) the local player, facing the way they're
	//facing. Called every time the local player's own screen (re)spawns - i.e. whenever a new video/ROM
	//session starts - never while just continuing an already-running one.
	private void SpawnScreenInFrontOfLocalPlayer()
	{
		if (!Services.LocalPlayerExists)
		{
			return;
		}

		var localPlayer = Services.Objects.LocalPlayer!;
		float yaw = localPlayer.Rotation;
		Vector3 forward = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));

		ScreenPosition = localPlayer.Position + forward * DefaultScreenSpawnDistance + new Vector3(0, DefaultScreenHeightOffset, 0);
		ScreenYaw = yaw + MathF.PI; //Face back towards the player, not away from them.
		ScreenScale = 1.0f;

		_screenPainter.SetTransform(ScreenPosition, ScreenYaw, ScreenScale);
	}

	//Live, unsaved position/scale edit from the Settings UI - only meaningful while hosting our own screen.
	internal void SetScreenPosition(Vector3 position, float yaw, float scale)
	{
		ScreenPosition = position;
		ScreenYaw = yaw;
		ScreenScale = scale;

		if (TVIsActive(Services.LocalPlayerId))
		{
			_screenPainter.SetTransform(ScreenPosition, ScreenYaw, ScreenScale);
			APIHelper?.NotifyScreenMoved();
		}
	}

	//Applied when a remote player we're watching moves/rescales their screen (synced via IPCVideoState).
	internal void ApplyRemoteScreenTransform(Vector3 position, float yaw, float scale)
	{
		_screenPainter.SetTransform(position, yaw, scale);
	}

	internal List<ScreenPositionPreset> GetScreenPresets()
	{
		return [.. _screenPresets];
	}

	internal void SaveScreenPreset(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return;
		}

		_screenPresets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
		_screenPresets.Add(new ScreenPositionPreset { Name = name, X = ScreenPosition.X, Y = ScreenPosition.Y, Z = ScreenPosition.Z, RotationDegrees = ScreenYaw * (180f / MathF.PI), Scale = ScreenScale });

		_plugin.Config.ScreenPresets = _screenPresets;
		_plugin.Config.Save();
	}

	internal void RemoveScreenPreset(string name)
	{
		_screenPresets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
		_plugin.Config.ScreenPresets = _screenPresets;
		_plugin.Config.Save();
	}

	internal void ApplyScreenPreset(ScreenPositionPreset preset)
	{
		SetScreenPosition(new Vector3(preset.X, preset.Y, preset.Z), preset.RotationDegrees * (MathF.PI / 180f), preset.Scale);
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

	//Hands the painter its texture and, if this is a genuinely new session (not just the same owner's
	//content continuing/changing), places the screen: 2 units in front of us if we're the one turning it
	//on, or wherever its owner last synced it to if we're joining someone else's. Must run synchronously on
	//the caller's thread, before any outgoing state broadcast (e.g. OnVideoStarted) picks up ScreenPosition -
	//callers of PlayVideo/PlaySnes are always on the main thread already, so this is safe to do directly
	//rather than deferring it to a per-frame poll.
	private void AssignScreenForSession(uint entityId, Texture2D screenTexture)
	{
		bool isNewSession = _activeEntityId != entityId;
		_screenPainter.SetTarget(screenTexture);

		if (!isNewSession)
		{
			return;
		}

		if (entityId == Services.LocalPlayerId)
		{
			SpawnScreenInFrontOfLocalPlayer();
		}
		else if (APIHelper != null && APIHelper.RemoteStates.TryGetValue(entityId, out IPCVideoState? state))
		{
			ApplyRemoteScreenTransform(new Vector3(state.ScreenX, state.ScreenY, state.ScreenZ), state.ScreenYaw, state.ScreenScale);
		}
	}

	public void Dispose()
	{
		_mpvRenderer?.Dispose();
		_snesRenderer?.Dispose();
		_screenPainter.Dispose();
		Coop.Dispose();

		GC.SuppressFinalize(this);
	}
}
