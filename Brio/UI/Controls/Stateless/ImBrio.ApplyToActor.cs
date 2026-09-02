using Brio.Entities;
using Brio.Entities.Actor;
using Brio.Entities.Core;
using Brio.Game.Actor;
using Brio.Game.Actor.Extensions;
using Brio.Game.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using System;

namespace Brio.UI.Controls.Stateless;

public partial class ImBrio
{
    public static void DrawApplyToActor(EntityManager entityManager, Action<ActorEntity> callback)
    {
        if(entityManager.SelectedEntity is null || entityManager.SelectedEntity is not ActorEntity selectedActor)
        {
            DrawSpawnActor(entityManager, callback);

            return;
        }

        if(ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl))
        {
            DrawSpawnActor(entityManager, callback);
        }
        else
        {
            if(ImGui.Button($"Apply To {selectedActor.FriendlyName}"))
            {
                callback?.Invoke(selectedActor);
            }


            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Hold Ctrl to spawn as a new actor");
        }

    }

    private static void DrawSpawnActor(EntityManager entityManager, Action<ActorEntity> callback)
    {
        if(!Brio.TryGetService(out ActorSpawnService spawnService))
        {
            using var _ = ImRaii.Disabled(true);
            ImGui.Button("Unable to Spawn");
        }


        if(ImGui.Button("Spawn As New Actor"))
        {
            if(!spawnService.CreateCharacter(out var character, disableSpawnCompanion: true))
            {
                Brio.Log.Error("Unable to spawn character");
                return;
            }

            // 🔴 RunUntilSatisfied 會逐幀重排最多 100 幀,而 character 的 Address 是建構當下凍結的。
            //    角色在這段期間被銷毀就成了懸空位址,IsReadyToDraw() 會踩到已釋放的記憶體
            //    —— AccessViolationException 在 .NET Core 是 corrupted-state exception,try/catch 攔不到。
            //    抄走索引 + 位址,每一幀由物件表重查(GetObjectAddress 只讀指標陣列,不解參)。
            //    ⚠️ 完成動作裡的 new EntityId(character) 只是把 Address 字串化、不解參,維持原樣。
            if(!Brio.TryGetService(out IObjectTable objectTable))
            {
                Brio.Log.Error("Unable to get the object table");
                return;
            }

            var actorRef = new LiveActorRef(objectTable, character);

            unsafe bool IsReadyToDraw()
            {
                var native = actorRef.Character;
                return native != null && native->IsReadyToDraw();
            }

            Brio.Framework.RunUntilSatisfied(
                IsReadyToDraw,
                (_) =>
                {
                    var entity = entityManager.GetEntity(new EntityId(character));
                    if(entity is not ActorEntity actorEntity)
                    {
                        Brio.Log.Error($"Unable to get actor entity is: {entity?.GetType()} {entity}");
                        return;
                    }

                    callback?.Invoke(actorEntity);
                },
                100,
                dontStartFor: 2
            );
        }
    }
}
