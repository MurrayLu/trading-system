using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.OrderBook.Iceberg
{
    public sealed record IcebergPersistEvent(string Symbol, decimal Price, bool IsBid, ArrivalTs ArrivalTimestamp, int Level /*1,2,3*/, double DurationSec) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
