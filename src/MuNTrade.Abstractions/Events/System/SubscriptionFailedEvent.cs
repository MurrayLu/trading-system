using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

namespace MuNTrade.Abstractions.Events.System
{
    /// <summary>
    /// 訂閱失敗（例如超過名額、或券商回報錯誤），由 SubscriptionManager 發佈。
    /// </summary>
    public enum FailureKind
    {
        Unknown,
        TickAttachTimeout,
        TickAttachException,
        TickLimitRejected,
        QuoteSubscribeFailure,
        BrokerHardFailure
    }

    public enum FailureScope
    {
        TickOnly,
        QuoteOnly,
        SymbolLifecycle
    }

    public enum FailureStage
    {
        Activation,
        Runtime,
        Disable,
        Recovery
    }

    public sealed record SubscriptionFailedEvent : IMuEvent
    {
        public string Symbol { get; init; } = "";
        public string Reason { get; init; } = "";
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }

        // Structured failure details.
        public FailureKind FailureKind { get; init; } = FailureKind.Unknown;
        public FailureScope FailureScope { get; init; } = FailureScope.SymbolLifecycle;
        public FailureStage Stage { get; init; } = FailureStage.Activation;

        public bool ShouldRetry { get; init; }
        public bool ShouldRemoveBoard { get; init; }
        public bool ShouldDisableSymbol { get; init; }

        public string BrokerActionTaken { get; init; } = "None";
        public int RetryDelayMs { get; init; }
        public int Attempt { get; init; }
        public string Source { get; init; } = "";
    }
}

