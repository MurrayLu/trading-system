using System;
using MuNTrade.Abstractions.Events.Strategy;

namespace MuNTrade.Abstractions.Contracts.Strategy
{
    public sealed record StrategyInstanceStatus(
        StrategyInstanceId InstanceId,
        StrategyTemplateId TemplateId,
        string? StrategyId, // [New] for 10B O(1) Cache
        string AccountKey, // [New] 10C Pinning
        StrategyState State,
        Abstractions.Events.Strategy.StrategyRuntimeState RuntimeState,
        IReadOnlyCollection<Type> Subscriptions, // [New] for 10B Type Gate
        Abstractions.Contracts.Strategy.IStrategyIngressSpec? IngressSpec, // [New] 12-2 Spec Filter
        long SubscriptionsVersion, // [New] for Refined 10B Cache Invalidation
        DateTimeOffset LastSeenAt);
}

