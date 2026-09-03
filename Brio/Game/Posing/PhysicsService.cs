
//
// Code found in this file is from and is inspired by,
// Anamnesis (https://github.com/imchillin/Anamnesis)
// And SimpleTweaks (https://github.com/Caraxi/SimpleTweaksPlugin/tree/main) 
//

//
// Thank you Winter!
//

using Brio.Core;
using Brio.Game.GPose;
using Dalamud.Game;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using System;

namespace Brio.Game.Posing;

public unsafe partial class PhysicsService : IDisposable
{
    private readonly GPoseService _gPoseService;
    private readonly IFramework _framework;

    //private unsafe delegate void HandlePhysicsDelegate(IntPtr arg1, short arg2, IntPtr arg3, char arg4, char arg5);
    //private readonly Hook<HandlePhysicsDelegate> _handlePhysicsDelegate = null!;

    public bool IsFreezeEnabled { get; private set; } = false;

    private readonly nint _freezePhysicsAddress;
    private byte[] _originalPhysicsBytes1 = [];
    private byte[] _originalPhysicsBytes2 = [];

    // 載入當下(還沒做任何修補)讀到的原始位元組,之後永不改寫。
    // 寫入前拿它跟現場記憶體比對,不符就不寫 —— 遊戲更新過、或別的外掛也動過同一段程式碼時,
    // 盲目寫 NOP 會蓋到別的指令,而那是靜默的。
    private readonly byte[] _pristinePhysicsBytes1 = [];
    private readonly byte[] _pristinePhysicsBytes2 = [];

    private bool _tcWarningLogged;

    /// <summary>UI tooltip 與 log 共用的警語(繁中)。這是「改遊戲程式碼」等級的功能,使用者要先知道。</summary>
    public const string TcPatchWarning =
        "注意:凍結物理是對執行中的遊戲程式碼做記憶體修補(把 7 個位元組改寫成 NOP)。\n" +
        "台服位址與指令邊界已離線驗證過,寫入前也會比對原始位元組,不符就不寫。\n" +
        "但這仍然是在修改遊戲程式碼。若遊戲出現異常請先關閉此功能並回報。";

    public PhysicsService(ISigScanner scanner, IFramework framework, GPoseService gPoseService, IGameInteropProvider hooking)
    {
        _gPoseService = gPoseService;
        _framework = framework;

        _framework.Update += OnFrameworkUpdate;

        // This signature is from Anamnesis (https://github.com/imchillin/Anamnesis)
        // Found in AddressService.cs on line 159 - SkeletonFreezePhysics (1/2/3)
        var freezePhysicsAddress = "0F 11 48 10 41 0F 10 44 24 ?? 0F 11 40 20 48 8B 46 28";
        _freezePhysicsAddress = NativeBinding.Scan(scanner, freezePhysicsAddress, "凍結物理 SkeletonFreezePhysics");

        // 🔴 上游在特徵碼失敗時把位址設成 0 之後照樣 ReadRaw ⇒ 讀位址 0 是 AccessViolation,
        // 而 AVE 在 .NET Core 是 corrupted-state exception,try/catch 攔不到。這裡改成直接停用。
        if(_freezePhysicsAddress != nint.Zero)
        {
            _originalPhysicsBytes1 = MemoryHelper.ReadRaw(_freezePhysicsAddress, 4);
            _originalPhysicsBytes2 = MemoryHelper.ReadRaw(_freezePhysicsAddress - 0x9, 3);

            _pristinePhysicsBytes1 = (byte[])_originalPhysicsBytes1.Clone();
            _pristinePhysicsBytes2 = (byte[])_originalPhysicsBytes2.Clone();
        }

        //var handlePhysicsSig = "E9 ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? 41 ?? ?? ?? 4c ?? ?? 30 ?? ?? ?? 41"; // e9 2d e0 09 00 ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? 41 0f b6 c0 4c 8b d1 45 8b c8 48 69 c8 20 02 00 00
        //_handlePhysicsDelegate = hooking.HookFromAddress<HandlePhysicsDelegate>(scanner.ScanText(handlePhysicsSig), HandlePhysicsDetour);
        //_handlePhysicsDelegate.Enable();
    }

    //public unsafe void HandlePhysicsDetour(IntPtr arg1, short arg2, IntPtr arg3, char arg4, char arg5)
    //{
    //    if(IsFreezeEnabled)
    //    {
    //        //return;
    //    }

    //    _handlePhysicsDelegate.Original(arg1, arg2, arg3, arg4, arg5);
    //}

    /// <summary>凍結物理需要對遊戲碼寫入 NOP;特徵碼失效時整個功能停用。</summary>
    public bool IsAvailable => _freezePhysicsAddress != nint.Zero
        && _originalPhysicsBytes1.Length == 4 && _originalPhysicsBytes2.Length == 3;

    public bool FreezeToggle() => IsFreezeEnabled ? FreezeRevert() : FreezeEnable();

    public bool FreezeRevert()
    {
        if(IsAvailable == false)
            return IsFreezeEnabled = false;

        ReplaceRaw(_freezePhysicsAddress, _originalPhysicsBytes1);
        ReplaceRaw(_freezePhysicsAddress - 0x9, _originalPhysicsBytes2);

        return IsFreezeEnabled = false;
    }

    public bool FreezeEnable()
    {
        if(IsAvailable == false)
            return IsFreezeEnabled = false;

        // 已經凍結就不要再寫一次:再寫一次會把 _originalPhysicsBytes* 覆蓋成 NOP,
        // 之後的還原就永遠還原不回原本的指令了。IPC 的 Brio.FreezePhysics 沒有 toggle 保護,會走到這裡。
        if(IsFreezeEnabled)
            return true;

        // 安全閘:現場位元組必須跟載入當下的快照一致,不符就不寫。
        if(VerifyPristine(_freezePhysicsAddress, _pristinePhysicsBytes1) == false
            || VerifyPristine(_freezePhysicsAddress - 0x9, _pristinePhysicsBytes2) == false)
            return IsFreezeEnabled = false;

        LogTcWarningOnce();

        _originalPhysicsBytes1 = ReplaceRaw(_freezePhysicsAddress, [0x90, 0x90, 0x90, 0x90]);
        _originalPhysicsBytes2 = ReplaceRaw(_freezePhysicsAddress - 0x9, [0x90, 0x90, 0x90]);

        return IsFreezeEnabled = true;
    }

    /// <summary>寫入前的安全閘:現場位元組必須等於載入當下的快照,否則不寫並記一筆 Information。</summary>
    private static bool VerifyPristine(nint address, byte[] expected)
    {
        var current = MemoryHelper.ReadRaw(address, expected.Length);
        if(current.AsSpan().SequenceEqual(expected))
            return true;

        Brio.Log.Information(
            $"[TC] 凍結物理:原 bytes 不符,已跳過。位址 0x{address:X} 預期 " +
            $"{Convert.ToHexString(expected)},實際 {Convert.ToHexString(current)}。" +
            "遊戲可能已更新,或另一個外掛改過同一段程式碼;本次不寫入。");
        return false;
    }

    /// <summary>第一次真的寫入時印一次(Information 級,使用者的 LogLevel 1 收得到)。</summary>
    private void LogTcWarningOnce()
    {
        if(_tcWarningLogged)
            return;
        _tcWarningLogged = true;

        Brio.Log.Information(
            $"[TC] Brio 凍結物理已啟用 —— 這個功能是對遊戲程式碼做記憶體修補:台服位址 " +
            $"0x{_freezePhysicsAddress:X} 的 4 個位元組與 0x{_freezePhysicsAddress - 0x9:X} 的 3 個位元組" +
            "會被改寫成 NOP(指令邊界已離線驗證過)。若遊戲出現異常,請關閉此功能並回報。");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if(IsFreezeEnabled && _gPoseService.IsGPosing == false)
        {
            FreezeRevert();
        }
    }

    // From SimpleTweaks (https://github.com/Caraxi/SimpleTweaksPlugin/blob/124523ca0ddbeadec86fd7bea323b66870e1a474/Tweaks/HighResScreenshots.cs)
    private static byte[] ReplaceRaw(nint address, byte[] data)
    {
        var originalBytes = MemoryHelper.ReadRaw(address, data.Length);
        var oldProtection = MemoryHelper.ChangePermission(address, data.Length, MemoryProtection.ExecuteReadWrite);
        MemoryHelper.WriteRaw(address, data);
        MemoryHelper.ChangePermission(address, data.Length, oldProtection);
        return originalBytes;
    }

    public void Dispose()
    {
        if(IsFreezeEnabled)
        {
            FreezeRevert();
        }

        _framework.Update -= OnFrameworkUpdate;
        //_handlePhysicsDelegate.Dispose();
    }
}
