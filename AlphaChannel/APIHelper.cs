using Dalamud.Game.ClientState.Objects.Types;
using Newtonsoft.Json;
using NoireLib;

namespace AlphaChannel;

public class APIHelper
{
    private readonly Core _core;
    private readonly Resources _resources;

    internal IReadOnlyDictionary<uint, IPCVideoState> RemoteStates => _remoteStates;
	private readonly Dictionary<uint, IPCVideoState> _remoteStates = [];

    internal event Action<IGameObject?, IPCVideoState>? OnNewPlayerSeen;

    internal sealed record IPCVideoState(
        [property: JsonRequired] string State,
        [property: JsonRequired] string Url,
        [property: JsonRequired] int PlaybackPosition,
        [property: JsonRequired] long Timestamp,
        [property: JsonRequired] float ScreenX,
        [property: JsonRequired] float ScreenY,
        [property: JsonRequired] float ScreenZ,
        [property: JsonRequired] float ScreenYaw,
        [property: JsonRequired] float ScreenScale);

    internal APIHelper(Core core, Resources resources)
    {
        _core = core;
        _resources = resources;
    }

    internal string? GetLocalState()
    {
        if(!_core.TVIsActive(Services.LocalPlayerId))
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

        return JsonConvert.SerializeObject(BuildState(stateStr, Uri.EscapeDataString(url), pos));
    }

    private IPCVideoState BuildState(string state, string escapedUrl, int playbackPosition)
    {
        return new IPCVideoState(state, escapedUrl, playbackPosition, _resources.CurrentTimeNTPNormalizedMilliseconds,
            _core.ScreenPosition.X, _core.ScreenPosition.Y, _core.ScreenPosition.Z, _core.ScreenYaw, _core.ScreenScale);
    }

    internal void SetRemoteState(nint addr, string stateJSON)
    {
        ICharacter? player = NoireLib.Helpers.CharacterHelper.GetCharacterFromAddress(addr);
        uint? playerId = player?.EntityId;

        if (!playerId.HasValue || playerId == Services.LocalPlayerId) 
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

        state = _remoteStates[playerId.Value] = new IPCVideoState(state.State, Uri.UnescapeDataString(state.Url), state.PlaybackPosition, state.Timestamp,
            state.ScreenX, state.ScreenY, state.ScreenZ, state.ScreenYaw, state.ScreenScale);

        if (foundState && oldState != null && _core.TVIsActive(playerId.Value))
        {
            _core.ApplyRemoteScreenTransform(new(state.ScreenX, state.ScreenY, state.ScreenZ), state.ScreenYaw, state.ScreenScale);

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
        ICharacter? player = NoireLib.Helpers.CharacterHelper.GetCharacterFromAddress(addr);
        uint? playerId = player?.EntityId;

        if (playerId.HasValue && playerId != Services.LocalPlayerId)
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
        string json = JsonConvert.SerializeObject(BuildState(stateStr, Uri.EscapeDataString(url), position));

        ApiProvider.NotifyStateChange(json);
    }

    internal void OnVideoStopped()
    {
        ApiProvider.NotifyStateChange(null);
    }

    internal async Task OnPaused(bool paused)
    {
        string? url = _core.GetCurrentUrl();
        if (string.IsNullOrEmpty(url)) { return; }

        int pos = (int)_core.GetInfo()[0];
        string json = JsonConvert.SerializeObject(BuildState(paused ? "paused" : "playing", Uri.EscapeDataString(url), pos));

        ApiProvider.NotifyStateChange(json);
    }

    internal void OnSeeked(int seconds)
    {
        string? url = _core.GetCurrentUrl();
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        string stateStr = _core.GetPaused() ? "paused" : "playing";
        string json = JsonConvert.SerializeObject(BuildState(stateStr, Uri.EscapeDataString(url), seconds));
        ApiProvider.NotifyStateChange(json);
    }

    internal void NotifyScreenMoved()
    {
        string? url = _core.GetCurrentUrl();
        if (string.IsNullOrEmpty(url)) { return; }

        int pos = (int)_core.GetInfo()[0];
        string stateStr = _core.GetPaused() ? "paused" : "playing";
        string json = JsonConvert.SerializeObject(BuildState(stateStr, Uri.EscapeDataString(url), pos));

        ApiProvider.NotifyStateChange(json);
    }

    internal void OnIdleReached()
    {
        string? url = _core.GetCurrentUrl();
        int pos = (int)_core.GetInfo()[0];
        string json = JsonConvert.SerializeObject(BuildState("paused", url != null ? Uri.EscapeDataString(url) : string.Empty, pos));
        ApiProvider.NotifyStateChange(json);
    }
}
