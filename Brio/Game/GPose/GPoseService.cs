using Brio.Config;
using Brio.Core;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Brio.Game.GPose;

public unsafe class GPoseService : IDisposable
{
    public bool IsGPosing => _isInFakeGPose || _isInGPose;

    public delegate void OnGPoseStateDelegate(bool newState);
    public event OnGPoseStateDelegate? OnGPoseStateChange;

    public bool FakeGPose
    {
        get => _isInFakeGPose;
        set
        {
            if(_isInFakeGPose == value)
                return;

            _isInFakeGPose = value;

            TriggerGPoseChange();
        }
    }

    private bool _isInGPose = false;
    private bool _isInFakeGPose = false;

    private delegate bool GPoseEnterExitDelegate(UIModule* uiModule);
    private delegate void ExitGPoseDelegate(UIModule* uiModule);
    private readonly Hook<GPoseEnterExitDelegate>? _enterGPoseHook;
    private readonly Hook<ExitGPoseDelegate>? _exitGPoseHook;

    private delegate nint MouseHoverDelegate(nint a1, nint a2, nint a3);
    private readonly Hook<MouseHoverDelegate>? _mouseHoverHook;

    internal delegate void TargetNameDelegate(nint args);
    internal static Hook<TargetNameDelegate>? _targetNameDelegateHook;

    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly ConfigurationService _configService;

    public const string BrioHiddenName = "[HIDDEN]";

    public GPoseService(IFramework framework, IClientState clientState, ConfigurationService configService, IGameInteropProvider interopProvider, ISigScanner scanner)
    {
        _framework = framework;
        _clientState = clientState;
        _configService = configService;

        _isInGPose = _clientState.IsGPosing;

        // 🔴 Framework/UIModule/虛擬表任一為 null 時解參考就是 AccessViolation(try/catch 攔不到)。
        nint enterGPoseAddress = nint.Zero;
        nint exitGPoseAddress = nint.Zero;
        var nativeFramework = Framework.Instance();
        UIModule* uiModule = nativeFramework == null ? null : nativeFramework->UIModule;
        if(uiModule == null || uiModule->VirtualTable == null)
        {
            NativeBinding.Fail("GPose 進出偵測 EnterGPose/ExitGPose", "UIModule 或其虛擬表在載入時尚未建立");
        }
        else
        {
            enterGPoseAddress = (nint)uiModule->VirtualTable->EnterGPose;
            exitGPoseAddress = (nint)uiModule->VirtualTable->ExitGPose;
        }

        _enterGPoseHook = NativeBinding.CreateHook<GPoseEnterExitDelegate>(interopProvider, enterGPoseAddress, EnteringGPoseDetour, "GPose 進入 EnterGPose");
        _exitGPoseHook = NativeBinding.CreateHook<ExitGPoseDelegate>(interopProvider, exitGPoseAddress, ExitingGPoseDetour, "GPose 離開 ExitGPose");

        var mouseHoverAddr = "40 57 48 83 EC ?? 48 89 5C 24 ?? 48 8B F9 48 89 6C 24 ?? 48 89 74 24 ?? 49 8B F0";
        _mouseHoverHook = NativeBinding.ScanHook<MouseHoverDelegate>(scanner, interopProvider, mouseHoverAddr, GPoseMouseEventDetour, "GPose 滑鼠選取 MouseHover", enable: false);

        var targetNameAddr = "E8 ?? ?? ?? ?? 48 8D 8D ?? ?? ?? ?? 48 83 C4 28"; // sig from, Ktisis GuiHooks.cs line 43 (https://github.com/ktisis-tools/Ktisis/blob/main/Ktisis/Interop/Hooks/GuiHooks.cs)
        _targetNameDelegateHook = NativeBinding.ScanHook<TargetNameDelegate>(scanner, interopProvider, targetNameAddr, TargetNameDetour, "GPose 目標名稱 TargetName", enable: false);

        _framework.Update += OnFrameworkUpdate;

        UpdateDynamicHooks();
    }

    private void TargetNameDetour(nint args)
    {
        if(_configService.Configuration.Posing.HideNameOnGPoseSettingsWindow)
        {
            for(var i = 0; i < BrioHiddenName.Length; i++)
            {
                *(char*)(args + 488 + i) = BrioHiddenName[i];
            }
        }

        _targetNameDelegateHook!.Original(args);
    }

    public void TriggerGPoseChange()
    {
        var gposing = IsGPosing;
        Brio.Log.Debug($"GPose state changed to {gposing}");
        OnGPoseStateChange?.Invoke(gposing);
    }

    public void AddCharacterToGPose(ICharacter chara) => AddCharacterToGPose((NativeCharacter*)chara.Address);

    public void AddCharacterToGPose(NativeCharacter* chara)
    {
        if(!IsGPosing)
            return;

        var ef = EventFramework.Instance();
        if(ef == null)
            return;

        ef->EventSceneModule.EventGPoseController.AddCharacterToGPose(chara);

    }

    private void ExitingGPoseDetour(UIModule* uiModule)
    {
        _exitGPoseHook!.Original.Invoke(uiModule);
        HandleGPoseStateChange(false);
    }

    private bool EnteringGPoseDetour(UIModule* uiModule)
    {
        bool didEnter = _enterGPoseHook!.Original.Invoke(uiModule);

        HandleGPoseStateChange(didEnter);

        return didEnter;
    }

    private nint GPoseMouseEventDetour(nint a1, nint a2, nint a3)
    {
        if(_configService.Configuration.Posing.DisableGPoseMouseSelect)
            return 0;

        return _mouseHoverHook!.Original(a1, a2, a3);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Only detect if we got snapped out
        if(!_clientState.IsGPosing && _isInGPose)
            HandleGPoseStateChange(_clientState.IsGPosing);
    }

    private void HandleGPoseStateChange(bool newState)
    {
        if(IsGPosing == newState || _isInFakeGPose)
            return;

        _isInGPose = newState;

        UpdateDynamicHooks();

        TriggerGPoseChange();
    }

    private void UpdateDynamicHooks()
    {
        if(IsGPosing)
        {
            if(_mouseHoverHook is not null && !_mouseHoverHook.IsEnabled)
                _mouseHoverHook.Enable();

            _targetNameDelegateHook?.Enable();

        }
        else
        {
            if(_mouseHoverHook is not null && _mouseHoverHook.IsEnabled)
                _mouseHoverHook.Disable();

            _targetNameDelegateHook?.Disable();
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;

        _targetNameDelegateHook?.Dispose();
        _enterGPoseHook?.Dispose();
        _exitGPoseHook?.Dispose();
        _mouseHoverHook?.Dispose();

        GC.SuppressFinalize(this);
    }
}

