using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Core.Settings;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Core.Capital;
using MuNTrade.Abstractions.Contracts.Time;

namespace MuNTrade.Core.Backtesting
{
    /// <summary>
    /// Backtest: freeze DailyLossLimit for each trade date (no session scheduler).
    /// </summary>
    public sealed class BacktestDailyLossFreezer
    {
        private readonly IOptionsMonitor<CapitalControlOptions> _options;
        private readonly IEventBus _bus;
        private readonly IClock _clock;
        private readonly ILogger<BacktestDailyLossFreezer> _logger;

        public BacktestDailyLossFreezer(
            IOptionsMonitor<CapitalControlOptions> options,
            IEventBus bus,
            IClock clock,
            ILogger<BacktestDailyLossFreezer> logger)
        {
            _options = options;
            _bus = bus;
            _clock = clock;
            _logger = logger;
        }

        public void EnsureFrozen(DateOnly tradeDate)
        {
            var sims = CapitalConfigService.LoadAllSimAccounts();
            var acc = sims.FirstOrDefault(x => x.IsActive) ?? sims.FirstOrDefault();
            if (acc == null)
            {
                _logger.LogWarning("[BacktestDailyLoss] No sim account found. Skip freeze.");
                return;
            }

            bool futuresFrozen = acc.FuturesDailyLossTradeDate == tradeDate;
            bool stockFrozen = acc.StockDailyLossTradeDate == tradeDate;
            if (futuresFrozen && stockFrozen) return;

            var phases = _options.CurrentValue?.StockRules?.PhaseRules;

            if (!stockFrozen)
            {
                var baseEquity = acc.StockBalance;
                var riskPercent = ResolveRiskPercent(phases, baseEquity);
                acc.StockDailyLossBaseEquity = baseEquity;
                acc.StockDailyLossLimitFrozen = baseEquity * riskPercent;
                acc.StockDailyLossTradeDate = tradeDate;
                acc.StockDailyLossUsedFrozen = 0m;

                _logger.LogInformation("[BacktestDailyLoss] tradeDate={TradeDate} baseEquity={BaseEquity:N0} risk%={RiskPercent:P2} limit={Limit:N0} (STOCK)",
                    tradeDate.ToString("yyyy-MM-dd"), baseEquity, riskPercent, acc.StockDailyLossLimitFrozen);
            }

            if (!futuresFrozen)
            {
                var baseEquity = acc.FuturesBalance;
                var riskPercent = ResolveRiskPercent(phases, baseEquity);
                acc.FuturesDailyLossBaseEquity = baseEquity;
                acc.FuturesDailyLossLimitFrozen = baseEquity * riskPercent;
                acc.FuturesDailyLossTradeDate = tradeDate;
                acc.FuturesDailyLossUsedFrozen = 0m;

                _logger.LogInformation("[BacktestDailyLoss] tradeDate={TradeDate} baseEquity={BaseEquity:N0} risk%={RiskPercent:P2} limit={Limit:N0} (FUTURES)",
                    tradeDate.ToString("yyyy-MM-dd"), baseEquity, riskPercent, acc.FuturesDailyLossLimitFrozen);
            }

            CapitalConfigService.SaveSimAccount(acc, _clock);
            _bus.Publish(new SimAccountChangedEvent(acc.Name));
        }

        private static decimal ResolveRiskPercent(IReadOnlyList<PhaseRule>? phases, decimal equity)
        {
            const decimal defaultRiskPercent = 0.01m;
            if (phases == null || phases.Count == 0) return defaultRiskPercent;

            var phase = phases.FirstOrDefault(p => equity >= p.MinEquity && equity < p.MaxEquity);
            if (phase != null) return phase.RiskPercent;

            if (equity >= phases.Last().MaxEquity) return phases.Last().RiskPercent;
            return defaultRiskPercent;
        }
    }
}

