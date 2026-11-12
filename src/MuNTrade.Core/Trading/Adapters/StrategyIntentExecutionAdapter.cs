using System;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Core.StrategyRuntime;
using MuNTrade.Abstractions.Contracts.Time;
using Microsoft.Extensions.Options;
using MuNTrade.Core.Settings;
using MuNTrade.Core.Capital;
using System.Linq;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Core.Backtesting;

namespace MuNTrade.Core.Trading.Adapters
{
    /// <summary>
    /// Bridges StrategyIntentEmittedEvent -> Risk check -> Execution.
    /// Keeps the intent pipeline observable with cid logs.
    /// </summary>
    public sealed class StrategyIntentExecutionAdapter : IDisposable
    {
        private readonly IEventBus _eventBus;
        private readonly ITradeExecutionManager _executionManager;
        private readonly ITradingSessionProvider _sessionProvider;
        private readonly IClock _clock;
        private readonly IOptionsMonitor<BacktestSettings> _backtest;
        private readonly IOptions<StrategySettings> _strategySettings;
        private readonly ILogger<StrategyIntentExecutionAdapter> _logger;
        private readonly IBacktestRiskGate? _backtestRiskGate;
        private readonly IDisposable _token;

        public StrategyIntentExecutionAdapter(
            IEventBus eventBus,
            ITradeExecutionManager executionManager,
            ITradingSessionProvider sessionProvider,
            IClock clock,
            IOptionsMonitor<BacktestSettings> backtest,
            IOptions<StrategySettings> strategySettings,
            ILogger<StrategyIntentExecutionAdapter> logger,
            IBacktestRiskGate? backtestRiskGate = null)
        {
            _eventBus = eventBus;
            _executionManager = executionManager;
            _sessionProvider = sessionProvider;
            _clock = clock;
            _backtest = backtest;
            _strategySettings = strategySettings;
            _logger = logger;
            _backtestRiskGate = backtestRiskGate;

            _token = _eventBus.Subscribe<StrategyIntentEmittedEvent>(OnIntent);
        }

        private void OnIntent(StrategyIntentEmittedEvent evt)
        {
            try
            {
                if (evt.Intent is not OrderIntent intent) return;

                var cid = string.IsNullOrWhiteSpace(intent.CorrelationId)
                    ? Guid.NewGuid().ToString("N")
                    : intent.CorrelationId;

                if (string.IsNullOrWhiteSpace(intent.CorrelationId))
                {
                    intent = intent with { CorrelationId = cid };
                }

                var strategyId = string.IsNullOrWhiteSpace(intent.StrategyId)
                    ? StrategyIdentity.BuildStrategyId(evt.TemplateId.Value, evt.InstanceId.Value)
                    : intent.StrategyId;

                if (!string.Equals(intent.StrategyId, strategyId, StringComparison.Ordinal))
                {
                    intent = intent with { StrategyId = strategyId };
                }

                var decision = EvaluateIntent(intent);
                _logger.LogInformation(
                    "[Risk] cid={Cid} sid={Sid} decision={Decision} reason={Reason} dailyLossRemaining={Remaining:N0} stage={Stage}",
                    cid,
                    strategyId,
                    decision.IsApproved ? "Accepted" : "Rejected",
                    decision.Reason,
                    decision.DailyLossRemaining,
                    decision.Stage);

                _eventBus.Publish(new StrategyPacketEmittedEvent(
                    evt.TemplateId,
                    evt.InstanceId,
                    StrategyScope.Symbol,
                    intent.Symbol,
                    StrategyCategory.Risk,
                    decision.IsApproved ? StrategySeverity.Info : StrategySeverity.Warning,
                    ActionCode: "RISK_DECISION",
                    HumanReason: $"decision={(decision.IsApproved ? "Accepted" : "Rejected")} reason={decision.Reason} remaining={decision.DailyLossRemaining:N0} stage={decision.Stage}",
                    OccurredAt: _clock.Now
                ));

                if (!decision.IsApproved && IsEntryIntent(intent))
                {
                    var entryModel = ExtractEntryModel(intent.IntentKind);
                    _eventBus.Publish(new StrategyPacketEmittedEvent(
                        evt.TemplateId,
                        evt.InstanceId,
                        StrategyScope.Symbol,
                        intent.Symbol,
                        StrategyCategory.Risk,
                        StrategySeverity.Warning,
                        ActionCode: "SUPPRESSED_BY_RISK",
                        HumanReason: $"WouldHaveEntered={entryModel} intentKind={intent.IntentKind} reason={decision.Reason} stage={decision.Stage}",
                        OccurredAt: _clock.Now
                    ));
                }

                if (!decision.IsApproved) return;

                if (IsEntryIntent(intent) && _backtestRiskGate != null)
                {
                    var gateDecision = _backtestRiskGate.Evaluate(intent);
                    if (!gateDecision.IsApproved)
                    {
                        var actionCode = gateDecision.Behavior == BacktestOverLossBehavior.LogOnly
                            ? "RISK_LOGONLY"
                            : "SUPPRESSED_BY_RISK";

                        _eventBus.Publish(new StrategyPacketEmittedEvent(
                            evt.TemplateId,
                            evt.InstanceId,
                            StrategyScope.Symbol,
                            intent.Symbol,
                            StrategyCategory.Risk,
                            StrategySeverity.Warning,
                            ActionCode: actionCode,
                            HumanReason: $"BacktestRiskGate reason={gateDecision.Reason} behavior={gateDecision.Behavior}",
                            OccurredAt: _clock.Now
                        ));

                        if (gateDecision.Behavior == BacktestOverLossBehavior.Lock)
                            return;
                    }
                }

                _ = _executionManager.SubmitAsync(intent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle StrategyIntentEmittedEvent.");
            }
        }

        private RiskDecision EvaluateIntent(OrderIntent intent)
        {
            if (string.IsNullOrWhiteSpace(intent.AccountKey))
                return RiskDecision.Reject("Intent missing AccountKey");

            if (intent.MarketKind == MarketKind.Unknown)
                return RiskDecision.Reject("Intent missing MarketKind (Market Truth not resolved)");

            if (intent.Plan == null || intent.Plan.Quantity <= 0)
                return RiskDecision.Reject("Intent missing Plan/Quantity");

            var stage = _backtest.CurrentValue?.Stage ?? "Explore";

            var dailyLossDecision = EvaluateDailyLoss(intent, stage);
            if (!dailyLossDecision.IsApproved)
                return dailyLossDecision;

            if (IsStrict(stage) && IsEntryIntent(intent))
            {
                var now = _clock.Now.TimeOfDay;
                var settings = _strategySettings.Value;
                if (now < settings.FirstEntryTime || now > settings.LastEntryTime)
                {
                    return RiskDecision.Reject($"Entry window blocked ({settings.FirstEntryTime:hh\\:mm\\:ss}-{settings.LastEntryTime:hh\\:mm\\:ss})", stage, dailyLossDecision.DailyLossRemaining);
                }
            }

            return RiskDecision.Approve(stage, dailyLossDecision.DailyLossRemaining);
        }

        private RiskDecision EvaluateDailyLoss(OrderIntent intent, string stage)
        {
            if (IsExitIntent(intent))
                return RiskDecision.Approve(stage, 0);

            var sims = CapitalConfigService.LoadAllSimAccounts();
            if (sims.Count == 0)
                return RiskDecision.Approve(stage, 0);

            var acc = ResolveSimAccount(sims, intent.AccountKey);
            if (acc == null)
                return RiskDecision.Approve(stage, 0);

            var tradeDate = ResolveTradeDate();

            decimal limit;
            decimal used;
            DateOnly frozenDate;
            if (intent.MarketKind == MarketKind.Derivatives)
            {
                limit = acc.FuturesDailyLossLimitFrozen;
                used = acc.FuturesDailyLossUsedFrozen;
                frozenDate = acc.FuturesDailyLossTradeDate;
            }
            else
            {
                limit = acc.StockDailyLossLimitFrozen;
                used = acc.StockDailyLossUsedFrozen;
                frozenDate = acc.StockDailyLossTradeDate;
            }

            if (frozenDate != tradeDate)
            {
                _logger.LogWarning("[TradeDate][Mismatch] component=Risk mkt={Market} tradeDate={TradeDate} frozenDate={FrozenDate}",
                    intent.MarketKind, tradeDate.ToString("yyyy-MM-dd"), frozenDate.ToString("yyyy-MM-dd"));
                return RiskDecision.Reject($"DailyLoss not frozen for tradeDate={tradeDate:yyyy-MM-dd}", stage, 0);
            }

            var remaining = limit - used;
            if (remaining <= 0)
            {
                return RiskDecision.Reject($"DailyLossBreached remaining={remaining:N0} limit={limit:N0} used={used:N0} tradeDate={tradeDate:yyyy-MM-dd}", stage, remaining);
            }

            return RiskDecision.Approve(stage, remaining);
        }

        private DateOnly ResolveTradeDate()
        {
            var now = _clock.Now;
            var session = _sessionProvider.GetSession("TX00", now);
            if (session != null)
                return session.TradeDate;

            var td = now.TimeOfDay >= new TimeSpan(15, 0, 0) ? now.Date.AddDays(1) : now.Date;
            _logger.LogWarning("[TradeDate][Mismatch] component=Risk sourceRisk=fallback tradeDate={TradeDate}", DateOnly.FromDateTime(td).ToString("yyyy-MM-dd"));
            return DateOnly.FromDateTime(td);
        }

        private static SimAccountModel? ResolveSimAccount(
            System.Collections.Generic.List<SimAccountModel> accounts,
            string accountKey)
        {
            if (accounts == null || accounts.Count == 0) return null;

            var acc = accounts.FirstOrDefault(a => string.Equals(a.Id, accountKey, StringComparison.OrdinalIgnoreCase));
            if (acc != null) return acc;

            bool isDefaultKey = accountKey.Contains("Default", StringComparison.OrdinalIgnoreCase)
                             || accountKey.Contains("Paper", StringComparison.OrdinalIgnoreCase);
            if (isDefaultKey)
            {
                acc = accounts.FirstOrDefault(a => a.Name.Contains("Default", StringComparison.OrdinalIgnoreCase)
                                               || a.Name.Contains("Paper", StringComparison.OrdinalIgnoreCase));
                if (acc != null) return acc;
            }

            return accounts.Count == 1 ? accounts[0] : accounts.FirstOrDefault(a => a.IsActive) ?? accounts.FirstOrDefault();
        }

        private static bool IsEntryIntent(OrderIntent intent)
            => intent.IntentKind != null && intent.IntentKind.Contains("ENTRY", StringComparison.OrdinalIgnoreCase);

        private static bool IsExitIntent(OrderIntent intent)
            => intent.IntentKind != null && intent.IntentKind.Contains("EXIT", StringComparison.OrdinalIgnoreCase);

        private static bool IsStrict(string stage)
            => string.Equals(stage, "Strict", StringComparison.OrdinalIgnoreCase);

        private static string ExtractEntryModel(string intentKind)
        {
            if (intentKind == null) return "Unknown";
            if (intentKind.Contains("ENTRY_A", StringComparison.OrdinalIgnoreCase)) return "A";
            if (intentKind.Contains("ENTRY_B", StringComparison.OrdinalIgnoreCase)) return "B";
            return "Unknown";
        }

        public void Dispose()
        {
            _token.Dispose();
        }
    }
}

