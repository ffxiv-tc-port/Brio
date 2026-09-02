using Brio.Game.Actor.Extensions;
using Brio.Game.Core;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using System;
using System.Threading.Tasks;

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
        Brio.Log.Info($"Beginning Brio redraw on gameobject {go.ObjectIndex}...");
        DisableDraw(go);
        try
        {
            ActorRedrawEvent?.Invoke(go, RedrawStage.Before);
            await DrawWhenReady(go);
            await WaitForDrawing(go);
            ActorRedrawEvent?.Invoke(go, RedrawStage.After);
            Brio.Log.Debug($"Brio redraw complete on gameobject {go.ObjectIndex}.");
            return RedrawResult.Full;
        }
        catch(Exception e)
        {
            Brio.Log.Error(e, $"Brio redraw failed on gameobject {go.ObjectIndex}.");
            return RedrawResult.Failed;
        }
    }

    public async Task RedrawAndWait(IGameObject go)
    {
        var objectIndex = go.ObjectIndex;

        Brio.Log.Info($"Beginning Brio RedrawAndWait on gameobject {objectIndex}...");
        try
        {
            // 🔴 這個迴圈最多跨 3 秒、每一圈都跨過 await。go 的 Address 是建構當下凍結的,
            //    角色若在這段期間消失,IsDrawing(go) 會解參懸空位址;go.IsValid() 只檢查有沒有登入
            //    (本 pin 的 Dalamud/Game/ClientState/Objects/Types/GameObject.cs:170-177),擋不住這件事,
            //    而 AccessViolationException 在 .NET Core 是 corrupted-state exception,try/catch 攔不到。
            //    改成只抄走位址,每一圈由物件表重新確認它還在表裡之後才解參。
            //    (FromAddress 只讀包裝物件自己的 Address 欄位,建構這一步本身也不解參。)
            var actorRef = LiveActorRef.FromAddress(_objectTable, go.Address);

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

    public unsafe bool IsDrawing(IGameObject go)
    {
        var native = go.Native();
        if(native is null) return false;
        return native->RenderFlags == 0x00;
    }

    public unsafe void DisableDraw(IGameObject go)
    {
        if(!go.IsValid())
            return;

        var native = go.Native();
        native->DisableDraw();
    }

    public unsafe void EnableDraw(IGameObject go)
    {
        if(!go.IsValid())
            return;

        var native = go.Native();
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
