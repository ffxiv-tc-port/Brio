using Brio.IPC;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Brio.Game.Core;

public class DalamudService : IDisposable
{
    public bool IsWine { get; init; }

    private readonly ICondition _condition;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly IGameConfig _gameConfig;
    private readonly IDataManager _dataManager;
    private readonly IObjectTable _objectTable;

    private uint? _classJobId = 0;

    /// <summary>
    /// 卸載期的判斷與節流訊息重用 IPC 端點那一層的實作，不另外寫一套。
    /// 這裡只用它的 <see cref="IpcFrameworkGate.ShouldBypassForUnloading"/>，
    /// 逾時等待那條路徑（同步 IPC 端點專用）在本檔完全沒用到。
    /// </summary>
    private readonly IpcFrameworkGate _unloadGate;

    public DalamudService(ICondition condition, IObjectTable gameObjects, IClientState clientState, IGameConfig gameConfig, IDataManager dataManager, IFramework framework)
    {
        _condition = condition;
        _clientState = clientState;
        _framework = framework;
        _gameConfig = gameConfig;
        _dataManager = dataManager;
        _objectTable = gameObjects;

        _unloadGate = new IpcFrameworkGate(framework);

        IsWine = Util.IsWine();

        _framework.Update += FrameworkOnUpdate;
    }

    public bool IsInCutscene { get; private set; } = false;
    public bool IsZoning => _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];

    public bool HasModifiedGameFiles => _dataManager.HasModifiedGameDataFiles;
    public bool IsLodEnabled { get; private set; }
    public uint ClassJobId => _classJobId!.Value;

    private void FrameworkOnUpdate(IFramework framework)
    {
        if(_condition[ConditionFlag.WatchingCutscene] && !IsInCutscene)
        {
            IsInCutscene = true;
        }
        else if(!_condition[ConditionFlag.WatchingCutscene] && IsInCutscene)
        {
            IsInCutscene = false;
        }

        var localPlayer = _clientState.LocalPlayer;
        if(localPlayer != null)
        {
            _classJobId = localPlayer.ClassJob.RowId;
        }

        if(_gameConfig != null
            && _gameConfig.TryGet(Dalamud.Game.Config.SystemConfigOption.LodType_DX11, out bool lodEnabled))
        {
            IsLodEnabled = lodEnabled;
        }
    }

    public async Task<uint> GetHomeWorldIdAsync()
    {
        return await RunOnFrameworkThread(GetHomeWorldId).ConfigureAwait(false);
    }

    public uint GetHomeWorldId()
    {
        return _clientState.LocalPlayer?.HomeWorld.RowId ?? 0;
    }

    public bool GetIsPlayerPresent()
    {
        return _clientState.LocalPlayer != null && _clientState.LocalPlayer.IsValid();
    }

    public async Task<bool> GetIsPlayerPresentAsync()
    {
        return await RunOnFrameworkThread(GetIsPlayerPresent).ConfigureAwait(false);
    }

    public string GetPlayerName()
    {
        return _clientState.LocalPlayer?.Name.ToString() ?? "--";
    }

    public bool IsObjectPresent(IGameObject? obj)
    {
        return obj != null && obj.IsValid();
    }

    public async Task<bool> IsObjectPresentAsync(IGameObject? obj)
    {
        return await RunOnFrameworkThread(() => IsObjectPresent(obj)).ConfigureAwait(false);
    }

    public async Task<string> GetPlayerNameAsync()
    {
        // 卸載期回的值沿用 GetPlayerName() 自己在「沒有玩家」時就會回的 "--"，
        // 而不是 null：唯一的呼叫端 TransientResourceService.PlayerPersistentDataKey
        // 會把它串成設定字典的鍵。
        return await RunOnFrameworkThread(GetPlayerName, "--").ConfigureAwait(false);
    }

    /// <summary>
    /// 🔴 回傳型別標成可空是實話而不是放寬：<c>GetPlayerCharacter()</c> 本來就是
    /// <c>_clientState.LocalPlayer!</c>，沒登入時原本就會回 null；卸載期閘門也回 null。
    /// 唯一的呼叫端（<c>MCDFService</c> 的 MCDF 套用流程）本來就寫著 <c>is not null</c>。
    /// </summary>
    public async Task<IPlayerCharacter?> GetPlayerCharacterAsync()
    {
        return await RunOnFrameworkThread(GetPlayerCharacter).ConfigureAwait(false);
    }
    public IPlayerCharacter GetPlayerCharacter()
    {
        return _clientState.LocalPlayer!;
    }

    public ICharacter? GetGposeCharacterFromObjectTableByName(string name, bool onlyGposeCharacters = false)
    {
        return (ICharacter?)_objectTable
             .FirstOrDefault(i => (!onlyGposeCharacters || i.ObjectIndex >= 200) && string.Equals(i.Name.ToString(), name, StringComparison.Ordinal));
    }

    /// <summary>把 <paramref name="func"/> 丟回遊戲主執行緒執行。本檔所有 <c>*Async</c> 包裝都走這裡。</summary>
    /// <param name="unavailable">卸載期（見下）要回的「做不到」值。一律沿用該包裝自己原本在「沒有玩家」時就會回的值，這樣呼叫端看到的值域一個都沒有變寬。</param>
    /// <remarks>
    /// 🔴 <b>卸載期閘門</b>：Dalamud 的 <c>IFramework.RunOnFrameworkThread</c> 在 <c>IsFrameworkUnloading</c> 為真時會<b>就地在呼叫端的執行緒</b>執行委派，等於這個轉接點在那一瞬間完全失效。
    /// 🔑 所以卸載期一律<b>不執行</b> <paramref name="func"/>，直接回 <paramref name="unavailable"/>：那一瞬間功能失效可以接受（遊戲要關了），崩潰不行。</remarks>
    public async Task<T> RunOnFrameworkThread<T>(Func<T> func, T unavailable = default!, [CallerMemberName] string callerMember = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0)
    {
        if(!_framework.IsInFrameworkUpdateThread)
        {
            // 到這裡代表我們在背景執行緒上。卸載期轉派會就地執行，保護不了原生存取。
            if(_unloadGate.ShouldBypassForUnloading($"DalamudService.{callerMember}"))
                return unavailable;

            var result = await _framework.RunOnFrameworkThread(func).ContinueWith((task) => task.Result).ConfigureAwait(false);
            while(_framework.IsInFrameworkUpdateThread) // yield the thread again, should technically never be triggered
            {
                await Task.Delay(1).ConfigureAwait(false);
            }
            return result;
        }

        return func.Invoke();
    }

    public void Dispose()
    {
        _framework.Update -= FrameworkOnUpdate;

        GC.SuppressFinalize(this);
    }
}
