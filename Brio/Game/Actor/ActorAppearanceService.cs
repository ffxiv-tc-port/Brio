using Brio.Config;
using Brio.Core;
using Brio.Entities;
using Brio.Game.Actor.Appearance;
using Brio.Game.Actor.Extensions;
using Brio.Game.Camera;
using Brio.Game.Core;
using Brio.Game.GPose;
using Brio.IPC;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static Brio.Game.Actor.ActorRedrawService;
using DrawDataContainer = FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer;

namespace Brio.Game.Actor;

public class ActorAppearanceService : IDisposable
{
    private readonly GPoseService _gPoseService;
    private readonly ConfigurationService _configurationService;
    private readonly ActorRedrawService _redrawService;
    private readonly GlamourerService _glamourerService;
    private readonly VirtualCameraManager _virtualCameraManager;
    private readonly EntityManager _entityManager;
    private readonly IObjectTable _objectTable;
    private readonly PenumbraService _penumbraService;
    private readonly CustomizePlusService _customizePlusService;
    private readonly ActorRedrawService _actorRedrawService;
    private readonly CharacterHandlerService _characterHandlerService;
    private readonly DalamudService _dalamudService;

    /// <summary>
    /// 主執行緒閘門。🔴 這支服務的原生存取有一半跑在 <c>await</c> 之後 —— 那時在執行緒池上,
    /// 不在框架執行緒上(理由見 <see cref="Redraw"/> 的註解)。閘門在已經是框架執行緒時就地執行,
    /// 行為逐字不變;<c>IsFrameworkUnloading</c> 為真且從別的執行緒進來時回「做不到」值,
    /// 因為 Dalamud 在卸載期會就地執行委派(<c>Dalamud/Game/Framework.cs:167-211</c>),轉派完全失效。
    /// </summary>
    private readonly IpcFrameworkGate _gate;

    private delegate byte EnforceKindRestrictionsDelegate(nint a1, nint a2);
    private readonly Hook<EnforceKindRestrictionsDelegate>? _enforceKindRestrictionsHook;

    private delegate nint UpdateWetnessDelegate(nint a1);
    private readonly Hook<UpdateWetnessDelegate>? _updateWetnessHook;

    private unsafe delegate nint UpdateTintDelegate(nint charaBase, nint tint);
    private readonly Hook<UpdateTintDelegate>? _updateTintHook;


    public bool CanTint => _configurationService.Configuration.Appearance.EnableTinting;

    public unsafe ActorAppearanceService(GPoseService gPoseService, VirtualCameraManager virtualCameraManager, CharacterHandlerService characterHandlerService,
        IObjectTable objectTable, CustomizePlusService customizePlusService, PenumbraService penumbraService, ActorRedrawService actorRedrawService, DalamudService dalamudService,
        ConfigurationService configurationService, ActorRedrawService redrawService, GlamourerService glamourerService, EntityManager entityManager,
        ISigScanner sigScanner, IGameInteropProvider hooks, IFramework framework)
    {
        _gate = new IpcFrameworkGate(framework);
        _gPoseService = gPoseService;
        _configurationService = configurationService;
        _redrawService = redrawService;
        _glamourerService = glamourerService;
        _customizePlusService = customizePlusService;
        _entityManager = entityManager;
        _objectTable = objectTable;
        _virtualCameraManager = virtualCameraManager;
        _penumbraService = penumbraService;
        _actorRedrawService = actorRedrawService;
        _dalamudService = dalamudService;
        _characterHandlerService = characterHandlerService;

        _enforceKindRestrictionsHook = NativeBinding.ScanHook<EnforceKindRestrictionsDelegate>(sigScanner, hooks,
            "E8 ?? ?? ?? ?? 41 B0 ?? 48 8B D6 48 8B", EnforceKindRestrictionsDetour, "外觀種族限制 EnforceKindRestrictions");

        _updateWetnessHook = NativeBinding.ScanHook<UpdateWetnessDelegate>(sigScanner, hooks,
            "40 53 48 83 EC ?? 48 8B 01 48 8B D9 FF 90 ?? ?? ?? ?? 48 85 C0 74 ?? 48 8B 03 48 8B CB 48 83 C4", UpdateWetnessDetour, "濕潤度 UpdateWetness");

        // CharacterBase 虛擬表槽 0xC0/8 = 24。虛擬表指標解不出來時讀它是 AccessViolation,先判空。
        nint updateTintHookAddress = nint.Zero;
        var charaBaseVTable = (nint)CharacterBase.StaticVirtualTablePointer;
        if(charaBaseVTable == nint.Zero)
            NativeBinding.Fail("角色染色 UpdateTint", "CharacterBase 靜態虛擬表指標未解析");
        else
            updateTintHookAddress = (nint)Marshal.ReadInt64(charaBaseVTable + 0xC0);
        _updateTintHook = NativeBinding.CreateHook<UpdateTintDelegate>(hooks, updateTintHookAddress, UpdateTintDetour, "角色染色 UpdateTint");

        // 上游自帶的 SetFacewear 特徵碼在台服有 2 個命中(離線稽核),其中一支的簽章根本不同
        // (吃 xmm3 浮點參數),取錯就是把結構指標餵給不相干的函式。
        // FFXIVClientStructs 的 DrawDataContainer.SetGlasses 有自己的特徵碼,離線驗證為唯一命中,
        // 且解出來就是上游這條特徵碼第一個命中所指的同一支函式 —— 改用它,歧義直接消失。
    }

    //

    public async Task<RedrawResult> Redraw(ICharacter character, bool revert)
    {
        // 🔴 只抄走位址,不解參:呼叫端不一定是在「取得 character 的那一幀」進來的
        //    (ActorAppearanceCapability.Redraw 自己就在 await 鏈上),而 IGameObject.Address
        //    是建構當下凍結的、永不重新解析。FromAddress 只讀包裝物件自己的 Address 欄位,
        //    每次查詢再由物件表比一次指標(GetObjectAddress 只讀物件表自己的指標陣列),
        //    所以這個存活檢查本身永遠安全。做法與 ActorRedrawService.Redraw 相同。
        var actorRef = LiveActorRef.FromAddress(_objectTable, character.Address);

        if(revert)
            await _characterHandlerService.Revert(character);

        // 🔴 上面那個 await 的續行在<執行緒池>上,不在遊戲主執行緒上:
        //    Dalamud 這個 pin 沒有安裝任何 SynchronizationContext(整個 Dalamud repo 對
        //    SynchronizationContext 零命中),而 UI 繪製、遊戲事件回呼、原生 detour 進來時
        //    TaskScheduler.Current 就是 TaskScheduler.Default ⇒ await 之後不會自己回到框架執行緒。
        //    GetActorAppearance 會解 ICharacter 的原生指標(ActorAppearance.FromCharacter 讀
        //    DrawData、CharacterBase、武器 CharacterBase),在執行緒池上做就是 AccessViolationException,
        //    而 AVE 在 .NET Core 是 corrupted-state exception,try/catch 與 HookSafety 都攔不到。
        //    閘門在已經是框架執行緒時就地執行,行為逐字不變、不多花任何一幀。
        //    卸載期回 null ⇒ 轉成 RedrawResult.Failed,那是本路徑原本就會回的「做不到」值。
        var appearance = await _gate.RunAsync<ActorAppearance?>(
            "ActorAppearanceService.Redraw", () =>
            {
                // 🔴 存活檢查:上面的 Revert 會走 RedrawAndWait(最長 3 秒、跨過無數幀),
                //    角色可能已經離開物件表(換區、退出 GPose、角色被刪)。回到框架執行緒
                //    防不了這件事 —— 那個位址指向的是已釋放的記憶體,而 GetActorAppearance
                //    會解 DrawData、CharacterBase 與武器 CharacterBase,踩下去就是
                //    AccessViolationException(在 .NET Core 是 corrupted-state exception,
                //    try/catch 攔不到)。重查放在框架執行緒上做,與解參之間沒有跨幀的空窗。
                if(actorRef.IsAlive == false)
                {
                    Brio.Log.Information("角色在重繪期間消失,略過外觀套用(讀取現有外觀階段)。");
                    return null;
                }

                return GetActorAppearance(character);
            }, null);
        if(appearance is null)
            return RedrawResult.Failed;

        return await SetCharacterAppearance(character, appearance.Value, AppearanceImportOptions.All, true);
    }

    public async Task<RedrawResult> SetCharacterAppearance(ICharacter character, ActorAppearance appearance, AppearanceImportOptions options, bool forceRedraw = false)
    {
        // 🔴 只抄走位址,不解參(理由同 Redraw:呼叫端自己就在 await 鏈上)。
        //    下面每一個 await 回來、每一次要解參之前,都由物件表重查一次它還在不在。
        var actorRef = LiveActorRef.FromAddress(_objectTable, character.Address);

        // 🔴 await 的續行在<執行緒池>上,不在遊戲主執行緒上:
        //    Dalamud 這個 pin 沒有安裝任何 SynchronizationContext(整個 Dalamud repo 對
        //    SynchronizationContext 零命中),而 UI 繪製、遊戲事件回呼、原生 detour 進來時
        //    TaskScheduler.Current 就是 TaskScheduler.Default ⇒ await 之後不會自己回到框架執行緒。
        //    這支的兩段原生存取原本都直接跑在呼叫端的執行緒上:第一段的呼叫端至少有一個是在
        //    await 之後才叫進來的(本檔 Redraw 的 revert 路徑),第二段更是自己在兩個 await 之後。
        //    兩段都要解 character 的原生指標、寫 DrawData、呼叫遊戲函式(UpdateDrawData／LoadWeapon／
        //    SetGlasses／HideHeadgear／SetVisor／HideVieraEars),在非框架執行緒上做就是 AVE。
        //    🔑 所以整段交回框架執行緒,不是只有第一行檢查 —— 下游的 extension 也一起被覆蓋。
        var stage1 = await _gate.RunAsync<AppearanceStage1?>(
            "ActorAppearanceService.SetCharacterAppearance",
            () =>
            {
                // 🔴 存活檢查:呼叫端自己就可能在 await 之後(本檔 Redraw 的 revert 路徑、
                //    ActorAppearanceCapability.SetAppearance / ResetAppearance)。
                if(actorRef.IsAlive == false)
                {
                    Brio.Log.Information("角色在重繪期間消失,略過外觀套用(第一階段)。");
                    return null;
                }

                return ApplyAppearanceStage1(character, appearance, options, forceRedraw);
            }, null);
        if(stage1 is null)
            return RedrawResult.Failed;

        var state = stage1.Value;

        RedrawResult redrawResult = RedrawResult.Optmized;

        if(state.NeedsRedraw)
            redrawResult = await _redrawService.Redraw(character);

        if(state.GlamourerReset)
            await _glamourerService.RevertCharacter(character);

        // 🔴 這裡是原本那個「兩個 await 之後的 unsafe 區塊」,同樣交回框架執行緒。
        //    卸載期什麼都不做:那一瞬間少套一次 ExtendedAppearance 可以接受,崩潰不行。
        var stage2Applied = false;
        await _gate.RunAsync(
            "ActorAppearanceService.SetCharacterAppearance.extended",
            () =>
            {
                // 🔴 存活檢查:上面的 _redrawService.Redraw 會等完整重繪(DrawWhenReady 與
                //    WaitForDrawing 各最多 100 幀),Glamourer 還原又是一次 IPC 往返 ——
                //    角色在這段期間消失之後,character 的位址指向的是已釋放的記憶體,
                //    回到框架執行緒防不了,ApplyAppearanceStage2 整段都是解參與寫入。
                if(actorRef.IsAlive == false)
                {
                    Brio.Log.Information("角色在重繪期間消失,略過外觀套用(延伸外觀階段)。");
                    return;
                }

                ApplyAppearanceStage2(character, state.Appearance, options, state.ForceHeadToggles);
                stage2Applied = true;
            });

        // 延伸外觀那一段沒有真的套上去(角色消失,或 Dalamud 卸載期直接旁路)就回既有的
        // 失敗值,不要回報成重繪成功 —— 與前兩段在同樣情況下回 RedrawResult.Failed 一致。
        return stage2Applied ? redrawResult : RedrawResult.Failed;
    }

    /// <summary>
    /// <see cref="SetCharacterAppearance"/> 前半段在框架執行緒上算出來的狀態。
    /// <para>
    /// <c>Appearance</c> 是被 <see cref="AppearanceSanitizer"/> 與臉飾／髮飾邏輯改過的那一份 ——
    /// 後半段必須用同一份,不可以用呼叫端原本傳進來的。
    /// </para>
    /// </summary>
    private readonly struct AppearanceStage1
    {
        public AppearanceStage1(ActorAppearance appearance, bool needsRedraw, bool forceHeadToggles, bool glamourerReset)
        {
            Appearance = appearance;
            NeedsRedraw = needsRedraw;
            ForceHeadToggles = forceHeadToggles;
            GlamourerReset = glamourerReset;
        }

        public ActorAppearance Appearance { get; }
        public bool NeedsRedraw { get; }
        public bool ForceHeadToggles { get; }
        public bool GlamourerReset { get; }
    }

    /// <summary>
    /// 原本是 <see cref="SetCharacterAppearance"/> 裡第一個 <c>unsafe</c> 區塊與它前後的旗標計算。
    /// <b>內容逐字未動</b>(連縮排都保留原本的 <c>unsafe</c> 區塊),抽成方法只是為了整段交回框架執行緒。
    /// </summary>
    private AppearanceStage1 ApplyAppearanceStage1(ICharacter character, ActorAppearance appearance, AppearanceImportOptions options, bool forceRedraw)
    {
        var existingAppearance = GetActorAppearance(character);

        AppearanceSanitizer.SanitizeAppearance(ref appearance, existingAppearance);

        bool needsRedraw = forceRedraw;
        bool forceHeadToggles = false;
        bool glamourerReset = false;
        bool glamourerUnlocked = false;

        unsafe
        {
            var native = character.Native();

            // Model
            if(native->ModelContainer.ModelCharaId != appearance.ModelCharaId)
            {
                native->ModelContainer.ModelCharaId = appearance.ModelCharaId;
                needsRedraw |= true;
                glamourerReset |= true;
            }

            if(options.HasFlag(AppearanceImportOptions.Equipment) || options.HasFlag(AppearanceImportOptions.Customize))
            {
                // Hat toggle hack
                if(!existingAppearance.Equipment.Head.Equals(appearance.Equipment.Head))
                {
                    appearance.Runtime.IsHatHidden = false;
                    forceHeadToggles = true;
                }

                // Customize & Equipment
                if(!existingAppearance.Customize.Equals(appearance.Customize) || !existingAppearance.Equipment.Equals(appearance.Equipment))
                {
                    forceHeadToggles = true;

                    if(!existingAppearance.Customize.Equals(appearance.Customize))
                        glamourerReset |= true;

                    if(_glamourerService.CheckForLock(character))
                    {
                        if(!existingAppearance.Customize.Equals(appearance.Customize) || !existingAppearance.Equipment.Equals(appearance.Equipment))
                            glamourerUnlocked |= true;
                    }

                    if
                    (
                        existingAppearance.Customize.Race != appearance.Customize.Race ||
                        existingAppearance.Customize.Tribe != appearance.Customize.Tribe ||
                        existingAppearance.Customize.Gender != appearance.Customize.Gender ||
                        existingAppearance.Customize.BodyType != appearance.Customize.BodyType ||
                        existingAppearance.Customize.FaceType != appearance.Customize.FaceType
                    )
                        needsRedraw |= true;

                    if(!needsRedraw)
                    {
                        // Model redraw optimized if we can
                        var human = character.GetHuman();
                        if(human != null)
                        {

                            byte[] data = new byte[108];
                            fixed(byte* ptr = data)
                            {
                                if(options.HasFlag(AppearanceImportOptions.Customize))
                                {
                                    Buffer.MemoryCopy(appearance.Customize.Data, ptr, 32, 32);
                                }
                                else
                                {
                                    Buffer.MemoryCopy(existingAppearance.Customize.Data, ptr, 28, 28);
                                }

                                if(options.HasFlag(AppearanceImportOptions.Equipment))
                                {
                                    Buffer.MemoryCopy(appearance.Equipment.Data, ptr + 32, 80, 80);
                                }
                                else
                                {
                                    Buffer.MemoryCopy(existingAppearance.Equipment.Data, ptr + 32, 80, 80);
                                }

                                var didUpdate = human->Human.UpdateDrawData(ptr, false);
                                needsRedraw |= !didUpdate;
                            }
                        }
                        else
                        {
                            needsRedraw |= true;
                        }
                    }

                    if(options.HasFlag(AppearanceImportOptions.Customize))
                    {
                        // We can just set the data again incase we didn't earlier
                        *(ActorCustomize*)&native->DrawData.CustomizeData = appearance.Customize;
                    }

                    if(options.HasFlag(AppearanceImportOptions.Equipment))
                    {
                        fixed(EquipmentModelId* ptr = native->DrawData.EquipmentModelIds)
                        {
                            *(ActorEquipment*)ptr = appearance.Equipment;
                        }
                    }
                }

                // Facewear
                if(existingAppearance.Facewear != appearance.Facewear)
                {
                    if(needsRedraw)
                    {
                        appearance.Facewear = native->DrawData.GlassesIds[0];
                    }
                    else
                    {
                        native->DrawData.SetGlasses(0, appearance.Facewear);
                    }
                }
            }

            if(options.HasFlag(AppearanceImportOptions.Weapon))
            {
                // Weapons
                if(!needsRedraw)
                {

                    if(!existingAppearance.Weapons.MainHand.Equals(appearance.Weapons.MainHand))
                        native->DrawData.LoadWeapon(DrawDataContainer.WeaponSlot.MainHand, appearance.Weapons.MainHand, 0, 0, 0, 0);

                    if(!existingAppearance.Weapons.OffHand.Equals(appearance.Weapons.OffHand))
                        native->DrawData.LoadWeapon(DrawDataContainer.WeaponSlot.OffHand, appearance.Weapons.OffHand, 0, 0, 0, 0);
                }

                native->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand).ModelId = appearance.Weapons.MainHand;
                native->DrawData.Weapon(DrawDataContainer.WeaponSlot.OffHand).ModelId = appearance.Weapons.OffHand;

                // Weapon Visibility
                if(existingAppearance.Runtime.IsMainHandHidden != appearance.Runtime.IsMainHandHidden)
                    character.GetWeaponDrawObjectData(ActorEquipSlot.MainHand)->IsHidden = appearance.Runtime.IsMainHandHidden;

                if(existingAppearance.Runtime.IsOffHandHidden != appearance.Runtime.IsOffHandHidden)
                    character.GetWeaponDrawObjectData(ActorEquipSlot.OffHand)->IsHidden = appearance.Runtime.IsOffHandHidden;

                if(existingAppearance.Runtime.IsPropHandHidden != appearance.Runtime.IsPropHandHidden)
                    character.GetWeaponDrawObjectData(ActorEquipSlot.Prop)->IsHidden = appearance.Runtime.IsPropHandHidden;
            }
        }


        if(glamourerUnlocked)
        {
            _glamourerService.UnlockAndRevertCharacter(character);

            needsRedraw = true;
        }

        return new AppearanceStage1(appearance, needsRedraw, forceHeadToggles, glamourerReset);
    }

    /// <summary>
    /// 原本是 <see cref="SetCharacterAppearance"/> 裡第二個 <c>unsafe</c> 區塊(兩個 await 之後那一段)。
    /// <b>內容逐字未動</b>,唯一的差別是 <c>existingAppearance</c> 從外層變數改成本地變數
    /// (原本那個賦值之後就沒有任何人再讀它)。
    /// </summary>
    private void ApplyAppearanceStage2(ICharacter character, ActorAppearance appearance, AppearanceImportOptions options, bool forceHeadToggles)
    {
        unsafe
        {

            var native = character.Native();

            var existingAppearance = GetActorAppearance(character);

            if(options.HasFlag(AppearanceImportOptions.ExtendedAppearance))
            {
                // Hat
                if(existingAppearance.Runtime.IsHatHidden != appearance.Runtime.IsHatHidden || forceHeadToggles)
                {
                    native->DrawData.IsHatHidden = !appearance.Runtime.IsHatHidden;
                    native->DrawData.HideHeadgear(0, appearance.Runtime.IsHatHidden);
                }

                // Visor
                if(existingAppearance.Runtime.IsVisorToggled != appearance.Runtime.IsVisorToggled || forceHeadToggles)
                {
                    native->DrawData.SetVisor(appearance.Runtime.IsVisorToggled);
                    native->DrawData.IsVisorToggled = appearance.Runtime.IsVisorToggled;
                }

                // Viera Ears
                if(existingAppearance.Runtime.IsVieraEarsHidden != appearance.Runtime.IsVieraEarsHidden || forceHeadToggles)
                {
                    native->DrawData.VieraEarsHidden = appearance.Runtime.IsVieraEarsHidden;
                    native->DrawData.HideVieraEars(appearance.Runtime.IsVieraEarsHidden);
                }

                // Wetness
                if(existingAppearance.ExtendedAppearance.Wetness != appearance.ExtendedAppearance.Wetness)
                {
                    var charaBase = character.GetCharacterBase();
                    charaBase->Wetness = appearance.ExtendedAppearance.Wetness;
                }
                if(existingAppearance.ExtendedAppearance.WetnessDepth != appearance.ExtendedAppearance.WetnessDepth)
                {
                    var charaBase = character.GetCharacterBase();
                    charaBase->WetnessDepth = appearance.ExtendedAppearance.WetnessDepth;
                }

                // Tints
                if(existingAppearance.ExtendedAppearance.CharacterTint != appearance.ExtendedAppearance.CharacterTint)
                {
                    var charaBase = character.GetCharacterBase();
                    charaBase->Tint = appearance.ExtendedAppearance.CharacterTint;
                }

                if(existingAppearance.ExtendedAppearance.MainHandTint != appearance.ExtendedAppearance.MainHandTint)
                {
                    var weaponCharaBase = character.GetWeaponCharacterBase(ActorEquipSlot.MainHand);
                    if(weaponCharaBase != null)
                        weaponCharaBase->Tint = appearance.ExtendedAppearance.MainHandTint;
                }

                if(existingAppearance.ExtendedAppearance.OffHandTint != appearance.ExtendedAppearance.OffHandTint)
                {
                    var weaponCharaBase = character.GetWeaponCharacterBase(ActorEquipSlot.OffHand);
                    if(weaponCharaBase != null)
                        weaponCharaBase->Tint = appearance.ExtendedAppearance.OffHandTint;
                }

                // Transparency
                if(existingAppearance.ExtendedAppearance.Transparency != appearance.ExtendedAppearance.Transparency)
                    native->Alpha = appearance.ExtendedAppearance.Transparency;

            }
        }
    }

    public ActorAppearance GetActorAppearance(ICharacter character) => ActorAppearance.FromCharacter(character);

    private byte EnforceKindRestrictionsDetour(nint a1, nint a2)
    {
        if(_configurationService.Configuration.Appearance.ApplyNPCHack == ApplyNPCHack.Always)
            return 0;

        if(_configurationService.Configuration.Appearance.ApplyNPCHack == ApplyNPCHack.InGPose && _gPoseService.IsGPosing)
            return 0;

        return _enforceKindRestrictionsHook!.Original(a1, a2);
    }

    private nint UpdateWetnessDetour(nint a1)
    {
        if(_gPoseService.IsGPosing)
            return 0;

        return _updateWetnessHook!.Original(a1);
    }

    private nint UpdateTintDetour(nint charaBase, nint tint)
    {
        if(_gPoseService.IsGPosing && CanTint)
            return 0;

        return _updateTintHook!.Original(charaBase, tint);
    }

    public void Dispose()
    {
        _enforceKindRestrictionsHook?.Dispose();
        _updateWetnessHook?.Dispose();
        _updateTintHook?.Dispose();

        GC.SuppressFinalize(this);
    }
}
