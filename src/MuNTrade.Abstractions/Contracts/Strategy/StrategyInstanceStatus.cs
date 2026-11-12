using System;
using MuNTrade.Abstractions.Events.Strategy;

namespace MuNTrade.Abstractions.Contracts.Strategy
{
    public sealed record StrategyInstanceStatus(
        StrategyInstanceId InstanceId,
        StrategyTemplateId TemplateId,
        string? StrategyId, // for O(1) Cache
        string AccountKey,
        StrategyState State,
        Abstractions.Events.Strategy.StrategyRuntimeState RuntimeState,
        IReadOnlyCollection<Type> Subscriptions, // for Type Gate
        Abstractions.Contracts.Strategy.IStrategyIngressSpec? IngressSpec,
        long SubscriptionsVersion, // for Cache Invalidation
        DateTimeOffset LastSeenAt);
}

