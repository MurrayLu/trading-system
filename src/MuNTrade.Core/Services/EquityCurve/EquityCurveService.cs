using System;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Contracts.Time;

namespace MuNTrade.Core.Services.EquityCurve
{
    public sealed class EquitySnapshot
    {
        public DateTime Ts { get; init; }
        public decimal Equity { get; init; }
        public decimal PeakEquity { get; init; }
        public decimal MaxDrawdown { get; init; }
        public decimal MaxDrawdownPct { get; init; }
    }

    public interface IEquityCurveService
    {
        decimal CurrentEquity { get; }
        decimal PeakEquityToday { get; }
        decimal MaxDrawdownToday { get; }
        decimal MaxDrawdownPctToday { get; }
        event Action<EquitySnapshot>? OnEquityUpdated;

        void UpdateEquity(decimal equity);
    }

    public sealed class EquityCurveService : IEquityCurveService
    {
        private decimal _current, _peak, _maxDD, _maxDDPct;
        private readonly IClock _clock;
        public decimal CurrentEquity => _current;
        public decimal PeakEquityToday => _peak;
        public decimal MaxDrawdownToday => _maxDD;
        public decimal MaxDrawdownPctToday => _maxDDPct;
        public event Action<EquitySnapshot>? OnEquityUpdated;

        public EquityCurveService(IClock clock)
        {
            _clock = clock;
        }

        public void UpdateEquity(decimal equity)
        {
            _current = equity;
            if (equity > _peak) _peak = equity;

            var dd = _peak - equity;
            if (dd > _maxDD) _maxDD = dd;

            _maxDDPct = _peak > 0 ? _maxDD / _peak : 0m;

            OnEquityUpdated?.Invoke(new EquitySnapshot
            {
                Ts = _clock.Now,
                Equity = _current,
                PeakEquity = _peak,
                MaxDrawdown = _maxDD,
                MaxDrawdownPct = _maxDDPct
            });
        }
    }
}

