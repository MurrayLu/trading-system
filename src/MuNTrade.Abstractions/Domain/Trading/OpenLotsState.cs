using System;
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Domain.Trading
{
    public sealed class OpenLotsState
    {
        public int SchemaVersion { get; set; } = 1;
        public string AccountKey { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }
        public List<SymbolLots> Lots { get; set; } = new();
    }

    public sealed class SymbolLots
    {
        public string Symbol { get; set; } = string.Empty;
        public List<OpenLot> Lots { get; set; } = new();
    }

    public sealed class OpenLot
    {
        public string LotId { get; set; } = string.Empty;
        public DateTime EntryTime { get; set; }
        public decimal EntryPrice { get; set; }
        public long RemainingQty { get; set; }
        public string SourceFillId { get; set; } = string.Empty;
        public string EntryStrategyId { get; set; } = string.Empty;
        public string EntryCorrelationId { get; set; } = string.Empty;
    }
}

