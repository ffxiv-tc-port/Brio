using Brio.Game.Actor.Extensions;
using Brio.Game.Core;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using System;
using System.Threading.Tasks;

using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Brio.Game.Actor;

public class ActorRedrawService(IFramework framework, IObjectTable objectTable)
{
    public delegate void ActorRedrawEventDelegate(IGameObject go, RedrawStage stage);

    public event ActorRedrawEventDelegate? ActorRedrawEvent;

    private readonly IFramework _framework = framework;
    private readonly IObjectTable _objectTable = objectTable;

    public Task<RedrawResult> RedrawObjectByIndex(int objectIndex)
    {
        var actor = _objectTable[objectIndex];
        if(actor == null)
            return Task.FromResult(RedrawResult.Failed);

        return Redraw(actor);
    }

    public async Task<RedrawResult> Redraw(IGameObject go)
    {
        // 🔴 呼叫端不一定是在「取得 go 的那一幀」呼叫進來的:
        //    ActorAppearanceService.SetCharacterAppearance 在中途 await 之後才呼叫本函式。
        //    IGameObject 的 Address 是建構當下凍結的,角色消失後就是懸空位址,
        //    連 go.ObjectIndex(第一行 log)都是解參。先由物件表確認它還指向表裡的同一個物件;
        //    確認之後、同一個呼叫堆疊之內的解參才是安全的
        //    (GetObjectAddress 只讀物件表自己的指標陣列,不解參任何存下來的位址)。
        var actorRef = LiveActorRef.FromAddress(_objectTable, go.Address);
        if(actorRef.IsAlive == false)
        {
            Brio.Log.Info("Brio redraw 略過:目標角色已經不在物件表裡。");
            return RedrawResult.Failed;
        }

        // 上一行剛確認過物件還在表裡,所以這次解參安全;之後的 log 一律用這個抄下來的值,
        // 不要在 await 之後再讀一次 go.ObjectIndex。
        var objectIndex = go.ObjectIndex;

        Brio.Log.Info($"Beginning Brio redraw on gameobject {objectIndex}...");
        DisableDraw(go);
        try
        {
            ActorRedrawEvent?.Invoke(go, RedrawStage.Before);
            await DrawWhenReady(go);
            await WaitForDrawing(go);

            // 🔴 到這裡已經跨過最多 200 幀。訂閱者(ModelTransformService.OnActorRedraw)會讀 go.Position
            //    並把 go.Address 直接當原生指標傳給 SetPosition ⇒ 角色已消失時就是懸空解參 + 懸空寫入。
            //    AccessViolationException 在 .NET Core 是 corrupted-state exception,下面的 catch 攔不到。
            if(actorRef.IsAlive == false)
            {
                Brio.Log.Info($"Brio redraw 未完成:gameobject {objectIndex} 在重繪期間離開物件表。");
                return RedrawResult.Failed;
            }

            ActorRedrawEvent?.Invoke(go, RedrawStage.After);
            Brio.Log.Debug($"Brio redraw complete on gameobject {objectIndex}.");
            return RedrawResult.Full;
        }
        catch(Exception e)
        {
            Brio.Log.Error(e, $"Brio redraw failed on gameobject {objectIndex}.");
            return RedrawResult.Failed;
        }
    }

    public async Task RedrawAndWait(IGameObject go)
    {
        // 🔴 這個迴圈最多跨 3 秒、每一圈都跨過 await。go 的 Address 是建構當下凍結的,
        //    角色若在這段期間消失,IsDrawing(go) 會解參懸空位址;go.IsValid() 只檢查有沒有登入
        //    (本 pin 的 Dalamud/Game/ClientState/Objects/Types/GameObject.cs:170-177),擋不住這件事,
        //    而 AccessViolationException 在 .NET Core 是 corrupted-state exception,try/catch 攔不到。
        //    改成只抄走位址,每一圈由物件表重新確認它還在表裡之後才解參。
        //    (FromAddress 只讀包裝物件自己的 Address 欄位,建構這一步本身也不解參。)
        //    ⚠️ 這一步要放在讀 go.ObjectIndex 之前 —— 呼叫端(CharacterHandlerService.Revert)本身就
        //    可能是在數次 await 之後才把包裝交進來的。
        var actorRef = LiveActorRef.FromAddress(_objectTable, go.Address);
        if(actorRef.IsAlive == false)
        {
            Brio.Log.Info("Brio RedrawAndWait 略過:目標角色已經不在物件表裡。");
            return;
        }

        var objectIndex = go.ObjectIndex;

        Brio.Log.Info($"Beginning Brio RedrawAndWait on gameobject {objectIndex}...");
        try
        {
            DisableDraw(go);

            _ = DrawWhenReady(go);

            var start = DateTime.Now;
            bool stillAlive = true;
            do
            {
                var state = await _framework.RunOnFrameworkThread(() => GetDrawState(actorRef));

                if(state.Drawing)
                {
                    Brio.Log.Debug($"Brio RedrawAndWait complete on gameobject {objectIndex}.");

                    return;
                }

                stillAlive = state.Alive;

                await Task.Delay(200);
            } while(stillAlive && (DateTime.Now - start).TotalSeconds < 3);
        }
        catch(Exception e)
        {
            Brio.Log.Error(e, $"Brio RedrawAndWait failed on gameobject {objectIndex}.");
        }
    }

    /// <summary>
    /// 由物件表重查位址之後才判斷有沒有在繪製。Alive = 物件還在物件表裡;Drawing = RenderFlags 為 0。
    /// 物件已經不在了就完全不解參,回傳 (false, false)。
    /// </summary>
    private unsafe (bool Alive, bool Drawing) GetDrawState(LiveActorRef actorRef)
    {
        var native = actorRef.GameObject;
        if(native == null)
            return (false, false);

        return (true, native->RenderFlags == 0x00);
    }

    /// <summary>
    /// 由物件表重新確認呼叫端傳進來的包裝還指向表裡的同一個物件,是才回傳原生指標,否則回 <c>null</c>。
    ///
    /// <para>
    /// 🔴 下面三支是 public 的,呼叫端可能在拿到 <see cref="IGameObject"/> 好幾幀之後才傳進來
    /// (<c>Redraw</c> / <c>RedrawAndWait</c> 自己就是這樣被呼叫的),而包裝的 <c>Address</c> 是建構當下
    /// 凍結的、永不重新解析。原本這裡寫的 <c>go.IsValid()</c> 不是防護 —— 本 pin 的 Dalamud
    /// <c>Game/ClientState/Objects/Types/GameObject.cs:170-177</c> 逐字是
    /// <c>if (actor == null) return false; return playerState.IsLoaded == true;</c>,只檢查有沒有登入。
    /// AccessViolationException 在 .NET Core 是 corrupted-state exception,<c>try/catch</c> 攔不到。
    /// </para>
    ///
    /// <para>
    /// <c>go.Address</c> 只讀包裝物件自己的欄位、<c>GetObjectAddress</c> 只讀物件表自己的指標陣列,
    /// 兩者都不解參任何存下來的位址,所以這個查詢本身永遠安全。
    /// 拿到指標之後<b>只能在同一個呼叫堆疊之內用完</b>,不可以再帶過幀。
    /// </para>
    /// </summary>
    private unsafe NativeGameObject* ResolveLive(IGameObject go)
        => LiveActorRef.FromAddress(_objectTable, go.Address).GameObject;

    public unsafe bool IsDrawing(IGameObject go)
    {
        var native = ResolveLive(go);
        if(native is null) return false;
        return native->RenderFlags == 0x00;
    }

    public unsafe void DisableDraw(IGameObject go)
    {
        var native = ResolveLive(go);
        if(native is null)
            return;

        native->DisableDraw();
    }

    public unsafe void EnableDraw(IGameObject go)
    {
        var native = ResolveLive(go);
        if(native is null)
            return;

        native->EnableDraw();
    }

    public unsafe Task DrawWhenReady(IGameObject go)
    {
        // 🔴 RunUntilSatisfied 會逐幀重排最多 100 幀。go 的 Address 是建構當下凍結的,
        //    角色在這段期間消失就成了懸空位址;原本的 go.IsValid() 只檢查有沒有登入,對懸空位址零作用。
        //    改成只抄走位址,之後每一幀由物件表重查它還在不在(GetObjectAddress 只讀指標陣列,不解參)。
        //    (FromAddress 只讀包裝物件自己的 Address 欄位,建構這一步本身也不解參。)
        var actorRef = LiveActorRef.FromAddress(_objectTable, go.Address);

        return _framework.RunUntilSatisfied(
           () =>
           {
               var native = actorRef.GameObject;
               if(native == null)
                   return false;

               return native->IsReadyToDraw();
           },
           (_) =>
           {
               // 與原本的 EnableDraw(go) 等價,但改成從重查到的位址呼叫。
               var native = actorRef.GameObject;
               if(native != null)
                   native->EnableDraw();
           },
           100,
           dontStartFor: 2
       );
    }

    public unsafe Task WaitForDrawing(IGameObject go)
    {
        // 🔴 同 DrawWhenReady:不要把 go 帶進逐幀重排的回呼,只抄走位址、每一幀由物件表重查。
        var actorRef = LiveActorRef.FromAddress(_objectTable, go.Address);

        return _framework.RunUntilSatisfied(
           () =>
           {
               var native = actorRef.GameObject;
               if(native == null)
                   return false;

               var drawObject = native->DrawObject;
               if(drawObject == null)
                   return false;

               return drawObject->IsVisible;
           },
           (_) => { },
           100,
           dontStartFor: 2
           );
    }

    public enum RedrawResult
    {
        NoChange,
        Optmized,
        Full,
        Failed
    }

    public enum RedrawStage
    {
        Before,
        After
    }
}
