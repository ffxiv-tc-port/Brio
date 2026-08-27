using Brio.Entities.Actor;
using Brio.Entities.Core;
using Brio.Game.Actor;
using Brio.Game.Actor.Extensions;
using Brio.Game.Core;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Brio.Entities;

public unsafe class EntityActorManager : IDisposable
{
    private readonly EntityManager _entityManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly ObjectMonitorService _monitorService;
    private readonly IObjectTable _objects;
    private readonly IFramework _framework;

    private readonly ActorContainerEntity _actorContainerEntity;
    private readonly ActorSpawnService _actorSpawnService;

    public EntityActorManager(EntityManager entityManager, ActorSpawnService actorSpawnService, IServiceProvider serviceProvider, ObjectMonitorService monitorService, IObjectTable objects, IFramework framework)
    {
        _entityManager = entityManager;
        _serviceProvider = serviceProvider;
        _monitorService = monitorService;
        _objects = objects;
        _framework = framework;
        _actorSpawnService = actorSpawnService;

        _monitorService.CharacterInitialized += OnCharacterInitialized;
        _monitorService.CharacterDestroyed += OnCharacterDestroyed;

        _actorContainerEntity = ActivatorUtilities.CreateInstance<ActorContainerEntity>(_serviceProvider);
    }

    public void AttachContainer()
    {
        _entityManager.AttachEntity(_actorContainerEntity, null);

        PopulateExistingActors();
    }

    private void PopulateExistingActors()
    {
        foreach(var go in _objects)
        {
            // 🔴 IObjectTable 的索引子/列舉回傳的是**每個槽預先配置、每次讀取就地改寫 Address**
            //    的共用包裝物件(Dalamud ObjectTable.CachedEntry.Update)。
            //    ActorEntity 會把這個物件長期持有,而 EntityId 又是在建構當下由 Address 字串化而來
            //    ⇒ 之後只要有人讀同一個槽,已存的 ActorEntity.GameObject.Address 就會靜默指向別人,
            //    但它的 EntityId 還停在舊位址(對不上 ⇒ 移除不掉,而姿勢寫入會寫到別的角色身上)。
            //    CreateObjectReference 會配一個獨立實例,與 OnCharacterInitialized 走的是同一條路。
            var owned = _objects.CreateObjectReference(go.Address);
            if(owned is null)
                continue;

            AttachActor(owned, _actorContainerEntity);
        }
    }

    private void AttachActor(IGameObject go, Entity parent)
    {
        if(_entityManager.TryGetEntity(new EntityId(go), out var entity))
        {
            // Already attached to the correct parent
            if(parent.Equals(entity.Parent))
                return;
        }
        else
        {
            // Only characters
            if(!go.Native()->IsCharacter())
                return;

            if(go.ObjectKind == ObjectKind.Ornament) return;

            // TODO: We should allow manipulation of overworld actors too
            if(!go.IsGPose())
                return;

            entity = ActivatorUtilities.CreateInstance<ActorEntity>(_serviceProvider, go);
        }
        entity.SetSpawnFlags(_actorSpawnService.GetSpawnFlagsByIndex((ushort)(go.ObjectIndex - 200)));

        _entityManager.AttachEntity(entity, parent, true);


        // This is ew, but we need to handle companions here for now.
        // This would be a stack overflow but the parenting check above prevents it.
        HandleCompanions(entity, true);
    }

    private void DetachActor(IGameObject actor)
    {
        if(_entityManager.TryGetEntity(new EntityId(actor), out var entity))
        {
            _entityManager.DetachEntity(entity, true);
        }
    }

    private void HandleCompanions(Entity entity, bool checkParent)
    {
        if(entity is ActorEntity actorEntity)
        {
            var currentActor = actorEntity.GameObject;

            if(currentActor is ICharacter character)
            {
                if(character.HasSpawnedCompanion())
                {
                    var companion = character.Native()->CompanionObject;
                    if(companion != null)
                    {
                        var companionObject = _objects.CreateObjectReference((nint)companion);
                        if(companionObject != null)
                        {
                            AttachActor(companionObject, entity);
                        }
                    }
                    return;
                }

                if(checkParent)
                {
                    var maybeParentId = currentActor.ObjectIndex - 1;
                    if(maybeParentId < 0)
                        return;

                    var maybeParent = _objects[maybeParentId];
                    if(maybeParent == null)
                        return;

                    _entityManager.TryGetEntity(new EntityId(maybeParent), out var maybeParentEntity);

                    if(maybeParentEntity == null)
                        return;

                    HandleCompanions(maybeParentEntity, false);
                }
            }
        }
    }

    private void OnCharacterDestroyed(NativeCharacter* chara)
    {
        var go = _objects.CreateObjectReference((nint)chara);
        if(go != null)
            DetachActor(go);
    }

    private void OnCharacterInitialized(NativeCharacter* chara)
    {
        // We wait for one frame on create to ensure that the actor is fully initialized
        _framework.RunOnTick(() =>
        {
            var go = _objects.CreateObjectReference((nint)chara);
            if(go != null)
                AttachActor(go, _actorContainerEntity);
        });
    }


    public void Dispose()
    {
        _monitorService.CharacterInitialized -= OnCharacterInitialized;
        _monitorService.CharacterDestroyed -= OnCharacterDestroyed;
    }
}
