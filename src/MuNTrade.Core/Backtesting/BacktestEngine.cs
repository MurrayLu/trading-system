
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Core.DataProcessing.KLine;
using MuNTrade.Core.Services;
using MuNTrade.Core.Trading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Core.Backtesting
{
    /// <summary>
    /// 【100% 銜接版】
    /// 負責執行回測的核心引擎。
    /// </summary>
    public sealed class BacktestEngine : IBacktestEngine, IDisposable
    {
        private readonly IClock _clock;
        private readonly ITickSource _tickSource;
        private readonly ILogger<BacktestEngine> _logger;
        private readonly IPositionManagerService _positionManager;
        private readonly SessionFilterService _sessionFilter;
        private readonly KLineBuilderService _klineBuilder;
        private readonly TradeLoggerService _tradeLogger;
        private readonly IEventBus _eventBus;

        // BacktestEngine 的職責是驅動數據流，而其他服務會自己訂閱事件來工作。

        public BacktestEngine(
            IClock clock,
            ITickSource tickSource,
            ILogger<BacktestEngine> logger,
            IPositionManagerService positionManager,
            SessionFilterService sessionFilter,
            KLineBuilderService klineBuilder,
            TradeLoggerService tradeLogger,
            IEventBus eventBus)
        {
            _clock = clock;
            _tickSource = tickSource;
            _logger = logger;
            _positionManager = positionManager;
            _sessionFilter = sessionFilter;
            _klineBuilder = klineBuilder;
            _tradeLogger = tradeLogger;
            _eventBus = eventBus;
        }

        public async Task<BacktestPerformance> RunAsync(
            IEnumerable<BacktestSegment> segments,
            CancellationToken ct = default)
        {
            _logger.LogInformation("--- Starting Full Backtest Run ---");

            foreach (var segment in segments)
            {
                if (ct.IsCancellationRequested) break;

                _logger.LogInformation("--- Starting Segment for {StockId} on {Date} ---", segment.Symbol, segment.Date);

                var fromTime = segment.Date.ToDateTime(segment.From);
                var toTime = segment.Date.ToDateTime(segment.To);

                await foreach (var tick in _tickSource.StreamTicksAsync(segment.Symbol, fromTime, toTime, ct))
                {
                    _clock.AdvanceTo(tick.ExchangeTs.Value);
                    _sessionFilter.ProcessTick(tick); // ProcessTick 會觸發後續的 Bar 建立和事件發布
                }
            }

            try
            {
                ForceCloseAllOpenPositions();
                // 在真實的回測中，需要等待事件處理完成，此處簡化
                await Task.Delay(100, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during force closing positions.");
            }
            finally
            {
                _klineBuilder.BuilderCore.FlushOpenBars();
                _logger.LogInformation("--- Full Backtest Run Finished ---");
            }

            return _tradeLogger.CalculatePerformance();
        }

        /// <summary>
        /// 使用新的「目標部位」方式來觸發平倉
        /// </summary>
        private void ForceCloseAllOpenPositions()
        {
            _logger.LogWarning("[BacktestEngine] End of backtest. Forcing to close all open positions...");
            var openPositions = _positionManager.GetAllOpenPositions();

            if (!openPositions.Any()) return;

            foreach (var pos in openPositions)
            {
                if (pos.Quantity == 0) continue;

                var closingSignal = new SignalEvent
                {
                    StockId = pos.StockId,
                    SignalTime = _clock.Now,
                    Price = pos.LastPrice,
                    TargetPositionRatio = 0.0m, //  目標：0% 部位，即空手
                    Reason = "BacktestForceClose"
                };

                // 直接發布平倉訊號，PositionManager 會自動接收並處理
                _eventBus.Publish(closingSignal);
            }
        }

        public void Dispose()
        {
            // 清理資源...
        }
    }
}

