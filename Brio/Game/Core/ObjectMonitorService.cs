using Brio.Core;
using Brio.Game.Actor.Interop;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System;
using System.Runtime.InteropServices;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Brio.Game.Core;

public unsafe class ObjectMonitorService : IDisposable
{
    public IObjectTable ObjectTable => _objectTable;

    public delegate void CharacterEventDelegate(NativeCharacter* chara);
    public event CharacterEventDelegate? CharacterInitialized;
    public event CharacterEventDelegate? CharacterDestroyed;

    public delegate void CharacterBaseEventDelegate(BrioCharacterBase* charaBase);
    public event CharacterBaseEventDelegate? CharacterBaseMaterialsUpdated;
    public event CharacterBaseEventDelegate? CharacterBaseDestroyed;

    private readonly IObjectTable _objectTable;

    private delegate nint NativeCharacterEventDelegate(NativeCharacter* chara);
    private readonly Hook<NativeCharacterEventDelegate>? _characterIntitializeHook;
    private readonly Hook<NativeCharacterEventDelegate>? _characterFinalizeHook;

    private delegate nint CharacterBaseUpdateMaterialsDelegate(BrioCharacterBase* charaBase);
    private readonly Hook<CharacterBaseUpdateMaterialsDelegate>? _characterBaseUpdateMaterialsHook;

    private delegate nint CharacterBaseCleanupDelegate(BrioCharacterBase* charaBase);
    private readonly Hook<CharacterBaseCleanupDelegate>? _characterBaseCleanupHook;

    public ObjectMonitorService(IObjectTable objectTable, ISigScanner scanner, IGameInteropProvider hooking)
    {
        _objectTable = objectTable;

        _characterIntitializeHook = NativeBinding.ScanHook<NativeCharacterEventDelegate>(scanner, hooking,
            "E8 ?? ?? ?? ?? 8D 57 ?? C6 83", CharacterIntitializeDetour, "角色初始化 Character::Initialize");

        _characterFinalizeHook = NativeBinding.ScanHook<NativeCharacterEventDelegate>(scanner, hooking,
            "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8D 05 ?? ?? ?? ?? 48 8B D9 48 89 01 48 8D 05 ?? ?? ?? ?? 48 89 81 ?? ?? ?? ?? 48 81 C1",
            CharacterFinalizeDetour, "角色解構 Character::Finalize");

        // CharacterBase 虛擬表槽 8/8 = 1。虛擬表指標解不出來時讀它是 AccessViolation,先判空。
        nint charaBaseCleanupAddr = nint.Zero;
        var charaBaseVTable = (nint)CharacterBase.StaticVirtualTablePointer;
        if(charaBaseVTable == nint.Zero)
            NativeBinding.Fail("角色模型清除 CharacterBase::Cleanup", "CharacterBase 靜態虛擬表指標未解析");
        else
            charaBaseCleanupAddr = (nint)Marshal.ReadInt64(charaBaseVTable + 8);
        _characterBaseCleanupHook = NativeBinding.CreateHook<CharacterBaseCleanupDelegate>(hooking, charaBaseCleanupAddr, CharacterBaseCleanupDetour, "角色模型清除 CharacterBase::Cleanup");

        _characterBaseUpdateMaterialsHook = NativeBinding.ScanHook<CharacterBaseUpdateMaterialsDelegate>(scanner, hooking,
            "48 89 5C 24 ?? 48 89 6C 24 ?? 56 57 41 56 48 83 EC ?? 4C 89 7C 24",
            CharacterBaseUpdateMaterialsDetour, "角色材質更新 CharacterBase::UpdateMaterials");
    }

    private nint CharacterIntitializeDetour(NativeCharacter* chara)
    {
        var result = _characterIntitializeHook!.Original.Invoke(chara);
        CharacterInitialized?.Invoke(chara);
        return result;
    }

    private nint CharacterFinalizeDetour(NativeCharacter* chara)
    {
        CharacterDestroyed?.Invoke(chara);
        return _characterFinalizeHook!.Original.Invoke(chara);
    }

    private nint CharacterBaseUpdateMaterialsDetour(BrioCharacterBase* charaBase)
    {
        var result = _characterBaseUpdateMaterialsHook!.Original(charaBase);
        CharacterBaseMaterialsUpdated?.Invoke(charaBase);
        return result;
    }

    private nint CharacterBaseCleanupDetour(BrioCharacterBase* charaBase)
    {
        if(charaBase != null)
        {
            CharacterBaseDestroyed?.Invoke(charaBase);
        }

        return _characterBaseCleanupHook!.Original(charaBase);
    }

    public void Dispose()
    {
        _characterIntitializeHook?.Dispose();
        _characterFinalizeHook?.Dispose();
        _characterBaseUpdateMaterialsHook?.Dispose();
        _characterBaseCleanupHook?.Dispose();
    }
}
