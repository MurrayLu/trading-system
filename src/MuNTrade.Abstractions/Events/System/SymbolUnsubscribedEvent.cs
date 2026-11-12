using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

namespace MuNTrade.Abstractions.Events.System
{
    /// <summary>
    /// 當一個商品成功被退訂時發布。
    /// </summary>
    public sealed record SymbolUnsubscribedEvent : IMuEvent
    {
        public string Symbol { get; init; } = "";
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
    }
}

