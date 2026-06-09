using Penumbra.Api.IpcSubscribers;
using Penumbra.Api.Enums;

namespace AlphaChannel;

public static class PenumbraIpc
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

        var addMod = new AddTemporaryModAll(Services.PluginInterface);
        addMod.Invoke(Tag, gamePaths, string.Empty, int.MaxValue);

        var assign = new AssignTemporaryCollection(Services.PluginInterface);
        assign.Invoke(_collectionId, actorIndex, true);
    }

    public static void RemoveTempMod(int actorIndex)
    {
        if (_collectionId != Guid.Empty)
        {
            var assign = new AssignTemporaryCollection(Services.PluginInterface);
            assign.Invoke(Guid.Empty, actorIndex, true);
        }
    }

    public static void Dispose()
    {
        if (Services.PluginInterface == null || _collectionId == Guid.Empty) {return;}

        var removeMod = new RemoveTemporaryModAll(Services.PluginInterface);
        removeMod.Invoke(Tag, int.MaxValue);

        var removeCollection = new DeleteTemporaryCollection(Services.PluginInterface);
        removeCollection.Invoke(_collectionId);

        _collectionId = Guid.Empty;
    }

    public static void Redraw(int gameObjectIndex)
    {
        var redraw = new RedrawObject(Services.PluginInterface);
        redraw.Invoke(gameObjectIndex, RedrawType.Redraw);
    }
}