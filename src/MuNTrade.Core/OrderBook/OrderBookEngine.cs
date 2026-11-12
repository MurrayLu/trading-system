using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Events; // Added for OrderBookQualityTier
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Core.Settings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions; // Added
using MuNTrade.Abstractions.Time;
using MuNTrade.Core.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook;
using MuNTrade.Core.Common;

namespace MuNTrade.Core.OrderBook
{
    public class OrderBookEngine
    {
        private readonly IEventBus _bus;
        private readonly ILogger<OrderBookEngine> _logger;
        private readonly IOptionsMonitor<OrderBookProcessingOptions> _options;
        private readonly ITradingSessionProvider _sessionProvider;
        private readonly IcebergDetector _icebergDetector;
        private readonly LiquidityFlowDetector _liquidityFlowDetector;
        private readonly IcebergTracker _icebergTracker;
        private readonly IcebergStoryEngine _storyEngine;
        private readonly MuNTrade.Abstractions.Contracts.Services.IPriceTierService _priceTier;
        private readonly IClock _clock;

        private readonly ConcurrentDictionary<string, SymbolContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _tradeMatchWindow = TimeSpan.FromMilliseconds(1000);

        public OrderBookEngine(
            IEventBus bus,
            ILogger<OrderBookEngine> logger,
            IOptionsMonitor<OrderBookProcessingOptions> options,
            ITradingSessionProvider sessionProvider,
            IClock clock,
            ILoggerFactory loggerFactory,
            MuNTrade.Abstractions.Contracts.Services.IPriceTierService priceTier)
        {
            _bus = bus;
            _logger = logger;
            _options = options;
            _sessionProvider = sessionProvider;
            _clock = clock;
            _priceTier = priceTier;
            var engineOpt = GetEngineOptions();
            _icebergDetector = new IcebergDetector(engineOpt.Iceberg ?? new IcebergDetectorOptions(), _clock);
            _liquidityFlowDetector = new LiquidityFlowDetector(loggerFactory.CreateLogger<LiquidityFlowDetector>(), _options);
            _icebergTracker = new IcebergTracker(bus, logger, engineOpt.IcebergTracker ?? new IcebergTrackerOptions());
            _storyEngine = new IcebergStoryEngine(bus, NullLogger<IcebergStoryEngine>.Instance);
        }

        private OrderBookEngineOptions GetEngineOptions()
        {
            return _options.CurrentValue.Engine ?? new OrderBookEngineOptions();
        }

        public void OnTrade(TradeTickEvent tick)
        {
            var cfg = GetEngineOptions();

            // [Fix] 時間軸契約修正 (選項 A):
            // 成交使用 ExchangeTs 查詢 Session，確保對齊「交易所正史」。
            // 這避免了因網路傳輸延遲導致 ArrivalTs 漂移到錯誤的 Session (如跨日/收盤後)。
            var session = _sessionProvider.GetSession(tick.Symbol, tick.ExchangeTs.Value);
            if (session == null) return;

            if (tick.IsSimulated)
            {
                var simCtx = _contexts.GetOrAdd(tick.Symbol, _ => new SymbolContext());
                lock (simCtx.SyncRoot)
                {
                    simCtx.LastWasSimulated = true;
                }
                return;
            }

            var ctx = _contexts.GetOrAdd(tick.Symbol, _ => new SymbolContext());

            lock (ctx.SyncRoot)
            {
                if (ctx.CurrentSessionStart != session.StartTime)
                {
                    ctx.Stats.Reset();
                    ctx.IsFirstTradeProcessed = false;
                    ctx.CurrentSessionStart = session.StartTime;
                    ctx.LastLargeThresholdReason = null;
                    ctx.LastLoggedOpeningSampleCount = -1;

                    ctx.PendingAggregation = null; // 清空待處理聚合
                }

                // 初始化新的待處理聚合（如果沒有）
                if (ctx.PendingAggregation == null)
                {
                    ctx.PendingAggregation = new AggregatedState
                    {
                        Symbol = tick.Symbol,
                        ExchangeTs = tick.ExchangeTs,
                        ArrivalTs = tick.ArrivalTs,
                        Side = tick.Side,
                        Price = tick.Price,
                        MinPrice = tick.Price,
                        MaxPrice = tick.Price,
                        Volume = tick.Volume,
                        IsReopenTrade = ctx.LastWasSimulated
                    };

                    ctx.LastWasSimulated = false;

                    // Skew and orphan statistics are updated by JoinerHealthEvent.
                    return;
                }

                // 檢查是否為同一筆交易 (時間與方向相同)
                if (tick.ExchangeTs == ctx.PendingAggregation.ExchangeTs &&
                    tick.Side == ctx.PendingAggregation.Side)
                {
                    ctx.PendingAggregation.Volume += tick.Volume;
                    ctx.PendingAggregation.Price = tick.Price;

                    if (tick.Price < ctx.PendingAggregation.MinPrice) ctx.PendingAggregation.MinPrice = tick.Price;
                    if (tick.Price > ctx.PendingAggregation.MaxPrice) ctx.PendingAggregation.MaxPrice = tick.Price;

                    ctx.PendingAggregation.ArrivalTs = tick.ArrivalTs;
                    return;
                }

                // 將上一個聚合寫入 (Flush)
                FlushAggregation(ctx, cfg, session.StartTime);

                // 設定新的待處理聚合 
                ctx.PendingAggregation = new AggregatedState
                {
                    Symbol = tick.Symbol,
                    ExchangeTs = tick.ExchangeTs,
                    ArrivalTs = tick.ArrivalTs,
                    Side = tick.Side,
                    Price = tick.Price,
                    MinPrice = tick.Price,
                    MaxPrice = tick.Price,
                    Volume = tick.Volume,
                    IsReopenTrade = ctx.LastWasSimulated
                };

                ctx.LastWasSimulated = false;

                // Skew and orphan statistics are updated by JoinerHealthEvent.

                if (ctx.LastQualityTier != OrderBookQualityTier.Bad && ctx.LastQualityTier != OrderBookQualityTier.Degraded)
                {
                    // [SSOT] Use unified price tier service for tick size
                    decimal tickSize = _priceTier.GetTickPrice(tick.Price);
                    _icebergTracker.OnTrade(tick.Symbol, tick, tickSize);
                }

            }
        }

        private void FlushAggregation(SymbolContext ctx, OrderBookEngineOptions cfg, DateTime sessionStart)
        {
            var agg = ctx.PendingAggregation;
            if (agg == null) return;

            if (!agg.IsReopenTrade)
            {
                var learningEnd = sessionStart.AddMinutes(cfg.learning_duration_minutes);

                if (agg.ExchangeTs.Value < learningEnd)
                {
                    if (!ctx.IsFirstTradeProcessed) ctx.IsFirstTradeProcessed = true;
                    else
                    {
                        ctx.Stats.AddSample(agg.Volume);
                        LogLargeThresholdSamplingProgress(ctx, agg.Symbol, agg.ExchangeTs.Value, learningEnd);
                    }
                }
                else
                {
                    var wasFrozen = ctx.Stats.IsFrozen;
                    ctx.Stats.Freeze(cfg);
                    if (!wasFrozen)
                    {
                        LogLargeThresholdFreeze(ctx, agg.Symbol, cfg, agg.ExchangeTs.Value);
                    }

                    if (ctx.Stats.IsLargeTrade(agg.Volume, cfg.large_trade_dynamic_mul))
                    {
                        double threshold = ctx.Stats.GetCurrentThreshold(cfg.large_trade_dynamic_mul);

                        // 根據買賣方向顯示不同的價格：賣單顯示最低價，買單顯示最高價
                        decimal reportPrice = agg.Price;
                        if (agg.Side == AggressorSide.Sell) reportPrice = agg.MinPrice;
                        else if (agg.Side == AggressorSide.Buy) reportPrice = agg.MaxPrice;

                        _bus.Publish(new LargeTradeEvent(
                            agg.Symbol, agg.ExchangeTs, reportPrice, agg.Volume, agg.Side, threshold)
                        {
                            OccurredAt = new DateTimeOffset(agg.ArrivalTs.Value)
                        });
                    }
                }
            }

            // 存入 Cache (供冰山計算使用)
            var queue = ctx.RecentTrades.GetOrAdd(agg.Price, _ => new ConcurrentQueue<(int Vol, ArrivalTs Ts)>());
            queue.Enqueue((agg.Volume, agg.ArrivalTs));

            CleanUpTradeCache(ctx, agg.ArrivalTs);

            // 清空待處理聚合
            ctx.PendingAggregation = null;
        }

        public void OnBest5(Best5DeltaEvent best5)
        {
            if (best5.FeedKind != TickFeedKind.RealTime) return;

            var cfg = GetEngineOptions();

            // [Fix] 時間軸契約修正 (選項 A 實作):
            // 移除 Best5 的 Session 檢查 (原本使用 ArrivalTs 導致與 Trade 的 ExchangeTs 不一致)。
            // 現在 Best5 完全依賴 OnTrade 開啟的 Session Gate (ctx.CurrentSessionStart)。
            // 這是為了避免「本機到達時間」與「交易所時間」在跨盤/收盤時的錯切衝突。
            // 如果 Context 未初始化 (尚未收到 Session 內的第一筆 Trade)，我們仍允許更新 Book 但不重置 Session 狀態。

            // var session = _sessionProvider.GetSession(best5.Symbol, best5.ArrivalTs.Value); // REMOVED

            var ctx = _contexts.GetOrAdd(best5.Symbol, _ => new SymbolContext());

            // 如果依賴 Session 狀態做邏輯判斷，應檢查 CurrentSessionStart 是否有效
            // 但為了保持 Book 更新，我們這裡繼續執行，只是已知 Session 狀態由 OnTrade 驅動。

            lock (ctx.SyncRoot)
            {
                // --- 檢查是否過時 ---
                bool isStale = ctx.LastBest5ArrivalTs != default && (best5.ArrivalTs - ctx.LastBest5ArrivalTs).TotalSeconds > 5;

                var startTime = (ctx.LastBest5ArrivalTs == default || isStale)
                    ? best5.ArrivalTs.Value.AddMilliseconds(-100)
                    : ctx.LastBest5ArrivalTs.Value;

                var endTime = best5.ArrivalTs.Value;
                ctx.LastBest5ArrivalTs = best5.ArrivalTs;

                if (isStale) ctx.IcebergSuspects.Clear();

                // --- 計算 OFI (Order Flow Imbalance) ---
                CalculateAndPublishOFI(ctx, best5);

                // --- Quality Gate Update ---
                var (score, tier, reasons) = ctx.QualityGate.UpdateBest5(best5.ArrivalTs, isStale);
                ctx.LastQualityTier = tier;

                bool allowIceberg = tier != OrderBookQualityTier.Bad && tier != OrderBookQualityTier.Degraded;

                int effMinTrade = cfg.iceberg_min_trade;
                double largeTradeThreshold = ctx.Stats.GetCurrentThreshold(cfg.large_trade_dynamic_mul);
                double effectiveLargeThreshold = largeTradeThreshold > 0 ? largeTradeThreshold : cfg.stat_fallback_threshold;
                LogLargeThresholdStateIfChanged(ctx, best5.Symbol, cfg, largeTradeThreshold, effectiveLargeThreshold);
                int activationThreshold = (int)Math.Ceiling(effectiveLargeThreshold);
                int effMinTradeFromThreshold = (int)Math.Ceiling(effectiveLargeThreshold / 5.0);

                if (allowIceberg)
                {
                    // 1. Iceberg Min Trade (Clamped 5-200, Safety: Max of Base/Threshold)
                    int profMinTrade = cfg.iceberg_min_trade;
                    effMinTrade = Math.Max(effMinTradeFromThreshold, profMinTrade);
                    effMinTrade = Math.Clamp(effMinTrade, 1, 200);
                    _icebergDetector.OverrideMinTrade(best5.Symbol, effMinTrade);
                    _icebergDetector.OverrideActivationThreshold(best5.Symbol, activationThreshold);

                    // 2. Iceberg MAD Window (Clamped 200-1200)
                    int profWindow = 600;
                    int effWindow = Math.Clamp(profWindow, 200, 1200);
                    _icebergDetector.OverrideMadWindow(best5.Symbol, effWindow);

                    // 3. Liquidity Floors (Handled in Process call via effAdd/effPull)
                    // _liquidityFlowDetector.OverrideFloors removed.

                    // 4. Iceberg Event Throttle (base default)
                    var profThrottle = TimeSpan.FromSeconds(1);
                    var baseThrottle = TimeSpan.FromSeconds(1.0); // Conservative default

                    var effThrottle = profThrottle > baseThrottle ? profThrottle : baseThrottle;
                    _icebergTracker.OverrideEventThrottle(best5.Symbol, effThrottle);

                }
                else
                {
                    // Fallback or Gating - Defaults
                    effMinTrade = Math.Max(cfg.iceberg_min_trade, 5);
                }

                if (allowIceberg)
                {
                    // [SSOT] Calculate BBO tick size for General Tracker updates
                    decimal bboPrice = 0m;
                    if (best5.Bids.Count > 0) bboPrice = best5.Bids[0].Price;
                    else if (best5.Asks.Count > 0) bboPrice = best5.Asks[0].Price;

                    decimal bboTick = _priceTier.GetTickPrice(bboPrice);
                    _icebergTracker.OnBest5Snapshot(best5.Symbol, best5, best5.ArrivalTs, isStale, bboTick);
                }

                // --- 準備新單 ---
                var newBids = ToDictionary(best5.Bids);
                var newAsks = ToDictionary(best5.Asks);

                var detectorEvents = new List<object>();
                int suppressedCount = 0;

                // --- 比較新舊單 ---
                if (!isStale)
                {
                    int wallThreshold = CalculateWallThreshold(ctx, cfg);
                    int dynamicThreshold = CalculateDynamicThreshold(best5, cfg);

                    var updates = new List<OrderBookChangeEvent>();

                    // [Fix] O(1) 效能優化：預先計算此區間內的成交量分佈
                    // 避免在 CompareSideOptimized 迴圈內重複掃描 Queue (O(N) -> O(1))
                    var matchedVols = BuildMatchedVolumeMap(ctx, startTime, endTime);

                    // === Price-aware: 計算本輪 best1 是否變價 ===
                    decimal prevBestBid = ctx.CurrentBids.Count > 0 ? ctx.CurrentBids.Keys.Max() : 0m;
                    decimal prevBestAsk = ctx.CurrentAsks.Count > 0 ? ctx.CurrentAsks.Keys.Min() : 0m;

                    decimal newBestBid = newBids.Count > 0 ? newBids.Keys.Max() : 0m;
                    decimal newBestAsk = newAsks.Count > 0 ? newAsks.Keys.Min() : 0m;

                    bool priceMovedBid = prevBestBid != 0m && newBestBid != 0m && prevBestBid != newBestBid;
                    bool priceMovedAsk = prevBestAsk != 0m && newBestAsk != 0m && prevBestAsk != newBestAsk;

                    // Compare Bids
                    var bidResult = CompareSideOptimized(ctx, best5, startTime, endTime,
                        ctx.CurrentBids, newBids,
                        isBid: true, wallThreshold, dynamicThreshold, cfg,
                        effMinTrade, isStale, matchedVols, score, tier, detectorEvents,
                        allowIceberg,
                        priceMovedSide: priceMovedBid);
                    updates.AddRange(bidResult.Changes);

                    // Compare Asks
                    var askResult = CompareSideOptimized(ctx, best5, startTime, endTime,
                        ctx.CurrentAsks, newAsks,
                        isBid: false, wallThreshold, dynamicThreshold, cfg,
                        effMinTrade, isStale, matchedVols, score, tier, detectorEvents,
                        allowIceberg,
                        priceMovedSide: priceMovedAsk);
                    updates.AddRange(askResult.Changes);


                    // [SSOT] Aggregate add/pull for this snapshot and maintain P90
                    double addSumTotal = bidResult.AddSum + askResult.AddSum;
                    double pullSumTotal = bidResult.PullSum + askResult.PullSum;

                    ctx.AddPctl.Add(addSumTotal);
                    ctx.PullPctl.Add(pullSumTotal);

                    var flowStats = new MuNTrade.Abstractions.Dtos.OrderBook.FlowWindowStats(
                        WindowMs: 1000,
                        P90Add: ctx.AddPctl.GetPctl(0.90),
                        P90Pull: ctx.PullPctl.GetPctl(0.90),
                        LastAdd: addSumTotal,
                        LastPull: pullSumTotal,
                        Samples: Math.Min(ctx.AddPctl.Count, ctx.PullPctl.Count)
                    );
                    ctx.LastFlowStats = flowStats;

                    // Publish CumHangEvent (Background Liquidity Update)
                    _bus.Publish(new CumHangEvent(best5.Symbol, best5.ArrivalTs.Value, flowStats)
                    {
                        OccurredAt = new DateTimeOffset(best5.ArrivalTs.Value)
                    });

                    // Liquidity flow detector applies the configured order-book rules.
                    if (allowIceberg)
                    {
                        var guards = new LiquidityTimeGuards(250, 250, 300, 200, 300);

                        // [Fix] Sync with Large Trade Threshold (User Request):
                        // If "Large Order" is dynamically calculated (e.g. 89), use it.
                        // If not yet available (Opening, stats not frozen), fallback to default 50.
                        var baseAdd = cfg.Liquidity.add_floor;
                        var basePull = cfg.Liquidity.pull_floor;

                        var effAdd = baseAdd;
                        var effPull = basePull;

                        // Apply Large Trade Threshold (Dynamic or Fallback 50)
                        effAdd = Math.Max(effAdd, effectiveLargeThreshold);
                        effPull = Math.Max(effPull, effectiveLargeThreshold);

                        var liqEvents = _liquidityFlowDetector.Process(
                            best5.Symbol,
                            best5.ArrivalTs.Value,
                            best5.Bids.ToList(),
                            best5.Asks.ToList(),
                            matchedVols,
                            effAdd,
                            effPull,
                            tier == OrderBookQualityTier.Bad, // OrderBook quality gate

                            0, // p95Spread placeholder
                            guards,
                            flowStats
                        );
                        detectorEvents.AddRange(liqEvents);
                    }

                    if (updates.Count > 0)
                    {
                        _bus.Publish(new OrderBookUpdateEvent(best5.Symbol, best5.ArrivalTs, updates)
                        {
                            OccurredAt = new DateTimeOffset(best5.ArrivalTs.Value)
                        });
                    }
                }

                // --- Kill Switch Logic ---
                foreach (var evt in detectorEvents)
                {
                    bool suppress = false;

                    if (tier == OrderBookQualityTier.Bad)
                    {
                        suppress = true;
                    }
                    else if (tier == OrderBookQualityTier.Degraded)
                    {
                        if (evt is IcebergSuspectEvent || evt is IcebergConfirmedEvent ||
                            evt is CumHangEvent || evt is CumPullEvent || evt is CumReplenishEvent)
                        {
                            suppress = true;
                        }
                        // BigHang/BigPull are allowed but marked as SuppressedByQuality (handled in detector)
                    }

                    // Enforce Quality Gate (Item 9) and skip internal state update when quality is bad/degraded
                    if (!suppress && allowIceberg)
                    {
                        if (evt is IcebergSuspectEvent susp) _icebergTracker.OnIcebergSuspect(susp);
                        else if (evt is IcebergConfirmedEvent conf) _icebergTracker.OnIcebergConfirmed(conf);
                        else if (evt is IcebergClearedEvent clr) _icebergTracker.OnIcebergCleared(clr);
                    }

                    if (suppress)
                    {
                        suppressedCount++;
                    }
                    else
                    {
                        _bus.Publish((dynamic)evt);
                    }
                }

                // Publish Quality Event (Always, with suppressed count)
                _bus.Publish(new OrderBookQualityEvent(
                    best5.Symbol,
                    best5.ArrivalTs,
                    score,
                    tier,
                    reasons,
                    false, // IsSuppressed (handled by detectors)
                    suppressedCount
                ));

                // --- 更新新單 ---
                ctx.CurrentBids = newBids;
                ctx.CurrentAsks = newAsks;
            }
        }

        // [New] 構建成交量查詢表 (O(T) where T is active trade count)
        private Dictionary<decimal, double> BuildMatchedVolumeMap(SymbolContext ctx, DateTime startTime, DateTime endTime)
        {
            var map = new Dictionary<decimal, double>();
            var lookbackStart = startTime.AddMilliseconds(-50);
            var lookbackEnd = endTime.AddMilliseconds(50);

            // 雖然這裡還是要掃描，但只掃描「有成交的價位」的 Queue，
            // 且在一次 Best5 週期只做一次，而非對 OrderBook 的每個價位都做一次。
            foreach (var kvp in ctx.RecentTrades)
            {
                double volSum = 0;
                foreach (var t in kvp.Value)
                {
                    if (t.Ts.Value > lookbackStart && t.Ts.Value <= lookbackEnd)
                    {
                        volSum += t.Vol;
                    }
                }
                if (volSum > 0)
                {
                    map[kvp.Key] = volSum;
                }
            }

            // [Fix] Include current PendingAggregation as it might not be flushed to RecentTrades yet
            var pending = ctx.PendingAggregation;
            if (pending != null && pending.ArrivalTs.Value > lookbackStart && pending.ArrivalTs.Value <= lookbackEnd)
            {
                if (map.TryGetValue(pending.Price, out double current))
                    map[pending.Price] = current + pending.Volume;
                else
                    map[pending.Price] = pending.Volume;
            }

            return map;
        }

        private int CalculateWallThreshold(SymbolContext ctx, OrderBookEngineOptions cfg)
        {
            double avgTradeSize = ctx.Stats.AvgOrderSize;
            return (int)Math.Max(cfg.min_wall_size, avgTradeSize * cfg.wall_size_mul);
        }

        private void LogLargeThresholdSamplingProgress(SymbolContext ctx, string symbol, DateTime exchangeTs, DateTime learningEnd)
        {
            int count = ctx.Stats.OpeningSampleCount;
            if (count == ctx.LastLoggedOpeningSampleCount)
                return;

            bool shouldLog = count == 1;
            if (!shouldLog)
                return;

            ctx.LastLoggedOpeningSampleCount = count;
            _logger.LogDebug(
                "[LargeThreshold] sample-start symbol={Symbol} count={Count} totalVolume={TotalVolume:0.##} recentAvg={RecentAvg:0.##} exchangeTs={ExchangeTs:HH:mm:ss.fff} learningEnd={LearningEnd:HH:mm:ss.fff}",
                symbol,
                count,
                ctx.Stats.OpeningSampleTotalVolume,
                ctx.Stats.RecentAvgSize,
                exchangeTs,
                learningEnd);
        }

        private void LogLargeThresholdFreeze(SymbolContext ctx, string symbol, OrderBookEngineOptions cfg, DateTime exchangeTs)
        {
            var threshold = ctx.Stats.GetCurrentThreshold(cfg.large_trade_dynamic_mul);

            _logger.LogInformation(
                "[LargeThreshold] frozen symbol={Symbol} threshold={Threshold:0.##} frozenThreshold={FrozenThreshold:0.##} samples={SampleCount} sampleVolume={SampleVolume:0.##} recentAvg={RecentAvg:0.##} reason={Reason} exchangeTs={ExchangeTs:HH:mm:ss.fff}",
                symbol,
                threshold,
                ctx.Stats.FrozenThreshold,
                ctx.Stats.LastFreezeSampleCount,
                ctx.Stats.LastFreezeSampleTotalVolume,
                ctx.Stats.RecentAvgSize,
                ctx.Stats.LastFreezeReason,
                exchangeTs);
        }

        private void LogLargeThresholdStateIfChanged(SymbolContext ctx, string symbol, OrderBookEngineOptions cfg, double currentThreshold, double effectiveThreshold)
        {
            var reason = DescribeLargeThresholdReason(ctx, currentThreshold);
            if (string.Equals(reason, ctx.LastLargeThresholdReason, StringComparison.Ordinal))
                return;

            ctx.LastLargeThresholdReason = reason;

            _logger.LogInformation(
                "[LargeThreshold] state symbol={Symbol} effective={EffectiveThreshold:0.##} dynamic={DynamicThreshold:0.##} fallback={FallbackThreshold:0.##} frozen={IsFrozen} reason={Reason} openingSamples={OpeningSampleCount} freezeSamples={FreezeSampleCount} freezeVolume={FreezeSampleVolume:0.##}",
                symbol,
                effectiveThreshold,
                currentThreshold,
                cfg.stat_fallback_threshold,
                ctx.Stats.IsFrozen,
                reason,
                ctx.Stats.OpeningSampleCount,
                ctx.Stats.LastFreezeSampleCount,
                ctx.Stats.LastFreezeSampleTotalVolume);
        }

        private static string DescribeLargeThresholdReason(SymbolContext ctx, double currentThreshold)
        {
            if (!ctx.Stats.IsFrozen)
            {
                if (ctx.Stats.OpeningSampleCount > 0)
                    return "sampling-opening-window";

                return "stats-not-ready";
            }

            if (currentThreshold > 0 && string.Equals(ctx.Stats.LastFreezeReason, "ready-dynamic", StringComparison.Ordinal))
                return "ready-dynamic";

            if (ctx.Stats.LastFreezeSampleCount == 0)
                return "opening-window-missed";

            return ctx.Stats.LastFreezeReason;
        }

        private int CalculateDynamicThreshold(Best5DeltaEvent best5, OrderBookEngineOptions cfg)
        {
            var volumes = new List<int>(10);
            foreach (var b in best5.Bids) if (b.Volume > 0) volumes.Add(b.Volume);
            foreach (var a in best5.Asks) if (a.Volume > 0) volumes.Add(a.Volume);

            if (volumes.Count == 0) return int.MaxValue;

            volumes.Sort();
            double median;
            int count = volumes.Count;
            if (count % 2 == 0) median = (volumes[count / 2 - 1] + volumes[count / 2]) / 2.0;
            else median = volumes[count / 2];

            return (int)Math.Max(cfg.change_min_size, median * cfg.change_relative_mul);
        }

        private readonly record struct SideComparisonResult(
            List<OrderBookChangeEvent> Changes,
            double AddSum,
            double PullSum,
            double ReplenishSum,
            double MaxAddDelta,
            double MaxPullDelta,
            decimal? MaxAddPrice,
            decimal? MaxPullPrice);

        private SideComparisonResult CompareSideOptimized(
            SymbolContext ctx, Best5DeltaEvent best5, DateTime startTime, DateTime endTime,
            Dictionary<decimal, int> oldState, Dictionary<decimal, int> newState,
            bool isBid, int wallThreshold, int dynamicThreshold, OrderBookEngineOptions cfg, int effectiveMinTrade, bool isStale,
            Dictionary<decimal, double> matchedVols, double quality, OrderBookQualityTier tier, List<object> detectorEvents, bool allowIceberg,
            bool priceMovedSide) // [New] Pass detectorEvents
        {
            var changes = new List<OrderBookChangeEvent>();
            double addSum = 0, pullSum = 0, replSum = 0;
            double maxAddDelta = 0, maxPullDelta = 0;
            decimal? maxAddPrice = null, maxPullPrice = null;

            // (A) Price-aware gating: best1 改價，避免 price shift 被誤判為 Add/Pull
            if (priceMovedSide)
                return new SideComparisonResult(changes, 0, 0, 0, 0, 0, null, null);

            // (B) 取本輪 best1 價供「被吃掉」判定（只有 best1 才承認成交消耗）
            decimal bestPriceThisSide = isBid
                ? (newState.Count > 0 ? newState.Keys.Max() : 0m)
                : (newState.Count > 0 ? newState.Keys.Min() : 0m);

            // (C) 遍歷 old∪new 全價位，做 Trade-aware 淨化
            var allPrices = new HashSet<decimal>(oldState.Keys);
            foreach (var p in newState.Keys) allPrices.Add(p);

            foreach (var price in allPrices)
            {
                oldState.TryGetValue(price, out var oldQty);
                newState.TryGetValue(price, out var newQty);
                int rawDelta = newQty - oldQty;

                matchedVols.TryGetValue(price, out var volAtPrice);
                bool hitThisPrice = volAtPrice > 0;
                bool isBest1Here = bestPriceThisSide != 0m && price == bestPriceThisSide;

                double add = 0, pull = 0, repl = 0;

                if (hitThisPrice)
                {
                    // [Restore] Iceberg Detection (Per-Level)
                    if (allowIceberg)
                    {
                        // [SSOT] Per-level tick size
                        var levelTick = _priceTier.GetTickPrice(price);

                        var iceEvents = _icebergDetector.Update(
                            best5.Symbol, price, isBid, (int)volAtPrice, rawDelta,
                            best5.ArrivalTs, isStale, quality, levelTick);
                        detectorEvents.AddRange(iceEvents);
                    }
                    // 成交只在 best1 才視為「被吃掉」；否則不承認（避免把穿價等結構性效應誤算）
                    double consumedByTrade = isBest1Here ? Math.Min(oldQty, volAtPrice) : 0;

                    // Replenish：被吃掉後又補回的量（冰山/補貨核心特徵）
                    // 期望：newQty ≈ oldQty - consumed + add/repl - pull
                    // repl 應該是 rawDelta + consumed 之正部份
                    repl = Math.Max(0, rawDelta + consumedByTrade);

                    // Pull：扣掉成交「被吃掉」部分後仍為淨減少者
                    pull = Math.Max(0, (-rawDelta) - consumedByTrade);

                    // Add：扣除 Replenish 後的淨新增
                    add = Math.Max(0, rawDelta - (int)repl);
                }
                else
                {
                    // 無成交影響 → 純 Order Book 變動
                    add = Math.Max(0, rawDelta);
                    pull = Math.Max(0, -rawDelta);
                    repl = 0;
                }

                if (add > 0 || pull > 0 || repl > 0)
                {
                    addSum += add;
                    pullSum += pull;
                    replSum += repl;

                    // 統計最大變化（用於 BigHang/BigPull 判斷）
                    if (add > maxAddDelta) { maxAddDelta = add; maxAddPrice = price; }
                    if (pull > maxPullDelta) { maxPullDelta = pull; maxPullPrice = price; }

                    // 這裡維持你原本的 Change 記錄（若 UI/分析要用）
                    var action = DetermineAction((int)volAtPrice, rawDelta);
                    changes.Add(new OrderBookChangeEvent(
                        price, rawDelta, action, isBid));
                }
            }

            // LiquidityFlowDetector call is handled by the caller (OnBest5).
            // This method returns the comparison statistics and changes.

            return new SideComparisonResult(changes, addSum, pullSum, replSum, maxAddDelta, maxPullDelta, maxAddPrice, maxPullPrice);
        }

        private static bool IsBetterPrice(decimal candidate, decimal? currentBest, decimal? anchorPrice, bool isBid)
        {
            if (anchorPrice == null) return currentBest == null;
            if (currentBest == null) return true;

            decimal anchor = anchorPrice.Value;
            decimal currentDistance = Math.Abs(currentBest.Value - anchor);
            decimal candidateDistance = Math.Abs(candidate - anchor);

            if (candidateDistance < currentDistance) return true;
            if (candidateDistance > currentDistance) return false;

            // Equal distance: prefer price closer to aggressive side (higher bid, lower ask)
            return isBid ? candidate > currentBest.Value : candidate < currentBest.Value;
        }

        private static decimal? GetAnchorPrice(Best5DeltaEvent best5, bool isBid)
        {
            if (isBid && best5.Bids.Count > 0) return best5.Bids[0].Price;
            if (!isBid && best5.Asks.Count > 0) return best5.Asks[0].Price;
            return null;
        }

        private OrderBookAction DetermineAction(int matchedVol, int delta)
        {
            if (delta > 0) return OrderBookAction.Add;
            // 如果減量跟成交量差異不大 (誤差 20% 內)，視為成交執行
            if (matchedVol > 0 && Math.Abs(Math.Abs(delta) - matchedVol) < matchedVol * 0.2)
                return OrderBookAction.TradeExecution;

            // 否則視為撤單
            return OrderBookAction.Cancel;
        }

        private class SymbolContext
        {
            public object SyncRoot { get; } = new();
            public OrderBookStats Stats { get; } = new();

            public DateTime CurrentSessionStart { get; set; } = DateTime.MinValue;
            public DateTime LastTradeCacheSweep { get; set; } = DateTime.MinValue;

            public Dictionary<decimal, int> CurrentBids { get; set; } = new();
            public Dictionary<decimal, int> CurrentAsks { get; set; } = new();
            public decimal? LastBestBidPrice { get; set; }
            public int LastBestBidQty { get; set; }
            public decimal? LastBestAskPrice { get; set; }
            public int LastBestAskQty { get; set; }

            public AggregatedState? PendingAggregation { get; set; }

            public bool LastWasSimulated { get; set; } = true;
            public bool IsFirstTradeProcessed { get; set; }

            public ConcurrentDictionary<decimal, ConcurrentQueue<(int Vol, ArrivalTs Ts)>> RecentTrades { get; } = new();

            public ArrivalTs LastBest5ArrivalTs { get; set; }
            public Dictionary<decimal, int> IcebergSuspects { get; } = new();
            public double CumulativeOFI { get; set; } = 0;

            public TimeDecayStats OfiFast { get; } = new TimeDecayStats(1.0);
            public TimeDecayStats OfiMedium { get; } = new TimeDecayStats(10.0);
            public TimeDecayStats OfiSlow { get; } = new TimeDecayStats(60.0);

            public DateTime? LastOfiUpdateTime { get; set; }

            public OrderBookQualityGate QualityGate { get; } = new();

            public OrderBookQualityTier LastQualityTier { get; set; } = OrderBookQualityTier.Good;
            public string? LastLargeThresholdReason { get; set; }
            public int LastLoggedOpeningSampleCount { get; set; } = -1;

            // --- SSOT for background liquidity ---
            public readonly MuNTrade.Core.Common.FlowRollingPercentile AddPctl = new(256);
            public readonly MuNTrade.Core.Common.FlowRollingPercentile PullPctl = new(256);
            public MuNTrade.Abstractions.Dtos.OrderBook.FlowWindowStats LastFlowStats = MuNTrade.Abstractions.Dtos.OrderBook.FlowWindowStats.Zero;
        }

        private void CalculateAndPublishOFI(SymbolContext ctx, Best5DeltaEvent next)
        {
            if (next.Bids.Count == 0 || next.Asks.Count == 0) return;

            var currentBidPrice = next.Bids[0].Price;
            var currentBidQty = next.Bids[0].Volume;
            var currentAskPrice = next.Asks[0].Price;
            var currentAskQty = next.Asks[0].Volume;

            if (ctx.LastBestBidPrice == null || ctx.LastBestAskPrice == null)
            {
                ctx.LastBestBidPrice = currentBidPrice;
                ctx.LastBestBidQty = currentBidQty;
                ctx.LastBestAskPrice = currentAskPrice;
                ctx.LastBestAskQty = currentAskQty;

                ctx.LastOfiUpdateTime = next.ArrivalTs.Value;
                return;
            }

            double e_n = 0;

            // Bid Side
            if (currentBidPrice > ctx.LastBestBidPrice)
                e_n += currentBidQty;
            else if (currentBidPrice == ctx.LastBestBidPrice)
                e_n += (currentBidQty - ctx.LastBestBidQty);
            else
                e_n -= ctx.LastBestBidQty;

            // Ask Side
            if (currentAskPrice < ctx.LastBestAskPrice)
                e_n -= currentAskQty;
            else if (currentAskPrice == ctx.LastBestAskPrice)
                e_n -= (currentAskQty - ctx.LastBestAskQty);
            else
                e_n += ctx.LastBestAskQty;

            var now = next.ArrivalTs.Value;
            var lastTime = ctx.LastOfiUpdateTime ?? now;
            double deltaSeconds = (now - lastTime).TotalSeconds;

            if (deltaSeconds < 0) deltaSeconds = 0;
            if (deltaSeconds > 60) deltaSeconds = 60;

            ctx.OfiFast.Update(e_n, deltaSeconds);
            ctx.OfiMedium.Update(e_n, deltaSeconds);
            ctx.OfiSlow.Update(e_n, deltaSeconds);

            ctx.CumulativeOFI += e_n;

            ctx.LastOfiUpdateTime = now;
            ctx.LastBestBidPrice = currentBidPrice;
            ctx.LastBestBidQty = currentBidQty;
            ctx.LastBestAskPrice = currentAskPrice;
            ctx.LastBestAskQty = currentAskQty;

            _bus.Publish(new OrderFlowImbalanceEvent(
                next.Symbol,
                next.ArrivalTs,
                e_n,
                ctx.CumulativeOFI,
                ctx.OfiFast.Value,
                ctx.OfiMedium.Value,
                ctx.OfiSlow.Value
            )
            {
                OccurredAt = new DateTimeOffset(next.ArrivalTs.Value)
            });
        }

        private void CleanUpTradeCache(SymbolContext ctx, ArrivalTs now)
        {
            if ((now.Value - ctx.LastTradeCacheSweep).TotalMilliseconds < 200) return;
            ctx.LastTradeCacheSweep = now.Value;

            foreach (var key in ctx.RecentTrades.Keys)
            {
                if (ctx.RecentTrades.TryGetValue(key, out var queue))
                {
                    while (queue.TryPeek(out var item) && (now - item.Ts) > TimeSpan.FromSeconds(2)) queue.TryDequeue(out _);
                    if (queue.IsEmpty) ctx.RecentTrades.TryRemove(key, out _);
                }
            }
        }

        private Dictionary<decimal, int> ToDictionary(IEnumerable<PriceLevel> levels)
        {
            var dict = new Dictionary<decimal, int>();
            if (levels == null) return dict;
            foreach (var lvl in levels)
            {
                if (lvl != null && lvl.Volume > 0) dict[lvl.Price] = lvl.Volume;
            }
            return dict;
        }

        public void OnJoinerHealth(JoinerHealthEvent e)
        {
            var ctx = _contexts.GetOrAdd(e.Symbol, _ => new SymbolContext());
            lock (ctx.SyncRoot)
            {
                // Update orphan rate and skew from JoinerHealthEvent.
                ctx.QualityGate.UpdateJoinerStats(e.OrphanRate);
                // Joiner sends latency in Milliseconds. Scale to seconds if needed or update Gate to accept ms?
                // QualityGate.UpdateSkew expects SECONDS.
                ctx.QualityGate.UpdateSkew(e.P95LatencyMs / 1000.0);
            }
        }

        private class AggregatedState
        {
            public string Symbol { get; init; } = "";
            public ExchangeTs ExchangeTs { get; init; }
            public ArrivalTs ArrivalTs { get; set; }
            public AggressorSide Side { get; init; }
            public decimal Price { get; set; }
            public decimal MinPrice { get; set; }
            public decimal MaxPrice { get; set; }
            public int Volume { get; set; }
            public bool IsReopenTrade { get; init; }
        }

        // Public accessor for UI ViewModel to query badges
        public IIcebergTracker Tracker => _icebergTracker;
    }
}

