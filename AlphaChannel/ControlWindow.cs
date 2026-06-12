using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;

using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;

namespace AlphaChannel;

public class ControlWindow : Window, IDisposable
{
	private bool _isTVPoweredOff;
	private bool _pauseToggle;
	private Plugin _plugin;
	private Compatibility _compat;
	private Core _core;
	private uint? LocalEntityId => Services.Objects?.LocalPlayer?.EntityId;

	//Render Vars
	private string _inputURL = "";
	private float _volume = 25;
	private float _seeker;
	private double _seekerExactTime;
	private int _seekerTimeSeconds;
	private int _seekerTimeMinutes;
	private int _seekerDurationSeconds;
	private int _seekerDurationMinutes;
	private int _seekerMaxSeconds;
	private bool _mpvIsIdle = true;
	private string _mediaTitle = string.Empty;
	private bool _libsLoaded;
	private bool _updatingLibs;
	private bool _uiElementActive;
	private DateTime _lastTextureDebugMsg = DateTime.MinValue;

	private readonly Dictionary<uint, IPCVideoState> _currentStates = []; //PlayerEntityID, IPCVideoState
	public sealed record IPCVideoState([property: JsonRequired] string State, [property: JsonRequired] string Url, [property: JsonRequired] int PlaybackPosition, [property: JsonRequired] long Timestamp);

	private IPCVideoState? _localPlayerState;

	public ControlWindow(Plugin plugin, string title)
		: base(title, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
	{
		_plugin = plugin;

		_compat = new Compatibility(_plugin);

		_core = new Core(_plugin);
		_core.VideoEnded += StopVideo;

		SizeConstraints = new WindowSizeConstraints
		{
			MinimumSize = new Vector2(275, 235),
			MaximumSize = new Vector2(275, 1080)
		};

		_compat.CheckForUpdates();
	}

	public void Dispose()
	{
		_plugin.UpdateIPCState(null);
		_core.VideoEnded -= StopVideo;
		_core.Dispose();
		GC.SuppressFinalize(this);
	}

	private bool _isFocused;
	private string _placeHolderURL = string.Empty;
	private IEnumerable<IGameObject> _playerList = [];
	public override void Draw()
	{
		Vector4 textColor;
		bool playerIsRunningTV = _core.IsLocalPlayerTVOn();

		if (Services.DutyState.IsDutyStarted)
		{
			ImGui.Text("AlphaChannel is deactivated");
			ImGui.Text("while in duties.");
			return;
		}
		if (!_libsLoaded)
		{
			bool needsFirstInstall = _plugin.AssemblyLocationMPV == null || _plugin.AssemblyLocationYTDLP == null;
			bool updatesAvailable = (_plugin.LibResources.MpvCheckResult[0] != string.Empty) || (_plugin.LibResources.YtdlpCheckResult[0] != string.Empty);

			_libsLoaded = !needsFirstInstall;
			if (!_libsLoaded)
			{
				if (_updatingLibs)
				{
					ImGui.Text("Installing dependencies...");
					return;
				}
				ImGui.Text("Please download the required dependencies to use AlphaChannel:");
				if (!updatesAvailable)
				{
					ImGui.BeginDisabled();
				}

				if (ImGui.Button(updatesAvailable ? "Install dependencies" : "Checking for updates..."))
				{
					Services.Log.Debug("Installing AlphaChannel Dependencies...");
					if (_plugin.AssemblyLocationMPV == null || _plugin.LibResources.MpvCheckResult[0] != string.Empty)
					{
						_plugin.LibResources.DownloadMPVAsync().ContinueWith(async task =>
						{
							if (task.Result)
							{
								Services.Log.Debug("MPV downloaded successfully");
								_plugin.AssemblyLocationMPV = _plugin.LibResources.GetLocationMPV()!;
								_plugin.LibResources.MpvCheckResult[0] = string.Empty;
							}
							else
							{
								Services.Log.Error("Failed to download MPV");
							}
						});
					}

					if (_plugin.AssemblyLocationYTDLP == null || _plugin.LibResources.YtdlpCheckResult[0] != string.Empty)
					{
						_plugin.LibResources.DownloadYTDLPAsync().ContinueWith(async task =>
						{
							if (task.Result)
							{
								Services.Log.Debug("YTDLP downloaded successfully");
								_plugin.AssemblyLocationYTDLP = _plugin.LibResources.GetLocationYTDLP()!;
								_plugin.LibResources.YtdlpCheckResult[0] = string.Empty;
							}
							else
							{
								Services.Log.Error("Failed to download YTDLP");
							}
						});
					}

					_updatingLibs = true;
				}
				if (!updatesAvailable)
				{
					ImGui.EndDisabled();
				}

				return;
			}

		}
		if (!_core.IsTVTurnedOff() && !_core.TextureExists() && _lastTextureDebugMsg.AddSeconds(5) < DateTime.Now)
		{
			ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " Error: Cannot Fetch Screen Texture");
			ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " 1. Keep the plugin activated");
			ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " 2. Restart the game client, or");
			ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " 3. Teleport to another zone");
			ImGui.Separator();
		}

		foreach (var item in _playerList)
		{
			bool isPlayer = item.EntityId == LocalEntityId;

			if ((isPlayer && _isTVPoweredOff) || _core.TVExistsForEntity(item.EntityId)) //Checks if TV exists or if it's the player and the TV is powered off
			{
				bool isTheRunningTV = _core.IsEntityTVOn(item.EntityId);
				string url = string.Empty;
				bool urlExists = false;
				bool urlEmpty = string.IsNullOrEmpty(_inputURL);

				if (isPlayer)
				{
					urlExists = ValidateURL(out _);
				}
				else
				{
					if (_currentStates.TryGetValue(item.EntityId, out IPCVideoState? tempURL))
					{
						url = tempURL.Url;
						urlExists = true;
					}
				}

				if (isPlayer)
				{
					textColor = urlExists || (isTheRunningTV && urlEmpty) ? new Vector4(0.3f, 0.8f, 0.3f, 1f) : new Vector4(0.8f, 0.3f, 0.3f, 1f);

					if (!isTheRunningTV)
					{
						ImGui.PushStyleColor(ImGuiCol.Border, textColor);
					}

					ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);

					ImGui.SetNextItemWidth(235);
					ImGui.InputText("##URL", ref _inputURL, 1000, ImGuiInputTextFlags.None);

					ImGui.PopStyleVar();
					if (!isTheRunningTV)
					{
						ImGui.PopStyleColor();
					}

					// Detect if the input is focused
					if (ImGui.IsItemActive())
					{
						_isFocused = true;
					}
					else if (ImGui.IsItemDeactivated())
					{
						_isFocused = false;
					}

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
							placeholder = placeholder[..^1];
							textSize = ImGui.CalcTextSize(placeholder + "........");
						}

						if (!placeholder.Equals(_placeHolderURL, StringComparison.Ordinal))
						{
							placeholder += "...";
						}

						ImGui.GetWindowDrawList().AddText(new Vector2(pos.X + 3, pos.Y + 2), ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 1.0f)), placeholder);
					}
				}
				else
				{
					if (isTheRunningTV)
					{
						ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.3f, 1.0f), url);
					}
					else if (!urlExists)
					{
						ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Not sharing anything");
					}
					else
					{
						ImGui.Text(url);
					}
				}

				if (urlExists || isPlayer)
				{
					ImGui.SameLine();

					ImGui.PushFont(UiBuilder.IconFont);
					if (ImGui.Button(FontAwesomeIcon.Clipboard.ToIconString() + "##clipboard" + item.EntityId))
					{
						ImGui.SetClipboardText(isPlayer ? (string.IsNullOrEmpty(_inputURL) && isTheRunningTV ? _placeHolderURL : _inputURL) : (url ?? string.Empty));
					}
					ImGui.PopFont();
					if (ImGui.IsItemHovered())
					{
						ImGui.BeginTooltip();
						ImGui.Text("Copy URL to clipboard");
						ImGui.EndTooltip();
					}
				}

				if (isTheRunningTV && _seekerExactTime > 0)
				{
					DrawScrollingText(_mediaTitle, 250);
				}
				else
				{
					ImGui.Text(item.Name.TextValue);
				}

				
				if (isTheRunningTV)
				{
					if (!isPlayer)
					{
						ImGui.BeginDisabled();
					}

					ImGui.SetNextItemWidth(268);
					ImGui.PushStyleColor(ImGuiCol.SliderGrab, new Vector4(0.8f, 0.3f, 0.3f, 1));
					ImGui.SliderFloat("##seeker" + item.EntityId, ref _seeker, 0, 100, $"{_seekerTimeMinutes}:{_seekerTimeSeconds:00} / {_seekerDurationMinutes}:{_seekerDurationSeconds:00}");
					if (ImGui.IsItemActive())
					{
						_uiElementActive = true;
					}

					if (ImGui.IsItemDeactivatedAfterEdit())
					{
						SeekPlayer(_seeker);
						_uiElementActive = false;
					}
					ImGui.PopStyleColor(1);
				}
				
				if(isPlayer)
				{
					ImGui.PushStyleColor(ImGuiCol.Text, _isTVPoweredOff ? new Vector4(1.0f, 1.0f, 1.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f));

					ImGui.PushFont(UiBuilder.IconFont);

					if(ImGui.Button(FontAwesomeIcon.PowerOff.ToIconString() + "##power" + item.EntityId))
					{
						if (_isTVPoweredOff)
						{
							PenumbraIPC.ApplyTempMod(Services.Objects.LocalPlayer?.ObjectIndex, _plugin.PenumbraTempModPaths);
						}
						else
						{
							PenumbraIPC.RemoveTempMod();
						}
						PenumbraIPC.Redraw(_core.GetCompanion(item.EntityId)?.ObjectIndex ?? -1);
					}

					ImGui.PopFont();
					ImGui.PopStyleColor();
				}

				if (_isTVPoweredOff)
				{
					ImGui.Separator();

					continue;
				}
				ImGui.SameLine();

				bool refreshNeeded = isTheRunningTV && !string.IsNullOrEmpty(_inputURL) && isPlayer && urlExists;

				if (isTheRunningTV)
				{
					textColor = isPlayer ? (refreshNeeded ? new Vector4(0.0f, 1.0f, 1.0f, 1.0f) : new Vector4(1.0f, 0.0f, 0.0f, 1.0f)) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f);
					ImGui.PushStyleColor(ImGuiCol.Text, textColor);
				}
				else if (!urlExists)
				{
					ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1.0f));
				}
				ImGui.PushFont(UiBuilder.IconFont);

				if (ImGui.Button((isTheRunningTV ?
					(refreshNeeded ?
						FontAwesomeIcon.ArrowRight.ToIconString()
						: FontAwesomeIcon.Stop.ToIconString()
					)
					: FontAwesomeIcon.Play.ToIconString()
					) + "##play" + item.EntityId))
				{
					try
					{
						if (!isTheRunningTV || refreshNeeded)
						{
							if (urlExists)
							{
								StartVideo(item.EntityId);
							}

							if (isPlayer)
							{
								_placeHolderURL = _inputURL;
								_inputURL = string.Empty;
							}
						}
						else
						{
							StopVideo();
						}

					}
					catch (Exception ex)
					{
						Services.Log.Error("FATAL ERROR: " + ex.ToString());
					}
				}
				ImGui.PopFont();

				if (isTheRunningTV || !urlExists)
				{
					ImGui.PopStyleColor();
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.BeginTooltip();
					ImGui.Text(

						isTheRunningTV ?
							(!string.IsNullOrEmpty(_inputURL) && isPlayer && urlExists ? "Visit new URL"
							 : "Stop"
						)
						: "Play"
					);
					ImGui.EndTooltip();
				}

				if (isTheRunningTV && isPlayer)
				{
					ImGui.SameLine();

					ImGui.PushFont(UiBuilder.IconFont);
					if (ImGui.Button(_mpvIsIdle ? FontAwesomeIcon.Repeat.ToIconString() : (_pauseToggle ? FontAwesomeIcon.Play.ToIconString() : FontAwesomeIcon.Pause.ToIconString()) + "##forceplay" + item.EntityId))
					{
						if (_mpvIsIdle)
						{
							SeekPlayer(0);
							_core.Pause(false);
							_pauseToggle = false;
						}
						else
						{
							_pauseToggle = !_pauseToggle;
							_core.Pause(_pauseToggle);
						}
					}
					ImGui.PopFont();
					if (ImGui.IsItemHovered())
					{
						ImGui.BeginTooltip();
						if (_mpvIsIdle)
						{
							ImGui.Text("Replay");
						}
						else if (_pauseToggle)
						{
							ImGui.Text("Pause");
						}
						else
						{
							ImGui.Text("Resume");
						}
						ImGui.EndTooltip();
					}

					ImGui.SameLine();

					ImGui.PushFont(UiBuilder.IconFont);
					ImGui.SetNextItemWidth(100);
					ImGui.SliderFloat("##volumebar" + item.EntityId, ref _volume, 0, 100, _volume < 1 ? FontAwesomeIcon.VolumeMute.ToIconString() : (_volume <= 60 ? FontAwesomeIcon.VolumeDown.ToIconString() : FontAwesomeIcon.VolumeUp.ToIconString()));
					if (ImGui.IsItemActive())
					{
						_uiElementActive = true;
					}

					if (ImGui.IsItemDeactivatedAfterEdit())
					{
						VolumePlayer(_volume);
						_uiElementActive = false;
					}
					ImGui.PopFont();
				}

				ImGui.Separator();
			}
			else
			{
				if (isPlayer)
				{
					ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: You have not summoned");
					ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " your Wanderer's Campfire.");
					ImGui.Separator();
				}
			}
		}
	}

	public void StartVideo(uint entityId)
	{
		if (LocalEntityId == entityId)
		{
			if (ValidateURL(out Uri? uri) && uri != null)
			{
				_core.SetCurrentTV(entityId);
				_lastTextureDebugMsg = DateTime.Now;

				_localPlayerState = new("playing", Uri.EscapeDataString(uri.ToString()), 0, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
				_plugin.UpdateIPCState(_localPlayerState);
				
				_core.PlayVideo(uri.ToString());
			}
		}
		else
		{
			if (_currentStates.TryGetValue(entityId, out IPCVideoState? stateInfo))
			{
				string url = stateInfo.Url;

				_core.SetCurrentTV(entityId);
				_lastTextureDebugMsg = DateTime.Now;

				bool result = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri?.Scheme == Uri.UriSchemeHttp || uri?.Scheme == Uri.UriSchemeHttps) && uri.Host.Contains('.') && !uri.Host.EndsWith('.') && Uri.CheckHostName(uri.Host) == UriHostNameType.Dns;

				if (!result)
				{
					Services.Log.Error("Failed fetching URL for player " + entityId);
					return;
				}

				int getTimeDiff = (int) (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - stateInfo.Timestamp);
				_core.PlayVideo(url, stateInfo.PlaybackPosition + getTimeDiff, stateInfo.State == "playing");
			}
		}
	}

	public void StopVideo()
	{
		_core.StopVideo();
		if (string.IsNullOrEmpty(_inputURL) && !string.IsNullOrEmpty(_placeHolderURL))
		{
			_inputURL = _placeHolderURL;
			_placeHolderURL = string.Empty;
		}
	}

	public bool ValidateURL(out Uri? url)
	{
		string formattedUrl = _inputURL;

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

	private long _lastMilliSecond1000ms;
	private long _lastMilliSecond6ms;
	public void Refresh()
	{
		if (_lastMilliSecond6ms + 6 < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
			_lastMilliSecond6ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			GetCoreInfo();
		}
		if (_lastMilliSecond1000ms + 1000 < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
			_lastMilliSecond1000ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			_isTVPoweredOff = _core.ScanForCompanions();

			_playerList = Services.Objects.Where(x => x is IPlayerCharacter).OrderBy(x => (x.EntityId == LocalEntityId) ? "@" : x.Name.TextValue);
		}
	}

	private void VolumePlayer(float volume)
	{
		int vol = (int)((float)Math.Sqrt(volume) * 10f); //Quadratic Slider Valuess
		Services.Log.Debug("Setting volume to " + vol + "%");
		_core.VolumePlayer(vol);
	}

	private void SeekPlayer(double percentage)
	{
		int seconds = (int)(_seekerMaxSeconds * (percentage / 100));
		Services.Log.Debug("Seeking to " + seconds + " seconds");

		IPCVideoState? state = IPCGetState();
		if(state != null)
		{
			state = new IPCVideoState(state.State, state.Url, seconds, state.Timestamp);
			_localPlayerState = state;
			_plugin.UpdateIPCState(state);
		}

		_core.SeekPlayer(seconds);
	}

	private void GetCoreInfo()
	{
		if (!_core.IsTVTurnedOff())
		{
			double[] info = _core.GetInfo();
			string title = _core.GetMediaTitle();
			_mediaTitle = title;
			

			double time = info[0];
			
			_seekerExactTime = time;
			_seekerTimeMinutes = (int)(time / 60);
			_seekerTimeSeconds = (int)(time % 60);
			double duration = info[1];
			if (duration > 0)
			{
				_seekerMaxSeconds = (int)duration;
				_seekerDurationMinutes = (int)(duration / 60);
				_seekerDurationSeconds = (int)(duration % 60);
			}

			if (!_uiElementActive)
			{
				if (duration > 0)
				{
					_seeker = (float)(duration > 0 ? time / duration * 100 : 100);
				}

				double volume = info[2];
				_volume = (float)volume / 100f * ((float)volume / 100f) * 100f; //Quadratic Slider Values
			}
		}
		
		if(_localPlayerState != null && !_core.IsLocalPlayerTVOn()) //Level 1 - TV is has been turned completely off
		{
			_localPlayerState = null;
			_plugin.UpdateIPCState(_localPlayerState);
		}
		else if(_mpvIsIdle != _core.IsIdle()) //Level 2 - TV has been turned idle
		{
			_mpvIsIdle = _core.IsIdle();
			_pauseToggle = true;
			_localPlayerState = IPCGetState();
			_plugin.UpdateIPCState(_localPlayerState);
		}
		else if(_pauseToggle != _core.GetPaused()) //Level 3 - TV has been paused
		{
			_pauseToggle = _core.GetPaused();
			_localPlayerState = IPCGetState();
			_plugin.UpdateIPCState(_localPlayerState);
		}
	}

	private float _scrollOffset;
	private float _pauseTimer;
	private int _phase;
	private string? _lastText;
	private double _lastTime = ImGui.GetTime();
	private void DrawScrollingText(string text, float maxWidth)
	{
		var textSize = ImGui.CalcTextSize(text);

		if (textSize.X <= maxWidth)
		{
			ImGui.Text(text);
			return;
		}

		if (text != _lastText)
		{
			_lastText = text;
			_scrollOffset = 0;
			_pauseTimer = 0;
			_phase = 0;
		}

		double now = ImGui.GetTime();
		float dt = (float)(now - _lastTime);
		_lastTime = now;

		const float pauseDuration = 3f;
		const float scrollSpeed = 50f;
		float maxScroll = textSize.X - maxWidth;

		switch (_phase)
		{
			case 0:
				_scrollOffset = 0;
				_pauseTimer += dt;
				if (_pauseTimer >= pauseDuration)
				{
					_phase = 1;
					_pauseTimer = 0;
				}
				break;

			case 1:
				_scrollOffset += dt * scrollSpeed;
				if (_scrollOffset >= maxScroll)
				{
					_scrollOffset = maxScroll;
					_phase = 2;
				}
				break;

			case 2:
				_scrollOffset = maxScroll;
				_pauseTimer += dt;
				if (_pauseTimer >= pauseDuration)
				{
					_phase = 0;
					_pauseTimer = 0;
					_scrollOffset = 0;
				}
				break;
		}

		var pos = ImGui.GetCursorScreenPos();
		var drawList = ImGui.GetWindowDrawList();
		drawList.PushClipRect(pos, new Vector2(pos.X + maxWidth, pos.Y + textSize.Y), true);
		drawList.AddText(new Vector2(pos.X - _scrollOffset, pos.Y),
						ImGui.GetColorU32(ImGuiCol.Text), text);
		drawList.PopClipRect();

		ImGui.Dummy(new Vector2(maxWidth, textSize.Y));
	}

	public void RemoveOtherPlayer(nint addr)
	{
		uint player = _playerList.FirstOrDefault(player => player.Address == addr)?.EntityId ?? 0;
		if (LocalEntityId != player && player != 0)
		{
			_currentStates.Remove(player);
			if (_core.IsEntityTVOn(player))
			{
				StopVideo();
			}
		}
	}

	public IPCVideoState? IPCGetState()
	{
		string? url = _core.GetCurrentUrl();
		int pos = _seekerTimeMinutes * 60 + _seekerTimeSeconds;
		IPCVideoState? state = null;

		if(_core.IsLocalPlayerTVOn() && !string.IsNullOrEmpty(url) && _core.GetPaused()) //LocalPlayer TV is on and video is paused
		{
			state = new IPCVideoState("paused", Uri.EscapeDataString(url), pos, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
		}
		else if(_core.IsLocalPlayerTVOn() && !string.IsNullOrEmpty(url) && !_core.GetPaused()) //LocalPlayer TV is on and video is playing
		{
			state = new IPCVideoState("playing", Uri.EscapeDataString(url), pos, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
		}

		return state;
	}

	public void IPCSetState(nint addr, string stateJSON)
	{
		int pos = _seekerTimeMinutes * 60 + _seekerTimeSeconds;
		uint player = _playerList.FirstOrDefault(player => player.Address == addr)?.EntityId ?? 0;
		if (player == LocalEntityId)
		{
			return;
		}
		if (LocalEntityId != player && player != 0)
		{
			if(stateJSON == null)
			{
				_currentStates.Remove(player);
				if (_core.IsEntityTVOn(player))
				{
					StopVideo();
				}
			}
			else
			{
				IPCVideoState? state = JsonSerializer.Deserialize<IPCVideoState>(stateJSON);
				if (state != null)
				{
					_currentStates.TryGetValue(player, out IPCVideoState? oldState);
					state = _currentStates[player] = new IPCVideoState(state.State, Uri.UnescapeDataString(state.Url), state.PlaybackPosition, state.Timestamp);
					
					if (oldState != null && _core.IsEntityTVOn(player))
					{
						if(oldState.Url != state.Url && state.Url != string.Empty)
						{
							switch(state.State)
							{
								case "playing":
									_core.PlayVideo(state.Url, state.PlaybackPosition, false);
									break;
								case "paused":
									_core.PlayVideo(state.Url, state.PlaybackPosition, true);
									break;
							}
						}
						else
						{
							if(pos + 7 >= state.PlaybackPosition && pos - 7 <= state.PlaybackPosition) //7s grace period to avoid unnecessary seeks due to minor desyncs
							{
								_core.SeekPlayer(state.PlaybackPosition);
							}
							switch(state.State)
							{
								case "playing":
									if(_core.GetPaused())
									{
										_core.Pause(false);
									}
									break;
								case "paused":
									if(!_core.GetPaused())
									{
										_core.Pause(true);
									}
									break;
							}
						}
					}
				}
				else{
					Services.Log.Error("Failed to deserialize state for player " + player + " with JSON: " + stateJSON);
				}
			}
			
		}
	}
}
