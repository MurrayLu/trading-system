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

namespace MuNTrade.Core.Indicators.KLine.ATR
{
    public sealed class AtrIndicator : IKLineIndicator
    {
        public string Name => "ATR";
        private readonly int _n;
        // states[symbol][tf] = ATR(n)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ATR>> _states
            = new(StringComparer.OrdinalIgnoreCase);

        public AtrIndicator(int period) => _n = Math.Max(2, period);

        public IReadOnlyDictionary<string, IReadOnlyList<IndicatorSeriesPoint>> Initialize(
            string symbol, string timeframe, IReadOnlyList<JsonKLineRecord> bars)
        {
            var tfDict = _states.GetOrAdd(symbol, _ => new(StringComparer.OrdinalIgnoreCase));
            var atr = new ATR(_n);

            IReadOnlyList<JsonKLineRecord> ordered = bars;
            if (bars.Count > 1 && bars[0].KlineTime > bars[^1].KlineTime)
                ordered = bars.Reverse().ToList();

            foreach (var b in ordered)
                atr.Update(Convert.ToDecimal(b.High), Convert.ToDecimal(b.Low), Convert.ToDecimal(b.Close));

            tfDict[timeframe] = atr;
            return new Dictionary<string, IReadOnlyList<IndicatorSeriesPoint>>();
        }

        public IReadOnlyDictionary<string, decimal> OnClosed(string symbol, string timeframe,
            decimal open, decimal high, decimal low, decimal close, ExchangeTs ts)
        {
            var tfDict = _states.GetOrAdd(symbol, _ => new(StringComparer.OrdinalIgnoreCase));
            if (!tfDict.TryGetValue(timeframe, out var atr))
                tfDict[timeframe] = atr = new ATR(_n);

            var v = atr.Update(high, low, close);
            return atr.HasValue ? new Dictionary<string, decimal> { [$"ATR{_n}"] = v } : new Dictionary<string, decimal>();
        }

        public IReadOnlyDictionary<string, decimal> OnPartial(string symbol, string timeframe,
            decimal open, decimal high, decimal low, decimal close, ExchangeTs ts)
        {
            var tfDict = _states.GetOrAdd(symbol, _ => new(StringComparer.OrdinalIgnoreCase));
            if (!tfDict.TryGetValue(timeframe, out var atr))
                tfDict[timeframe] = atr = new ATR(_n);

            var pv = atr.Preview(high, low, close);
            return pv.HasValue ? new Dictionary<string, decimal> { [$"ATR{_n}"] = pv.Value } : new Dictionary<string, decimal>();
        }
    }
}

