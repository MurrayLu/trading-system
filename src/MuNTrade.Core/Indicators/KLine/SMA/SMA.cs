using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Core.Indicators.KLine.SMA
{
    public sealed class SMA
    {
        private readonly int _n;
        private decimal _sum;
        private int _count;
        private readonly Queue<decimal> _window = new();

        public SMA(int n) { _n = n; }

        public bool HasValue => _count >= _n;

        public decimal Update(decimal close)
        {
            _window.Enqueue(close);
            _sum += close; _count++;
            if (_count > _n) _sum -= _window.Dequeue();
            return HasValue ? _sum / _n : default;
        }

        public decimal? Preview(decimal close)
        {
            if (_count < _n - 1) return null;
            var sum = _sum + close - (_count >= _n ? _window.Peek() : 0m);
            var denom = _count >= _n ? _n : _count + 1;
            if (denom < _n) return null;
            return sum / _n;
        }
    }
}

