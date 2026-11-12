using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

namespace MuNTrade.Abstractions.Events.System
{
    using Serilog.Events;

    /// <summary>
    /// 當一筆新的日誌被記錄時，由 LogRelayService 發布此事件。
    /// </summary>
    public sealed record LogEventRaisedEvent : IMuEvent
    {
        public LogEvent Event { get; init; } = default!;
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
    }
}

