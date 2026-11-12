using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Core.Backtesting.Universe;
using MuNTrade.Core.DataProcessing.KLine;
using MuNTrade.Core.DataProcessing.Store;
using MuNTrade.Abstractions.Contracts.Services;

namespace MuNTrade.Core.Backtesting.Warmup
{
    public sealed record WarmupCacheKey(string Symbol, TimeSpan TimeSpan);

    public sealed class WarmupCacheEntry
    {
        // 核心：使用自定義或既有的 RingBuffer (此處借用 IKLineStore 的概念)
        public CircularBuffer<DynamicBar> Buffer { get; }
        public DateOnly HydratedUntil { get; set; }
        public DateOnly? NextUseDate { get; set; }
        public DateOnly LastSeenDate { get; set; }

        public WarmupCacheEntry(int capacity)
        {
            Buffer = new CircularBuffer<DynamicBar>(capacity);
        }
    }

    // 一個簡單的固定長度 Ring Buffer 實作 (若 Core 中沒現成的先放這)
    public class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private int _head;
        private int _tail;
        private int _count;

        public int Capacity { get; }
        public int Count => _count;

        public CircularBuffer(int capacity)
        {
            Capacity = capacity;
            _buffer = new T[capacity];
        }

        public void Add(T item)
        {
            if (_count == Capacity)
            {
                _buffer[_head] = item;
                _head = (_head + 1) % Capacity;
            }
            else
            {
                _buffer[_tail] = item;
                _tail = (_tail + 1) % Capacity;
                _count++;
            }
        }

        public IEnumerable<T> GetItems()
        {
            for (int i = 0; i < _count; i++)
            {
                yield return _buffer[(_head + i) % Capacity];
            }
        }

        public void Clear()
        {
            _head = 0;
            _tail = 0;
            _count = 0;
        }
    }

    public interface IWarmupCacheManager
    {
        Task<WarmupPreparationReport> PrepareAsync(DateOnly tradeDate, IReadOnlyList<string> symbols, IReadOnlyList<TimeSpan> timeFrames, int maxLookback, BacktestUniversePlan plan, CancellationToken ct = default);
        IReadOnlyList<DynamicBar> GetWarmedUpBars(string symbol, TimeSpan timeFrame);
    }

    public sealed record WarmupPreparationReport
    {
        public DateOnly TradeDate { get; init; }
        public bool Succeeded { get; init; }
        public IReadOnlyList<WarmupMissingDataIssue> MissingIssues { get; init; } = Array.Empty<WarmupMissingDataIssue>();
    }

    public sealed record WarmupMissingDataIssue
    {
        public required string Symbol { get; init; }
        public required TimeSpan TimeFrame { get; init; }
        public DateOnly ExpectedFrom { get; init; }
        public DateOnly ExpectedTo { get; init; }
        public string? ExpectedPath { get; init; }
        public required string Reason { get; init; }
    }

    /// <summary>
    /// Phase 3: 有界增量 Warmup Cache (Bounded Tail Cache)
    /// </summary>
    public sealed class WarmupCacheManager : IWarmupCacheManager
    {
        private readonly ILogger<WarmupCacheManager> _logger;
        private readonly IKLineHistoryProvider _historyProvider;
        private readonly IHolidayCalendar _holidayCalendar;
        private readonly ConcurrentDictionary<WarmupCacheKey, WarmupCacheEntry> _cache = new();

        public WarmupCacheManager(
            ILogger<WarmupCacheManager> logger,
            IKLineHistoryProvider historyProvider,
            IHolidayCalendar holidayCalendar)
        {
            _logger = logger;
            _historyProvider = historyProvider;
            _holidayCalendar = holidayCalendar;
        }

        public async Task<WarmupPreparationReport> PrepareAsync(
            DateOnly tradeDate,
            IReadOnlyList<string> symbols,
            IReadOnlyList<TimeSpan> timeFrames,
            int maxLookback,
            BacktestUniversePlan plan,
            CancellationToken ct = default)
        {
            _logger.LogInformation("[WarmupManager] WARMUP_BEGIN for {Date}. Symbols: {Count}", tradeDate, symbols.Count);

            var capacity = (int)(maxLookback * 1.2); // +20% buffer
            var missingIssues = new List<WarmupMissingDataIssue>();

            // 1. Evict phase (預備淘汰)
            EvictStaleEntries(tradeDate, plan);

            // 2. Hydration phase (增量與冷啟動)
            foreach (var symbol in symbols)
            {
                foreach (var tf in timeFrames)
                {
                    var key = new WarmupCacheKey(symbol, tf);
                    var entry = _cache.GetOrAdd(key, _ => new WarmupCacheEntry(capacity));

                    // 更新使用資訊
                    entry.LastSeenDate = tradeDate;
                    if (plan.UsageBySymbol.TryGetValue(symbol, out var usage) && usage.NextUseByDate.TryGetValue(tradeDate, out var nextUse))
                    {
                        entry.NextUseDate = nextUse;
                    }
                    else
                    {
                        entry.NextUseDate = null;
                    }

                    // 判斷該補齊多少區間 (嚴禁跨越 tradeDate 今日開盤)
                    var targetTo = tradeDate.AddDays(-1); // 到昨天為止 (實作中以開盤前為準，這裡以日期切線概略表示)

                    if (entry.HydratedUntil < targetTo)
                    {
                        // Cache miss 或需增量
                        var needFrom = default(DateOnly);

                        if (entry.Buffer.Count == 0)
                        {
                            // 冷啟動：精確推算往回 maxLookback 個交易日，再多加幾個交易日作為緩衝
                            int tradingDaysToLookBack = (int)(maxLookback * 1.5) + 5;
                            var fromDate = tradeDate;
                            int foundDays = 0;
                            while (foundDays < tradingDaysToLookBack)
                            {
                                fromDate = fromDate.AddDays(-1);
                                if (!_holidayCalendar.IsHoliday(fromDate))
                                {
                                    foundDays++;
                                }
                            }
                            needFrom = fromDate;
                            _logger.LogTrace("[WarmupManager] Cache Miss [{Sym}|{TF}]. Cold start pulling history from {From}.", symbol, tf, needFrom);
                        }
                        else
                        {
                            // 增量：從上次 hydrated 的隔天開始補
                            needFrom = entry.HydratedUntil.AddDays(1);
                            _logger.LogTrace("[WarmupManager] Cache Hit [{Sym}|{TF}]. Incremental from {From} to {To}.", symbol, tf, needFrom, targetTo);
                        }

                        // 開始拉取資料並灌入 Buffer (實作上需要提供這支 API)
                        // 注意：這支 API 必須保證回傳的 KLine <= tradeDate 的開盤前
                        var bars = await _historyProvider.PullHistoryAsync(symbol, tf, needFrom, targetTo, maxLookback, ct);

                        foreach (var bar in bars)
                        {
                            entry.Buffer.Add(bar);
                        }

                        if (bars.Count > 0)
                        {
                            // 更新最後水位線為拉取到的最後一根 bar 的日期
                            var lastBarDate = DateOnly.FromDateTime(bars.Last().EndTime.Value.Date);
                            entry.HydratedUntil = lastBarDate > entry.HydratedUntil ? lastBarDate : entry.HydratedUntil;
                        }
                        else
                        {
                            _logger.LogWarning(
                                "[WarmupManager] No history data found for {Sym} ({Tf}) between {From} and {To}. Cache miss remains unresolved.",
                                symbol, tf, needFrom, targetTo);
                            missingIssues.Add(new WarmupMissingDataIssue
                            {
                                Symbol = symbol,
                                TimeFrame = tf,
                                ExpectedFrom = needFrom,
                                ExpectedTo = targetTo,
                                ExpectedPath = null,
                                Reason = "MissingWarmupData"
                            });
                        }
                    }
                }
            }

            _logger.LogInformation("[WarmupManager] WARMUP_END for {Date}. IND_READY.", tradeDate);
            return new WarmupPreparationReport
            {
                TradeDate = tradeDate,
                Succeeded = missingIssues.Count == 0,
                MissingIssues = missingIssues
            };
        }

        public IReadOnlyList<DynamicBar> GetWarmedUpBars(string symbol, TimeSpan timeFrame)
        {
            var key = new WarmupCacheKey(symbol, timeFrame);
            if (_cache.TryGetValue(key, out var entry))
            {
                return entry.Buffer.GetItems().ToList();
            }
            return Array.Empty<DynamicBar>();
        }

        private void EvictStaleEntries(DateOnly currentDate, BacktestUniversePlan plan)
        {
            var keysToRemove = new List<WarmupCacheKey>();

            foreach (var kvp in _cache)
            {
                var entry = kvp.Value;

                // 優先規則：已經不會再使用了 (nextUseDate = null) 且最後一次看到早於今天
                if (entry.NextUseDate == null && entry.LastSeenDate < currentDate)
                {
                    keysToRemove.Add(kvp.Key);
                }
                // 次級規則：可加上成本或 nextUseDate 過遠的淘汰邏輯（目前先實作強硬邊界淘汰）
            }

            foreach (var key in keysToRemove)
            {
                _cache.TryRemove(key, out _);
                _logger.LogTrace("[WarmupManager] Evicted stale cache for {Sym}|{TF}.", key.Symbol, key.TimeSpan);
            }
        }
    }
}

