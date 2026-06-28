using Penumbra.Api.IpcSubscribers;
using Penumbra.Api.Enums;

namespace AlphaChannel;

public static class PenumbraIPC
{
    private const string Tag = "AlphaChannelTemporaryMod";

    private static Guid _collectionId = Guid.Empty;
    private static readonly List<string> _keys = [];

    public static bool CheckTempMod(string key)
    {
        return _collectionId != Guid.Empty && _keys.Contains(key);
    }

    public static void ApplyTempMod(string key, ushort? actorIndex, Dictionary<string, string> gamePaths)
    {
        if(actorIndex == null)
        {
            return;
        }
        else if(!_keys.Contains(key))
        {
            if (_collectionId == Guid.Empty)
            {
                var createCollection = new CreateTemporaryCollection(Services.PluginInterface);
                createCollection.Invoke(Tag, Tag, out _collectionId);
            }

            
            var addMod = new AddTemporaryMod(Services.PluginInterface);
            addMod.Invoke(Tag + key, _collectionId, gamePaths, string.Empty, int.MaxValue);

            var assign = new AssignTemporaryCollection(Services.PluginInterface);
            assign.Invoke(_collectionId, (int)actorIndex, true);

            _keys.Add(key);

            Services.Log.Debug("Assigned Temp Mod " + key + " to collection " + _collectionId);
        }
    }

    public static void RemoveTempMod(string key)
    {
        if (_collectionId != Guid.Empty && _keys.Contains(key))
        {
            var assign = new RemoveTemporaryMod(Services.PluginInterface);
            assign.Invoke(Tag + key, _collectionId, int.MaxValue);
            _keys.Remove(key);
            Services.Log.Debug("Removed Temp Mod " + key);
        }
    }

    public static void Redraw(ushort gameObjectIndex)
    {
        var redraw = new RedrawObject(Services.PluginInterface);
        redraw.Invoke(gameObjectIndex, RedrawType.Redraw);
    }

    public static void Dispose()
    {
        if (Services.PluginInterface == null || _collectionId == Guid.Empty) {return;}

        foreach(string key in _keys.ToList())
        {
            RemoveTempMod(key);
        }
        var removeCollection = new DeleteTemporaryCollection(Services.PluginInterface);
        removeCollection.Invoke(_collectionId);

        _collectionId = Guid.Empty;
        _keys.Clear();
    }
}