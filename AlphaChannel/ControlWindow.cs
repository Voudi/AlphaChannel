using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;

namespace AlphaChannel;

public class ControlWindow : Window, IDisposable
{
	private bool _playerCarbuncleFound;
	private bool _pauseToggle;
	private Plugin _plugin;
	private Compatibility _compat;
	private Core _core;
	private uint? LocalEntityId => Services.Objects?.LocalPlayer?.EntityId;

	//Render Vars
	private string _inputURL = "";
	private float _volume = 25;
	private float _volumesnes = 25;
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
	private bool _installingLibs;
	private bool _updatingMPV;
	private bool _updatingYTDLP;
	private bool _installingSNES9X;
	private bool _uiElementActive;
	private uint _nextLinkId = 1;
	private readonly FileDialogManager _fileDialog = new();
	private int _waitingForKey = -1;

	private readonly Dictionary<uint, IPCVideoState> _currentStates = []; //PlayerEntityID, IPCVideoState
	public sealed record IPCVideoState([property: JsonRequired] string State, [property: JsonRequired] string Url, [property: JsonRequired] int PlaybackPosition, [property: JsonRequired] long Timestamp);

	private IPCVideoState? _localPlayerState;

	public ControlWindow(Plugin plugin, Core core, string title)
		: base(title, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
	{
		_plugin = plugin;

		_compat = new Compatibility(_plugin);

		_core = core;

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
		_core.Dispose();
		GC.SuppressFinalize(this);
	}

	private bool _isFocused;
	private static readonly string _placeHolderDefault = "Enter the Video URL...";
	private string _placeHolderURL = _placeHolderDefault;
	private IEnumerable<IGameObject> _playerList = [];
	public override void Draw()
	{
		bool playerIsRunningTV = _core.IsLocalPlayerTVOn();

		if (Services.DutyState.IsDutyStarted)
		{
			ImGui.Text("AlphaChannel is deactivated");
			ImGui.Text("inside duties.");
			return;
		}
		if (!_libsLoaded)
		{
			bool needsFirstInstall = _plugin.AssemblyLocationMPV == null || _plugin.AssemblyLocationYTDLP == null;
			bool updatesAvailable = (_plugin.LibResources.MpvCheckResult[0] != string.Empty) || (_plugin.LibResources.YtdlpCheckResult[0] != string.Empty);

			_libsLoaded = !needsFirstInstall;
			if (!_libsLoaded)
			{
				if (_installingLibs)
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

					_installingLibs = true;
				}
				if (!updatesAvailable)
				{
					ImGui.EndDisabled();
				}

				return;
			}
		}

		if (ImGui.BeginTabBar("AlphaChannelTabBar"))
		{
			if (ImGui.BeginTabItem("Join"))
    		{
				DrawJoin();
			}
			if (ImGui.BeginTabItem("Host"))
			{
				DrawHost();
			}
			if(ImGui.BeginTabItem("Snes9x"))
			{
				DrawGame();
			}
			if (ImGui.BeginTabItem("Settings"))
			{
				DrawSettings();
			}
			ImGui.EndTabBar();
		}

		_fileDialog.Draw();
	}

	private void StartGame(string path)
	{
		if(LocalEntityId.HasValue)
		{
			if (_core.PlayGame(path))
			{
				_core.SetCurrentTV(LocalEntityId.Value);
			}
		}
	}

	private void StartVideo(uint entityId)
	{
		if (LocalEntityId == entityId)
		{
			if (ValidateURL(out Uri? uri) && uri != null)
			{
				_core.SetCurrentTV(entityId);

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
		if (string.IsNullOrEmpty(_inputURL) && !string.IsNullOrEmpty(_placeHolderURL) && _placeHolderURL != _placeHolderDefault)
		{
			_inputURL = _placeHolderURL;
			_placeHolderURL = _placeHolderDefault;
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

			_playerCarbuncleFound = _core.ScanForCompanions();

			_playerList = Services.Objects.Where(x => x is IPlayerCharacter).OrderBy(x => (x.EntityId == LocalEntityId) ? "@" : x.Name.TextValue);
		}
	}

	private void VolumePlayer(float volume)
	{
		int vol = (int)((float)Math.Sqrt(volume) * 10f); //Quadratic slider values
		Services.Log.Debug("Setting volume to " + vol + "%");
		_core.VolumePlayer(vol);
	}
	private void VolumeSnes(float volume)
	{
		int vol = (int)volume;
		Services.Log.Debug("Setting volume to " + vol + "%");
		_core.VolumeSnes(vol);
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
				_volume = (float)volume / 100f * ((float)volume / 100f) * 100f; //Quadratic slider values
			}
		}
		
		if(_localPlayerState != null && !_core.IsLocalPlayerTVOn()) //Level 1 - TV is has been turned completely off
		{
			_localPlayerState = null;
			_plugin.UpdateIPCState(_localPlayerState);
		}
		else if(_mpvIsIdle != _core.IsIdle()) //Level 2 - TV has been turned idle (however they managed to do that)
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
			state = new IPCVideoState("paused", Uri.EscapeDataString(url), pos, _plugin.LibResources.NTPTimeSeconds);
		}
		else if(_core.IsLocalPlayerTVOn() && !string.IsNullOrEmpty(url) && !_core.GetPaused()) //LocalPlayer TV is on and video is playing
		{
			state = new IPCVideoState("playing", Uri.EscapeDataString(url), pos, _plugin.LibResources.NTPTimeSeconds);
		}

		return state;
	}

	public void IPCSetState(nint addr, string stateJSON)
	{
		int pos = _seekerTimeMinutes * 60 + _seekerTimeSeconds;

		IGameObject? player = _playerList.FirstOrDefault(player => player.Address == addr);
		if(player == null)
		{
			return;
		}
		uint playerId = player.EntityId;
		if (playerId == LocalEntityId)
		{
			return;
		}
		if (LocalEntityId != playerId && playerId != 0)
		{
			if(stateJSON == null)
			{
				_currentStates.Remove(playerId);
				if (_core.IsEntityTVOn(playerId))
				{
					StopVideo();
				}
			}
			else
			{
				IPCVideoState? state = JsonSerializer.Deserialize<IPCVideoState>(stateJSON);
				if (state != null)
				{
					bool foundstate = _currentStates.TryGetValue(playerId, out IPCVideoState? oldState);
					state = _currentStates[playerId] = new IPCVideoState(state.State, Uri.UnescapeDataString(state.Url), state.PlaybackPosition, state.Timestamp);
					
					if (foundstate && oldState != null && _core.IsEntityTVOn(playerId))
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
							if(pos + 7 < state.PlaybackPosition && pos - 7 > state.PlaybackPosition) //7s grace period to avoid unnecessary seek jumps due to minor desyncs
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
					else if(!foundstate)
					{
						//First new player state received after abandoning TV, send chat message

						DalamudLinkPayload _linkPayload = Services.Chat.AddChatLinkHandler(_nextLinkId, (commandId, msg) =>
						{
							StartVideo(playerId);
							if(!IsOpen)
							{
								Toggle();
							}
						});
						string url = state.Url;
						if(state.Url.Length > 60)
						{
							url = state.Url.Substring(0, 60);
							url += "...";
						}
						SeString seString = new SeStringBuilder()
							.AddUiForeground("[AlphaChannel] ", 35)
							.AddText(player.Name.TextValue + " is currently hosting " + url)
							.Add(_linkPayload)
							.AddUiForeground("[Click to start playback]", 32)
							.Add(RawPayload.LinkTerminator)
							.Build();

						Services.Chat.Print(new XivChatEntry
						{
							Message = seString,
							Type    = XivChatType.Echo
						});
					}
				}
				else{
					Services.Log.Error("Failed to deserialize state for player " + playerId + " with JSON: " + stateJSON);
				}
			}
			
		}
	}

	private void DrawJoin()
	{
		int count = 0;
		foreach (var item in _playerList)
		{
			if(item.EntityId == LocalEntityId)
			{
				continue;
			}

			if (_core.TVExistsForEntity(item.EntityId)) //Checks if TV exists
			{
				count++;
				bool isTheRunningTV = _core.IsEntityTVOn(item.EntityId);
				string url = string.Empty;
				bool urlExists = false;
				bool urlEmpty = string.IsNullOrEmpty(_inputURL);

				if (_currentStates.TryGetValue(item.EntityId, out IPCVideoState? state))
				{
					url = state.Url;
					urlExists = true;
				}
				
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
				

				if (urlExists)
				{
					ImGui.SameLine();

					ImGui.PushFont(UiBuilder.IconFont);
					if (ImGui.Button(FontAwesomeIcon.Clipboard.ToIconString() + "##clipboard" + item.EntityId))
					{
						ImGui.SetClipboardText(url ?? string.Empty);
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
					ImGui.BeginDisabled();
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
					ImGui.EndDisabled();
				}

				if (_playerCarbuncleFound)
				{
					ImGui.Separator();

					continue;
				}

				ImGui.SameLine();

				if (isTheRunningTV)
				{
					ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.0f, 1.0f, 0.0f, 1.0f));
				}
				else if (!urlExists)
				{
					ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1.0f));
				}
				ImGui.PushFont(UiBuilder.IconFont);

				if (ImGui.Button((isTheRunningTV ?
					FontAwesomeIcon.Stop.ToIconString()
					: FontAwesomeIcon.Play.ToIconString()
					) + "##play" + item.EntityId))
				{
					if (!isTheRunningTV)
					{
						if (urlExists)
						{
							StartVideo(item.EntityId);
						}
					}
					else
					{
						StopVideo();
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

						isTheRunningTV ? "Stop" : "Play"
					);
					ImGui.EndTooltip();
				}

				ImGui.Separator();
			}
		}
		if(count == 0)
		{
			ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: No hosts detected nearby.");
		}
		ImGui.EndTabItem();
	}
	private void DrawHost()
	{
		Vector4 textColor;
		IPlayerCharacter? player = Services.Objects.LocalPlayer;
		if(player != null)
		{
			if (_playerCarbuncleFound || _core.TVExistsForEntity(player.EntityId)) //Checks if players Carbuncle or TV exists
			{
				bool playerTVRunning = _core.IsLocalPlayerTVOn();
				bool urlEmpty = string.IsNullOrEmpty(_inputURL);
				bool urlExists = ValidateURL(out _);

				ImGui.PushStyleColor(ImGuiCol.Text, _playerCarbuncleFound ? new Vector4(1.0f, 1.0f, 1.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f));
				ImGui.PushFont(UiBuilder.IconFont);
				if(ImGui.Button(FontAwesomeIcon.PowerOff.ToIconString() + "##power" + player.EntityId))
				{
					if (_playerCarbuncleFound)
					{
						PenumbraIPC.ApplyTempMod("companion", Services.Objects.LocalPlayer?.ObjectIndex, _plugin.PenumbraTempModPaths);
					}
					else
					{
						PenumbraIPC.RemoveTempMod("companion");
					}
					PenumbraIPC.Redraw(_core.GetCompanion(player.EntityId)?.ObjectIndex ?? -1);
				}
				ImGui.PopFont();
				ImGui.PopStyleColor();

				if (!_playerCarbuncleFound && !_core.IsPlayingSnes())
				{
					if (!playerTVRunning)
					{
						ImGui.Text("Play Video:");
					}

					ImGui.PushFont(UiBuilder.IconFont);
					if (ImGui.Button(FontAwesomeIcon.Clipboard.ToIconString() + "##clipboard" + player.EntityId))
					{
						ImGui.SetClipboardText(string.IsNullOrEmpty(_inputURL) && playerTVRunning ? _placeHolderURL : _inputURL);
					}
					ImGui.PopFont();
					if (ImGui.IsItemHovered())
					{
						ImGui.BeginTooltip();
						ImGui.Text("Copy URL to clipboard");
						ImGui.EndTooltip();
					}
					ImGui.SameLine();

					textColor = (urlExists || urlEmpty) ? new Vector4(0.3f, 0.8f, 0.3f, 1f) : new Vector4(0.8f, 0.3f, 0.3f, 1f);
					if (!playerTVRunning && !urlEmpty)
					{
						ImGui.PushStyleColor(ImGuiCol.Border, textColor);
					}
					ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
					ImGui.SetNextItemWidth(200);
					ImGui.InputText("##URL", ref _inputURL, 1000, ImGuiInputTextFlags.None);
					ImGui.PopStyleVar();
					if (!playerTVRunning && !urlEmpty)
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
					if (!_isFocused && string.IsNullOrEmpty(_inputURL))
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

					ImGui.SameLine();

					bool refreshNeeded = playerTVRunning && !string.IsNullOrEmpty(_inputURL) && urlExists;
					if (playerTVRunning)
					{
						textColor = refreshNeeded ? new Vector4(0.0f, 1.0f, 1.0f, 1.0f) : new Vector4(1.0f, 0.0f, 0.0f, 1.0f);
						ImGui.PushStyleColor(ImGuiCol.Text, textColor);
					}
					else if (!urlExists)
					{
						ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1.0f));
					}
					ImGui.PushFont(UiBuilder.IconFont);
					if (ImGui.Button((playerTVRunning ?
						(refreshNeeded ?
							FontAwesomeIcon.ArrowRight.ToIconString()
							: FontAwesomeIcon.Stop.ToIconString()
						)
						: FontAwesomeIcon.Play.ToIconString()
						) + "##play" + player.EntityId))
					{
						try
						{
							if (!playerTVRunning || refreshNeeded)
							{
								if (urlExists)
								{
									StartVideo(player.EntityId);
								}

								_placeHolderURL = _inputURL;
								_inputURL = string.Empty;
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
					if (playerTVRunning || !urlExists)
					{
						ImGui.PopStyleColor();
					}
					if (ImGui.IsItemHovered())
					{
						ImGui.BeginTooltip();
						ImGui.Text(

							playerTVRunning ?
								(!string.IsNullOrEmpty(_inputURL) && urlExists ? "Visit new URL"
								: "Stop"
							)
							: "Play"
						);
						ImGui.EndTooltip();
					}

					if (playerTVRunning)
					{
						ImGui.SetNextItemWidth(265);
						ImGui.PushStyleColor(ImGuiCol.SliderGrab, new Vector4(0.8f, 0.3f, 0.3f, 1));
						ImGui.SliderFloat("##seeker" + player.EntityId, ref _seeker, 0, 100, $"{_seekerTimeMinutes}:{_seekerTimeSeconds:00} / {_seekerDurationMinutes}:{_seekerDurationSeconds:00}");
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

						if (_seekerExactTime > 0)
						{
							DrawScrollingText(_seekerExactTime > 0 ? _mediaTitle : " ", 125);
							ImGui.SameLine();
						}

						ImGui.SameLine();

						ImGui.PushFont(UiBuilder.IconFont);
						ImGui.SetNextItemWidth(100);
						ImGui.SliderFloat("##volumebar" + player.EntityId, ref _volume, 0, 100, _volume < 1 ? FontAwesomeIcon.VolumeMute.ToIconString() : (_volume <= 60 ? FontAwesomeIcon.VolumeDown.ToIconString() : FontAwesomeIcon.VolumeUp.ToIconString()));
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
						ImGui.SameLine();
						ImGui.PushFont(UiBuilder.IconFont);
						if (ImGui.Button(_mpvIsIdle ? FontAwesomeIcon.Repeat.ToIconString() : (_pauseToggle ? FontAwesomeIcon.Play.ToIconString() : FontAwesomeIcon.Pause.ToIconString()) + "##forceplay" + player.EntityId))
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
					}
				}
			}
			else
			{
				ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: You have not summoned");
				ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " your Blue Carbuncle.");
			}
		}
		ImGui.EndTabItem();
	}
	
	private void DrawGame()
	{
		uint entityId = LocalEntityId ?? 0;
		bool snesExists = !string.IsNullOrEmpty(_plugin.AssemblyLocationSnes);

		if (_playerCarbuncleFound || _core.TVExistsForEntity(entityId))
		{
			bool playerTVRunning = _core.IsLocalPlayerTVOn();

			ImGui.PushStyleColor(ImGuiCol.Text, _playerCarbuncleFound ? new Vector4(1.0f, 1.0f, 1.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f));
			ImGui.PushFont(UiBuilder.IconFont);
			if(ImGui.Button(FontAwesomeIcon.PowerOff.ToIconString() + "##power" + entityId))
			{
				if (_playerCarbuncleFound)
				{
					PenumbraIPC.ApplyTempMod("companion", Services.Objects.LocalPlayer?.ObjectIndex, _plugin.PenumbraTempModPaths);
				}
				else
				{
					PenumbraIPC.RemoveTempMod("companion");
				}
				PenumbraIPC.Redraw(_core.GetCompanion(entityId)?.ObjectIndex ?? -1);
			}
			ImGui.PopFont();
			ImGui.PopStyleColor();
			if (!_playerCarbuncleFound)
			{
				if (playerTVRunning)
				{
					ImGui.SameLine();
					Vector4 textColor = new Vector4(1.0f, 0.0f, 0.0f, 1.0f);
					ImGui.PushStyleColor(ImGuiCol.Text, textColor);

					ImGui.PushFont(UiBuilder.IconFont);
					if (ImGui.Button(FontAwesomeIcon.Stop.ToIconString()+ "##stopgame" + entityId))
					{
						StopVideo();
					}
					ImGui.PopFont();
					ImGui.PopStyleColor();

					ImGui.SameLine();
					
					ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12, 8));
					ImGui.PushStyleColor(ImGuiCol.Text, _core.IsPlayingSnes() && _core.IsSnesControlsEnabled() ? new Vector4(0.0f, 1.0f, 1.0f, 1.0f) : new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
					ImGui.PushFont(UiBuilder.IconFont);
					if(ImGui.Button(FontAwesomeIcon.Gamepad.ToIconString() + "##alphaenablecontrols"))
					{
						_core.EnableSnesControls(!_core.IsSnesControlsEnabled());
					}
					ImGui.PopFont();
					ImGui.PopStyleColor();
					ImGui.PopStyleVar();
					if (ImGui.IsItemHovered())
					{
						ImGui.BeginTooltip();
						ImGui.Text((_core.IsPlayingSnes() && _core.IsSnesControlsEnabled()) ? "Unplug Controls" : "Plug in Controls");
						ImGui.EndTooltip();
					}
					
					ImGui.SameLine();

					ImGui.PushFont(UiBuilder.IconFont);
					ImGui.SetNextItemWidth(100);
					ImGui.SliderFloat("##volumebarsnes", ref _volumesnes, 0, 100, _volumesnes < 1 ? FontAwesomeIcon.VolumeMute.ToIconString() : (_volumesnes <= 60 ? FontAwesomeIcon.VolumeDown.ToIconString() : FontAwesomeIcon.VolumeUp.ToIconString()));
					if (ImGui.IsItemActive())
					{
						_uiElementActive = true;
					}
					if (ImGui.IsItemDeactivatedAfterEdit())
					{
						VolumeSnes(_volumesnes);
						_uiElementActive = false;
					}
					ImGui.PopFont();
				}

				if (snesExists)
				{
					if (ImGui.Button("Open Folder"))
					{
						Process.Start(new ProcessStartInfo
						{
							FileName = Plugin.ROMSLocationSnesDir,
							UseShellExecute = true
						});
					}
					ImGui.SameLine();
					if(ImGui.Button("Load ROM"))
					{
						_fileDialog.OpenFileDialog(
							"load SNES ROM",
							"SNES ROMs{.sfc,.smc},All Files{.*}",
							(success, paths) =>
							{
								if (!success || paths.Count == 0) { return; }
								string romPath = paths[0];
								
								StartGame(romPath);

							},
							1,
							Plugin.ROMSLocationSnesDir,
							false);
					}
				}
				else
				{
					ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: Snes9x not found");
				}

				ImGui.Text("Configure Keys:");

				string pressAKey = "Press a key... (Click again to abort)";

				foreach(Snes9xInput key in _core.SnesKeys.Keys)
				{
					if(_core.SnesKeys.TryGetValue(key, out VirtualKey virtualKey))
					{
						float pos = ImGui.GetCursorPosX();
						ImGui.Text(key.ToString());
						ImGui.SameLine();
						ImGui.SetCursorPosX(pos + 80);
						string label = (_waitingForKey == (int)key ? pressAKey : virtualKey == VirtualKey.NO_KEY ? "Unmapped" : virtualKey.ToString()) + "##keymap"+key;

						if (ImGui.Button(label))
						{
							if (_waitingForKey == (int)key)
							{
								_waitingForKey = -1;
							}
							else
							{
								_waitingForKey = (int)key;
							}
						}

						if (_waitingForKey == (int)key)
						{
							foreach (VirtualKey vk in Services.KeyState.GetValidVirtualKeys())
							{
								if (Services.KeyState[vk] && _core.IsKeyMappable(vk))
								{
									_core.SnesKeys[key] = vk;
									_plugin.Config.KeyMappings[key] = vk;
									_plugin.Config.Save();
									_waitingForKey = -1;
									break;
								}
							}
						}
					}
				}
			}
		}
		else
		{
			ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: You have not summoned");
			ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " your Blue Carbuncle.");
		}
		ImGui.EndTabItem();
	}
	
	private void DrawSettings()
	{
		bool mpvUpdateAvailable = _plugin.LibResources.MpvCheckResult[0] != string.Empty;
		bool ytdlpUpdateAvailable = _plugin.LibResources.YtdlpCheckResult[0] != string.Empty;
		bool snesInstallAvailable = string.IsNullOrEmpty(_plugin.AssemblyLocationSnes);


		bool installingMPV = _updatingMPV;
		bool installingYTDLP = _updatingYTDLP;
		bool installingSNES9X = _installingSNES9X;

		ImGui.Text("Dependencies:");

		ImGui.Text("mpv-winbuild");
		ImGui.SameLine();
		if (mpvUpdateAvailable)
		{
			if (installingMPV)
			{
				ImGui.BeginDisabled();
			}
			if (ImGui.Button((installingMPV ? "Updating..." : "Update") + "##mpvUpdate"))
			{
				if (!string.IsNullOrWhiteSpace(_plugin.LibResources.MpvCheckResult[0]))
				{
					_updatingMPV = true;
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
						_updatingMPV = false;
					});
				}
			}
			if (installingMPV)
			{
				ImGui.EndDisabled();
			}
		}
		else
		{
			ImGui.PushFont(UiBuilder.IconFont);
			ImGui.Text(FontAwesomeIcon.CheckCircle.ToIconString());
			ImGui.PopFont();
		}

		ImGui.Text("yt-dlp");
		ImGui.SameLine();
		if (ytdlpUpdateAvailable)
		{
			if (installingYTDLP)
			{
				ImGui.BeginDisabled();
			}
			if (ImGui.Button((installingYTDLP ? "Updating..." : "Update") + "##ytdlpUpdate"))
			{
				if (!string.IsNullOrWhiteSpace(_plugin.LibResources.YtdlpCheckResult[0]))
				{
					_updatingYTDLP = true;
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
						_updatingYTDLP = false;
					});
				}
			}
			if (installingYTDLP)
			{
				ImGui.EndDisabled();
			}
		}
		else
		{
			ImGui.PushFont(UiBuilder.IconFont);
			ImGui.Text(FontAwesomeIcon.CheckCircle.ToIconString());
			ImGui.PopFont();
		}

		ImGui.Text("snes9x");
		ImGui.SameLine();
		if (snesInstallAvailable)
		{
			if (installingSNES9X)
			{
				ImGui.BeginDisabled();
			}
			if (ImGui.Button((installingSNES9X ? "Updating..." : "Update") + "##snes9xUpdate"))
			{
				_installingSNES9X = true;
				_plugin.LibResources.DownloadSNES9XAsync().ContinueWith(async task =>
				{
					if (task.Result)
					{
						Services.Log.Debug("SNES9X downloaded successfully");
						_plugin.AssemblyLocationSnes = _plugin.LibResources.GetLocationSNES9X()!;
					}
					else
					{
						Services.Log.Error("Failed to download SNES9X");
					}
					_installingSNES9X = false;
				});
			}
			if (installingSNES9X)
			{
				ImGui.EndDisabled();
			}
		}
		else
		{
			ImGui.PushFont(UiBuilder.IconFont);
			ImGui.Text(FontAwesomeIcon.CheckCircle.ToIconString());
			ImGui.PopFont();
		}


		ImGui.EndTabItem();
	}
}
