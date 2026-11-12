using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine
using MuNTrade.Abstractions.Domain.MarketData.Enums;

namespace MuNTrade.Abstractions.Events.MarketData
{
    public sealed record KLinePartialUpdatedEvent(KLine PartialBar) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
        public TickFeedKind FeedKind { get; init; } = TickFeedKind.RealTime;
    }
}

