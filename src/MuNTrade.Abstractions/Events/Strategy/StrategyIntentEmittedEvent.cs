using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Abstractions.Events.Strategy
{
    public sealed record StrategyIntentEmittedEvent(
        StrategyInstanceId InstanceId,
        StrategyTemplateId TemplateId,
        string IntentKind,
        object? Intent,
        DateTimeOffset OccurredAt,
        string? CorrelationId = null) : IMuEvent;
}

