using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Indicators;
using MuNTrade.Core.Settings;
using System;
using System.Collections.Concurrent;

namespace MuNTrade.Core.Indicators.Intraday.Orb
{
    public sealed class OrbTracker : IOrbTracker
    {
        private readonly ConcurrentDictionary<string, Day> _state = new();
        private readonly TimeSpan _start, _end;
        // ★★★ 新增：夜盤的時間區間欄位 ★★★
        private readonly TimeSpan _nightStart, _nightEnd;

        public OrbTracker(IOptions<OrbSettings> opt)
        {
            var s = opt?.Value ?? new OrbSettings();
            _start = TimeSpan.Parse(s.Start);
            _end = TimeSpan.Parse(s.End);

            // ★★★ 新增：讀取夜盤設定 (加上 TryParse 避免舊設定檔報錯) ★★★
            // 若設定檔沒寫，預設給 TimeSpan.Zero 或一個不會誤觸的時間
            if (!TimeSpan.TryParse(s.NightStart, out _nightStart)) _nightStart = TimeSpan.FromHours(15);
            if (!TimeSpan.TryParse(s.NightEnd, out _nightEnd)) _nightEnd = TimeSpan.FromHours(15.25); // 15:15
        }

        public void OnTrade(string symbol, DateTime tsLocal, decimal last, int qty, DateTime tradingDateLocal)
        {
            var d = _state.GetOrAdd(symbol, _ => new Day(tradingDateLocal.Date));

            lock (d)
            {
                // 如果交易日換了 (例如夜盤算是 T+1 盤)，重置數據
                if (d.TradingDate.Date != tradingDateLocal.Date) d.Reset(tradingDateLocal.Date);

                var tod = tsLocal.TimeOfDay;

                // ★★★ 核心修正：判斷是否在「日盤區間」或「夜盤區間」 ★★★
                bool isDayOrb = tod >= _start && tod <= _end;
                bool isNightOrb = tod >= _nightStart && tod <= _nightEnd;

                if (isDayOrb || isNightOrb)
                {
                    if (d.High == 0)
                    {
                        d.High = last;
                        d.Low = last;
                    }
                    else
                    {
                        d.High = Math.Max(d.High, last);
                        d.Low = Math.Min(d.Low, last);
                    }
                }

                // ★★★ 修正閉盤判斷：只有當「完全超過」所有區間才算 Closed ★★★
                // 簡單邏輯：如果是日盤結束後，或者是夜盤結束後
                // 但要小心：日盤結束(09:15)後，還沒到夜盤(15:00)前，這段時間算 Closed
                // 夜盤結束(15:15)後，也算 Closed

                // 判斷邏輯：如果現在時間 > 結束時間，且不在另一個開盤時間前
                // 簡化版：只要時間大於對應的 End，就標記 Closed
                if ((tod > _end && tod < _nightStart) || (tod > _nightEnd))
                {
                    d.Closed = true;
                }
                else
                {
                    // 如果正在 ORB 區間內，確保 Closed 是 false
                    d.Closed = false;
                }
            }
        }

        public OrbSnapshot? Get(string symbol)
        {
            if (_state.TryGetValue(symbol, out var d))
            {
                lock (d)
                {
                    return new OrbSnapshot(d.TradingDate, d.High, d.Low, d.Closed);
                }
            }
            return null;
        }

        public void Reset(string symbol, DateTime tradingDateLocal)
        {
            var d = _state.GetOrAdd(symbol, _ => new Day(tradingDateLocal.Date));
            lock (d)
            {
                d.Reset(tradingDateLocal.Date);
            }
        }

        private sealed class Day
        {
            public Day(DateTime date) => Reset(date);
            public DateTime TradingDate { get; private set; }
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public bool Closed { get; set; }
            public void Reset(DateTime date) { TradingDate = date.Date; High = 0; Low = 0; Closed = false; }
        }
    }
}
