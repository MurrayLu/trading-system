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

