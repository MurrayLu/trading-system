using System;
using MuNTrade.Abstractions.Domain.Common.Enums;

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

        // Snapshot written by the market state source.
        public MarketNo MarketNo { get; init; } = MarketNo.Unknown;
        public MarketSource Source { get; init; } = MarketSource.Domestic;
    }
}
