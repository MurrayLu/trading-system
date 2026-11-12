using System;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;

namespace MuNTrade.Abstractions.Domain.Trading
{
    /// <summary>
    /// 代表一筆訂單成交的回報事件。
    /// </summary>
    public class OrderFillEvent
    {
        public string? StockId { get; set; }
        public DateTime FillTime { get; set; }

        /// <summary>
        /// 使用 OrderSide 表示執行的動作是「買」還是「賣」。
        /// </summary>
        public OrderSide Side { get; set; }

        public decimal FillPrice { get; set; }
        public long FillQuantity { get; set; }

        /// <summary>
        /// ID of the original order.
        /// </summary>
        public string? OrderId { get; set; }

        /// <summary>
        /// Reason associated with the original signal.
        /// </summary>
        public string? OriginalReason { get; set; }

        /// <summary>
        /// Account identifier.
        /// </summary>
        public string Account { get; set; } = string.Empty;

        public string StrategyId { get; set; } = "";
        public string CorrelationId { get; set; } = "";
        public string AccountKey { get; set; } = "";
    }
}

