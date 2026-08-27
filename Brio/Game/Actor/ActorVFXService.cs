using Brio.Core;
using Brio.Game.Actor.Extensions;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Types;
using System;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Brio.Game.Actor;

public unsafe class ActorVFXService : IDisposable
{

    private delegate* unmanaged<string, NativeGameObject*, NativeGameObject*, float, byte, ushort, byte, nint> _createActorVfx;

    private delegate* unmanaged<nint, void> _vfxDtor;


    public ActorVFXService(ISigScanner scanner)
    {
        var vfxCreateAddress = NativeBinding.Scan(scanner, "E8 ?? ?? ?? ?? 48 8B D8 48 85 C0 74 ?? 0F B6 57 ?? 48 8B C8 C0 EA", "角色特效建立 CreateActorVfx");
        _createActorVfx = (delegate* unmanaged<string, NativeGameObject*, NativeGameObject*, float, byte, ushort, byte, nint>)vfxCreateAddress;

        var vfxDtorAddress = NativeBinding.Scan(scanner, "48 89 5C 24 ?? 57 48 83 EC ?? 48 8D 05 ?? ?? ?? ?? 48 8B D9 48 89 01 8B FA 48 8D 05 ?? ?? ?? ?? 48 89 81 ?? ?? ?? ?? 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 ?? 48 8B 01 48 8B D3", "角色特效解構 VfxDtor");
        _vfxDtor = (delegate* unmanaged<nint, void>)vfxDtorAddress;
    }

    public nint CreateActorVFX(string vfxName, IGameObject actor, IGameObject? target = null)
    {
        if(target == null)
            target = actor;

        return CreateActorVFX(vfxName, actor.Native(), target.Native());
    }

    public nint CreateActorVFX(string vfxName, NativeGameObject* actor, NativeGameObject* target = null)
    {
        if(target == null)
            target = actor;

        // 特徵碼失效時 _createActorVfx 為 null,呼叫它等同呼叫位址 0 ⇒ 直接讓功能停用。
        if(_createActorVfx == null)
            return 0;

        return _createActorVfx(vfxName, actor, target, -1, 0, 0, 0);
    }

    public void DestroyVFX(nint vfxInstance)
    {
        if(vfxInstance != 0 && _vfxDtor != null)
            _vfxDtor(vfxInstance);
    }

    public unsafe void Dispose()
    {
    }
}
