using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.OrderBook
{
    public sealed record InstitutionalPresenceEvent(
        string Symbol,
        ArrivalTs Timestamp,
        decimal Price,
        int Volume,
        bool IsBid,
        string Note
    ) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
