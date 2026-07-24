using Dalamud.Plugin.Ipc;

namespace AlphaChannel;

public static class ApiProvider
{
    private const int ApiVersionMajor = 1;
    private const int ApiVersionMinor = 0;

    private static ICallGateProvider<(int, int)>? _version;

    private static ICallGateProvider<string?>? _getState;
    private static ICallGateProvider<nint, string, object?>? _setState;
    private static ICallGateProvider<nint, object?>? _clearState;
    private static ICallGateProvider<string?, object?>? _stateChange;

    private static ICallGateProvider<object?>? _onReady;
    private static ICallGateProvider<object?>? _onDispose;


    public static void Init(APIHelper helper)
    {
        _version          = Services.PluginInterface.GetIpcProvider<(int, int)>("AlphaChannel.Version");
        _getState         = Services.PluginInterface.GetIpcProvider<string?>("AlphaChannel.GetState");
        _setState         = Services.PluginInterface.GetIpcProvider<nint, string, object?>("AlphaChannel.SetState");
        _clearState       = Services.PluginInterface.GetIpcProvider<nint, object?>("AlphaChannel.ClearState");

        _stateChange      = Services.PluginInterface.GetIpcProvider<string?, object?>("AlphaChannel.StateChange");

        _onReady          = Services.PluginInterface.GetIpcProvider<object?>("AlphaChannel.OnReady");
        _onDispose        = Services.PluginInterface.GetIpcProvider<object?>("AlphaChannel.OnDispose");

        _version.RegisterFunc(() => (ApiVersionMajor, ApiVersionMinor));
        _getState.RegisterFunc(helper.GetLocalState);
        _setState.RegisterAction(helper.SetRemoteState);
        _clearState.RegisterAction(helper.ClearRemoteState);

        _onReady.SendMessage();
    }

    public static void NotifyStateChange(string? fullState) //Full and partial states are basically the same in this project due to NTP timestamp usage instead of updating states periodically
        => _stateChange?.SendMessage(fullState);

    public static void DeInit()
    {
        _onDispose?.SendMessage();

        _version?.UnregisterFunc();
        _getState?.UnregisterFunc();
        _setState?.UnregisterAction();
        _clearState?.UnregisterAction();

        _setState = null;
        _clearState = null;
        _stateChange = null;
        _onReady = null;
        _onDispose = null;
    }
}