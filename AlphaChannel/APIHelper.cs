using Dalamud.Game.ClientState.Objects.Types;
using Newtonsoft.Json;

namespace AlphaChannel;

public class APIHelper
{
    private readonly Core _core;
    private readonly Resources _resources;
    private readonly Dictionary<uint, IPCVideoState> _remoteStates = [];

    internal IReadOnlyDictionary<uint, IPCVideoState> RemoteStates => _remoteStates;

    internal event Action<IGameObject, IPCVideoState>? OnNewPlayerSeen;

    internal sealed record IPCVideoState(
        [property: JsonRequired] string State,
        [property: JsonRequired] string Url,
        [property: JsonRequired] int PlaybackPosition,
        [property: JsonRequired] long Timestamp);

    internal APIHelper(Core core, Resources resources)
    {
        _core = core;
        _resources = resources;
    }

    internal string? GetLocalState()
    {
        uint localId = Services.Objects?.LocalPlayer?.EntityId ?? 0;
        if (!_core.TVIsActive(localId))
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
        uint localId = Services.Objects?.LocalPlayer?.EntityId ?? 0;

        IGameObject? player = Services.Objects?.FirstOrDefault(x => x.Address == addr);
        if (player == null)
        {
            return;
        }
        uint playerId = player.EntityId;
        if (playerId == localId || playerId == 0) 
        {
            return;
        }

        if (stateJSON == null)
        {
            _remoteStates.Remove(playerId);
            if (_core.TVIsActive(playerId))
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

        bool foundState = _remoteStates.TryGetValue(playerId, out IPCVideoState? oldState);
        if (oldState?.Timestamp == state.Timestamp) 
        {
            return;
        }

        state = _remoteStates[playerId] = new IPCVideoState(state.State, Uri.UnescapeDataString(state.Url), state.PlaybackPosition, state.Timestamp);

        if (foundState && oldState != null && _core.TVIsActive(playerId))
        {
            if (oldState.Url != state.Url && state.Url != string.Empty)
            {
                switch (state.State)
                {
                    case "playing": _core.PlayVideo(playerId, state.Url, state.PlaybackPosition, false); break;
                    case "paused":  _core.PlayVideo(playerId, state.Url, state.PlaybackPosition, true); break;
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
        uint localId = Services.Objects?.LocalPlayer?.EntityId ?? 0;
        IGameObject? player = Services.Objects?.FirstOrDefault(x => x.Address == addr);
        uint playerId = player?.EntityId ?? 0;

        if (playerId != localId && playerId != 0)
        {
            _remoteStates.Remove(playerId);
            if (_core.TVIsActive(playerId))
            {
                _core.StopVideoSilent();
            }
        }
    }

    // Called by Core when local player's video starts
    internal void OnVideoStarted(string url, int position, bool isPlaying)
    {
        string stateStr = isPlaying ? "playing" : "paused";
        string json = JsonConvert.SerializeObject(new IPCVideoState(stateStr, Uri.EscapeDataString(url), position, _resources.CurrentTimeNTPNormalizedMilliseconds));
        ApiProvider.NotifyStateChange(json, json);
    }

    // Called by Core when local player's video stops
    internal void OnVideoStopped()
    {
        ApiProvider.NotifyStateChange(null, null);
    }

    // Called by Core when local player pauses/resumes
    internal void OnPaused(bool paused)
    {
        string? url = _core.GetCurrentUrl();
        if (string.IsNullOrEmpty(url)) { return; }

        int pos = (int)_core.GetInfo()[0];
        string json = JsonConvert.SerializeObject(new IPCVideoState(paused ? "paused" : "playing", Uri.EscapeDataString(url), pos, _resources.CurrentTimeNTPNormalizedMilliseconds));
        ApiProvider.NotifyStateChange(json, json);
    }

    // Called by Core when local player seeks (position is the seek target, not yet reflected in GetInfo)
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
    }

    internal void OnIdleReached()
    {
        string? url = _core.GetCurrentUrl();
        int pos = (int)_core.GetInfo()[0];
        string json = JsonConvert.SerializeObject(new IPCVideoState("paused", url != null ? Uri.EscapeDataString(url) : string.Empty, pos, _resources.CurrentTimeNTPNormalizedMilliseconds));
        ApiProvider.NotifyStateChange(json, json);
    }

    
}
