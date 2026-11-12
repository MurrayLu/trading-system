using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

namespace MuNTrade.Abstractions.Events.System.Warmup
{
    /// <summary>
    /// 在某個時間週期的暖機開始時發佈。
    /// </summary>
    public sealed class WarmupStageStartedEvent : IMuEvent
    {
        public string Symbol { get; init; } = "";
        public string Timeframe { get; init; } = "";
        public int TargetBars { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
    }
}

