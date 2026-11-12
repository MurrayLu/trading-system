using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

namespace MuNTrade.Abstractions.Events.System.Warmup
{
    public sealed record SymbolReadyEvent(string Symbol) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
    }
}

