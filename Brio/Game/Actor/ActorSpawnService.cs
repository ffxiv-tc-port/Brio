using Brio.Capabilities.Actor;
using Brio.Capabilities.Posing;
using Brio.Core;
using Brio.Entities;
using Brio.Files;
using Brio.Game.Actor.Appearance;
using Brio.Game.Actor.Extensions;
using Brio.Game.Core;
using Brio.Game.GPose;
using Brio.Game.Posing;
using Brio.Game.Types;
using Brio.IPC;
using Brio.Resources;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using CharacterCopyFlags = FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterSetupContainer.CopyFlags;
using ClientObjectManager = FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Brio.Game.Actor;

public class ActorSpawnService : IDisposable
{
    private readonly ObjectMonitorService _monitorService;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly IFramework _framework;
    private readonly GPoseService _gPoseService;
    private readonly ActorRedrawService _actorRedrawService;
    private readonly GlamourerService _glamourerService;
    private readonly TargetService _targetService;
    private readonly EntityManager _entityManager;
    private readonly PosingService _posingService;
    private readonly ActorAppearanceService _actorAppearanceService;
    private readonly CustomizePlusService _customizePlusService;
    private readonly ActorLookAtService _actorLookAtService;
    private readonly CharacterHandlerService _characterHandlerService;

    private readonly Dictionary<ushort, SpawnFlags> _createdIndexes = [];

    public unsafe ActorSpawnService(ObjectMonitorService monitorService, CustomizePlusService customizePlusService, ActorLookAtService actorLookAtService, CharacterHandlerService characterHandlerService,
        ActorAppearanceService actorAppearanceService, PosingService posingService, GlamourerService glamourerService,
        EntityManager entityManager, IObjectTable objectTable, IClientState clientState, IFramework framework,
        GPoseService gPoseService, ActorRedrawService actorRedrawService, TargetService targetService)
    {
        _monitorService = monitorService;
        _objectTable = objectTable;
        _clientState = clientState;
        _framework = framework;
        _gPoseService = gPoseService;
        _actorRedrawService = actorRedrawService;
        _glamourerService = glamourerService;
        _targetService = targetService;
        _entityManager = entityManager;
        _posingService = posingService;
        _actorAppearanceService = actorAppearanceService;
        _customizePlusService = customizePlusService;

        _actorLookAtService = actorLookAtService;
        _characterHandlerService = characterHandlerService;

        _monitorService.CharacterDestroyed += OnCharacterDestroyed;
        _gPoseService.OnGPoseStateChange += OnGPoseStateChanged;
        _clientState.TerritoryChanged += OnTerritoryChanged;
    }

    public bool CreateCharacter([MaybeNullWhen(false)] out ICharacter outCharacter, SpawnFlags flags = SpawnFlags.Default, bool disableSpawnCompanion = false)
    {
        outCharacter = null;

        var localPlayer = _clientState.LocalPlayer;
        if(localPlayer != null)
        {
            if(CloneCharacter(localPlayer, out outCharacter, flags, disableSpawnCompanion: disableSpawnCompanion))
            {
                return true;
            }
        }

        return false;
    }

    public unsafe bool CloneCharacter(ICharacter sourceCharacter, [MaybeNullWhen(false)] out ICharacter outCharacter, SpawnFlags flags = SpawnFlags.Default, bool disableSpawnCompanion = false)
    {
        outCharacter = null;

        CharacterCopyFlags copyFlags = CharacterCopyFlags.WeaponHiding;

        bool hasCompanion = sourceCharacter.HasSpawnedCompanion();
        if(disableSpawnCompanion == false && hasCompanion)
        {
            flags |= SpawnFlags.ReserveCompanionSlot;
            copyFlags |= CharacterCopyFlags.Companion | CharacterCopyFlags.Ornament | CharacterCopyFlags.Mount;
        }

        if(flags.HasFlag(SpawnFlags.CopyPosition))
            copyFlags |= CharacterCopyFlags.Position;


        if(CreateEmptyCharacter(out outCharacter, flags))
        {

            var sourceNative = sourceCharacter.Native();
            var targetNative = outCharacter.Native();

            // This double copy from self is needed for some tools like Penumbra/Glamourer.
            // We first copy the real source, then we copy ourselves onto ourselves.
            targetNative->CharacterSetup.CopyFromCharacter(sourceCharacter.Native(), copyFlags);
            targetNative->CharacterSetup.CopyFromCharacter(outCharacter.Native(), CharacterCopyFlags.None);

            // Copy position if requested
            if(flags.HasFlag(SpawnFlags.CopyPosition))
            {
                var position = sourceNative->GameObject.Position;
                var rotation = sourceNative->GameObject.Rotation;

                // TODO: This is only needed for Anamnesis and Ktisis. 
                if(sourceNative->GameObject.DrawObject != null && sourceNative->GameObject.DrawObject->IsVisible)
                {
                    // TODO: This is weird if you are mounted
                    position = sourceNative->GameObject.DrawObject->Object.Position;
                }

                targetNative->GameObject.DefaultPosition = position;
                targetNative->GameObject.Position = position;
                targetNative->GameObject.Rotation = rotation;
                targetNative->GameObject.DefaultRotation = rotation;
            }

            // Start drawing
            _actorRedrawService.DrawWhenReady(outCharacter);

            if(disableSpawnCompanion == false && hasCompanion)
            {
                // We need to wait for the companion to be ready before we can draw it.
                var companion = _objectTable.CreateObjectReference((nint)(targetNative->CompanionObject));
                if(companion != null)
                    _actorRedrawService.DrawWhenReady(companion);
            }


            return true;
        }

        return false;
    }

    public unsafe bool SpawnNewProp(out ICharacter? gamechara)
    {
        if(CreateCharacter(out ICharacter? chara, SpawnFlags.IsProp | SpawnFlags.CopyPosition, true))
        {
            // 🔴 RunUntilSatisfied 會逐幀重排最多 100 幀,而 chara 的 Address 是建構當下凍結的。
            //    道具在這段期間被銷毀就成了懸空位址,chara.Native()->IsReadyToDraw() 會踩到已釋放的記憶體
            //    —— AccessViolationException 在 .NET Core 是 corrupted-state exception,
            //    ProcessTask 外圍的 try/catch 完全攔不到。抄走索引 + 位址,每一幀由物件表重查。
            var actorRef = new LiveActorRef(_objectTable, chara);

            _framework.RunUntilSatisfied(
            () =>
            {
                var native = actorRef.Character;
                return native != null && native->IsReadyToDraw();
            },
            (__) =>
            {
                var native = actorRef.Character;
                if(native == null)
                    return;

                var entity = _entityManager.GetEntity(native);
                if(entity is not null)
                {
                    entity.GetCapability<ActionTimelineCapability>().SetOverallSpeedOverride(0);

                    var acf = JsonSerializer.Deserialize<AnamnesisCharaFile>(ResourceProvider.Instance.GetRawResourceString("Data.BrioPropChar.chara"));
                    if(acf.Race == 0 && acf.ModelType == 0)
                    {
                        Brio.Log.Fatal("BrioPropChar was Invalid!!");
                    }
                    else
                    {
                        entity.GetCapability<ActorAppearanceCapability>().SetAppearanceAsTask(acf, AppearanceImportOptions.Default);
                    }

                    _framework.RunOnTick(() =>
                    {
                        // 又過了 5 幀,entity.GameObject.Address 一樣可能已經懸空 —— 先確認它還在物件表裡。
                        if(actorRef.IsAlive == false)
                            return;

                        entity.GetCapability<PosingCapability>().LoadResourcesPose("Data.BrioPropPose.pose");

                        _framework.RunOnTick(() =>
                        {
                            if(actorRef.IsAlive == false)
                                return;

                            entity.GetCapability<ActorAppearanceCapability>().AttachWeapon();
                        }, delayTicks: 5);
                    }, delayTicks: 5);
                }
            },
                100,
                dontStartFor: 2
            );

            gamechara = chara;
            return true;
        }

        gamechara = null;
        return false;
    }

    public void ClearAll()
    {
        for(int i = ActorTableHelpers.GPoseStart; i <= ActorTableHelpers.GPoseEnd; i++)
        {
            var obj = _objectTable[i];
            if(obj == null)
                continue;

            DestroyObject(obj);
        }
    }

    public bool DestroyObject(int objectIndex)
    {
        var go = _objectTable[objectIndex];

        if(go != null)
            return DestroyObject(go);

        return false;
    }

    public unsafe bool DestroyObject(IGameObject go)
    {
        if(go is null)
            return false;

        CleanObject(go, false);

        var com = ClientObjectManager.Instance();
        var native = go.Native();
        var idx = com->GetIndexByObject(native);
        if(idx != 0xFFFFFFFF)
        {
            com->DeleteObjectByIndex((ushort)idx, 0);
            return true;
        }

        return false;
    }

    public unsafe void DestroyAllCreated(bool disposing)
    {
        Brio.Log.Debug("Destroying all created gameobjects.");

        var indexes = _createdIndexes.Keys;
        var com = ClientObjectManager.Instance();
        foreach(var idx in indexes)
        {
            var obj = com->GetObjectByIndex(idx);
            if(obj is not null)
            {
                try
                {
                    var go = _objectTable.CreateObjectReference((nint)obj);

                    if(obj is not null)
                        CleanObject(go, disposing);
                    else
                        Brio.Log.Fatal($"CleanObject could not be called because the object was null idx:{idx}");
                }
                catch(Exception ex)
                {
                    Brio.Log.Warning(ex, $"Exception while destroying all the created objects idx:{idx}");
                }
            }
            com->DeleteObjectByIndex(idx, 0);
        }
        _createdIndexes.Clear();
    }

    public void CleanObject(IGameObject? go, bool disposing)
    {
        if(go is null) return;

        Brio.Log.Debug($"Destroying gameobject: {go.ObjectIndex}...");

        _actorLookAtService.RemoveObjectFromLook(go);

        _ = _characterHandlerService.Revert(go, disposing);
    }

    public void DestroyCompanion(ICharacter character)
    {
        if(character.CalculateCompanionInfo(out var info))
        {
            publicSetCompanion(character, info.Kind, 0);
        }
    }

    public unsafe void CreateCompanion(ICharacter character, CompanionContainer container)
    {
        DestroyCompanion(character);
        publicSetCompanion(character, container.Kind, (short)container.Id);

        // We need to wait for the companion to be ready before we can draw it.
        //
        // 🔴🔴 原本這裡把 &character.Native()->CompanionObject->Character.GameObject 這個**裸原生指標**
        //     捕獲進最多 1000 幀(60fps 下約 16 秒)的逐幀重排回呼,每一幀拿它解參,滿足時還往裡面寫
        //     EnableDraw()。宿主或同伴在這 16 秒內被銷毀是使用者按一下就會發生的事,而
        //     AccessViolationException 在 .NET Core 是 corrupted-state exception,try/catch 攔不到
        //     ⇒ 直接把遊戲弄崩。character 本身的 Address 也是建構當下凍結的,
        //     CalculateCompanionInfo 每一幀都在解參它,同樣會踩到懸空位址。
        //
        //     同伴物件是宿主的子結構(CompanionObject),沒有自己能抄走的穩定身分,所以正解是
        //     「每一幀從宿主重新導航」:抄走宿主的物件表索引 + 位址(都是值型別),每一幀先由物件表
        //     確認宿主還在(GetObjectAddress 只讀物件表的指標陣列、不解參任何存下來的位址),
        //     再從活著的宿主重新讀 CompanionObject。宿主不在了就一直回報未滿足,最後由
        //     RunUntilSatisfied 自己逾時(留下一行 Warning),全程不解參懸空位址。
        var hostRef = new LiveActorRef(_objectTable, character);

        _framework.RunUntilSatisfied(
            () =>
            {
                var companionGameObject = ResolveCompanionGameObject(hostRef, container);
                return companionGameObject != null && companionGameObject->IsReadyToDraw();
            },
            (_) =>
            {
                var companionGameObject = ResolveCompanionGameObject(hostRef, container);
                if(companionGameObject != null)
                    companionGameObject->EnableDraw();
            },
            1000,
            dontStartFor: 1
        );
    }

    /// <summary>
    /// 每次呼叫都先由物件表重新確認宿主還活著,再從宿主導航到同伴物件。
    /// 宿主已消失、同伴槽是空的、或現在掛著的同伴不是這次要求的那一隻時回傳 <c>null</c>,
    /// 全程不解參任何先前存下來的位址。<b>回傳的指標只能在同一幀之內使用。</b>
    /// 條件與原本的 <c>character.CalculateCompanionInfo(out info) &amp;&amp; info.Kind == container.Kind
    /// &amp;&amp; info.Id == container.Id</c> 逐項等價(CalculateCompanionInfo 為真 ≡ Kind != None)。
    /// </summary>
    private unsafe NativeGameObject* ResolveCompanionGameObject(LiveActorRef hostRef, CompanionContainer container)
    {
        var host = hostRef.Character;
        if(host == null)
            return null;

        var companion = host->CompanionObject;
        if(companion == null)
            return null;

        var info = CharacterExtensions.GetCompanionInfo(host);
        if(info.Kind == CompanionKind.None || info.Kind != container.Kind || info.Id != container.Id)
            return null;

        return &companion->Character.GameObject;
    }

    private unsafe void publicSetCompanion(ICharacter character, CompanionKind kind, short id)
    {
        var native = character.Native();
        switch(kind)
        {
            case CompanionKind.Companion:
                native->CompanionData.SetupCompanion(id, 0);
                break;

            case CompanionKind.Mount:
                native->Mount.CreateAndSetupMount(id, 0, 0, 0, 0, 0, 0);
                break;

            case CompanionKind.Ornament:
                native->OrnamentData.SetupOrnament(id, 0);
                break;
        }
    }

    private bool CreateEmptyCharacter([MaybeNullWhen(false)] out ICharacter outCharacter, SpawnFlags flags)
    {
        outCharacter = null;

        Brio.Log.Debug("Creating Brio character...");

        unsafe
        {
            var com = ClientObjectManager.Instance();
            uint idCheck = com->CreateBattleCharacter(param: (byte)(flags.HasFlag(SpawnFlags.ReserveCompanionSlot) ? 1 : 0));
            if(idCheck == 0xffffffff)
            {
                Brio.Log.Warning("Failed to create character, invalid ID was returned.");
                EventBus.Instance.NotifyError("Failed to create character.");
                return false;
            }
            ushort newId = (ushort)idCheck;

            _createdIndexes.Add(newId, flags);

            var newObject = com->GetObjectByIndex(newId);
            if(newObject == null) return false;

            var newPlayer = (NativeCharacter*)newObject;

            newObject->CalculateAndSetName(newId); // Brio One etc

            _gPoseService.AddCharacterToGPose(newPlayer);

            var character = _objectTable.CreateObjectReference((nint)newObject);
            if(character is null or not ICharacter)
                return false;

            outCharacter = (ICharacter)character;
        }

        if(_gPoseService.IsGPosing && _targetService.HasGPoseTarget == false)
            _targetService.GPoseTarget = outCharacter;

        return true;
    }

    public unsafe SpawnFlags GetSpawnFlagsByIndex(ushort objectIndex)
    {
        if(_createdIndexes.TryGetValue(objectIndex, out var spawnFlags))
        {
            Brio.Log.Verbose($"GetSpawnFlagsByIndex {objectIndex} {spawnFlags}");
            return spawnFlags;
        }

        return SpawnFlags.None;
    }

    private void OnGPoseStateChanged(bool newState)
    {
        if(newState == false)
            DestroyAllCreated(newState);
    }

    private unsafe void OnCharacterDestroyed(NativeCharacter* chara)
    {
        var go = _objectTable.CreateObjectReference((nint)chara);
        if(go != null && go.IsGPose())
        {
            var com = ClientObjectManager.Instance();
            var idx = com->GetIndexByObject(go.Native());
            if(idx < ushort.MaxValue)
                _createdIndexes.Remove((ushort)idx);
        }
    }

    private void OnTerritoryChanged(ushort obj)
    {
        _createdIndexes.Clear();
    }

    public unsafe void Dispose()
    {
        _monitorService.CharacterDestroyed -= OnCharacterDestroyed;
        _gPoseService.OnGPoseStateChange -= OnGPoseStateChanged;
        _clientState.TerritoryChanged -= OnTerritoryChanged;

        DestroyAllCreated(true);

        GC.SuppressFinalize(this);
    }
}

[Flags]
public enum SpawnFlags
{
    None = 0,
    ReserveCompanionSlot = 1 << 0,
    CopyPosition = 1 << 1,
    IsProp = 1 << 2,
    IsEffect = 1 << 3,
    SetDefaultAppearance = 1 << 4,

    Prop = IsProp | SetDefaultAppearance | CopyPosition,
    Effect = IsEffect | SetDefaultAppearance | CopyPosition,
    Default = CopyPosition,
}
