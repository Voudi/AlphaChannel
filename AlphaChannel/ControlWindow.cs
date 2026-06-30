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
	private bool _libsLoaded;
	private bool _installingLibs;
	private bool _updatingMPV;
	private bool _updatingYTDLP;
	private bool _updatingSNES9X;

	//Video player vars
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
	private bool _pauseToggle;
	private bool _mpvIsIdle;
	private bool _mpvIsDone => _mpvIsIdle && _seekerMaxSeconds - 2 < _seekerExactTime;
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

		SizeConstraints = new WindowSizeConstraints
		{
			MinimumSize = new Vector2(275, 235),
			MaximumSize = new Vector2(275, 1080)
		};
	}
	
	public override void Draw()
	{
		using var _wp = new Padding(new Vector2(5, 5));

		if (Services.DutyState.IsDutyStarted)
		{
			ImGui.Text("AlphaChannel is deactivated");
			ImGui.Text("inside duties.");
		}
		else
		{
			if (!_libsLoaded)
			{
				bool needsFirstInstall = string.IsNullOrWhiteSpace(_plugin.AssemblyLocationMPV) || string.IsNullOrWhiteSpace(_plugin.AssemblyLocationYTDLP)|| string.IsNullOrWhiteSpace(_plugin.AssemblyLocationSnes);

				_libsLoaded = !needsFirstInstall;
				if (!_libsLoaded)
				{
					DrawFirstInstall();
				}
			}

			if (_libsLoaded && ImGui.BeginTabBar("AlphaChannelTabBar") && Services.LocalPlayerExists)
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
		}

		_fileDialog.Draw();
	}

	private void StartVideo(uint entityId)
	{
		if (Services.LocalPlayerId == entityId)
		{
			if (_core.ValidateURL(_inputURL, out Uri? uri) && uri != null)
			{
				_core.PlayVideo(entityId, uri.ToString());
			}
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
		if (string.IsNullOrEmpty(_inputURL) && !string.IsNullOrEmpty(_urlPlaceholder) && _urlPlaceholder != UrlPlaceholderDefault)
		{
			_inputURL = _urlPlaceholder;
			_urlPlaceholder = UrlPlaceholderDefault;
		}
	}

	private long _lastMilliSecond1000ms;
	private long _lastMilliSecond6ms;
	internal void OnFrameworkUpdate()
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

			if (Services.LocalPlayerExists)
			{
				_visiblePlayers = Services.Objects.Where(x => x is IPlayerCharacter).OrderBy(x => x.Name.TextValue);
			}
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

	private void SeekPlayer(double percentage, bool silent = false)
	{
		int seconds = (int)(_seekerMaxSeconds * (percentage / 100));
		Services.Log.Debug("Seeking to " + seconds + " seconds");
		if (silent)
		{
			_core.SeekSilent(seconds);
		}
		else
		{
			_core.Seek(seconds);
		}
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

	private void DrawFirstInstall()
	{
		ImGui.BeginChild("##scrollListInstall" + _plugin.Name, new Vector2(0, 0), true);

		bool snesInstallAvailable = string.IsNullOrEmpty(_plugin.AssemblyLocationSnes);
		bool updatesAvailable = !string.IsNullOrWhiteSpace(_plugin.LibResources.MpvCheckResult[0]) || !string.IsNullOrWhiteSpace(_plugin.LibResources.YtdlpCheckResult[0]) || snesInstallAvailable;
		

		if (_installingLibs)
		{
			ImGui.Text("Installing dependencies...");
		}
		else
		{
			ImGui.Text("Please download the required ");
			ImGui.Text("dependencies to use AlphaChannel:");
			if (Button(updatesAvailable ? "Install dependencies" : "Checking for updates...", "installDeps", disabled: !updatesAvailable))
			{
				Services.Log.Debug("Installing AlphaChannel Dependencies...");
				Task.Run(() => {
					if (string.IsNullOrWhiteSpace(_plugin.AssemblyLocationMPV) || !string.IsNullOrWhiteSpace(_plugin.LibResources.MpvCheckResult[0]))
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
				}).ContinueWith(task =>
				{
					if (string.IsNullOrWhiteSpace(_plugin.AssemblyLocationYTDLP) || !string.IsNullOrWhiteSpace(_plugin.LibResources.YtdlpCheckResult[0]))
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
				}).ContinueWith(task =>
				{
					if (snesInstallAvailable)
					{
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
				});

				_installingLibs = true;
			}
		}
		

		ImGui.EndChild();
	}
	private void DrawJoin()
	{
		ImGui.BeginChild("##scrollListJoin" + _plugin.Name, new Vector2(0, 0), true);

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
			ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: No hosts detected nearby.");
		}

		ImGui.EndChild();
		ImGui.EndTabItem();
	}
	private void DrawHost()
	{
		ImGui.BeginChild("##scrollListHost" + _plugin.Name, new Vector2(0, 0), true);
		Vector4 textColor;
		uint localPlayerId = Services.LocalPlayerId;
		if (_playerCarbuncleFound || _core.TVIsVisible(localPlayerId)) //Checks if players Carbuncle or TV exists
		{
			bool playerTVRunning = _core.TVIsActive(localPlayerId);
			bool urlEmpty = string.IsNullOrEmpty(_inputURL);
			bool urlExists = _core.ValidateURL(_inputURL, out _);
			bool refreshNeeded = playerTVRunning && !string.IsNullOrEmpty(_inputURL) && urlExists;

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

				bool hostPlayDisabled = !urlExists && !playerTVRunning;
				Vector4? hostPlayColor = playerTVRunning ? new Vector4(1.0f, 0.0f, 0.0f, 1.0f) : (hostPlayDisabled ? new Vector4(0.5f, 0.5f, 0.5f, 1.0f) : null);
				FontAwesomeIcon hostPlayIcon = playerTVRunning ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play;
				if (IconButton(hostPlayIcon, "playbutton" + localPlayerId, hostPlayColor, true, disabled: hostPlayDisabled))
				{
					if (!playerTVRunning)
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
						StopVideo();
					}
				}
				Tooltip(playerTVRunning ? "Stop" : "Play");

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
									SeekPlayer(0, true);
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
			}
		}
		else
		{
			ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: You have not summoned");
			ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " your Blue Carbuncle.");
		}
		
		ImGui.EndChild();
		ImGui.EndTabItem();
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
						ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: Snes9x not found...");
					}
					else
					{
						ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: Video player is running");
					}
				}
			}
		}
		else
		{
			ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: You have not summoned");
			ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " your Blue Carbuncle.");
		}

		ImGui.EndChild();
		ImGui.EndTabItem();
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

		ImGui.Text("Dependencies:");

		ImGui.Text("mpv-winbuild");
		ImGui.SameLine();
		if (false && mpvUpdateAvailable) //Deactivate updating from inside the plugin for now
		{
			if (Button(installingMPV ? "Updating..." : "Update", "mpvUpdate", disabled: installingMPV))
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
		if (false && ytdlpUpdateAvailable)
		{
			if (Button(installingYTDLP ? "Updating..." : "Update", "ytdlpUpdate", disabled: installingYTDLP))
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
		if (false && snesInstallAvailable)
		{
			if (Button(installingSNES9X ? "Updating..." : "Update", "snes9xUpdate", disabled: installingSNES9X))
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

		ImGui.EndChild();
		ImGui.EndTabItem();
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

	private static void Tooltip(string text)
	{
		if (ImGui.IsItemHovered()) { ImGui.BeginTooltip(); ImGui.Text(text); ImGui.EndTooltip(); }
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
		_core.StopVideo();
		_core.Dispose();
		GC.SuppressFinalize(this);
	}
}