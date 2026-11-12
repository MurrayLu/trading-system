using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Contracts.Hubs;

namespace MuNTrade.Core.OrderBook
{
    /// <summary>
    /// 將 OrderBook 相關事件寫入 ring buffer，並定期輸出統計摘要。
    /// </summary>
    internal sealed class OrderBookEventLogSink
    {
        private readonly IEventBus _bus;
        private readonly TimeSpan _retention = TimeSpan.FromMinutes(30);
        private readonly TimeSpan _summaryInterval = TimeSpan.FromSeconds(60);

        private readonly ConcurrentDictionary<string, SymbolBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);

        public OrderBookEventLogSink(IEventBus bus)
        {
            _bus = bus;
        }

        public void Record(string symbol, string name, ArrivalTs arrival, ExchangeTs? exchange, bool? isBid, decimal? price, Dictionary<string, double> metrics, int bidLevels, int askLevels, int evictions)
        {
            var buf = _buffers.GetOrAdd(symbol, s => new SymbolBuffer(_retention, _summaryInterval));
            buf.Add(new Entry
            {
                Arrival = arrival,
                Exchange = exchange,
                Symbol = symbol,
                Name = name,
                IsBid = isBid,
                Price = price,
                Metrics = metrics,
                BidLevels = bidLevels,
                AskLevels = askLevels,
                Evictions = evictions
            });

            if (buf.TryBuildSummary(out var summary))
            {
                _bus.Publish(summary);
            }
        }

        private sealed class SymbolBuffer
        {
            private readonly TimeSpan _retention;
            private readonly TimeSpan _summaryInterval;
            private readonly LinkedList<Entry> _entries = new();
            private ArrivalTs _lastSummaryTs = new(DateTime.MinValue);

            public SymbolBuffer(TimeSpan retention, TimeSpan summaryInterval)
            {
                _retention = retention;
                _summaryInterval = summaryInterval;
            }

            public void Add(Entry entry)
            {
                _entries.AddLast(entry);
                Sweep(entry.Arrival);
            }

            private void Sweep(ArrivalTs now)
            {
                while (_entries.First != null && (now - _entries.First.Value.Arrival) > _retention)
                {
                    _entries.RemoveFirst();
                }
            }

            public bool TryBuildSummary(out OrderBookEventSummary summary)
            {
                summary = null!;
                if (_entries.Count == 0) return false;
                var latest = _entries.Last!.Value.Arrival;
                if ((latest - _lastSummaryTs) < _summaryInterval) return false;

                _lastSummaryTs = latest;

                int suspect = 0, confirmed = 0, cumHang = 0, cumPull = 0, cumRepl = 0, bigHang = 0, bigPull = 0;
                int levelsBid = 0, levelsAsk = 0, evictions = 0;
                int matchedSamples = 0, replenishSamples = 0, ratioSamples = 0, touchSamples = 0;

                foreach (var e in _entries)
                {
                    switch (e.Name)
                    {
                        case nameof(IcebergSuspectEvent):
                            suspect++; break;
                        case nameof(IcebergConfirmedEvent):
                            confirmed++; break;
                        case nameof(CumHangEvent):
                            cumHang++; break;
                        case nameof(CumPullEvent):
                            cumPull++; break;
                        case nameof(CumReplenishEvent):
                            cumRepl++; break;
                        case nameof(BigHangEvent):
                            bigHang++; break;
                        case nameof(BigPullEvent):
                            bigPull++; break;
                    }

                    if (e.Metrics.TryGetValue("matchedSamples", out var mv)) matchedSamples = (int)mv;
                    if (e.Metrics.TryGetValue("replenishSamples", out var rv)) replenishSamples = (int)rv;
                    if (e.Metrics.TryGetValue("ratioSamples", out var rsv)) ratioSamples = (int)rsv;
                    if (e.Metrics.TryGetValue("touchSamples", out var tv)) touchSamples = (int)tv;

                    levelsBid = e.BidLevels;
                    levelsAsk = e.AskLevels;
                    evictions = e.Evictions;
                }

                double staleRatio = _entries.Count == 0 ? 0 : _entries.Count(e => e.Metrics.ContainsKey("stale") && e.Metrics["stale"] > 0) / (double)_entries.Count;

                summary = new OrderBookEventSummary(
                    _entries.Last!.Value.Symbol,
                    latest,
                    suspect,
                    confirmed,
                    cumHang,
                    cumPull,
                    cumRepl,
                    bigHang,
                    bigPull,
                    levelsBid,
                    levelsAsk,
                    evictions,
                    staleRatio,
                    matchedSamples,
                    replenishSamples,
                    ratioSamples,
                    touchSamples
                );
                return true;
            }
        }

        private sealed class Entry
        {
            public ArrivalTs Arrival { get; set; }
            public ExchangeTs? Exchange { get; set; }
            public string Symbol { get; set; } = "";
            public string Name { get; set; } = "";
            public bool? IsBid { get; set; }
            public decimal? Price { get; set; }
            public Dictionary<string, double> Metrics { get; set; } = new();
            public int BidLevels { get; set; }
            public int AskLevels { get; set; }
            public int Evictions { get; set; }
        }
    }
}

