using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Market;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Helpers;

namespace MuNTrade.Core.Services.Market
{
    /// <summary>
    /// 核心市場資訊快取服務。
    /// 負責訂閱各類市場事件 (Ticks, Best5, QuoteSnapshot)，
    /// 維護 Symbol -> MarketInfo 的映射，作為全系統的單一真相。
    /// </summary>
    public class MarketInfoCacheService : IMarketInfoProvider, IHostedService, IDisposable
    {
        private readonly ConcurrentDictionary<string, MarketInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly IEventBus _eventBus;
        private readonly Microsoft.Extensions.Logging.ILogger<MarketInfoCacheService> _logger;
        private readonly List<IDisposable> _subscriptions = new();

        public MarketInfoCacheService(IEventBus eventBus, Microsoft.Extensions.Logging.ILogger<MarketInfoCacheService> logger)
        {
            _eventBus = eventBus;
            _logger = logger;
        }

        /// <summary>
        /// UI 專用：嘗試取得市場資訊，不拋錯、不 log。
        /// </summary>
        public bool TryGetMarketInfo(string symbol, out MarketInfo info)
        {
            info = MarketInfo.Unknown(symbol ?? "");
            if (string.IsNullOrWhiteSpace(symbol)) return false;

            if (_cache.TryGetValue(symbol, out var cached) && cached != null)
            {
                info = cached;
                // 只有當 MarketNo 不是 Unknown 時才視為有效
                return info.MarketNo != MarketNo.Unknown;
            }
            return false; // 不 log，不 throw
        }

        /// <summary>
        /// Risk/Execution 專用：取得市場資訊，Unknown 時根據用途決定是否拋錯。
        /// </summary>
        public MarketInfo GetMarketInfoOrThrow(string symbol, MarketInfoPurpose purpose)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                if (purpose is MarketInfoPurpose.Risk or MarketInfoPurpose.Execution)
                    throw new InvalidOperationException($"StrictMode: Empty symbol for {purpose}");
                return MarketInfo.Unknown("");
            }

            if (TryGetMarketInfo(symbol, out var info))
                return info;

            if (purpose is MarketInfoPurpose.Risk or MarketInfoPurpose.Execution)
            {
                throw new InvalidOperationException(
                    $"StrictMode: Missing MarketNo. Symbol={symbol}, Purpose={purpose}. " +
                    $"（嚴格模式：缺少 MarketNo；Symbol={symbol}；用途={purpose}。此商品必須先訂閱行情或存在於持倉快照中。）");
            }

            // UI 理論上不該呼叫 OrThrow，但保險回 Unknown
            return MarketInfo.Unknown(symbol);
        }

        // Patch A: Throttling & Counting
        private readonly ConcurrentDictionary<string, long> _lastWarnTicks = new(StringComparer.OrdinalIgnoreCase);
        private long _legacyGetCalls;
        private long _legacyLastReportTicks;
        private static readonly long WarnIntervalTicks = TimeSpan.FromSeconds(30).Ticks;
        private static readonly long ReportIntervalTicks = TimeSpan.FromSeconds(60).Ticks;

        /// <summary>
        /// [Obsolete] 請改用 TryGetMarketInfo 或 GetMarketInfoOrThrow
        /// </summary>
        [Obsolete("Use TryGetMarketInfo (UI) or GetMarketInfoOrThrow (Risk/Execution) instead.")]
        public MarketInfo GetMarketInfo(string symbol)
        {
            // 1. Count global calls
            Interlocked.Increment(ref _legacyGetCalls);

            // 2. Report aggregate every 60s
            var now = DateTime.UtcNow.Ticks;
            var lastReport = Interlocked.Read(ref _legacyLastReportTicks);
            if (now - lastReport > ReportIntervalTicks &&
                Interlocked.CompareExchange(ref _legacyLastReportTicks, now, lastReport) == lastReport)
            {
                var count = Interlocked.Exchange(ref _legacyGetCalls, 0);
                if (count > 0)
                {
                    _logger.LogWarning("[MarketInfo] Legacy GetMarketInfo calls in last 60s = {Count}", count);
                }
            }

            if (string.IsNullOrWhiteSpace(symbol)) return MarketInfo.Unknown("");

            if (_cache.TryGetValue(symbol, out var info))
            {
                return info;
            }

            // 3. Throttle per-symbol warning (30s)
            var lastWarn = _lastWarnTicks.GetOrAdd(symbol, 0);
            if (now - lastWarn > WarnIntervalTicks)
            {
                // Simple race condition acceptable here (last writer wins)
                _lastWarnTicks[symbol] = now;
                _logger.LogWarning("[MarketInfo] StrictMode(UI legacy): Unknown MarketNo for {Symbol}.", symbol);
            }

            return MarketInfo.Unknown(symbol);
        }

        /// <summary>
        /// 從持倉快照載入市場資訊（啟動時 warm cache）。
        /// </summary>
        public void UpsertFromSnapshot(string symbol, MarketNo marketNo, MarketSource source)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            if (marketNo == MarketNo.Unknown) return;

            var kind = marketNo.ToKind();
            var info = new MarketInfo(symbol, marketNo, kind, source);
            _cache[symbol] = info;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _subscriptions.Add(_eventBus.Subscribe<TradeTickEvent>(Handle));
            _subscriptions.Add(_eventBus.Subscribe<QuoteBest5Event>(Handle));
            _subscriptions.Add(_eventBus.Subscribe<QuoteSnapshotEvent>(Handle));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Dispose();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            foreach (var sub in _subscriptions) sub.Dispose();
            _subscriptions.Clear();
        }

        private void Handle(TradeTickEvent evt) => UpdateCache(evt.Symbol, evt.MarketNo, evt.Source);
        private void Handle(QuoteBest5Event evt) => UpdateCache(evt.Symbol, evt.MarketNo, evt.Source);
        private void Handle(QuoteSnapshotEvent evt) => UpdateCache(evt.Symbol, evt.MarketNo, evt.Source);

        private void UpdateCache(string symbol, MarketNo marketNo, MarketSource source)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;

            // 若已存在且一致，則不動作 (優化)
            if (_cache.TryGetValue(symbol, out var existing))
            {
                if (existing.MarketNo == marketNo && existing.Source == source) return;
            }

            var kind = marketNo.ToKind(); // 使用唯一分類器
            var info = new MarketInfo(symbol, marketNo, kind, source);
            _cache[symbol] = info;
        }
    }
}

