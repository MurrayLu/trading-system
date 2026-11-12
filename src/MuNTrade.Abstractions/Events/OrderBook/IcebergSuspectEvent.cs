using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.OrderBook
{
    // 冰山偵測：懷疑事件（低信心可先行發出）
    public sealed record IcebergSuspectEvent(
        string Symbol,
        decimal Price,
        bool IsBid,
        ArrivalTs ArrivalTimestamp,
        ExchangeTs? ExchangeTimestamp,
        double Score1m,
        double Score5m,
        double Score20m,
        double MatchedDecayed,
        double ReplenishDecayed,
        double TouchesDecayed,
        double Ratio,
        double Confidence,
        double AbsorptionPct
    ) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
