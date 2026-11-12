using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

namespace MuNTrade.Abstractions.Events.System.Warmup
{
    /// <summary>
    /// 在暖機過程中，每回放一定數量的 K 棒時發佈，用於更新進度條。
    /// </summary>
    public sealed record WarmupPhaseProgressEvent(string Symbol, string Timeframe, int Produced, int Target) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
    }
}

