using System.Threading;
using System.Threading.Tasks;

namespace Brio.Game.Core;

public static class TaskExtensions
{
    /// <summary>
    /// 丟棄式(fire-and-forget)工作的例外觀察點。
    /// 📌 <b>不改變任何行為</b>:只在工作已經失敗之後多寫一行 Error,不等待、不重擲、不吞任何東西。
    /// ⚠️ 取消不算失敗(<c>OnlyOnFaulted</c> 不涵蓋 Canceled),卸載期被取消的工作不會產生噪音。
    /// 🔑 續行排在 <c>TaskScheduler.Default</c>:寫 log 絕不佔用遊戲主執行緒。
    /// </summary>
    /// <param name="what">寫進錯誤訊息的來源名稱。</param>
    public static void Observe(this Task task, string what)
    {
        _ = task.ContinueWith(
            t => Brio.Log.Error(t.Exception!, $"背景工作「{what}」以未處理的例外結束。"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
