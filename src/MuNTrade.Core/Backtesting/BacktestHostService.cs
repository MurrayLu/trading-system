using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Core.Settings;
using MuNTrade.Core.Capital;
using System.Linq;

namespace MuNTrade.Core.Backtesting
{
    /// <summary>
    /// Hosted service to run BacktestHost and stop application when done.
    /// </summary>
    public sealed class BacktestHostService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<BacktestHostService> _logger;
        private readonly IOptionsMonitor<ApplicationSettings> _appSettings;
        private readonly IOrderGateway _orderGateway;
        private readonly IOptionsMonitor<BacktestSettings> _backtestSettings;
        private readonly IOptionsMonitor<DayTradingRules> _dayRules;
        private readonly IOptionsMonitor<CapitalControlOptions> _capitalOptions;

        public BacktestHostService(
            IServiceScopeFactory scopeFactory,
            IHostApplicationLifetime lifetime,
            ILogger<BacktestHostService> logger,
            IOptionsMonitor<ApplicationSettings> appSettings,
            IOrderGateway orderGateway,
            IOptionsMonitor<BacktestSettings> backtestSettings,
            IOptionsMonitor<DayTradingRules> dayRules,
            IOptionsMonitor<CapitalControlOptions> capitalOptions)
        {
            _scopeFactory = scopeFactory;
            _lifetime = lifetime;
            _logger = logger;
            _appSettings = appSettings;
            _orderGateway = orderGateway;
            _backtestSettings = backtestSettings;
            _dayRules = dayRules;
            _capitalOptions = capitalOptions;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                BacktestSafetyGuard.EnsurePaperOnly(_appSettings.CurrentValue);
                BacktestSafetyGuard.EnsureBacktestGateway(_orderGateway);
                using var scope = _scopeFactory.CreateScope();
                var host = scope.ServiceProvider.GetRequiredService<BacktestHost>();
                await host.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BacktestHostService] Backtest failed.");
            }
            finally
            {
                _lifetime.StopApplication();
            }
        }

        public async Task<BacktestResult> RunAsync(
            BacktestRunRequest request,
            IProgress<BacktestProgress>? progress,
            CancellationToken ct)
        {
            BacktestSafetyGuard.EnsurePaperOnly(_appSettings.CurrentValue);
            BacktestSafetyGuard.EnsureBacktestGateway(_orderGateway);

            ApplyRequestOverrides(request);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var host = scope.ServiceProvider.GetRequiredService<BacktestHost>();
                return await host.RunAsync(request, progress, ct);
            }
            catch (OperationCanceledException)
            {
                return new BacktestResult { Succeeded = false };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BacktestHostService] Backtest failed.");
                return new BacktestResult
                {
                    Succeeded = false,
                    Errors = { ex.Message }
                };
            }
        }

        [Obsolete("Legacy backtest request override path. Transitional only.")]
        private void ApplyRequestOverrides(BacktestRunRequest request)
        {
            var backtest = _backtestSettings.CurrentValue;
            backtest.ReportMode = request.ReportMode;
            backtest.Stage = request.Stage;
            backtest.TimeMode = request.TimeMode;
            backtest.UniverseMode = request.UniverseMode;
            backtest.TurnoverTopN = request.TurnoverTopN;
            if (!string.IsNullOrWhiteSpace(request.Symbol))
                backtest.StockIds = new System.Collections.Generic.List<string> { request.Symbol.Trim() };
            else
                backtest.StockIds = request.ManualList ?? new System.Collections.Generic.List<string>();
            backtest.StartDate = request.StartDate != default ? request.StartDate.ToString("yyyy-MM-dd") : backtest.StartDate;
            backtest.EndDate = request.EndDate != default ? request.EndDate.ToString("yyyy-MM-dd") : backtest.EndDate;
            backtest.StrategyTemplateId = request.StrategyTemplateId;
            backtest.AccountKey = request.AccountKey;
            backtest.ReportRoot = request.ReportRoot;
            backtest.SkipMissingData = request.SkipMissingData;
            backtest.Risk = request.Risk ?? new BacktestRiskOptions();
            backtest.Execution = request.Execution ?? new BacktestExecutionOptions();
            var stockFill = backtest.FillSettings.Stocks;
            stockFill.CommissionRate = backtest.Execution.Fees;
            stockFill.TaxRate = backtest.Execution.Tax;
            if (backtest.Execution.ConservativeSlippageTicks > 0)
                stockFill.SlippagePoints = backtest.Execution.ConservativeSlippageTicks;

            var futFill = backtest.FillSettings.Futures;
            futFill.CommissionRate = backtest.Execution.Fees;
            futFill.TaxRate = backtest.Execution.Tax;
            if (backtest.Execution.ConservativeSlippageTicks > 0)
                futFill.SlippagePoints = backtest.Execution.ConservativeSlippageTicks;

            if (backtest.Risk.MaxTradesPerDay > 0)
                _dayRules.CurrentValue.DailyTradeLimit = backtest.Risk.MaxTradesPerDay;
            if (backtest.Risk.ConsecutiveLossLimit > 0)
                _dayRules.CurrentValue.MaxConsecutiveLosses = backtest.Risk.ConsecutiveLossLimit;

            if (backtest.Risk.DailyLossLimit > 0)
                OverrideDailyLossLimit(backtest.Risk.DailyLossLimit);
        }

        private void OverrideDailyLossLimit(decimal dailyLossLimit)
        {
            var sims = CapitalConfigService.LoadAllSimAccounts();
            var acc = sims.FirstOrDefault(x => x.IsActive) ?? sims.FirstOrDefault();
            if (acc == null) return;

            var stockBase = Math.Max(1m, acc.StockBalance);
            var futuresBase = Math.Max(1m, acc.FuturesBalance);
            var stockPct = dailyLossLimit / stockBase;
            var futuresPct = dailyLossLimit / futuresBase;

            var stockRules = _capitalOptions.CurrentValue.StockRules;
            stockRules.PhaseRules = new System.Collections.Generic.List<PhaseRule>
            {
                new PhaseRule
                {
                    Name = "BacktestOverride",
                    MinEquity = 0m,
                    MaxEquity = decimal.MaxValue,
                    RiskPercent = stockPct
                }
            };

            var futuresRules = _capitalOptions.CurrentValue.FuturesRules;
            futuresRules.PhaseRules = new System.Collections.Generic.List<PhaseRule>
            {
                new PhaseRule
                {
                    Name = "BacktestOverride",
                    MinEquity = 0m,
                    MaxEquity = decimal.MaxValue,
                    RiskPercent = futuresPct
                }
            };
        }
    }
}

