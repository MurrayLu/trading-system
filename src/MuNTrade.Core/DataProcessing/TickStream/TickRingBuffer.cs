// MuNTrade.Core/DataProcessing/TickStream/TickRingBuffer.cs
using System;
using System.Collections.Generic;

namespace MuNTrade.Core.DataProcessing.TickStream
{
    /// <summary>
    /// 固定容量環狀緩衝區。Push O(1)，不搬移元素，不分配記憶體。
    /// 非執行緒安全，呼叫端負責加鎖。
    /// </summary>
    internal sealed class TickRingBuffer<T>
    {
        private readonly T[] _buf;
        private int _head;  // 下次寫入位置
        private int _count; // 目前筆數

        public TickRingBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _buf = new T[capacity];
            Capacity = capacity;
        }

        public int Capacity { get; }
        public int Count => _count;

        /// <summary>O(1)。超過容量自動覆寫最舊元素。</summary>
        public void Push(T item)
        {
            _buf[_head] = item;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }

        /// <summary>
        /// 回傳 [最舊 → 最新] 排列的 copy list。
        /// maxCount &lt;= 0 表示全部；maxCount &gt; 0 表示只取最新的 maxCount 筆。
        /// </summary>
        public List<T> Snapshot(int maxCount = 0)
        {
            if (_count == 0) return new List<T>(0);

            int take = (maxCount > 0 && maxCount < _count) ? maxCount : _count;
            var list = new List<T>(take);

            // 如果只取最新 take 筆，起始位置跳到正確的地方
            int start = (_count == Capacity) ? _head : 0;
            if (take < _count)
                start = (_head - take + Capacity * 2) % Capacity;

            for (int i = 0; i < take; i++)
                list.Add(_buf[(start + i) % Capacity]);

            return list;
        }

        public void Clear()
        {
            Array.Clear(_buf, 0, _buf.Length);
            _head = 0;
            _count = 0;
        }
    }
}

