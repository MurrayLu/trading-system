using System;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Scheduling
{
    /// <summary>
    /// 定義排程任務最基本的界面，方便日後擴充更多型別的排程。
    /// </summary>
    public interface ITradingScheduleTask
    {
        /// <summary>
        /// 任務名稱，用於日誌輸出。
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 建議的檢查頻率；排程器會依此間隔呼叫 ShouldRun。
        /// </summary>
        TimeSpan PollingInterval { get; }

        /// <summary>
        /// 由任務自己判斷目前時間是否需要執行，傳入的 now 來自 IClock。
        /// </summary>
        bool ShouldRun(DateTime now);

        /// <summary>
        /// 真正執行任務的內容，call site 會傳入同一個 now 以及取消訊號。
        /// </summary>
        Task ExecuteAsync(DateTime now, CancellationToken cancellationToken);
    }
}

