using Brio.Game.World;
using Dalamud.Game.ClientState.Objects.Types;
using System.Collections.Concurrent;

using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Brio.Entities.Core;

public record struct EntityId(string Unique)
{
    // 動畫 hook 每幀對每個角色都要由位址查實體（沒進 GPose 也是），所以「位址→鍵字串」不能每次組。
    // 位址只當數字用、從不解參；hook 不保證都在主執行緒，所以用並行字典。
    private static readonly ConcurrentDictionary<nint, string> ActorKeys = new();
    private const int MaxActorKeys = 4096;

    public EntityId(IGameObject go) : this($"actor_{go.Address}")
    {
    }
    public EntityId(CameraId id) : this($"camera_{id}")
    {
    }
    public EntityId(IGameLight id) : this($"light_{id.Address}")
    {
    }

    public static implicit operator EntityId(Entity entity)
    {
        return entity.Id;
    }
    public static implicit operator EntityId(string id)
    {
        return new EntityId(id);
    }

    public static implicit operator EntityId(CameraId id)
    {
        return new EntityId(id);
    }

    public unsafe static implicit operator EntityId(NativeCharacter* chara)
    {
        return new EntityId(ActorKey((nint)chara));
    }

    public unsafe static implicit operator EntityId(NativeGameObject* go)
    {
        return new EntityId(ActorKey((nint)go));
    }
    public unsafe static implicit operator EntityId(GameLight* light)
    {
        return new EntityId($"light_{(nint)light}");
    }

    private static string ActorKey(nint address)
    {
        if(ActorKeys.TryGetValue(address, out var key))
            return key;

        // 上限只防位址不斷換新時無限長；清掉後重組出來的字串相同，不影響比對。
        if(ActorKeys.Count >= MaxActorKeys)
            ActorKeys.Clear();

        key = $"actor_{address}";
        ActorKeys[address] = key;
        return key;
    }
}
