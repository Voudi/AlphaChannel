using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;

namespace AlphaChannel;

internal sealed class InputManager
{
	private readonly Plugin _plugin;

	internal Dictionary<Snes9xInput, string> SnesKeyMap { get; } = [];
	private readonly Dictionary<VirtualKey, bool> _heldState = [];
	private readonly Dictionary<Snes9xInput, bool> _lastSentCoopState = [];

	internal enum GamePadSticks
	{
		LeftStickUp    = 0x10000,
		LeftStickDown  = 0x20000,
		LeftStickLeft  = 0x40000,
		LeftStickRight = 0x80000,
		RightStickUp    = 0x100000,
		RightStickDown  = 0x200000,
		RightStickLeft  = 0x400000,
		RightStickRight = 0x800000
	}

	internal InputManager(Plugin plugin)
	{
		_plugin = plugin;

		List<Snes9xInput> keyOrder = [Snes9xInput.UP, Snes9xInput.DOWN, Snes9xInput.LEFT, Snes9xInput.RIGHT, Snes9xInput.A, Snes9xInput.B, Snes9xInput.X, Snes9xInput.Y, Snes9xInput.L, Snes9xInput.R, Snes9xInput.START, Snes9xInput.SELECT];
		foreach (Snes9xInput key in keyOrder)
		{
			SnesKeyMap.Add(key, plugin.Config.KeyMappings.TryGetValue(key, out string? vk) ? (vk ?? VirtualKey.NO_KEY.ToString()) : VirtualKey.NO_KEY.ToString());
		}
	}

	internal bool IsSnesKeyMappable(VirtualKey vk)
	{
		return (vk >= VirtualKey.KEY_0 && vk <= VirtualKey.KEY_9)
			|| (vk >= VirtualKey.A && vk <= VirtualKey.Z)
			|| (vk >= VirtualKey.NUMPAD0 && vk <= VirtualKey.DIVIDE)
			|| (vk >= VirtualKey.F1 && vk <= VirtualKey.F12)
			|| vk == VirtualKey.SPACE
			|| (vk >= VirtualKey.LEFT && vk <= VirtualKey.DOWN);
	}

	internal List<int> GetAllGamePadButtons()
	{
		var buttons = Enum.GetValues<GamepadButtons>().Select(b => (int)b).ToList();
		buttons.AddRange(Enum.GetValues<GamePadSticks>().Select(b => (int)b));
		return buttons;
	}

	internal string GetGamePadButtonName(int gamePadButton)
	{
		if (gamePadButton < 0x10000)
		{
			return Enum.GetName((GamepadButtons)(ushort)gamePadButton) ?? VirtualKey.NO_KEY.ToString();
		}
		return Enum.GetName((GamePadSticks)gamePadButton) ?? VirtualKey.NO_KEY.ToString();
	}

	private int GetGamePadButtonId(string name)
	{
		if (Enum.TryParse(name, out GamePadSticks stick)) { return (int)stick; }
		if (Enum.TryParse(name, out GamepadButtons button)) { return (int)button; }
		return (int)GamepadButtons.None;
	}

	internal bool IsGamePadButtonPressed(int gamePadButton)
	{
		if (gamePadButton < 0x10000)
		{
			return Services.GamepadState.Raw((GamepadButtons)(ushort)gamePadButton) != 0;
		}

		bool leftStick = gamePadButton < 0x100000;
		var button = (GamePadSticks)gamePadButton;
		float x = leftStick ? Services.GamepadState.LeftStick.X : Services.GamepadState.RightStick.X;
		float y = leftStick ? Services.GamepadState.LeftStick.Y : Services.GamepadState.RightStick.Y;
		if (x == 0 && y == 0) { return false; }

		float ratio = Math.Min(Math.Abs(x), Math.Abs(y)) / Math.Max(Math.Abs(x), Math.Abs(y));
		bool diagonal = ratio > 0.6;
		return button switch
		{
			GamePadSticks.LeftStickUp    or GamePadSticks.RightStickUp    => y > 0 && (Math.Abs(y) > Math.Abs(x) || diagonal),
			GamePadSticks.LeftStickDown  or GamePadSticks.RightStickDown  => y < 0 && (Math.Abs(y) > Math.Abs(x) || diagonal),
			GamePadSticks.LeftStickLeft  or GamePadSticks.RightStickLeft  => x < 0 && (Math.Abs(x) > Math.Abs(y) || diagonal),
			GamePadSticks.LeftStickRight or GamePadSticks.RightStickRight => x > 0 && (Math.Abs(x) > Math.Abs(y) || diagonal),
			_ => false
		};
	}

	internal bool TryDetectInput(out string keyName)
	{
		foreach (VirtualKey vk in Services.KeyState.GetValidVirtualKeys())
		{
			if (Services.KeyState[vk] && IsSnesKeyMappable(vk))
			{
				keyName = vk.ToString();
				return true;
			}
		}
		foreach (int gamePadButton in GetAllGamePadButtons())
		{
			if (IsGamePadButtonPressed(gamePadButton))
			{
				keyName = GetGamePadButtonName(gamePadButton);
				return true;
			}
		}
		keyName = string.Empty;
		return false;
	}

	internal void AssignKey(Snes9xInput input, string keyName)
	{
		foreach (Snes9xInput existing in SnesKeyMap.Keys)
		{
			if (SnesKeyMap[existing].Equals(keyName, StringComparison.OrdinalIgnoreCase))
			{
				SnesKeyMap[existing] = VirtualKey.NO_KEY.ToString();
				_plugin.Config.KeyMappings[existing] = VirtualKey.NO_KEY.ToString();
				break;
			}
		}
		SnesKeyMap[input] = keyName;
		_plugin.Config.KeyMappings[input] = keyName;
		_plugin.Config.Save();
	}

	internal void OnFrameworkUpdate(bool isPlayingSnes, bool controlsEnabled, Snes9xRenderer? snesRenderer, HashSet<int> keyUpEvents)
	{
		if (!isPlayingSnes || !controlsEnabled)
		{
			return;
		}

		foreach (Snes9xInput key in SnesKeyMap.Keys)
		{
			if (SnesKeyMap.TryGetValue(key, out string? virtualKeyString)
				&& virtualKeyString != null
				&& virtualKeyString != VirtualKey.NO_KEY.ToString()
				&& Enum.TryParse(virtualKeyString, out VirtualKey virtualKey)
				&& IsSnesKeyMappable(virtualKey))
			{
				bool pressed = Services.KeyState[virtualKey];
				_heldState.TryGetValue(virtualKey, out bool held);
				if (pressed) { held = true; }
				if (keyUpEvents.Contains((int)virtualKey)) { held = false; }
				_heldState[virtualKey] = held;
				snesRenderer?.SetButton(0, (int)key, held);
				if (pressed)
				{
					Services.KeyState[virtualKey] = false;
				}
			}
			else if (SnesKeyMap.TryGetValue(key, out string? gamePadString)
				&& gamePadString != null
				&& gamePadString != VirtualKey.NO_KEY.ToString())
			{
				bool pressed = IsGamePadButtonPressed(GetGamePadButtonId(gamePadString));
				snesRenderer?.SetButton(0, (int)key, pressed);
			}
		}
		snesRenderer?.OnFrameworkUpdate();
	}

	//Mirrors OnFrameworkUpdate's key-hold logic, but for a co-op joiner who isn't running the emulator locally:
	//sends button state to the host over the relay instead of calling SetButton directly, and only on change.
	internal void OnFrameworkUpdateAsCoopJoiner(CoopClient coop, HashSet<int> keyUpEvents)
	{
		foreach (Snes9xInput key in SnesKeyMap.Keys)
		{
			bool pressedNow;
			if (SnesKeyMap.TryGetValue(key, out string? virtualKeyString)
				&& virtualKeyString != null
				&& virtualKeyString != VirtualKey.NO_KEY.ToString()
				&& Enum.TryParse(virtualKeyString, out VirtualKey virtualKey)
				&& IsSnesKeyMappable(virtualKey))
			{
				bool pressed = Services.KeyState[virtualKey];
				_heldState.TryGetValue(virtualKey, out bool held);
				if (pressed) { held = true; }
				if (keyUpEvents.Contains((int)virtualKey)) { held = false; }
				_heldState[virtualKey] = held;
				pressedNow = held;
				if (pressed) { Services.KeyState[virtualKey] = false; }
			}
			else if (SnesKeyMap.TryGetValue(key, out string? gamePadString)
				&& gamePadString != null
				&& gamePadString != VirtualKey.NO_KEY.ToString())
			{
				pressedNow = IsGamePadButtonPressed(GetGamePadButtonId(gamePadString));
			}
			else
			{
				continue;
			}

			if (!_lastSentCoopState.TryGetValue(key, out bool lastSent) || lastSent != pressedNow)
			{
				_lastSentCoopState[key] = pressedNow;
				_ = coop.SendInputAsync(1, (int)key, pressedNow);
			}
		}
	}
}
