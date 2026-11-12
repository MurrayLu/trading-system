using System;
using System.Collections.Generic;
using System.Linq;
using MuNTrade.Core.Settings;

namespace MuNTrade.Core.OrderBook
{
    public class OrderBookStats
    {
        private readonly List<double> _openingSamples = new();
        private double _openingSampleTotalVolume;

        private double _frozenMean = 1;
        private double _frozenThreshold = double.MaxValue;
        private bool _isFrozen;

        private double _recentAvgSize;
        private const double EmaAlpha = 0.1;
        public int OpeningSampleCount => _openingSamples.Count;
        public double OpeningSampleTotalVolume => _openingSampleTotalVolume;
        public bool IsFrozen => _isFrozen;
        public double FrozenThreshold => _frozenThreshold;
        public double RecentAvgSize => _recentAvgSize;
        public string LastFreezeReason { get; private set; } = "not-frozen";
        public int LastFreezeSampleCount { get; private set; }
        public double LastFreezeSampleTotalVolume { get; private set; }

        public void AddSample(double volume)
        {
            if (_recentAvgSize == 0)
                _recentAvgSize = volume;
            else
                _recentAvgSize = (_recentAvgSize * (1 - EmaAlpha)) + (volume * EmaAlpha);

            if (!_isFrozen)
            {
                _openingSamples.Add(volume);
                _openingSampleTotalVolume += volume;
            }
        }

        public void Freeze(OrderBookEngineOptions? opt)
        {
            if (_isFrozen) return;
            opt ??= new OrderBookEngineOptions();

            double totalVolume = _openingSampleTotalVolume;
            LastFreezeSampleCount = _openingSamples.Count;
            LastFreezeSampleTotalVolume = totalVolume;

            if (_openingSamples.Count > 5 && totalVolume > opt.stat_min_opening_vol)
            {
                double mean = totalVolume / _openingSamples.Count;
                double sumSq = _openingSamples.Sum(v => Math.Pow(v - mean, 2));
                double stdDev = Math.Sqrt(sumSq / _openingSamples.Count);

                _frozenMean = mean;
                _frozenThreshold = Math.Max(opt.large_trade_min_floor, mean + (opt.large_trade_z_score * stdDev));
                LastFreezeReason = "ready-dynamic";
            }
            else
            {
                _frozenMean = opt.stat_fallback_mean;
                _frozenThreshold = opt.stat_fallback_threshold;
                LastFreezeReason = _openingSamples.Count <= 5
                    ? "fallback-insufficient-opening-samples"
                    : "fallback-insufficient-opening-volume";
            }

            _isFrozen = true;
            _openingSamples.Clear();
            _openingSampleTotalVolume = 0;
            _openingSamples.TrimExcess();
        }

        public bool IsLargeTrade(double volume, double dynamicMultiplier)
        {
            if (!_isFrozen) return false;

            double staticTh = _frozenThreshold;
            double dynamicTh = _recentAvgSize * dynamicMultiplier;
            double effectiveThreshold = Math.Max(staticTh, dynamicTh);

            return volume >= effectiveThreshold;
        }

        public double GetCurrentThreshold(double dynamicMultiplier) =>
            _isFrozen ? Math.Max(_frozenThreshold, _recentAvgSize * dynamicMultiplier) : 0;

        public double AvgOrderSize => _isFrozen ? Math.Max(_frozenMean, _recentAvgSize) : 1;

        public void Reset()
        {
            _isFrozen = false;
            _openingSamples.Clear();
            _openingSampleTotalVolume = 0;
            _recentAvgSize = 0;
            _frozenMean = 1;
            _frozenThreshold = double.MaxValue;
            LastFreezeReason = "not-frozen";
            LastFreezeSampleCount = 0;
            LastFreezeSampleTotalVolume = 0;
        }
    }
}

