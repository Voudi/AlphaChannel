using Penumbra.Api.IpcSubscribers;
using Penumbra.Api.Enums;

namespace AlphaChannel;

public static class PenumbraIPC
{
    private const string Tag = "AlphaChannelTemporaryMod";

    private static readonly Dictionary<string, Guid> _collectionIds = [];

    public static bool CheckTempMod(string key)
    {
        return _collectionIds.TryGetValue(key, out _);
    }

    public static void ApplyTempMod(string key, int? actorIndex, Dictionary<string, string> gamePaths)
    {
        if(actorIndex == null)
        {
            return;
        }
        else
        {
            if (!_collectionIds.TryGetValue(key, out Guid colId))
            {
                var createCollection = new CreateTemporaryCollection(Services.PluginInterface);
                createCollection.Invoke(Tag + key, Tag + key, out colId);
                _collectionIds[key] = colId;
            }

            var addMod = new AddTemporaryMod(Services.PluginInterface);
            addMod.Invoke(Tag + key, colId, gamePaths, string.Empty, int.MaxValue);

            var assign = new AssignTemporaryCollection(Services.PluginInterface);
            assign.Invoke(colId, (int)actorIndex, true);
        }
    }

    public static void RemoveTempMod(string key)
    {
        if (_collectionIds.TryGetValue(key, out Guid colId))
        {
            var assign = new RemoveTemporaryMod(Services.PluginInterface);
            assign.Invoke(Tag + key, colId, int.MaxValue);
            _collectionIds.Remove(key);
        }
    }

    public static void Dispose()
    {
        if (Services.PluginInterface == null || _collectionIds.Values.Count == 0) {return;}

        foreach(string key in _collectionIds.Keys)
        {
            var removeMod = new RemoveTemporaryMod(Services.PluginInterface);
            removeMod.Invoke(Tag + key, _collectionIds[key], int.MaxValue);

            var removeCollection = new DeleteTemporaryCollection(Services.PluginInterface);
            removeCollection.Invoke(_collectionIds[key]);
        }

        _collectionIds.Clear();

        Redraw(-1);
    }

    public static void Redraw(int gameObjectIndex)
    {
        if(gameObjectIndex < 0) { 
            var redrawAll = new RedrawAll(Services.PluginInterface);
            redrawAll.Invoke(RedrawType.Redraw);
            Services.Log.Warning("Fallback: Redrawing all actors.");
            return; 
        }
        var redraw = new RedrawObject(Services.PluginInterface);
        redraw.Invoke(gameObjectIndex, RedrawType.Redraw);
    }
}