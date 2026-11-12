using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Time;
using MuNTrade.Core.Settings;
using MuNTrade.Core.OrderBook;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.OrderBook;

namespace MuNTrade.Core.OrderBook
{
    public sealed class LiquidityFlowDetector
    {
        private readonly ILogger _logger;
        private readonly LiquidityFlowDetectorOptions _opt;
        private readonly ConcurrentDictionary<string, SymbolFlowState> _states = new(StringComparer.OrdinalIgnoreCase);

        private static readonly FlowWindowStats ZeroStats = new(0, 0, 0, 0, 0, 0);

        public LiquidityFlowDetector(
            ILogger<LiquidityFlowDetector> logger,
            IOptionsMonitor<OrderBookProcessingOptions> options)
        {
            _logger = logger;
            _opt = options.CurrentValue.Engine.Liquidity;
        }

        public List<object> Process(
            string symbol,
            DateTime now,
            List<PriceLevel> bids,
            List<PriceLevel> asks,
            Dictionary<decimal, double> matchedVol,
            double addFloor,
            double pullFloor,
            bool isBroken,
            decimal p95Spread,
            LiquidityTimeGuards guards,
            FlowWindowStats flowStats)
        {
            // [Rule 6] Bad OrderBook quality tier => gating
            if (isBroken) return new List<object>();

            var state = _states.GetOrAdd(symbol, s => new SymbolFlowState(s));
            // Always update guards on the state
            return state.Update(now, bids, asks, matchedVol, addFloor, pullFloor, p95Spread, guards, flowStats);
        }

        private sealed class SymbolFlowState
        {
            private readonly string _symbol;
            private readonly Dictionary<decimal, PriceState> _bids = new();
            private readonly Dictionary<decimal, PriceState> _asks = new();
            private decimal _prevBestBid;
            private decimal _prevBestAsk;
            private DateTime _prevTime;

            // Backtrack History: Price -> LastAddEventTime
            private readonly Dictionary<decimal, DateTime> _recentAdds = new();

            public SymbolFlowState(string symbol)
            {
                _symbol = symbol;
            }

            public List<object> Update(
                DateTime now,
                List<PriceLevel> newBids,
                List<PriceLevel> newAsks,
                Dictionary<decimal, double> matched,
                double addFloor, double pullFloor, decimal p95Spread,
                LiquidityTimeGuards guards,
            FlowWindowStats flowStats)
            {
                var events = new List<object>();

                decimal bestBid = newBids.Count > 0 ? newBids[0].Price : 0;
                decimal bestAsk = newAsks.Count > 0 ? newAsks[0].Price : 0;

                // [Rule 2] Shift Guard Detection
                // If BBO moved, mark direction
                bool bidShiftUp = bestBid > _prevBestBid && _prevBestBid > 0;
                bool bidShiftDown = bestBid < _prevBestBid;
                bool askShiftUp = bestAsk > _prevBestAsk && _prevBestAsk > 0;
                bool askShiftDown = bestAsk < _prevBestAsk;

                // Process Bids
                ProcessSide(now, true, newBids, _bids, matched, addFloor, pullFloor,
                    bidShiftUp, bidShiftDown, bestBid, events, guards, flowStats);

                // Process Asks
                ProcessSide(now, false, newAsks, _asks, matched, addFloor, pullFloor,
                    askShiftUp, askShiftDown, bestAsk, events, guards, flowStats);

                _prevBestBid = bestBid;
                _prevBestAsk = bestAsk;
                _prevTime = now;

                // Cleanup Backtrack history
                var expire = now.AddMilliseconds(-guards.BacktrackMs);
                var keys = _recentAdds.Keys.ToList(); // copy
                foreach (var k in keys) if (_recentAdds[k] < expire) _recentAdds.Remove(k);

                return events;
            }

            private void ProcessSide(
                DateTime now, bool isBid,
                List<PriceLevel> levels,
                Dictionary<decimal, PriceState> state,
                Dictionary<decimal, double> matched,
                double addFloor, double pullFloor,
                bool shiftedTowards, bool shiftedAway, decimal bestPrice,
                List<object> events, LiquidityTimeGuards guards, FlowWindowStats flowStats)
            {
                var seen = new HashSet<decimal>();
                var occurredAt = new DateTimeOffset(now);

                foreach (var lvl in levels)
                {
                    seen.Add(lvl.Price);
                    double vol = (double)lvl.Volume;
                    matched.TryGetValue(lvl.Price, out double matchVol);

                    if (!state.TryGetValue(lvl.Price, out var s))
                    {
                        // [Rule 1] Existence: New Level -> Exposed=true, Ban Add this tick (Reason=NewLevel)
                        s = new PriceState { Price = lvl.Price, Volume = vol, Created = now, Exposed = true, ExposedAt = now };
                        state[lvl.Price] = s;

                        if (shiftedTowards) { s.Shifted = true; s.ShiftedAt = now; }

                        // Publish Suppressed Event for New Level ONLY if not caused by Shift AND volume > Threshold
                        // If shiftedTowards is true, this "new level" is likely just existing depth we moved into.
                        // Also enforce addFloor to avoid spamming 1-lot new levels.
                        if (!shiftedTowards && vol > addFloor)
                        {
                            events.Add(new BigHangEvent(_symbol, isBid, new ArrivalTs(now), null, vol, flowStats, false, lvl.Price, LiquidityGuardReason.NewLevel, true)
                            {
                                OccurredAt = occurredAt
                            });
                        }
                        continue;
                    }

                    // Existing Level
                    double delta = vol - s.Volume;
                    double effectiveDelta = delta + matchVol;

                    // [Grace Period] Reset PendingRemoval if level reappears
                    if (s.PendingRemoval) s.PendingRemoval = false;

                    // Clean Expire Flags
                    if (s.Exposed && (now - s.ExposedAt).TotalMilliseconds > guards.ExposeMs) s.Exposed = false;
                    if (s.Shifted && (now - s.ShiftedAt).TotalMilliseconds > guards.ShiftMs) s.Shifted = false;

                    // [Rule 2] Live Shift Guard Application
                    if (shiftedTowards)
                    {
                        s.Shifted = true;
                        s.ShiftedAt = now;
                        // Suppress ADD if trying to add during shift towards
                        // Logic below handles suppression via s.Shifted check
                    }

                    // ADD Case
                    if (effectiveDelta > addFloor)
                    {
                        var guard = LiquidityGuardReason.None;

                        // [Rule 1] Exposed Check
                        if (s.Exposed) guard = LiquidityGuardReason.Exposed;

                        // [Rule 2] Shift Check
                        else if (s.Shifted)
                        {
                            if ((now - s.ShiftedAt).TotalMilliseconds < guards.ShiftMs)
                                guard = LiquidityGuardReason.Shifted;
                        }

                        // Always publish, but mark suppressed if guard != None
                        bool suppressed = guard != LiquidityGuardReason.None;
                        if (!suppressed) _recentAdds[lvl.Price] = now;

                        events.Add(new BigHangEvent(_symbol, isBid, new ArrivalTs(now), null, effectiveDelta, flowStats, false, lvl.Price, guard, suppressed)
                        {
                            OccurredAt = occurredAt
                        });
                    }
                    // PULL Case
                    else if (effectiveDelta < -pullFloor)
                    {
                        double pullAmt = -effectiveDelta;
                        var guard = LiquidityGuardReason.None;

                        // [Rule 3] Resting Life
                        if ((now - s.Created).TotalMilliseconds < guards.RestMs) guard = LiquidityGuardReason.Young;

                        // [Rule 5] Backtrack (Absorbed)
                        if (_recentAdds.TryGetValue(lvl.Price, out var addTime))
                        {
                            if ((now - addTime).TotalMilliseconds < guards.BacktrackMs) guard = LiquidityGuardReason.Absorbed;
                        }

                        events.Add(new BigPullEvent(_symbol, isBid, new ArrivalTs(now), null, pullAmt, flowStats, false, lvl.Price, guard, guard != LiquidityGuardReason.None)
                        {
                            OccurredAt = occurredAt
                        });
                    }

                    // Update State
                    s.Volume = vol;
                    s.LastUpdated = now;
                }

                // Handle Disappeared Levels
                var missing = state.Keys.Where(k => !seen.Contains(k)).ToList();
                foreach (var p in missing)
                {
                    var s = state[p];

                    // [Grace Period] Defer Removal
                    // If not yet pending, mark as pending and skip.
                    if (!s.PendingRemoval && guards.RemovalDelayMs > 0)
                    {
                        s.PendingRemoval = true;
                        s.PendingRemovalSince = now;
                        continue;
                    }

                    // If pending, check timer
                    if (s.PendingRemoval)
                    {
                        if ((now - s.PendingRemovalSince).TotalMilliseconds < guards.RemovalDelayMs)
                            continue; // Still in grace period
                    }

                    matched.TryGetValue(p, out double matchVol);
                    double delta = 0 - s.Volume;
                    double effectiveDelta = delta + matchVol;

                    if (effectiveDelta < -pullFloor)
                    {
                        var guard = LiquidityGuardReason.None;
                        // [Rule 3] Resting
                        if ((now - s.Created).TotalMilliseconds < guards.RestMs) guard = LiquidityGuardReason.Young;

                        // [Rule 2] Shift Away
                        if (shiftedAway) guard = LiquidityGuardReason.Shifted;

                        // [Rule 5] Backtrack
                        if (_recentAdds.TryGetValue(p, out var addTime))
                        {
                            if ((now - addTime).TotalMilliseconds < guards.BacktrackMs) guard = LiquidityGuardReason.Absorbed;
                        }

                        events.Add(new BigPullEvent(_symbol, isBid, new ArrivalTs(now), null, -effectiveDelta, flowStats, false, p, guard, guard != LiquidityGuardReason.None)
                        {
                            OccurredAt = occurredAt
                        });
                    }
                    state.Remove(p);
                }
            }
        }

        private class PriceState
        {
            public decimal Price;
            public double Volume;
            public DateTime Created;
            public DateTime LastUpdated;
            public bool Exposed;
            public DateTime ExposedAt;
            public bool Shifted;
            public DateTime ShiftedAt;
            public bool PendingRemoval;
            public DateTime PendingRemovalSince;
        }
    }
}

