using System.Collections.Generic;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.OrderBook
{
    public enum OrderBookQualityTier
    {
        Good,
        Degraded,
        Bad
    }

    public record OrderBookQualityEvent(
        string Symbol,
        ArrivalTs ArrivalTimestamp,
        double Score,
        OrderBookQualityTier Tier,
        List<string> Reasons,
        bool IsSuppressed,
        int SuppressedCount = 0
    );
}

