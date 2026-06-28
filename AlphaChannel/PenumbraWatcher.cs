using System.Collections.Concurrent;
using System.Runtime.Serialization;
using Dalamud.Plugin;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace AlphaChannel;

public sealed class PenumbraWatcher : IDisposable
{
    public const string WatchFor = "chara/monster/m7002/obj/body/b0001/texture/tv_d.tex";
    private readonly EventSubscriber<nint, int> _redrawn;
    private readonly ConcurrentQueue<(int idx, long dueMs)> _pending = new();
    private readonly GetGameObjectResourcePaths _resourcePaths;

    public PenumbraWatcher()
    {
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
				Dictionary<string, HashSet<string>>? paths = res.Length > 0 ? res[0] : null;
                if (paths == null) { continue; }

                foreach ((string? local, HashSet<string>? games) in paths)
                {
                    if (games.Any(g => !string.Equals(g, local, StringComparison.OrdinalIgnoreCase) && string.Equals(g, WatchFor, StringComparison.OrdinalIgnoreCase)))
                    {
                        Services.Log.Debug("Detected file load " + local);
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