using System.Collections.Generic;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;

namespace MuNTrade.Abstractions.Events.Broker
{
    public sealed record BrokerPositionMetricsEvent : MuNTrade.Abstractions.Contracts.Eventing.IMuEvent
    {
        public global::System.DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
        public List<PositionMetricDto> Metrics { get; init; } = new();
    }

    public sealed record PositionMetricDto
    {
        public string Account { get; init; } = "";
        public string Symbol { get; init; } = "";
        public long Quantity { get; init; }
        public decimal? MarketPrice { get; init; }
        public decimal? AverageCost { get; init; }
        public decimal? UnrealizedPnL { get; init; }
        public decimal? BreakEvenPrice { get; init; }
        public decimal? Commission { get; init; }
        public decimal? Tax { get; init; }
        public decimal? PnLRatio { get; init; }
    }
}

