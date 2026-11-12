using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Domain.Trading.Enums;

namespace MuNTrade.Abstractions.Domain.Trading
{
    public sealed class RealizedLeg
    {
        public string LegId { get; set; } = string.Empty;
        public string AccountKey { get; set; } = string.Empty;
        public string Account { get; set; } = string.Empty;
        public string StockId { get; set; } = string.Empty;
        public OrderSide ExitSide { get; set; }
        public long CloseQty { get; set; }
        public decimal ExitPrice { get; set; }
        public DateTime ExitTime { get; set; }
        public string ExitFillId { get; set; } = string.Empty;
        public List<MatchedLot> MatchedLots { get; set; } = new();
        public decimal GrossPnL { get; set; }
        public decimal Fee { get; set; }
        public decimal Tax { get; set; }
        public decimal NetPnL { get; set; }
        public string StrategyId { get; set; } = string.Empty;
        public string ExitInitiatorStrategyId { get; set; } = string.Empty;
        public bool AttributionConflict { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
    }

    public sealed class MatchedLot
    {
        public string LotId { get; set; } = string.Empty;
        public DateTime EntryTime { get; set; }
        public decimal EntryPrice { get; set; }
        public long MatchedQty { get; set; }
        public string SourceFillId { get; set; } = string.Empty;
        public string EntryStrategyId { get; set; } = string.Empty;
        public string EntryCorrelationId { get; set; } = string.Empty;
    }
}

