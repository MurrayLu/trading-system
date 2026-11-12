using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.OrderBook
{
    public sealed record OrderBookAlertEvent(
        string Symbol,
        ArrivalTs Timestamp,
        string Message,
        decimal Price,
        int Quantity,
        bool IsBid,
        bool IsCancel
    ) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
