using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;

namespace Brio.Core;

/// <summary>
/// 台服(TC)移植用的原生繫結閘門。
///
/// 上游 Brio 直接呼叫 <c>ISigScanner.ScanText</c>,特徵碼對不上時它會擲例外;
/// Brio 的服務全部是 DI 單例,任何一個建構子擲例外 = 整個外掛載入失敗。
/// 台服的執行檔與國際服不同版,特徵碼失效是常態而不是意外,
/// 所以這裡把「找不到特徵碼」從「外掛掛掉」降級成「該功能停用」。
///
/// 診斷一律寫 Information 級(使用者跑 LogLevel 2,Debug/Verbose 收不到)。
/// </summary>
public static class NativeBinding
{
    private static readonly List<string> _failures = [];

    /// <summary>本次載入中比對失敗的原生繫結名稱(給 UI/診斷用)。</summary>
    public static IReadOnlyList<string> Failures => _failures;

    public static bool HasFailures => _failures.Count > 0;

    /// <summary>
    /// 掃描特徵碼。找不到或擲例外時回 <see cref="nint.Zero"/> 並記錄,不會往外擲。
    /// </summary>
    public static nint Scan(ISigScanner scanner, string signature, string purpose)
    {
        try
        {
            if(scanner.TryScanText(signature, out var address) && address != nint.Zero)
                return address;
        }
        catch(Exception ex)
        {
            Fail(purpose, $"掃描時發生例外:{ex.Message}");
            return nint.Zero;
        }

        Fail(purpose, "特徵碼在本客戶端找不到");
        return nint.Zero;
    }

    /// <summary>
    /// 由位址建立並啟用 hook。位址為 0 或建立失敗時回 null,呼叫端必須容許 null。
    /// </summary>
    public static Hook<T>? CreateHook<T>(IGameInteropProvider provider, nint address, T detour, string purpose, bool enable = true)
        where T : Delegate
    {
        if(address == nint.Zero)
            return null;

        try
        {
            var hook = provider.HookFromAddress(address, detour);
            if(enable)
                hook.Enable();
            return hook;
        }
        catch(Exception ex)
        {
            Fail(purpose, $"建立 hook 失敗:{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 給「離線稽核時在本客戶端有多個命中、而且無法分辨哪一個才對」的特徵碼用。
    /// 行為與 <see cref="Scan"/> 相同(Dalamud 取第一個命中),但會在載入時明白記一筆,
    /// 這樣「這個功能沒作用」就能立刻對應到「當初就知道它有歧義」,而不是重新查一遍。
    /// </summary>
    /// <param name="offlineHitCount">離線對台服執行檔掃描到的命中數。</param>
    public static nint ScanAmbiguous(ISigScanner scanner, string signature, string purpose, int offlineHitCount)
    {
        var address = Scan(scanner, signature, purpose);
        if(address != nint.Zero)
        {
            Brio.Log.Information(
                $"[TC] 特徵碼有歧義:{purpose} —— 離線稽核在台服執行檔上有 {offlineHitCount} 個命中," +
                "取第一個。此功能若行為異常,請連同本行一起回報。");
        }

        return address;
    }

    /// <summary>掃描 + 建立 hook 的一次性組合。</summary>
    public static Hook<T>? ScanHook<T>(ISigScanner scanner, IGameInteropProvider provider, string signature, T detour, string purpose, bool enable = true)
        where T : Delegate
        => CreateHook(provider, Scan(scanner, signature, purpose), detour, purpose, enable);

    /// <summary>
    /// 把函式位址包成 delegate。位址為 0 時回 null,呼叫端必須容許 null。
    /// </summary>
    public static T? GetDelegate<T>(nint address, string purpose) where T : Delegate
    {
        if(address == nint.Zero)
            return null;

        try
        {
            return System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<T>(address);
        }
        catch(Exception ex)
        {
            Fail(purpose, $"建立 delegate 失敗:{ex.Message}");
            return null;
        }
    }

    /// <summary>記錄一個非特徵碼來源的繫結失敗(例如 vtable 槽解不出來、遊戲資料表缺失)。</summary>
    public static void Fail(string purpose, string reason, string kind = "原生繫結")
    {
        var line = $"[TC] {kind}失效:{purpose} —— {reason}。相關功能已停用,外掛其餘部分仍可使用。";
        lock(_failures)
        {
            if(!_failures.Contains(purpose))
                _failures.Add(purpose);
        }
        Brio.Log.Information(line);
    }
}
