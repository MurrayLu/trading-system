using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Scheduling;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Core.Capital;
using MuNTrade.Core.Services;
using MuNTrade.Core.Settings;

namespace MuNTrade.Core.Scheduling.Tasks
{
    /// <summary>
    /// 每日最大虧損上限凍結 (15:00 觸發)。
    /// 使用期貨 tradeDate 判斷是否已凍結，避免日曆日誤判。
    /// </summary>
    public sealed class FreezeDailyLossLimitTask : ITradingScheduleTask
    {
        private static readonly TimeOnly FreezeTime = new(15, 0);
        private const string FuturesSessionSymbol = "TX00";
        private const string DefaultPaperAccountKey = "Sim:Paper:All:Default";

        private readonly ITradingSessionProvider _sessionProvider;
        private readonly PositionSnapshotStore _posStore;
        private readonly IOptionsMonitor<CapitalControlOptions> _options;
        private readonly IEventBus _bus;
        private readonly IClock _clock;
        private readonly ILogger<FreezeDailyLossLimitTask> _logger;

        private DateOnly _lastSkipTradeDate = DateOnly.MinValue;
        private DateOnly _lastRunTradeDate = DateOnly.MinValue;

        public FreezeDailyLossLimitTask(
            ITradingSessionProvider sessionProvider,
            PositionSnapshotStore posStore,
            IOptionsMonitor<CapitalControlOptions> options,
            IEventBus bus,
            IClock clock,
            ILogger<FreezeDailyLossLimitTask> logger)
        {
            _sessionProvider = sessionProvider;
            _posStore = posStore;
            _options = options;
            _bus = bus;
            _clock = clock;
            _logger = logger;
        }

        public string Name => "每日虧損上限凍結 (股票/期貨)";

        public TimeSpan PollingInterval => TimeSpan.FromSeconds(30);

        public bool ShouldRun(DateTime now)
        {
            var nowTime = TimeOnly.FromDateTime(now);
            if (nowTime.Hour != FreezeTime.Hour || nowTime.Minute != FreezeTime.Minute)
                return false;

            var tradeDate = ResolveTradeDate(now, logFallback: false);
            if (tradeDate == _lastRunTradeDate)
                return false;

            var sims = CapitalConfigService.LoadAllSimAccounts();
            var activeSim = sims.FirstOrDefault(x => x.IsActive) ?? sims.FirstOrDefault();
            if (activeSim == null)
                return true;

            return !(activeSim.FuturesDailyLossTradeDate == tradeDate && activeSim.StockDailyLossTradeDate == tradeDate);
        }

        public Task ExecuteAsync(DateTime now, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.CompletedTask;

            if (TimeOnly.FromDateTime(now) < FreezeTime)
                return Task.CompletedTask;

            var sims = CapitalConfigService.LoadAllSimAccounts();
            var activeSim = sims.FirstOrDefault(x => x.IsActive) ?? sims.FirstOrDefault();
            if (activeSim == null)
            {
                _logger.LogWarning("[DailyLossFreeze] No sim account found. Skip.");
                return Task.CompletedTask;
            }

            var tradeDate = ResolveTradeDate(now, logFallback: true);
            _lastRunTradeDate = tradeDate;

            bool futuresFrozen = activeSim.FuturesDailyLossTradeDate == tradeDate;
            bool stockFrozen = activeSim.StockDailyLossTradeDate == tradeDate;
            if (futuresFrozen && stockFrozen)
            {
                if (_lastSkipTradeDate != tradeDate)
                {
                    _logger.LogInformation("[DailyLossFreeze] Skip: already frozen tradeDate={TradeDate}.", tradeDate.ToString("yyyy-MM-dd"));
                    _lastSkipTradeDate = tradeDate;
                }
                return Task.CompletedTask;
            }

            var tradeRules = CapitalConfigService.LoadTradeRules();
            var futuresSymbols = tradeRules?.Futures?.Keys?.AsEnumerable() ?? Enumerable.Empty<string>();
            var futuresSet = new HashSet<string>(futuresSymbols, StringComparer.OrdinalIgnoreCase);
            if (futuresSet.Count == 0)
            {
                _logger.LogWarning("[DailyLossFreeze] Futures rules empty. UnrealizedPnL assumed 0.");
            }

            var accountKey = DefaultPaperAccountKey;
            var positions = _posStore.LoadPositions(accountKey);
            if (positions.Count == 0)
            {
                _logger.LogInformation("[DailyLossFreeze] No positions for {AccountKey}. UnrealizedPnL assumed 0.", accountKey);
            }
            else
            {
                _logger.LogDebug("[DailyLossFreeze] Using positions from {AccountKey} (single-paper-account assumption).", accountKey);
            }

            var futuresUnrealized = positions
                .Where(p => futuresSet.Contains(p.StockId))
                .Sum(p => p.UnrealizedPnL);

            var stockUnrealized = positions
                .Where(p => !futuresSet.Contains(p.StockId))
                .Sum(p => p.UnrealizedPnL);

            var phases = _options.CurrentValue?.StockRules?.PhaseRules;

            if (!stockFrozen)
            {
                var stockBaseEquity = activeSim.StockBalance + stockUnrealized;
                var stockRiskPercent = ResolveRiskPercent(phases, stockBaseEquity);
                var stockFrozenLimit = stockBaseEquity * stockRiskPercent;

                activeSim.StockDailyLossBaseEquity = stockBaseEquity;
                activeSim.StockDailyLossLimitFrozen = stockFrozenLimit;
                activeSim.StockDailyLossTradeDate = tradeDate;
                activeSim.StockDailyLossUsedFrozen = 0m;

                _logger.LogInformation("[DailyLossFreeze] tradeDate={TradeDate} baseEquity={BaseEquity:N0} risk%={RiskPercent:P2} limit={Limit:N0} (STOCK)",
                    tradeDate.ToString("yyyy-MM-dd"), stockBaseEquity, stockRiskPercent, stockFrozenLimit);
            }

            if (!futuresFrozen)
            {
                var futuresBaseEquity = activeSim.FuturesBalance + futuresUnrealized;
                var futuresRiskPercent = ResolveRiskPercent(phases, futuresBaseEquity);
                var futuresFrozenLimit = futuresBaseEquity * futuresRiskPercent;

                activeSim.FuturesDailyLossBaseEquity = futuresBaseEquity;
                activeSim.FuturesDailyLossLimitFrozen = futuresFrozenLimit;
                activeSim.FuturesDailyLossTradeDate = tradeDate;
                activeSim.FuturesDailyLossUsedFrozen = 0m;

                _logger.LogInformation("[DailyLossFreeze] tradeDate={TradeDate} baseEquity={BaseEquity:N0} risk%={RiskPercent:P2} limit={Limit:N0} (FUTURES)",
                    tradeDate.ToString("yyyy-MM-dd"), futuresBaseEquity, futuresRiskPercent, futuresFrozenLimit);
            }

            CapitalConfigService.SaveSimAccount(activeSim, _clock);
            _bus.Publish(new SimAccountChangedEvent(activeSim.Name));

            return Task.CompletedTask;
        }

        private DateOnly ResolveTradeDate(DateTime now, bool logFallback)
        {
            var session = _sessionProvider.GetSession(FuturesSessionSymbol, now);
            if (session != null)
                return session.TradeDate;

            var td = now.TimeOfDay >= new TimeSpan(15, 0, 0) ? now.Date.AddDays(1) : now.Date;
            var tradeDate = DateOnly.FromDateTime(td);
            if (logFallback)
            {
                _logger.LogWarning("[TradeDate][Mismatch] component=FreezeTask symbol={Symbol} time={Time:yyyy-MM-dd HH:mm:ss} sourceFreeze=fallback tradeDate={TradeDate}.",
                    FuturesSessionSymbol, now, tradeDate.ToString("yyyy-MM-dd"));
            }
            return tradeDate;
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

