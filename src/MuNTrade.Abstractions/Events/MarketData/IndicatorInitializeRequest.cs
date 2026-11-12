using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

namespace MuNTrade.Abstractions.Events.MarketData
{
    using MuNTrade.Abstractions.Domain.Session; // For JsonKLineRecord

    /// <summary>
    /// 請指標服務以指定歷史 K 線狀態做一次批量初始化（不觸發交易信號，僅建立內部狀態），
    /// 之後會改走增量（OnTick/OnPartial/OnClosed）。
    /// BarsByTimeframe 的 key 用 Time_1 / Time_5 / Time_60 / Day / Week / Month 等與 KLineStore 相同的標識。
    /// </summary>
    public sealed record IndicatorInitializeRequest(
        string Symbol,
        IReadOnlyDictionary<string, IReadOnlyList<JsonKLineRecord>> BarsByTimeframe) : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
    }
}

