using Brio.Config;
using Brio.Game.Actor;
using Brio.Game.Core;
using Brio.Game.GPose;
using Brio.IPC;
using Brio.MCDF.API.Data;
using Brio.MCDF.Game.FileCache;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Util;
using K4os.Compression.LZ4.Legacy;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Brio.MCDF.Game.Services;

/// <summary>
/// 匯出流程用的角色身分。<b>只保存值型別,不保存 IGameObject。</b>
///
/// <para>
/// 匯出一份 MCDF 中間有 WaitForDrawing、最長 10 秒的存在性輪詢、以及數個 Penumbra / Glamourer / Customize+ 的
/// IPC 往返,前後跨越數百幀。<c>IGameObject</c> 的 <c>Address</c> 是建構當下凍結的、永不重新解析,
/// 角色在這段期間離開 GPose 或消失之後,任何解參考(<c>Name</c> / <c>ObjectIndex</c> / <c>ObjectKind</c> /
/// <c>GameObjectId</c> —— 最後這個還是虛擬函式呼叫)都是懸空讀。
/// AccessViolationException 在 .NET Core 是 corrupted-state exception,<c>try/catch</c> 攔不到。
/// </para>
///
/// <para>
/// 重新繫結時<b>兩個條件都要成立</b>,兩者互相補對方的盲點:
/// <list type="number">
/// <item><c>ObjectIndex</c> + <c>Address</c> 仍在物件表裡(<c>LiveActorRef</c>,只讀物件表自己的指標陣列,
/// 不解參考任何存下來的位址)⇒ 記憶體安全;這一條擋不住的是槽位被回收後換人(ABA)。</item>
/// <item>重查到的物件回報的 <c>GameObjectId</c> 與當初抄下來的相同 ⇒ 身分正確;
/// 這一條擋不住的是同一個 id 出現在多個槽位(GPose 複本),那由第 1 條擋。</item>
/// </list>
/// 所以這裡刻意<b>不</b>用 <c>SearchById</c>:它只比 id、回傳第一個命中,而且對 id 為 0 的角色直接回 null。
/// (套用流程 ApplyDataAsync 走的是 SearchById —— 那裡的語意是「這個角色回來了就繼續套」,
/// 匯出的語意則是「來源中途沒了就中止」,不該改繫結到別的物件上。)
/// </para>
/// </summary>
internal readonly record struct McdfExportActor(string Name, ulong GameObjectId, int ObjectIndex, nint Address);

/// <summary>匯出途中角色從物件表消失。用專屬型別讓上層能與「真的出錯」分開處理。</summary>
internal sealed class McdfExportActorLostException(string message) : Exception(message);

public class MCDFService : IDisposable
{
    public static readonly IImmutableList<string> AllowedFileExtensions = [".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".pbd", ".scd", ".skp", ".shpk", ".kdb"];

    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly TargetService _targetService;
    private readonly ConfigurationService _configurationService;
    private readonly FileCacheService _fileCacheService;
    private readonly DalamudService _dalamudService;
    private readonly ActorRedrawService _actorRedrawService;
    private readonly ActorAppearanceService _actorAppearanceService;
    private readonly TransientResourceService _transientResourceService;

    private readonly CharacterHandlerService _characterHandlerService;
    private readonly GPoseService _gPoseService;

    private readonly PenumbraService _penumbraService;
    private readonly GlamourerService _glamourerService;
    private readonly CustomizePlusService _customizePlusService;

    public bool IsIPCAvailable => _penumbraService.IsAvailable && _glamourerService.IsAvailable;

    public Task<(MareCharaFileHeader LoadedFile, long ExpectedLength)>? LoadedMcdfHeader { get; private set; }

    public Task? McdfApplicationTask { get; private set; }
    public Task? UiBlockingComputation { get; private set; }
    public string DataApplicationProgress { get; private set; } = string.Empty;

    public bool IsSavingMCDF => UiBlockingComputation?.Status == TaskStatus.Running;
    public bool IsApplyingMCDF => _currentApplicationCount > 0;

    //

    private int _currentApplicationCount = 0;

    //private int _globalFileCounter = 0;

    public MCDFService(IFramework framework, IObjectTable objectTable, CharacterHandlerService characterHandlerService, GPoseService gPoseService, ActorAppearanceService actorAppearanceService, ConfigurationService configurationService, FileCacheService fileCacheService, TargetService targetService, ActorRedrawService actorRedrawService, DalamudService dalamudService,
        PenumbraService penumbraService, TransientResourceService transientResourceService, GlamourerService glamourerService, CustomizePlusService customizePlusService)
    {
        _framework = framework;
        _objectTable = objectTable;
        _configurationService = configurationService;
        _fileCacheService = fileCacheService;
        _targetService = targetService;
        _dalamudService = dalamudService;
        _actorRedrawService = actorRedrawService;
        _actorAppearanceService = actorAppearanceService;
        _transientResourceService = transientResourceService;

        _characterHandlerService = characterHandlerService;
        _gPoseService = gPoseService;

        _penumbraService = penumbraService;
        _glamourerService = glamourerService;
        _customizePlusService = customizePlusService;

        _gPoseService.OnGPoseStateChange += OnGPoseStateChange;
    }

    private void OnGPoseStateChange(bool newState)
    {
        if(newState is false)
        {
            try
            {
                if(IsApplyingMCDF)
                {
                    McdfApplicationTask?.Dispose(); //This is not good code and does nothing
                }
            }
            catch(Exception ex)
            {
                Brio.Log.Verbose(ex, "Exception while trying to stop the application process of a MCDF on GPose exit");
            }
        }
    }

    public async Task LoadMCDFHeader(string path)
    {
        await (LoadedMcdfHeader = LoadFileHeader(path));
    }
    public async Task SaveMCDF(string path, string description, IGameObject gameObject)
    {
        // 在 framework 執行緒上、而且確認過物件還在物件表裡之後才抄身分(CaptureActorAsync 自己會做這兩件事),
        // 之後整條匯出流程都不再持有這個 IGameObject。
        var actor = await CaptureActorAsync(gameObject).ConfigureAwait(false);
        if(actor is null)
        {
            Brio.Log.Information("角色已不在物件表裡,取消 MCDF 匯出(階段:抄下匯出目標)。");
            return;
        }

        UiBlockingComputation = Task.Run(async () => await SaveCharaFileAsync(description, path, actor.Value).ConfigureAwait(false));

        await UiBlockingComputation.ConfigureAwait(false);
    }

    public void ApplyMCDFToGPoseTarget()
    {
        var canApply = _targetService.CanApplyMCDFToTarget();

        if(canApply.CanApply)
            _ = ApplyMCDF(canApply.GameObject);
    }

    // Load

    public async Task ApplyMCDF(IGameObject gameObject)
    {
        if(gameObject.Address == IntPtr.Zero || gameObject.ObjectKind != ObjectKind.Player)
            return;

        var name = gameObject.Name.TextValue;

        // 在這裡(物件確定還新鮮)就把身分抄走。之後整條套用流程有數秒的等待與重繪,
        // 期間 IObjectTable 的共用包裝可能已經被改寫成別人,再讀 GameObjectId 就是錯的。
        var gameObjectId = gameObject.GameObjectId;

        _currentApplicationCount++;

        await (McdfApplicationTask = Task.Run(async () =>
        {
            List<string> actuallyExtractedFiles = [];

            Brio.Log.Info("Extracting MCDF");

            try
            {
                Guid applicationId = Guid.NewGuid();

                if(LoadedMcdfHeader == null || !LoadedMcdfHeader.IsCompletedSuccessfully) return;

                var playerChar = await _dalamudService.GetPlayerCharacterAsync().ConfigureAwait(false);
                bool isSelf = playerChar is not null && string.Equals(playerChar.Name.TextValue, name, StringComparison.Ordinal);

                if(isSelf) return;

                long expectedExtractedSize = LoadedMcdfHeader.Result.ExpectedLength;
                var charaFile = LoadedMcdfHeader.Result.LoadedFile;

                DataApplicationProgress = "Extracting MCDF data";
                Brio.Log.Debug($"{DataApplicationProgress}");

                var extractedFiles = McdfExtractFiles(charaFile, expectedExtractedSize, actuallyExtractedFiles);

                foreach(var entry in charaFile.CharaFileData.FileSwaps.SelectMany(k => k.GamePaths, (k, p) => new KeyValuePair<string, string>(p, k.FileSwapPath)))
                {
                    extractedFiles[entry.Key] = entry.Value;
                }

                DataApplicationProgress = "Applying MCDF data";
                Brio.Log.Debug($"{DataApplicationProgress}");

                await ApplyDataAsync(applicationId, (name, gameObjectId), isSelf, charaFile.FilePath,
                    extractedFiles, charaFile.CharaFileData.ManipulationData, charaFile.CharaFileData.GlamourerData,
                    charaFile.CharaFileData.CustomizePlusData, CancellationToken.None).ConfigureAwait(false);
            }
            catch(Exception ex)
            {
                Brio.Log.Warning(ex, "Failed to extract MCDF");
                throw;
            }
            finally
            {
                _currentApplicationCount--;

                // delete extracted files
                foreach(var file in actuallyExtractedFiles)
                {
                    File.Delete(file);
                }
            }
        }));
    }

    public Task<(MareCharaFileHeader loadedCharaFile, long expectedLength)> LoadFileHeader(string filePath)
    {
        try
        {
            using var file = File.OpenRead(filePath);
            using var zipStream = new LZ4Stream(file, LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
            using var reader = new BinaryReader(zipStream);
            var loadedFile = MareCharaFileHeader.FromBinaryReader(filePath, reader);

            long expectedLength = 0;

            if(loadedFile != null)
            {
                var itemNr = 0;
                foreach(var item in loadedFile.CharaFileData.Files)
                {
                    itemNr++;
                    expectedLength += item.Length;
                }
            }
            else
            {
                throw new InvalidOperationException("MCDF Header was null");
            }
            return Task.FromResult((loadedFile, expectedLength));

        }
        catch(Exception ex)
        {
            throw new InvalidOperationException($"Could not parse MCDF header of file {filePath}", ex);
        }
    }

    public Dictionary<string, string> McdfExtractFiles(MareCharaFileHeader? charaFileHeader, long expectedLength, List<string> extractedFiles)
    {
        if(charaFileHeader == null)
            return [];

        using var lz4Stream = new LZ4Stream(File.OpenRead(charaFileHeader.FilePath), LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
        using var reader = new BinaryReader(lz4Stream);
        MareCharaFileHeader.AdvanceReaderToData(reader);

        long totalRead = 0;
        Dictionary<string, string> gamePathToFilePath = new(StringComparer.Ordinal);
        foreach(var fileData in charaFileHeader.CharaFileData.Files)
        {
            var fileName = Path.Combine(_fileCacheService.CacheFolder, "brio_" + fileData.Hash + ".tmp");
            extractedFiles.Add(fileName);
            var length = fileData.Length;
            var bufferSize = length;
            using var fs = File.OpenWrite(fileName);
            using var wr = new BinaryWriter(fs);
            Brio.Log.Debug("Reading {length} of {fileName}", length.ToByteString(), fileName);
            var buffer = reader.ReadBytes(bufferSize);
            wr.Write(buffer);
            wr.Flush();
            wr.Close();
            if(buffer.Length == 0) throw new EndOfStreamException("Unexpected EOF");
            foreach(var path in fileData.GamePaths)
            {
                gamePathToFilePath[path] = fileName;
                Brio.Log.Debug("{path} => {fileName} [{hash}]", path, fileName, fileData.Hash);
            }
            totalRead += length;
            Brio.Log.Debug("Read {read}/{expected} bytes", totalRead.ToByteString(), expectedLength.ToByteString());
        }

        return gamePathToFilePath;
    }

    // 這條路徑從頭到尾有數秒的等待(3 秒 Delay 與兩次 redraw-and-wait),期間目標角色可能離開 GPose 或直接消失。
    // 所以這裡只帶「名字 + GameObjectId」,不帶 IGameObject:每個 await 回來都用 id 重查一次物件表。
    //  - SearchById 回傳的是 IObjectTable 共用的包裝實例(存取時就地改寫 Address,槽位空掉時連改寫都不做),
    //    不能跨幀留著;因此在同一個 framework 回呼裡就用 CreateObjectReference 轉成獨立實例,只給接下來那一步用。
    //  - 查不到就代表角色已經不在物件表裡 ⇒ 中止套用,走 finally 的既有還原路徑,不留半套狀態。
    // ⚠️ AccessViolationException 在 .NET Core 是 corrupted-state exception,try/catch 攔不到,所以只能靠不解參舊位址。
    private async Task ApplyDataAsync(Guid applicationId, (string Name, ulong GameObjectId) tempHandler, bool isSelf, string UID,
        Dictionary<string, string> modPaths, string? manipData, string? glamourerData, string? customizeData, CancellationToken token)
    {
        Guid? cPlusId = null;
        Guid? penumbraCollection = null;
        bool actorLost = false;

        // 以 id 重查目標,並在同一個 framework 回呼裡把物件索引一併讀出來(索引是值,可以安全帶出回呼)。
        // 回傳的 IGameObject 是 CreateObjectReference 產生的獨立實例:Address 是這一刻凍結的,
        // 不會被物件表的共用包裝改寫成別人 —— 但也只保證「緊接著的這一步」有效,不要跨下一個 await 留著。
        //
        // 📌 為什麼這裡用 SearchById 是安全的(2026-09-03 對台服 7.20 執行檔離線驗證,image base 0x140000000):
        //    GameObject::GetGameObjectId 在 0x1408530E0,64 位元結果的高 4 位元組是「種類標籤」:
        //      EntityId(+0x78)!= 0xE0000000              -> Id = EntityId,標籤 0
        //      否則 BaseId(+0x7C)== 0                     -> Id = ObjectIndex(+0x8C) | (2 << 32)
        //      否則 200 <= ObjectIndex <= 448(0x141632670)-> Id = ObjectIndex | (2 << 32)
        //      否則                                        -> Id = BaseId | (1 << 32)
        //    而 GPose 槽位(索引 200 起,ClientObjectManager 的配置迴圈上限 0xF0 ⇒ 200..439)的物件是由
        //    0x1416320EB / 0x1416321D9 / 0x141631F95 建的,三處都寫死 EntityId = 0xE0000000、BaseId = 0
        //    ⇒ GPose 目標的 id 一律是「索引 | 2<<32」。
        //    ⇒ (1) 它永遠不會是 0,所以 Dalamud 對 id 0 直接回 null 那條(ObjectTable.cs:107-108)碰不到;
        //       (2) 標籤在高位,所以它只可能跟另一個標籤 2 的 id 相等,而那是由物件表槽位號決定的
        //           ⇒ 全表唯一,SearchById「回索引最小的第一個命中」不可能挑到別的角色。
        //    ⚠️ 唯一在 200+ 範圍建物件卻不寫 0xE0000000 的是 0x141AC5E42(EntityId = 來源的 BaseId),
        //       但它緊接著把 ObjectKind 設成 3(EventNpc),而 ApplyMCDF 的入口只收 ObjectKind.Player。
        //    ⚠️ 離線證不到的部分:上面驗的是「建立時寫什麼」,不是「之後沒有人改寫 +0x78」。
        //       真的有人改寫的話後果是套到別的角色(明顯的外觀錯誤),不是崩潰 ——
        //       SearchById 拿到的位址來自這一幀的物件表,CreateObjectReference 解參的是活的物件。
        async Task<(IGameObject? Actor, int Index)> ResolveActorAsync(string stage)
        {
            var resolved = await _framework.RunOnFrameworkThread<(IGameObject? Actor, int Index)>(() =>
            {
                if(tempHandler.GameObjectId == 0)
                    return (null, 0);

                var live = _objectTable.SearchById(tempHandler.GameObjectId);
                if(live is null || live.Address == nint.Zero)
                    return (null, 0);

                return (_objectTable.CreateObjectReference(live.Address), (int)live.ObjectIndex);
            }).ConfigureAwait(false);

            if(resolved.Actor is null)
                Brio.Log.Information($"角色已消失,取消 MCDF 套用(階段:{stage};角色「{tempHandler.Name}」,GameObjectId 0x{tempHandler.GameObjectId:X})");

            return resolved;
        }

        try
        {
            DataApplicationProgress = "Reverting previous Application";

            var (actor, _) = await ResolveActorAsync("還原先前的套用").ConfigureAwait(false);
            if(actor is null)
            {
                actorLost = true;
                return;
            }

            await _penumbraService.Redraw(actor);

            (actor, _) = await ResolveActorAsync("重繪並等待").ConfigureAwait(false);
            if(actor is null)
            {
                actorLost = true;
                return;
            }

            await _actorRedrawService.RedrawAndWait(actor);

            (actor, _) = await ResolveActorAsync("還原 Glamourer 與 Customize+").ConfigureAwait(false);
            if(actor is null)
            {
                actorLost = true;
                return;
            }

            _glamourerService.UnlockAndRevertCharacter(actor);
            _glamourerService.UnlockAndRevertCharacterByName(tempHandler.Name);

            _customizePlusService.RemoveTemporaryProfile(actor);

            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);

            DataApplicationProgress = "Applying Penumbra information";

            int idx;
            (actor, idx) = await ResolveActorAsync("套用 Penumbra 資料").ConfigureAwait(false);
            if(actor is null)
            {
                actorLost = true;
                return;
            }

            Brio.Log.Debug($"{DataApplicationProgress} idx:{idx}");

            penumbraCollection = await _penumbraService.CreateTemporaryCollectionAsync($"Brio_{idx}").ConfigureAwait(false);

            await _penumbraService.AssignTemporaryCollectionAsync(penumbraCollection.Value, idx).ConfigureAwait(false);
            await _penumbraService.SetTemporaryModsAsync(applicationId, penumbraCollection.Value, modPaths).ConfigureAwait(false);
            await _penumbraService.SetManipulationDataAsync(applicationId, penumbraCollection.Value, manipData ?? string.Empty).ConfigureAwait(false);

            DataApplicationProgress = "Applying Glamourer and redrawing Character";
            Brio.Log.Debug($"{DataApplicationProgress}");

            (actor, _) = await ResolveActorAsync("套用 Glamourer 並重繪").ConfigureAwait(false);
            if(actor is null)
            {
                actorLost = true;
                return;
            }

            _glamourerService.ApplyAllAsync(actor, glamourerData, applicationId);

            await _actorRedrawService.RedrawAndWait(actor);

            await _penumbraService.RemoveTemporaryCollectionAsync(applicationId, penumbraCollection.Value).ConfigureAwait(false);
            penumbraCollection = null;

            DataApplicationProgress = "Applying Customize+ data";

            (actor, _) = await ResolveActorAsync("套用 Customize+ 資料").ConfigureAwait(false);
            if(actor is null)
            {
                actorLost = true;
                return;
            }

            if(!string.IsNullOrEmpty(customizeData))
            {
                Brio.Log.Debug($"{DataApplicationProgress}");
                cPlusId = await _customizePlusService.SetBodyScaleAsync(actor, customizeData).ConfigureAwait(false);
                //Brio.Log.Warning("LOOK AT ME I' M MR MESEECKS {customizeData}");
                //Brio.Log.Warning(customizeData);
            }
            else
            {
                Brio.Log.Debug($"{DataApplicationProgress} IsNullOrEmpty");
                cPlusId = await _customizePlusService.SetBodyScaleAsync(actor, Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))).ConfigureAwait(false);
            }

            _characterHandlerService.CharacterHandler.Add(new CharacterHolder(tempHandler.GameObjectId, cPlusId, tempHandler.Name));
        }
        finally
        {
            if(actorLost || token.IsCancellationRequested)
            {
                DataApplicationProgress = "Application aborted. Reverting Character...";

                // 已建立但還沒收掉的 Penumbra 暫時集合要拆掉,否則會留下孤兒集合(正常路徑上已經移除並設回 null)。
                if(penumbraCollection.HasValue)
                {
                    try
                    {
                        await _penumbraService.RemoveTemporaryCollectionAsync(applicationId, penumbraCollection.Value).ConfigureAwait(false);
                    }
                    catch(Exception ex)
                    {
                        Brio.Log.Warning(ex, "取消 MCDF 套用時移除 Penumbra 暫時集合失敗");
                    }
                }

                // 既有的還原路徑:holder 只帶 id,RevertMCDF 自己會重查物件表,查不到時退回以名稱還原 Glamourer。
                await _characterHandlerService.RevertMCDF(new CharacterHolder(tempHandler.GameObjectId, cPlusId, tempHandler.Name)).ConfigureAwait(false);
            }

            DataApplicationProgress = string.Empty;
        }
    }

    // Save

    /// <summary>
    /// 在 framework 執行緒上抄下角色身分。先由物件表確認呼叫端給的包裝還指向表裡的物件,再解參考讀名字與 id;
    /// 呼叫端拿著的是好幾幀前的包裝時回 <c>null</c>,不會踩到懸空位址。
    /// </summary>
    private async Task<McdfExportActor?> CaptureActorAsync(IGameObject? gameObject)
        => await _framework.RunOnFrameworkThread(() => CaptureActor(gameObject)).ConfigureAwait(false);

    private McdfExportActor? CaptureActor(IGameObject? gameObject)
    {
        if(gameObject is null)
            return null;

        // go.Address 只讀包裝自己的欄位、GetObjectAddress 只讀物件表自己的指標陣列,兩者都不解參考。
        var address = LiveActorRef.FromAddress(_objectTable, gameObject.Address).Address;
        if(address == nint.Zero)
            return null;

        // 位址確認還在物件表裡了,這時候解參考才安全。CreateObjectReference 產生的是獨立包裝,
        // 不會被物件表槽位的共用實例就地改寫。
        var live = _objectTable.CreateObjectReference(address);
        if(live is null)
            return null;

        return new McdfExportActor(live.Name.TextValue, live.GameObjectId, live.ObjectIndex, address);
    }

    /// <summary>
    /// 角色現在還在物件表裡、而且還是同一個角色時,回傳一個當場產生的獨立包裝,否則回 <c>null</c>。
    /// <b>必須在 framework 執行緒上呼叫,而且拿到的包裝只能在同一個呼叫堆疊之內用完,不可以再帶過幀。</b>
    /// </summary>
    private IGameObject? ResolveExportActor(McdfExportActor actor)
    {
        // 第一關:位址還在物件表裡嗎(不解參考)。
        var address = new LiveActorRef(_objectTable, actor.ObjectIndex, actor.Address).Address;
        if(address == nint.Zero)
            return null;

        var live = _objectTable.CreateObjectReference(address);
        if(live is null)
            return null;

        // 第二關:還在的是不是同一個角色(擋槽位被回收之後換人)。此時位址已確認在表裡,解參考是安全的。
        if(live.GameObjectId != actor.GameObjectId)
            return null;

        return live;
    }

    /// <summary>
    /// 在 framework 執行緒上重查角色,<b>並且在同一個回呼裡就把要用的東西讀完</b>。
    /// 這樣「確認還活著」與「解參考」之間不會夾一次執行緒切換,不會有中間跨幀的空窗。
    /// 查不到就擲 <see cref="McdfExportActorLostException"/> 中止整份匯出。
    /// </summary>
    private async Task<T> WithLiveActorAsync<T>(McdfExportActor actor, string stage, Func<IGameObject, T> read)
    {
        var result = await _framework.RunOnFrameworkThread(() =>
        {
            var live = ResolveExportActor(actor);
            return live is null ? (false, default(T)!) : (true, read(live));
        }).ConfigureAwait(false);

        if(result.Item1 == false)
            throw new McdfExportActorLostException(ActorLostMessage(actor, stage));

        return result.Item2;
    }

    private static string ActorLostMessage(McdfExportActor actor, string stage)
        => $"角色已消失,取消 MCDF 匯出(階段:{stage};角色「{actor.Name}」,物件索引 {actor.ObjectIndex},GameObjectId 0x{actor.GameObjectId:X})";

    private async Task<IGameObject> ResolveExportActorOrThrowAsync(McdfExportActor actor, string stage)
    {
        var live = await _framework.RunOnFrameworkThread(() => ResolveExportActor(actor)).ConfigureAwait(false);
        if(live is null)
            throw new McdfExportActorLostException(ActorLostMessage(actor, stage));

        return live;
    }

    public async Task ExportSelfAsMCDFAsync(string description, string filePath)
    {
        var gposeTaget = await _framework.RunOnTick(() =>
        {
            if(_dalamudService.GetIsPlayerPresent())
            {
                return _dalamudService.GetPlayerCharacter();
            }
            return null;
        });

        if(gposeTaget is not null && gposeTaget.Address == IntPtr.Zero)
            return;

        await Task.Run(async () => await SaveCharaFileAsync(description, filePath, gposeTaget!).ConfigureAwait(false));
    }
    public async Task ExportTargetAsMCDFAsync(string description, string filePath)
    {
        var gposeTaget = await _framework.RunOnFrameworkThread(_targetService.CanApplyMCDFToTarget);

        if(gposeTaget.CanApply == false)
            return;

        await Task.Run(async () => await SaveCharaFileAsync(description, filePath, gposeTaget.GameObject).ConfigureAwait(false));
    }

    internal async Task SaveCharaFileAsync(string description, string filePath, IGameObject gameObject)
    {
        var actor = await CaptureActorAsync(gameObject).ConfigureAwait(false);
        if(actor is null)
        {
            Brio.Log.Information("角色已不在物件表裡,取消 MCDF 匯出(階段:抄下匯出目標)。");
            return;
        }

        await SaveCharaFileAsync(description, filePath, actor.Value).ConfigureAwait(false);
    }

    private async Task SaveCharaFileAsync(string description, string filePath, McdfExportActor actor)
    {
        var tempFilePath = filePath + ".tmp";

        try
        {
            Brio.Log.Info("Starting MCDF export...");

            var data = await CreatePlayerData(actor).ConfigureAwait(false);
            if(data == null) return;

            MareCharaFileData mareCharaFileData = new MareCharaFileData(_fileCacheService, "", data);
            MareCharaFileHeader output = new(MareCharaFileHeader.CurrentVersion, mareCharaFileData);

            // Why do I need this and Mare didn't huh?!?
            await Task.Run(async () =>
            {
                using var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                using var lz4 = new LZ4Stream(fs, LZ4StreamMode.Compress, LZ4StreamFlags.HighCompression);
                using var writer = new BinaryWriter(lz4);
                output.WriteToStream(writer);

                foreach(var item in output.CharaFileData.Files)
                {
                    var file = _fileCacheService.GetFileCacheByHash(item.Hash)!;

                    var fsRead = File.OpenRead(file.ResolvedFilepath);
                    await using(fsRead.ConfigureAwait(false))
                    {
                        using var br = new BinaryReader(fsRead);
                        byte[] buffer = new byte[item.Length];
                        br.Read(buffer, 0, item.Length);
                        writer.Write(buffer);
                    }
                }

                writer.Flush();
                await lz4.FlushAsync().ConfigureAwait(false);
                await fs.FlushAsync().ConfigureAwait(false);
                fs.Close();
                File.Move(tempFilePath, filePath, true);

                Brio.Log.Info("MCDF export complete!");
            });
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "Failure Saving Mare Chara File, deleting output");
            File.Delete(tempFilePath);
        }
    }

    public async Task<API.Data.CharacterData?> CreatePlayerData(IGameObject gameObject)
    {
        var actor = await CaptureActorAsync(gameObject).ConfigureAwait(false);
        if(actor is null)
        {
            Brio.Log.Information("角色已不在物件表裡,取消建立 MCDF 資料。");
            return null;
        }

        return await CreatePlayerData(actor.Value).ConfigureAwait(false);
    }

    private async Task<API.Data.CharacterData?> CreatePlayerData(McdfExportActor actor)
    {
        CharacterDataEX newCdata = new();

        CharacterDataFragment? fragment;
        try
        {
            fragment = await BuildCharacterData(actor, CancellationToken.None).ConfigureAwait(false);
        }
        catch(McdfExportActorLostException ex)
        {
            // 角色中途消失:整份匯出中止,連空檔都不要寫出去。
            Brio.Log.Information(ex.Message);
            return null;
        }

        newCdata.SetFragment(API.Data.Enum.ObjectKind.Player, fragment);

        if(newCdata.FileReplacements.TryGetValue(API.Data.Enum.ObjectKind.Player, out var playerData) && playerData != null)
        {
            foreach(var data in playerData.Select(g => g.GamePaths))
            {
                data.RemoveWhere(g => g.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)
                    || g.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase)
                    || g.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)
                    || (g.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)
                        && !g.Contains("/weapon/", StringComparison.OrdinalIgnoreCase)
                        && !g.Contains("/equipment/", StringComparison.OrdinalIgnoreCase))
                    || (g.EndsWith(".atex", StringComparison.OrdinalIgnoreCase)
                        && !g.Contains("/weapon/", StringComparison.OrdinalIgnoreCase)
                        && !g.Contains("/equipment/", StringComparison.OrdinalIgnoreCase)));
            }

            playerData.RemoveWhere(g => g.GamePaths.Count == 0);
        }

        return newCdata.ToAPI();
    }

    public async Task<CharacterDataFragment?> BuildCharacterData(IGameObject playerRelatedObject, CancellationToken token)
    {
        var actor = await CaptureActorAsync(playerRelatedObject).ConfigureAwait(false);
        if(actor is null) return null;

        return await BuildCharacterData(actor.Value, token).ConfigureAwait(false);
    }

    private async Task<CharacterDataFragment?> BuildCharacterData(McdfExportActor actor, CancellationToken token)
    {
        if(IsIPCAvailable is false)
        {
            throw new InvalidOperationException("Penumbra or Glamourer is not connected");
        }

        bool pointerIsZero = true;
        try
        {
            pointerIsZero = await CheckForNullDrawObject(actor).ConfigureAwait(false);
        }
        catch(Exception ex)
        {
            pointerIsZero = true;
            Brio.Log.Warning(ex, "Could not create data for {object}", actor.Name);
        }

        if(pointerIsZero)
        {
            Brio.Log.Debug("Pointer was zero for {object}", actor.Name);
            return null;
        }

        try
        {
            return await CreateCharacterData(actor, token).ConfigureAwait(false);
        }
        catch(OperationCanceledException)
        {
            Brio.Log.Debug("Cancelled creating Character data for {object}", actor.Name);
            throw;
        }
        catch(McdfExportActorLostException)
        {
            // 🔴 這一條必須排在下面的 catch(Exception) 前面:被吞掉的話上層會以為只是「沒有資料」,
            //    然後照樣寫出一個空的 MCDF 檔。這裡讓它往上傳,由 CreatePlayerData 中止整份匯出。
            throw;
        }
        catch(Exception e)
        {
            Brio.Log.Warning(e, "Failed to create {object} data", actor.Name);
        }

        return null;
    }

    private async Task<bool> CheckForNullDrawObject(McdfExportActor actor)
    {
        return await _framework.RunOnFrameworkThread(() => CheckForNullDrawObjectUnsafe(actor)).ConfigureAwait(false);
    }

    // 原本這支收的是一個裸 IntPtr,而那個位址是好幾幀之前從包裝物件讀出來的 ——
    // 角色在這段期間消失就是解參考懸空位址,AccessViolationException 在 .NET Core 攔不到。
    // 改成在同一個 framework 回呼裡先由物件表確認位址還在(只讀指標陣列,不解參考),再解參考。
    private unsafe bool CheckForNullDrawObjectUnsafe(McdfExportActor actor)
    {
        var native = new LiveActorRef(_objectTable, actor.ObjectIndex, actor.Address).Character;
        if(native is null)
            return true;

        return native->GameObject.DrawObject == null;
    }

    // 🔴 這條路徑從頭到尾跨越數百幀:WaitForDrawing、最長 10 秒的存在性輪詢,以及 Penumbra / Glamourer /
    //    Customize+ 的 IPC 往返。所以這裡不留 IGameObject,每個要解參考的地方都重查一次;
    //    查不到就擲 McdfExportActorLostException,由 CreatePlayerData 接住並中止整份匯出(不寫出空檔)。
    private async Task<CharacterDataFragment> CreateCharacterData(McdfExportActor actor, CancellationToken ct)
    {
        var objectKind = await WithLiveActorAsync(actor, "讀取角色種類", go => go.ObjectKind).ConfigureAwait(false);
        CharacterDataFragment fragment = objectKind == ObjectKind.Player ? new CharacterDataFragmentPlayer() : new();

        Brio.Log.Verbose("Building character data for {obj}", actor.Name);

        // wait until chara is not drawing and present so nothing spontaneously explodes
        // (WaitForDrawing 只讀包裝物件自己的 Address 欄位,之後每一幀自己向物件表重查,不會解參考過期的位址。)
        var drawTarget = await ResolveExportActorOrThrowAsync(actor, "等待角色繪製完成").ConfigureAwait(false);
        await _actorRedrawService.WaitForDrawing(drawTarget).ConfigureAwait(false);

        // 🔴 原本這個迴圈的條件是 _dalamudService.IsObjectPresentAsync(playerRelatedObject),而它底下是
        //    IGameObject.IsValid() —— 本 pin 的 Dalamud 那支只檢查「有沒有登入」,對已經消失的角色永遠回 true,
        //    所以這個迴圈從來不會因為「角色不在了」而多等一次,也從來不會因此結束。
        //    改成真的去物件表查(只讀指標陣列,不解參考存下來的位址)。
        int totalWaitTime = 10000;
        while(totalWaitTime > 0)
        {
            var present = await _framework.RunOnFrameworkThread(() => ResolveExportActor(actor) is not null).ConfigureAwait(false);
            if(present)
                break;

            Brio.Log.Debug("Character is null but it shouldn't be, waiting");
            await Task.Delay(50, ct).ConfigureAwait(false);
            totalWaitTime -= 50;
        }

        // Make sure Brio can't MCDF if the actor had a "Mare" actor loaded on it by cheaking for lock from glamourer
        // (重查與 CheckForLock 放在同一個 framework 回呼裡:CheckForLock 內部會讀 ObjectIndex,那是解參考。)
        if(await WithLiveActorAsync(actor, "檢查 Glamourer 鎖定", go => _glamourerService.CheckForLock(go)).ConfigureAwait(false))
        {
            Brio.Log.Information("Unable to apply MCDF, Actor is Locked by Glamourer");
            Brio.NotifyError("Unable to apply MCDF! Actor is Locked! Are you using a sync service?!");

            throw new Exception("Glamourer has Lock");
        }

        ct.ThrowIfCancellationRequested();

        Dictionary<string, List<ushort>>? boneIndices =
            objectKind != ObjectKind.Player
            ? null
            : await WithLiveActorAsync(actor, "讀取骨架骨骼索引", GetSkeletonBoneIndices).ConfigureAwait(false);

        DateTime start = DateTime.UtcNow;

        // penumbra call, it's currently broken (How is this broken?) (KEN)
        Dictionary<string, HashSet<string>>? resolvedPaths;

        // 物件索引在「確認角色還在物件表裡」的同一個 framework 回呼裡讀出來,再交給 Penumbra ——
        // 原本是把 IGameObject 交過去、由 Penumbra 自己在之後某一幀才去解參考它的 ObjectIndex。
        var penumbraIndex = await WithLiveActorAsync(actor, "取得 Penumbra 資源路徑", go => go.ObjectIndex).ConfigureAwait(false);
        resolvedPaths = (await _penumbraService.GetCharacterData(penumbraIndex).ConfigureAwait(false));
        if(resolvedPaths == null) throw new InvalidOperationException("Penumbra returned null data");

        ct.ThrowIfCancellationRequested();

        fragment.FileReplacements = [.. new HashSet<FileReplacement>(resolvedPaths.Select(c => new FileReplacement([.. c.Value], c.Key)), FileReplacementComparer.Instance).Where(p => p.HasFileReplacement)];
        fragment.FileReplacements.RemoveWhere(c => c.GamePaths.Any(g => !AllowedFileExtensions.Any(e => g.EndsWith(e, StringComparison.OrdinalIgnoreCase))));

        ct.ThrowIfCancellationRequested();

        Brio.Log.Verbose("== Static Replacements ==");
        foreach(var replacement in fragment.FileReplacements.Where(i => i.HasFileReplacement).OrderBy(i => i.GamePaths.First(), StringComparer.OrdinalIgnoreCase))
        {
            Brio.Log.Verbose("=> {repl}", replacement);
            ct.ThrowIfCancellationRequested();
        }

        await _transientResourceService.WaitForRecording(ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        Brio.Log.Verbose("Handling transient update for {obj}", actor.Name);

        // remove all potentially gathered paths from the transient resource manager that are resolved through static resolving
        _transientResourceService.ClearTransientPaths(API.Data.Enum.ObjectKind.Player, fragment.FileReplacements.SelectMany(c => c.GamePaths).ToList());

        // get all remaining paths and resolve them
        var transientPaths = ManageSemiTransientData(API.Data.Enum.ObjectKind.Player);
        var resolvedTransientPaths = await GetFileReplacementsFromPaths(transientPaths, new HashSet<string>(StringComparer.Ordinal)).ConfigureAwait(false);

        Brio.Log.Verbose("== Transient Replacements ==");
        foreach(var replacement in resolvedTransientPaths.Select(c => new FileReplacement([.. c.Value], c.Key)).OrderBy(f => f.ResolvedPath, StringComparer.Ordinal))
        {
            Brio.Log.Verbose("=> {repl}", replacement);
            fragment.FileReplacements.Add(replacement);
        }

        // clean up all semi transient resources that don't have any file replacement (aka null resolve)
        _transientResourceService.CleanUpSemiTransientResources(API.Data.Enum.ObjectKind.Player, [.. fragment.FileReplacements]);

        ct.ThrowIfCancellationRequested();

        // make sure we only return data that actually has file replacements
        fragment.FileReplacements = new HashSet<FileReplacement>(fragment.FileReplacements.Where(v => v.HasFileReplacement).OrderBy(v => v.ResolvedPath, StringComparer.Ordinal), FileReplacementComparer.Instance);

        // gather up data from ipc
        //Task<string> getHeelsOffset = _ipcManager.Heels.GetOffsetAsync();
        //Task<string> getHonorificTitle = _ipcManager.Honorific.GetTitle();

        // GetCharacterCustomizationAsync 收的是位址,它自己會在 framework 執行緒上先向物件表確認再解參考,
        // 所以這裡把當初抄下來的位址交過去就行(見 GlamourerService)。
        Task<string> getGlamourerData = _glamourerService.GetCharacterCustomizationAsync(actor.Address);

        // ⚠️ Customize+ 這一支內部自己會切到 framework 執行緒才解參考,沒有辦法從外面把「重查」塞進它那個回呼裡,
        //    所以這裡仍然有一次切換的空窗。至少改成當場重查出來的獨立包裝,不再是數百幀前的那一個。
        var customizeTarget = await ResolveExportActorOrThrowAsync(actor, "取得 Customize+ 縮放").ConfigureAwait(false);
        Task<string?> getCustomizeData = _customizePlusService.GetScaleAsync(customizeTarget);
        
        fragment.GlamourerString = await getGlamourerData.ConfigureAwait(false);
        Brio.Log.Verbose("Glamourer is now: {data}", fragment.GlamourerString);
       
        fragment.CustomizePlusScale = await getCustomizeData.ConfigureAwait(false) ?? string.Empty;
        Brio.Log.Verbose("Customize is now: {data}", fragment.CustomizePlusScale);

        if(objectKind == ObjectKind.Player)
        {
            var playerFragment = (fragment as CharacterDataFragmentPlayer)!;
            playerFragment.ManipulationString = _penumbraService.GetMetaManipulations();

            //    playerFragment!.HonorificData = await getHonorificTitle.ConfigureAwait(false);
            //    Brio.Log.Verbose("Honorific is now: {data}", playerFragment!.HonorificData);

            //    playerFragment!.HeelsData = await getHeelsOffset.ConfigureAwait(false);
            //    Brio.Log.Verbose("Heels is now: {heels}", playerFragment!.HeelsData);

            //    playerFragment!.MoodlesData = await _ipcManager.Moodles.GetStatusAsync(playerRelatedObject.Address).ConfigureAwait(false) ?? string.Empty;
            //    Brio.Log.Verbose("Moodles is now: {moodles}", playerFragment!.MoodlesData);

            //    playerFragment!.PetNamesData = _ipcManager.PetNames.GetLocalNames();
            //    Brio.Log.Verbose("Pet Nicknames is now: {petnames}", playerFragment!.PetNamesData);
        }

        ct.ThrowIfCancellationRequested();

        var toCompute = fragment.FileReplacements.Where(f => !f.IsFileSwap).ToArray();
        Brio.Log.Verbose("Getting Hashes for {amount} Files", toCompute.Length);
        var computedPaths = _fileCacheService.GetFileCachesByPaths(toCompute.Select(c => c.ResolvedPath).ToArray());
        foreach(var file in toCompute)
        {
            ct.ThrowIfCancellationRequested();
            file.Hash = computedPaths[file.ResolvedPath]?.Hash ?? string.Empty;
        }

        var removed = fragment.FileReplacements.RemoveWhere(f => !f.IsFileSwap && string.IsNullOrEmpty(f.Hash));
        if(removed > 0)
        {
            Brio.Log.Verbose("Removed {amount} of invalid files", removed);
        }

        ct.ThrowIfCancellationRequested();

        if(objectKind == ObjectKind.Player)
        {
            try
            {
                await VerifyPlayerAnimationBones(boneIndices, (fragment as CharacterDataFragmentPlayer)!, ct).ConfigureAwait(false);
            }
            catch(OperationCanceledException e)
            {
                Brio.Log.Debug(e, "Cancelled during player animation verification");
                throw;
            }
            catch(Exception e)
            {
                Brio.Log.Warning(e, "Failed to verify player animations, continuing without further verification");
            }
        }

        Brio.Log.Info("Building character data for {obj} took {time}ms", objectKind, TimeSpan.FromTicks(DateTime.UtcNow.Ticks - start.Ticks).TotalMilliseconds);

        return fragment;
    }

    private async Task VerifyPlayerAnimationBones(Dictionary<string, List<ushort>>? boneIndices, CharacterDataFragmentPlayer fragment, CancellationToken ct)
    {
        if(boneIndices == null) return;

        foreach(var kvp in boneIndices)
        {
            Brio.Log.Verbose("Found {skellyname} ({idx} bone indices) on player: {bones}", kvp.Key, kvp.Value.Any() ? kvp.Value.Max() : 0, string.Join(',', kvp.Value));
        }

        if(boneIndices.All(u => u.Value.Count == 0)) return;

        int noValidationFailed = 0;
        foreach(var file in fragment.FileReplacements.Where(f => !f.IsFileSwap && f.GamePaths.First().EndsWith("pap", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            ct.ThrowIfCancellationRequested();

            var skeletonIndices = await _framework.RunOnFrameworkThread(() => GetBoneIndicesFromPap(file.Hash)).ConfigureAwait(false);
            bool validationFailed = false;
            if(skeletonIndices != null)
            {
                // 105 is the maximum vanilla skellington spoopy bone index
                if(skeletonIndices.All(k => k.Value.Max() <= 105))
                {
                    Brio.Log.Verbose("All indices of {path} are <= 105, ignoring", file.ResolvedPath);
                    continue;
                }

                Brio.Log.Verbose("Verifying bone indices for {path}, found {x} skeletons", file.ResolvedPath, skeletonIndices.Count);

                foreach(var boneCount in skeletonIndices.Select(k => k).ToList())
                {
                    if(boneCount.Value.Max() > boneIndices.SelectMany(b => b.Value).Max())
                    {
                        Brio.Log.Debug("Found more bone indices on the animation {path} skeleton {skl} (max indice {idx}) than on any player related skeleton (max indice {idx2})",
                             file.ResolvedPath, boneCount.Key, boneCount.Value.Max(), boneIndices.SelectMany(b => b.Value).Max());
                        validationFailed = true;
                        break;
                    }
                }
            }

            if(validationFailed)
            {
                noValidationFailed++;
                Brio.Log.Verbose("Removing {file} from sent file replacements and transient data", file.ResolvedPath);
                fragment.FileReplacements.Remove(file);
                foreach(var gamePath in file.GamePaths)
                {
                    _transientResourceService.RemoveTransientResource(API.Data.Enum.ObjectKind.Player, gamePath);
                }
            }
        }

        if(noValidationFailed > 0)
        {
            //_mareMediator.Publish(new NotificationMessage("Invalid Skeleton Setup",
            //    $"Your client is attempting to send {noValidationFailed} animation files with invalid bone data. Those animation files have been removed from your sent data. " +
            //    $"Verify that you are using the correct skeleton for those animation files (Check /xllog for more information).",
            //    NotificationType.Warning, TimeSpan.FromSeconds(10)));
        }
    }

    private async Task<IReadOnlyDictionary<string, string[]>> GetFileReplacementsFromPaths(HashSet<string> forwardResolve, HashSet<string> reverseResolve)
    {
        var forwardPaths = forwardResolve.ToArray();
        var reversePaths = reverseResolve.ToArray();
        Dictionary<string, List<string>> resolvedPaths = new(StringComparer.Ordinal);
        var (forward, reverse) = await _penumbraService.ResolvePathsAsync(forwardPaths, reversePaths).ConfigureAwait(false);
        for(int i = 0; i < forwardPaths.Length; i++)
        {
            var filePath = forward[i].ToLowerInvariant();
            if(resolvedPaths.TryGetValue(filePath, out var list))
            {
                list.Add(forwardPaths[i].ToLowerInvariant());
            }
            else
            {
                resolvedPaths[filePath] = [forwardPaths[i].ToLowerInvariant()];
            }
        }

        for(int i = 0; i < reversePaths.Length; i++)
        {
            var filePath = reversePaths[i].ToLowerInvariant();
            if(resolvedPaths.TryGetValue(filePath, out var list))
            {
                list.AddRange(reverse[i].Select(c => c.ToLowerInvariant()));
            }
            else
            {
                resolvedPaths[filePath] = [.. reverse[i].Select(c => c.ToLowerInvariant())];
            }
        }

        return resolvedPaths.ToDictionary(k => k.Key, k => k.Value.ToArray(), StringComparer.OrdinalIgnoreCase).AsReadOnly();
    }

    private HashSet<string> ManageSemiTransientData(API.Data.Enum.ObjectKind objectKind)
    {
        _transientResourceService.PersistTransientResources(objectKind);

        HashSet<string> pathsToResolve = new(StringComparer.Ordinal);
        foreach(var path in _transientResourceService.GetSemiTransientResources(objectKind).Where(path => !string.IsNullOrEmpty(path)))
        {
            pathsToResolve.Add(path);
        }

        return pathsToResolve;
    }

    // 🔴 這支是 public 的,呼叫端可能拿著好幾幀之前建構的包裝(IGameObject 的 Address 是建構當下凍結的)。
    //    handler.Address 只讀包裝自己的欄位、GetObjectAddress 只讀物件表自己的指標陣列,兩者都不解參考,
    //    所以先做這個確認永遠安全;確認過之後拿到的指標只能在這個呼叫堆疊之內用完。
    public unsafe Dictionary<string, List<ushort>>? GetSkeletonBoneIndices(IGameObject handler)
    {
        var native = LiveActorRef.FromAddress(_objectTable, handler.Address).Character;
        if(native is null) return null;

        // 原本沒有這個判空:DrawObject 為 null 時直接呼叫 GetModelType() 就是對 null 解參考。
        var drawObject = native->GameObject.DrawObject;
        if(drawObject is null) return null;

        var chara = (CharacterBase*)drawObject;
        if(chara->GetModelType() != CharacterBase.ModelType.Human) return null;
        var resHandles = chara->Skeleton->SkeletonResourceHandles;
        Dictionary<string, List<ushort>> outputIndices = [];
        try
        {
            for(int i = 0; i < chara->Skeleton->PartialSkeletonCount; i++)
            {
                var handle = *(resHandles + i);
                //Brio.Log.Verbose("Iterating over SkeletonResourceHandle #{i}:{x}", i, ((nint)handle).ToString("X"));
                if((nint)handle == nint.Zero) continue;
                var curBones = handle->BoneCount;
                // this is unrealistic, the filename shouldn't ever be that long
                if(handle->FileName.Length > 1024) continue;
                var skeletonName = handle->FileName.ToString();
                if(string.IsNullOrEmpty(skeletonName)) continue;
                outputIndices[skeletonName] = new();
                for(ushort boneIdx = 0; boneIdx < curBones; boneIdx++)
                {
                    var boneName = handle->HavokSkeleton->Bones[boneIdx].Name.String;
                    if(boneName == null) continue;
                    outputIndices[skeletonName].Add((ushort)(boneIdx + 1));
                }
            }
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "Could not process skeleton data");
        }

        return (outputIndices.Count != 0 && outputIndices.Values.All(u => u.Count > 0)) ? outputIndices : null;
    }

    public unsafe Dictionary<string, List<ushort>>? GetBoneIndicesFromPap(string hash)
    {
        if(_configurationService.Configuration.MCDF.DataStorage.BonesDictionary.TryGetValue(hash, out var bones)) return bones;

        var cacheEntity = _fileCacheService.GetFileCacheByHash(hash);
        if(cacheEntity == null) return null;

        using BinaryReader reader = new BinaryReader(File.Open(cacheEntity.ResolvedFilepath, FileMode.Open, FileAccess.Read, FileShare.Read));

        // most of this shit is from vfxeditor, surely nothing will change in the pap format :copium:
        reader.ReadInt32(); // ignore
        reader.ReadInt32(); // ignore
        reader.ReadInt16(); // read 2 (num animations)
        reader.ReadInt16(); // read 2 (modelid)
        var type = reader.ReadByte();// read 1 (type)
        if(type != 0) return null; // it's not human, just ignore it, whatever

        reader.ReadByte(); // read 1 (variant)
        reader.ReadInt32(); // ignore
        var havokPosition = reader.ReadInt32();
        var footerPosition = reader.ReadInt32();
        var havokDataSize = footerPosition - havokPosition;
        reader.BaseStream.Position = havokPosition;
        var havokData = reader.ReadBytes(havokDataSize);
        if(havokData.Length <= 8) return null; // no havok data

        var output = new Dictionary<string, List<ushort>>(StringComparer.OrdinalIgnoreCase);
        var tempHavokDataPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()) + ".hkx";
        var tempHavokDataPathAnsi = Marshal.StringToHGlobalAnsi(tempHavokDataPath);

        try
        {
            File.WriteAllBytes(tempHavokDataPath, havokData);

            var loadoptions = stackalloc hkSerializeUtil.LoadOptions[1];
            loadoptions->TypeInfoRegistry = hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry();
            loadoptions->ClassNameRegistry = hkBuiltinTypeRegistry.Instance()->GetClassNameRegistry();
            loadoptions->Flags = new hkFlags<hkSerializeUtil.LoadOptionBits, int>
            {
                Storage = (int)(hkSerializeUtil.LoadOptionBits.Default)
            };

            var resource = hkSerializeUtil.LoadFromFile((byte*)tempHavokDataPathAnsi, null, loadoptions);
            if(resource == null)
            {
                throw new InvalidOperationException("Resource was null after loading");
            }

            var rootLevelName = @"hkRootLevelContainer"u8;
            fixed(byte* n1 = rootLevelName)
            {
                var container = (hkRootLevelContainer*)resource->GetContentsPointer(n1, hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry());
                var animationName = @"hkaAnimationContainer"u8;
                fixed(byte* n2 = animationName)
                {
                    var animContainer = (hkaAnimationContainer*)container->findObjectByName(n2, null);
                    for(int i = 0; i < animContainer->Bindings.Length; i++)
                    {
                        var binding = animContainer->Bindings[i].ptr;
                        var boneTransform = binding->TransformTrackToBoneIndices;
                        string name = binding->OriginalSkeletonName.String! + "_" + i;
                        output[name] = [];
                        for(int boneIdx = 0; boneIdx < boneTransform.Length; boneIdx++)
                        {
                            output[name].Add((ushort)boneTransform[boneIdx]);
                        }
                        output[name].Sort();
                    }
                }
            }
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "Could not load havok file in {path}", tempHavokDataPath);
        }
        finally
        {
            Marshal.FreeHGlobal(tempHavokDataPathAnsi);
            File.Delete(tempHavokDataPath);
        }

        _configurationService.Configuration.MCDF.DataStorage.BonesDictionary[hash] = output;
        _configurationService.Save();
        return output;
    }

    public void Dispose()
    {
        _gPoseService.OnGPoseStateChange -= OnGPoseStateChange;

        GC.SuppressFinalize(this);
    }
}
