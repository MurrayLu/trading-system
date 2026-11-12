using System;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Contracts.Eventing;

namespace MuNTrade.Abstractions.Events.Broker
{
    public sealed class BrokerPositionQueryCompletedEvent : IMuEvent
    {
        public string Account { get; init; } = "";
        public MarketKind MarketKind { get; init; } // Securities / Derivatives
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; } // IMuEvent requirement usually
    }
}

