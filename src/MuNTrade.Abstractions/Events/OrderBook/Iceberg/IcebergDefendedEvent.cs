using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.OrderBook.Iceberg
{
    public sealed record IcebergDefendedEvent(string Symbol, decimal Price, bool IsBid, ArrivalTs ArrivalTimestamp, int BounceTicks, int AggressorVol) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
