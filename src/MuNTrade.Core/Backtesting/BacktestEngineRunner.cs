// MuNTrade.Core/Backtesting/BacktestEngineRunner.cs

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Core.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Core.Backtesting
{
    /// <summary>
    /// 在 UI 模式下，負責讀取 BacktestSettings 並驅動 BacktestEngine 播放資料。
    /// </summary>
    [Obsolete("Legacy")]
    public class BacktestEngineRunner
    {
        private readonly ILogger<BacktestEngineRunner> _logger;
        private readonly IBacktestEngine _backtestEngine;
        private readonly IOptions<BacktestSettings> _backtestSettings;

        public BacktestEngineRunner(
            ILogger<BacktestEngineRunner> logger,
            IBacktestEngine backtestEngine,
            IOptions<BacktestSettings> backtestSettings)
        {
            _logger = logger;
            _backtestEngine = backtestEngine;
            _backtestSettings = backtestSettings;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            // 從 BacktestSettings 建立回測區段
            var segments = BuildSegmentsFromSettings();
            _logger.LogInformation("回測引擎運行器啟動，準備執行 {Count} 個回測區段...", segments.Count);

            if (segments.Any())
            {
                await _backtestEngine.RunAsync(segments, ct);
            }

            _logger.LogInformation("所有回測區段執行完畢。");
        }

        private List<BacktestSegment> BuildSegmentsFromSettings()
        {
            var settings = _backtestSettings.Value;
            var segments = new List<BacktestSegment>();

            if (settings.StockIds == null || !settings.StockIds.Any())
            {
                return segments;
            }

            var startDate = DateOnly.Parse(settings.StartDate);
            var endDate = DateOnly.Parse(settings.EndDate);

            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                foreach (var stockId in settings.StockIds)
                {
                    segments.Add(new BacktestSegment(
                        stockId,
                        date,
                        TimeOnly.MinValue, // 從一天開始
                        TimeOnly.MaxValue  // 到一天結束
                    ));
                }
            }
            return segments;
        }
    }
}
