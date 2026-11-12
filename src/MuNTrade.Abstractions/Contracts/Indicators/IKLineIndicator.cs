using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using MuNTrade.Abstractions.Time;

namespace MuNTrade.Core.Indicators.KLine
{
    /// 指標實作的統一介面（K 線專用）
    public interface IKLineIndicator
    {
        string Name { get; }

        // 以 Warmup 歷史一次灌入（只會在 InitializeRequest 時呼叫）
        // 若有產生「歷史指標序列」可回傳，供 UI 繪製；若無則回傳 null/empty
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSeriesPoint>> Initialize(
            string symbol, string timeframe, IReadOnlyList<JsonKLineRecord> bars);

        // 收到「已收線」K 棒
        IReadOnlyDictionary<string, decimal> OnClosed(string symbol, string timeframe,
            decimal open, decimal high, decimal low, decimal close, ExchangeTs ts);

        // 收到「尚未收線」的 Partial K 棒（不可改狀態，只預覽數值）
        IReadOnlyDictionary<string, decimal> OnPartial(string symbol, string timeframe,
            decimal open, decimal high, decimal low, decimal close, ExchangeTs ts);
    }
}

