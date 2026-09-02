using Brio.Capabilities.Actor;
using Brio.Capabilities.Posing;
using Brio.Capabilities.World;
using Brio.Config;
using Brio.Core;
using Brio.Entities;
using Brio.Entities.Actor;
using Brio.Entities.Core;
using Brio.Entities.World;
using Brio.Files;
using Brio.Game.Actor.Appearance;
using Brio.Game.Actor.Extensions;
using Brio.Game.Camera;
using Brio.Game.Core;
using Dalamud.Plugin.Services;
using System.Threading.Tasks;

namespace Brio.Game.Scene;

public class SceneService(EntityManager _entityManager, VirtualCameraManager _virtualCameraManager, IFramework _framework, IObjectTable _objectTable)
{
    public bool IsLoading { get; private set; }

    public SceneFile GenerateSceneFile()
    {
        SceneFile sceneFile = new();

        var entity = _entityManager.GetEntity<ActorContainerEntity>("actorContainer")!;

        foreach(var child in entity.Children)
        {
            if(child is ActorEntity actorEntity)
            {
                sceneFile.Actors.Add(actorEntity);
            }
        }

        foreach(var camera in _virtualCameraManager.GetAllCameras())
        {
            sceneFile.GameCameras.Add(new GameCameraFile { Camera = camera.VirtualCamera, CameraType = camera.CameraType });
        }

        var environmentEntity = _entityManager.GetEntity<EnvironmentContainerEntity>("environment");

        if(environmentEntity is not null)
        {
            var wc = environmentEntity.GetCapability<TimeWeatherCapability>();
            var wrc = environmentEntity.GetCapability<WorldRenderingCapability>();
            sceneFile.EnvironmentData = new EnvironmentData
            {
                CurrentWeather = wc.WeatherService.CurrentWeather,
                IsTimeFrozen = wc.TimeService.IsTimeFrozen,
                EorzeaTime = wc.TimeService.EorzeaTime,
                DayOfMonth = wc.TimeService.DayOfMonth,
                MinuteOfDay = wc.TimeService.MinuteOfDay,
                IsWaterFrozen = wrc.WorldRenderingService.IsWaterFrozen
            };
        }

        return sceneFile;
    }

    public unsafe void LoadScene(SceneFile sceneFile, bool destroyAll = false)
    {
        IsLoading = true;

        ActorContainerEntity actorContainerEntity = _entityManager.GetEntity<ActorContainerEntity>("actorContainer")!;

        var actorCapability = actorContainerEntity.GetCapability<ActorContainerCapability>();

        if(ConfigurationService.Instance.Configuration.SceneDestoryActorsBeforeImport || destroyAll)
        {
            actorCapability.DestroyAll();
            _virtualCameraManager.DestroyAll();
        }

        foreach(ActorFile actorFile in sceneFile.Actors)
        {
            if(actorFile.IsProp)
            {
                var (actorId, actor) = actorCapability.CreateProp(false);

                // 🔴 actor 的 Address 是建構當下凍結的,而下面這個條件會逐幀重跑最多 100 幀。
                //    角色在這段期間消失就成了懸空位址,actor.Native()->IsReadyToDraw() 會踩到已釋放的
                //    記憶體(AccessViolationException 在 .NET Core 連 try/catch 都攔不到)。
                //    抄走索引 + 位址,每一幀由物件表重查;完成動作只用 actorId,本來就不解參。
                var actorRef = new LiveActorRef(_objectTable, actor);

                _framework.RunUntilSatisfied(
                    () =>
                    {
                        var native = actorRef.Character;
                        return native != null && native->IsReadyToDraw();
                    },
                    (__) =>
                    {
                        _ = LoadProp(actorId, actorFile);
                    },
                    100,
                    dontStartFor: 2
                );
            }
            else
            {
                var (actorId, actor) = actorCapability.CreateCharacter(actorFile.HasChild, false, forceSpawnActorWithoutCompanion: !actorFile.HasChild);

                // 🔴 actor 的 Address 是建構當下凍結的,而下面這個條件會逐幀重跑最多 100 幀。
                //    角色在這段期間消失就成了懸空位址,actor.Native()->IsReadyToDraw() 會踩到已釋放的
                //    記憶體(AccessViolationException 在 .NET Core 連 try/catch 都攔不到)。
                //    抄走索引 + 位址,每一幀由物件表重查;完成動作只用 actorId,本來就不解參。
                var actorRef = new LiveActorRef(_objectTable, actor);

                _framework.RunUntilSatisfied(
                    () =>
                    {
                        var native = actorRef.Character;
                        return native != null && native->IsReadyToDraw();
                    },
                    (__) =>
                    {
                        _ = ApplyDataToActor(actorId, actorFile);
                    },
                    100,
                    dontStartFor: 2
                );
            }
        }

        foreach(GameCameraFile item in sceneFile.GameCameras)
        {
            _virtualCameraManager.CreateCamera(item.CameraType, false, false, item.Camera);
        }

        if(sceneFile.EnvironmentData is not null)
        {
            var environmentEntity = _entityManager.GetEntity<EnvironmentContainerEntity>("environment")!;
            var wc = environmentEntity.GetCapability<TimeWeatherCapability>();
            var wrc = environmentEntity.GetCapability<WorldRenderingCapability>();

            wc.WeatherService.CurrentWeather = sceneFile.EnvironmentData.CurrentWeather;
            wc.TimeService.IsTimeFrozen = sceneFile.EnvironmentData.IsTimeFrozen;
            wc.TimeService.EorzeaTime = sceneFile.EnvironmentData.EorzeaTime;
            wc.TimeService.DayOfMonth = sceneFile.EnvironmentData.DayOfMonth;
            wc.TimeService.MinuteOfDay = sceneFile.EnvironmentData.MinuteOfDay;
            wrc.WorldRenderingService.IsWaterFrozen = sceneFile.EnvironmentData.IsWaterFrozen;
        }

        _framework.RunOnTick(() =>
        {
            IsLoading = false;
        }, delayTicks: 250);
    }

    private async Task LoadProp(EntityId actorId, ActorFile actorFile)
    {
        // ⚠️ 本函式是 LoadScene 那個 RunUntilSatisfied 的完成回呼,最可能在建立道具之後 100 幀才跑。
        //    使用者這段期間把道具刪掉時 GetEntity 會回 null,原本的 ! 會變成 NullReferenceException,
        //    而呼叫端是 fire-and-forget(`_ = LoadProp(...)`)會把它靜默吞掉。改成明說並結束。
        var attachedActor = _entityManager.GetEntity<ActorEntity>(actorId);
        if(attachedActor is null)
        {
            Brio.Log.Info("場景載入:道具在準備就緒之前就已經被移除,略過套用。");
            return;
        }

        var modelCapability = attachedActor.GetCapability<ModelPosingCapability>();
        var appearanceCapability = attachedActor.GetCapability<ActorAppearanceCapability>();

        await _framework.RunOnTick(async () =>
        {
            // 🔴 又過了 2 幀。modelCapability.Transform 的 getter 與 setter 都會走
            //    ModelTransformService.GetTransform / SetTransform(GameObject) → go.Native() 解參並寫入,
            //    而 GameObject.Address 是建構當下凍結的 ⇒ 道具已消失時就是懸空位址。
            //    AccessViolationException 在 .NET Core 是 corrupted-state exception,try/catch 攔不到。
            //    IsGameObjectAlive 只讀物件表自己的指標陣列(GetObjectAddress),不解參任何存下來的位址。
            if(attachedActor.IsGameObjectAlive == false)
                return;

            if(actorFile.PropData is not null)
                modelCapability.Transform += actorFile.PropData.PropTransformDifference;

            await _framework.RunOnTick(async () =>
            {
                // 再過 10 幀(累計 12)。SetAppearance 內部也有同一道閘門,這裡擋的是「連叫都不要叫」。
                if(attachedActor.IsGameObjectAlive == false)
                    return;

                await appearanceCapability.SetAppearance(actorFile.AnamnesisCharaFile, AppearanceImportOptions.Weapon);
                await _framework.RunOnTick(() =>
                {
                    // 再過 10 幀(累計 22)。AttachWeapon 本體開頭已有 IsGameObjectAlive 閘門。
                    appearanceCapability.AttachWeapon();
                }, delayTicks: 10);
            }, delayTicks: 10);
        }, delayTicks: 2);
    }

    private async Task ApplyDataToActor(EntityId actorId, ActorFile actorFile)
    {
        // ⚠️ 同 LoadProp:本函式是 LoadScene 那個 RunUntilSatisfied 的完成回呼,最多在建立角色之後
        //    100 幀才跑,GetEntity 可能已經回 null(原本的 ! 會變成被 fire-and-forget 靜默吞掉的 NRE)。
        var attachedActor = _entityManager.GetEntity<ActorEntity>(actorId);
        if(attachedActor is null)
        {
            Brio.Log.Info("場景載入:角色在準備就緒之前就已經被移除,略過套用。");
            return;
        }

        var posingCapability = attachedActor.GetCapability<PosingCapability>();
        var appearanceCapability = attachedActor.GetCapability<ActorAppearanceCapability>();
        var actionTimeline = attachedActor.GetCapability<ActionTimelineCapability>();

        attachedActor.FriendlyName = actorFile.Name; // 純受控狀態,不解參

        // 🔴 SetOverallSpeedOverride 會寫 Character.Native()->Timeline.OverallSpeed。
        //    本函式最快也是建立角色之後好幾十幀才跑到這裡,GameObject.Address 是建構當下凍結的
        //    ⇒ 角色已消失時就是懸空寫入,AccessViolationException 在 .NET Core 是 corrupted-state
        //    exception,try/catch 攔不到。IsGameObjectAlive 只讀物件表自己的指標陣列,不解參存下來的位址。
        if(attachedActor.IsGameObjectAlive == false)
        {
            Brio.Log.Info("場景載入:角色已經不在物件表裡,略過套用。");
            return;
        }

        actionTimeline.SetOverallSpeedOverride(0);

        await _framework.RunOnTick(async () =>
        {
            // 又過了 1 幀:SetAppearance 會整條解參 Character(內部也有同一道閘門)。
            if(attachedActor.IsGameObjectAlive == false)
                return;

            // only import shaders if the appearance is valid and it's not a prop (characters only)
            if(actorFile.AnamnesisCharaFile.IsExtendedAppearanceValid && !actorFile.IsProp)
            {
                BrioUtilities.ImportShadersFromFile(ref appearanceCapability._modelShaderOverride, actorFile.AnamnesisCharaFile);
                await appearanceCapability.SetAppearance(actorFile.AnamnesisCharaFile, AppearanceImportOptions.All);
            }
            else if(actorFile.IsProp) // if it's a prop, only import the appearance
            {
                await appearanceCapability.SetAppearance(actorFile.AnamnesisCharaFile, AppearanceImportOptions.Customize);
            }
            else // chars with invalid extended appearances
            {
                await appearanceCapability.SetAppearance(actorFile.AnamnesisCharaFile, AppearanceImportOptions.Default);
            }

            await _framework.RunOnTick(async () =>
            {
                // 🔴 再過 10 幀。這一段有三處會解參 GameObject:
                //    posingCapability.ImportPose → ActionTimelineCapability.SpeedMultiplier /
                //      SetOverallSpeedOverride(兩者都是 Character.Native()->Timeline...)
                //    companionCapability.SetCompanion → ActorSpawnService.CreateCompanion(Character, ...)
                //    (延後之後的 ImportPose_Internal 自己另有閘門)
                if(attachedActor.IsGameObjectAlive == false)
                    return;

                bool mountPose = false;
                if(actorFile.Child is not null && actorFile.Child.Companion.Kind == Types.CompanionKind.Mount)
                    mountPose = true;

                if(mountPose == false)
                    posingCapability.ImportPose(actorFile.PoseFile, asScene: true, asProp: actorFile.IsProp);

                if(attachedActor.HasCapability<CompanionCapability>() == true && actorFile.HasChild && actorFile.Child is not null)
                {
                    var companionCapability = attachedActor.GetCapability<CompanionCapability>();

                    companionCapability.SetCompanion(actorFile.Child.Companion);

                    await _framework.RunOnTick(() =>
                    {
                        // 🔴 又過了 1 幀。GetCompanionAsEntity 會做 Character.HasSpawnedCompanion() 與
                        //    &Character.Native()->CompanionObject->Character.GameObject 兩次解參,
                        //    mountPose 那條的 ImportPose 也是。宿主已消失時全是懸空位址。
                        if(attachedActor.IsGameObjectAlive == false)
                            return;

                        if(actorFile.Child.PoseFile is not null)
                        {
                            var companionEntity = companionCapability.GetCompanionAsEntity();

                            if(companionEntity is not null && companionEntity.TryGetCapability<PosingCapability>(out var posingCapability))
                            {
                                posingCapability.ImportPose(actorFile.Child.PoseFile, asScene: true, freezeOnLoad: true, asProp: actorFile.IsProp);
                            }
                        }

                        if(mountPose == true)
                            posingCapability.ImportPose(actorFile.PoseFile, asScene: true, asProp: actorFile.IsProp);
                    });
                }
            }, delayTicks: 10); // I don't like having to set delayTicks to this but I don't think I have another way without more rework
        });
    }
}
