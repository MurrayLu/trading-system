using System;
using MuNTrade.Abstractions.Domain.Common.Enums;

// ★ 核心修改：將 class 改為 record，並將 set 改為 init
// init 表示這個屬性只能在物件初始化時被賦值
namespace MuNTrade.Abstractions.Domain.Trading
{
    public record Position
    {
        public string StockId { get; init; } = string.Empty;
        public long Quantity { get; init; }
        public decimal AveragePrice { get; init; }
        public DateTime EntryTime { get; init; }
        public decimal LastPrice { get; init; }
        public decimal UnrealizedPnL { get; init; }
        public decimal EstimatedTotalCost { get; init; }
        public decimal BreakEvenPrice { get; init; }
        public string Account { get; init; } = string.Empty;

        public string StrategyId { get; init; } = "";
        public string AccountKey { get; init; } = "";
        public string EntryCorrelationId { get; init; } = "";

        // [Step 3] 新增：市場真相寫入快照
        public MarketNo MarketNo { get; init; } = MarketNo.Unknown;
        public MarketSource Source { get; init; } = MarketSource.Domestic;
    }
}
