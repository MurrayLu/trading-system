using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

namespace MuNTrade.Abstractions.Events.MarketData
{
    /// <summary>
    /// 指標輸出事件（通用容器）。來源：IndicatorCalculator。
    /// 用途：策略引擎、風控、UI。
    /// </summary>
    public sealed class IndicatorsUpdatedEvent : IMuEvent
    {
        public string Symbol { get; init; } = "";
        /// <summary>如 ""Time_1""、""Time_5""、""tick""、""mur"" 等。</summary>
        public string Period { get; init; } = "";
        /// <summary>true=partial；false=closed</summary>
        public bool IsPartial { get; init; }
        public ExchangeTs ExchangeTs { get; init; }
        public ArrivalTs ArrivalTs { get; init; }

        /// <summary>例：{""sma20"": 123.45m, ""atr14"": 1.23m, ""bias"": 0.56m}</summary>
        public IReadOnlyDictionary<string, decimal> Values { get; init; } = new Dictionary<string, decimal>();

        /// <summary>
        /// 觸發此次指標計算的來源 KLine Bar。
        /// 對於非 K線觸發的指標 (如 tick-based OFI)，此屬性可能為 null。
        /// </summary>
        public KLine? SourceBar { get; init; }

        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
    }
}

