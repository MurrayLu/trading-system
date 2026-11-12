// MuNTrade.Core/DataProcessing/KLine/KLineBuilderService.cs (雙模式最終版)

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Domain.MarketData.Channels;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Contracts.Data;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Core.Hubs;
using MuNTrade.Core.Settings;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using KLineModel = MuNTrade.Abstractions.Domain.MarketData.KLine;

namespace MuNTrade.Core.DataProcessing.KLine
{
    // ★ 修改：繼承自 BackgroundService 以支援 LIVE 模式自動啟動
    public class KLineBuilderService : BackgroundService
    {
        private readonly ILogger<KLineBuilderService> _logger;
        private readonly IKLineBuilder _builderCore;
        private readonly IKLineHub _kLineHub;
        private readonly IEventBus _eventBus;
        private readonly IKLineStore _store;
        private readonly IClock _clock;
        private readonly ConcurrentDictionary<string, TickFeedKind> _lastFeedKind = new(StringComparer.OrdinalIgnoreCase);

        // partial 節流（per symbol-period）
        private readonly ConcurrentDictionary<string, long> _lastPartialTicks = new();
        private readonly int _partialMinIntervalMs;

        // ★ MODIFIED: Changed format from ':' to '_' for consistency.
        private static string PeriodKey(DynamicBar bar)
            => bar.Config == null ? "Unknown" : $"{bar.Config.Type}_{bar.Config.Period}";

        public IKLineBuilder BuilderCore => _builderCore;

        public KLineBuilderService(
                        ILogger<KLineBuilderService> logger,
                        IKLineHub kLineHub,
                        IKLineBuilder builderCore,
                        IEventBus eventBus,
                        IKLineStore store,
                        IClock clock,
                        IOptions<DataAggregatorSettings> aggregatorSettings)
        {
            _logger = logger;
            _kLineHub = kLineHub;
            _builderCore = builderCore;
            _eventBus = eventBus;
            _store = store;
            _clock = clock;

            _builderCore.OnBarClosed += OnBarClosed;
            _builderCore.OnBarPartial += OnBarPartial;

            _partialMinIntervalMs = aggregatorSettings?.Value?.PartialMinIntervalMs ?? 100; // 新增設定，沒設就用 100ms


            if (aggregatorSettings?.Value?.EnabledBarConfigs != null)
            {
                foreach (var config in aggregatorSettings.Value.EnabledBarConfigs)
                {
                    _builderCore.RegisterConfiguration(config);
                    _logger.LogInformation("[KLineSvc] 已註冊建構器設定: {Type} - {Period}", config.Type, config.Period);
                }
            }
        }

        /// <summary>
        /// ★ 保留：這個公開方法是給 BacktestEngine 手動呼叫的
        /// </summary>
        public void ProcessTick(TickData tick)
        {
            if (tick == null || tick.IsSimulated) return;
            _lastFeedKind[tick.StockId] = tick.FeedKind;
            _builderCore.ProcessTick(tick);
        }

        private void OnBarClosed(DynamicBar bar)
        {
            var period = PeriodKey(bar); // Use the unified helper method
            PersistBarSnapshot(bar, period);

            // ★ MODIFIED: Updated log format to include EndTime.
            /*_logger.LogDebug("[KLine Closed] {Symbol}|{Period} End={EndTime:yyyy-MM-dd HH:mm} | O={Open} H={High} L={Low} C={Close} V={Volume}",
                                 bar.StockId,
                                 period,
                                 bar.EndTime,
                                 bar.Open,
                                 bar.High,
                                 bar.Low,
                                 bar.Close,
                                 bar.Volume);*/

            // 1) 寫 Hub（保留）
            var kLine = new KLineModel(bar);
            if (!_kLineHub.Writer.TryWrite(kLine))
            {
                _logger.LogWarning("KLine Hub Channel 已滿或已關閉，一根 {StockId} 的 KLine 被丟棄。", bar.StockId);
            }

            // 2) 發「收尾事件」── 只帶 KLine
            _eventBus.Publish(new KLineClosedEvent(new KLineModel(bar))
            {
                OccurredAt = new DateTimeOffset(_clock.Now)
            });
        }

        private void OnBarPartial(DynamicBar bar)
        {
            // 節流：同一 (Symbol+Period) 至少相隔 _partialMinIntervalMs 才發
            string key = $"{bar.StockId}|{PeriodKey(bar)}";
            long now = Environment.TickCount64;
            var last = _lastPartialTicks.GetOrAdd(key, 0);

            if (now - last < _partialMinIntervalMs)
                return;

            _lastPartialTicks[key] = now;
            var period = PeriodKey(bar);
            PersistBarSnapshot(bar, period);

            // ★ MODIFIED: Updated log format to include EndTime.
            _logger.LogDebug("[KLine Partial] {Symbol}|{Period} End={EndTime:yyyy-MM-dd HH:mm} | O={Open} H={High} L={Low} C={Close} V={Volume}",
                                 bar.StockId,
                                 period,
                                 bar.EndTime,
                                 bar.Open,
                                 bar.High,
                                 bar.Low,
                                 bar.Close,
                                 bar.Volume);

            // 只帶 KLine
            var feedKind = _lastFeedKind.TryGetValue(bar.StockId, out var kind)
                ? kind
                : TickFeedKind.RealTime;

            _eventBus.Publish(new KLinePartialUpdatedEvent(new KLineModel(bar))
            {
                OccurredAt = new DateTimeOffset(_clock.Now),
                FeedKind = feedKind
            });
        }

        private void PersistBarSnapshot(DynamicBar bar, string period)
        {
            if (string.IsNullOrWhiteSpace(bar.StockId) || bar.Config is null) return;

            var record = new JsonKLineRecord
            {
                KlineTime = bar.EndTime.Value,
                Open = (double)bar.Open,
                High = (double)bar.High,
                Low = (double)bar.Low,
                Close = (double)bar.Close,
                Qty = bar.Volume
            };

            try
            {
                _store.UpsertLast(bar.StockId, period, record);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[KLineSvc] UpsertLast failed for {Symbol}|{Period}", bar.StockId, period);
            }
        }

        /// <summary>
        /// ★ 新增：這是 BackgroundService 的核心執行迴圈，供 LIVE 模式自動啟動
        /// </summary>
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // ★ 核心修正 2：
            // 從 IOptions<ApplicationSettings> 讀取運行模式
            // (您需要在建構子中注入 IOptions<ApplicationSettings> opt)
            // 假設您已經注入了...

            // 在 LIVE 模式下 (AppMode.Execution)，Tick 由 MarketDataPipeline 驅動，此服務只作為事件發佈器，
            // 其建構子中的事件訂閱 (_builderCore.OnBarClosed += ...) 已經足夠。
            // 因此，ExecuteAsync 不需要做任何事，只需保持服務存活即可。

            _logger.LogInformation("[KLineSvc] K線建構服務已啟動 (Hosted Service 模式)...");

            // 檢查是否在非 LIVE 模式 (例如 Backtest)
            // if (_appSettings.AppMode != AppMode.Execution) {
            //     _eventBus.Subscribe<TradeTickEvent>(...);
            // }

            // 對於 LIVE 模式，我們直接返回一個無限等待的任務，直到應用程式關閉。
            return Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}

