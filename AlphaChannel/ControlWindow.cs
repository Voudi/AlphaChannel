using System.Numerics;
using Dalamud.Bindings.ImGui;

using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace AlphaChannel;

public class ControlWindow : Window, IDisposable
{
	private bool _modenabled;
	private bool _installWarningMessage;

	private bool _pauseToggle;

	//Render Vars
	private string _inputURL = "";
	private float _volume = 25;
	private float _seeker;
	private int _seekerTimeSeconds;
	private int _seekerTimeMinutes;
	private int _seekerDurationSeconds;
	private int _seekerDurationMinutes;
	private int _seekerMaxSeconds;
	private bool _libsLoaded;
	private bool _updatingLibs;
	private bool _uiElementActive;
	private string _mediaTitle = string.Empty;
	private bool _mpvIsIdle = true;
	private DateTime _lastTVTurnOn = DateTime.MinValue;
	private Plugin _plugin;
	private Compatibility _compat;
	private Core _core;

	public delegate void URLShortenerCallback(Uri result);
	public delegate void URLFetchCallback(string result);
	public delegate void URLShortenerErrorCallback(string result);
	private readonly Dictionary<uint, string> _currentURLs = []; //Playerpointer, URL

	public ControlWindow(Plugin plugin, string title)
		: base(title, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
	{
		_plugin = plugin;

		_compat = new Compatibility(_plugin);

		_core = new Core(_plugin);
		_core.VideoEnded += TurnOffTV;

		SizeConstraints = new WindowSizeConstraints
		{
			MinimumSize = new Vector2(275, 235),
			MaximumSize = new Vector2(275, 1080)
		};

		_ = _compat.CheckTVMod();

		_compat.CheckForUpdates();
	}

	public void Dispose()
	{
		_core.VideoEnded -= TurnOffTV;
		_core.Dispose();
		GC.SuppressFinalize(this);
	}

	private bool _isFocused;
	private string _placeHolderURL = string.Empty;
	private IEnumerable<IGameObject> _playerList = [];
	public override void Draw()
	{
		Vector4 textColor;
		bool playerIsRunningTV = _core.IsPlayerTVOn();

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
		if (!_core.IsTVTurnedOff() && !_core.TextureExists() && _lastTVTurnOn.AddSeconds(5) < DateTime.Now)
		{
			ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " Error: Cannot Fetch Screen Texture");
			ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " 1. Keep the plugin activated");
			ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " 2. Restart the game client, or");
			ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1.0f), " 3. Teleport to another zone");
		}
		if (!_compat.ModExists && !_ignoreError1)
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
		if (!_modenabled && _compat.InstalledMod && !_ignoreError2)
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
			bool isPlayer = item.EntityId == Services.Objects.LocalPlayer?.EntityId;

			if (isPlayer && _installWarningMessage && _modenabled)
			{
				ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " Please enable the AlphaChannelTV");
				ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " Penumbra mod and make sure");
				ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.3f, 1.0f), " it has the highest priority.");
			}
			if ((isPlayer && _installWarningMessage) || _core.TVExistsForEntity(item.EntityId)) //Checks if players carbuncle exists OR other players TV exists
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
					if (_currentURLs.TryGetValue(item.EntityId, out string? tempURL))
					{
						url = tempURL;
						urlExists = true;
					}
				}

				ImGui.Text(isPlayer ? "YOU" : " " + item.Name.TextValue);

				ImGui.SameLine();


				if (isTheRunningTV)
				{
					textColor = isPlayer ? new Vector4(1.0f, 0.0f, 0.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f);
					;
					ImGui.PushStyleColor(ImGuiCol.Text, textColor);
				}
				else if (isPlayer && _installWarningMessage)
				{
					ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.0f, 1.0f, 0.0f, 1.0f));
				}
				else if (!urlExists)
				{
					ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1.0f));
				}
				ImGui.PushFont(UiBuilder.IconFont);

				bool refreshNeeded = isTheRunningTV && !string.IsNullOrEmpty(_inputURL) && isPlayer && urlExists;

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
							if (urlExists)
							{
								TurnOnTV(item.EntityId);
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

				if (isTheRunningTV || !urlExists || (isPlayer && _installWarningMessage))
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
							_core.PlayVideo(_placeHolderURL);
						}
						else
						{
							_core.TogglePause();
							_pauseToggle = !_pauseToggle;
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

				if (isTheRunningTV)
				{
					DrawScrollingText(_mediaTitle, 250);
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

				if (isPlayer)
				{
					textColor = (urlExists || (isTheRunningTV && urlEmpty) ? new Vector4(0.3f, 0.8f, 0.3f, 1f) : new Vector4(0.8f, 0.3f, 0.3f, 1f));

					if (!isTheRunningTV)
					{
						ImGui.PushStyleColor(ImGuiCol.Border, textColor); // red border
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

				if (!_playerList.Last().Equals(item))
				{
					ImGui.Separator();
				}
			}
			else
			{
				if (isPlayer)
				{
					ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " Notice: You have not summoned");
					ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), " your standard blue carbuncle.");
				}
			}
		}
	}

	public void TurnOnTV(uint entityId)
	{
		if (Services.Objects.LocalPlayer?.EntityId == entityId)
		{
			if (ValidateURL(out Uri? uri) && uri != null)
			{
				_core.SetCurrentTV(entityId);
				_lastTVTurnOn = DateTime.Now;

				_core.PlayVideo(uri.ToString());
			}
		}
		else
		{
			if (_currentURLs.TryGetValue(entityId, out string? url))
			{
				_core.SetCurrentTV(entityId);
				_lastTVTurnOn = DateTime.Now;

				bool result = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri?.Scheme == Uri.UriSchemeHttp || uri?.Scheme == Uri.UriSchemeHttps) && uri.Host.Contains('.') && !uri.Host.EndsWith('.') && Uri.CheckHostName(uri.Host) == UriHostNameType.Dns;

				if (!result)
				{
					Services.Log.Error("Failed fetching URL for player " + entityId);
					return;
				}

				_core.PlayVideo(url);
			}
		}
	}

	public void TurnOffTV()
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

			(_modenabled, _installWarningMessage) = _core.ScanForCompanions();

			_playerList = Services.Objects.Where(x => x is IPlayerCharacter).OrderBy(x => (x.EntityId == Services.Objects.LocalPlayer?.EntityId) ? "@" : x.Name.TextValue);
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
		
		_mpvIsIdle = _core.IsIdle() ?? true;
		_pauseToggle = _core.GetPaused() || _mpvIsIdle;
	}

	private float _scrollOffset;
	private float _pauseTimer;
	private int _phase; // 0 = Pause Anfang, 1 = scrollen, 2 = Pause Ende
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
			case 0: // Pause am Anfang
				_scrollOffset = 0;
				_pauseTimer += dt;
				if (_pauseTimer >= pauseDuration)
				{
					_phase = 1;
					_pauseTimer = 0;
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
}
