using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Dtos.OrderBook;

namespace MuNTrade.Abstractions.Events.OrderBook
{
    public sealed record CumReplenishEvent(
        string Symbol,
        DateTime Ts,
        FlowWindowStats Stats
    ) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
