/*using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MuNTrade.Core.Services;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Core.Backtesting
{
    public class BacktestLifetimeManager : BackgroundService
    {
        private readonly ILogger<BacktestLifetimeManager> _logger;
        private readonly BacktestCompletionService _completionService;
        private readonly TradeLoggerService _tradeLogger;

        public BacktestLifetimeManager(
            ILogger<BacktestLifetimeManager> logger,
            BacktestCompletionService completionService,
            TradeLoggerService tradeLogger)
        {
            _logger = logger;
            _completionService = completionService;
            _tradeLogger = tradeLogger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await _completionService.WaitForCompletionAsync();

                // 在所有流程結束後，才安全地產生報告
                _tradeLogger.GenerateReport();

                _logger.LogWarning("--- 回測報告已產生。您可以隨時按下 Ctrl+C 來關閉應用程式。 ---");

                // 讓這個服務永遠等待，直到使用者手動關閉，確保主控台不會自動退出
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("應用程式正在透過使用者請求關閉...");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "!!! BacktestLifetimeManager 發生未預期錯誤 !!!");
            }
        }
    }
}*/
