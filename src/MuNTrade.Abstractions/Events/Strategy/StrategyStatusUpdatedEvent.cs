using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Abstractions.Events.Strategy
{
    /// <summary>
    /// Strategy status snapshot update for UI console.
    /// Payload is defined by the strategy that emits the event.
    /// </summary>
    public sealed record StrategyStatusUpdatedEvent(
        StrategyInstanceId InstanceId,
        StrategyTemplateId TemplateId,
        DateOnly TradeDate,
        object Payload,
        DateTimeOffset OccurredAt,
        string? CorrelationId = null
    ) : IMuEvent;
}

