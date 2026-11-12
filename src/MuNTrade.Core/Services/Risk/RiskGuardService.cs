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
        private int _consecutiveLosses;
        private decimal _maxDrawdownPctToday;

        private RiskState _state = new() { IsLocked = false, Reason = RiskLockReason.None };
        public RiskState Current => _state;
        public event Action<RiskState>? OnRiskStateChanged;

        public RiskGuardService(IOptions<DayTradingRules> rules, IEventBus bus, IClock clock)
        {
            _rules = rules.Value;
            _bus = bus;
            _clock = clock;

            _bus.Subscribe<BrokerReportDto>(OnReport);
            // 若有 EquitySnapshot 型別，可在 EquityCurveService 發佈，在這裡訂閱以刷新 _maxDrawdownPctToday
            // _bus.Subscribe<EquitySnapshot>(OnEquity);
        }

        private void OnReport(BrokerReportDto r)
        {
            if (r.ReportType == "Filled" && r.ExchangeTs.Value.Date == _clock.Now.Date)
            {
                _tradesUsedToday++;
            }

            // 連續虧損：實務上應該由成交盈虧事件來判定，這裡先留骨架
            // if (fillPnL < 0) _consecutiveLosses++; else _consecutiveLosses = 0;

            // 延遲/滑價守門：連續超標可鎖單（骨架）
            if (r.LatencyMs.HasValue && r.LatencyMs.Value > _rules.MinLatencyGuardMs)
            {
                // TODO: 計數窗口，連續 N 次後觸發
            }
            if (r.Slippage.HasValue /* && 超過 Ticks 換算閾值 */)
            {
                // TODO: 同上
            }

            EvaluateLocks();
        }

        // private void OnEquity(EquitySnapshot s) { _maxDrawdownPctToday = s.MaxDrawdownPct; EvaluateLocks(); }

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
            // TODO: PerSymbolExposureLimit / MaxLossPerTrade 等檢查
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

