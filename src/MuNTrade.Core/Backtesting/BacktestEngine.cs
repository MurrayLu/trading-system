// 檔案路徑: MuNTrade.Core/Backtesting/BacktestEngine.cs

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
        private readonly TradeLoggerService _tradeLogger; // ★ 新增：用於計算最終績效
        private readonly IEventBus _eventBus;

        // ★ 核心變化：不再直接依賴 OrderExecutionService 或 StrategyEngineService。
        // BacktestEngine 的職責是驅動數據流，而其他服務會自己訂閱事件來工作。

        public BacktestEngine(
            IClock clock,
            ITickSource tickSource,
            ILogger<BacktestEngine> logger,
            IPositionManagerService positionManager,
            SessionFilterService sessionFilter,
            KLineBuilderService klineBuilder,
            TradeLoggerService tradeLogger, // ★ 新增
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

        /// <summary>
        /// ★ 修正：方法簽章與 IBacktestEngine 介面完全一致
        /// </summary>
        public async Task<BacktestPerformance> RunAsync( // ★ 修正回傳類型為 BacktestPerformance
            IEnumerable<BacktestSegment> segments, // ★ 修正參數類型為 BacktestSegment
            CancellationToken ct = default)
        {
            _logger.LogInformation("--- Starting Full Backtest Run ---");

            foreach (var segment in segments)
            {
                if (ct.IsCancellationRequested) break;

                // ★ 修正：使用 BacktestSegment 的 Symbol 和 Date 屬性
                _logger.LogInformation("--- Starting Segment for {StockId} on {Date} ---", segment.Symbol, segment.Date);

                // ★ 修正：根據 ITickSource 介面，組合 DateTime 參數
                var fromTime = segment.Date.ToDateTime(segment.From);
                var toTime = segment.Date.ToDateTime(segment.To);

                // ★ 修正：使用 ITickSource 的正確方法 StreamTicksAsync
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

            // ★ 修正：返回由 TradeLoggerService 計算出的真實績效物件
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
                    TargetPositionRatio = 0.0m, // ★ 目標：0% 部位，即空手
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

