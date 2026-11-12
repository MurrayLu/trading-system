using System;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.OrderBook
{
    // 冰山偵測：清除事件（消逝或移除）
    public sealed record IcebergClearedEvent(
        string Symbol,
        decimal Price,
        bool IsBid,
        ArrivalTs ArrivalTimestamp
    ) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
