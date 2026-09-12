using Brio.Game.Core;
using Brio.Game.GPose;
using Brio.IPC;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Brio.Game.Actor;

// 🔴 這裡刻意只存 GameObjectId,不存 IGameObject:
//    Dalamud 的 IObjectTable 對每個槽位×每種 kind 預配一個包裝實例,存取時就地改寫 Address
//    (ObjectTable.CachedEntry.Update),槽位空掉時連改寫都不做。跨幀持有那個包裝
//    ⇒ 靜默換成別的角色,或原地留著已釋放的位址(連 .GameObjectId 都不能讀)。
//    這個 holder 從 MCDF 套用當下一直活到 GPose 結束,是最長命的持有點之一。
public record CharacterHolder(ulong GameObjectId, Guid? CPlusID, string Name);

public class CharacterHandlerService : IDisposable
{
    private readonly IFramework _framework;
    /// <summary>
    /// 主執行緒轉派的卸載期閘門。🔴 <c>IFramework.RunOnFrameworkThread</c> 與<b>無延遲的</b>
    /// <c>RunOnTick</c> 在 <c>IsFrameworkUnloading</c> 為真時會<b>就地在呼叫端執行緒</b>執行委派
    /// （<c>Dalamud/Game/Framework.cs:167-211</c>），等於轉派在那一瞬間完全失效。
    /// </summary>
    private readonly IpcFrameworkGate _gate;
    private readonly IObjectTable _objectTable;
    private readonly ActorRedrawService _redrawService;
    private readonly GPoseService _gPoseService;
    private readonly DalamudService _dalamudService;

    private readonly PenumbraService _penumbraService;
    private readonly GlamourerService _glamourerService;
    private readonly CustomizePlusService _customizePlusService;

    //

    public HashSet<CharacterHolder> CharacterHandler = [];

    public CharacterHandlerService(IFramework framework, IObjectTable objectTable, DalamudService dalamudService, GPoseService gPoseService, ActorRedrawService redrawService,
        PenumbraService penumbraService, GlamourerService glamourerService, CustomizePlusService customizePlusService)
    {
        _framework = framework;
        _gate = new IpcFrameworkGate(framework);
        _objectTable = objectTable;
        _redrawService = redrawService;
        _gPoseService = gPoseService;
        _dalamudService = dalamudService;

        _penumbraService = penumbraService;
        _glamourerService = glamourerService;
        _customizePlusService = customizePlusService;

        _gPoseService.OnGPoseStateChange += OnGPoseStateChange;
    }

    private void OnGPoseStateChange(bool newState)
    {
        if(newState == false)
        {
            foreach(var entry in CharacterHandler)
            {
                var character = _dalamudService.GetGposeCharacterFromObjectTableByName(entry.Name, onlyGposeCharacters: true);
                if(character is null)
                {
                    RevertMCDF(entry).GetAwaiter().GetResult();
                }
            }
            CharacterHandler.Clear();
        }
    }

    public async Task RevertMCDF(CharacterHolder mCDFCharacterHolder)
    {
        // holder 只帶 id,要用的當下才重查物件表;SearchById 回的是物件表共用的包裝,不能帶出這個回呼
        // ⇒ 當場用 CreateObjectReference 換成獨立實例(Address 這一刻凍結,不會被改寫成別人)。
        // 🔴 UnlockAndRevertCharacter 讀 ObjectIndex 是解參,所以連它一起留在閘門內:
        //    await 的續行跑在執行緒池上,在那裡解參等於讀可能已釋放的記憶體。
        var actor = await _gate.RunAsync<IGameObject?>("CharacterHandlerService.RevertMCDF", () =>
        {
            var live = mCDFCharacterHolder.GameObjectId != 0
                ? _objectTable.SearchById(mCDFCharacterHolder.GameObjectId)
                : null;

            if(live is null || live.Address == nint.Zero)
                return null;

            var resolved = _objectTable.CreateObjectReference(live.Address);
            if(resolved is not null)
                _glamourerService.UnlockAndRevertCharacter(resolved);

            return resolved;
        }, null).ConfigureAwait(false);

        if(actor is null)
            Brio.Log.Info($"RevertMCDF: 物件表中找不到 GameObjectId 0x{mCDFCharacterHolder.GameObjectId:X}(角色「{mCDFCharacterHolder.Name}」已離開),改為只以名稱還原 Glamourer。");

        // 刻意留在閘門外:這支只把名字字串交給 Glamourer,不解參任何遊戲物件,
        // 而角色已消失(或閘門走卸載期旁路)時它是唯一還跑得動的還原路徑。
        if(mCDFCharacterHolder.Name.IsNullOrEmpty() is false)
            _glamourerService.UnlockAndRevertCharacterByName(mCDFCharacterHolder.Name);

        if(actor is null || actor.Address == nint.Zero)
            return;

        // actor.Address 讀的是獨立包裝自己的欄位(不解參),抄成 LiveActorRef 可以安全跨執行緒;
        // RemoveTemporaryProfile 讀的 ObjectIndex 才是解參 ⇒ 回框架執行緒重查存活後才呼叫。
        var actorRef = LiveActorRef.FromAddress(_objectTable, actor.Address);

        await _gate.RunAsync("CharacterHandlerService.RevertMCDF/CustomizePlus", () =>
        {
            if(actorRef.IsAlive == false)
            {
                Brio.Log.Information($"角色「{mCDFCharacterHolder.Name}」已經不在物件表裡,略過 Customize+ 暫時設定的還原。");
                return;
            }

            _customizePlusService.RemoveTemporaryProfile(actor);
        }).ConfigureAwait(false);

        // Penumbra.Redraw 自己會抄位址、自己上閘門並重查存活,所以留在外面 await。
        await _penumbraService.Redraw(actor, true).ConfigureAwait(false);
    }

    public async Task Revert(IGameObject obj, bool afterGpose = false)
    {
        if(obj is null) return;

        _glamourerService.UnlockAndRevertCharacterByName(obj.Name.TextValue);
        _glamourerService.UnlockAndRevertCharacter(obj);

        _customizePlusService.RemoveTemporaryProfile(obj);

        if(obj.Address != nint.Zero)
        {
            if(afterGpose == false)
                await _redrawService.RedrawAndWait(obj).ConfigureAwait(false);
            await _penumbraService.Redraw(obj, afterGpose).ConfigureAwait(false);
        }
    }

    public async Task RevertHandledChara(CharacterHolder? holder)
    {
        if(holder == null) return;
        CharacterHandler.Remove(holder);
        await _framework.RunOnTick(async () => await RevertMCDF(holder));
    }

    public void Dispose()
    {
        _gPoseService.OnGPoseStateChange -= OnGPoseStateChange;

        foreach(var character in CharacterHandler)
        {
            _ = RevertHandledChara(character);
        }

        CharacterHandler.Clear();

        GC.SuppressFinalize(this);
    }
}
