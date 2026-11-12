using MuNTrade.Abstractions.Domain.OrderBook;
using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.OrderBook
{
    public sealed record BigPullEvent(
        string Symbol,
        bool IsBid,
        ArrivalTs ArrivalTimestamp,
        ExchangeTs? ExchangeTimestamp,
        double Delta,
        FlowWindowStats Stats,
        bool SuppressedByQuality = false,
        decimal? Price = null,
        LiquidityGuardReason Reason = LiquidityGuardReason.None,
        bool IsSuppressed = false
    ) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
