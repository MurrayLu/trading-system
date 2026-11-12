using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Events.OrderBook.Iceberg;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Time;
using MuNTrade.Core.Settings;
using MuNTrade.Abstractions.Contracts.Time;

namespace MuNTrade.Core.OrderBook
{
    /// <summary>
    /// 冰山偵測器：以 Arrival 軸為主，維持每價位/方向的衰減分數與動態門檻。
    /// </summary>
    internal sealed class IcebergDetector
    {
        private readonly IcebergDetectorOptions _opt;
        private readonly IClock _clock;
        private readonly ConcurrentDictionary<string, SymbolState> _symbols = new(StringComparer.OrdinalIgnoreCase);

        public IcebergDetector(IcebergDetectorOptions opt, IClock clock)
        {
            _opt = opt;
            _clock = clock;
        }

        public void OverrideMadWindow(string symbol, int window)
        {
            var sym = _symbols.GetOrAdd(symbol, _ => new SymbolState(_opt, symbol, _clock));
            sym.SetWindow(window);
        }

        public void OverrideMinTrade(string symbol, int minTrade)
        {
            var sym = _symbols.GetOrAdd(symbol, _ => new SymbolState(_opt, symbol, _clock));
            sym.EffectiveMinTrade = minTrade;
        }

        public void OverrideActivationThreshold(string symbol, int activation)
        {
            var sym = _symbols.GetOrAdd(symbol, _ => new SymbolState(_opt, symbol, _clock));
            sym.ActivationThreshold = Math.Max(1, activation);
        }

        public IEnumerable<object> Update(string symbol, decimal price, bool isBid, int matchedVol, int deltaQty, ArrivalTs arrival, bool isStale, double quality, decimal tickSize)
        {
            if (matchedVol <= 0) return Enumerable.Empty<object>(); // 只接受有成交資訊的價位
            var sym = _symbols.GetOrAdd(symbol, _ => new SymbolState(_opt, symbol, _clock));
            return sym.Update(price, isBid, matchedVol, deltaQty, arrival, isStale, quality, tickSize);
        }

        private enum IcebergDisplayStatus { None, Suspect, Confirmed, Decaying }

        private sealed class SymbolState
        {
            private readonly IcebergDetectorOptions _opt;
            private readonly string _symbol;

            private readonly ConcurrentDictionary<decimal, LevelState> _bidLevels = new();
            private readonly ConcurrentDictionary<decimal, LevelState> _askLevels = new();

            private readonly RollingEstimator _matchedBase;
            private readonly RollingEstimator _replenishBase;
            private readonly RollingEstimator _ratioBase;
            private readonly RollingEstimator _touchBase;

            // Deterministic Sweep: 改用 ArrivalTs 記錄上次清理時間
            private ArrivalTs _lastSweepArrival;

            public SymbolState(IcebergDetectorOptions opt, string symbol, IClock clock)
            {
                _opt = opt;
                _symbol = symbol;
                _matchedBase = new RollingEstimator(opt.rolling_window, clock);
                _replenishBase = new RollingEstimator(opt.rolling_window, clock);
                _ratioBase = new RollingEstimator(opt.rolling_window, clock);
                _touchBase = new RollingEstimator(opt.rolling_window, clock);
            }

            public int EffectiveMinTrade { get; set; }
            public int ActivationThreshold { get; set; }

            public void SetWindow(int window)
            {
                _matchedBase.Capacity = window;
                _replenishBase.Capacity = window;
                _ratioBase.Capacity = window;
                _touchBase.Capacity = window;
            }

            public IEnumerable<object> Update(decimal price, bool isBid, int matchedVol, int deltaQty, ArrivalTs arrival, bool isStale, double quality, decimal tickSize)
            {
                var events = new List<object>();
                var occurredAt = new DateTimeOffset(arrival.Value);
                // 根據方向選擇正確的 Dictionary
                var levels = isBid ? _bidLevels : _askLevels;
                var state = levels.GetOrAdd(price, _ => new LevelState(_opt));

                int activationThreshold = ActivationThreshold > 0
                    ? ActivationThreshold
                    : (EffectiveMinTrade > 0 ? EffectiveMinTrade : 1);

                if (!state.IsActivationOpen)
                {
                    if (matchedVol < activationThreshold)
                    {
                        return events;
                    }
                    state.IsActivationOpen = true;
                }

                // Queue Rebuild Filter
                // If this is a replenish (delta > 0), check if worse price dropped simultaneously
                bool isQueueRebuild = false;
                if (deltaQty > 0)
                {
                    decimal neighborP = isBid ? price - _opt.tick_size : price + _opt.tick_size;

                    // Only check if neighbor exists (no new dictionary creation)
                    if (levels.TryGetValue(neighborP, out var neighbor))
                    {
                        double dt = neighbor.LastSeenArrival != default
                            ? (arrival - neighbor.LastSeenArrival).TotalSeconds
                            : 999;

                        // "Simultaneous" = same batch or very close (e.g. 50ms)
                        if (dt < 0.05 && neighbor.LastDeltaQty < 0)
                        {
                            isQueueRebuild = true;
                        }
                    }
                }

                // [Debug] Log entry
                /*if (matchedVol > 0)
                {
                    Console.WriteLine($"[Iceberg] Update: {price} ({(isBid ? "Bid" : "Ask")}) Matched={matchedVol} Delta={deltaQty}");
                }*/

                double dtSec = state.LastSeenArrival != default ? (arrival - state.LastSeenArrival).TotalSeconds : 0;
                double decay = Math.Exp(-dtSec / Math.Max(1.0, _opt.decay_tau_sec));
                double scoreFastDecay = Math.Exp(-dtSec / Math.Max(1.0, _opt.score_tau_fast_sec));
                double scoreMidDecay = Math.Exp(-dtSec / Math.Max(1.0, _opt.score_tau_mid_sec));
                double scoreSlowDecay = Math.Exp(-dtSec / Math.Max(1.0, _opt.score_tau_slow_sec));

                int replenished = Math.Max(0, deltaQty + matchedVol);

                if (isQueueRebuild)
                {
                    // Queue shifts do not count as replenishment; use matched volume only.
                    replenished = matchedVol;
                }

                double touchInc = 1.0; // 只有 matchedVol>0 時才進來
                double ratio = matchedVol > 0 ? (double)replenished / matchedVol : 0.0;
                double ratioCapped = Math.Min(_opt.ratio_cap, ratio);

                state.MatchedDecayed = state.MatchedDecayed * decay + matchedVol;
                state.ReplenishDecayed = state.ReplenishDecayed * decay + replenished;
                state.TouchesDecayed = state.TouchesDecayed * decay + touchInc;

                // [Quality] Adjust score contribution by quality
                double scoreAdd = Math.Log(1 + matchedVol) * ratioCapped * quality;

                state.Score1m = state.Score1m * scoreFastDecay + scoreAdd;
                state.Score5m = state.Score5m * scoreMidDecay + scoreAdd;
                state.Score20m = state.Score20m * scoreSlowDecay + scoreAdd;

                state.LastSeenArrival = arrival;

                // Baseline 保護 + Stale 護欄
                if (!isStale)
                {
                    _matchedBase.Add(matchedVol);

                    // Fix: Update LastDeltaQty/Arrival if delta is significant (non-zero) or just update always for synchronization?
                    // We only care about drops/adds.
                    if (deltaQty != 0)
                    {
                        state.LastDeltaQty = deltaQty;
                        state.LastDeltaArrival = arrival;
                    }

                    if (replenished > 0)
                    {
                        _replenishBase.Add(replenished);
                        _ratioBase.Add(ratioCapped);
                    }

                    if (touchInc > 0) _touchBase.Add(touchInc);
                }

                bool warmSuspect = _matchedBase.IsWarm(_opt.min_samples_suspect) &&
                                   _touchBase.IsWarm(_opt.min_samples_suspect);

                bool warmConfirm = _matchedBase.IsWarm(_opt.min_samples_confirm) &&
                                   _replenishBase.IsWarm(_opt.min_samples_confirm) &&
                                   _ratioBase.IsWarm(_opt.min_samples_confirm) &&
                                   _touchBase.IsWarm(_opt.min_samples_confirm);

                double matchedNeed = _matchedBase.Threshold(_opt.matched_mad_mul, _opt.matched_floor, _opt.min_samples_suspect);
                double touchNeed = _touchBase.Threshold(_opt.touch_mad_mul, _opt.touch_floor, _opt.min_samples_suspect);

                double replenishNeed = _replenishBase.IsWarm(_opt.min_samples_suspect)
                    ? _replenishBase.Threshold(_opt.replenish_mad_mul, _opt.replenish_floor, _opt.min_samples_suspect)
                    : double.PositiveInfinity;

                double ratioNeed = _ratioBase.IsWarm(_opt.min_samples_suspect)
                    ? _ratioBase.Threshold(_opt.ratio_mad_mul, _opt.ratio_floor, _opt.min_samples_suspect)
                    : double.PositiveInfinity;

                bool strongMatched = state.MatchedDecayed >= matchedNeed;
                bool strongReplenish = state.ReplenishDecayed >= replenishNeed;
                bool strongTouch = state.TouchesDecayed >= touchNeed;
                bool strongRatio = ratioCapped >= ratioNeed;

                // New Logic: AbsorptionPct
                double totalVol = state.MatchedDecayed + state.ReplenishDecayed;
                state.AbsorptionPct = totalVol > 0.001 ? state.MatchedDecayed / totalVol : 0.0;

                // New Logic: Confidence
                double rawConf = 0.5 * state.Score1m + 0.3 * state.Score5m + 0.2 * state.Score20m;
                state.Confidence = Math.Min(1.0, Math.Max(0.0, rawConf));

                if (isStale) state.Confidence *= 0.6;
                state.Confidence *= quality;

                bool confirmed = strongMatched && strongReplenish && strongRatio && strongTouch && warmConfirm;
                bool suspect = warmSuspect && strongTouch && (strongMatched || strongReplenish);

                // Debug for non-triggering updates
                if (!confirmed && !suspect && matchedVol > 0)
                {
                    // Intentionally left blank; hook for temporary debugging.
                }

                // State Machine Logic
                if (confirmed)
                {
                    state.UiStatus = IcebergDisplayStatus.Confirmed;
                    state.LastTouched = arrival;
                }
                else if (suspect)
                {
                    if (state.UiStatus != IcebergDisplayStatus.Confirmed)
                    {
                        state.UiStatus = IcebergDisplayStatus.Suspect;
                    }
                    state.LastTouched = arrival;
                }
                else
                {
                    // Decay logic
                    double gap = state.LastTouched != default ? (arrival - state.LastTouched).TotalSeconds : 0;
                    double quietGap = 3.0;

                    if (state.UiStatus != IcebergDisplayStatus.None && state.UiStatus != IcebergDisplayStatus.Decaying)
                    {
                        if (gap > quietGap)
                        {
                            state.UiStatus = IcebergDisplayStatus.Decaying;
                        }
                    }

                    if (state.UiStatus == IcebergDisplayStatus.Decaying)
                    {
                        // Check if cleared
                        double totalScore = state.Score1m + state.Score5m + state.Score20m;
                        if (totalScore < 0.1 || gap > _opt.ttl_sec)
                        {
                            state.UiStatus = IcebergDisplayStatus.None;
                            state.LastPublishedStatus = IcebergDisplayStatus.None; // Reset status
                            state.IsActivationOpen = false;
                            events.Add(new IcebergClearedEvent(_symbol, price, isBid, arrival)
                            {
                                OccurredAt = occurredAt
                            });
                        }
                    }
                }

                // [Debug] Log status
                /*if (matchedVol > 0)
                {
                    Console.WriteLine($"[Iceberg] Status: Confirmed={confirmed} Suspect={suspect} UI={state.UiStatus} | Matched={state.MatchedDecayed:F1}/{matchedNeed:F1} Repl={state.ReplenishDecayed:F1}/{replenishNeed:F1} Ratio={ratioCapped:F2}/{ratioNeed:F2}");
                }*/

                var stats = _ratioBase.Snapshot();

                if (confirmed)
                {
                    // 狀態變更 (Edge Trigger) 才發送 Confirmed (首次確認)
                    if (state.LastPublishedStatus != IcebergDisplayStatus.Confirmed)
                    {
                        state.LastConfirmedArrival = arrival;
                        state.LastPublishedStatus = IcebergDisplayStatus.Confirmed;

                        events.Add(new IcebergConfirmedEvent(
                            _symbol,
                            price,
                            isBid,
                            arrival,
                            null,
                            state.Score1m,
                            state.Score5m,
                            state.Score20m,
                            state.MatchedDecayed,
                            state.ReplenishDecayed,
                            state.TouchesDecayed,
                            ratioCapped,
                            state.Confidence,
                            state.AbsorptionPct)
                        {
                            OccurredAt = occurredAt
                        });

                        // Console.WriteLine($"[Iceberg] EVENT CONFIRMED: {price}");
                    }
                    else if (state.CanPublishConfirmed(arrival, _opt.confirmed_min_gap_sec))
                    {
                        // 若已確認且隔了一段時間，改發 Persist (持續)，避免重複洗版 "冰山確認"
                        state.LastConfirmedArrival = arrival;
                        // Duration: 從最開始懷疑到現在
                        double duration = state.LastSuspectArrival != default
                            ? (arrival - state.LastSuspectArrival).TotalSeconds
                            : 0;

                        events.Add(new IcebergPersistEvent(
                            _symbol,
                            price,
                            isBid,
                            arrival,
                            1, // Level 暫定 1
                            duration)
                        {
                            OccurredAt = occurredAt
                        });
                    }
                }
                else if (suspect)
                {
                    // Keep confirmed states from publishing a lower Suspect status.
                    if (state.UiStatus != IcebergDisplayStatus.Confirmed && state.CanPublishSuspect(arrival, _opt.suspect_min_gap_sec))
                    {
                        state.LastPublishedStatus = IcebergDisplayStatus.Suspect;
                        state.LastSuspectArrival = arrival;
                        events.Add(new IcebergSuspectEvent(
                            _symbol,
                            price,
                            isBid,
                            arrival,
                            null,
                            state.Score1m,
                            state.Score5m,
                            state.Score20m,
                            state.MatchedDecayed,
                            state.ReplenishDecayed,
                            state.TouchesDecayed,
                            ratioCapped,
                            state.Confidence * 0.7,
                            state.AbsorptionPct)
                        {
                            OccurredAt = occurredAt
                        });

                        // Console.WriteLine($"[Iceberg] EVENT SUSPECT: {price}");
                    }
                }

                Sweep(arrival);
                return events;
            }

            private void Sweep(ArrivalTs now)
            {
                // 改用 ArrivalTs 計算時間差，確保回測與實盤行為一致 (Deterministic)
                // 只有當差距大於 30 秒 (牆鐘或模擬時間) 才執行清理
                if (_lastSweepArrival != default && (now - _lastSweepArrival).TotalSeconds < 30) return;
                _lastSweepArrival = now;

                // 分別對 Bid/Ask 進行清掃與容量限制
                SweepDict(_bidLevels, now);
                SweepDict(_askLevels, now);
            }

            private void SweepDict(ConcurrentDictionary<decimal, LevelState> levels, ArrivalTs now)
            {
                var ttl = TimeSpan.FromSeconds(_opt.ttl_sec);
                var remove = new List<decimal>();

                // 1. TTL Check
                foreach (var kv in levels)
                {
                    var lvl = kv.Value;
                    if (lvl.LastSeenArrival != default &&
                        (now - lvl.LastSeenArrival) > ttl &&
                        lvl.Score20m < 0.5 &&
                        lvl.TouchesDecayed < 0.5)
                    {
                        remove.Add(kv.Key);
                    }
                }
                foreach (var k in remove) levels.TryRemove(k, out _);

                // 2. Capacity Check (Per Side)
                if (levels.Count > _opt.max_levels_per_side)
                {
                    var excess = levels.Count - _opt.max_levels_per_side;
                    var toEvict = levels
                        .OrderBy(k => k.Value.Score20m)
                        .ThenBy(k => k.Value.LastSeenArrival)
                        .Take(excess)
                        .Select(k => k.Key);

                    foreach (var key in toEvict)
                    {
                        levels.TryRemove(key, out _);
                    }
                }
            }
        }

        private sealed class LevelState
        {
            private readonly IcebergDetectorOptions _opt;

            public ArrivalTs LastSeenArrival { get; set; }
            public double MatchedDecayed { get; set; }
            public double ReplenishDecayed { get; set; }
            public double TouchesDecayed { get; set; }
            public double Score1m { get; set; }
            public double Score5m { get; set; }
            public double Score20m { get; set; }
            public ArrivalTs LastSuspectArrival { get; set; }
            public ArrivalTs LastConfirmedArrival { get; set; }

            // New fields
            public IcebergDisplayStatus UiStatus { get; set; }
            public double Confidence { get; set; }
            public double AbsorptionPct { get; set; }
            public ArrivalTs LastTouched { get; set; }
            public IcebergDisplayStatus LastPublishedStatus { get; set; } // Track emitted status
            public bool IsActivationOpen { get; set; }

            public LevelState(IcebergDetectorOptions opt)
            {
                _opt = opt;
            }

            public bool CanPublishSuspect(ArrivalTs now, double gapSec)
            {
                if (LastSuspectArrival == default) return true;
                return (now - LastSuspectArrival).TotalSeconds >= gapSec;
            }

            public bool CanPublishConfirmed(ArrivalTs now, double gapSec)
            {
                if (LastConfirmedArrival == default) return true;
                return (now - LastConfirmedArrival).TotalSeconds >= gapSec;
            }

            public int LastDeltaQty { get; set; }
            public ArrivalTs LastDeltaArrival { get; set; }
        }
    }
}

