using Dalamud.Game.ClientState.Objects.Types;
using Newtonsoft.Json;
using NoireLib;

namespace AlphaChannel;

public class APIHelper
{
    private readonly Plugin _plugin;
    private readonly Core _core;
    private readonly Resources _resources;
    private readonly Dictionary<uint, IPCVideoState> _remoteStates = [];

    internal IReadOnlyDictionary<uint, IPCVideoState> RemoteStates => _remoteStates;

    internal event Action<IGameObject?, IPCVideoState>? OnNewPlayerSeen;

    internal sealed record IPCVideoState(
        [property: JsonRequired] string State,
        [property: JsonRequired] string Url,
        [property: JsonRequired] int PlaybackPosition,
        [property: JsonRequired] long Timestamp);

    internal APIHelper(Plugin plugin, Core core, Resources resources)
    {
        _plugin = plugin;
        _core = core;
        _resources = resources;
    }

    internal string? GetLocalState()
    {
        uint? localPlayerId = Services.LocalPlayerId;
        if(!localPlayerId.HasValue || !_core.TVIsActive(localPlayerId.Value))
        {
            return null;
        }

        string? url = _core.GetCurrentUrl();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        int pos = (int)_core.GetInfo()[0];
        string stateStr = _core.GetPaused() ? "paused" : "playing";

        return JsonConvert.SerializeObject(new IPCVideoState(stateStr, Uri.EscapeDataString(url), pos, _resources.CurrentTimeNTPNormalizedMilliseconds));
    }

    internal void SetRemoteState(nint addr, string stateJSON)
    {
        uint? localPlayerId = Services.LocalPlayerId;
        ICharacter? player = NoireLib.Helpers.CharacterHelper.GetCharacterFromAddress(addr);
        uint? playerId = player?.EntityId;

        if (!playerId.HasValue || playerId == localPlayerId) 
        {
            Services.Log.Debug("No need to set state for local player.");
            return;
        }

        if (stateJSON == null)
        {
            _remoteStates.Remove(playerId.Value);
            if (_core.TVIsActive(playerId.Value))
            {
                _core.StopVideoSilent();
            }
            return;
        }

        IPCVideoState? state = JsonConvert.DeserializeObject<IPCVideoState>(stateJSON);
        if (state == null)
        {
            Services.Log.Error($"Failed to deserialize state for player {playerId} with JSON: {stateJSON}");
            return;
        }

        bool foundState = _remoteStates.TryGetValue(playerId.Value, out IPCVideoState? oldState);
        if (oldState?.Timestamp == state.Timestamp) 
        {
            Services.Log.Debug("Old state equals new state. Aborting.");
            return;
        }

        state = _remoteStates[playerId.Value] = new IPCVideoState(state.State, Uri.UnescapeDataString(state.Url), state.PlaybackPosition, state.Timestamp);

        if (foundState && oldState != null && _core.TVIsActive(playerId.Value))
        {
            if (oldState.Url != state.Url && state.Url != string.Empty)
            {
                switch (state.State)
                {
                    case "playing": _core.PlayVideo(playerId.Value, state.Url, state.PlaybackPosition, false); break;
                    case "paused":  _core.PlayVideo(playerId.Value, state.Url, state.PlaybackPosition, true); break;
                }
            }
            else
            {
                int currentPos = (int)_core.GetInfo()[0];
                if (currentPos + 7 < state.PlaybackPosition || currentPos - 7 > state.PlaybackPosition)
                {
                    _core.SeekSilent(state.PlaybackPosition);
                }

                switch (state.State)
                {
                    case "playing": if (_core.GetPaused()) { _core.PauseSilent(false); } break;
                    case "paused":  if (!_core.GetPaused()) { _core.PauseSilent(true); } break;
                }
            }
        }
        else if (!foundState)
        {
            OnNewPlayerSeen?.Invoke(player, state);
        }
    }

    internal void ClearRemoteState(nint addr)
    {
        uint? localPlayerId = Services.LocalPlayerId;
        ICharacter? player = NoireLib.Helpers.CharacterHelper.GetCharacterFromAddress(addr);
        uint? playerId = player?.EntityId;

        if (playerId.HasValue && playerId != localPlayerId)
        {
            _remoteStates.Remove(playerId.Value);
            if (_core.TVIsActive(playerId.Value))
            {
                _core.StopVideoSilent();
            }
        }
    }
    
    internal void OnVideoStarted(string url, int position, bool isPlaying)
    {
        string stateStr = isPlaying ? "playing" : "paused";
        string json = JsonConvert.SerializeObject(new IPCVideoState(stateStr, Uri.EscapeDataString(url), position, _resources.CurrentTimeNTPNormalizedMilliseconds));

        ApiProvider.NotifyStateChange(json, json);
        _=NotifyStateDebug(json);
    }

    internal void OnVideoStopped()
    {
        ApiProvider.NotifyStateChange(null, null);
        _=NotifyStateDebug(null);
    }

    internal async Task OnPaused(bool paused)
    {
        string? url = _core.GetCurrentUrl();
        if (string.IsNullOrEmpty(url)) { return; }

        int pos = (int)_core.GetInfo()[0];
        string json = JsonConvert.SerializeObject(new IPCVideoState(paused ? "paused" : "playing", Uri.EscapeDataString(url), pos, _resources.CurrentTimeNTPNormalizedMilliseconds));

        ApiProvider.NotifyStateChange(json, json);
        _=NotifyStateDebug(json);
    }

    internal void OnSeeked(int seconds)
    {
        string? url = _core.GetCurrentUrl();
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        string stateStr = _core.GetPaused() ? "paused" : "playing";
        string json = JsonConvert.SerializeObject(new IPCVideoState(stateStr, Uri.EscapeDataString(url), seconds, _resources.CurrentTimeNTPNormalizedMilliseconds));
        ApiProvider.NotifyStateChange(json, json);
        _=NotifyStateDebug(json);
    }

    internal void OnIdleReached()
    {
        string? url = _core.GetCurrentUrl();
        int pos = (int)_core.GetInfo()[0];
        string json = JsonConvert.SerializeObject(new IPCVideoState("paused", url != null ? Uri.EscapeDataString(url) : string.Empty, pos, _resources.CurrentTimeNTPNormalizedMilliseconds));
        ApiProvider.NotifyStateChange(json, json);
        _=NotifyStateDebug(json);
    }

    internal async Task NotifyStateDebug(string? json)
    {
        if(json != null)
        {
            await _plugin.TextureTranslate.EncodeToTexAsync(json, _plugin.PenumbraQRPaths.First(p => p.Key.Equals(PenumbraWatcher.WatchFor, StringComparison.OrdinalIgnoreCase)).Value);
        }
        else
        {
            PenumbraIPC.RemoveTempMod("qr");
            _plugin.LibResources.LoadPenumbraQRResources();
			PenumbraIPC.ApplyTempMod("qr", Services.LocalPlayerIndex, _plugin.PenumbraQRPaths);
        }

        _=NoireService.Framework.RunOnTick(() =>
        {
            if(Services.LocalPlayerId.HasValue)
                {
                    PenumbraIPC.Redraw(_core.GetCompanionIndex(Services.LocalPlayerId.Value));
                }
        });
    }
    
}
