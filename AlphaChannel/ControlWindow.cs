using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Penumbra.Api.IpcSubscribers;

namespace AlphaChannel;

internal sealed class ControlWindow : Window, IDisposable
{
	private readonly Plugin _plugin;
	private readonly Core _core;
	private readonly APIHelper _apiHelper;

	private bool _playerCarbuncleFound;
	
	//Resource vars
	private bool _updatingMPV;
	private bool _updatingYTDLP;
	private bool _updatingSNES9X;

	//Video player vars
	private string _inputURL = "";
	private readonly List<string> _videoQueue = [];
	private string _twitchChannel = "";
	private string _coopJoinCode = "";
	private string _activeTabName = "";
	private string _activeVideoSource = "";
	private float _volume = 25;
	private float _volumesnes = 25;
	private float _seeker;
	private double _seekerExactTime;
	private int _seekerTimeSeconds;
	private int _seekerTimeMinutes;
	private int _seekerDurationSeconds;
	private int _seekerDurationMinutes;
	private int _seekerMaxSeconds;
	private bool _pauseToggle;
	private bool _mpvIsIdle;
	private bool _mpvIsDone => _seekerMaxSeconds - 2 < _seekerExactTime;
	private string _mediaTitle = string.Empty;

	//Controls vars
	private bool _sliderActive;
	private bool _urlInputActive;
	private uint _nextLinkId = 1;
	private readonly FileDialogManager _fileDialog = new();
	private int _awaitKeyPress = -1;
	private const string UrlPlaceholderDefault = "Enter the Video URL...";
	private string _urlPlaceholder = UrlPlaceholderDefault;
	private IEnumerable<IGameObject> _visiblePlayers = [];

	internal ControlWindow(Plugin plugin, Core core, APIHelper apiHelper, string title)
		: base(title, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
	{
		_plugin = plugin;
		_core = core;
		_apiHelper = apiHelper;
		_apiHelper.OnNewPlayerSeen += HandleNewPlayerSeen;
		_core.OnLocalVideoIdle += PlayNextQueued;

		SizeConstraints = new WindowSizeConstraints
		{
			MinimumSize = new Vector2(275, 235),
			MaximumSize = new Vector2(900, 1400)
		};
	}
	
	private bool _tabInit;
	public override void Draw()
	{
		using var _theme = new RokuTheme();
		using var _wp = new Padding(new Vector2(5, 5));

		DrawHeader();

		if (Services.DutyState.IsDutyStarted)
		{
			ImGui.Text("AlphaChannel is deactivated");
			ImGui.Text("inside duties.");
		}
		else
		{
			if (ImGui.BeginTabBar("AlphaChannelTabBar") && Services.LocalPlayerExists)
			{
				if (ImGui.BeginTabItem("Join"))
				{
					UpdateActiveTab("Join");
					DrawJoin();
					ImGui.EndTabItem();
				}
				if (ImGui.BeginTabItem("YouTube"))
				{
					UpdateActiveTab("YouTube");
					DrawYouTube();
					ImGui.EndTabItem();
				}
				if (ImGui.BeginTabItem("Twitch"))
				{
					UpdateActiveTab("Twitch");
					DrawTwitch();
					ImGui.EndTabItem();
				}
				if(ImGui.BeginTabItem("Snes9x"))
				{
					UpdateActiveTab("Snes9x");
					DrawGame();
					ImGui.EndTabItem();
				}
				if (ImGui.BeginTabItem("Settings", _tabInit ? ImGuiTabItemFlags.None : ImGuiTabItemFlags.SetSelected))
				{
					_tabInit = true;
					UpdateActiveTab("Settings");
					DrawSettings();
					ImGui.EndTabItem();
				}
				ImGui.EndTabBar();
			}
		}

		_fileDialog.Draw();
	}

	private void DrawHeader()
	{
		Color(ImGuiCol.Text, RokuAccent, () => IconFont(() => ImGui.Text(FontAwesomeIcon.Tv.ToIconString())));
		ImGui.SameLine();
		Color(ImGuiCol.Text, RokuText, () => ImGui.Text("AlphaChannel"));
		Color(ImGuiCol.Separator, RokuAccent, () => ImGui.Separator());
		ImGui.Spacing();
	}

	private void StartVideo(uint entityId)
	{
		if (Services.LocalPlayerId == entityId)
		{
			PlayLocalUrl(_inputURL, "YouTube");
		}
		else
		{
			if (_apiHelper.RemoteStates.TryGetValue(entityId, out APIHelper.IPCVideoState? stateInfo))
			{
				string url = stateInfo.Url;

				bool result = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri?.Scheme == Uri.UriSchemeHttp || uri?.Scheme == Uri.UriSchemeHttps) && uri.Host.Contains('.') && !uri.Host.EndsWith('.') && Uri.CheckHostName(uri.Host) == UriHostNameType.Dns;

				if (!result)
				{
					Services.Log.Error("Failed fetching URL for player " + entityId);
					return;
				}

				int getTimeDiffMillis = (int) (_plugin.LibResources.CurrentTimeNTPNormalizedMilliseconds - stateInfo.Timestamp);
				_core.PlayVideo(entityId, url, stateInfo.PlaybackPosition + (getTimeDiffMillis / 1000), stateInfo.State == "playing");
			}
		}
	}

	private void StopVideo()
	{
		_core.StopVideo();
		_inputURL = "";
		_twitchChannel = "";
		_urlPlaceholder = UrlPlaceholderDefault;
		_activeVideoSource = "";
	}

	private void UpdateActiveTab(string tabName)
	{
		if (_activeTabName == tabName) { return; }

		if (_activeTabName == "YouTube") { _inputURL = ""; }
		else if (_activeTabName == "Twitch") { _twitchChannel = ""; }

		_activeTabName = tabName;
	}

	private void PlayLocalUrl(string url, string source)
	{
		if (_core.ValidateURL(url, out Uri? uri) && uri != null)
		{
			_core.PlayVideo(Services.LocalPlayerId, uri.ToString());
			_activeVideoSource = source;
		}
	}

	private void PlayNextQueued()
	{
		if (_videoQueue.Count == 0) { return; }

		string next = _videoQueue[0];
		_videoQueue.RemoveAt(0);
		_videoQueue.Add(next); //Recycle to the back so the queue loops indefinitely

		PlayLocalUrl(next, "YouTube");
	}

	private long _lastMilliSecond5000ms;
	private long _lastMilliSecond1500ms;
	private long _lastMilliSecond6ms;
	internal void OnFrameworkUpdate()
	{
		if (_lastMilliSecond6ms + 6 < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
			_lastMilliSecond6ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			GetCoreInfo();
		}
		if (_lastMilliSecond1500ms + 1500 < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
			_lastMilliSecond1500ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			_playerCarbuncleFound = _core.ScanForCompanions();

			if (Services.LocalPlayerExists)
			{
				_visiblePlayers = Services.Objects.Where(x => x is IPlayerCharacter).OrderBy(x => x.Name.TextValue);
			}
		}
		if (_lastMilliSecond5000ms + 5000 < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
			_lastMilliSecond5000ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			_core.RedrawIfNeeded();
		}
	}

	private void SetVolume(float volume, bool quadratic = false)
	{
		int vol = (int)volume;
		if(quadratic)
		{
			vol = (int)((float)Math.Sqrt(volume) * 10f); //Quadratic slider values
		}
		_core.SetVolume(vol);
	}

	private void SeekPlayer(double percentage)
	{
		int seconds = (int)(_seekerMaxSeconds * (percentage / 100));
		if(seconds >= _seekerMaxSeconds-1)
		{
			seconds = _seekerMaxSeconds-2;
		}
		Services.Log.Debug("Seeking to " + seconds + " seconds");
		_core.Seek(seconds);
	}

	private void GetCoreInfo()
	{
		if (!_core.TVIsActive(0)) //If its 0, TV is inactive
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

			if (!_sliderActive)
			{
				if (duration > 0)
				{
					_seeker = (float)(duration > 0 ? time / duration * 100 : 100);
				}

				double volume = info[2];
				_volume = (float)volume / 100f * ((float)volume / 100f) * 100f; //Quadratic slider values
			}
		}
		
		if(_mpvIsIdle != _core.GetIdle())
		{
			_mpvIsIdle = _core.GetIdle();
			if (_mpvIsIdle) { _pauseToggle = true; }
		}
		else if(_pauseToggle != _core.GetPaused())
		{
			_pauseToggle = _core.GetPaused();
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

	private void HandleNewPlayerSeen(IGameObject? player, APIHelper.IPCVideoState state)
	{
		uint? playerId = player?.EntityId;
		if(!playerId.HasValue)
		{
			return;
		}
		DalamudLinkPayload linkPayload = Services.Chat.AddChatLinkHandler(_nextLinkId++, (commandId, msg) =>
		{
			StartVideo(playerId.Value);
			if (!IsOpen) { Toggle(); }
		});
		string url = state.Url.Length > 60 ? state.Url[..60] + "..." : state.Url;
		SeString seString = new SeStringBuilder()
			.AddUiForeground("[AlphaChannel] ", 35)
			.AddText(player?.Name.TextValue + " is currently hosting " + url)
			.Add(linkPayload)
			.AddUiForeground("[Click to start playback]", 32)
			.Add(RawPayload.LinkTerminator)
			.Build();
		Services.Chat.Print(new XivChatEntry { Message = seString, Type = XivChatType.Echo });
	}

	private void DrawJoin()
	{
		ImGui.BeginChild("##scrollListJoin" + _plugin.Name, new Vector2(0, 0), true);

		SectionHeader("Hosts Nearby");

		int count = 0;
		foreach (IGameObject item in _visiblePlayers)
		{
			if(item.EntityId == Services.LocalPlayerId)
			{
				continue;
			}

			if (_core.TVIsVisible(item.EntityId)) //Checks if TV exists
			{
				count++;
				bool isTheRunningTV = _core.TVIsActive(item.EntityId);
				string url = string.Empty;
				bool urlExists = false;
				bool urlEmpty = string.IsNullOrEmpty(_inputURL);

				if (_apiHelper.RemoteStates.TryGetValue(item.EntityId, out APIHelper.IPCVideoState? state))
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

					if (IconButton(FontAwesomeIcon.Clipboard, "clipboard" + item.EntityId))
					{
						ImGui.SetClipboardText(url ?? string.Empty);
					}
					Tooltip("Copy URL to clipboard");
				}

				if (isTheRunningTV && _seekerExactTime > 0)
				{
					DrawScrollingText(_mediaTitle, 125);
				}
				else
				{
					ImGui.Text(item.Name.TextValue);
				}
				
				if (isTheRunningTV)
				{						
					ImGui.SameLine();

					IconFont(() =>
					{
						ImGui.SetNextItemWidth(100);
						ImGui.SliderFloat("##volumebar" + item.EntityId, ref _volume, 0, 100, _volume < 1 ? FontAwesomeIcon.VolumeMute.ToIconString() : (_volume <= 60 ? FontAwesomeIcon.VolumeDown.ToIconString() : FontAwesomeIcon.VolumeUp.ToIconString()));
						if (ImGui.IsItemActive()) { SetVolume(_volume, true); _sliderActive = true; }
						if (ImGui.IsItemDeactivatedAfterEdit()) { SetVolume(_volume, true); _sliderActive = false; }
					});

					WithDisabled(true, () =>
					{
						ImGui.SetNextItemWidth(268);
						Color(ImGuiCol.SliderGrab, new Vector4(0.8f, 0.3f, 0.3f, 1), () =>
						{
							ImGui.SliderFloat("##seeker" + item.EntityId, ref _seeker, 0, 100, $"{_seekerTimeMinutes}:{_seekerTimeSeconds:00} / {_seekerDurationMinutes}:{_seekerDurationSeconds:00}");
							if (ImGui.IsItemActive()) { _sliderActive = true; }
							if (ImGui.IsItemDeactivatedAfterEdit()) { SeekPlayer(_seeker); _sliderActive = false; }
						});
					});
				}

				Vector4? joinPlayColor = isTheRunningTV ? new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
					: (!urlExists ? new Vector4(0.5f, 0.5f, 0.5f, 1.0f) : null);
				if (IconButton(isTheRunningTV ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play, "play" + item.EntityId, joinPlayColor))
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

				Tooltip(isTheRunningTV ? "Stop" : "Play");

				ImGui.Separator();
			}
		}
		if(count == 0)
		{
			EmptyState(FontAwesomeIcon.Search, "No hosts detected nearby.");
		}

		ImGui.EndChild();
	}
	private void DrawYouTube()
	{
		ImGui.BeginChild("##scrollListHost" + _plugin.Name, new Vector2(0, 0), true);
		Vector4 textColor;
		uint localPlayerId = Services.LocalPlayerId;
		if (_playerCarbuncleFound || _core.TVIsVisible(localPlayerId)) //Checks if players Carbuncle or TV exists
		{
			SectionHeader("Now Playing");

			bool playerTVRunning = _core.TVIsActive(localPlayerId);
			bool youTubeIsActive = playerTVRunning && _activeVideoSource == "YouTube";
			bool urlEmpty = string.IsNullOrEmpty(_inputURL);
			bool urlExists = _core.ValidateURL(_inputURL, out _);
			bool refreshNeeded = youTubeIsActive && !string.IsNullOrEmpty(_inputURL) && urlExists;

			if (IconButton(FontAwesomeIcon.Cat, "powerbutton" + localPlayerId,
				_playerCarbuncleFound ? new Vector4(1.0f, 1.0f, 1.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f), true))
			{
				if (_playerCarbuncleFound)
				{
					PenumbraIPC.ApplyTempMod("companion", _plugin.PenumbraTempModPaths);
					PenumbraIPC.ApplyTempMod("qr", _plugin.PenumbraQRPaths);
					PenumbraIPC.Redraw(_core.GetCompanionIndex(localPlayerId));
				}
				else
				{
					PenumbraIPC.RemoveTempMod("companion");
					PenumbraIPC.RemoveTempMod("qr");
					PenumbraIPC.Redraw(_core.GetCompanionIndex(localPlayerId));
					_core.RemoveCompanion();
					_core.StopVideo();
				}
			}

			if (!_playerCarbuncleFound && !_core.IsPlayingSnes())
			{
				ImGui.SameLine();

				bool hostPlayDisabled = !urlExists && !youTubeIsActive && _videoQueue.Count == 0;
				Vector4? hostPlayColor = youTubeIsActive ? new Vector4(1.0f, 0.0f, 0.0f, 1.0f) : (hostPlayDisabled ? new Vector4(0.5f, 0.5f, 0.5f, 1.0f) : null);
				FontAwesomeIcon hostPlayIcon = youTubeIsActive ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play;
				if (IconButton(hostPlayIcon, "playbutton" + localPlayerId, hostPlayColor, true, disabled: hostPlayDisabled))
				{
					if (youTubeIsActive)
					{
						StopVideo();
					}
					else if (urlExists)
					{
						StartVideo(localPlayerId);
						_urlPlaceholder = _inputURL;
						_inputURL = string.Empty;
						_twitchChannel = "";
					}
					else if (_videoQueue.Count > 0)
					{
						PlayNextQueued();
						_twitchChannel = "";
					}
				}
				Tooltip(youTubeIsActive ? "Stop" : "Play");

				if (playerTVRunning)
				{
					ImGui.SameLine();
					Vector4? hostPauseColor = refreshNeeded || _mpvIsIdle || _pauseToggle ? new Vector4(0.0f, 1.0f, 1.0f, 1.0f) : null;
					FontAwesomeIcon pauseIcon = refreshNeeded ? FontAwesomeIcon.ArrowRight : (_mpvIsDone ? FontAwesomeIcon.Repeat : (_pauseToggle || _mpvIsIdle ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause));
					Color(ImGuiCol.Text, hostPauseColor, () =>
					{
						if (IconButton(pauseIcon, "pausebutton" + localPlayerId, isBig: true))
						{
							if(refreshNeeded)
							{
								if (urlExists)
								{
									StartVideo(localPlayerId);
								}

								_urlPlaceholder = _inputURL;
								_inputURL = string.Empty;
							}
							else
							{
								if (_mpvIsDone)
								{
									SeekPlayer(0);
								}
								
								if(_mpvIsIdle)
								{
									_core.Pause(false);
									_pauseToggle = false;
								}
								else
								{
									_pauseToggle = !_pauseToggle;
									_core.Pause(_pauseToggle);
								}
							}
						}
					});
					Tooltip(refreshNeeded ? "Load new URL..." : (_mpvIsIdle ? "Replay" : (_pauseToggle ? "Pause" : "Resume")));
					ImGui.SameLine();
					Style(ImGuiStyleVar.FramePadding, new Vector2(0, 8), () =>
					{
						IconFont(() =>
						{
							ImGui.SetNextItemWidth(104);
							ImGui.SliderFloat("##volumebar" + localPlayerId, ref _volume, 0, 100, _volume < 1 ? FontAwesomeIcon.VolumeMute.ToIconString() : (_volume <= 60 ? FontAwesomeIcon.VolumeDown.ToIconString() : FontAwesomeIcon.VolumeUp.ToIconString()));
							if (ImGui.IsItemActive()) { SetVolume(_volume, true); _sliderActive = true; }
							if (ImGui.IsItemDeactivatedAfterEdit()) { SetVolume(_volume, true); _sliderActive = false; }
						});
					});
					
					Tooltip(((int)_volume)+"");
				}

				if (IconButton(FontAwesomeIcon.Clipboard, "clipboard" + localPlayerId))
				{
					ImGui.SetClipboardText(string.IsNullOrEmpty(_inputURL) && playerTVRunning ? _urlPlaceholder : _inputURL);
				}
				Tooltip("Copy URL to clipboard");
				ImGui.SameLine();

				textColor = (urlExists || urlEmpty) ? new Vector4(0.3f, 0.8f, 0.3f, 1f) : new Vector4(0.8f, 0.3f, 0.3f, 1f);
				Color(ImGuiCol.Border, (!playerTVRunning && !urlEmpty) ? textColor : null, () =>
				{
					Style(ImGuiStyleVar.FrameBorderSize, 1.0f, () =>
					{
						ImGui.SetNextItemWidth(217);
						ImGui.InputText("##URL", ref _inputURL, 1000, ImGuiInputTextFlags.None);
					});
				});
				// Detect if the input is focused
				if (ImGui.IsItemActive())
				{
					_urlInputActive = true;
				}
				else if (ImGui.IsItemDeactivated())
				{
					_urlInputActive = false;
				}
				// Render placeholder if input is empty and unfocused
				if (!_urlInputActive && string.IsNullOrEmpty(_inputURL))
				{
					var pos = ImGui.GetItemRectMin();
					var max = ImGui.GetItemRectMax();

					float maxWidth = max.X - pos.X;

					string placeholder = _urlPlaceholder;

					Vector2 textSize = ImGui.CalcTextSize(placeholder);

					while (textSize.X > maxWidth && placeholder.Length > 0)
					{
						placeholder = placeholder[..^1];
						textSize = ImGui.CalcTextSize(placeholder + "........");
					}

					if (!placeholder.Equals(_urlPlaceholder, StringComparison.Ordinal))
					{
						placeholder += "...";
					}

					ImGui.GetWindowDrawList().AddText(new Vector2(pos.X + 3, pos.Y + 2), ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 1.0f)), placeholder);
				}

				if (playerTVRunning)
				{
					if (_seekerExactTime > 0)
					{
						DrawScrollingText(_seekerExactTime > 0 ? _mediaTitle : " ", 249);
					}

					ImGui.SetNextItemWidth(250);
					Color(ImGuiCol.SliderGrab, new Vector4(0.8f, 0.3f, 0.3f, 1), () =>
					{
						ImGui.SliderFloat("##seeker" + localPlayerId, ref _seeker, 0, 100, $"{_seekerTimeMinutes}:{_seekerTimeSeconds:00} / {_seekerDurationMinutes}:{_seekerDurationSeconds:00}");
						if (ImGui.IsItemActive()) { _sliderActive = true; }
						if (ImGui.IsItemDeactivatedAfterEdit()) { SeekPlayer(_seeker); _sliderActive = false; }
					});
				}

				if (Button("Add to Queue", "addQueue" + localPlayerId, disabled: !urlExists))
				{
					_videoQueue.Add(_inputURL);
					_inputURL = string.Empty;
					_twitchChannel = "";
				}

				ImGui.SameLine();

				if (Button("Next", "queueNext" + localPlayerId, disabled: _videoQueue.Count == 0))
				{
					PlayNextQueued();
				}

				ImGui.SameLine();

				if (Button("Clear", "queueClear" + localPlayerId, disabled: _videoQueue.Count == 0))
				{
					_videoQueue.Clear();
				}

				DrawQueue();
			}
		}
		else
		{
			EmptyState(FontAwesomeIcon.Cat, "You have not summoned", "your Blue Carbuncle.");
		}
		
		ImGui.EndChild();
	}

	private void DrawTwitch()
	{
		ImGui.BeginChild("##scrollListTwitch" + _plugin.Name, new Vector2(0, 0), true);

		uint localPlayerId = Services.LocalPlayerId;

		if (_playerCarbuncleFound || _core.TVIsVisible(localPlayerId))
		{
			SectionHeader("Twitch");

			bool playerTVRunning = _core.TVIsActive(localPlayerId);
			bool twitchIsActive = playerTVRunning && _activeVideoSource == "Twitch";
			bool channelExists = !string.IsNullOrWhiteSpace(_twitchChannel);

			if (IconButton(FontAwesomeIcon.Cat, "powertwitch" + localPlayerId,
				_playerCarbuncleFound ? new Vector4(1.0f, 1.0f, 1.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f), true))
			{
				if (_playerCarbuncleFound)
				{
					PenumbraIPC.ApplyTempMod("companion", _plugin.PenumbraTempModPaths);
					PenumbraIPC.ApplyTempMod("qr", _plugin.PenumbraQRPaths);
					PenumbraIPC.Redraw(_core.GetCompanionIndex(localPlayerId));
				}
				else
				{
					PenumbraIPC.RemoveTempMod("companion");
					PenumbraIPC.RemoveTempMod("qr");
					PenumbraIPC.Redraw(_core.GetCompanionIndex(localPlayerId));
					_core.RemoveCompanion();
					_core.StopVideo();
				}
			}

			if (!_playerCarbuncleFound)
			{
				ImGui.SameLine();

				bool playDisabled = !channelExists && !twitchIsActive;
				Vector4? playColor = twitchIsActive ? new Vector4(1.0f, 0.0f, 0.0f, 1.0f) : (playDisabled ? new Vector4(0.5f, 0.5f, 0.5f, 1.0f) : null);
				FontAwesomeIcon playIcon = twitchIsActive ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play;
				if (IconButton(playIcon, "twitchplay" + localPlayerId, playColor, true, disabled: playDisabled))
				{
					if (twitchIsActive)
					{
						StopVideo();
					}
					else if (channelExists)
					{
						PlayLocalUrl(NormalizeTwitchUrl(_twitchChannel), "Twitch");
						_inputURL = "";
					}
				}
				Tooltip(twitchIsActive ? "Stop" : "Play");

				if (playerTVRunning)
				{
					ImGui.SameLine();
					Style(ImGuiStyleVar.FramePadding, new Vector2(0, 8), () =>
					{
						IconFont(() =>
						{
							ImGui.SetNextItemWidth(104);
							ImGui.SliderFloat("##volumebartwitch" + localPlayerId, ref _volume, 0, 100, _volume < 1 ? FontAwesomeIcon.VolumeMute.ToIconString() : (_volume <= 60 ? FontAwesomeIcon.VolumeDown.ToIconString() : FontAwesomeIcon.VolumeUp.ToIconString()));
							if (ImGui.IsItemActive()) { SetVolume(_volume, true); _sliderActive = true; }
							if (ImGui.IsItemDeactivatedAfterEdit()) { SetVolume(_volume, true); _sliderActive = false; }
						});
					});
					Tooltip(((int)_volume) + "");
				}

				ImGui.Text("Channel:");
				ImGui.SetNextItemWidth(217);
				ImGui.InputText("##twitchChannel", ref _twitchChannel, 100, ImGuiInputTextFlags.None);
			}
		}
		else
		{
			EmptyState(FontAwesomeIcon.Cat, "You have not summoned", "your Blue Carbuncle.");
		}

		ImGui.EndChild();
	}

	private static string NormalizeTwitchUrl(string input)
	{
		string trimmed = input.Trim();
		if (trimmed.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase))
		{
			return trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? trimmed : "https://" + trimmed;
		}
		return "https://www.twitch.tv/" + trimmed;
	}

	private void DrawQueue()
	{
		if (_videoQueue.Count == 0) { return; }

		ImGui.Separator();
		SectionHeader("Up Next");

		int? moveUp = null;
		int? moveDown = null;
		int? remove = null;

		for (int i = 0; i < _videoQueue.Count; i++)
		{
			if (IconButton(FontAwesomeIcon.ChevronUp, "queueUp" + i, disabled: i == 0)) { moveUp = i; }
			ImGui.SameLine();
			if (IconButton(FontAwesomeIcon.ChevronDown, "queueDown" + i, disabled: i == _videoQueue.Count - 1)) { moveDown = i; }
			ImGui.SameLine();
			if (IconButton(FontAwesomeIcon.Times, "queueRemove" + i)) { remove = i; }
			ImGui.SameLine();
			ImGui.Text(_videoQueue[i]);
		}

		if (moveUp.HasValue)
		{
			(_videoQueue[moveUp.Value - 1], _videoQueue[moveUp.Value]) = (_videoQueue[moveUp.Value], _videoQueue[moveUp.Value - 1]);
		}
		if (moveDown.HasValue)
		{
			(_videoQueue[moveDown.Value + 1], _videoQueue[moveDown.Value]) = (_videoQueue[moveDown.Value], _videoQueue[moveDown.Value + 1]);
		}
		if (remove.HasValue)
		{
			_videoQueue.RemoveAt(remove.Value);
		}
	}

	private void DrawGame()
	{
		ImGui.BeginChild("##scrollListGame" + _plugin.Name, new Vector2(0, 0), true);

		uint localPlayerId = Services.LocalPlayerId;
		bool snesExists = !string.IsNullOrEmpty(_plugin.AssemblyLocationSnes);

		if ( _playerCarbuncleFound || _core.TVIsVisible(localPlayerId))
		{
			bool playerTVRunning = _core.TVIsActive(localPlayerId);

			if (IconButton(FontAwesomeIcon.Cat, "powersnes",
				_playerCarbuncleFound ? new Vector4(1.0f, 1.0f, 1.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f), true))
			{
				if (_playerCarbuncleFound)
				{
					PenumbraIPC.ApplyTempMod("companion", _plugin.PenumbraTempModPaths);
					PenumbraIPC.ApplyTempMod("qr", _plugin.PenumbraQRPaths);
					PenumbraIPC.Redraw(_core.GetCompanionIndex(localPlayerId));
				}
				else
				{
					PenumbraIPC.RemoveTempMod("companion");
					PenumbraIPC.RemoveTempMod("qr");
					PenumbraIPC.Redraw(_core.GetCompanionIndex(localPlayerId));
					_core.RemoveCompanion();
				}
			}

			if (!_playerCarbuncleFound)
			{
				if (playerTVRunning && _core.IsPlayingSnes())
				{
					ImGui.SameLine();
					if (IconButton(FontAwesomeIcon.Stop, "stopgame", new Vector4(1.0f, 0.0f, 0.0f, 1.0f), true))
					{
						StopVideo();
					}

					ImGui.SameLine();

					if (IconButton(FontAwesomeIcon.Gamepad, "alphaenablecontrols",
						_core.IsPlayingSnes() && _core.IsSnesControlsEnabled() ? new Vector4(0.0f, 1.0f, 1.0f, 1.0f) : new Vector4(1.0f, 1.0f, 1.0f, 1.0f), true))
					{
						_core.EnableSnesControls(!_core.IsSnesControlsEnabled());
					}
					Tooltip((_core.IsPlayingSnes() && _core.IsSnesControlsEnabled()) ? "Unplug Controller" : "Plug in Controller");

					ImGui.SameLine();

					Style(ImGuiStyleVar.FramePadding, new Vector2(0, 8), () =>
					{
						IconFont(() =>
						{
							ImGui.SetNextItemWidth(104);
							ImGui.SliderFloat("##volumebarsnes", ref _volumesnes, 0, 100, _volumesnes < 1 ? FontAwesomeIcon.VolumeMute.ToIconString() : (_volumesnes <= 60 ? FontAwesomeIcon.VolumeDown.ToIconString() : FontAwesomeIcon.VolumeUp.ToIconString()));
							if (ImGui.IsItemActive()) { SetVolume(_volumesnes); _sliderActive = true; }
							if (ImGui.IsItemDeactivatedAfterEdit()) { SetVolume(_volumesnes); _sliderActive = false; }
						});
					});
					Tooltip(((int)_volumesnes)+"");
				}

				if (snesExists)
				{
					if (ImGui.CollapsingHeader("TV Effect##" + localPlayerId, ImGuiTreeNodeFlags.DefaultOpen))
					{
						string[] effectNames = ["None", "CRT Scanlines"];
						int effectIndex = (int)_core.SnesEffect;
						ImGui.SetNextItemWidth(217);
						if (ImGui.Combo("##snesEffect" + localPlayerId, ref effectIndex, effectNames, effectNames.Length))
						{
							_core.SetSnesEffect((Snes9xEffect)effectIndex, _core.SnesEffectMaskStrength, _core.SnesEffectScanBeam);
						}

						if (_core.SnesEffect == Snes9xEffect.CrtScanlines)
						{
							float mask = _core.SnesEffectMaskStrength;
							ImGui.SetNextItemWidth(217);
							if (ImGui.SliderFloat("Mask Strength##" + localPlayerId, ref mask, 0f, 1f))
							{
								_core.SetSnesEffect(_core.SnesEffect, mask, _core.SnesEffectScanBeam);
							}

							float beam = _core.SnesEffectScanBeam;
							ImGui.SetNextItemWidth(217);
							if (ImGui.SliderFloat("Scan Beam##" + localPlayerId, ref beam, 0.5f, 6f))
							{
								_core.SetSnesEffect(_core.SnesEffect, _core.SnesEffectMaskStrength, beam);
							}
						}
					}

					if (ImGui.CollapsingHeader("Co-op##" + localPlayerId, ImGuiTreeNodeFlags.DefaultOpen))
					{
						DrawCoop(localPlayerId);
					}
				}

				if (snesExists && (!playerTVRunning || _core.IsPlayingSnes()))
				{
					if(ImGui.CollapsingHeader("Load ROM##" + localPlayerId, ImGuiTreeNodeFlags.DefaultOpen))
					{
						List<string> paths = _core.GetRecentSnesPaths();
						if(paths.Count > 0)
						{
							ImGui.Text("Open Recent:");
						}
						int cnt = 1;
						foreach(string path in paths)
						{
							if(ImGui.Button(cnt++ + ". " + Path.GetFileName(path)))
							{
								if (File.Exists(path))
								{
									_core.PlaySnes(Services.LocalPlayerId, path);
								}
								else
								{
									Plugin.ErrorPopup("Could not find file: " + path);
									_core.RemoveSnesPath(path);
								}
							}
						}
						if (Button("Open ROM Folder", "openRomFolder", null, isBig:true))
						{
							Process.Start(new ProcessStartInfo
							{
								FileName = _plugin.ROMSLocationSnesDir,
								UseShellExecute = true
							});
						}

						ImGui.SameLine();

						if (Button("Select ROM", "selectRom", isBig:true))
						{
							_fileDialog.OpenFileDialog(
								"load SNES ROM",
								"SNES ROMs{.sfc,.smc},All Files{.*}",
								(success, paths) =>
								{
									if (!success || paths.Count == 0) { return; }
									string romPath = paths[0];
									
									_core.PlaySnes(Services.LocalPlayerId, romPath);
								},
								1,
								_plugin.ROMSLocationSnesDir,
								false);
						}
					}
					if(ImGui.CollapsingHeader("Input Configuration##" + localPlayerId))
					{
						const string pressAKey = "Awaiting keypress... (click=abort)";

						foreach (Snes9xInput key in _core.Input.SnesKeyMap.Keys)
						{
							_core.Input.SnesKeyMap.TryGetValue(key, out string? boundKey);
							float pos = ImGui.GetCursorPosX();
							ImGui.Text(key.ToString());
							ImGui.SameLine();
							ImGui.SetCursorPosX(pos + 70);
							string label = (_awaitKeyPress == (int)key ? pressAKey : string.IsNullOrEmpty(boundKey) || boundKey == "NO_KEY" ? "Unmapped" : boundKey) + "##keymap" + key;

							if (ImGui.Button(label))
							{
								_awaitKeyPress = _awaitKeyPress == (int)key ? -1 : (int)key;
							}

							if (_awaitKeyPress == (int)key)
							{
								if (_core.Input.TryDetectInput(out string detectedKey))
								{
									_core.Input.AssignKey(key, detectedKey);
									_awaitKeyPress = -1;
								}
							}
						}
					}
				}
				else
				{
					if(!snesExists)
					{
						EmptyState(FontAwesomeIcon.Gamepad, "Snes9x not found...");
					}
					else
					{
						EmptyState(FontAwesomeIcon.Film, "Video player is running");
					}
				}
			}
		}
		else
		{
			EmptyState(FontAwesomeIcon.Cat, "You have not summoned", "your Blue Carbuncle.");
		}

		ImGui.EndChild();
	}

	private void DrawCoop(uint localPlayerId)
	{
		if (string.IsNullOrWhiteSpace(_plugin.Config.RelayUrl))
		{
			EmptyState(FontAwesomeIcon.Wifi, "Set a Relay URL in Settings", "to use Co-op.");
			return;
		}

		if (_core.Coop.IsConnected && _core.Coop.IsPaired)
		{
			ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.3f, 1f), _core.CoopJoinActive ? "Connected - sending input" : "Player 2 connected!");
			if (Button("Disconnect", "coopDisconnect" + localPlayerId))
			{
				_core.Coop.Disconnect();
				_core.CoopJoinActive = false;
			}
		}
		else if (_core.Coop.IsConnected && _core.Coop.RoomCode != null)
		{
			ImGui.Text("Share this code:");
			ImGui.SameLine();
			Color(ImGuiCol.Text, RokuAccent, () => ImGui.Text(_core.Coop.RoomCode));
			ImGui.SameLine();
			if (IconButton(FontAwesomeIcon.Clipboard, "coopCodeCopy" + localPlayerId))
			{
				ImGui.SetClipboardText(_core.Coop.RoomCode);
			}
			ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "Waiting for player 2...");
			if (Button("Cancel", "coopCancel" + localPlayerId))
			{
				_core.Coop.Disconnect();
			}
		}
		else
		{
			if (Button("Host Co-op", "coopHost" + localPlayerId))
			{
				_ = _core.Coop.HostAsync(_plugin.Config.RelayUrl).ContinueWith(t =>
				{
					if (t.IsFaulted) { Services.Log.Error("[Coop] Failed to host: " + t.Exception); }
				});
			}

			ImGui.Text("or join with a code:");
			ImGui.SetNextItemWidth(120);
			ImGui.InputText("##coopJoinCode" + localPlayerId, ref _coopJoinCode, 6, ImGuiInputTextFlags.CharsUppercase);
			ImGui.SameLine();
			if (Button("Join", "coopJoin" + localPlayerId, disabled: string.IsNullOrWhiteSpace(_coopJoinCode)))
			{
				_core.CoopJoinActive = true;
				string code = _coopJoinCode;
				_ = _core.Coop.JoinAsync(_plugin.Config.RelayUrl, code).ContinueWith(t =>
				{
					if (t.IsFaulted) { Services.Log.Error("[Coop] Failed to join: " + t.Exception); }
				});
			}
		}

		if (!string.IsNullOrEmpty(_core.Coop.LastError))
		{
			ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1f), _core.Coop.LastError);
		}
	}

	private void DrawSettings()
	{
		ImGui.BeginChild("##scrollListHost" + _plugin.Name, new Vector2(0, 0), true);

		bool mpvUpdateAvailable = _plugin.LibResources.MpvCheckResult[0] != string.Empty;
		bool ytdlpUpdateAvailable = _plugin.LibResources.YtdlpCheckResult[0] != string.Empty;
		bool snesInstallAvailable = string.IsNullOrEmpty(_plugin.AssemblyLocationSnes);


		bool installingMPV = _updatingMPV;
		bool installingYTDLP = _updatingYTDLP;
		bool installingSNES9X = _updatingSNES9X;

		SectionHeader("Dependencies");

		ImGui.Text("mpv-winbuild");
		ImGui.SameLine();
		if (mpvUpdateAvailable)
		{
			if (Button(installingMPV ? "Installing..." : "Install", "mpvUpdate", disabled: installingMPV))
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
		}
		else
		{
			IconFont(() => ImGui.Text(FontAwesomeIcon.CheckCircle.ToIconString()));
		}

		ImGui.Text("yt-dlp");
		ImGui.SameLine();
		if (ytdlpUpdateAvailable)
		{
			if (Button(installingYTDLP ? "Installing..." : "Install", "ytdlpUpdate", disabled: installingYTDLP))
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
		}
		else
		{
			IconFont(() => ImGui.Text(FontAwesomeIcon.CheckCircle.ToIconString()));
		}

		ImGui.Text("snes9x");
		ImGui.SameLine();
		if (snesInstallAvailable)
		{
			if (Button(installingSNES9X ? "Installing..." : "Install", "snes9xUpdate", disabled: installingSNES9X))
			{
				_updatingSNES9X = true;
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
					_updatingSNES9X = false;
				});
			}
		}
		else
		{
			IconFont(() => ImGui.Text(FontAwesomeIcon.CheckCircle.ToIconString()));
		}

		SectionHeader("Display");

		bool hideNearbyNameplates = _core.HideNearbyNameplates;
		if (ImGui.Checkbox("Hide nameplates in front of the TV", ref hideNearbyNameplates))
		{
			_core.HideNearbyNameplates = hideNearbyNameplates;
		}

		SectionHeader("YouTube");

		string[] qualityLabels = ["480p", "720p", "1080p", "Best Available"];
		int[] qualityValues = [480, 720, 1080, 0];
		int qualityIndex = Array.IndexOf(qualityValues, _plugin.Config.YoutubeMaxQuality);
		if (qualityIndex < 0) { qualityIndex = 2; }
		ImGui.Text("Max Quality:");
		ImGui.SetNextItemWidth(217);
		if (ImGui.Combo("##youtubeQuality", ref qualityIndex, qualityLabels, qualityLabels.Length))
		{
			_plugin.Config.YoutubeMaxQuality = qualityValues[qualityIndex];
			_plugin.Config.Save();
		}

		ImGui.Text("Default Volume:");
		int defaultVolume = _plugin.Config.YoutubeDefaultVolume;
		ImGui.SetNextItemWidth(217);
		if (ImGui.SliderInt("##youtubeDefaultVolume", ref defaultVolume, 0, 100))
		{
			_plugin.Config.YoutubeDefaultVolume = defaultVolume;
			_plugin.Config.Save();
		}

		bool hwDecode = _plugin.Config.YoutubeHardwareDecoding;
		if (ImGui.Checkbox("Hardware Decoding", ref hwDecode))
		{
			_plugin.Config.YoutubeHardwareDecoding = hwDecode;
			_plugin.Config.Save();
		}

		bool disableTls = _plugin.Config.YoutubeDisableTlsVerify;
		if (ImGui.Checkbox("Disable TLS Verification", ref disableTls))
		{
			_plugin.Config.YoutubeDisableTlsVerify = disableTls;
			_plugin.Config.Save();
		}
		Tooltip("Workaround for cert issues under Wine. Reduces security - only enable if playback fails otherwise.");

		SectionHeader("Multiplayer");

		ImGui.Text("Relay URL:");
		string relayUrl = _plugin.Config.RelayUrl;
		ImGui.SetNextItemWidth(217);
		if (ImGui.InputText("##relayUrl", ref relayUrl, 200, ImGuiInputTextFlags.None))
		{
			_plugin.Config.RelayUrl = relayUrl;
			_plugin.Config.Save();
		}

		ImGui.EndChild();
	}

	private static bool Button(string value, string id, Vector4? color = null, bool isBig = false, bool disabled = false)
	{
		if (disabled) { ImGui.BeginDisabled(); }
		if (color.HasValue) { ImGui.PushStyleColor(ImGuiCol.Text, color.Value); }
		if (isBig) { ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12, 8)); }
		bool result = ImGui.Button(value + "##" + id);
		if (isBig) { ImGui.PopStyleVar(); }
		if (color.HasValue) { ImGui.PopStyleColor(); }
		if (disabled) { ImGui.EndDisabled(); }
		return result;
	}

	private static bool IconButton(FontAwesomeIcon icon, string id, Vector4? color = null, bool isBig = false, bool disabled = false)
	{
		if (disabled) { ImGui.BeginDisabled(); }
		if (color.HasValue) { ImGui.PushStyleColor(ImGuiCol.Text, color.Value); }
		if (isBig) { ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12, 8)); }
		ImGui.PushFont(UiBuilder.IconFont);
		bool result = ImGui.Button(icon.ToIconString() + "##" + id);
		ImGui.PopFont();
		if (isBig) { ImGui.PopStyleVar(); }
		if (color.HasValue) { ImGui.PopStyleColor(); }
		if (disabled) { ImGui.EndDisabled(); }
		return result;
	}

	private readonly struct Padding : IDisposable
	{
		internal Padding(Vector2 val) => ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, val);
		public void Dispose() => ImGui.PopStyleVar();
	}

	private static readonly Vector4 RokuBg = new(0.06f, 0.02f, 0.12f, 1.0f);
	private static readonly Vector4 RokuBgChild = new(0.10f, 0.04f, 0.18f, 1.0f);
	private static readonly Vector4 RokuPurple = new(0.40f, 0.18f, 0.58f, 1.0f);
	private static readonly Vector4 RokuPurpleHovered = new(0.52f, 0.24f, 0.72f, 1.0f);
	private static readonly Vector4 RokuPurpleActive = new(0.30f, 0.13f, 0.45f, 1.0f);
	private static readonly Vector4 RokuAccent = new(0.72f, 0.42f, 0.90f, 1.0f);
	private static readonly Vector4 RokuText = new(0.95f, 0.95f, 0.98f, 1.0f);
	private static readonly Vector4 RokuBorder = new(0.40f, 0.18f, 0.58f, 0.6f);

	private readonly struct RokuTheme : IDisposable
	{
		private const int ColorCount = 19;
		private const int VarCount = 5;

		public RokuTheme()
		{
			ImGui.PushStyleColor(ImGuiCol.WindowBg, RokuBg);
			ImGui.PushStyleColor(ImGuiCol.ChildBg, RokuBgChild);
			ImGui.PushStyleColor(ImGuiCol.TitleBg, RokuBgChild);
			ImGui.PushStyleColor(ImGuiCol.TitleBgActive, RokuPurpleActive);
			ImGui.PushStyleColor(ImGuiCol.Border, RokuBorder);
			ImGui.PushStyleColor(ImGuiCol.FrameBg, RokuPurpleActive);
			ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, RokuPurple);
			ImGui.PushStyleColor(ImGuiCol.FrameBgActive, RokuPurpleHovered);
			ImGui.PushStyleColor(ImGuiCol.Button, RokuPurple);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, RokuPurpleHovered);
			ImGui.PushStyleColor(ImGuiCol.ButtonActive, RokuPurpleActive);
			ImGui.PushStyleColor(ImGuiCol.Tab, RokuBgChild);
			ImGui.PushStyleColor(ImGuiCol.TabHovered, RokuPurpleHovered);
			ImGui.PushStyleColor(ImGuiCol.TabActive, RokuPurple);
			ImGui.PushStyleColor(ImGuiCol.SliderGrab, RokuAccent);
			ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, RokuAccent);
			ImGui.PushStyleColor(ImGuiCol.CheckMark, RokuAccent);
			ImGui.PushStyleColor(ImGuiCol.Text, RokuText);
			ImGui.PushStyleColor(ImGuiCol.Separator, RokuPurple);

			ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 14f);
			ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 12f);
			ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 10f);
			ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 10f);
			ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 10f);
		}

		public void Dispose()
		{
			ImGui.PopStyleVar(VarCount);
			ImGui.PopStyleColor(ColorCount);
		}
	}

	private static void Tooltip(string text)
	{
		if (ImGui.IsItemHovered()) { ImGui.BeginTooltip(); ImGui.Text(text); ImGui.EndTooltip(); }
	}

	private static void SectionHeader(string text)
	{
		Color(ImGuiCol.Text, RokuAccent, () => ImGui.Text(text.ToUpperInvariant()));
		ImGui.Spacing();
	}

	private static void EmptyState(FontAwesomeIcon icon, string line1, string? line2 = null)
	{
		ImGui.Spacing();
		Color(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1.0f), () =>
		{
			IconFont(() => ImGui.Text(icon.ToIconString()));
			ImGui.SameLine();
			ImGui.Text(line1);
			if (line2 != null) { ImGui.Text("  " + line2); }
		});
	}

	private static void Color(ImGuiCol col, Vector4? color, Action body)
	{
		if (color.HasValue) { ImGui.PushStyleColor(col, color.Value); }
		body();
		if (color.HasValue) { ImGui.PopStyleColor(); }
	}

	private static void Style(ImGuiStyleVar styleVar, float val, Action body)
	{
		ImGui.PushStyleVar(styleVar, val);
		body();
		ImGui.PopStyleVar();
	}
	private static void Style(ImGuiStyleVar styleVar, Vector2 val, Action body)
	{
		ImGui.PushStyleVar(styleVar, val);
		body();
		ImGui.PopStyleVar();
	}

	private static void IconFont(Action body)
	{
		ImGui.PushFont(UiBuilder.IconFont);
		body();
		ImGui.PopFont();
	}

	private static void WithDisabled(bool active, Action body)
	{
		if (active) { ImGui.BeginDisabled(); }
		body();
		if (active) { ImGui.EndDisabled(); }
	}

	public void Dispose()
	{
		_apiHelper.OnNewPlayerSeen -= HandleNewPlayerSeen;
		_core.OnLocalVideoIdle -= PlayNextQueued;
		_core.StopVideo();
		_core.Dispose();
		GC.SuppressFinalize(this);
	}
}