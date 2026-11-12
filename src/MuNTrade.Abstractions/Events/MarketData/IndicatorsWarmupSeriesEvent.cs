using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Core.Indicators.KLine;

namespace MuNTrade.Abstractions.Events.MarketData
{
    /// <summary>
    /// 指標暖機序列事件（一次性送出完整序列）。
    /// 用於 UI / 策略在 warmup 後載入完整指標線。
    /// </summary>
    public sealed class IndicatorsWarmupSeriesEvent : IMuEvent
    {
        public string Symbol { get; init; } = "";
        /// <summary>如 "Time_1"、"Time_5"、"Day"...</summary>
        public string Period { get; init; } = "";
        /// <summary>key=指標名稱（ex: "SMA5"），value=該指標的序列點</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<IndicatorSeriesPoint>> Series { get; init; }
            = new Dictionary<string, IReadOnlyList<IndicatorSeriesPoint>>();

        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
    }

}

