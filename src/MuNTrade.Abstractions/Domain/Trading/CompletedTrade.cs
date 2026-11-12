using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;

namespace MuNTrade.Abstractions.Domain.Trading
{
    /// <summary>
    /// 代表一筆完整的、已平倉的交易紀錄。
    /// </summary>
    public class CompletedTrade
    {
        public string? StockId { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public DateTime EntryTime { get; set; }
        public DateTime ExitTime { get; set; }
        public decimal Commission { get; set; }
        public decimal Tax { get; set; }
        public decimal GrossPnL { get; set; }
        public decimal NetPnL { get; set; }

        /// <summary>
        /// Buy = 作多 (先買後賣)
        /// Sell = 作空 (先賣後買)
        /// </summary>
        public OrderSide Direction { get; set; }

        public long Quantity { get; set; }
        public string EntryReason { get; set; } = string.Empty;
        public string ExitReason { get; set; } = string.Empty;

        public string Account { get; set; } = "Real"; // Distinguish Real vs Paper
        public string StrategyId { get; set; } = "";
        public string AccountKey { get; set; } = "";
        public string EntryCorrelationId { get; set; } = "";
        public string ExitCorrelationId { get; set; } = "";
        public bool AttributionConflict { get; set; }

        public long HoldDurationSeconds { get; set; }
        public List<string> LegIds { get; set; } = new();
    }
}

