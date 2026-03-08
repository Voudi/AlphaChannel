#if WINDOWS
using SharpDX.DXGI;
using System;
using System.Linq;
using D3D11 = SharpDX.Direct3D11;
using D2D1 = SharpDX.Direct2D1;
using SharpDX.Direct2D1;
using Windows.Devices.HumanInterfaceDevice;

internal static class DxHandler
{
	public static D3D11.Device Device { get; private set; }
    public static D2D1.Device Device2D { get; private set; }
    public static D2D1.DeviceContext Device2DContext { get; private set; }

    public static bool Initialise(long adapterLuid)
	{
		// Find the adapter matching the luid from the parent process
		SharpDX.DXGI.Factory1 factory = new SharpDX.DXGI.Factory1();
		Adapter gameAdapter = null;
		foreach (Adapter adapter in factory.Adapters)
		{
			if (adapter == null)
				continue;
			if (adapter.Description.Luid == adapterLuid)
			{
				gameAdapter = adapter;
				break;
			}
		}

		if (gameAdapter == null)
		{
			string foundLuids = string.Join(",", factory.Adapters.Select(adapter => adapter.Description.Luid));
			Console.Error.WriteLine($"FATAL: Could not find adapter matching game adapter LUID {adapterLuid}. Found: {foundLuids}.");
			return false;
		}

		// Use the adapter to build the device we'll use
		D3D11.DeviceCreationFlags flags = D3D11.DeviceCreationFlags.BgraSupport;

		Device = new D3D11.Device(gameAdapter, flags);

		// Get DXGI Device
		using (var dxgiDevice = Device.QueryInterface<SharpDX.DXGI.Device>())
        {
            var d2dFactory = new D2D1.Factory1(FactoryType.MultiThreaded);

            var d2dDevice = new D2D1.Device(d2dFactory, dxgiDevice);

            var d2dContext = new DeviceContext(d2dDevice, DeviceContextOptions.None);

            Device2D = d2dDevice;
			Device2DContext = d2dContext;
        }

        return true;
	}

	public static void Shutdown()
	{
		Device?.Dispose();
	}
}
#endif