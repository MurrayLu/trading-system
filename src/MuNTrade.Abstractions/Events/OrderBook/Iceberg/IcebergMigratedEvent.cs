using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.OrderBook.Iceberg
{
    public sealed record IcebergMigratedEvent(string Symbol, decimal FromPrice, decimal ToPrice, bool IsBid, ArrivalTs ArrivalTimestamp) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
