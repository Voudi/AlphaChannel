using Penumbra.Api.IpcSubscribers;
using Penumbra.Api.Enums;

namespace AlphaChannel;

public static class PenumbraIPC
{
    private const string Tag = "AlphaChannelTemporaryTVMod";

    private static Guid _collectionId = Guid.Empty;

    public static void ApplyTempMod(int actorIndex, Dictionary<string, string> gamePaths)
    {
        if (_collectionId == Guid.Empty)
        {
            var createCollection = new CreateTemporaryCollection(Services.PluginInterface);
            createCollection.Invoke(Tag, Tag, out _collectionId);
        }

        var addMod = new AddTemporaryMod(Services.PluginInterface);
        addMod.Invoke(Tag, _collectionId, gamePaths, string.Empty, int.MaxValue);

        var assign = new AssignTemporaryCollection(Services.PluginInterface);
        assign.Invoke(_collectionId, actorIndex, true);
    }

    public static void RemoveTempMod()
    {
        if (_collectionId != Guid.Empty)
        {
            var assign = new RemoveTemporaryMod(Services.PluginInterface);
            assign.Invoke(Tag, _collectionId, int.MaxValue);
        }
    }

    public static void Dispose()
    {
        if (Services.PluginInterface == null || _collectionId == Guid.Empty) {return;}

        var removeMod = new RemoveTemporaryMod(Services.PluginInterface);
        removeMod.Invoke(Tag, _collectionId, int.MaxValue);

        var removeCollection = new DeleteTemporaryCollection(Services.PluginInterface);
        removeCollection.Invoke(_collectionId);

        _collectionId = Guid.Empty;

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