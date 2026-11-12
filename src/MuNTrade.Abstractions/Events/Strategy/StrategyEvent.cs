using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Abstractions.Events.Strategy
{
    public sealed record StrategyEvent(
        StrategyInstanceId InstanceId,
        StrategyTemplateId TemplateId,
        string Kind,
        object? Payload,
        DateTimeOffset OccurredAt,
        string? CorrelationId = null) : IMuEvent;
}

