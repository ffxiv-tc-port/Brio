using Brio.Config;
using Brio.Entities;
using Brio.Entities.Actor;
using Brio.Game.Actor.Extensions;
using Brio.Game.Posing;
using Brio.Game.Types;
using Brio.IPC;
using Brio.UI.Widgets.Actor;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static Brio.Game.Actor.ActionTimelineService;

namespace Brio.Capabilities.Actor;

public class ActionTimelineCapability : ActorCharacterCapability
{
    private readonly IFramework _framework;

    /// <summary>
    /// 主執行緒閘門。🔴 <see cref="StopSpeedAndResetTimeline"/> 在 <c>await</c> 之後才 Invoke
    /// 呼叫端交進來的 <c>postStopAction</c>,而 <c>await</c> 的續行在執行緒池上。
    /// </summary>
    private readonly IpcFrameworkGate _gate;

    public unsafe float SpeedMultiplier => SpeedMultiplierOverride ?? Character.Native()->Timeline.OverallSpeed;
    public bool HasSpeedMultiplierOverride => SpeedMultiplierOverride.HasValue;
    public float? SpeedMultiplierOverride { get; private set; }
    public bool IsPaused { get; private set; } = false;

    public bool HasOverride => (SlotedBlendAnimation != 0 || SlotedBaseAnimation != 0) && (HasBaseOverride || HasSpeedMultiplierOverride || DoBaseInterrupt is false || LipsOverride > 0);

    public bool DoBaseInterrupt = true;
    public int SlotedBaseAnimation = 0;
    public int SlotedBlendAnimation = 0;

    public unsafe ushort LipsOverride
    {
        get => Character.Native()->Timeline.LipsOverride;
        set => Character.Native()->Timeline.SetLipsOverrideTimeline(value);
    }

    private readonly Dictionary<ActionTimelineSlots, float> _actionTimelineSlotSpeedOverrides = [];
    private OriginalBaseAnimation? _originalBaseAnimation = null;
    private bool _slotsDirty = false;

    public ActionTimelineCapability(IFramework framework, ActorEntity parent, EntityManager entityManager, PhysicsService physicsService, ConfigurationService configService) : base(parent)
    {
        _framework = framework;
        _gate = new IpcFrameworkGate(framework);

        Widget = new ActionTimelineWidget(this, entityManager, physicsService, configService);
    }

    public unsafe void SetOverallSpeedOverride(float speed)
    {
        SpeedMultiplierOverride = speed;
        Character.Native()->Timeline.OverallSpeed = speed;
    }

    public void ResetOverallSpeedOverride()
    {
        SpeedMultiplierOverride = null;
    }

    public unsafe ActionTimelineUnion GetSlotAction(ActionTimelineSlots slot)
    {
        var timeline = Character.Native()->Timeline.TimelineSequencer.TimelineIds[(int)slot];
        return new ActionTimelineId(timeline);
    }

    public unsafe float GetSlotSpeed(ActionTimelineSlots slot)
    {
        if(_actionTimelineSlotSpeedOverrides.TryGetValue(slot, out float speed))
            return speed;

        return Character.Native()->Timeline.TimelineSequencer.TimelineSpeeds[(int)slot];
    }

    public unsafe void SetSlotSpeedOverride(ActionTimelineSlots slot, float speed)
    {
        _actionTimelineSlotSpeedOverrides[slot] = speed;
        Character.Native()->Timeline.TimelineSequencer.SetSlotSpeed((uint)slot, speed);
        _slotsDirty = true;
    }

    public bool HasSlotSpeedOverride(ActionTimelineSlots slot)
    {
        return _actionTimelineSlotSpeedOverrides.ContainsKey(slot);
    }

    public void ResetSlotSpeedOverride(ActionTimelineSlots slot)
    {
        _actionTimelineSlotSpeedOverrides.Remove(slot);
        _slotsDirty = true;
    }

    public bool CheckAndResetDirtySlots() => _slotsDirty && !(_slotsDirty = false);

    public unsafe void ApplyBaseOverride(ushort actionTimeline, bool interrupt)
    {
        if(_originalBaseAnimation == null)
            _originalBaseAnimation = new(Character.Native()->Mode, Character.Native()->ModeParam, Character.Native()->Timeline.BaseOverride);

        var chara = Character.Native();

        chara->SetMode(CharacterModes.AnimLock, 0);
        chara->Timeline.BaseOverride = actionTimeline;

        if(interrupt)
            BlendTimeline(actionTimeline);
    }

    public async void StopSpeedAndResetTimeline(Action? postStopAction = null, bool resetSpeedAfterAction = false)
    {
        // 🔴 這支是 async void:未處理的例外不會進任何 Task,而是變成行程層級的未處理例外。
        //    而下面 await 的是<帶延遲>的 RunOnTick —— Dalamud 在卸載期對帶延遲的 RunOnTick
        //    回的是 Task.FromCanceled(委派完全不執行,Dalamud/Game/Framework.cs:200-211),
        //    await 它就擲 TaskCanceledException。
        //    🔑 這裡只吞「取消」這一種流程控制,別的例外原樣往外拋 —— 不是拿 try/catch 當防護
        //    (AccessViolationException 在 .NET Core 是 corrupted-state exception,本來就攔不到)。
        try
        {
            await StopSpeedAndResetTimelineCore(postStopAction, resetSpeedAfterAction);
        }
        catch(OperationCanceledException)
        {
            Brio.Log.Information("StopSpeedAndResetTimeline 在 Dalamud 卸載期被取消,動畫時間軸與速度未還原(遊戲正在關閉)。");
        }
    }

    /// <summary>
    /// <see cref="StopSpeedAndResetTimeline"/> 的本體。<b>內容逐字未動</b>,抽成 async Task
    /// 只是為了讓上面那層 async void 能把卸載期的取消攔下來。
    /// </summary>
    private async Task StopSpeedAndResetTimelineCore(Action? postStopAction, bool resetSpeedAfterAction)
    {
        Brio.Log.Verbose($"StopSpeedAndResetTimeline {postStopAction is not null} {resetSpeedAfterAction}");

        var oldSpeed = SpeedMultiplier;

        SetOverallSpeedOverride(0);

        Brio.Log.Verbose($"SetOverallSpeedOverride {oldSpeed} {SpeedMultiplier}");

        await _framework.RunOnTick(() =>
        {
            unsafe
            {
                // 🔴 這段是 4 幀之後才跑的。Character(= ActorEntity.GameObject)的 Address 是建構當下
                //    凍結的,角色若已經消失就是懸空位址,Character.Native() 之後的每一次解參都會踩到
                //    已釋放的記憶體 —— AccessViolationException 在 .NET Core 是 corrupted-state
                //    exception,try/catch 攔不到,只能事前擋。IsGameObjectAlive 只讀物件表自己的指標
                //    陣列(GetObjectAddress),不解參任何存下來的位址,是安全的存活檢查。
                if(Actor.IsGameObjectAlive == false)
                    return;

                var drawObj = Character.Native()->GameObject.DrawObject;
                if(drawObj == null)
                    return;

                if(drawObj->Object.GetObjectType() != ObjectType.CharacterBase)
                    return;

                var charaBase = (CharacterBase*)drawObj;
                if(charaBase->Skeleton == null)
                    return;

                var skeleton = charaBase->Skeleton;
                for(int p = 0; p < skeleton->PartialSkeletonCount; ++p)
                {
                    var partial = &skeleton->PartialSkeletons[p];

                    var animatedSkele = partial->GetHavokAnimatedSkeleton(0);
                    if(animatedSkele == null)
                        continue;

                    for(int c = 0; c < animatedSkele->AnimationControls.Length; ++c)
                    {
                        var control = animatedSkele->AnimationControls[c].Value;
                        if(control == null)
                            continue;

                        var binding = control->hkaAnimationControl.Binding;
                        if(binding.ptr == null)
                            continue;

                        var anim = binding.ptr->Animation.ptr;
                        if(anim == null)
                            continue;

                        if(control->PlaybackSpeed == 0)
                        {
                            control->hkaAnimationControl.LocalTime = 0;
                            Brio.Log.Verbose($"hkaAnimationControl");
                        }
                    }
                }
            }
        }, delayTicks: 4);

        // 🔴 上面那個 await 的續行在執行緒池上,不在遊戲主執行緒上(Dalamud 這個 pin 沒有安裝
        //    任何 SynchronizationContext,而 UI／事件回呼進來時 TaskScheduler.Current 是 Default)。
        //    唯一真的會傳 postStopAction 的呼叫端是 PosingCapability.ImportPose,它包的是
        //    ImportPose_Internal —— 那支會走 ModelPosing.ImportModelPose → ModelTransformService
        //    的 GetTransform／SetTransform(IGameObject),全是解原生指標與寫入。
        //    在執行緒池上做就是 AccessViolationException,而 AVE 在 .NET Core 是
        //    corrupted-state exception,try/catch 攔不到。
        //    📌 閘門是 await 的,所以與後面 resetSpeedAfterAction 那一段的先後順序不變。
        await _gate.RunAsync("ActionTimelineCapability.postStopAction", () =>
        {
            // 🔴 存活檢查:上面那個 delayTicks: 4 已經跨了 4 幀以上,角色可能已經離開物件表。
            //    唯一真的會傳 postStopAction 的呼叫端(PosingCapability.ImportPose 包的
            //    ImportPose_Internal)整條路徑都在解 Character 的原生指標,而那個位址是建構
            //    當下凍結的 —— 角色消失之後就是已釋放的記憶體,回到框架執行緒防不了。
            //    (ImportPose_Internal 自己開頭也有同一個閘門;這裡擋的是所有 postStopAction。)
            if(Actor.IsGameObjectAlive == false)
            {
                Brio.Log.Information("角色在等待動畫停止期間消失,略過後續動作(姿勢套用)。");
                return;
            }

            postStopAction?.Invoke();
        });

        Brio.Log.Verbose($"postStopAction Invoke: {postStopAction is not null}");

        if(resetSpeedAfterAction)
        {
            await _framework.RunOnTick(() =>
            {
                // 同上:又過了 2 幀,SetOverallSpeedOverride 與 SpeedMultiplier 都會解參 Character.Native()。
                if(Actor.IsGameObjectAlive == false)
                    return;

                SetOverallSpeedOverride(oldSpeed);

                Brio.Log.Verbose($"SetOverallSpeedOverride {SpeedMultiplier}");
            }, delayTicks: 2);
        }
    }

    public unsafe void ResetBaseOverride()
    {
        if(_originalBaseAnimation == null)
            return;

        var chara = Character.Native();

        chara->Timeline.BaseOverride = _originalBaseAnimation.Value.OriginalTimeline;
        chara->Mode = _originalBaseAnimation.Value.OriginalMode;
        chara->ModeParam = _originalBaseAnimation.Value.OriginalInput;

        _originalBaseAnimation = null;

        BlendTimeline(3);
    }

    public bool HasBaseOverride => _originalBaseAnimation != null;

    public unsafe void BlendTimeline(ushort actionTimeline)
    {
        Character.Native()->Timeline.TimelineSequencer.PlayTimeline(actionTimeline);
    }

    public void Stop()
    {
        if(HasBaseOverride)
        {
            ResetBaseOverride();
            ResetOverallSpeedOverride();
        }
    }

    public void Reset()
    {
        DoBaseInterrupt = true;

        SlotedBaseAnimation = 0;
        SlotedBlendAnimation = 0;
        LipsOverride = 0;

        IsPaused = false;

        ResetBaseOverride();
        ResetOverallSpeedOverride();
    }

    public override void Dispose()
    {
        SpeedMultiplierOverride = null;
        _actionTimelineSlotSpeedOverrides.Clear();
        ResetBaseOverride();

        base.Dispose();
    }

    public static ActionTimelineCapability? CreateIfEligible(IServiceProvider provider, ActorEntity entity)
    {
        if(entity.GameObject is ICharacter)
            return ActivatorUtilities.CreateInstance<ActionTimelineCapability>(provider, entity);

        return null;
    }

    public record struct OriginalBaseAnimation(CharacterModes OriginalMode, byte OriginalInput, ushort OriginalTimeline);
}
