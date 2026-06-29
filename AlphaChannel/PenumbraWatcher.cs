using System.Collections.Concurrent;
using System.Runtime.Serialization;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace AlphaChannel;

public sealed class PenumbraWatcher : IDisposable
{
    public const string WatchFor = "chara/monster/m7002/obj/body/b0001/texture/tv_d.tex";
	private readonly APIHelper _apiHelper;
    private readonly TextureTranslate _textureTranslate;
	private readonly EventSubscriber<nint, int> _redrawn;
    private readonly ConcurrentQueue<(int idx, long dueMs)> _pending = new();
    private readonly GetGameObjectResourcePaths _resourcePaths;

    public PenumbraWatcher(APIHelper apiHelper, TextureTranslate textureTranslate)
    {
        _textureTranslate = textureTranslate;
        _apiHelper = apiHelper;
        _redrawn = GameObjectRedrawn.Subscriber(Services.PluginInterface, OnRedrawn);
        _resourcePaths = new GetGameObjectResourcePaths(Services.PluginInterface);
    }

    private void OnRedrawn(nint objectAddress, int objectTableIndex)
        => _pending.Enqueue((objectTableIndex, Environment.TickCount64 + 500)); // wait half a sec for path to exist

    public void OnFrameworkUpdate()
    {
        long now = Environment.TickCount64;
        int count = _pending.Count;
        for (int i = 0; i < count; i++)
        {
            if (!_pending.TryDequeue(out (int idx, long dueMs) item)) {break;}
            if (now < item.dueMs) { _pending.Enqueue(item); continue; }

            try
            {
				Dictionary<string, HashSet<string>>?[] res = _resourcePaths.Invoke((ushort)item.idx);
				uint companionEntityId = Services.Objects.First(o => o.ObjectIndex == item.idx).EntityId;
                unsafe {
                        uint ownerId = CharacterManager.Instance()->LookupBattleCharaByEntityId(companionEntityId)->CompanionOwnerId;
                        nint addr = Services.Objects.First(o => o.EntityId == ownerId).Address;

                        string playerName = Services.Objects.First(o => o.EntityId == ownerId).Name.TextValue;

                        Dictionary<string, HashSet<string>>? paths = res.Length > 0 ? res[0] : null;
                        if (paths == null) { continue; }

                        foreach ((string? localFile, HashSet<string>? games) in paths)
                        {
                            if (games.Any(g => !string.Equals(g, localFile, StringComparison.OrdinalIgnoreCase) && string.Equals(g, WatchFor, StringComparison.OrdinalIgnoreCase)))
                            {
                                if(Services.LocalPlayerAddr != addr)
                                {
                                    string? state = _textureTranslate.DecodeFromTex(localFile);
                                    if(state != null)
                                    {
                                        _apiHelper.SetRemoteState(addr, state);
                                    }
                                }
                                Services.Log.Debug("Detected file load " + localFile + " for player " + playerName);
                            }
                        }
                    }
            }
            catch (Exception ex) { Services.Log.Error($"[Delayed] EX: {ex}"); }
        }
    }

    public void Dispose()
    {
        _redrawn.Dispose();
    }
}