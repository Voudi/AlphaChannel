using System.Numerics;
using Browsingway;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using ImGuiNET;
using SharpDX.Direct3D11;
using SharpDX.Direct3D;
using static FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkHistory.Delegates;

public class MainWindow : Window, IDisposable
{
	private readonly Dictionary<IntPtr, IntPtr> _currentTVs = [];
	private bool _flagForRedraw = false;
	private Texture2D? _currentSharedTexture;
	private readonly byte[] _blankCanvas = new byte[16777216];
	private Plugin _plugin;
	InlayConfiguration _overlayConfig;

	public unsafe MainWindow(Plugin plugin)
        : base("Pictomatic remote", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {

		SizeConstraints = new WindowSizeConstraints
		{
			MinimumSize = new Vector2(375, 330),
			MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
		};

		this._plugin = plugin;
	}

	public void TurnOnTV()
	{
		InlayConfiguration config = _plugin.InitiazlizePictomaticWindow(inputURL, inputCSS);
		_overlayConfig = config;
		ResetAllTVs(true);
		TVTurnedOn = true;
		
		
	}

	public void TurnOffTV()
	{
		ResetAllTVs(false);
		TVTurnedOn = false;

		_plugin.RemovePictomaticWindow(_overlayConfig);
	}

	private unsafe void ResetAllTVs(bool turnOn)
	{
		fixed (byte* ptr = _blankCanvas)
		{
			foreach (var item in _currentTVs)
			{
				if (turnOn && _overlayConfig is not null)
				{
					ReassignTextureForTV(item.Key);
				}
				else
				{
					var TV = (CharacterBase*)item.Key;
					TV->Models[0]->Materials[1]->Textures[3].Texture->Texture->InitializeContents((void*)ptr);
				}
			}
		}
	}

	private unsafe IntPtr ResetTV(IntPtr TVaddr)
	{
		fixed (byte* ptr = _blankCanvas)
		{
			var TV = (CharacterBase*)TVaddr;
			TV->Models[0]->Materials[1]->Textures[3].Texture->Texture->InitializeContents((void*)ptr);
			return (IntPtr) ptr;
		}
	}

	public void Dispose() {
	}

	private String buttonToggleTV => TVTurnedOn ? ">TURN OFF<" : ">TURN ON<";
	private String buttonReload = "Reload";
	bool TVTurnedOn = false;
	private String inputURL = "https://w2g.tv/en/room/?room_id=6cf6i1hlqwlv05y83n&w2g_init=1&w2g_nick=Guest";// "about:blank";
	private String inputCSS = "#video_container";//"#divid";
	float volume = 0.5f;
	public unsafe override void Draw()
	{
		ImGui.Text("");

		if (ImGui.Button(buttonToggleTV))
		{
			if (!TVTurnedOn)
			{
				TurnOnTV();
			}
			else
			{
				TurnOffTV();
			}
		}

		ImGui.Text("");
		/*
		ImGui.Text("  -");
		ImGui.SameLine();
		ImGui.SliderFloat("##Volume", ref volume, 0.0f, 1.0f, "Volume");
		ImGui.SameLine();
		ImGui.Text("+");
		ImGui.Text("");
		*/
		ImGui.Text("Connection Settings:");
		if (TVTurnedOn)
		{
			ImGui.BeginDisabled();
		}
		ImGui.Text("  URL  ");
		ImGui.SameLine();
		ImGui.InputText("##URL", ref inputURL, 1000, ImGuiInputTextFlags.NoHorizontalScroll);
		ImGui.Text("  CSS  ");
		ImGui.SameLine();
		ImGui.InputText("##CSS", ref inputCSS, 1000, ImGuiInputTextFlags.NoHorizontalScroll);
		if (TVTurnedOn)
		{
			ImGui.EndDisabled();
		}

		/*
		ImGui.Text("");
		ImGui.Text("Debug info: ");
		foreach (var item in _currentTVs)
		{
			ImGui.Text(item.Key.ToString() + " - " + item.Value.ToString());
		}
		*/
	}

	private long _lastMilliSecond = 0;
	public void RefreshTV()
	{
		//Check for Texture Updates once per sec
		if (_lastMilliSecond + 500 < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
		{
			CheckAllTVs();
			_lastMilliSecond = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}
		
		
	}

	private unsafe void CheckAllTVs()
	{
		if (_flagForRedraw)
		{
			foreach (var item in _currentTVs)
			{
				if (item.Key == item.Value)
					ReassignTextureForTV(item.Key);
			}
			_flagForRedraw = false;
		}

		var pointers = Services.ObjectTable;
		
		List<IntPtr> visitedTvs = new List<IntPtr>();
		var npcList = pointers.Where(x => x is INpc).Cast<INpc>().OrderBy(x => x.YalmDistanceX);
		foreach(var item in npcList)
		{
			if(item.Name.TextValue == "Plush Cushion")
			{
				var character = (Companion*)item.Address;
				if (character != null)
				{
					if (character->DrawObject->GetObjectType() == ObjectType.CharacterBase)
					{
						var tvDraw = (CharacterBase*)character->DrawObject;
						if (tvDraw->Models[0]->Materials[1] is not null)
							if (tvDraw->Models[0]->Materials[1]->TextureCount >= 4)
							{
								visitedTvs.Add((IntPtr)tvDraw);
								CheckTV(tvDraw);
							}
					}
				}
			}
		}

		
		//Remove unvisited TVs
		_currentTVs.Where(x => !visitedTvs.Contains(x.Key)).Select(x => x.Key).ToList().ForEach(x=>_currentTVs.Remove(x));

		//No TVs have been visited, turn off pictomatic
		if (visitedTvs.Count == 0 && TVTurnedOn)
			TurnOffTV();
	}

	private unsafe void CheckTV(CharacterBase* tvDraw)
	{
		IntPtr txtPtrNew = (IntPtr)tvDraw->Models[0]->Materials[1]->Textures[3].Texture->Texture->D3D11Texture2D;
		IntPtr ptr = (IntPtr)tvDraw;
		IntPtr txtPtrOld;
		if(_currentTVs.TryGetValue(ptr, out txtPtrOld))
		{
			if(txtPtrOld != txtPtrNew)
			{
				_currentTVs[ptr] = ReassignTextureForTV(ptr);
			}
		}
		else
		{
			_currentTVs.Add(ptr, ReassignTextureForTV(ptr));
		}
	}

	private unsafe IntPtr ReassignTextureForTV(IntPtr TVaddr)
	{
		var TV = (CharacterBase*)TVaddr;

		//TV is off or theres no shared texture to assign
		if(!TVTurnedOn || _currentSharedTexture is null)
			return ResetTV(TVaddr);

		var _textureSource = _currentSharedTexture;
		
		ShaderResourceView view = new(DxHandler.Device, _textureSource, new ShaderResourceViewDescription { Format = _textureSource.Description.Format, Dimension = ShaderResourceViewDimension.Texture2D, Texture2D = { MipLevels = _textureSource.Description.MipLevels } });

		// Obtain the native pointers
		void* D3D11Texture2D = (void*)_textureSource.NativePointer;
		TV->Models[0]->Materials[1]->Textures[3].Texture->Texture->D3D11Texture2D = D3D11Texture2D;
		void* D3D11ShaderResourceView = (void*)view.NativePointer;
		TV->Models[0]->Materials[1]->Textures[3].Texture->Texture->D3D11ShaderResourceView = D3D11ShaderResourceView;

		return (IntPtr)D3D11Texture2D;
	}

	public unsafe void UpdateSharedDXTexture(Texture2D _textureSource, InlayConfiguration config)
	{
		if(config.Name != "pictomatic")
		{
			return;
		}
		_currentSharedTexture = _textureSource;
		_flagForRedraw = true;
	}
}

