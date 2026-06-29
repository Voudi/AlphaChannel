using Penumbra.Api.IpcSubscribers;
using Penumbra.Api.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using NoireLib;

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
                var createCollection = new Penumbra.Api.IpcSubscribers.CreateTemporaryCollection(Services.PluginInterface);
                createCollection.Invoke(Tag, Tag, out _collectionId);
            }

            
            var addMod = new Penumbra.Api.IpcSubscribers.AddTemporaryMod(Services.PluginInterface);
            addMod.Invoke(Tag + key, _collectionId, gamePaths, string.Empty, int.MaxValue);

            var assign = new Penumbra.Api.IpcSubscribers.AssignTemporaryCollection(Services.PluginInterface);
            assign.Invoke(_collectionId, (int)actorIndex, true);

            _keys.Add(key);

            Services.Log.Debug("Assigned Temp Mod " + key + " to collection " + _collectionId);
        }
    }

    public static void RemoveTempMod(string key)
    {
        if (_collectionId != Guid.Empty && _keys.Contains(key))
        {
            var assign = new Penumbra.Api.IpcSubscribers.RemoveTemporaryMod(Services.PluginInterface);
            assign.Invoke(Tag + key, _collectionId, int.MaxValue);
            _keys.Remove(key);
            Services.Log.Debug("Removed Temp Mod " + key);
        }
    }

    public static void Redraw(ushort gameObjectIndex)
    {
        if(gameObjectIndex == ushort.MaxValue)
        {
            _=NoireService.Framework.RunOnTick(() =>
            {
                RedrawAll();
            });
        }
        else
        {
            var redraw = new Penumbra.Api.IpcSubscribers.RedrawObject(Services.PluginInterface);
            redraw.Invoke(gameObjectIndex, RedrawType.Redraw);
        }
    }

    public static void RedrawAll()
    {
        foreach (ushort item in Services.Objects.Where(x => x is IBattleNpc && x.BaseId == 13498 && x.ObjectKind is ObjectKind.BattleNpc).Select(o => o.ObjectIndex))
        {
            var redraw = new Penumbra.Api.IpcSubscribers.RedrawObject(Services.PluginInterface);
            redraw.Invoke(item, RedrawType.Redraw);
            Services.Log.Debug("Redrawing item " + item);
        }
        
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