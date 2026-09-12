using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Brio.IPC;

/// <summary>
/// IPC 端點的「遊戲主執行緒閘門」。提供端的碼跑在<b>呼叫端的執行緒</b>上。一律把<b>整個方法體</b>交回主執行緒執行。
/// 📌 <b>已經在主執行緒上呼叫時行為逐字不變</b>：直接就地執行。
/// ⚠️ 逾時的處置：等主執行緒最多 <see cref="TimeoutMs"/> 毫秒。逾時就回該端點<b>原本就有的</b>「不可用」值。
/// </summary>
public sealed class IpcFrameworkGate(IFramework framework)
{
    /// <summary>等主執行緒的上限。超過就當作「現在做不到」。</summary>
    public const int TimeoutMs = 5000;

    private const int StatePending = 0;
    private const int StateRunning = 1;
    private const int StateAbandoned = 2;

    private readonly IFramework _framework = framework;

    /// <summary>
    /// 非同步轉派點的共用入口。<b>沒有在卸載期時行為與直接呼叫 <c>IFramework.RunOnFrameworkThread</c> 逐字相同</b>。
    /// </summary>
    /// <param name="context">出現在診斷訊息裡的來源名稱，同時是節流表的鍵。</param>
    /// <param name="unavailable">卸載期要回的「做不到」值。🔴 一律沿用該呼叫點<b>原本失敗時就會回的那個值</b>。</param>
    public Task<T> RunAsync<T>(string context, Func<T> body, T unavailable)
        => ShouldBypassForUnloading(context) ? Task.FromResult(unavailable) : _framework.RunOnFrameworkThread(body);

    /// <summary>沒有回傳值的版本。卸載期什麼都不做（原本也只是「做了就做了」的副作用）。</summary>
    public Task RunAsync(string context, Action body)
        => ShouldBypassForUnloading(context) ? Task.CompletedTask : _framework.RunOnFrameworkThread(body);

    /// <summary>
    /// <paramref name="body"/> 本身就是非同步的版本。
    /// 🔴 名字刻意與 <see cref="RunAsync{T}(string, Func{T}, T)"/> 不同：同名會變成呼叫端無法解析的歧義。
    /// 📌 用無延遲的 <c>RunOnTick</c> 而不是 <c>RunOnFrameworkThread(Func&lt;Task&lt;T&gt;&gt;)</c>：後者在本 pin 標了 <c>[Obsolete]</c>。
    /// </summary>
    public Task<T> RunTaskAsync<T>(string context, Func<Task<T>> body, T unavailable)
        => ShouldBypassForUnloading(context) ? Task.FromResult(unavailable) : _framework.RunOnTick(body);

    /// <summary>
    /// 有回傳值的端點。<paramref name="unavailable"/> 是逾時／卸載期要回的「不可用」值，
    /// 一律沿用該端點原本失敗時就會回的那個值。
    /// </summary>
    public T Get<T>(string endpoint, Func<T> body, T unavailable)
    {
        if(_framework.IsInFrameworkUpdateThread) return body();

        if(ShouldBypassForUnloading(endpoint)) return unavailable;

        var state = StatePending;
        var task = _framework.RunOnFrameworkThread(() =>
        {
            // 呼叫端已經逾時走人了就什麼都不做。
            if(Interlocked.CompareExchange(ref state, StateRunning, StatePending) != StatePending) return unavailable;
            return body();
        });

        // WaitAny 對已經失敗的工作也回 0（不擲），交給 GetResult 原樣重擲原始例外，
        // 這樣呼叫端看到的例外型別與沒有這層閘門時完全一樣（不會變成 AggregateException）。
        if(Task.WaitAny([task], TimeoutMs) == 0) return task.GetAwaiter().GetResult();

        ReportTimeout(endpoint, Interlocked.CompareExchange(ref state, StateAbandoned, StatePending) == StatePending);
        return unavailable;
    }

    /// <summary>
    /// 🔴 Dalamud 卸載期的閘門旁路：<c>Framework.RunOnFrameworkThread</c> 在 <c>IsFrameworkUnloading</c> 為真時會<b>就地在呼叫端執行緒</b>執行 body。
    /// 而<b>無延遲的 <c>RunOnTick</c> 在卸載期也會轉呼叫 <c>RunOnFrameworkThread</c></b>。
    /// 🔑 所以卸載期一律直接回該端點原本的「不可用」值：那一瞬間功能失效可以接受（遊戲要關了），卸載期的 AccessViolationException 不行。
    /// </summary>
    public bool ShouldBypassForUnloading(string endpoint)
    {
        if(!_framework.IsFrameworkUnloading || _framework.IsInFrameworkUpdateThread) return false;
        if(ShouldLogTimeout(endpoint + "/卸載期"))
        {
            Brio.Log.Information($"[Brio IPC 閘門] {endpoint} 在 Dalamud 卸載期從別的執行緒被呼叫，已回傳「不可用」值。卸載期的 RunOnFrameworkThread／RunOnTick 會就地在呼叫端執行緒執行，閘門保護不了原生記憶體存取；此時功能失效可以接受，崩潰不行。");
        }
        return true;
    }

    /// <summary>同一個端點的逾時訊息重印間隔。</summary>
    private const long TimeoutLogIntervalMs = 10000;

    /// <summary>節流表上限，避免端點名意外發散時無限成長。</summary>
    private const int MaxTrackedTimeoutKeys = 128;

    private readonly Dictionary<string, long> _timeoutLogTimes = [];

    /// <summary>
    /// 自帶的節流：首次必放行，之後每 <see cref="TimeoutLogIntervalMs"/> 毫秒放行一次。
    /// 🔴 這條路徑跑在呼叫端的執行緒上，所以節流表一定要自己帶鎖 —— 裸 <c>Dictionary</c> 被並行插入時弄壞的是整張表。
    /// 🔴 鎖內只碰字典 —— 不寫 log、不做 I/O、不呼叫任何別的外掛。
    /// </summary>
    private bool ShouldLogTimeout(string key)
    {
        var now = Environment.TickCount64;
        lock(_timeoutLogTimes)
        {
            if(_timeoutLogTimes.TryGetValue(key, out var last) && now - last < TimeoutLogIntervalMs) return false;
            if(_timeoutLogTimes.Count >= MaxTrackedTimeoutKeys && !_timeoutLogTimes.ContainsKey(key)) _timeoutLogTimes.Clear();
            _timeoutLogTimes[key] = now;
            return true;
        }
    }

    /// <summary>要使用者回報的診斷寫 Information（使用者的 LogLevel 收得到，且不會被 Debug 的數十萬行淹沒）。</summary>
    private void ReportTimeout(string endpoint, bool abandoned)
    {
        if(!ShouldLogTimeout(endpoint)) return;
        var outcome = abandoned
            ? "工作還沒開始就被取消，什麼都沒做"
            : "工作已經開始執行，會照常跑完（呼叫端拿到的回值不代表它沒發生）";
        Brio.Log.Information($"[Brio IPC 閘門] {endpoint} 等待遊戲主執行緒超過 {TimeoutMs} 毫秒，已回傳「不可用」值。{outcome}。通常代表遊戲正在讀取畫面或嚴重掉幀；若持續出現請回報。");
    }
}
