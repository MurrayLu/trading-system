using System;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Contracts.Time;

namespace MuNTrade.Core.Services.Risk
{
    public enum RiskLockReason { None, DailyTradeLimit, MaxConsecutiveLosses, MaxLossPerTrade, MaxDrawdown, LatencyGuard, SlippageGuard }

    public sealed class RiskState
    {
        public bool IsLocked { get; init; }
        public RiskLockReason Reason { get; init; }
        public DateTime? UnlockAt { get; init; }
        public string? Message { get; init; }
    }

    public interface IRiskGuardService
    {
        RiskState Current { get; }
        event Action<RiskState>? OnRiskStateChanged;

        bool AllowNewOrder(string symbol, decimal estRisk);
        void ForceLock(RiskLockReason reason, TimeSpan? duration = null);
        void Unlock();
    }

    public sealed class RiskGuardService : IRiskGuardService
    {
        private readonly DayTradingRules _rules;
        private readonly IEventBus _bus;
        private readonly IClock _clock;

        private int _tradesUsedToday;
        private int _consecutiveLosses = 0;
        private decimal _maxDrawdownPctToday = 0;

        private RiskState _state = new() { IsLocked = false, Reason = RiskLockReason.None };
        public RiskState Current => _state;
        public event Action<RiskState>? OnRiskStateChanged;

        public RiskGuardService(IOptions<DayTradingRules> rules, IEventBus bus, IClock clock)
        {
            _rules = rules.Value;
            _bus = bus;
            _clock = clock;

            _bus.Subscribe<BrokerReportDto>(OnReport);
            // Maximum drawdown is not currently updated from equity events.
        }

        private void OnReport(BrokerReportDto r)
        {
            if (r.ReportType == "Filled" && r.ExchangeTs.Value.Date == _clock.Now.Date)
            {
                _tradesUsedToday++;
            }
            // Consecutive-loss counts are not updated here; they require realized P&L events.
            // Latency and slippage thresholds are checked without a consecutive-breach counter.
            if (r.LatencyMs.HasValue && r.LatencyMs.Value > _rules.MinLatencyGuardMs)
            {
                // Latency breaches are not accumulated over a rolling window.
            }
            if (r.Slippage.HasValue /* threshold conversion is not applied */)
            {
                // Slippage breaches are not accumulated over a rolling window.
            }

            EvaluateLocks();
        }

        private void EvaluateLocks()
        {
            if (_tradesUsedToday >= _rules.DailyTradeLimit)
                ForceLock(RiskLockReason.DailyTradeLimit, TimeSpan.FromSeconds(_rules.AutoLockDurationSeconds));
            else if (_consecutiveLosses >= _rules.MaxConsecutiveLosses)
                ForceLock(RiskLockReason.MaxConsecutiveLosses, TimeSpan.FromSeconds(_rules.AutoLockDurationSeconds));
            else if (_maxDrawdownPctToday >= _rules.MaxIntradayDrawdownPct)
                ForceLock(RiskLockReason.MaxDrawdown, null);
        }

        public bool AllowNewOrder(string symbol, decimal estRisk)
        {
            if (_state.IsLocked) return false;
            // Per-symbol exposure and per-trade loss limits are not checked here.
            return true;
        }

        public void ForceLock(RiskLockReason reason, TimeSpan? duration = null)
        {
            DateTime? unlockAt = duration.HasValue ? _clock.Now.Add(duration.Value) : null;
            _state = new RiskState
            {
                IsLocked = true,
                Reason = reason,
                UnlockAt = unlockAt,
                Message = $"Locked by {reason}"
            };
            OnRiskStateChanged?.Invoke(_state);
            _bus.Publish(_state); // 讓 UI 能訂閱
        }

        public void Unlock()
        {
            _state = new RiskState { IsLocked = false, Reason = RiskLockReason.None };
            OnRiskStateChanged?.Invoke(_state);
            _bus.Publish(_state);
        }
    }
}

