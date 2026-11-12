using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.OrderBook
{
    public sealed record LargeTradeEvent(
        string Symbol,
        ExchangeTs Timestamp,
        decimal Price,
        int Volume,
        AggressorSide Side,
        double Threshold
    ) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
