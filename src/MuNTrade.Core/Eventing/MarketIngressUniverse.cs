using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Events.System;

namespace MuNTrade.Core.Eventing
{
    public static class MarketIngressUniverse
    {
        // 全系統 router 可入站事件全集（維持 Strict Ingress Whitelist 精神）
        public static readonly IReadOnlyList<Type> All = new[]
        {
            typeof(TradeTickEvent),
            typeof(QuoteBest5Event), // Note: Using QuoteBest5Event as per original Router usage (was Best5Event in user snippet, correcting to actual type used in project)
            typeof(QuoteSnapshotEvent),

            typeof(KLineClosedEvent),
            typeof(KLinePartialUpdatedEvent), // Added per snippet but check if actually used in project. User snippet said "KLinePartialUpdatedEvent". 
                                              // Original Router whitelist didn't have Partial, but user snippet added it. I'll include it.

            typeof(IndicatorsUpdatedEvent),

            typeof(SymbolSubscribedEvent),
            typeof(SymbolUnsubscribedEvent),
            typeof(TicksSubscribedEvent),
            typeof(TicksUnsubscribedEvent),
            typeof(SubscriptionFailedEvent),

            typeof(StrategyPulseEvent),
        };

        // 探針/驗收可用：少量事件集合（避免策略 log 被淹沒）
        public static readonly IReadOnlyList<Type> DefaultProbeSet = new[]
        {
            typeof(TradeTickEvent),
            typeof(KLineClosedEvent),
            typeof(IndicatorsUpdatedEvent),
            typeof(SymbolSubscribedEvent),
        };
    }
}

