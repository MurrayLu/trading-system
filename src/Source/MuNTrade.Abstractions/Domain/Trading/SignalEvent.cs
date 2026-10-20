// 檔案路徑: MuNTrade.Abstractions/Domain/Models/SignalEvent.cs
using System;

namespace MuNTrade.Abstractions.Domain.Trading
{
    /// <summary>
    /// 【升級後版本】
    /// 事件：策略引擎產生了一個交易訊號。
    /// 這個訊號不再是「動作」，而是策略期望達到的「目標部位狀態」。
    /// </summary>
    public class SignalEvent
    {
        public string? StockId { get; set; }
        public DateTime SignalTime { get; set; }

        /// <summary>
        /// 【新的核心】策略期望的「目標部位比例」。
        /// 1.0m  = 100% 多頭部位 (滿倉做多)
        /// -1.0m = 100% 空頭部位 (滿倉做空)
        /// 0.5m  = 50% 多頭部位 (半倉做多)
        /// 0.0m  = 0% 部位 (空手)
        /// </summary>
        public decimal TargetPositionRatio { get; set; }

        /// <summary>
        /// 觸發此訊號的價格，可用於限價單或績效分析。
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// 產生此訊號的原因或策略名稱。
        /// </summary>
        public string? Reason { get; set; }

        /// <summary>
        /// 用於分類或標記的通用標籤。
        /// </summary>
        public string? Tag { get; set; }

        public string? StrategyId { get; set; }
        public string? AccountKey { get; set; }
    }
}

