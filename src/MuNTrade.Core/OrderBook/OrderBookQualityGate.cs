using System;
using System.Collections.Generic;
using System.Linq;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Events; // Added (OrderBookQualityTier moved here)
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Core.OrderBook
{
    public class OrderBookQualityGate
    {
        private readonly TimeSpan _windowSize = TimeSpan.FromSeconds(30);
        private readonly LinkedList<(ArrivalTs Ts, bool IsStale)> _staleHistory = new();
        private readonly LinkedList<(ArrivalTs Ts, bool IsOrphan)> _orphanHistory = new();
        private readonly LinkedList<ArrivalTs> _arrivalHistory = new();
        private readonly List<double> _skewHistory = new();

        private double _joinerOrphanRate = 0;

        // Configurable thresholds
        private const double FreqThresholdHz = 1.0;
        private const double SkewThresholdSec = 0.5;

        public (double Score, OrderBookQualityTier Tier, List<string> Reasons) UpdateBest5(ArrivalTs now, bool isStale)
        {
            Sweep(now);

            // 1. Stale Rate
            _staleHistory.AddLast((now, isStale));
            double staleRate = _staleHistory.Count == 0 ? 0 : (double)_staleHistory.Count(x => x.IsStale) / _staleHistory.Count;

            // 2. Best5 Frequency
            _arrivalHistory.AddLast(now);
            double duration = _arrivalHistory.Count > 1 ? (_arrivalHistory.Last!.Value - _arrivalHistory.First!.Value).TotalSeconds : 1;
            double freq = duration > 0 ? _arrivalHistory.Count / duration : 0;

            return CalculateScore(staleRate, freq);
        }

        public void UpdateSkew(double skewSeconds)
        {
            _skewHistory.Add(skewSeconds);
            if (_skewHistory.Count > 100) _skewHistory.RemoveAt(0);
        }

        public void UpdateJoinerStats(double orphanRate)
        {
            _joinerOrphanRate = Math.Clamp(orphanRate, 0, 1);
        }

        public double LastOrphanRate => _joinerOrphanRate;
        public double LastSkewP95Sec { get; private set; }

        private (double Score, OrderBookQualityTier Tier, List<string> Reasons) CalculateScore(double staleRate, double freq)
        {
            // 3. Skew
            double skewP95 = _skewHistory.Count > 0 ? Percentile(_skewHistory, 95) : 0;
            LastSkewP95Sec = skewP95;

            // Normalize subscores to [0, 1] (Higher is Better)
            double scoreStale = Math.Clamp(1.0 - staleRate, 0, 1);
            double scoreOrphan = Math.Clamp(1.0 - _joinerOrphanRate, 0, 1);
            double scoreFreq = Math.Clamp(freq >= FreqThresholdHz ? 1.0 : freq / FreqThresholdHz, 0, 1);
            double scoreSkew = Math.Clamp(skewP95 <= SkewThresholdSec ? 1.0 : 1.0 - (skewP95 - SkewThresholdSec), 0, 1);

            // Weighted Average (Weights sum to 1.0)
            // Stale/Orphan are critical (30% each), Freq/Skew are secondary (20% each)
            double score = (scoreStale * 0.3) + (scoreOrphan * 0.3) + (scoreFreq * 0.2) + (scoreSkew * 0.2);

            // Determine Tier
            OrderBookQualityTier tier;
            if (score >= 0.4) tier = OrderBookQualityTier.Good;
            else if (score >= 0.2) tier = OrderBookQualityTier.Degraded;
            else tier = OrderBookQualityTier.Bad;

            // Reasons
            var reasons = new List<string>();
            if (scoreStale < 0.8) reasons.Add($"High Stale Rate: {staleRate:P0}");
            if (scoreOrphan < 0.8) reasons.Add($"High Orphan Rate: {_joinerOrphanRate:P0}");
            if (scoreFreq < 0.8) reasons.Add($"Low Frequency: {freq:F1}Hz");
            if (scoreSkew < 0.8) reasons.Add($"High Skew: {skewP95:F3}s");

            return (score, tier, reasons);
        }

        private void Sweep(ArrivalTs now)
        {
            while (_staleHistory.First != null && (now - _staleHistory.First.Value.Ts) > _windowSize)
                _staleHistory.RemoveFirst();

            while (_arrivalHistory.First != null && (now - _arrivalHistory.First.Value) > _windowSize)
                _arrivalHistory.RemoveFirst();
        }

        private double Percentile(List<double> values, double p)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(x => x).ToList();
            int index = (int)Math.Ceiling((p / 100.0) * sorted.Count) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Count - 1, index))];
        }
    }
}

