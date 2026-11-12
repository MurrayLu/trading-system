using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.OrderBook.Iceberg
{
    public sealed record IcebergStoryEvent(string Symbol, decimal Price, bool IsBid, ArrivalTs ArrivalTimestamp, string StoryName, string Description, double Score) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
