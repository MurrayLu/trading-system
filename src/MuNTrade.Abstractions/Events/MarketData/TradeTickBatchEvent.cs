using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

namespace MuNTrade.Abstractions.Events.MarketData
{
    /// <summary>
    /// 逐筆成交批次事件，避免 UI 在回放期被逐筆淹沒。
    /// </summary>
    public sealed record TradeTickBatchEvent : IMuEvent
    {
        public string Symbol { get; init; } = "";
        public IReadOnlyList<TradeTickEvent> Ticks { get; init; } = Array.Empty<TradeTickEvent>();

        /// <summary>當前批次是否仍處於回放狀態。</summary>
        public bool IsCatchup { get; init; }

        /// <summary>是否剛完成回放並轉入即時模式。</summary>
        public bool CatchupCompleted { get; init; }

        /// <summary>最新一筆成交相對現在的落後秒數（負值視為 0）。</summary>
        public double CurrentLagSeconds { get; init; }

        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
    }
}

