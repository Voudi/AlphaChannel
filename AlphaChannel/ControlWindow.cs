using AlphaChannel;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using System.Numerics;

public class ControlWindow : Window, IDisposable
{
    private bool _signalShareTitle = false;
    private bool _modenabled = false;
    private bool _installWarningMessage = false;

	private bool _shareURLToggle = false;
	private bool _syncPlayToggle = false;
	private bool _pauseToggle = false;

    //Render Vars
    private string _inputURL = "";
	private float _volume = 100;
	private float _seeker = 0;
	private int _seekerTimeSeconds = 0;
	private int _seekerTimeMinutes = 0;
	private int _seekerDurationSeconds = 0;
	private int _seekerDurationMinutes = 0;
	private int _seekerMaxSeconds = 0;
	private bool _libsLoaded = false;
	private bool _updatingLibs = false;
	private bool _mvpIsPlaying = false;
	private bool _UIElementActive = false;
	private string _mediaTitle = string.Empty;
	private bool _sharingTitle = false;
	private bool _mpvIsIdle = true;
	private string _shortenedURL = "";
    private string _lastURL = "";
	private DateTime _lastTVTurnOn= DateTime.MinValue;
	private Plugin _plugin;
	private Compatibility _compat;
	private Core _core;

	public delegate void URLShortenerCallback(string result);
	public delegate void URLFetchCallback(string result);
	public delegate void URLShortenerErrorCallback(string result);
	private readonly Dictionary<uint, string> _currentURLs = []; //Playerpointer, URL
	private readonly Dictionary<uint, string> _currentTitles = []; //Playerpointer, Title

	private readonly OTTApi _OTTApi;

    public unsafe ControlWindow(Plugin plugin, string title)
        : base(title, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
		_plugin = plugin;

        _OTTApi = new OTTApi(this);

		_compat = new Compatibility(_plugin);

		_core = new Core(_plugin);

        SizeConstraints = new WindowSizeConstraints
		{
			MinimumSize = new Vector2(275, 235),
			MaximumSize = new Vector2(275, 1080)
		};

        _ = _compat.CheckTVMod();

        if (!_OTTApi.IsInitialized)
            _OTTApi.Login();
		
		_compat.CheckForUpdates();
    }

	public void Dispose()
	{
        _core.Dispose();
	}

	private bool _isFocused = false;
	private String _placeHolderURL = String.Empty;
	private IEnumerable<IGameObject> _playerList = [];
	public override void Draw()
	{
		Vector4 textColor;
		var playerIsRunningTV = _core.IsPlayerTVOn();

        if (Services.DutyState.IsDutyStarted)
		{
            ImGui.Text("AlphaChannel is deactivated");
			ImGui.Text("while in duties.");
			return;
        }
		if(!_libsLoaded)
		{
			bool needsFirstInstall = _plugin.AssemblyLocationMPV == null || _plugin.AssemblyLocationYTDLP == null;
			bool updatesAvailable = (_plugin.LibResources.mpvCheckResult[0] != string.Empty) || (_plugin.LibResources.ytdlpCheckResult[0] != string.Empty);

			_libsLoaded = !needsFirstInstall && !updatesAvailable;
			if (!_libsLoaded)
			{
				if(_updatingLibs)
				{
					ImGui.Text("Downloading dependencies...");
					return;
				}
				ImGui.Text("Please download the required dependencies to use AlphaChannel:");
				if(!updatesAvailable)
					ImGui.BeginDisabled();
				if (ImGui.Button(updatesAvailable ? "Update dependencies" : "Checking for updates..."))
				{
					Services.Log.Debug("Updating AlphaChannel Dependencies...");
					if(_plugin.AssemblyLocationMPV == null || _plugin.LibResources.mpvCheckResult[0] != string.Empty)
						_plugin.LibResources.DownloadMPVAsync().ContinueWith(async task =>
						{
							if (task.Result)
							{
								Services.Log.Debug("MPV downloaded successfully");
								_plugin.AssemblyLocationMPV = _plugin.LibResources.GetLocationMPV()!;
								_plugin.LibResources.mpvCheckResult[0] = string.Empty;
							}
							else
							{
								Services.Log.Error("Failed to download MPV");
							}
						});
					if(_plugin.AssemblyLocationYTDLP == null || _plugin.LibResources.ytdlpCheckResult[0] != string.Empty)
						_plugin.LibResources.DownloadYTDLPAsync().ContinueWith(async task =>
						{
							if (task.Result)
							{
								Services.Log.Debug("YTDLP downloaded successfully");
								_plugin.AssemblyLocationYTDLP = _plugin.LibResources.GetLocationYTDLP()!;
								_plugin.LibResources.ytdlpCheckResult[0] = string.Empty;
							}
							else
							{
								Services.Log.Error("Failed to download YTDLP");
							}
						});
					_updatingLibs = true;
				}
				if(!updatesAvailable)
					ImGui.EndDisabled();

				return;
			}
			
		}
        if (!_core.IsTVTurnedOff() && !_core.TextureExists() && _lastTVTurnOn.AddSeconds(5) < DateTime.Now)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " Error: Cannot Fetch Screen Texture");
            ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " 1. Keep the plugin activated");
            ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " 2. Restart the game client, or");
			ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " 3. Teleport to another zone");
        }
        if (!_compat._modexists && !_ignoreError1)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " Please install the AlphaChannelTV");
			ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " Penumbra mod before continuing:");
            if (ImGui.Button("Step 1 - Install"))
            {
                _compat.InstallTVMod();
            }
            if (ImGui.Button("Ignore this error (for custom configurations)"))
            {
				_ignoreError1 = true;
            }
            return;
        }
        if (!_modenabled && _compat._installedmod && !_ignoreError2)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " Please enable the AlphaChannelTV");
			ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " Penumbra mod before continuing:");
            if (ImGui.Button("Step 2 - Enable"))
            {
                Services.CommandManager?.ProcessCommand("/penumbra reload");
                Services.CommandManager?.ProcessCommand("/penumbra mod enable Default | AlphaChannelTV");
                Services.CommandManager?.ProcessCommand("/penumbra redraw carbuncle");
                _modenabled = true;
            }
			if (ImGui.Button("Ignore this error (for custom configurations)"))
            {
				_ignoreError2 = true;
            }
            return;
        }

        ImGui.Text(" Available TV List:");
		ImGui.Separator();
        foreach (var item in _playerList)
		{
            var isPlayer = item.EntityId == Services.Objects.LocalPlayer?.EntityId;
            
			if(isPlayer && _installWarningMessage && _modenabled)
			{
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " Please enable the AlphaChannelTV");
				ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " Penumbra mod and make sure");
				ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " it has the highest priority.");
			}
            if ((isPlayer && _installWarningMessage) || _core.TVExistsForEntity(item.EntityId)) //Checks if players carbuncle exists OR other players TV exists
			{
				var isTheRunningTV = _core.IsEntityTVOn(item.EntityId);
                var url = string.Empty;
				bool urlExists = false;
				bool urlEmpty = string.IsNullOrEmpty(_inputURL);
				
				if (isPlayer)
				{
					urlExists = ValidateURL(out _);
				}
				else
				{
					if(_currentURLs.TryGetValue(item.EntityId, out var tempURL))
					{
						url = tempURL;
					}
				}

                ImGui.Text(isPlayer ? "YOU" : " " + item.Name.TextValue);

				ImGui.SameLine();


				if (isTheRunningTV)
				{
					textColor = isPlayer ? new Vector4(1.0f, 0.0f, 0.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f); ;
					ImGui.PushStyleColor(ImGuiCol.Text, textColor);
				}
				else if(isPlayer && _installWarningMessage)
				{
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.0f, 1.0f, 0.0f, 1.0f));
                }
				else if (!urlExists)
				{
					ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1.0f));
				}
				ImGui.PushFont(UiBuilder.IconFont);

				var refreshNeeded = isTheRunningTV && !string.IsNullOrEmpty(_inputURL) && isPlayer && urlExists;

				if (ImGui.Button((isTheRunningTV ?
					(refreshNeeded ?
						FontAwesomeIcon.Repeat.ToIconString()
						: FontAwesomeIcon.Stop.ToIconString()
					)
					: ((isPlayer && _installWarningMessage) ?
							FontAwesomeIcon.Download.ToIconString()
						 : FontAwesomeIcon.Play.ToIconString()
					)) + "##play" + item.EntityId))
				{
					try
					{
						if (!isTheRunningTV || refreshNeeded)
						{
							if(urlExists)
								TurnOnTV(item.EntityId, refreshNeeded);

							if (refreshNeeded) 
							{
								//Update title share if player is changing url (only for non-sync mode)
								if (_shareURLToggle && !_syncPlayToggle)
								{
									//Reapply sharing
									_signalShareTitle = true;
								}
							}

							if (isPlayer)
							{
								_placeHolderURL = _inputURL;
								_inputURL = string.Empty;
							}
						}
						else
						{
							TurnOffTV();
						}
                        
					}
					catch (Exception ex)
					{
						Services.Log.Error("FATAL ERROR: " + ex.ToString());
					}
				}
				ImGui.PopFont();

				if (isTheRunningTV || !urlExists || (isPlayer && _installWarningMessage)) ImGui.PopStyleColor();

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
							_mvpIsPlaying = false;
							if (_syncPlayToggle)
							{
								_OTTApi.PushNextVideo();
							}
							else
							{
								_core.PlayVideo(_placeHolderURL);
							}
						}
						else
						{
							if(_syncPlayToggle)
							_OTTApi.PlayPauseVideo(_pauseToggle);
							else
							{
								_plugin.TogglePause();
								_pauseToggle = !_pauseToggle;
							}
						}
                    }
                    ImGui.PopFont();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text("Pause/Resume");
                        ImGui.EndTooltip();
                    }

					ImGui.SameLine();

					ImGui.PushFont(UiBuilder.IconFont);
					ImGui.SetNextItemWidth(100);
					ImGui.SliderFloat("##volumebar" + item.EntityId, ref _volume, 0, 100, _volume < 1 ? FontAwesomeIcon.VolumeMute.ToIconString() : (_volume <= 60 ? FontAwesomeIcon.VolumeDown.ToIconString() : FontAwesomeIcon.VolumeUp.ToIconString()));
					if (ImGui.IsItemActive()) _UIElementActive = true;
					if(ImGui.IsItemDeactivatedAfterEdit())
					{
						VolumePlayer(_volume);
						_UIElementActive = false;
					}
					ImGui.PopFont();
				}
				if (isPlayer)
				{
					ImGui.SameLine();
		
					textColor = _shareURLToggle ? new Vector4(0.2f, 1.0f, 1.0f, 1.0f) : new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
					ImGui.PushStyleColor(ImGuiCol.Text, textColor);

					ImGui.PushFont(UiBuilder.IconFont);
					if (ImGui.Button(FontAwesomeIcon.ShareAlt.ToIconString() + "##urlshare"))
					{
						_shareURLToggle = !_shareURLToggle;
						if(playerIsRunningTV)
						{
							if (_shareURLToggle)
							{
								//Reapply sharing
								_signalShareTitle = true;
							}
							else
							{
								_signalShareTitle = false;
								Services.CommandManager?.ProcessCommand("/honorific force clear");
							}
						}
					}
					ImGui.PopFont();

					ImGui.PopStyleColor();

					if (ImGui.IsItemHovered())
					{
						ImGui.BeginTooltip();
						ImGui.Text(_shareURLToggle ? "Currently sharing URL with others, press to stop sharing." : "Currently not sharing URL with others, press to share URL.");
						ImGui.EndTooltip();
					}

					ImGui.SameLine();

					textColor = _syncPlayToggle ? new Vector4(0.2f, 1.0f, 1.0f, 1.0f) : new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
					ImGui.PushStyleColor(ImGuiCol.Text, textColor);

					ImGui.PushFont(UiBuilder.IconFont);
					if (ImGui.Button(FontAwesomeIcon.Link.ToIconString() + "##sync"))
					{
						if (!_OTTApi.IsInitialized)
							_OTTApi.Login();

						_syncPlayToggle = !_syncPlayToggle;

						if (playerIsRunningTV) //If currently hosting, turn it off, no matter what
						{
							TurnOffTV();
							if (string.IsNullOrEmpty(_inputURL))
							{
								_inputURL = _placeHolderURL;
							}
						}
					}
					ImGui.PopFont();

					ImGui.PopStyleColor();

					if (ImGui.IsItemHovered())
					{
						ImGui.BeginTooltip();
						ImGui.Text(_syncPlayToggle ? "Currently using video sync. Click to deactivate." : "Currently not using video sync. Click to activate.");
						ImGui.EndTooltip();
					}
				}

				if(_mvpIsPlaying && isTheRunningTV)
					DrawScrollingText(_mediaTitle, 250);

				if (_mvpIsPlaying && isTheRunningTV)
				{
					ImGui.SetNextItemWidth(268);
					ImGui.PushStyleColor(ImGuiCol.SliderGrab, new Vector4(0.8f, 0.3f, 0.3f, 1));
					ImGui.SliderFloat("##seeker" + item.EntityId, ref _seeker, 0, 100, $"{_seekerTimeMinutes}:{_seekerTimeSeconds:00} / {_seekerDurationMinutes}:{_seekerDurationSeconds:00}");
					if (ImGui.IsItemActive()) _UIElementActive = true;
					if(ImGui.IsItemDeactivatedAfterEdit())
					{
						SeekPlayer(_seeker);
						_UIElementActive = false;
					}
					ImGui.PopStyleColor(1);
				}

				if(!_playerList.Last().Equals(item))
				{
					ImGui.Separator();
				}

				if (isPlayer)
				{
					textColor = _syncPlayToggle ? 

						(_OTTApi.IsChecking ? new Vector4(0.8f, 0.8f, 0.3f, 1f) 
							: (urlExists || (isTheRunningTV && urlEmpty) ? new Vector4(0.3f, 0.8f, 0.3f, 1f) : new Vector4(0.8f, 0.3f, 0.3f, 1f))) 

						: (urlExists || (isTheRunningTV && urlEmpty) ? new Vector4(0.3f, 0.8f, 0.3f, 1f) : new Vector4(0.8f, 0.3f, 0.3f, 1f));

					if(!isTheRunningTV)
						ImGui.PushStyleColor(ImGuiCol.Border, textColor); // red border
					
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
                    
					ImGui.SetNextItemWidth(235);
					ImGui.InputText("##URL", ref _inputURL, 1000, ImGuiInputTextFlags.None);

					ImGui.PopStyleVar();
					if(!isTheRunningTV)
						ImGui.PopStyleColor();

					// Detect if the input is focused
					if (ImGui.IsItemActive())
						_isFocused = true;
					else if (ImGui.IsItemDeactivated())
						_isFocused = false;

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
							placeholder = placeholder.Substring(0, placeholder.Length - 1);
							textSize = ImGui.CalcTextSize(placeholder + "........");
						}

						if (!placeholder.Equals(_placeHolderURL)) placeholder += "...";

						ImGui.GetWindowDrawList().AddText(new Vector2(pos.X + 3, pos.Y + 2), ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 1.0f)), placeholder);
					}
				}
				else
				{
					if(isTheRunningTV)
						ImGui.TextColored(new Vector4(0.3f, 0.8f, 0.3f, 1.0f), url);
					else if(!urlExists)
                        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Not sharing anything");
                    else
						ImGui.Text(url);
                }

				if (urlExists || isPlayer)
				{
					ImGui.SameLine();

                    ImGui.PushFont(UiBuilder.IconFont);
                    if (ImGui.Button(FontAwesomeIcon.Clipboard.ToIconString() + "##clipboard" + item.EntityId))
                    {
                        ImGui.SetClipboardText(isPlayer ? (string.IsNullOrEmpty(_inputURL) && isTheRunningTV ? _placeHolderURL : _inputURL) : (url ?? String.Empty));
                    }
                    ImGui.PopFont();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text("Copy URL to clipboard");
                        ImGui.EndTooltip();
                    }
                }
			}
			else
			{
				if(isPlayer)
				{
					ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: You have not summoned");
					ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " your standard blue carbuncle.");
				}
            }
        }
    }

	public void TurnOnTV(uint entityId, bool isRefresh)
	{
		var player = Services.Objects.LocalPlayer;
		var isPlayer = entityId == player?.EntityId;
		if (isPlayer)
		{
			if(ValidateURL(out Uri? url) && url != null)
			{
				_core.SetCurrentTV(entityId);
				_pauseToggle = false;
				_lastTVTurnOn = DateTime.Now;

				if (_syncPlayToggle)
				{
					_mvpIsPlaying = false;
					_OTTApi.PushNextVideo();
				}
				else
					_core.PlayVideo(url.ToString());

				ShareTitle(url.ToString());
			}
			else
			{
				return;
			}
		}
		else
		{
			if(_currentURLs.TryGetValue(entityId, out var url))
			{
				_core.SetCurrentTV(entityId);
				_pauseToggle = false;
				_lastTVTurnOn = DateTime.Now;

				/*if("isOTTLink" == "")
					//_plugin.StartMPV(_OTTApiOther._videoURL, _currentSharedTexture);
					TODO: NEUE OTTAPI ALS GAST OEFFNEN
				else*/
					_core.PlayVideo(url);
				
			}
            else
            {
                return;
            }
        }
    }

	public void TurnOffTV()
	{
		_pauseToggle = false;
		_core.StopVideo();
		if (string.IsNullOrEmpty(_inputURL) && !string.IsNullOrEmpty(_placeHolderURL))
		{
			_inputURL = _placeHolderURL;
			_placeHolderURL = string.Empty;
		}
	}
	
	public bool ValidateURL(out Uri? url)
	{
		var formattedUrl =  _inputURL;

		if (!formattedUrl.StartsWith("http://") && !formattedUrl.StartsWith("https://"))
			formattedUrl = "https://" + formattedUrl;

		var result = Uri.TryCreate(formattedUrl, UriKind.Absolute, out url) && (url?.Scheme == Uri.UriSchemeHttp || url?.Scheme == Uri.UriSchemeHttps) && url.Host.Contains('.') && !url.Host.EndsWith('.') && Uri.CheckHostName(url.Host) == UriHostNameType.Dns;

		if (!result)
			return false;

		if (url != null && _syncPlayToggle && !url!.Host.EndsWith(".opentogethertube.com", StringComparison.OrdinalIgnoreCase) && !string.Equals(url!.Host, "opentogethertube.com", StringComparison.OrdinalIgnoreCase))
		{
            _OTTApi.CheckURL(formattedUrl);

			result &= _OTTApi.LastCheckSuccessful;

			formattedUrl = _OTTApi.GetRoomURL;

            result &= Uri.TryCreate(formattedUrl, UriKind.Absolute, out url) && (url?.Scheme == Uri.UriSchemeHttp || url?.Scheme == Uri.UriSchemeHttps) && url.Host.Contains('.') && !url.Host.EndsWith('.') && Uri.CheckHostName(url.Host) == UriHostNameType.Dns;
        }

		return result;
    }

	private long _lastMilliSecond = 0;
	private long _lastMilliSecond144fps = 0;
	public void Refresh()
	{
		if (_lastMilliSecond144fps + 6 < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
			_lastMilliSecond144fps = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			RefreshPlayer();
		}
		if (_lastMilliSecond + 1000 < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
			_lastMilliSecond = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			ProcessURLShare();

			_core.ScanTVs();

			_playerList = Services.Objects.Where(x => x is IPlayerCharacter).OrderBy(x => (x.EntityId == Services.Objects.LocalPlayer?.EntityId) ? "@" : x.Name.TextValue);
        }
	}

	private void VolumePlayer(float volume)
	{
		var vol = (int)((float)Math.Sqrt(volume) * 10f); //Quadratic Slider Valuess
		Services.Log.Debug("Setting volume to " + vol + "%");
		_plugin.VolumePlayer(vol);
	}
	private void SeekPlayer(double percentage)
	{
		int seconds = (int)(_seekerMaxSeconds * (percentage/100));
		Services.Log.Debug("Seeking to " + seconds + " seconds");
		if(_syncPlayToggle)
			_OTTApi.Seek(seconds);
		else
			_plugin.SeekPlayer(seconds);
	}
    private void RefreshPlayer()
    {
		if(!_core.IsTVTurnedOff())
		{
			var info = _plugin.GetPlayerInfos();
			var title = _plugin.GetMediaTitle();

			_mediaTitle = title;

			double time = info[0];
			
			if(!_mvpIsPlaying && time > 0)
			{
				_mvpIsPlaying = true;

				if(_syncPlayToggle && _core.IsPlayerTVOn())
					_OTTApi.PlayPauseVideo(true);
			}

			_seekerTimeMinutes = (int)(time / 60);
			_seekerTimeSeconds = (int)(time % 60);
			double duration = info[1];
			if(duration > 0){
				_seekerMaxSeconds = (int) duration;
				_seekerDurationMinutes = (int)(duration / 60);
				_seekerDurationSeconds = (int)(duration % 60);
			}

			if(!_UIElementActive)
			{
				if(duration > 0)
				_seeker = (float) (duration > 0 ? time / duration * 100 : 100);

				double volume = info[2];
				_volume =  (float) volume / 100f * ((float)volume / 100f) * 100f; //Quadratic Slider Values
			}
		}
		_pauseToggle = _plugin.GetPaused();
		_mpvIsIdle = _plugin.IsIdle() ?? true;
    }



	public async Task ShortenURL(string inputURL, URLShortenerCallback callback, URLShortenerErrorCallback error)
	{
        try
        {
            HttpResponseMessage response = await Plugin.HTTPCLIENT.GetAsync("https://is.gd/create.php?format=simple&url=" + Uri.EscapeDataString(inputURL));
            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();

            if (responseBody.Contains("error") || responseBody.Contains("Error"))
            {
                error(responseBody);
            }
            else
            {
                callback(responseBody);
            }
        }
        catch (Exception e)
        {
			error(e.Message);
        }
    }

    public void OTTReceiveNewVideo() //Receive new Video from OTT
    {
		if (!_core.IsTVTurnedOff())
		{
			_core.PlayVideo(_OTTApi._videoURL);
		}
    }

    public void OTTReceiveSeek(double playbackPosition)
    {
		_plugin.SeekPlayer((int) playbackPosition);
    }

    public void OTTReceivePlayPause(bool playpause, double playbackPosition)
    {
		if (!_core.IsTVTurnedOff()) //TV is running
		{
			if(playpause == _pauseToggle)
			{
				_plugin.TogglePause();
				_pauseToggle = !_pauseToggle;
				if(playbackPosition > 0)
				_plugin.SeekPlayer((int) playbackPosition);
			}
		}
    }

	private float _scrollOffset = 0f;
	private float _pauseTimer = 0f;
	private int _phase = 0; // 0 = Pause Anfang, 1 = scrollen, 2 = Pause Ende
	private string? _lastText;
	private double _lastTime = ImGui.GetTime();
    private bool _ignoreError1;
    private bool _ignoreError2;

    private void DrawScrollingText(string text, float maxWidth)
	{
		var textSize = ImGui.CalcTextSize(text);

		if (textSize.X <= maxWidth)
		{
			ImGui.Text(text);
			return;
		}

		// Reset bei neuem Text
		if (text != _lastText)
		{
			_lastText = text;
			_scrollOffset = 0f;
			_pauseTimer = 0f;
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
			case 0: // Pause am Anfang
				_scrollOffset = 0f;
				_pauseTimer += dt;
				if (_pauseTimer >= pauseDuration)
				{
					_phase = 1;
					_pauseTimer = 0f;
				}
				break;

			case 1: // scrollen
				_scrollOffset += dt * scrollSpeed;
				if (_scrollOffset >= maxScroll)
				{
					_scrollOffset = maxScroll;
					_phase = 2;
				}
				break;

			case 2: // Pause am Ende
				_scrollOffset = maxScroll;
				_pauseTimer += dt;
				if (_pauseTimer >= pauseDuration)
				{
					_phase = 0;
					_pauseTimer = 0f;
					_scrollOffset = 0f;
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
	public void ProcessURLShare()
	{
		if (_signalShareTitle &&  _shareURLToggle)
		{
			Services.CommandManager.ProcessCommand("/honorific force set alpha:" + _shortenedURL + "|silent");
			_sharingTitle = true;
			_signalShareTitle = false;
		}
		else if (_core.IsTVTurnedOff() && _sharingTitle)
		{
			Services.CommandManager?.ProcessCommand("/honorific force clear");
			_sharingTitle = false;
		}
	}

    private async void ShareTitle(string url)
	{
		if (url == _lastURL)
		{
			_signalShareTitle = true;
		}
		else
		{
			await ShortenURL(url, result =>
			{
				_lastURL = url;
				_shortenedURL = result.Split("/").Last();
				_signalShareTitle = true; //Shortened URL will be updated on main thread
				
			}, error => {
				Services.Log.Debug("Request exception: Could not create Shortlink: " + error);
                TurnOffTV(); //TODO: Proper Error Handling!
			});
		}
	}

	public async void UpdateTitle(uint entityId, string title)
	{
		if (entityId == Services.Objects.LocalPlayer?.EntityId) return;
		if (!_currentTitles.TryGetValue(entityId, out var oldTitle) || oldTitle != title)
		{
			_currentTitles[entityId] = title;

			if (title.Length < 7 || !title.StartsWith("alpha:"))
			{
				if(_currentURLs.TryGetValue(entityId, out _))
				{
					_currentURLs.Remove(entityId);
				}
					
				if (_core.IsEntityTVOn(entityId))
					TurnOffTV();
			}
			else
			{
				var url = "https://is.gd/" + title.Substring("alpha:".Length);
				await FetchURLData(url, response => {
					Services.Log.Debug("New URL: " + url);
					_currentURLs[entityId] = response;
					if (_core.IsEntityTVOn(entityId))
						/*if("ISOTTAPILINK" == "")
						_plugin.StartMPV(_OTTApiOther._videoURL, _currentSharedTexture);
						else*/
						_core.PlayVideo(url);
				});
			}
		}
	}
    private async Task FetchURLData(string url, URLFetchCallback callback)
	{
        try
        {
            HttpResponseMessage response = await Plugin.NOREDIRECTHTTPCLIENT.GetAsync(url);

            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                // Get the Location header value
                if (response.Headers.Location != null)
                {
                    callback(response.Headers.Location.ToString());
                    return;
                }
            }
            Services.Log.Debug("Request exception: Shortlink returned 200-OK instead of a 300-redirect, which should not happen with Shortlinks");
        }
        catch (HttpRequestException e)
        {
            Services.Log.Debug("Request exception: " + e.Message + e.StackTrace);
        }
	}
}