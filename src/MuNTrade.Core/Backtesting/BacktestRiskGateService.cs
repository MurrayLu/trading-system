using System;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Core.Settings;

namespace MuNTrade.Core.Backtesting
{
    public enum BacktestOverLossBehavior
    {
        Lock = 0,
        LogOnly = 1
    }

    public sealed record BacktestRiskGateDecision(bool IsApproved, string Reason, BacktestOverLossBehavior Behavior);

    public interface IBacktestRiskGate
    {
        BacktestRiskGateDecision Evaluate(OrderIntent intent);
    }

    public sealed class BacktestRiskGateService : IBacktestRiskGate, IDisposable
    {
        private readonly IOptionsMonitor<BacktestSettings> _options;
        private readonly ITradingSessionProvider _sessionProvider;
        private readonly IClock _clock;
        private readonly IDisposable _sub;

        private DateOnly _currentTradeDate = default;
        private int _tradesToday;
        private int _consecutiveLosses;

        public BacktestRiskGateService(
            IEventBus bus,
            IOptionsMonitor<BacktestSettings> options,
            ITradingSessionProvider sessionProvider,
            IClock clock)
        {
            _options = options;
            _sessionProvider = sessionProvider;
            _clock = clock;
            _sub = bus.Subscribe<CompletedTrade>(OnTrade);
        }

        public BacktestRiskGateDecision Evaluate(OrderIntent intent)
        {
            RefreshTradeDate();

            var risk = _options.CurrentValue.Risk;
            var behavior = ResolveBehavior(risk.OverLossBehavior);

            if (risk.MaxTradesPerDay > 0 && _tradesToday >= risk.MaxTradesPerDay)
                return new BacktestRiskGateDecision(false, "MaxTradesPerDay", behavior);

            if (risk.ConsecutiveLossLimit > 0 && _consecutiveLosses >= risk.ConsecutiveLossLimit)
                return new BacktestRiskGateDecision(false, "ConsecutiveLossLimit", behavior);

            return new BacktestRiskGateDecision(true, "OK", behavior);
        }

        private void OnTrade(CompletedTrade trade)
        {
            RefreshTradeDate(trade.ExitTime);

            _tradesToday++;
            if (trade.NetPnL < 0)
                _consecutiveLosses++;
            else
                _consecutiveLosses = 0;
        }

        private void RefreshTradeDate()
        {
            RefreshTradeDate(_clock.Now);
        }

        private void RefreshTradeDate(DateTime now)
        {
            var session = _sessionProvider.GetSession("TX00", now);
            var tradeDate = session?.TradeDate ?? DateOnly.FromDateTime(now.TimeOfDay >= new TimeSpan(15, 0, 0) ? now.Date.AddDays(1) : now.Date);
            if (_currentTradeDate != tradeDate)
            {
                _currentTradeDate = tradeDate;
                _tradesToday = 0;
                _consecutiveLosses = 0;
            }
        }

        private static BacktestOverLossBehavior ResolveBehavior(string? behavior)
        {
            return string.Equals(behavior, "LogOnly", StringComparison.OrdinalIgnoreCase)
                ? BacktestOverLossBehavior.LogOnly
                : BacktestOverLossBehavior.Lock;
        }

        public void Dispose()
        {
            _sub.Dispose();
        }
    }
}

