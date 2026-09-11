using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Brio.IPC;

/// <summary>
/// IPC 端點的「遊戲主執行緒閘門」。做法照艦隊裡其他外掛的
/// <c>IpcFrameworkGate</c>（Lifestream／Artisan 等），只是 Brio 沒有 ECommons，
/// 所以改成由 DI 注入的 <see cref="IFramework"/> 建構的一般物件，而不是靜態類別。
/// </summary>
/// <remarks>
/// 🔴🔴 為什麼需要這一層：Dalamud 的 CallGate 是<b>直接方法呼叫</b>，
/// 提供端的碼跑在<b>呼叫端的執行緒</b>上。別的外掛（Ktisis、Mare 之類的消費端都在艦隊外）
/// 從自己的 <c>Task.Run</c>、背景工作或任何非 framework 執行緒打過來時，
/// Brio 這一側就會在那條執行緒上：
/// <list type="bullet">
/// <item>生成／銷毀遊戲角色（<c>ActorSpawnService.CreateCharacter</c>／<c>DestroyObject</c>）、
/// 解 <c>IGameObject.Native()</c> 的原生指標、改 model transform 與骨架 —— 讀到一半被主執行緒
/// 換掉就是 AccessViolationException，而 AVE 在 .NET Core 是 corrupted-state exception，
/// <c>try</c>／<c>catch</c> 攔不到，整個遊戲直接崩掉；</item>
/// <item>對遊戲程式碼寫入 NOP（<c>PhysicsService.FreezeEnable</c>／<c>FreezeRevert</c>）；</item>
/// <item>走訪 framework 獨佔的裸集合 —— <c>EntityManager</c> 的 <c>_entityMap</c> 是裸
/// <c>Dictionary</c>（<c>Brio/Entities/EntityManager.cs:47</c>），而 <c>Actor.GetAll</c> 會
/// <c>foreach</c> 它。並行改動時失敗形式<b>不是「拿到舊值」而是字典本身壞掉</b>，
/// <c>foreach</c> 走訪中被改動則擲 <c>InvalidOperationException</c>。</item>
/// </list>
/// 🔑 所以凡是「會碰到上面任何一項」的端點，一律把<b>整個方法體</b>交回主執行緒執行，
/// 不是只有第一行檢查 —— 這樣連下游的 capability、service 也一起被覆蓋，不必逐一追。
/// <br/><br/>
/// 📌 <b>已經在主執行緒上呼叫時行為逐字不變</b>：直接就地執行，不配置 Task、
/// 不改變例外型別、不多花任何一幀。絕大多數消費端（在自己的 <c>Framework.Update</c>
/// 或 GPose 流程裡呼叫）走的就是這條路。
/// <br/><br/>
/// ⚠️ 逾時的處置：等主執行緒最多 <see cref="TimeoutMs"/> 毫秒。
/// 🔴 <b>等待一定要有上限</b>：呼叫端有可能正是主執行緒（間接經由別的外掛轉手），
/// 無上限地等下去就是死鎖。逾時就回該端點<b>原本就有的</b>「不可用」值
/// （false／null／0／<c>(null, null, null)</c>），語意與「現在做不到」相同 ——
/// 呼叫端本來就要處理這個狀態，回傳型別一個都沒有改。
/// 同時用 <see cref="Interlocked"/> 把還沒開始跑的工作標成放棄，避免
/// 「呼叫端已經拿到 null 走人了，五秒後角色才真的被生出來」這種形狀。
/// <br/><br/>
/// 🔴 用 <c>RunOnFrameworkThread</c> 不是 <c>Framework.Run</c>：前者在已經是主執行緒時
/// 就地執行，同步等它不會死鎖；後者一律排隊，同步等會死鎖。
/// </remarks>
public sealed class IpcFrameworkGate(IFramework framework)
{
    /// <summary>等主執行緒的上限。超過就當作「現在做不到」。</summary>
    public const int TimeoutMs = 5000;

    private const int StatePending = 0;
    private const int StateRunning = 1;
    private const int StateAbandoned = 2;

    private readonly IFramework _framework = framework;

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
    /// 🔴 Dalamud 卸載期的閘門旁路：<c>Framework.RunOnFrameworkThread</c> 在
    /// <c>IsFrameworkUnloading</c> 為真時會<b>就地在呼叫端執行緒</b>執行 body
    /// （<c>Dalamud/Game/Framework.cs:167-197</c> 的
    /// <c>IsInFrameworkUpdateThread || IsFrameworkUnloading</c>），
    /// 而<b>無延遲的 <c>RunOnTick</c> 在卸載期也會轉呼叫 <c>RunOnFrameworkThread</c></b>
    /// （<c>Framework.cs:200-211</c>）—— 等於這一層完全失效、原生記憶體存取退回未保護狀態。
    /// 🔑 所以卸載期一律直接回該端點原本的「不可用」值：那一瞬間功能失效可以接受
    /// （遊戲要關了），卸載期的 AccessViolationException 不行 —— 使用者看到的是崩潰。
    /// 📌 已經在 framework 執行緒上時不受影響（那本來就是安全的執行緒），
    /// 所以 Brio 自己在 <c>Dispose</c> 裡的同步呼叫行為逐字不變。
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
    /// 🔴 這條路徑跑在呼叫端的執行緒上，所以節流表一定要自己帶鎖 ——
    /// 裸 <c>Dictionary</c> 被並行插入時弄壞的是整張表，不是「拿到舊值」。
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
