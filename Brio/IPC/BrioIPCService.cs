using Brio.Capabilities.Actor;
using Brio.Capabilities.Posing;
using Brio.Config;
using Brio.Core;
using Brio.Entities;
using Brio.Entities.Core;
using Brio.Files;
using Brio.Game.Actor;
using Brio.Game.Actor.Extensions;
using Brio.Game.Core;
using Brio.Game.GPose;
using Brio.Game.Posing;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Brio.IPC;
public class BrioIPCService : IDisposable
{
    public static readonly (int, int) CurrentApiVersion = (2, 0);

    public bool IsIPCEnabled { get; private set; } = false;

    //

    public const string ApiVersion_IPCName = "Brio.ApiVersion";
    private ICallGateProvider<(int, int)>? API_Version_IPC;


    public const string Actor_Spawn_IPCName = "Brio.Actor.Spawn";
    private ICallGateProvider<IGameObject?>? Actor_Spawn_IPC;

    public const string Actor_SpawnAsync_IPCName = "Brio.Actor.SpawnAsync";
    private ICallGateProvider<Task<IGameObject?>>? Actor_SpawnAsync_IPC;

    public const string Actor_SpawnExAsync_IPCName = "Brio.Actor.SpawnExAsync";
    private ICallGateProvider<bool, bool, bool, Task<IGameObject?>>? Actor_SpawnExAsync_IPC;

    public const string Actor_SpawnEx_IPCName = "Brio.Actor.SpawnEx";
    private ICallGateProvider<bool, bool, bool, IGameObject?>? Actor_SpawnEx_IPC;


    public const string Actor_Despawn_IPCName = "Brio.Actor.Despawn";
    private ICallGateProvider<IGameObject, bool>? Actor_DespawnActor_IPC;


    public const string Actor_SetModelTransform_IPCName = "Brio.Actor.SetModelTransform";
    private ICallGateProvider<IGameObject, Vector3?, Quaternion?, Vector3?, bool, bool>? Actor_SetModelTransform_IPC;

    public const string Actor_GetModelTransform_IPCName = "Brio.Actor.GetModelTransform";
    private ICallGateProvider<IGameObject, (Vector3?, Quaternion?, Vector3?)>? Actor_GetModelTransform_IPC;

    public const string Actor_ResetModelTransform_IPCName = "Brio.Actor.ResetModelTransform";
    private ICallGateProvider<IGameObject, bool>? Actor_ResetModelTransform_IPC;


    public const string Actor_PoseLoadFromFile_IPCName = "Brio.Actor.Pose.LoadFromFile";
    private ICallGateProvider<IGameObject, string, bool>? Actor_Pose_LoadFromFile_IPC;

    public const string Actor_PoseLoadFromJson_IPCName = "Brio.Actor.Pose.LoadFromJson";
    private ICallGateProvider<IGameObject, string, bool, bool>? Actor_Pose_LoadFromJson_IPC;

    public const string Actor_PoseGetAsJson_IPCName = "Brio.Actor.Pose.GetPoseAsJson";
    private ICallGateProvider<IGameObject, string?>? Actor_Pose_GetFromJson_IPC;

    public const string Actor_Pose_Reset_IPCName = "Brio.Actor.Pose.Reset";
    private ICallGateProvider<IGameObject, bool, bool>? Actor_Pose_Reset_IPC;

    public const string Actor_Exists_IPCName = "Brio.Actor.Exists";
    private ICallGateProvider<IGameObject, bool>? Actor_Exists_IPC;

    public const string Actor_GetAll_IPCName = "Brio.Actor.GetAll";
    private ICallGateProvider<IGameObject[]?>? Actor_GetAll_IPC;


    public const string Actor_SetSpeed_IPCName = "Brio.Actor.SetSpeed";
    private ICallGateProvider<IGameObject, float, bool>? Actor_SetSpeed_IPC;

    public const string Actor_GetSpeed_IPCName = "Brio.Actor.GetSpeed";
    private ICallGateProvider<IGameObject, float>? Actor_GetSpeed_IPC;

    public const string Actor_Freeze_IPCName = "Brio.Actor.Freeze";
    private ICallGateProvider<IGameObject, bool>? Actor_Freeze_IPC;

    public const string Actor_UnFreeze_IPCName = "Brio.Actor.UnFreeze";
    private ICallGateProvider<IGameObject, bool>? Actor_UnFreeze_IPC;

    public const string FreezePhysics_IPCName = "Brio.FreezePhysics";
    private ICallGateProvider<bool>? FreezePhysics_IPC;

    public const string UnFreezePhysics_IPCName = "Brio.UnFreezePhysics";
    private ICallGateProvider<bool>? UnFreezePhysics_IPC;

    //

    private readonly ActorSpawnService _actorSpawnService;
    private readonly GPoseService _gPoseService;
    private readonly ConfigurationService _configurationService;
    private readonly EntityManager _entityManager;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IFramework _framework;
    private readonly PhysicsService _physicsService;
    private readonly IObjectTable _objectTable;

    /// <summary>
    /// 🔴 所有同步端點的遊戲主執行緒閘門。CallGate 是直接方法呼叫，提供端跑在<b>呼叫端的執行緒</b>上，
    /// 而下面每一個 Impl 都會生成／銷毀角色、解原生指標、改 model transform、對遊戲碼寫 NOP、
    /// 或走訪 <c>EntityManager</c> 的裸字典 —— 那些在非 framework 執行緒上做就是
    /// AccessViolationException，而 AVE 在 .NET Core 連 <c>try</c>／<c>catch</c> 都攔不到。
    /// 📌 已經在 framework 執行緒上呼叫時，閘門就地執行、行為逐字不變。
    /// </summary>
    private readonly IpcFrameworkGate _ipcGate;

    public BrioIPCService(ActorSpawnService actorSpawnService, GPoseService gPoseService, ConfigurationService configurationService, EntityManager entityManager,
        IDalamudPluginInterface pluginInterface, IFramework framework, PhysicsService physicsService, IObjectTable objectTable)
    {
        _objectTable = objectTable;
        _actorSpawnService = actorSpawnService;
        _gPoseService = gPoseService;
        _configurationService = configurationService;
        _entityManager = entityManager;
        _pluginInterface = pluginInterface;
        _framework = framework;
        _physicsService = physicsService;
        _ipcGate = new IpcFrameworkGate(framework);

        if(_configurationService.Configuration.IPC.EnableBrioIPC)
            CreateIPC();

        _configurationService.OnConfigurationChanged += OnConfigurationChanged;
    }
    private void OnConfigurationChanged()
    {
        if(IsIPCEnabled != _configurationService.Configuration.IPC.EnableBrioIPC)
        {
            if(_configurationService.Configuration.IPC.EnableBrioIPC)
                CreateIPC();
            else
                DisposeIPC();
        }
    }

    private void CreateIPC()
    {
        API_Version_IPC = _pluginInterface.GetIpcProvider<(int, int)>(ApiVersion_IPCName);
        API_Version_IPC.RegisterFunc(ApiVersion_Impl);


        // 🔑 閘門包在「註冊的那一層」而不是塞進每個 Impl 裡：
        //    ①Impl 的內容逐字不變，包含它們原本失敗時回的那個值；
        //    ②整個方法體（連同下游的 service／capability）一起被交回主執行緒，不必逐一追；
        //    ③端點的回傳型別一個都沒改 —— 改型別比刪端點更兇（CallGate 的型別轉換有靜默成功的方向）。
        Actor_Spawn_IPC = _pluginInterface.GetIpcProvider<IGameObject?>(Actor_Spawn_IPCName);
        Actor_Spawn_IPC.RegisterFunc(() => _ipcGate.Get<IGameObject?>(Actor_Spawn_IPCName, SpawnActor, null));

        Actor_SpawnAsync_IPC = _pluginInterface.GetIpcProvider<Task<IGameObject?>>(Actor_SpawnAsync_IPCName);
        Actor_SpawnAsync_IPC.RegisterFunc(SpawnActorAsync_Impl);

        Actor_SpawnExAsync_IPC = _pluginInterface.GetIpcProvider<bool, bool, bool, Task<IGameObject?>>(Actor_SpawnExAsync_IPCName);
        Actor_SpawnExAsync_IPC.RegisterFunc(SpawnExAsync_Impl);

        Actor_SpawnEx_IPC = _pluginInterface.GetIpcProvider<bool, bool, bool, IGameObject?>(Actor_SpawnEx_IPCName);
        Actor_SpawnEx_IPC.RegisterFunc((spawnCompanionSlot, selectInHierarchy, spawnFrozen) =>
            _ipcGate.Get<IGameObject?>(Actor_SpawnEx_IPCName, () => SpawnEx(spawnCompanionSlot, selectInHierarchy, spawnFrozen), null));

        Actor_DespawnActor_IPC = _pluginInterface.GetIpcProvider<IGameObject, bool>(Actor_Despawn_IPCName);
        Actor_DespawnActor_IPC.RegisterFunc(gameObject =>
            _ipcGate.Get(Actor_Despawn_IPCName, () => DespawnActor(gameObject), false));

        Actor_SetModelTransform_IPC = _pluginInterface.GetIpcProvider<IGameObject, Vector3?, Quaternion?, Vector3?, bool, bool>(Actor_SetModelTransform_IPCName);
        Actor_SetModelTransform_IPC.RegisterFunc((gameObject, position, rotation, scale, additiveMode) =>
            _ipcGate.Get(Actor_SetModelTransform_IPCName, () => ActorSetModelTransform_Impl(gameObject, position, rotation, scale, additiveMode), false));

        Actor_GetModelTransform_IPC = _pluginInterface.GetIpcProvider<IGameObject, (Vector3?, Quaternion?, Vector3?)>(Actor_GetModelTransform_IPCName);
        // default 展開就是 (null, null, null)，與 Impl 自己的失敗回值逐字相同。
        Actor_GetModelTransform_IPC.RegisterFunc(gameObject =>
            _ipcGate.Get<(Vector3?, Quaternion?, Vector3?)>(Actor_GetModelTransform_IPCName, () => ActorGetModelTransform_Impl(gameObject), default));

        Actor_ResetModelTransform_IPC = _pluginInterface.GetIpcProvider<IGameObject, bool>(Actor_ResetModelTransform_IPCName);
        Actor_ResetModelTransform_IPC.RegisterFunc(gameObject =>
            _ipcGate.Get(Actor_ResetModelTransform_IPCName, () => ActorResetModelTransform_Impl(gameObject), false));

        Actor_Pose_LoadFromFile_IPC = _pluginInterface.GetIpcProvider<IGameObject, string, bool>(Actor_PoseLoadFromFile_IPCName);
        Actor_Pose_LoadFromFile_IPC.RegisterFunc((gameObject, fileURI) =>
            _ipcGate.Get(Actor_PoseLoadFromFile_IPCName, () => LoadFromFile_Impl(gameObject, fileURI), false));

        Actor_Pose_LoadFromJson_IPC = _pluginInterface.GetIpcProvider<IGameObject, string, bool, bool>(Actor_PoseLoadFromJson_IPCName);
        Actor_Pose_LoadFromJson_IPC.RegisterFunc((gameObject, json, isLegacyCMToolPose) =>
            _ipcGate.Get(Actor_PoseLoadFromJson_IPCName, () => LoadFromJson_Impl(gameObject, json, isLegacyCMToolPose), false));

        Actor_Pose_GetFromJson_IPC = _pluginInterface.GetIpcProvider<IGameObject, string?>(Actor_PoseGetAsJson_IPCName);
        Actor_Pose_GetFromJson_IPC.RegisterFunc(gameObject =>
            _ipcGate.Get<string?>(Actor_PoseGetAsJson_IPCName, () => GetPoseAsJson_Impl(gameObject), null));

        Actor_Pose_Reset_IPC = _pluginInterface.GetIpcProvider<IGameObject, bool, bool>(Actor_Pose_Reset_IPCName);
        Actor_Pose_Reset_IPC.RegisterFunc((gameObject, clearRedoHistory) =>
            _ipcGate.Get(Actor_Pose_Reset_IPCName, () => ResetPose_Impl(gameObject, clearRedoHistory), false));

        Actor_Exists_IPC = _pluginInterface.GetIpcProvider<IGameObject, bool>(Actor_Exists_IPCName);
        Actor_Exists_IPC.RegisterFunc(gameObject =>
            _ipcGate.Get(Actor_Exists_IPCName, () => ActorExists_Impl(gameObject), false));

        Actor_GetAll_IPC = _pluginInterface.GetIpcProvider<IGameObject[]?>(Actor_GetAll_IPCName);
        // 🔴 這個看起來是唯讀端點，但它 foreach 的是 EntityManager 的裸 Dictionary，
        //    而那張表由 framework 執行緒每幀增刪 —— 跨執行緒走訪與寫入是同一級問題。
        Actor_GetAll_IPC.RegisterFunc(() =>
            _ipcGate.Get<IGameObject[]?>(Actor_GetAll_IPCName, ActorGetAll_Impl, null));

        Actor_SetSpeed_IPC = _pluginInterface.GetIpcProvider<IGameObject, float, bool>(Actor_SetSpeed_IPCName);
        Actor_SetSpeed_IPC.RegisterFunc((actor, speed) =>
            _ipcGate.Get(Actor_SetSpeed_IPCName, () => SetActorSpeed_Impl(actor, speed), false));

        Actor_GetSpeed_IPC = _pluginInterface.GetIpcProvider<IGameObject, float>(Actor_GetSpeed_IPCName);
        Actor_GetSpeed_IPC.RegisterFunc(actor =>
            _ipcGate.Get(Actor_GetSpeed_IPCName, () => GetActorSpeed_Impl(actor), 0f));

        Actor_Freeze_IPC = _pluginInterface.GetIpcProvider<IGameObject, bool>(Actor_Freeze_IPCName);
        Actor_Freeze_IPC.RegisterFunc(actor =>
            _ipcGate.Get(Actor_Freeze_IPCName, () => FreezActor_Impl(actor), false));

        Actor_UnFreeze_IPC = _pluginInterface.GetIpcProvider<IGameObject, bool>(Actor_UnFreeze_IPCName);
        Actor_UnFreeze_IPC.RegisterFunc(actor =>
            _ipcGate.Get(Actor_UnFreeze_IPCName, () => UnFreezActor_Impl(actor), false));

        FreezePhysics_IPC = _pluginInterface.GetIpcProvider<bool>(FreezePhysics_IPCName);
        FreezePhysics_IPC.RegisterFunc(() =>
            _ipcGate.Get(FreezePhysics_IPCName, FreezePhysics_Impl, false));

        UnFreezePhysics_IPC = _pluginInterface.GetIpcProvider<bool>(UnFreezePhysics_IPCName);
        UnFreezePhysics_IPC.RegisterFunc(() =>
            _ipcGate.Get(UnFreezePhysics_IPCName, UnFreezePhysics_Impl, false));

        IsIPCEnabled = true;
    }
    private void DisposeIPC()
    {
        API_Version_IPC?.UnregisterFunc();

        Actor_Spawn_IPC?.UnregisterFunc();
        Actor_SpawnAsync_IPC?.UnregisterFunc();
        Actor_SpawnExAsync_IPC?.UnregisterFunc();
        Actor_SpawnEx_IPC?.UnregisterFunc();

        Actor_DespawnActor_IPC?.UnregisterFunc();

        Actor_SetModelTransform_IPC?.UnregisterFunc();
        Actor_GetModelTransform_IPC?.UnregisterFunc();
        Actor_ResetModelTransform_IPC?.UnregisterFunc();

        Actor_Pose_LoadFromFile_IPC?.UnregisterFunc();
        Actor_Pose_LoadFromJson_IPC?.UnregisterFunc();
        Actor_Pose_GetFromJson_IPC?.UnregisterFunc();

        Actor_Freeze_IPC?.UnregisterFunc();
        Actor_GetSpeed_IPC?.UnregisterFunc();
        Actor_GetAll_IPC?.UnregisterFunc();
        Actor_SetSpeed_IPC?.UnregisterFunc();

        Actor_Exists_IPC?.UnregisterFunc();

        Actor_Pose_Reset_IPC?.UnregisterFunc();

        Actor_UnFreeze_IPC?.UnregisterFunc();
        FreezePhysics_IPC?.UnregisterFunc();
        UnFreezePhysics_IPC?.UnregisterFunc();

        API_Version_IPC = null;

        Actor_Spawn_IPC = null;
        Actor_SpawnAsync_IPC = null;
        Actor_SpawnExAsync_IPC = null;
        Actor_SpawnEx_IPC = null;

        Actor_DespawnActor_IPC = null;

        Actor_SetModelTransform_IPC = null;
        Actor_GetModelTransform_IPC = null;
        Actor_ResetModelTransform_IPC = null;

        Actor_Pose_LoadFromFile_IPC = null;
        Actor_Pose_LoadFromJson_IPC = null;
        Actor_Pose_GetFromJson_IPC = null;

        Actor_Freeze_IPC = null;
        Actor_GetSpeed_IPC = null;
        Actor_GetAll_IPC = null;
        Actor_SetSpeed_IPC = null;

        Actor_Exists_IPC = null;

        Actor_Pose_Reset_IPC = null;

        Actor_UnFreeze_IPC = null;
        FreezePhysics_IPC = null;
        UnFreezePhysics_IPC = null;

        IsIPCEnabled = false;
    }

    //

    private (int, int) ApiVersion_Impl() => CurrentApiVersion;

    // 🔴 非同步孿生版也要判卸載期：無延遲的 RunOnTick 在 IsFrameworkUnloading 為真時
    //    會轉呼叫 RunOnFrameworkThread（Dalamud/Game/Framework.cs:200-211），而那個在卸載期
    //    是「就地在呼叫端執行緒執行」—— 等於角色生成整段跑在別人的背景執行緒上。
    //    卸載期回 null（＝「現在生不出來」，這個端點本來就會回 null）比崩潰好。
    //    📌 已經在 framework 執行緒上時不受影響，行為逐字不變。
    private Task<IGameObject?> SpawnActorAsync_Impl()
    {
        if(_ipcGate.ShouldBypassForUnloading(Actor_SpawnAsync_IPCName))
            return Task.FromResult<IGameObject?>(null);

        return _framework.RunOnTick(SpawnActor);
    }
    private IGameObject? SpawnActor()
    {
        if(_gPoseService.IsGPosing == false) return null;

        if(_actorSpawnService.CreateCharacter(out var character, SpawnFlags.Default, disableSpawnCompanion: true))
            return character;

        return null;
    }

    // 🔴 與 SpawnActorAsync_Impl 同一個理由：卸載期的無延遲 RunOnTick 會就地執行。
    private async Task<IGameObject?> SpawnExAsync_Impl(bool spawnCompanion, bool selectInHierarchy, bool spawnFrozen)
    {
        if(_ipcGate.ShouldBypassForUnloading(Actor_SpawnExAsync_IPCName))
            return null;

        return await _framework.RunOnTick(() => SpawnEx(spawnCompanion, selectInHierarchy, spawnFrozen));
    }
    private IGameObject? SpawnEx(bool spawnCompanionSlot, bool selectInHierarchy, bool spawnFrozen)
    {
        if(_gPoseService.IsGPosing == false) return null;

        SpawnFlags flags = SpawnFlags.Default;
        if(spawnCompanionSlot)
        {
            flags |= SpawnFlags.ReserveCompanionSlot;
        }

        if(_actorSpawnService.CreateCharacter(out var character, flags, disableSpawnCompanion: !spawnCompanionSlot))
        {
            if(selectInHierarchy)
            {
                _entityManager.SetSelectedEntity(character);
            }

            if(spawnFrozen)
            {
                unsafe
                {
                    // 🔴 character 的 Address 是建構當下凍結的,而下面這個條件會逐幀重跑最多 100 幀。
                    //    角色在這段期間被銷毀就成了懸空位址,character.Native() 的解參會踩到已釋放的記憶體
                    //    (AccessViolationException 在 .NET Core 連 try/catch 都攔不到)。
                    //    抄走索引 + 位址,每一幀由物件表重查。
                    var actorRef = new LiveActorRef(_objectTable, character);

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

                            if(_entityManager.TryGetEntity(native, out var entity))
                            {
                                if(entity.TryGetCapability<ActionTimelineCapability>(out var actionTimeline))
                                {
                                    actionTimeline.SetOverallSpeedOverride(0);
                                }
                            }
                        },
                        100,
                        dontStartFor: 2
                    );
                }
            }

            return character;
        }

        return null;
    }

    private bool DespawnActor(IGameObject gameObject)
    {
        if(_gPoseService.IsGPosing == false) return false;

        return _actorSpawnService.DestroyObject(gameObject);
    }

    private unsafe bool ActorSetModelTransform_Impl(IGameObject gameObject, Vector3? position, Quaternion? rotation, Vector3? scale, bool additiveMode)
    {
        if(_gPoseService.IsGPosing == false) return false;

        if(_entityManager.TryGetEntity(gameObject.Native(), out var entity))
        {
            if(entity.TryGetCapability<ModelPosingCapability>(out var transformCapability))
            {
                var transform = transformCapability.Transform;

                if(additiveMode)
                {
                    if(position.HasValue)
                        transform.Position += position.Value;
                    if(rotation.HasValue)
                        transform.Rotation += rotation.Value;
                    if(scale.HasValue)
                        transform.Scale += scale.Value;
                }
                else
                {
                    if(position.HasValue)
                        transform.Position = position.Value;
                    if(rotation.HasValue)
                        transform.Rotation = rotation.Value;
                    if(scale.HasValue)
                        transform.Scale = scale.Value;
                }

                transformCapability.Transform = transform;

                return true;
            }
        }
        return false;
    }
    private unsafe (Vector3?, Quaternion?, Vector3?) ActorGetModelTransform_Impl(IGameObject gameObject)
    {
        if(_gPoseService.IsGPosing == false) return (null, null, null);

        if(_entityManager.TryGetEntity(gameObject.Native(), out var entity))
        {
            if(entity.TryGetCapability<ModelPosingCapability>(out var transformCapability))
            {
                var transform = transformCapability.Transform;

                return (transform.Position, transform.Rotation, transform.Scale);
            }
        }
        return (null, null, null);
    }
    private unsafe bool ActorResetModelTransform_Impl(IGameObject gameObject)
    {
        if(_gPoseService.IsGPosing == false) return false;

        if(_entityManager.TryGetEntity(gameObject.Native(), out var entity))
        {
            if(entity.TryGetCapability<ModelPosingCapability>(out var transformCapability))
            {
                transformCapability.ResetTransform();
                return true;
            }
        }
        return false;
    }

    private unsafe bool LoadFromFile_Impl(IGameObject gameObject, string fileURI)
    {
        if(_gPoseService.IsGPosing == false || string.IsNullOrEmpty(fileURI)) return false;

        if(_entityManager.TryGetEntity(gameObject.Native(), out var entity))
        {
            if(entity.TryGetCapability<PosingCapability>(out var posingCapability))
            {
                posingCapability.ImportPose(fileURI);

                return true;
            }
        }
        return false;
    }
    private unsafe bool LoadFromJson_Impl(IGameObject gameObject, string json, bool isLegacyCMToolPose)
    {
        if(_gPoseService.IsGPosing == false || string.IsNullOrEmpty(json)) return false;

        if(_entityManager.TryGetEntity(gameObject.Native(), out Entity? entity) && entity is not null)
        {
            if(entity.TryGetCapability<PosingCapability>(out var posingCapability))
            {
                try
                {
                    if(isLegacyCMToolPose)
                    {
                        posingCapability.ImportPose(JsonSerializer.Deserialize<CMToolPoseFile>(json), null, asIPCpose: true);
                    }
                    else
                    {
                        posingCapability.ImportPose(JsonSerializer.Deserialize<PoseFile>(json), null, asIPCpose: true);
                    }

                    return true;
                }
                catch
                {
                    Brio.NotifyError("Invalid pose file loaded from IPC.");
                }
            }
        }

        return false;
    }
    private unsafe string? GetPoseAsJson_Impl(IGameObject gameObject)
    {
        if(_gPoseService.IsGPosing == false) return null;

        if(_entityManager.TryGetEntity(gameObject.Native(), out var entity))
        {
            if(entity.TryGetCapability<PosingCapability>(out var posingCapability))
            {
                var pose = posingCapability.ExportPose();

                return JsonSerializer.Serialize(pose);
            }
        }
        return null;
    }

    private unsafe bool ResetPose_Impl(IGameObject gameObject, bool clearRedoHistory)
    {
        if(_gPoseService.IsGPosing == false) return false;

        if(_entityManager.TryGetEntity(gameObject.Native(), out var entity))
        {
            if(entity.TryGetCapability<PosingCapability>(out var posingCapability))
            {
                posingCapability.Reset(false, false, clearRedoHistory);

                return true;
            }
        }

        return false;
    }

    private unsafe bool ActorExists_Impl(IGameObject gameObject)
    {
        if(_gPoseService.IsGPosing == false) return false;

        return _entityManager.EntityExists(gameObject.Native());
    }

    private unsafe IGameObject[]? ActorGetAll_Impl()
    {
        if(_gPoseService.IsGPosing == false) return null;

        return _entityManager.TryGetAllActorsAsGameObject().ToArray();
    }

    private unsafe bool SetActorSpeed_Impl(IGameObject actor, float speed)
    {
        if(_gPoseService.IsGPosing == false) return false;

        if(_entityManager.TryGetEntity(actor.Native(), out var entity))
        {
            if(entity.TryGetCapability<ActionTimelineCapability>(out var actionTimeline))
            {
                actionTimeline.SetOverallSpeedOverride(speed);
                return true;
            }
        }

        return false;
    }
    private unsafe float GetActorSpeed_Impl(IGameObject actor)
    {
        if(_gPoseService.IsGPosing == false) return 0;

        if(_entityManager.TryGetEntity(actor.Native(), out var entity))
        {
            if(entity.TryGetCapability<ActionTimelineCapability>(out var actionTimeline))
            {
                return actionTimeline.SpeedMultiplier;
            }
        }

        return 0;
    }

    private unsafe bool FreezActor_Impl(IGameObject actor)
    {
        if(_gPoseService.IsGPosing == false) return false;

        if(_entityManager.TryGetEntity(actor.Native(), out var entity))
        {
            if(entity.TryGetCapability<ActionTimelineCapability>(out var actionTimeline))
            {
                actionTimeline.StopSpeedAndResetTimeline();
                return true;
            }
        }

        return false;
    }
    private unsafe bool UnFreezActor_Impl(IGameObject actor)
    {
        if(_gPoseService.IsGPosing == false) return false;

        if(_entityManager.TryGetEntity(actor.Native(), out var entity))
        {
            if(entity.TryGetCapability<ActionTimelineCapability>(out var actionTimeline))
            {
                actionTimeline.SetOverallSpeedOverride(1);
                return true;
            }
        }

        return false;
    }

    public bool FreezePhysics_Impl()
    {
        if(_gPoseService.IsGPosing == false) return false;

        return _physicsService.FreezeEnable();
    }

    public bool UnFreezePhysics_Impl()
    {
        if(_gPoseService.IsGPosing == false) return false;

        return _physicsService.FreezeRevert();
    }

    public void Dispose()
    {
        _configurationService.OnConfigurationChanged -= OnConfigurationChanged;
        DisposeIPC();
    }
}
