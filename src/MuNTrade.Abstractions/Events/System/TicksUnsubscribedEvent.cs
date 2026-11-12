using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

namespace MuNTrade.Abstractions.Events.System
{
    /// <summary>
    /// 逐筆／五檔退訂成功（Pipeline 層 or SubscriptionManager 發佈）。
    /// </summary>
    public sealed record TicksUnsubscribedEvent : IMuEvent
    {
        public string Symbol { get; init; } = "";
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
    }
}

