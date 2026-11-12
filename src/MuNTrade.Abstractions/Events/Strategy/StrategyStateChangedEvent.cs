using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Abstractions.Events.Strategy
{
    public sealed record StrategyStateChangedEvent(
        ArrivalTs Time,
        StrategyInstanceId InstanceId,
        string Symbol,
        StrategyState From,
        StrategyState To,
        string? Reason = null
    ) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
    }
}

