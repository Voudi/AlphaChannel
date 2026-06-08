using Dalamud.Plugin.Ipc;

namespace AlphaChannel;

public static class ApiProvider
{
    private const int ApiVersionMajor = 1;
    private const int ApiVersionMinor = 0;

    private static ICallGateProvider<(int, int)>? _version;

    private static ICallGateProvider<string?>? _getState;
    private static ICallGateProvider<nint, string, object?>? _setState;
    private static ICallGateProvider<nint, string, object?>? _applyStateUpdate;
    private static ICallGateProvider<nint, object?>? _clearState;
    private static ICallGateProvider<string?, string?, object?>? _stateChange;

    private static ICallGateProvider<object?>? _onReady;
    private static ICallGateProvider<object?>? _onDispose;


    public static void Init(Plugin plugin)
    {
        _version          = Services.PluginInterface.GetIpcProvider<(int, int)>("AlphaChannel.Version");

        _getState         = Services.PluginInterface.GetIpcProvider<string?>("AlphaChannel.GetState");
        _setState         = Services.PluginInterface.GetIpcProvider<nint, string, object?>("AlphaChannel.SetState");
        _applyStateUpdate = Services.PluginInterface.GetIpcProvider<nint, string, object?>("AlphaChannel.ApplyStateUpdate");
        _clearState       = Services.PluginInterface.GetIpcProvider<nint, object?>("AlphaChannel.ClearState");
        _stateChange      = Services.PluginInterface.GetIpcProvider<string?, string?, object?>("AlphaChannel.StateChange");

        _onReady          = Services.PluginInterface.GetIpcProvider<object?>("AlphaChannel.OnReady");
        _onDispose        = Services.PluginInterface.GetIpcProvider<object?>("AlphaChannel.OnDispose");

        _version.RegisterFunc(() => (ApiVersionMajor, ApiVersionMinor));
        _getState.RegisterFunc(plugin.IPCGetLocalState);
        _setState.RegisterAction(plugin.IPCSetState);
        _applyStateUpdate.RegisterAction(plugin.IPCApplyStateUpdate);
        _clearState.RegisterAction(plugin.IPCClearState);

        _onReady.SendMessage();
    }

    public static void NotifyStateChange(string? fullState, string? partialState = null)
        => _stateChange?.SendMessage(fullState, partialState);

    public static void DeInit()
    {
        _onDispose?.SendMessage();

        _version?.UnregisterFunc();
        _getState?.UnregisterFunc();
        _setState?.UnregisterAction();
        _applyStateUpdate?.UnregisterAction();
        _clearState?.UnregisterAction();

        _setState = null;
        _applyStateUpdate = null;
        _clearState = null;
        _stateChange = null;
        _onReady = null;
        _onDispose = null;
    }
}