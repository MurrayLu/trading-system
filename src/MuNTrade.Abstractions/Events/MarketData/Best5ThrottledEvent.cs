using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

namespace MuNTrade.Abstractions.Events.MarketData
{
    /// <summary>經節流後發佈給 UI/指標的五檔事件。</summary>
    public sealed record Best5ThrottledEvent(QuoteBest5Event Data) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
    }
}

