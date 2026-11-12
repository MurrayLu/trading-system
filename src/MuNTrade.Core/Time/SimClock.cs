// MuNTrade.Core/Time/SimClock.cs
using MuNTrade.Abstractions.Contracts.Time;
using System;
using System.Threading; // ★ 引入 Interlocked

namespace MuNTrade.Core.Time
{
    public class SimClock : IClock
    {
        private DateTime _baseTimestamp = DateTime.MinValue;
        private long _tickOffset = 0; // 使用 long 來避免溢位
        private DateTime _lastAdvanced = DateTime.MinValue;

        public DateTime Now
        {
            get
            {
                // ★ 核心修改：每次呼叫都原子性地增加一個 Tick (100奈秒)
                // 這確保了即使在同一毫秒內被多個執行緒呼叫，每次也會得到唯一且遞增的時間
                long currentOffset = Interlocked.Increment(ref _tickOffset);
                var ts = _baseTimestamp.AddTicks(currentOffset);
                return DateTime.SpecifyKind(ts, DateTimeKind.Utc);
            }
        }

        public SimClock() { }

        public void AdvanceTo(DateTime newTimestamp)
        {
            // BacktestEngine 推進基礎時間
            if (_lastAdvanced != DateTime.MinValue && newTimestamp < _lastAdvanced)
            {
                throw new InvalidOperationException($"SimClock cannot go backwards: {_lastAdvanced:O} -> {newTimestamp:O}");
            }
            var normalized = DateTime.SpecifyKind(newTimestamp, DateTimeKind.Utc);
            _baseTimestamp = normalized;
            _lastAdvanced = normalized;
            // 重置 Tick 偏移量，為下一個時間點的併發呼叫做準備
            Interlocked.Exchange(ref _tickOffset, 0);
        }
    }
}

