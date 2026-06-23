using System.Diagnostics;
using System.Numerics;
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

internal sealed class ControlWindow : Window, IDisposable
{
	private readonly Plugin _plugin;
	private readonly Core _core;
	private readonly APIHelper _apiHelper;

	private uint LocalEntityId => Services.Objects?.LocalPlayer?.EntityId ?? 0;
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
		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(5, 5));

		if (Services.DutyState.IsDutyStarted)
		{
			ImGui.Text("AlphaChannel is deactivated");
			ImGui.Text("inside duties.");
			return;
		}
		
		if (!_libsLoaded)
		{
			bool needsFirstInstall = string.IsNullOrWhiteSpace(_plugin.AssemblyLocationMPV) || string.IsNullOrWhiteSpace(_plugin.AssemblyLocationYTDLP);

			_libsLoaded = !needsFirstInstall;
			if (!_libsLoaded)
			{
				DrawFirstInstall();

				ImGui.PopStyleVar();
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

		ImGui.PopStyleVar();
		_fileDialog.Draw();
	}

	private void StartVideo(uint entityId)
	{
		if (LocalEntityId == entityId)
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

	private void StartSnes(string path)
	{
		_core.PlaySnes(LocalEntityId, path);
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

			_visiblePlayers = Services.Objects.Where(x => x is IPlayerCharacter).OrderBy(x => (x.EntityId == LocalEntityId) ? "@" : x.Name.TextValue);
		}
	}

	private void SetVolume(float volume, bool quadratic = false)
	{
		int vol = (int)volume;
		if(quadratic)
		{
			vol = (int)((float)Math.Sqrt(volume) * 10f); //Quadratic slider values
		}
		Services.Log.Debug("Setting volume to " + vol + "%");
		_core.SetVolume(vol);
	}

	private void SeekPlayer(double percentage)
	{
		int seconds = (int)(_seekerMaxSeconds * (percentage / 100));
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

	private void HandleNewPlayerSeen(IGameObject player, APIHelper.IPCVideoState state)
	{
		uint playerId = player.EntityId;
		DalamudLinkPayload linkPayload = Services.Chat.AddChatLinkHandler(_nextLinkId++, (commandId, msg) =>
		{
			StartVideo(playerId);
			if (!IsOpen) { Toggle(); }
		});
		string url = state.Url.Length > 60 ? state.Url[..60] + "..." : state.Url;
		SeString seString = new SeStringBuilder()
			.AddUiForeground("[AlphaChannel] ", 35)
			.AddText(player.Name.TextValue + " is currently hosting " + url)
			.Add(linkPayload)
			.AddUiForeground("[Click to start playback]", 32)
			.Add(RawPayload.LinkTerminator)
			.Build();
		Services.Chat.Print(new XivChatEntry { Message = seString, Type = XivChatType.Echo });
	}

	private void DrawFirstInstall()
	{
		bool updatesAvailable = !string.IsNullOrWhiteSpace(_plugin.LibResources.MpvCheckResult[0]) || !string.IsNullOrWhiteSpace(_plugin.LibResources.YtdlpCheckResult[0]);
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

			_installingLibs = true;
		}
		if (!updatesAvailable)
		{
			ImGui.EndDisabled();
		}
	}
	private void DrawJoin()
	{
		int count = 0;
		foreach (var item in _visiblePlayers)
		{
			if(item.EntityId == LocalEntityId)
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
					DrawScrollingText(_mediaTitle, 125);
				}
				else
				{
					ImGui.Text(item.Name.TextValue);
				}

				
				if (isTheRunningTV)
				{						
					ImGui.SameLine();

					ImGui.PushFont(UiBuilder.IconFont);
					ImGui.SetNextItemWidth(100);
					ImGui.SliderFloat("##volumebar" + item.EntityId, ref _volume, 0, 100, _volume < 1 ? FontAwesomeIcon.VolumeMute.ToIconString() : (_volume <= 60 ? FontAwesomeIcon.VolumeDown.ToIconString() : FontAwesomeIcon.VolumeUp.ToIconString()));
					if (ImGui.IsItemActive())
					{
						_sliderActive = true;
					}

					if (ImGui.IsItemDeactivatedAfterEdit())
					{
						SetVolume(_volume, true);
						_sliderActive = false;
					}
					ImGui.PopFont();

					ImGui.BeginDisabled();
					ImGui.SetNextItemWidth(268);
					ImGui.PushStyleColor(ImGuiCol.SliderGrab, new Vector4(0.8f, 0.3f, 0.3f, 1));
					ImGui.SliderFloat("##seeker" + item.EntityId, ref _seeker, 0, 100, $"{_seekerTimeMinutes}:{_seekerTimeSeconds:00} / {_seekerDurationMinutes}:{_seekerDurationSeconds:00}");
					if (ImGui.IsItemActive())
					{
						_sliderActive = true;
					}

					if (ImGui.IsItemDeactivatedAfterEdit())
					{
						SeekPlayer(_seeker);
						_sliderActive = false;
					}
					ImGui.PopStyleColor(1);
					ImGui.EndDisabled();
				}

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
			if (_playerCarbuncleFound || _core.TVIsVisible(player.EntityId)) //Checks if players Carbuncle or TV exists
			{
				bool playerTVRunning = _core.TVIsActive(LocalEntityId);
				bool urlEmpty = string.IsNullOrEmpty(_inputURL);
				bool urlExists = _core.ValidateURL(_inputURL, out _);

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
					PenumbraIPC.Redraw(_core.GetCompanionIndex(player.EntityId));
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
						ImGui.SetClipboardText(string.IsNullOrEmpty(_inputURL) && playerTVRunning ? _urlPlaceholder : _inputURL);
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

								_urlPlaceholder = _inputURL;
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
							_sliderActive = true;
						}

						if (ImGui.IsItemDeactivatedAfterEdit())
						{
							SeekPlayer(_seeker);
							_sliderActive = false;
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
							_sliderActive = true;
						}

						if (ImGui.IsItemDeactivatedAfterEdit())
						{
							SetVolume(_volume, true);
							_sliderActive = false;
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
		uint entityId = LocalEntityId;
		bool snesExists = !string.IsNullOrEmpty(_plugin.AssemblyLocationSnes);

		if (_playerCarbuncleFound || _core.TVIsVisible(entityId))
		{
			bool playerTVRunning = _core.TVIsActive(LocalEntityId);

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
				PenumbraIPC.Redraw(_core.GetCompanionIndex(entityId));
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
						_sliderActive = true;
					}
					if (ImGui.IsItemDeactivatedAfterEdit())
					{
						SetVolume(_volumesnes);
						_sliderActive = false;
					}
					ImGui.PopFont();
				}

				if (snesExists)
				{
					if (ImGui.Button("Open Folder"))
					{
						Process.Start(new ProcessStartInfo
						{
							FileName = _plugin.ROMSLocationSnesDir,
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
								
								StartSnes(romPath);

							},
							1,
							_plugin.ROMSLocationSnesDir,
							false);
					}
				}
				else
				{
					ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: Snes9x not found");
				}

				ImGui.Text("Configure Keys:");

				string pressAKey = "Press a key... (Click again to abort)";

				foreach(Snes9xInput key in _core.SnesKeyMap.Keys)
				{
					if(_core.SnesKeyMap.TryGetValue(key, out string? virtualKey))
					{
						float pos = ImGui.GetCursorPosX();
						ImGui.Text(key.ToString());
						ImGui.SameLine();
						ImGui.SetCursorPosX(pos + 80);
						string label = (_awaitKeyPress == (int)key ? pressAKey : (virtualKey == null || virtualKey == VirtualKey.NO_KEY.ToString()) ? "Unmapped" : virtualKey) + "##keymap"+key;

						if (ImGui.Button(label))
						{
							if (_awaitKeyPress == (int)key)
							{
								_awaitKeyPress = -1;
							}
							else
							{
								_awaitKeyPress = (int)key;
							}
						}

						if (_awaitKeyPress == (int)key)
						{
							foreach (VirtualKey vk in Services.KeyState.GetValidVirtualKeys())
							{
								if (Services.KeyState[vk] && _core.IsSnesKeyMappable(vk))
								{
									string keyName = vk.ToString();
									foreach (Snes9xInput doubleKey in _core.SnesKeyMap.Keys)
									{
										if(_core.SnesKeyMap[doubleKey].Equals(keyName, StringComparison.OrdinalIgnoreCase))
										{
											_core.SnesKeyMap[doubleKey] = VirtualKey.NO_KEY.ToString();
											_plugin.Config.KeyMappings[doubleKey] = VirtualKey.NO_KEY.ToString();
											break;
										}
									}
									_core.SnesKeyMap[key] = keyName;
									_plugin.Config.KeyMappings[key] = keyName;
									_plugin.Config.Save();
									_awaitKeyPress = -1;
									break;
								}
							}
							foreach(int gamePadButton in _core.GetAllGamePadButtons())
							{
								if (_core.IsGamePadButtonPressed(gamePadButton))
								{
									string keyName = _core.GetGamePadButtonName(gamePadButton);
									foreach (Snes9xInput doubleKey in _core.SnesKeyMap.Keys)
									{
										if(_core.SnesKeyMap[doubleKey].Equals(keyName, StringComparison.OrdinalIgnoreCase))
										{
											_core.SnesKeyMap[doubleKey] = VirtualKey.NO_KEY.ToString();
											_plugin.Config.KeyMappings[doubleKey] = VirtualKey.NO_KEY.ToString();
											break;
										}
									}
									_core.SnesKeyMap[key] = keyName;
									_plugin.Config.KeyMappings[key] = keyName;
									_plugin.Config.Save();
									_awaitKeyPress = -1;
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
		bool installingSNES9X = _updatingSNES9X;

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

	public void Dispose()
	{
		_apiHelper.OnNewPlayerSeen -= HandleNewPlayerSeen;
		_core.StopVideo();
		_core.Dispose();
		GC.SuppressFinalize(this);
	}
}
