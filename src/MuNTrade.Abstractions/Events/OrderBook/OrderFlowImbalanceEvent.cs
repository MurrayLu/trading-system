using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.OrderBook
{
    public sealed record OrderFlowImbalanceEvent(
        string Symbol,
        ArrivalTs Timestamp,
        double NetFlow,
        double CumulativeOfi,
        double? OfiSma50,
        double? OfiSma300,
        double? OfiSma1500
    ) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
