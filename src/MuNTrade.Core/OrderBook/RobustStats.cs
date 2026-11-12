using System;
using System.Collections.Generic;
using System.Linq;
using MuNTrade.Abstractions.Contracts.Time;

namespace MuNTrade.Core.OrderBook
{
    internal sealed class RobustStats
    {
        public double Median { get; }
        public double Mad { get; }
        public double P75 { get; }
        public double P90 { get; }

        public RobustStats(double median, double mad, double p75, double p90)
        {
            Median = median;
            Mad = mad;
            P75 = p75;
            P90 = p90;
        }
    }

    /// <summary>
    /// 簡易的滾動窗 Robust 統計，提供 median/MAD/分位數作為動態門檻。
    /// </summary>
    internal sealed class RollingEstimator
    {
        private int _capacity;
        private readonly Queue<double> _buffer = new();
        private RobustStats _cached = new RobustStats(0, 0, 0, 0);
        private readonly IClock _clock;
        private DateTime _lastSnapshot = DateTime.MinValue;
        private int _lastCount;
        private const double MinSnapshotIntervalMs = 250;

        public RollingEstimator(int capacity, IClock clock)
        {
            _capacity = Math.Max(8, capacity);
            _clock = clock;
        }

        public int Count => _buffer.Count;

        public int Capacity
        {
            get => _capacity;
            set
            {
                _capacity = Math.Max(8, value);
                while (_buffer.Count > _capacity)
                    _buffer.Dequeue();
            }
        }

        public void Add(double value)
        {
            _buffer.Enqueue(value);
            while (_buffer.Count > _capacity)
                _buffer.Dequeue();
        }

        public bool IsWarm(int minSamples) => _buffer.Count >= minSamples;

        public RobustStats Snapshot()
        {
            if (_buffer.Count == 0) return _cached;

            var now = _clock.Now;
            if (_buffer.Count == _lastCount && (now - _lastSnapshot).TotalMilliseconds < MinSnapshotIntervalMs)
            {
                return _cached;
            }

            var arr = _buffer.ToArray();
            Array.Sort(arr);

            double median = GetQuantile(arr, 0.5);
            double p75 = GetQuantile(arr, 0.75);
            double p90 = GetQuantile(arr, 0.9);

            // MAD = median(|x - median|)
            var deviations = arr.Select(v => Math.Abs(v - median)).ToArray();
            Array.Sort(deviations);
            double mad = GetQuantile(deviations, 0.5);

            _cached = new RobustStats(median, mad, p75, p90);
            _lastCount = _buffer.Count;
            _lastSnapshot = now;
            return _cached;
        }

        public double Threshold(double madMultiplier, double floor, int minSamples)
        {
            var stats = Snapshot();
            if (!IsWarm(minSamples))
            {
                // 暖機不足時使用地板，避免過早觸發
                return floor;
            }

            double mad = stats.Mad;
            double adjMad = mad > 0 ? mad : Math.Max(stats.Median * 0.1, 1.0);
            return Math.Max(stats.Median + madMultiplier * adjMad, floor);
        }

        private static double GetQuantile(double[] sorted, double q)
        {
            if (sorted.Length == 0) return 0;
            double pos = (sorted.Length - 1) * q;
            int idx = (int)pos;
            double frac = pos - idx;
            if (idx + 1 < sorted.Length)
                return sorted[idx] * (1 - frac) + sorted[idx + 1] * frac;
            return sorted[idx];
        }
    }
}

