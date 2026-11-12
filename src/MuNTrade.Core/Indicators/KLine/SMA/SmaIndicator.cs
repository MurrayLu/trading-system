using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Time;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Core.Indicators.KLine;

namespace MuNTrade.Core.Indicators.KLine.SMA
{
    public sealed class SmaIndicator : IKLineIndicator
    {
        public string Name => "SMA";

        private readonly int[] _periods;
        // states[symbol][tf][period] = SMA
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Dictionary<int, SMA>>> _states
            = new(StringComparer.OrdinalIgnoreCase);

        public SmaIndicator(int[] periods) => _periods = periods ?? Array.Empty<int>();

        public IReadOnlyDictionary<string, IReadOnlyList<IndicatorSeriesPoint>> Initialize(
            string symbol, string timeframe, IReadOnlyList<JsonKLineRecord> bars)
        {
            var tfDict = _states.GetOrAdd(symbol, _ => new(StringComparer.OrdinalIgnoreCase));
            var dict = tfDict.GetOrAdd(timeframe, _ => new Dictionary<int, SMA>());
            var result = new Dictionary<string, IReadOnlyList<IndicatorSeriesPoint>>();

            // 確保 bars 是舊->新
            IReadOnlyList<JsonKLineRecord> ordered = bars;
            if (bars.Count > 1 && bars[0].KlineTime > bars[^1].KlineTime)
                ordered = bars.Reverse().ToList();

            // 為每個週期準備收集器
            var collectors = new Dictionary<int, List<IndicatorSeriesPoint>>(_periods.Length);

            // 重置並初始化每個週期的 SMA
            foreach (var n in _periods)
            {
                var sma = new SMA(n);
                dict[n] = sma;
                collectors[n] = new List<IndicatorSeriesPoint>(ordered.Count);
            }

            // 一次性遍歷所有歷史 K 棒，建立完整序列同時建立狀態
            foreach (var b in ordered)
            {
                var closeParams = Convert.ToDecimal(b.Close);
                var time = b.KlineTime;

                foreach (var n in _periods)
                {
                    if (dict.TryGetValue(n, out var sma))
                    {
                        var v = sma.Update(closeParams);
                        if (sma.HasValue)
                        {
                            collectors[n].Add(new IndicatorSeriesPoint(time, v));
                        }
                    }
                }
            }

            // 轉為唯讀回傳
            foreach (var kv in collectors)
            {
                if (kv.Value.Count > 0)
                    result[$"SMA{kv.Key}"] = kv.Value;
            }

            return result;
        }


        public IReadOnlyDictionary<string, decimal> OnClosed(string symbol, string timeframe,
            decimal open, decimal high, decimal low, decimal close, ExchangeTs ts)
        {
            var tfDict = _states.GetOrAdd(symbol, _ => new(StringComparer.OrdinalIgnoreCase));
            var dict = tfDict.GetOrAdd(timeframe, _ => new Dictionary<int, SMA>());

            var values = new Dictionary<string, decimal>();
            foreach (var n in _periods)
            {
                if (!dict.TryGetValue(n, out var sma))
                    dict[n] = sma = new SMA(n);

                var v = sma.Update(close);
                if (sma.HasValue) values[$"SMA{n}"] = v;
            }
            return values;
        }

        public IReadOnlyDictionary<string, decimal> OnPartial(string symbol, string timeframe,
            decimal open, decimal high, decimal low, decimal close, ExchangeTs ts)
        {
            var tfDict = _states.GetOrAdd(symbol, _ => new(StringComparer.OrdinalIgnoreCase));
            var dict = tfDict.GetOrAdd(timeframe, _ => new Dictionary<int, SMA>());

            var values = new Dictionary<string, decimal>();
            foreach (var n in _periods)
            {
                if (!dict.TryGetValue(n, out var sma))
                    dict[n] = sma = new SMA(n);

                var pv = sma.Preview(close);
                if (pv.HasValue) values[$"SMA{n}"] = pv.Value; // 嚴格暖機：不足 n 不輸出
            }
            return values;
        }
    }
}

