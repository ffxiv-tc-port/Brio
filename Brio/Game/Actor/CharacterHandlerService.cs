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
        // holder 只帶 id,要用的當下才重查物件表。SearchById 要求主執行緒,
        // 已經在 framework 執行緒上時 RunOnFrameworkThread 會就地同步執行(不會排隊,不會卡住呼叫端)。
        var gameObject = mCDFCharacterHolder.GameObjectId != 0
            ? await _framework.RunOnFrameworkThread(() => _objectTable.SearchById(mCDFCharacterHolder.GameObjectId)).ConfigureAwait(false)
            : null;

        if(gameObject is null)
            Brio.Log.Info($"RevertMCDF: 物件表中找不到 GameObjectId 0x{mCDFCharacterHolder.GameObjectId:X}(角色「{mCDFCharacterHolder.Name}」已離開),改為只以名稱還原 Glamourer。");

        if(gameObject is not null)
            _glamourerService.UnlockAndRevertCharacter(gameObject);

        if(mCDFCharacterHolder.Name.IsNullOrEmpty() is false)
            _glamourerService.UnlockAndRevertCharacterByName(mCDFCharacterHolder.Name);

        if(gameObject is not null && gameObject.Address != nint.Zero)
        {
            _customizePlusService.RemoveTemporaryProfile(gameObject);
            await _penumbraService.Redraw(gameObject, true).ConfigureAwait(false);
        }
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
