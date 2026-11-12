using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.OrderBook.Iceberg
{
    public sealed record IcebergRetreatedEvent(string Symbol, decimal Price, bool IsBid, ArrivalTs ArrivalTimestamp, int RetreatAbsDelta, int ExplainedByTrades) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
