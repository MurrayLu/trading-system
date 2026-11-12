using System;

namespace MuNTrade.Core.Common
{
    public sealed class FlowRollingPercentile
    {
        private readonly double[] _buf;
        private int _idx, _count;

        public FlowRollingPercentile(int cap) { _buf = new double[cap]; }

        public int Count => _count;

        public void Add(double v)
        {
            _buf[_idx++] = v;
            if (_idx == _buf.Length) _idx = 0;
            if (_count < _buf.Length) _count++;
        }

        public double GetPctl(double p) // p in (0,1]
        {
            if (_count == 0) return 0;
            var tmp = new double[_count];
            // 拷貝出現有元素
            for (int i = 0; i < _count; i++) tmp[i] = _buf[i];
            Array.Sort(tmp);
            int k = (int)Math.Clamp(Math.Ceiling(p * _count) - 1, 0, _count - 1);
            return tmp[k];
        }
    }
}

