// 檔案路徑: MuNTrade.Abstractions/Domain/Models/OrderFillEvent.cs
using System;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;

namespace MuNTrade.Abstractions.Domain.Trading
{
    /// <summary>
    /// 【升級後版本】
    /// 代表一筆訂單成交的回報事件。
    /// </summary>
    public class OrderFillEvent
    {
        public string? StockId { get; set; }
        public DateTime FillTime { get; set; }

        /// <summary>
        /// 【修改】使用 OrderSide，明確表示執行的動作是「買」還是「賣」。
        /// </summary>
        public OrderSide Side { get; set; }

        public decimal FillPrice { get; set; }
        public long FillQuantity { get; set; }

        /// <summary>
        /// ★ 新增：對應到原始委託的唯一識別碼。
        /// </summary>
        public string? OrderId { get; set; }

        /// <summary>
        /// ★ 建議新增：保留原始訊號的原因，方便追蹤
        /// </summary>
        public string? OriginalReason { get; set; }

        /// <summary>
        /// ★ 新增：帳戶識別 (區分 Real / Paper / SIM)
        /// </summary>
        public string Account { get; set; } = string.Empty;

        public string StrategyId { get; set; } = "";
        public string CorrelationId { get; set; } = "";
        public string AccountKey { get; set; } = "";
    }
}

