using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Time;
using MuNTrade.Core.Settings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MuNTrade.Core.OrderBook
{
    public interface ITradeQuoteJoiner
    {
        void OnBest5(Best5DeltaEvent best5);
        JoinMatchKind TryJoin(TradeTickEvent trade, out JoinedTradeQuote? joined, out double deltaMilliseconds);
        TimeSpan GetLookaheadBuffer(string symbol);
    }

    public enum JoinMatchKind
    {
        None = 0,
        Core = 1,
        Tail = 2,
        SnapshotFallback = 3
    }

    internal sealed class TradeQuoteJoiner : ITradeQuoteJoiner
    {
        private readonly ConcurrentDictionary<string, SymbolState> _states = new(StringComparer.OrdinalIgnoreCase);
        private readonly IOptionsMonitor<OrderBookProcessingOptions> _options;
        private readonly IEventBus _bus;
        private readonly IClock _clock;
        private readonly ConcurrentDictionary<string, JoinMetrics> _metrics = new(StringComparer.OrdinalIgnoreCase);

        public TradeQuoteJoiner(
            IOptionsMonitor<OrderBookProcessingOptions> options,
            IEventBus bus,
            IClock clock)
        {
            _options = options;
            _bus = bus;
            _clock = clock;
        }

        public void OnBest5(Best5DeltaEvent best5)
        {
            if (best5 is null || string.IsNullOrWhiteSpace(best5.Symbol))
                return;

            var joinOpt = _options.CurrentValue.Join;
            var state = _states.GetOrAdd(best5.Symbol, _ => new SymbolState(joinOpt));
            var snapshot = Best5Snapshot.From(best5);
            var historyWindow = TimeSpan.FromSeconds(Math.Max(1, joinOpt.history_seconds));

            lock (state.Sync)
            {
                state.Snapshots.Add(snapshot);
                state.LastSnapshot = snapshot;
                TrimSnapshots(state.Snapshots, best5.ArrivalTs - historyWindow);
            }
        }

        public JoinMatchKind TryJoin(TradeTickEvent trade, out JoinedTradeQuote? joined, out double deltaMilliseconds)
        {
            joined = null;
            deltaMilliseconds = double.NaN;

            if (trade is null || string.IsNullOrWhiteSpace(trade.Symbol))
                return JoinMatchKind.None;

            if (!_states.TryGetValue(trade.Symbol, out var state))
                return JoinMatchKind.None;

            var joinOpt = _options.CurrentValue.Join;
            var windows = state.GetWindows(joinOpt);
            var core = TimeSpan.FromMilliseconds(Math.Max(1, windows.coreMs));
            var tail = TimeSpan.FromMilliseconds(Math.Max(core.TotalMilliseconds, windows.tailMs));
            var maxSnapshotAge = TimeSpan.FromMilliseconds(Math.Max(tail.TotalMilliseconds, joinOpt.snapshot_max_age_ms));
            var tradeTs = trade.ArrivalTs;

            Best5Snapshot? pre = null;
            Best5Snapshot? post = null;
            TimeSpan preDelta = TimeSpan.MaxValue;
            TimeSpan postDelta = TimeSpan.MaxValue;
            Best5Snapshot? lastSnapshot = null;
            bool isHistoryData = false;

            lock (state.Sync)
            {
                if (state.Snapshots.Count > 0 && tradeTs < state.Snapshots[0].ArrivalTs)
                {
                    isHistoryData = true;
                }
                else if (state.Snapshots.Count > 0)
                {
                    int index = state.Snapshots.BinarySearch(new Best5Snapshot(tradeTs), SnapshotTimestampComparer.Instance);

                    if (index >= 0)
                    {
                        pre = state.Snapshots[index];
                        post = state.Snapshots[index];
                        preDelta = TimeSpan.Zero;
                        postDelta = TimeSpan.Zero;
                    }
                    else
                    {
                        int nextIndex = ~index;

                        if (nextIndex > 0)
                        {
                            var candidate = state.Snapshots[nextIndex - 1];
                            var delta = tradeTs - candidate.ArrivalTs;
                            if (delta <= tail)
                            {
                                pre = candidate;
                                preDelta = delta;
                            }
                        }

                        if (nextIndex < state.Snapshots.Count)
                        {
                            var candidate = state.Snapshots[nextIndex];
                            var delta = candidate.ArrivalTs - tradeTs;
                            if (delta <= tail)
                            {
                                post = candidate;
                                postDelta = delta;
                            }
                        }
                    }

                    lastSnapshot = state.LastSnapshot;
                }
                else
                {
                    lastSnapshot = state.LastSnapshot;
                }
            }

            if (isHistoryData)
                return JoinMatchKind.None;

            if (pre != null || post != null)
            {
                if (pre is null && post.HasValue) pre = post;
                if (post is null && pre.HasValue) post = pre;

                joined = CreateJoined(trade, pre!.Value, post!.Value);

                var nearest = preDelta <= postDelta ? preDelta : postDelta;
                deltaMilliseconds = nearest.TotalMilliseconds;
                var kind = nearest <= core ? JoinMatchKind.Core : JoinMatchKind.Tail;

                RecordMetrics(trade.Symbol, state, kind, deltaMilliseconds);
                return kind;
            }

            if (lastSnapshot.HasValue)
            {
                var age = tradeTs - lastSnapshot.Value.ArrivalTs;
                if (age >= TimeSpan.Zero && age <= maxSnapshotAge)
                {
                    joined = CreateJoined(trade, lastSnapshot.Value, lastSnapshot.Value);
                    deltaMilliseconds = age.TotalMilliseconds;
                    RecordMetrics(trade.Symbol, state, JoinMatchKind.SnapshotFallback, deltaMilliseconds);
                    return JoinMatchKind.SnapshotFallback;
                }
            }

            if (trade.BidAtTrade > 0 && trade.AskAtTrade > 0)
            {
                var fallbackSnapshot = new Best5Snapshot(
                    trade.ArrivalTs,
                    trade.BidAtTrade,
                    trade.AskAtTrade,
                    Array.Empty<int>(),
                    Array.Empty<int>());

                joined = CreateJoined(trade, fallbackSnapshot, fallbackSnapshot);
                deltaMilliseconds = 0;
                RecordMetrics(trade.Symbol, state, JoinMatchKind.SnapshotFallback, 0);
                return JoinMatchKind.SnapshotFallback;
            }

            RecordMetrics(trade.Symbol, state, JoinMatchKind.None, double.NaN);
            return JoinMatchKind.None;
        }

        public TimeSpan GetLookaheadBuffer(string symbol)
        {
            if (_states.TryGetValue(symbol, out var state))
                return TimeSpan.FromMilliseconds(state.LookaheadMs);

            return TimeSpan.FromMilliseconds(_options.CurrentValue.Join.lookahead_buffer_ms);
        }

        private static JoinedTradeQuote CreateJoined(TradeTickEvent trade, Best5Snapshot pre, Best5Snapshot post)
        {
            var preBidDepth = CloneDepth(pre.BidDepth);
            var preAskDepth = CloneDepth(pre.AskDepth);
            var postBidDepth = CloneDepth(post.BidDepth);
            var postAskDepth = CloneDepth(post.AskDepth);

            return new JoinedTradeQuote(
                trade.Symbol,
                trade.ExchangeTs,
                trade.ArrivalTs,
                trade.Price,
                trade.Volume,
                pre.BestBid,
                pre.BidDepth.Length > 0 ? pre.BidDepth[0] : 0,
                preBidDepth,
                pre.BestAsk,
                pre.AskDepth.Length > 0 ? pre.AskDepth[0] : 0,
                preAskDepth,
                post.BestBid,
                post.BidDepth.Length > 0 ? post.BidDepth[0] : 0,
                postBidDepth,
                post.BestAsk,
                post.AskDepth.Length > 0 ? post.AskDepth[0] : 0,
                postAskDepth);
        }

        private static int[] CloneDepth(int[] source)
        {
            var dest = new int[5];
            if (source is null || source.Length == 0) return dest;
            var len = Math.Min(5, source.Length);
            Array.Copy(source, dest, len);
            return dest;
        }

        private void RecordMetrics(string symbol, SymbolState? state, JoinMatchKind kind, double deltaMs)
        {
            var opts = _options.CurrentValue;
            if (opts.logging_interval_sec <= 0) return;

            var metrics = _metrics.GetOrAdd(symbol, _ => new JoinMetrics(_bus, _clock));
            metrics.Record(symbol, kind, deltaMs, TimeSpan.FromSeconds(opts.logging_interval_sec), state, opts.Join);
        }

        private static void TrimSnapshots(List<Best5Snapshot> snapshots, ArrivalTs cutoff)
        {
            if (snapshots.Count == 0) return;

            var removeCount = 0;
            for (int i = 0; i < snapshots.Count; i++)
            {
                if (snapshots[i].ArrivalTs < cutoff)
                    removeCount++;
                else
                    break;
            }

            if (removeCount > 0)
            {
                snapshots.RemoveRange(0, Math.Min(removeCount, snapshots.Count));
            }
        }

        private sealed class SnapshotTimestampComparer : IComparer<Best5Snapshot>
        {
            public static readonly SnapshotTimestampComparer Instance = new();

            public int Compare(Best5Snapshot x, Best5Snapshot y)
            {
                return x.ArrivalTs.CompareTo(y.ArrivalTs);
            }
        }

        private sealed class SymbolState
        {
            public List<Best5Snapshot> Snapshots { get; } = new();
            public Best5Snapshot? LastSnapshot;
            public object Sync { get; } = new();
            public double CoreWindowMs { get; private set; }
            public double TailWindowMs { get; private set; }
            public double LookaheadMs { get; private set; }

            public SymbolState(JoinWindowOptions opt)
            {
                CoreWindowMs = opt.core_window_ms;
                TailWindowMs = opt.tail_window_ms;
                LookaheadMs = opt.lookahead_buffer_ms;
            }

            public (double coreMs, double tailMs) GetWindows(JoinWindowOptions opt)
            {
                if (!opt.adaptive_enabled)
                {
                    CoreWindowMs = opt.core_window_ms;
                    TailWindowMs = opt.tail_window_ms;
                    LookaheadMs = opt.lookahead_buffer_ms;
                }

                return (CoreWindowMs, TailWindowMs);
            }

            public void Adapt(JoinWindowOptions opt, double? p50, double? p95, double orphanRatio)
            {
                if (!opt.adaptive_enabled) return;

                if (p50.HasValue)
                {
                    var target = p50.Value + opt.core_offset_ms;
                    CoreWindowMs = Math.Max(opt.core_min_ms, Math.Min(opt.core_max_ms, target));
                    var lookaheadTarget = Math.Max(opt.lookahead_min_ms, Math.Min(opt.lookahead_max_ms, p50.Value * 0.25));
                    LookaheadMs = lookaheadTarget;
                }

                if (p95.HasValue)
                {
                    var targetTail = p95.Value + opt.tail_offset_ms;
                    TailWindowMs = Math.Max(opt.tail_min_ms, Math.Min(opt.tail_max_ms, targetTail));
                }

                if (orphanRatio >= opt.orphan_ratio_threshold_pct)
                {
                    TailWindowMs = Math.Min(opt.tail_max_ms, TailWindowMs + opt.orphan_adjust_ms);
                    LookaheadMs = Math.Min(opt.lookahead_max_ms, LookaheadMs + opt.orphan_adjust_ms);
                }
                else
                {
                    LookaheadMs = Math.Max(opt.lookahead_min_ms, Math.Min(opt.lookahead_max_ms, LookaheadMs));
                }
            }
        }

        private readonly struct Best5Snapshot
        {
            public Best5Snapshot(ArrivalTs arrivalTs) : this()
            {
                ArrivalTs = arrivalTs;
                BidDepth = Array.Empty<int>();
                AskDepth = Array.Empty<int>();
            }

            public Best5Snapshot(ArrivalTs arrivalTs, decimal bestBid, decimal bestAsk, int[] bidDepth, int[] askDepth)
            {
                ArrivalTs = arrivalTs;
                BestBid = bestBid;
                BestAsk = bestAsk;
                BidDepth = bidDepth ?? Array.Empty<int>();
                AskDepth = askDepth ?? Array.Empty<int>();
            }

            public ArrivalTs ArrivalTs { get; }
            public decimal BestBid { get; }
            public decimal BestAsk { get; }
            public int[] BidDepth { get; }
            public int[] AskDepth { get; }

            public static Best5Snapshot From(Best5DeltaEvent e)
            {
                return new Best5Snapshot(
                    e.ArrivalTs,
                    e.BestBid,
                    e.BestAsk,
                    CloneDepth(e.BidDepth),
                    CloneDepth(e.AskDepth));
            }
        }

        private sealed class JoinMetrics
        {
            private readonly IEventBus _bus;
            private readonly IClock _clock;
            private int _core;
            private int _tail;
            private int _snapshot;
            private int _orphans;
            private readonly List<double> _deltas = new();
            private DateTime _lastLog;

            public JoinMetrics(IEventBus bus, IClock clock)
            {
                _bus = bus;
                _clock = clock;
                _lastLog = _clock.Now;
            }

            public void Record(string symbol, JoinMatchKind kind, double deltaMs, TimeSpan interval, SymbolState? state, JoinWindowOptions joinOpt)
            {
                lock (_deltas)
                {
                    switch (kind)
                    {
                        case JoinMatchKind.Core: _core++; break;
                        case JoinMatchKind.Tail: _tail++; break;
                        case JoinMatchKind.SnapshotFallback: _snapshot++; break;
                        default: _orphans++; break;
                    }

                    if (!double.IsNaN(deltaMs) && deltaMs >= 0)
                    {
                        _deltas.Add(deltaMs);
                        if (_deltas.Count > 1024) _deltas.RemoveRange(0, _deltas.Count - 1024);
                    }

                    var now = _clock.Now;
                    if ((now - _lastLog) >= interval)
                    {
                        Flush(symbol, state, joinOpt);
                        _lastLog = now;
                    }
                }
            }

            private void Flush(string symbol, SymbolState? state, JoinWindowOptions joinOpt)
            {
                var deltas = _deltas.Count > 0 ? _deltas.OrderBy(x => x).ToArray() : Array.Empty<double>();
                var desc = Describe(deltas);
                var total = _core + _tail + _snapshot + _orphans;

                if (state != null && joinOpt.adaptive_enabled && desc.hasSamples)
                {
                    var orphanRatio = total > 0 ? (_orphans / (double)total) * 100.0 : 0;
                    state.Adapt(joinOpt, desc.p50, desc.p95, orphanRatio);

                    _bus.Publish(new JoinerHealthEvent(
                        symbol,
                        new ArrivalTs(_clock.Now),
                        orphanRatio / 100.0,
                        desc.p95.GetValueOrDefault())
                    {
                        OccurredAt = new DateTimeOffset(_clock.Now)
                    });
                }

                _deltas.Clear();
                _core = _tail = _snapshot = _orphans = 0;
            }

            private static (bool hasSamples, double? p50, double? p95) Describe(double[] sorted)
            {
                if (sorted.Length == 0)
                    return (false, null, null);

                return (true, Percentile(sorted, 0.5), Percentile(sorted, 0.95));
            }

            private static double Percentile(double[] sorted, double percentile)
            {
                if (sorted.Length == 0) return double.NaN;

                var idx = percentile * (sorted.Length - 1);
                var lower = (int)Math.Floor(idx);
                var upper = (int)Math.Ceiling(idx);
                if (lower == upper) return sorted[lower];

                var frac = idx - lower;
                return sorted[lower] + (sorted[upper] - sorted[lower]) * frac;
            }
        }
    }
}

