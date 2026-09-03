using Brio.Config;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Brio.IPC;

public class PenumbraService : BrioIPC
{
    public override string Name { get; } = "Penumbra";

    public override bool IsAvailable
        => PenumbraCheckStatus();

    public override bool AllowIntegration
        => _configurationService.Configuration.IPC.AllowPenumbraIntegration;

    public override int APIMajor => 5;
    public override int APIMinor => 10;

    public override (int Major, int Minor) GetAPIVersion()
        => _penumbraApiVersion.Invoke();

    public override IDalamudPluginInterface GetPluginInterface()
        => _pluginInterface;

    //
    //

    private string? _penumbraModDirectory;
    public string? ModDirectory
    {
        get => _penumbraModDirectory;
        private set
        {
            if(!string.Equals(_penumbraModDirectory, value, StringComparison.Ordinal))
            {
                _penumbraModDirectory = value;
            }
        }
    }

    //

    private readonly ConfigurationService _configurationService;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IFramework _framework;

    //private readonly GetEnabledState _penumbraEnabled;
    private readonly ApiVersion _penumbraApiVersion;

    private readonly OpenMainWindow _penumbraOpenMainWindow;

    public delegate void PenumbraRedrawEvent(int gameObjectId);
    public event PenumbraRedrawEvent? OnPenumbraRedraw;

    public delegate void PenumbraResourceLoadedEvent(IntPtr ptr, string arg1, string arg2);
    public event PenumbraResourceLoadedEvent? OnPenumbraResourceLoaded;

    private readonly RedrawObject _penumbraRedraw;

    private readonly EventSubscriber<nint, int> _penumbraRedrawEvent;
    private readonly EventSubscriber _penumbraInitializedSubscriber;
    private readonly EventSubscriber _penumbraDisposedSubscriber;

    private readonly EventSubscriber<nint, string, string> _penumbraGameObjectResourcePathResolved;

    private readonly SetCollectionForObject _penumbraSetCollectionForObject;
    private readonly GetCollectionForObject _penumbraGetCollectionForObject;
    private readonly SetCutsceneParentIndex _penumbraSetCutsceneParentIndex;
    private readonly GetCollections _penumbraGetCollections;

    private readonly CreateTemporaryCollection _penumbraCreateNamedTemporaryCollection;
    private readonly AssignTemporaryCollection _penumbraAssignTemporaryCollection;
    private readonly DeleteTemporaryCollection _penumbraRemoveTemporaryCollection;


    private readonly AddTemporaryMod _penumbraAddTemporaryMod;
    private readonly RemoveTemporaryMod _penumbraRemoveTemporaryMod;

    private readonly GetModDirectory _penumbraResolveModDir;

    private readonly ResolvePlayerPathsAsync _penumbraResolvePaths;
    private readonly GetGameObjectResourcePaths _penumbraResourcePaths;
    private readonly GetPlayerMetaManipulations _penumbraGetMetaManipulations;
    //private readonly ConvertTextureFile _penumbraConvertTextureFile;

    public PenumbraService(IDalamudPluginInterface pluginInterface, IFramework framework, ConfigurationService configurationService)
    {
        _pluginInterface = pluginInterface;
        _configurationService = configurationService;
        _framework = framework;

        _penumbraInitializedSubscriber = Initialized.Subscriber(_pluginInterface, OnConfigurationChanged);
        _penumbraDisposedSubscriber = Disposed.Subscriber(_pluginInterface, OnConfigurationChanged);
        _penumbraRedrawEvent = GameObjectRedrawn.Subscriber(_pluginInterface, HandlePenumbraRedraw);
        _penumbraGameObjectResourcePathResolved = GameObjectResourcePathResolved.Subscriber(_pluginInterface, ResourceLoaded);

        _penumbraGetCollectionForObject = new GetCollectionForObject(_pluginInterface);
        _penumbraSetCollectionForObject = new SetCollectionForObject(_pluginInterface);
        _penumbraSetCutsceneParentIndex = new SetCutsceneParentIndex(_pluginInterface);
        _penumbraGetCollections = new GetCollections(_pluginInterface);
        _penumbraOpenMainWindow = new OpenMainWindow(_pluginInterface);
        _penumbraApiVersion = new ApiVersion(_pluginInterface);

        _penumbraResolveModDir = new GetModDirectory(_pluginInterface);
        _penumbraRedraw = new RedrawObject(_pluginInterface);
        _penumbraRemoveTemporaryMod = new RemoveTemporaryMod(_pluginInterface);
        _penumbraAddTemporaryMod = new AddTemporaryMod(_pluginInterface);
        _penumbraCreateNamedTemporaryCollection = new CreateTemporaryCollection(_pluginInterface);
        _penumbraRemoveTemporaryCollection = new DeleteTemporaryCollection(_pluginInterface);
        _penumbraAssignTemporaryCollection = new AssignTemporaryCollection(_pluginInterface);
        //_penumbraEnabled = new GetEnabledState(_pluginInterface);

        _penumbraResolvePaths = new ResolvePlayerPathsAsync(_pluginInterface);
        _penumbraResourcePaths = new GetGameObjectResourcePaths(_pluginInterface);
        _penumbraGetMetaManipulations = new GetPlayerMetaManipulations(_pluginInterface);
        //_penumbraConvertTextureFile = new ConvertTextureFile(_pluginInterface);

        _configurationService.OnConfigurationChanged += OnConfigurationChanged;

        OnConfigurationChanged();
        PenumbraCheckStatus();
    }

    public bool PenumbraCheckStatus()
    {
        var status = CheckStatus() == IPCStatus.Available;

        checkModDirectory(status);

        return status;
    }
    void checkModDirectory(bool available)
    {
        if(!available)
        {
            ModDirectory = string.Empty;
        }
        else
        {
            ModDirectory = _penumbraResolveModDir!.Invoke().ToLowerInvariant();
        }
    }

    public void OpenPenumbra()
    {
        if(IsAvailable == false)
            return;

        _penumbraOpenMainWindow.Invoke(Penumbra.Api.Enums.TabType.Mods);
    }

    public string GetCollectionForObject(IGameObject gameObject)
    {
        if(IsAvailable == false || gameObject is null)
            return string.Empty;

        var (_, _, collection) = _penumbraGetCollectionForObject.Invoke(gameObject.ObjectIndex);
        return collection.Name;
    }

    /// <summary>
    /// 把指定角色指派到某個 Penumbra 集合,並回傳「還原時該指回去的集合」。
    /// <para>
    /// 回傳 <c>null</c> 代表「沒有可還原的目標」(Penumbra 不在、指派被拒、或問不到目前生效的集合),
    /// 呼叫端不可以把它當成有效集合拿去還原。
    /// </para>
    /// </summary>
    public Guid? SetCollectionForObject(IGameObject gameObject, Guid collectionName)
    {
        if(IsAvailable == false || gameObject is null)
            return null;

        Brio.Log.Debug($"Setting GameObject {gameObject.ObjectIndex} collection to {collectionName}");

        // 先問「這一格現在生效的是哪個集合」。Brio 生成的角色通常沒有個別指派,
        // 下面 SetCollectionForObject 回來的舊集合會是 null —— 那時只有這個值能拿來還原。
        var (objectValid, _, effectiveCollection) = _penumbraGetCollectionForObject.Invoke(gameObject.ObjectIndex);

        var (result, oldCollection) = _penumbraSetCollectionForObject.Invoke(gameObject.ObjectIndex, collectionName, true, true);
        if(result is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
        {
            Brio.Log.Information($"Penumbra rejected assigning GameObject {gameObject.ObjectIndex} to collection {collectionName}: {result}");
            return null;
        }

        if(oldCollection is not null)
            return oldCollection.Value.Id;

        // 這裡刻意與上游 cycleapple 的寫法有兩點不同:
        //   (1) 失敗一律回 null,不回 Guid.Empty。本方法回傳型別是 Guid?,呼叫端
        //       (ActorAppearanceCapability.SetCollection) 只檢查 is not null;回 Guid.Empty 會讓它
        //       把全 0 的假集合記成待還原目標,ResetCollection 反而會把角色指到不存在的集合。
        //   (2) 不拿 objectValid 當提前 return 的閘門。它只說物件表那一格當下有沒有東西,
        //       真正的權威是 SetCollectionForObject 自己的回傳碼(上面已經檢查過)。
        //       objectValid 為 false 時 effectiveCollection 不可信,所以只在這裡當成「沒有舊集合」。
        return objectValid ? effectiveCollection.Id : null;
    }

    public Dictionary<Guid, string> GetCollections()
    {
        if(IsAvailable == false)
            return null!;

        return _penumbraGetCollections.Invoke();
    }

    /// <summary>
    /// 讓 Penumbra 把這一格當成獨立角色,而不是「某個角色的過場複製體」。
    /// <para>
    /// Brio 生成角色時用 <c>CharacterSetup.CopyFromCharacter</c> 從來源角色整份複製,
    /// Penumbra 因此會把這一格的集合與 Glamourer 查詢導向來源角色的識別碼 ——
    /// 使用者對 Brio 角色設定的集合就落不到它自己身上。
    /// </para>
    /// </summary>
    /// <param name="objectIndex">
    /// 刻意收索引而不是 <c>IGameObject</c>:本 pin 的物件表包裝是每格重用、存取時就地改寫 Address,
    /// 跨幀持有會靜默換人。索引請在呼叫端當場抄走。
    /// </param>
    public void DetachCutsceneActor(ushort objectIndex)
    {
        if(IsAvailable == false)
            return;

        // -1 = 沒有過場母體。
        // Penumbra 只在它自己認定的過場索引範圍內接受這個呼叫,超出範圍會回 InvalidArgument。
        // 這裡不自己手刻範圍檢查(抄上游手刻的邊界很容易差一),改成把 Penumbra 的回傳碼
        // 原樣寫進 log,實機上一看就知道有沒有生效。
        var result = _penumbraSetCutsceneParentIndex.Invoke(objectIndex, -1);
        if(result != PenumbraApiEc.Success)
            Brio.Log.Information($"Penumbra could not detach GameObject {objectIndex} from its cutscene parent: {result}");
    }

    private void ResourceLoaded(IntPtr ptr, string arg1, string arg2)
    {
        if(ptr != IntPtr.Zero && string.Compare(arg1, arg2, ignoreCase: true, System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            OnPenumbraResourceLoaded?.Invoke(ptr, arg1, arg2);
        }
    }

    public async Task<(string[] forward, string[][] reverse)> ResolvePathsAsync(string[] forward, string[] reverse)
    {
        return await _penumbraResolvePaths.Invoke(forward, reverse).ConfigureAwait(false);
    }

    /// <summary>
    /// 呼叫端已經在 framework 執行緒上確認過角色還在物件表裡、並且<b>在同一個回呼裡</b>把索引讀出來時用這一支。
    /// 另一個多載是在它自己的 framework 回呼裡才去解參考 <c>IGameObject.ObjectIndex</c> —— 那已經是後來的某一幀,
    /// 角色若在這之間消失就是懸空讀,而 AccessViolationException 在 .NET Core 攔不到。
    /// </summary>
    public async Task<Dictionary<string, HashSet<string>>?> GetCharacterData(ushort objectIndex)
    {
        if(IsAvailable == false) return null;

        return await _framework.RunOnFrameworkThread(() =>
        {
            Brio.Log.Debug("Calling On IPC: Penumbra.GetGameObjectResourcePaths");
            return _penumbraResourcePaths.Invoke(objectIndex)[0];
        }).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, HashSet<string>>?> GetCharacterData(IGameObject gameObject)
    {
        if(IsAvailable == false) return null;

        return await _framework.RunOnFrameworkThread(() =>
        {
            Brio.Log.Debug("Calling On IPC: Penumbra.GetGameObjectResourcePaths");
            var idx = gameObject?.ObjectIndex;
            if(idx == null) return null;
            return _penumbraResourcePaths.Invoke(idx.Value)[0];
        }).ConfigureAwait(false);
    }

    public string GetMetaManipulations()
    {
        if(IsAvailable == false) return string.Empty;

        Brio.Log.Debug("Calling On IPC: Penumbra.GetMetaManipulations");

        return _penumbraGetMetaManipulations.Invoke();
    }

    public async Task RemoveTemporaryCollectionAsync(Guid applicationId, Guid collId)
    {
        if(!IsAvailable) return;
        await _framework.RunOnFrameworkThread(() =>
        {
            Brio.Log.Debug("[{applicationId}] Removing temp collection for {collId}", applicationId, collId);
            var ret2 = _penumbraRemoveTemporaryCollection.Invoke(collId);
            Brio.Log.Debug("[{applicationId}] RemoveTemporaryCollection: {ret2}", applicationId, ret2);
        }).ConfigureAwait(false);
    }

    public async Task AssignTemporaryCollectionAsync(Guid collName, int idx)
    {
        if(!IsAvailable) return;

        await _framework.RunOnFrameworkThread(() =>
        {
            var retAssign = _penumbraAssignTemporaryCollection.Invoke(collName, idx, forceAssignment: true);
            Brio.Log.Debug("Assigning Temp Collection {collName} to index {idx}, Success: {ret}", collName, idx, retAssign);
            return collName;
        }).ConfigureAwait(false);
    }

    public async Task<Guid> CreateTemporaryCollectionAsync(string uid)
    {
        if(!IsAvailable) return Guid.Empty;

        return await _framework.RunOnFrameworkThread(() =>
        {
            var collName = "Brio_" + uid;
            _penumbraCreateNamedTemporaryCollection.Invoke("Brio", collName, out var collId);
            Brio.Log.Debug("Creating Temp Collection {collName}, GUID: {collId}", collName, collId);
            return collId;

        }).ConfigureAwait(false);
    }

    public async Task SetTemporaryModsAsync(Guid applicationId, Guid collId, Dictionary<string, string> modPaths)
    {
        if(!IsAvailable) return;

        await _framework.RunOnFrameworkThread(() =>
        {
            foreach(var mod in modPaths)
            {
                Brio.Log.Debug("[{applicationId}] Change: {from} => {to}", applicationId, mod.Key, mod.Value);
            }
            var retRemove = _penumbraRemoveTemporaryMod.Invoke("BrioChara_Files", collId, 0);
            Brio.Log.Debug("[{applicationId}] Removing temp files mod for {collId}, Success: {ret}", applicationId, collId, retRemove);
            var retAdd = _penumbraAddTemporaryMod.Invoke("BrioChara_Files", collId, modPaths, string.Empty, 0);
            Brio.Log.Debug("[{applicationId}] Setting temp files mod for {collId}, Success: {ret}", applicationId, collId, retAdd);
        }).ConfigureAwait(false);
    }

    public async Task SetManipulationDataAsync(Guid applicationId, Guid collId, string manipulationData)
    {
        if(!IsAvailable) return;

        await _framework.RunOnFrameworkThread(() =>
        {
            Brio.Log.Debug("[{applicationId}] Manip: {data}", applicationId, manipulationData);
            var retAdd = _penumbraAddTemporaryMod.Invoke("BrioChara_Meta", collId, [], manipulationData, 0);
            Brio.Log.Debug("[{applicationId}] Setting temp meta mod for {collId}, Success: {ret}", applicationId, collId, retAdd);
        }).ConfigureAwait(false);
    }

    public async Task Redraw(IGameObject gameObject, bool afterGPose = false)
    {
        if(!IsAvailable) return;

        var redrawType = RedrawType.Redraw;
        if(afterGPose) redrawType = RedrawType.AfterGPose;

        await _framework.RunOnFrameworkThread(() =>
        {
            _penumbraRedraw!.Invoke(gameObject.ObjectIndex, setting: redrawType);
        });
    }

    private void HandlePenumbraRedraw(nint arg1, int arg2)
    {
        Brio.Log.Debug("Penumbra redraw event received.");
        OnPenumbraRedraw?.Invoke(arg2);
    }

    private void OnConfigurationChanged()
        => CheckStatus();

    public override void Dispose()
    {
        _configurationService.OnConfigurationChanged -= OnConfigurationChanged;

        _penumbraGameObjectResourcePathResolved.Dispose();

        _penumbraInitializedSubscriber.Dispose();
        _penumbraDisposedSubscriber.Dispose();
        _penumbraRedrawEvent.Dispose();

        GC.SuppressFinalize(this);
    }
}
