using Brio.Game.Actor;
using Brio.Game.Actor.Extensions;
using Brio.Game.Core;
using Brio.IPC;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using System.Threading.Tasks;

namespace Brio.Web;

public class ActorWebController(IFramework framework, ActorSpawnService actorSpawnService, ActorRedrawService redrawService, IObjectTable objectTable) : WebApiController
{
    private readonly IObjectTable _objectTable = objectTable;
    private readonly IFramework _framework = framework;
    private readonly ActorSpawnService _actorSpawnService = actorSpawnService;
    private readonly ActorRedrawService _redrawService = redrawService;

    /// <summary>
    /// 主執行緒轉派的卸載期閘門。🔴 <c>IFramework.RunOnFrameworkThread</c> 與<b>無延遲的</b>
    /// <c>RunOnTick</c> 在 <c>IsFrameworkUnloading</c> 為真時會<b>就地在呼叫端執行緒</b>執行委派
    /// （<c>Dalamud/Game/Framework.cs:167-211</c>），等於轉派在那一瞬間完全失效。
    /// </summary>
    private readonly IpcFrameworkGate _gate = new(framework);

    [Route(HttpVerbs.Post, "/redraw")]
    public async Task<string> RedrawActor([JsonData] RedrawRequest data)
    {
        Brio.Log.Debug("Received redraw request on WebAPI");
        try
        {
            var result = await _gate.RunTaskAsync("WebAPI /redraw", () => _redrawService.RedrawObjectByIndex(data.ObjectIndex), ActorRedrawService.RedrawResult.Failed);
            return result.ToString();
        }
        catch
        {
            HttpContext.Response.StatusCode = 500;
            return ActorRedrawService.RedrawResult.Failed.ToString();
        }
    }

    [Route(HttpVerbs.Post, "/spawn")]
    public async Task<int> Spawn()
    {
        Brio.Log.Debug("Received spawn request on WebAPI");
        try
        {
            ICharacter? character = null;
            var res = await _gate.RunAsync("WebAPI /spawn", () =>
            {
                if(_actorSpawnService.CreateCharacter(out ICharacter? chara, SpawnFlags.Default))
                {
                    character = chara;

                    return chara.ObjectIndex;
                }
                return -1;
            }, -1);

            if(character is not null)
            {
                await WaitForReadyToDraw(character);
            }

            return res;
        }
        catch
        {
            HttpContext.Response.StatusCode = 500;
            return -1;
        }
    }

    public unsafe Task WaitForReadyToDraw(IGameObject go)
    {
        // 🔴 RunUntilSatisfied 會逐幀重排最多 100 幀。go 的 Address 是建構當下凍結的,角色在這段期間
        //    消失就成了懸空位址;原本的 go.IsValid() 只檢查有沒有登入,對懸空位址零作用
        //    (本 pin 的 Dalamud/Game/ClientState/Objects/Types/GameObject.cs:170-177)。
        //    改成只抄走位址,每一幀由物件表重查(GetObjectAddress 只讀指標陣列,不解參)。
        //    (FromAddress 只讀包裝物件自己的 Address 欄位,建構這一步本身也不解參。)
        var actorRef = LiveActorRef.FromAddress(_objectTable, go.Address);

        return _framework.RunUntilSatisfied(
           () => {
               var native = actorRef.GameObject;
               if(native == null)
                   return false;

               return native->IsReadyToDraw();
           },
           (_) => { },
           100,
           dontStartFor: 2
       );
    }

    [Route(HttpVerbs.Post, "/despawn")]
    public async Task<bool> Despawn([JsonData] DespawnRequest data)
    {
        Brio.Log.Debug("Received despawn request on WebAPI");
        try
        {
            var didDestroy = await _gate.RunAsync("WebAPI /despawn", () => _actorSpawnService.DestroyObject(data.ObjectIndex), false);
            return didDestroy;
        }
        catch
        {
            HttpContext.Response.StatusCode = 500;
            return false;
        }

    }
}

public class DespawnRequest
{
    public int ObjectIndex { get; set; }
}

public class RedrawRequest
{
    public int ObjectIndex { get; set; }
}
