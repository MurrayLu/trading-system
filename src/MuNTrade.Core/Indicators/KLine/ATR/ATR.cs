using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Core.Indicators.KLine.ATR
{
    public sealed class ATR
    {
        private readonly int _n;
        private int _countTR;
        private decimal _atr;
        private decimal _sumTR;
        private decimal? _prevClose;

        public ATR(int n) { _n = n; }
        public bool HasValue => _countTR >= _n;

        private static decimal TR(decimal h, decimal l, decimal pc)
            => Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));

        public decimal Update(decimal h, decimal l, decimal c)
        {
            var tr = _prevClose is decimal pc ? TR(h, l, pc) : h - l;
            _prevClose = c;

            if (_countTR < _n)
            {
                _sumTR += tr; _countTR++;
                if (_countTR == _n) _atr = _sumTR / _n;
                return _atr;
            }
            _atr = (_atr * (_n - 1) + tr) / _n;
            _countTR++;
            return _atr;
        }

        public decimal? Preview(decimal h, decimal l, decimal c)
        {
            var tr = _prevClose is decimal pc ? TR(h, l, pc) : h - l;
            if (_countTR < _n - 1) return null;
            if (_countTR == _n - 1) return (_sumTR + tr) / _n;
            return (_atr * (_n - 1) + tr) / _n;
        }
    }
}

