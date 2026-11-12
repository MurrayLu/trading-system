using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Data;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Core.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MuNTrade.Core.DataProcessing.Store
{
    public sealed class KLineStore : IKLineStore
    {
        private readonly ConcurrentDictionary<(string sym, string tf), List<JsonKLineRecord>> _data
            = new(SymbolTfTupleComparer.Instance);
        private const int MaxBarsPerSeries = 512;

        private readonly ConcurrentDictionary<(string sym, string tf), bool> _ready
            = new(SymbolTfTupleComparer.Instance);

        private readonly ConcurrentDictionary<(string sym, string type, int period), DynamicBar> _openBars = new();

        public void PutInitial(string symbol, string timeframe, IReadOnlyList<JsonKLineRecord> bars)
        {
            var key = (symbol, timeframe);
            var list = _data.GetOrAdd(key, _ => new List<JsonKLineRecord>(bars.Count));
            lock (list)
            {
                list.Clear();
                if (bars.Count <= MaxBarsPerSeries)
                {
                    list.AddRange(bars);
                }
                else
                {
                    var start = bars.Count - MaxBarsPerSeries;
                    for (int i = start; i < bars.Count; i++)
                    {
                        list.Add(bars[i]);
                    }
                }
            }
        }

        public IReadOnlyList<JsonKLineRecord> GetTail(string symbol, string timeframe, int n)
        {
            var key = (symbol, timeframe);
            if (!_data.TryGetValue(key, out var list)) return Array.Empty<JsonKLineRecord>();
            lock (list)
            {
                if (n <= 0 || n >= list.Count) return list.ToArray();
                return list.Skip(Math.Max(0, list.Count - n)).ToArray();
            }
        }

        public void UpsertLast(string symbol, string timeframe, JsonKLineRecord bar)
        {
            var key = (symbol, timeframe);
            var list = _data.GetOrAdd(key, _ => new List<JsonKLineRecord>());
            lock (list)
            {
                if (list.Count == 0)
                {
                    list.Add(bar);
                }
                var last = list[^1];
                if (last.KlineTime == bar.KlineTime)
                {
                    list[^1] = bar; // 同一根（同 Key / 同分鐘）覆蓋更新
                }
                else if (last.KlineTime < bar.KlineTime)
                {
                    list.Add(bar); // 較新 → 追加
                }
                while (list.Count > MaxBarsPerSeries)
                {
                    list.RemoveAt(0);
                }
                // 舊資料則忽略
            }
        }

        // ----------------------
        // 以下為補齊的方法（interface 要求）
        // ----------------------
        public void MarkTfReady(string symbol, string timeframe) => _ready[(symbol, timeframe)] = true;

        public bool IsTfReady(string symbol, string timeframe)
            => _ready.TryGetValue((symbol, timeframe), out var ok) && ok;

        public bool IsAllTfReady(string symbol, IEnumerable<string> tfs)
        {
            foreach (var tf in tfs)
            {
                if (!IsTfReady(symbol, tf)) return false;
            }
            return true;
        }

        public void Clear(string symbol)
        {
            foreach (var kv in _data.Keys.Where(k => k.sym.Equals(symbol, StringComparison.OrdinalIgnoreCase)).ToList())
                _data.TryRemove(kv, out _);
            foreach (var kv in _ready.Keys.Where(k => k.sym.Equals(symbol, StringComparison.OrdinalIgnoreCase)).ToList())
                _ready.TryRemove(kv, out _);
        }

        // 這個方法供你在關棒時寫回 open bar 狀態；你已有其他寫回點也行
        public void UpsertOpenBar(DynamicBar bar)
        {
            if (bar.Config is null) return;
            var key = (bar.StockId, bar.Config.Type, bar.Config.Period);
            _openBars[key] = bar;
        }


        public bool TryGetOpenBar(string symbol, BarConfig cfg, out DynamicBar? snapshot)
        {
            var key = (symbol, cfg.Type, cfg.Period);
            if (_openBars.TryGetValue(key, out var cur))
            {
                snapshot = DynamicBar.FromSnapshot(
                    symbol, cfg,
                    cur.StartTime, cur.EndTime,
                    cur.Open, cur.High, cur.Low, cur.Close,
                    cur.Volume, cur.TickCount, cur.LastTickTime
                );
                return true;
            }
            snapshot = null;
            return false;
        }
    }
}







