using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Events.OrderBook.Iceberg;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Time;
using MuNTrade.Core.Settings;

namespace MuNTrade.Core.OrderBook
{
    public enum IcebergUiPhase { None = 0, Suspect, Confirmed, Decaying }
    public enum IcebergTier { Weak = 0, Medium = 1, Strong = 2 }

    public enum IcebergClosedReason
    {
        None = 0,
        Breached,           // 被破：方向穿價
        Retreated,          // 撤退：無法以成交解釋的消失
        Decayed,            // 自然衰退 TTL 到
        ClearedOpposite,    // 對向同價出現，舊邊被清場（靜默）
        Disappeared         // 書上瞬間歸零/離書但無可解釋成交（供復活白名單使用）
    }

    public sealed class IcebergTrackerState
    {
        public required string Symbol { get; init; }
        public required bool IsBid { get; init; }
        public required decimal Price { get; init; }

        // Volatile for thread-safe reads from UI pull
        public volatile IcebergUiPhase UiPhase;
        public volatile IcebergTier Tier;

        public ArrivalTs FirstConfirmed { get; set; }
        public ArrivalTs LastTouched { get; set; }
        public ArrivalTs LastSeenInBook { get; set; }
        public bool IsVisibleTopN { get; set; }

        // Optimized sliding windows
        public SlidingTradesWindow TradesNearP { get; } = new();
        public SlidingBookWindow BookAtP { get; } = new();

        public ArrivalTs LastDefended;
        public ArrivalTs LastBreached;
        public ArrivalTs LastRetreated;
        public ArrivalTs LastEscalated;
        public ArrivalTs LastMigrated;

        public bool PersistL1Done, PersistL2Done, PersistL3Done;

        // Migration candidates
        public ArrivalTs MigrateCandidateSince;

        // [Resurrection Fields]
        public long EpochId;                        // 同 (sym, side, px) 的生命週期編號
        public ArrivalTs LastTerminalAt;            // 最後一次終止（或被標記終止）時間
        public IcebergClosedReason ClosedReason;    // 終止原因（供抑制 Decayed / 復活判斷）

        // 近窗累計（供復活按比例承接，與 UI/Story 統計用）
        public int MatchedVolumeAccumulated;        // 累積被吸收量（近窗加權或 EMA）
        public int ReplenishAccumulated;            // 累積回補量（近窗加權或 EMA）
    }

    // Optimized: Use Queue and running sums to avoid LINQ
    public class SlidingTradesWindow
    {
        private struct TradeItem
        {
            public long TimeTicks;
            public decimal Price;
            public int Vol;
            public bool AggressorIsSell;
        }

        private readonly Queue<TradeItem> _buffer = new(256);

        // Caches for quick lookup? No, specific query filters by price/side.
        // But we can clean up efficiently.

        public void Add(ArrivalTs time, decimal price, int vol, bool aggressorIsSell)
        {
            _buffer.Enqueue(new TradeItem
            {
                TimeTicks = time.Value.Ticks,
                Price = price,
                Vol = vol,
                AggressorIsSell = aggressorIsSell
            });
        }

        public void Prune(ArrivalTs now, double windowSec)
        {
            long cutoff = now.Value.AddSeconds(-windowSec).Ticks;
            while (_buffer.Count > 0 && _buffer.Peek().TimeTicks < cutoff)
            {
                _buffer.Dequeue();
            }
        }

        public int GetAggressorVolume(bool isSell, decimal minPrice, decimal maxPrice)
        {
            int sum = 0;
            foreach (var item in _buffer)
            {
                if (item.AggressorIsSell == isSell && item.Price >= minPrice && item.Price <= maxPrice)
                {
                    sum += item.Vol;
                }
            }
            return sum;
        }

        // Sum of all aggressor volume against the iceberg
        public int GetExplainedVolume(bool isBidIceberg)
        {
            bool targetAggressorIsSell = isBidIceberg;
            int sum = 0;
            foreach (var item in _buffer)
            {
                if (item.AggressorIsSell == targetAggressorIsSell)
                {
                    sum += item.Vol;
                }
            }
            return sum;
        }
    }

    public class SlidingBookWindow
    {
        private struct BookItem
        {
            public long TimeTicks;
            public int Qty;
        }

        private readonly Queue<BookItem> _buffer = new(128);

        public void Add(ArrivalTs time, int qty)
        {
            _buffer.Enqueue(new BookItem { TimeTicks = time.Value.Ticks, Qty = qty });
        }

        public void Prune(ArrivalTs now, double windowSec)
        {
            long cutoff = now.Value.AddSeconds(-windowSec).Ticks;
            while (_buffer.Count > 0 && _buffer.Peek().TimeTicks < cutoff)
            {
                _buffer.Dequeue();
            }
        }

        // Returns: QtyBefore, QtyAfter, Replenish, Deplete
        public (int QtyBefore, int QtyAfter, int Replenish, int Deplete) GetChanges()
        {
            if (_buffer.Count < 2)
            {
                int q = _buffer.Count > 0 ? _buffer.Peek().Qty : 0;
                return (q, q, 0, 0);
            }

            int replenish = 0;
            int deplete = 0;
            int prevQty = -1;
            int firstQty = -1;
            int lastQty = 0;

            foreach (var item in _buffer)
            {
                if (prevQty == -1)
                {
                    prevQty = item.Qty;
                    firstQty = item.Qty;
                    continue;
                }

                int delta = item.Qty - prevQty;
                if (delta > 0) replenish += delta;
                else if (delta < 0) deplete += (-delta);

                prevQty = item.Qty;
                lastQty = item.Qty;
            }

            return (firstQty, lastQty, replenish, deplete);
        }

        public int StartQty => _buffer.Count > 0 ? _buffer.Peek().Qty : 0;
        public int EndQty => _buffer.Count > 0 ? _buffer.Last().Qty : 0; // O(N) for Queue.Last(), but Queue is small. Could opt if needed.
    }

    public interface IIcebergTracker
    {
        void OnIcebergSuspect(IcebergSuspectEvent e);
        void OnIcebergConfirmed(IcebergConfirmedEvent e);
        void OnIcebergCleared(IcebergClearedEvent e);
        void OnBest5Snapshot(string symbol, Best5DeltaEvent snap, ArrivalTs arrival, bool isStale, decimal tickSize);
        void OnTrade(string symbol, TradeTickEvent t, decimal tickSize);
        bool TryGetUiBadge(string symbol, bool isBid, decimal price, out (IcebergUiPhase phase, IcebergTier tier) badge);
        // Batch API
        int TryGetUiBadges(string symbol, IReadOnlyList<decimal> prices, bool isBid, (IcebergUiPhase phase, IcebergTier tier)[] results);
    }

    public class IcebergTracker : IIcebergTracker
    {
        private readonly IEventBus _bus;
        private readonly ILogger _logger;
        private readonly IcebergTrackerOptions _opts;

        // Optimized lookup by Symbol+Side+Price
        // Key: (Symbol, IsBid, Price)
        private readonly ConcurrentDictionary<(string, bool, decimal), IcebergTrackerState> _states = new();

        // Indexes for fast TopN lookup.
        // Key: Symbol -> Price -> State
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<decimal, IcebergTrackerState>> _bidIndex = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<decimal, IcebergTrackerState>> _askIndex = new();

        // [Resurrection Caches]
        // 最近終止（Terminal）快取：供同側同價短時間復活的白名單判斷
        private sealed record ClosedRecord(
            long EpochId, ArrivalTs At, IcebergClosedReason Reason,
            int MatchedAccum, int ReplenishAccum);

        private readonly ConcurrentDictionary<(string sym, bool isBid, decimal px), ClosedRecord> _recentlyClosed
            = new();

        // 終止後的價位冷卻（同價任何邊都要靜一靜，抑制殘響）
        private readonly ConcurrentDictionary<(string sym, decimal px), ArrivalTs> _priceCooldown = new();

        // 事件節流（避免短時間重複噴發）
        private readonly ConcurrentDictionary<(string sym, bool isBid, decimal px, long epoch, string type), ArrivalTs> _eventThrottle = new();
        private readonly ConcurrentDictionary<string, TimeSpan> _throttleOverrides = new(StringComparer.OrdinalIgnoreCase);

        private long _epochCounter = 0;

        public IcebergTracker(IEventBus bus, ILogger logger, IcebergTrackerOptions options)
        {
            _bus = bus;
            _logger = logger;
            _opts = options ?? new IcebergTrackerOptions();
        }

        public void OverrideEventThrottle(string symbol, TimeSpan throttle)
        {
            _throttleOverrides[symbol] = throttle;
        }

        private IcebergTrackerState GetOrAddState(string symbol, bool isBid, decimal price, ArrivalTs now)
        {
            var key = (symbol, isBid, price);
            if (_states.TryGetValue(key, out var state)) return state;

            var newState = new IcebergTrackerState
            {
                Symbol = symbol,
                IsBid = isBid,
                Price = price,
                EpochId = Interlocked.Increment(ref _epochCounter),
                UiPhase = IcebergUiPhase.Suspect,
                FirstConfirmed = now,
                LastTouched = now,
                Tier = IcebergTier.Weak,
                IsVisibleTopN = true,
                LastSeenInBook = now
            };

            if (_states.TryAdd(key, newState))
            {
                AddToIndex(newState);
                return newState;
            }
            return _states[key];
        }

        private void AddToIndex(IcebergTrackerState state)
        {
            var index = state.IsBid ? _bidIndex : _askIndex;
            var symDict = index.GetOrAdd(state.Symbol, _ => new ConcurrentDictionary<decimal, IcebergTrackerState>());
            symDict[state.Price] = state;
        }

        private void RemoveState(string symbol, bool isBid, decimal price)
        {
            if (_states.TryRemove((symbol, isBid, price), out var state))
            {
                var index = isBid ? _bidIndex : _askIndex;
                if (index.TryGetValue(symbol, out var symDict))
                {
                    symDict.TryRemove(price, out _);
                }
            }
        }

        private bool ShouldThrottle(string type, IcebergTrackerState state, ArrivalTs ts)
        {
            var key = (state.Symbol, state.IsBid, state.Price, state.EpochId, type);
            double limit = _opts.event_throttle_sec;
            if (_throttleOverrides.TryGetValue(state.Symbol, out var ov))
            {
                limit = ov.TotalSeconds;
            }

            if (_eventThrottle.TryGetValue(key, out var last))
            {
                if ((ts - last).TotalSeconds < limit) return true;
            }
            _eventThrottle[key] = ts;
            return false;
        }

        public void OnIcebergSuspect(IcebergSuspectEvent e)
        {
            var state = GetOrAddState(e.Symbol, e.IsBid, e.Price, e.ArrivalTimestamp);
            // Suspect keeps it alive
            state.LastTouched = e.ArrivalTimestamp;
        }

        public void OnIcebergConfirmed(IcebergConfirmedEvent e)
        {
            var state = GetOrAddState(e.Symbol, e.IsBid, e.Price, e.ArrivalTimestamp);
            state.UiPhase = IcebergUiPhase.Confirmed;
            state.LastTouched = e.ArrivalTimestamp;

            if (e.Confidence > 0.8) state.Tier = IcebergTier.Strong;
            else if (e.Confidence > 0.5) state.Tier = IcebergTier.Medium;
            else state.Tier = IcebergTier.Weak;
        }

        public void OnIcebergCleared(IcebergClearedEvent e)
        {
            // [Refinement] Register to _recentlyClosed to prevent cross-side residual issues
            // and ensure closed loop. Reason = ClearedOpposite (or effectively cleared).
            // Using ClearedOpposite ensures Resurrection logic (which requires Disappeared)
            // will NOT resurrect this.
            if (_states.TryGetValue((e.Symbol, e.IsBid, e.Price), out var state))
            {
                state.ClosedReason = IcebergClosedReason.ClearedOpposite;
                state.LastTerminalAt = e.ArrivalTimestamp;
                _recentlyClosed[(state.Symbol, state.IsBid, state.Price)] =
                    new ClosedRecord(state.EpochId, e.ArrivalTimestamp, IcebergClosedReason.ClearedOpposite,
                                     state.MatchedVolumeAccumulated, state.ReplenishAccumulated);
            }
            RemoveState(e.Symbol, e.IsBid, e.Price);
        }

        public void OnBest5Snapshot(string symbol, Best5DeltaEvent snap, ArrivalTs arrival, bool isStale, decimal tickSize)
        {
            if (isStale) return;

            // Refresh visible levels here; trade events also advance states outside the visible levels.

            // Process Bids
            ProcessSide(symbol, snap.Bids, true, arrival, snap, tickSize);

            // Process Asks
            ProcessSide(symbol, snap.Asks, false, arrival, snap, tickSize);

            // [Missing Scan] 缺席掃描：同側同價位「上一輪可見，這一輪不見」→ 終止
            var seen = new HashSet<(bool isBid, decimal px)>();
            foreach (var lv in snap.Bids) seen.Add((true, lv.Price));
            foreach (var lv in snap.Asks) seen.Add((false, lv.Price));

            // 僅掃描本 symbol 的 state；避免全表 O(N)
            // Iterate visible TopN driven states or comprehensive scan?
            // Since we use indexes, we can iterate index for symbol.
            if (_bidIndex.TryGetValue(symbol, out var bidDict)) ProcessMissingInDict(bidDict, true);
            if (_askIndex.TryGetValue(symbol, out var askDict)) ProcessMissingInDict(askDict, false);

            void ProcessMissingInDict(ConcurrentDictionary<decimal, IcebergTrackerState> dict, bool isBid)
            {
                foreach (var state in dict.Values)
                {
                    var isVisibleNow = seen.Contains((isBid, state.Price));

                    // 只處理「上一輪曾可見且已確認或懷疑中的活體」→ 現在不見了
                    if (!isVisibleNow && (state.UiPhase == IcebergUiPhase.Suspect || state.UiPhase == IcebergUiPhase.Confirmed))
                    {
                        ProcessMissingState(state, arrival);
                    }
                }
            }

            // Cleanup stale states (Suspects that never confirmed, or Confirmed that disappeared)
            PruneStaleStates(symbol, arrival);
        }

        private void ProcessMissingState(IcebergTrackerState state, ArrivalTs now)
        {
            // 估算近窗可被成交解釋的量
            var explained = state.TradesNearP.GetExplainedVolume(state.IsBid); // Using default method defined in SlidingTradesWindow
                                                                               // Note: GetExplainedVolume inside checks buffer.

            var reason = explained >= _opts.breach_min_aggr_vol
                ? IcebergClosedReason.Breached
                : IcebergClosedReason.Disappeared; // 白名單：可允許復活

            state.ClosedReason = reason;
            state.LastTerminalAt = now;
            state.UiPhase = IcebergUiPhase.Decaying; // 切入衰退

            // 記下最近終止紀錄（供復活判斷）
            _recentlyClosed[(state.Symbol, state.IsBid, state.Price)] =
                new ClosedRecord(state.EpochId, now, reason, state.MatchedVolumeAccumulated, state.ReplenishAccumulated);

            // 價位冷卻（同價雙邊都冷卻，抑制殘響）
            _priceCooldown[(state.Symbol, state.Price)] = now;

            // 立即移除狀態，避免沿用
            RemoveState(state.Symbol, state.IsBid, state.Price);
        }

        // Suspected states are removed after inactivity; no decay phase is emitted.


        private void PruneStaleStates(string symbol, ArrivalTs now)
        {
            if (_bidIndex.TryGetValue(symbol, out var bidDict)) PruneDict(bidDict);
            if (_askIndex.TryGetValue(symbol, out var askDict)) PruneDict(askDict);

            void PruneDict(ConcurrentDictionary<decimal, IcebergTrackerState> dict)
            {
                // Iterate a snapshot of values to avoid modification issues during enumeration if strictly required,
                // though ConcurrentDictionary supports concurrent mod.
                foreach (var state in dict.Values)
                {
                    bool remove = false;
                    double secSinceTouch = (now - state.LastTouched).TotalSeconds;
                    double secSinceSeen = (now - state.LastSeenInBook).TotalSeconds;

                    switch (state.UiPhase)
                    {
                        case IcebergUiPhase.Suspect:
                            // Suspect: Tighter TTL (e.g. 10s or 2x touch window)
                            if (secSinceTouch > 10.0)
                            {
                                // Move suspected states to Decaying without publishing Decayed.
                                state.UiPhase = IcebergUiPhase.Decaying;
                                // Mark with a reason that suppresses "Decayed" event in CheckDecayed/Prune
                                state.ClosedReason = IcebergClosedReason.Decayed;
                                // Retain the state until the decay TTL expires.
                            }
                            break;

                        case IcebergUiPhase.Decaying:
                            // Decaying: TTL from Options
                            if (secSinceTouch > _opts.decay_ttl_sec) remove = true;
                            break;

                        case IcebergUiPhase.Confirmed:
                            // Confirmed: If it disappears from book for too long (e.g. 30s), assume cancelled/filled cleanly
                            // without breach/retreat detection.
                            if (secSinceSeen > 30.0) remove = true;
                            break;
                    }

                    if (remove)
                    {
                        // Fire Cleared event? Or Decayed?
                        // If it was Confirmed, maybe Decayed is better for UI log.
                        // If Suspect, just silent remove.
                        if (state.UiPhase == IcebergUiPhase.Confirmed || state.UiPhase == IcebergUiPhase.Decaying)
                        {
                            bool explicitDeath = state.LastBreached != default || state.LastRetreated != default || state.ClosedReason != IcebergClosedReason.None;
                            if (!explicitDeath)
                            {
                                _bus.Publish(new IcebergDecayedEvent(state.Symbol, state.Price, state.IsBid, now)
                                {
                                    OccurredAt = new DateTimeOffset(now.Value)
                                });
                            }
                        }
                        RemoveState(state.Symbol, state.IsBid, state.Price);
                    }
                }
            }
        }

        private void ProcessSide(string symbol, IReadOnlyList<MuNTrade.Abstractions.Dtos.Trading.PriceLevel> levels, bool isBid, ArrivalTs now, Best5DeltaEvent snap, decimal tickSize)
        {
            var index = isBid ? _bidIndex : _askIndex;
            if (!index.TryGetValue(symbol, out var symDict)) return;

            int count = Math.Min(levels.Count, _opts.visible_topN_limit);
            for (int i = 0; i < count; i++)
            {
                var lvl = levels[i];
                if (symDict.TryGetValue(lvl.Price, out var state))
                {
                    ProcessSnapshotForState(state, lvl.Volume, now, snap, tickSize);
                }
                else
                {
                    // [Resurrection / New Epoch Check]
                    // Reappearance handling（在嘗試 new 狀態之前）
                    var k = (symbol, isBid, lvl.Price);
                    if (_recentlyClosed.TryGetValue(k, out var closed))
                    {
                        var allowResurrect =
                            _opts.resurrect_enabled &&
                            closed.Reason == IcebergClosedReason.Disappeared &&                // 只允許 Disappeared 復活
                            (now - closed.At).TotalSeconds <= _opts.resurrect_window_sec &&
                            (!_priceCooldown.TryGetValue((k.symbol, k.Price), out var t0) ||   // 冷卻不應阻擋「同側」復活
                            (now - t0).TotalSeconds <= _opts.resurrect_window_sec);

                        // Note: Opposing side check should typically be handled by detector/cleared logic.

                        IcebergTrackerState ns;
                        if (allowResurrect)
                        {
                            // 復活：部分承接
                            ns = new IcebergTrackerState
                            {
                                Symbol = k.symbol,
                                IsBid = k.isBid,
                                Price = k.Price,
                                EpochId = closed.EpochId + 1,
                                UiPhase = IcebergUiPhase.Suspect,
                                LastTouched = now,
                                FirstConfirmed = now, // Reset timings or keep? Default to now for simplicity
                                LastSeenInBook = now,
                                Tier = IcebergTier.Weak,
                                IsVisibleTopN = true,
                                ClosedReason = IcebergClosedReason.None,
                                MatchedVolumeAccumulated = (int)(closed.MatchedAccum * _opts.resurrect_max_carry_ratio),
                                ReplenishAccumulated = (int)(closed.ReplenishAccum * _opts.resurrect_max_carry_ratio)
                            };
                            // （可選）Bus Publish Resurrected?
                        }
                        else
                        {
                            // 新建：乾淨開始
                            ns = new IcebergTrackerState
                            {
                                Symbol = k.symbol,
                                IsBid = k.isBid,
                                Price = k.Price,
                                EpochId = closed.EpochId + 1,
                                UiPhase = IcebergUiPhase.Suspect,
                                LastTouched = now,
                                FirstConfirmed = now,
                                LastSeenInBook = now,
                                Tier = IcebergTier.Weak,
                                IsVisibleTopN = true,
                                ClosedReason = IcebergClosedReason.None,
                                MatchedVolumeAccumulated = 0,
                                ReplenishAccumulated = 0
                            };
                            // Bus Publish New Epoch?
                        }

                        if (_states.TryAdd((k.symbol, k.isBid, k.Price), ns))
                        {
                            AddToIndex(ns);
                            // Process update immediately
                            ProcessSnapshotForState(ns, lvl.Volume, now, snap, tickSize);
                        }

                        // Remove from closed cache
                        _recentlyClosed.TryRemove(k, out _);
                    }
                }
            }
        }

        private void ProcessSnapshotForState(IcebergTrackerState state, int volume, ArrivalTs now, Best5DeltaEvent snap, decimal tickSize)
        {
            state.IsVisibleTopN = true; // Confirmed visible
            state.LastSeenInBook = now;
            state.BookAtP.Add(now, volume);

            state.BookAtP.Prune(now, _opts.touch_window_sec);
            state.TradesNearP.Prune(now, _opts.touch_window_sec);

            // 1. Check Retreated
            CheckRetreated(state, now);

            // 2. Check Migrated
            CheckMigrated(state, now, snap, tickSize);

            // 3. Check Persist
            CheckPersist(state, now);

            // 4. Check Decayed
            CheckDecayed(state, now);

            // 5. Rebound Logic
            CheckRebound(state, now, snap, tickSize);
        }

        private void CheckRebound(IcebergTrackerState state, ArrivalTs now, Best5DeltaEvent snap, decimal tickSize)
        {
            if (state.UiPhase != IcebergUiPhase.Confirmed) return;

            // Rebound condition: Price >= P + n * tick
            decimal bestPrice = state.IsBid
                ? (snap.Bids.Count > 0 ? snap.Bids[0].Price : 0)
                : (snap.Asks.Count > 0 ? snap.Asks[0].Price : 0);

            if (bestPrice == 0) return;

            decimal diff = state.IsBid ? (bestPrice - state.Price) : (state.Price - bestPrice);
            decimal threshold = _opts.rebound_min_ticks * tickSize;

            if (diff >= threshold)
            {
                // Must have been touched recently
                if ((now - state.LastTouched).TotalSeconds < _opts.rebound_window_sec)
                {
                    // Emit Defended as "Rebound"
                    if ((now - state.LastDefended).TotalSeconds >= _opts.defend_debounce_sec)
                    {
                        // Calculate bounce ticks
                        int ticks = (int)(diff / (tickSize > 0 ? tickSize : 1));

                        if (ShouldThrottle(nameof(IcebergDefendedEvent), state, now)) return;
                        _bus.Publish(new IcebergDefendedEvent(state.Symbol, state.Price, state.IsBid, now, ticks, 0)
                        {
                            OccurredAt = new DateTimeOffset(now.Value)
                        });
                        state.LastDefended = now;
                    }
                }
            }
        }

        public void OnTrade(string symbol, TradeTickEvent t)
        {
            OnTrade(symbol, t, _opts.tick_size);
        }

        public void OnTrade(string symbol, TradeTickEvent t, decimal tickSize)
        {
            // Process Direct Hits (Exact Price)
            if (t.Side == AggressorSide.Sell && _bidIndex.TryGetValue(symbol, out var bidDict))
            {
                if (bidDict.TryGetValue(t.Price, out var state)) ProcessTradeForState(state, t, tickSize);
            }
            else if (t.Side == AggressorSide.Buy && _askIndex.TryGetValue(symbol, out var askDict))
            {
                if (askDict.TryGetValue(t.Price, out var state)) ProcessTradeForState(state, t, tickSize);
            }

            // Process Breaches (Trade Through)
            // If Sell Aggressor @ P, it might breach Bids > P (if gap) or Bids == P.
            // But usually we just check if trade P is "worse" than iceberg P.
            // Bid Iceberg P_ice. Trade P_trade.
            // If P_trade < P_ice, then P_ice was breached/traded through.

            bool isSell = t.Side == AggressorSide.Sell;
            var index = isSell ? _bidIndex : _askIndex;

            if (index.TryGetValue(symbol, out var symDict))
            {
                // Iterate copy of values to allow modification (RemoveState) inside logic if needed,
                // or just to be safe. Though typically we just publish event.
                foreach (var state in symDict.Values)
                {
                    // Skip if already decaying
                    if (state.UiPhase == IcebergUiPhase.Decaying) continue;

                    bool breached = isSell
                        ? (t.Price < state.Price) // Sell Trade below Bid Iceberg
                        : (t.Price > state.Price); // Buy Trade above Ask Iceberg

                    if (breached)
                    {
                        ProcessTradeForBreach(state, t);
                    }
                }
            }
        }

        private void ProcessTradeForBreach(IcebergTrackerState state, TradeTickEvent t)
        {
            if (state.UiPhase != IcebergUiPhase.Confirmed) return;

            // [Guard] Ask 冰山：只能成交價 > P 才是被破；Bid 冰山：只能成交價 < P 才是被破
            if (state.IsBid)
            {
                if (t.Price >= state.Price) return;
            }
            else
            {
                if (t.Price <= state.Price) return;
            }

            // Trade-through events can indicate a breach even when no trade was reported at the tracked price.

            state.LastTouched = t.ArrivalTs; // It was interacted with (destroyed)

            if ((t.ArrivalTs - state.LastBreached).TotalSeconds < _opts.breach_debounce_sec) return;

            // The trade price has moved beyond the tracked level.
            if (ShouldThrottle(nameof(IcebergBreachedEvent), state, t.ArrivalTs)) return;
            _bus.Publish(new IcebergBreachedEvent(state.Symbol, state.Price, state.IsBid, t.ArrivalTs, t.Volume)
            {
                OccurredAt = new DateTimeOffset(t.ArrivalTs.Value)
            });
            state.LastBreached = t.ArrivalTs;
            state.UiPhase = IcebergUiPhase.Decaying;
            state.ClosedReason = IcebergClosedReason.Breached;
            state.LastTerminalAt = t.ArrivalTs;
        }

        private void ProcessTradeForState(IcebergTrackerState state, TradeTickEvent t, decimal tickSize)
        {
            state.TradesNearP.Add(t.ArrivalTs, t.Price, t.Volume, t.Side == AggressorSide.Sell);
            state.TradesNearP.Prune(t.ArrivalTs, _opts.touch_window_sec);

            // 1. Defended
            // Exact touch logic
            decimal tick = tickSize;
            bool isTouch = state.IsBid
               ? (t.Price <= state.Price && t.Price >= state.Price - tick)
               : (t.Price >= state.Price && t.Price <= state.Price + tick);

            if (isTouch)
            {
                bool isAttack = state.IsBid ? t.Side == AggressorSide.Sell : t.Side == AggressorSide.Buy;
                if (isAttack) CheckDefended(state, t.ArrivalTs);
            }

            // 2. Breached (At Price)
            // Logic is covered by "Direct Hits" call to here combined with "ProcessBreach" loop above?
            // Actually, if P_trade == P_ice, it's not strictly "Breached" yet unless we see price drop below.
            // But "Direct Hits" does call this.
            // Original logic:
            // bool breached = state.IsBid ? (t.Price < state.Price) : (t.Price > state.Price);
            // If t.Price == state.Price, it's not breached yet.
            // If t.Price < state.Price, it IS breached.
            // But checks above handle "Strictly Less/Greater".
            // So we don't need to duplicate logic here unless we want to handle "Direct Hit but slightly past"?
            // We'll leave the generic loop to handle the "Strictly Past" case.
        }

        private void CheckDefended(IcebergTrackerState state, ArrivalTs now)
        {
            if (state.UiPhase != IcebergUiPhase.Confirmed) return;

            if ((now - state.LastDefended).TotalSeconds < _opts.defend_debounce_sec) return;

            // Aggressor Vol Check
            var aggrVol = state.TradesNearP.GetAggressorVolume(!state.IsBid, state.Price, state.Price);
            if (aggrVol < _opts.min_touch_aggr_vol) return;

            // Absorption is indicated by replenishment or a smaller-than-expected quantity drop.
            var (qtyBefore, qtyAfter, replenish, deplete) = state.BookAtP.GetChanges();
            int tradesExplained = state.TradesNearP.GetExplainedVolume(state.IsBid);

            // Logic:
            // 1. Explicit Replenish (someone added limit orders)
            // 2. Absorption: Quantity didn't drop as much as it should have.
            //    Expected Drop = TradesExplained + Deplete(cancels)
            //    Actual Drop = QtyBefore - QtyAfter
            //    If Actual < Expected, implies hidden refill? Or just replenish?
            //    Simple: QtyAfter >= QtyBefore - TradesExplained (ignoring cancels for conservative check)

            bool absorbed = replenish > 0 || (qtyAfter >= qtyBefore - tradesExplained);

            if (absorbed)
            {
                if (ShouldThrottle(nameof(IcebergDefendedEvent), state, now)) return;
                _bus.Publish(new IcebergDefendedEvent(state.Symbol, state.Price, state.IsBid, now, 0, aggrVol)
                {
                    OccurredAt = new DateTimeOffset(now.Value)
                });
                state.LastDefended = now;
                state.LastTouched = now;
            }
        }

        private void CheckBreached(IcebergTrackerState state, ArrivalTs now)
        {
            if (state.UiPhase != IcebergUiPhase.Confirmed) return;

            if ((now - state.LastBreached).TotalSeconds < _opts.breach_debounce_sec) return;

            var aggrVol = state.TradesNearP.GetExplainedVolume(state.IsBid);
            if (aggrVol < _opts.breach_min_aggr_vol) return;

            if (ShouldThrottle(nameof(IcebergBreachedEvent), state, now)) return;
            _bus.Publish(new IcebergBreachedEvent(state.Symbol, state.Price, state.IsBid, now, aggrVol)
            {
                OccurredAt = new DateTimeOffset(now.Value)
            });
            state.LastBreached = now;
            state.UiPhase = IcebergUiPhase.Decaying;
            // Cleanup schedule via Decayed check logic
        }

        private void CheckRetreated(IcebergTrackerState state, ArrivalTs now)
        {
            if (state.UiPhase != IcebergUiPhase.Confirmed) return;


            // Verify retreat using the drop not explained by aggressor trades.
            var (qtyBefore, qtyAfter, replenish, deplete) = state.BookAtP.GetChanges();

            // Note: deplete is positive (sum of negative deltas inverted).
            int tradesExplained = state.TradesNearP.GetExplainedVolume(state.IsBid);

            // Unexplained Drop: Total Drop (Deplete) - Trades.
            // If Deplete > Trades, the difference is cancels/modifies.
            // If UnexplainedDrop is large, it's a Retreat.
            long unexplainedDrop = (long)deplete - tradesExplained;

            if (unexplainedDrop >= _opts.retreat_min_abs_delta)
            {
                if ((now - state.LastRetreated).TotalSeconds < 5.0) return;

                if (ShouldThrottle(nameof(IcebergRetreatedEvent), state, now)) return;
                _bus.Publish(new IcebergRetreatedEvent(state.Symbol, state.Price, state.IsBid, now, (int)unexplainedDrop, tradesExplained)
                {
                    OccurredAt = new DateTimeOffset(now.Value)
                });
                state.LastRetreated = now;
                state.UiPhase = IcebergUiPhase.Decaying;
                state.ClosedReason = IcebergClosedReason.Retreated;
                state.LastTerminalAt = now;
            }
        }

        private void CheckPersist(IcebergTrackerState state, ArrivalTs now)
        {
            if (state.UiPhase != IcebergUiPhase.Confirmed) return;

            double duration = (now - state.FirstConfirmed).TotalSeconds;

            if (!state.PersistL1Done && duration >= _opts.persist_tiers_sec_L1)
            {
                if (CheckPersistCondition(state, now))
                {
                    if (!ShouldThrottle($"{nameof(IcebergPersistEvent)}_L1", state, now))
                    {
                        _bus.Publish(new IcebergPersistEvent(state.Symbol, state.Price, state.IsBid, now, 1, duration)
                        {
                            OccurredAt = new DateTimeOffset(now.Value)
                        });
                        state.PersistL1Done = true;
                    }
                }
            }
            if (!state.PersistL2Done && duration >= _opts.persist_tiers_sec_L2)
            {
                if (CheckPersistCondition(state, now))
                {
                    if (!ShouldThrottle($"{nameof(IcebergPersistEvent)}_L2", state, now))
                    {
                        _bus.Publish(new IcebergPersistEvent(state.Symbol, state.Price, state.IsBid, now, 2, duration)
                        {
                            OccurredAt = new DateTimeOffset(now.Value)
                        });
                        state.PersistL2Done = true;
                    }
                }
            }
            if (!state.PersistL3Done && duration >= _opts.persist_tiers_sec_L3)
            {
                if (CheckPersistCondition(state, now))
                {
                    if (!ShouldThrottle($"{nameof(IcebergPersistEvent)}_L3", state, now))
                    {
                        _bus.Publish(new IcebergPersistEvent(state.Symbol, state.Price, state.IsBid, now, 3, duration)
                        {
                            OccurredAt = new DateTimeOffset(now.Value)
                        });
                        state.PersistL3Done = true;
                    }
                }
            }
        }

        private bool CheckPersistCondition(IcebergTrackerState state, ArrivalTs now)
        {
            if (state.IsVisibleTopN) return true;
            if ((now - state.LastTouched).TotalSeconds < 10.0) return true; // Arbitrary 10s or window
            return false;
        }

        private void CheckMigrated(IcebergTrackerState state, ArrivalTs now, Best5DeltaEvent snap, decimal tickSize)
        {
            if (!_opts.migrated_enabled) return;
            if (state.UiPhase != IcebergUiPhase.Confirmed) return;

            if ((now - state.LastMigrated).TotalSeconds < _opts.migrate_window_sec) return;

            // Simplified detection:
            // 1. Original P Retreated (Unexplained Drop)
            var (qB, qA, repl, dep) = state.BookAtP.GetChanges();
            int trades = state.TradesNearP.GetExplainedVolume(state.IsBid);
            long unexplained = (long)dep - trades;

            if (unexplained < _opts.migrate_min_abs_delta) return;

            // 2. Check P+/-1
            decimal tick = tickSize;
            var neighbors = new[] { state.Price + tick, state.Price - tick };
            var bestLevels = state.IsBid ? snap.Bids : snap.Asks;

            foreach (var targetP in neighbors)
            {
                var lvl = bestLevels.FirstOrDefault(x => x.Price == targetP);
                if (lvl != null && lvl.Volume > _opts.migrate_min_abs_delta)
                {
                    // [Refinement C] Conservative Migration Check
                    // Require targetP to be Active (Suspect/Confirmed)
                    bool targetIsActive = false;
                    var index = state.IsBid ? _bidIndex : _askIndex;
                    if (index.TryGetValue(state.Symbol, out var symDict))
                    {
                        if (symDict.TryGetValue(targetP, out var targetState))
                        {
                            if (targetState.UiPhase == IcebergUiPhase.Suspect || targetState.UiPhase == IcebergUiPhase.Confirmed)
                            {
                                targetIsActive = true;
                            }
                        }
                    }

                    if (!targetIsActive)
                    {
                        return;
                    }

                    // Trigger Migrated
                    if (ShouldThrottle(nameof(IcebergMigratedEvent), state, now)) return;
                    _bus.Publish(new IcebergMigratedEvent(state.Symbol, state.Price, targetP, state.IsBid, now)
                    {
                        OccurredAt = new DateTimeOffset(now.Value)
                    });
                    state.LastMigrated = now;
                    return;
                }
            }
        }

        private void CheckDecayed(IcebergTrackerState state, ArrivalTs now)
        {
            if (state.UiPhase == IcebergUiPhase.Decaying)
            {
                // [Refinement A] Delayed Cleanup for Breached
                double cleanupDelay = state.ClosedReason == IcebergClosedReason.Breached
                    ? _opts.breach_cleanup_delay_sec
                    : 0;

                double ttl = Math.Max(_opts.decay_ttl_sec, cleanupDelay);

                if ((now - state.LastTouched).TotalSeconds >= ttl)
                {
                    // Only publish "Decayed" if it wasn't already explicitly Breached or Retreated or Closed
                    // (prevent double notification)
                    bool explicitDeath = state.LastBreached != default || state.LastRetreated != default || state.ClosedReason != IcebergClosedReason.None;
                    if (!explicitDeath)
                    {
                        if (!ShouldThrottle(nameof(IcebergDecayedEvent), state, now))
                        {
                            _bus.Publish(new IcebergDecayedEvent(state.Symbol, state.Price, state.IsBid, now)
                            {
                                OccurredAt = new DateTimeOffset(now.Value)
                            });
                        }
                    }
                    RemoveState(state.Symbol, state.IsBid, state.Price);
                }
            }
        }

        public bool TryGetUiBadge(string symbol, bool isBid, decimal price, out (IcebergUiPhase phase, IcebergTier tier) badge)
        {
            var key = (symbol, isBid, price);
            if (_states.TryGetValue(key, out var state))
            {
                badge = (state.UiPhase, state.Tier);
                return true;
            }
            badge = default;
            return false;
        }

        public int TryGetUiBadges(string symbol, IReadOnlyList<decimal> prices, bool isBid, (IcebergUiPhase phase, IcebergTier tier)[] results)
        {
            var index = isBid ? _bidIndex : _askIndex;
            if (!index.TryGetValue(symbol, out var symDict)) return 0;

            int count = 0;
            for (int i = 0; i < prices.Count; i++)
            {
                if (symDict.TryGetValue(prices[i], out var state))
                {
                    results[i] = (state.UiPhase, state.Tier);
                    count++;
                }
                else
                {
                    results[i] = default;
                }
            }
            return count;
        }
    }
}


