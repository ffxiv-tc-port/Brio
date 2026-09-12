using Brio.Config;
using Brio.Game.Actor;
using Brio.Game.Core;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Glamourer.Api.Enums;
using Glamourer.Api.Helpers;
using Glamourer.Api.IpcSubscribers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Brio.IPC;

public class GlamourerService : BrioIPC
{
    public override string Name { get; } = "Glamourer";

    public override bool IsAvailable
        => CheckStatus() == IPCStatus.Available;

    public override bool AllowIntegration
        => _configurationService.Configuration.IPC.AllowGlamourerIntegration;

    public override int APIMajor => 1;
    public override int APIMinor => 4;

    public override (int Major, int Minor) GetAPIVersion()
        => _glamourerApiVersion.Invoke();

    public override IDalamudPluginInterface GetPluginInterface()
        => _pluginInterface;

    //

    private readonly ConfigurationService _configurationService;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ActorRedrawService _redrawService;
    private readonly IFramework _framework;
    /// <summary>
    /// 主執行緒轉派的卸載期閘門。🔴 <c>IFramework.RunOnFrameworkThread</c> 與<b>無延遲的</b>
    /// <c>RunOnTick</c> 在 <c>IsFrameworkUnloading</c> 為真時會<b>就地在呼叫端執行緒</b>執行委派
    /// （<c>Dalamud/Game/Framework.cs:167-211</c>），等於轉派在那一瞬間完全失效。
    /// </summary>
    private readonly IpcFrameworkGate _gate;
    private readonly ICommandManager _commandManager;
    private readonly IObjectTable _gameObjects;
    private readonly DalamudService _dalamudService;

    //
    //

    private readonly EventSubscriber _glamourerInitializedSubscriber;

    private readonly ApiVersion _glamourerApiVersion;

    private readonly GetState _glamourerGetState;
    private readonly ApplyState _glamourerApplyState;
    private readonly RevertState _glamourerRevertCharacter;
    private readonly RevertStateName _glamourerRevertByName;
    private readonly GetStateBase64? _glamourerGetAllCustomization;

    private readonly UnlockState _glamourerUnlock;
    private readonly UnlockStateName _glamourerUnlockByName;

    private readonly GetDesignList _glamourerGetDesignList;
    private readonly ApplyDesign _glamourerApplyDesign;

    //

    private readonly uint LockCode = 0x6D617265;

    public GlamourerService(IDalamudPluginInterface pluginInterface, IObjectTable gameObjects, ICommandManager commandManager, DalamudService dalamudService, ConfigurationService configurationService, IFramework framework, ActorRedrawService redrawService)
    {
        _pluginInterface = pluginInterface;
        _configurationService = configurationService;
        _framework = framework;
        _gate = new IpcFrameworkGate(framework);
        _redrawService = redrawService;
        _commandManager = commandManager;
        _dalamudService = dalamudService;
        _gameObjects = gameObjects;

        _glamourerInitializedSubscriber = Initialized.Subscriber(_pluginInterface, OnConfigurationChanged);

        _glamourerApiVersion = new ApiVersion(_pluginInterface);

        _glamourerGetState = new GetState(_pluginInterface);
        _glamourerApplyState = new ApplyState(_pluginInterface);
        _glamourerRevertCharacter = new RevertState(_pluginInterface);

        _glamourerRevertByName = new RevertStateName(_pluginInterface);
        _glamourerGetAllCustomization = new GetStateBase64(_pluginInterface);
        _glamourerUnlock = new UnlockState(_pluginInterface);
        _glamourerUnlockByName = new UnlockStateName(_pluginInterface);

        _glamourerGetDesignList = new GetDesignList(_pluginInterface);
        _glamourerApplyDesign = new ApplyDesign(_pluginInterface);

        OnConfigurationChanged();

        _configurationService.OnConfigurationChanged += OnConfigurationChanged;
    }

    public void OpenGlamourer()
    {
        _commandManager.ProcessCommand("/glamourer");
    }

    public bool CheckForLock(IGameObject? character)
    {
        if(IsAvailable == false || character is null)
            return false;

        var (key, _) = _glamourerGetState.Invoke(character!.ObjectIndex);

        Brio.Log.Verbose("Glamourer CheckForLock... " + key);

        return key == GlamourerApiEc.InvalidKey;
    }

    public bool UnlockAndRevertCharacterByName(string name)
    {
        if(IsAvailable == false || name.IsNullOrEmpty())
            return false;

        Brio.Log.Debug("Starting glamourer UnlockAndRevertByName...");

        var success = _glamourerUnlockByName.Invoke(name, LockCode);

        if(success is not GlamourerApiEc.Success)
        {
            Brio.Log.Info($"Glamourer UnlockAndRevertCharacterByName was not Successful: {success}");
            return false;
        }

        return true;
    }

    public bool UnlockAndRevertCharacter(IGameObject? character)
    {
        if(IsAvailable == false || character is null)
            return false;

        Brio.Log.Debug("Starting glamourer UnlockAndRevert...");

        var success = _glamourerRevertCharacter.Invoke(character!.ObjectIndex, LockCode);

        if(success is not GlamourerApiEc.Success)
        {
            Brio.Log.Info($"Glamourer UnlockAndRevertCharacter was not Successful: {success}");
            return false;
        }

        return true;
    }

    public Task RevertCharacter(IGameObject? character)
    {
        if(IsAvailable == false || character is null)
            return Task.CompletedTask;

        Brio.Log.Debug("Starting glamourer Revert...");

        var success = _glamourerRevertCharacter.Invoke(character!.ObjectIndex);

        if(success == Glamourer.Api.Enums.GlamourerApiEc.InvalidKey)
        {
            Brio.Log.Info("Glamourer character was locked..");
            UnlockAndRevertCharacter(character);
            return Task.CompletedTask;
        }

        if(success == Glamourer.Api.Enums.GlamourerApiEc.Success)
        {
            Brio.Log.Debug("Glamourer revert Started");
            return _framework.RunOnTick(async () =>
            {
                await _redrawService.WaitForDrawing(character!);
                Brio.Log.Debug("Glamourer revert complete");
            }, delayTicks: 5);
        }

        return Task.CompletedTask;
    }

    public Dictionary<Guid, string>? GetDesignList()
    {
        if(IsAvailable == false)
            return null;

        return _glamourerGetDesignList.Invoke();
    }

    public async Task RevertByNameAsync(string name, Guid applicationId)
    {
        if((!IsAvailable) || _dalamudService.IsZoning) return;

        await _framework.RunOnFrameworkThread(() =>
        {
            RevertByName(name, applicationId);

        }).ConfigureAwait(false);
    }

    public void ApplyAllAsync(IGameObject? character, string? customization, Guid applicationId)
    {
        if(IsAvailable == false || string.IsNullOrEmpty(customization)) return;

        try
        {
            Brio.Log.Debug("[{appid}] Calling on IPC: GlamourerApplyAll", applicationId);
            _glamourerApplyState.Invoke(customization, character!.ObjectIndex, LockCode);
        }
        catch(Exception ex)
        {
            Brio.Log.Debug(ex, "[{appid}] Failed to apply Glamourer data", applicationId);
        }
    }

    /// <summary>
    /// 透過角色名字還原 Glamourer 狀態並解鎖。
    /// </summary>
    /// <remarks>
    /// 🔴 進場條件原本寫 <c>if((IsAvailable) || _dalamudService.IsZoning) return;</c> —— 少一個 <c>!</c>。
    /// 三個相鄰的孿生方法方向都相反:非同步版 <see cref="RevertByNameAsync"/> 是 <c>if((!IsAvailable) || …)</c>、
    /// <see cref="ApplyAllAsync"/> 與 <see cref="UnlockAndRevertCharacterByName"/> 都是
    /// <c>IsAvailable == false</c> 才提前 return。
    /// <para>
    /// 寫反之後兩個方向都是壞的:Glamourer <b>在</b>的時候什麼都不做直接 return(該還原的沒還原);
    /// Glamourer <b>不在</b>的時候反而照樣去 Invoke 它的 IPC 端點,擲例外之後被下面的 catch
    /// 收成一行 Warning。也就是說這支不論如何都不會成功還原。
    /// </para>
    /// <para>
    /// 📌 可達性:本方法目前<b>零行為變化</b>。唯一的呼叫端是 <see cref="RevertByNameAsync"/>,
    /// 而那支在全 repo 也是零呼叫端;Brio 對外的 CallGate 端點全在 <c>BrioIPCService</c>,
    /// 那個檔對 Glamourer 零命中 ⇒ 別的外掛也打不到這裡。修它的意義是:這兩支是 public API,
    /// 哪天被接上(MCDF 還原流程本來就該用它)時不會帶著一個只會寫 Warning 的沉默失敗。
    /// </para>
    /// </remarks>
    public void RevertByName(string name, Guid applicationId)
    {
        if((!IsAvailable) || _dalamudService.IsZoning) return;

        try
        {
            Brio.Log.Debug("[{appid}] Calling On IPC: GlamourerRevertByName", applicationId);
            _glamourerRevertByName.Invoke(name, LockCode);
            Brio.Log.Debug("[{appid}] Calling On IPC: GlamourerUnlockName", applicationId);
            _glamourerUnlockByName.Invoke(name, LockCode);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "Error during Glamourer RevertByName");
        }
    }

    public async Task<string> GetCharacterCustomizationAsync(IntPtr character)
    {
        if(IsAvailable == false) return string.Empty;

        try
        {
            return await _gate.RunAsync("Glamourer.GetCharacterCustomization", () =>
            {
                // 🔴 character 是呼叫端好幾幀之前讀出來的位址,而 CreateObjectReference 會解參考它去讀 ObjectKind
                //    (本 pin 的 Dalamud ObjectTable.cs:155-156)。角色已消失的話那就是懸空讀,try/catch 攔不到。
                //    先由物件表確認位址還在 —— 只讀物件表自己的指標陣列,不解參考任何存下來的位址。
                var liveAddress = LiveActorRef.FromAddress(_gameObjects, character).Address;
                if(liveAddress == nint.Zero)
                    return string.Empty;

                var gameObj = _gameObjects.CreateObjectReference(liveAddress);
                if(gameObj is ICharacter c)
                {
                    return _glamourerGetAllCustomization!.Invoke(c.ObjectIndex).Item2 ?? string.Empty;
                }
                return string.Empty;
            }, string.Empty).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 對角色套用 Glamourer 設計。回傳 <c>true</c> 代表 Glamourer 真的接受了這次套用。
    /// </summary>
    public bool ApplyDesign(Guid design, IGameObject? character)
    {
        if(IsAvailable == false || character is null)
            return false;

        var result = _glamourerApplyDesign.Invoke(design, character.ObjectIndex);

        // 這裡刻意比上游 cycleapple 寬一格:NothingDone 也算成功。
        // NothingDone 的語意是「呼叫合法,但狀態本來就是這樣所以沒動到東西」,設計其實是生效的;
        // 把它當失敗會讓 ActorAppearanceCapability.IsDesignOverridden 停在 false,
        // 使用者之後就再也還原不了這個設計 —— 那個方向的錯比多還原一次嚴重得多。
        if(result is not GlamourerApiEc.Success and not GlamourerApiEc.NothingDone)
        {
            Brio.Log.Information($"Glamourer could not apply design {design} to GameObject {character.ObjectIndex}: {result}");
            return false;
        }

        return true;
    }

    private void OnConfigurationChanged()
        => CheckStatus();

    public override void Dispose()
    {
        _configurationService.OnConfigurationChanged -= OnConfigurationChanged;

        _glamourerInitializedSubscriber.Dispose();

        GC.SuppressFinalize(this);
    }
}
