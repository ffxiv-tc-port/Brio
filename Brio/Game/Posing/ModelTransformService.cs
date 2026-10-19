using Brio.Capabilities.Posing;
using Brio.Entities;
using Brio.Game.Actor;
using Brio.Game.Actor.Extensions;
using Brio.Game.GPose;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using System;
using static Brio.Game.Actor.ActorRedrawService;
using StructsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Brio.Game.Posing;

public unsafe class ModelTransformService : IDisposable
{
    public delegate void SetPositionDelegate(StructsGameObject* gameObject, float x, float y, float z);
    private readonly Hook<SetPositionDelegate>? _setPositionHook;

    private readonly EntityManager _entityManager;
    private readonly GPoseService _gPoseService;
    private readonly ActorRedrawService _actorRedrawService;

    public ModelTransformService(EntityManager entityManager, GPoseService gPoseService, ActorRedrawService actorRedrawService, IGameInteropProvider hooking)
    {
        _entityManager = entityManager;
        _gPoseService = gPoseService;
        _actorRedrawService = actorRedrawService;

        // 🔴 CS 解不出 SetPosition 時 .Value 是 0,裸 HookFromAddress(0) 會擲例外;
        // 而 Brio 的服務是 DI 單例 ⇒ 那等於整個外掛載不起來。
        var setPositionAddress = (nint)StructsGameObject.Addresses.SetPosition.Value;
        if(setPositionAddress == nint.Zero)
            global::Brio.Core.NativeBinding.Fail("模型位移 GameObject::SetPosition", "FFXIVClientStructs 未能在本客戶端解析此位址");
        _setPositionHook = global::Brio.Core.NativeBinding.CreateHook<SetPositionDelegate>(hooking, setPositionAddress, UpdatePositionDetour, "模型位移 GameObject::SetPosition");

        _actorRedrawService.ActorRedrawEvent += OnActorRedraw;
    }

    public unsafe Transform GetTransform(IGameObject go)
    {
        var native = go.Native();
        var drawObject = native->DrawObject;
        if(drawObject != null)
        {
            return *(Transform*)(&drawObject->Object.Position);
        }
        else
        {
            return new Transform()
            {
                Position = native->Position
            };
        }
        ;
    }

    public unsafe void SetTransform(IGameObject go, Transform transform) => SetTransform(go.Native(), transform);

    public unsafe void SetTransform(StructsGameObject* native, Transform transform)
    {
        var drawObject = native->DrawObject;

        if(drawObject != null)
        {
            *(Transform*)(&drawObject->Object.Position) = transform;
        }
    }

    private void UpdatePositionDetour(StructsGameObject* gameObject, float x, float y, float z)
    {
        if(_gPoseService.IsGPosing)
        {
            if(_entityManager.TryGetEntity(gameObject, out var entity))
            {
                if(entity.TryGetCapability<ModelPosingCapability>(out var transformCapability))
                {
                    if(transformCapability.OverrideTransform.HasValue)
                    {
                        var transform = transformCapability.OverrideTransform.Value;
                        SetTransform(gameObject, transform);
                        return;
                    }
                }
            }
        }

        // ⚠️ 這個 detour 也會被 OnActorRedraw 直接呼叫(不經 hook),
        //    所以 hook 沒建立時仍會走到這一行 —— 必須真的判 null,不能只加 !。
        _setPositionHook?.Original(gameObject, x, y, z);
    }

    private void OnActorRedraw(IGameObject go, RedrawStage stage)
    {
        if(go is not null)
            if(stage == RedrawStage.After)
                UpdatePositionDetour((StructsGameObject*)go.Address, go.Position.X, go.Position.Y, go.Position.Z);
    }


    public void Dispose()
    {
        _setPositionHook?.Dispose();
        _actorRedrawService.ActorRedrawEvent -= OnActorRedraw;
    }
}
