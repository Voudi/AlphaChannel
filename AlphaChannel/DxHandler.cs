using Dalamud.Plugin;
using SharpDX.DXGI;
using D3D11 = SharpDX.Direct3D11;

namespace AlphaChannel;

internal static class DxHandler
{
	public static D3D11.Device? Device { get; private set; }
	public static D3D11.Device? DrawDevice { get; private set; }
	public static long AdapterLuid { get; private set; }

	public static void Initialise(IDalamudPluginInterface pluginInterface)
	{
		Device = new D3D11.Device(pluginInterface.UiBuilder.DeviceHandle);

		// Get the game's device adapter, we'll need that as a reference for the render process.
		Device? dxgiDevice = Device.QueryInterface<Device>();
		AdapterLuid = dxgiDevice.Adapter.Description.Luid;

		//Create a separate device for the render process, since the one we have here is shared with the game and thus can't be used for rendering without potentially interfering with the game. 
		//This is a bit of a hack, but it works and is simpler than setting up a proper shared device.
		DrawDevice = CreateDrawDevice();
	}

	private static D3D11.Device? CreateDrawDevice()
	{
		// Find the adapter matching the luid from the parent process
		Factory1 factory = new();
		Adapter? gameAdapter = null;
		foreach (Adapter adapter in factory.Adapters)
		{
			if (adapter == null)
			{
				continue;
			}

			if (adapter.Description.Luid == AdapterLuid)
			{
				gameAdapter = adapter;
				break;
			}
		}

		if (gameAdapter == null)
		{
			string foundLuids = string.Join(",", factory.Adapters.Select(adapter => adapter.Description.Luid));
			Console.Error.WriteLine($"FATAL: Could not find adapter matching game adapter LUID {AdapterLuid}. Found: {foundLuids}.");
			return null;
		}

		D3D11.DeviceCreationFlags flags = D3D11.DeviceCreationFlags.BgraSupport;

		return new D3D11.Device(gameAdapter, flags);
	}
	public static void Shutdown()
	{
		Device = null; //Do not dispose this device, as it's owned by the game process.
		DrawDevice?.Dispose();
	}
}
