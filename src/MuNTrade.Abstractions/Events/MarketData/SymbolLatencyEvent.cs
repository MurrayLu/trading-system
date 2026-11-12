using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

namespace MuNTrade.Abstractions.Events.MarketData
{
    /// <summary>
    /// 用於傳遞 Joiner 的即時數據延遲 (Tick vs Best5)
    /// </summary>
    public sealed record SymbolLatencyEvent(string Symbol, double JoinLatencyMs) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
    }
}

