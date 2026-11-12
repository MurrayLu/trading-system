using MuNTrade.Abstractions.Contracts.Indicators;
using System;
using System.Collections.Concurrent;

namespace MuNTrade.Core.Indicators.Intraday.Vwap
{
    public sealed class VwapTracker : IVwapTracker
    {
        private readonly ConcurrentDictionary<string, Day> _state = new();

        public void OnTrade(string symbol, DateTime tsLocal, decimal last, int qty, DateTime tradingDateLocal)
        {
            // 這裡不變，快速查找
            var d = _state.GetOrAdd(symbol, _ => new Day(tradingDateLocal.Date));

            lock (d)
            {
                if (d.TradingDate.Date != tradingDateLocal.Date) d.Reset(tradingDateLocal.Date);

                //  效能優化關鍵
                // 將運算轉為 double。
                // 在大量回放時，這行 (double * int) 比 (decimal * int) 快非常多。
                double tradeVal = (double)last * qty;

                d.CumPV += tradeVal;
                d.CumVol += qty;

                // 這裡只存狀態，不急著轉回 decimal，減少轉換開銷
                // Vwap 的計算也延後到 Get() 再做，除非內部需要即時用
                // 為了保持 Day 物件簡單，我們這裡先算出 double 版本的 Vwap
                if (d.CumVol > 0)
                    d.Vwap = d.CumPV / d.CumVol;
            }
        }

        public VwapSnapshot? Get(string symbol)
        {
            if (_state.TryGetValue(symbol, out var d))
            {
                lock (d)
                {
                    //  讀取路徑 (Cold Path)
                    // 只有在 UI 或策略來要資料時，才轉回 decimal。
                    // 這樣就把轉型的成本從「每筆 Tick」轉移到了「每次查詢」。
                    return new VwapSnapshot(
                        d.TradingDate,
                        (decimal)d.Vwap,
                        (decimal)d.CumPV,
                        (int)d.CumVol
                    );
                }
            }
            return null;
        }

        public void Reset(string symbol, DateTime tradingDateLocal)
            => _state.AddOrUpdate(symbol, _ => new Day(tradingDateLocal.Date), (_, __) => new Day(tradingDateLocal.Date));

        private sealed class Day
        {
            public Day(DateTime date) => Reset(date);

            public DateTime TradingDate { get; private set; }

            //  改用 double 儲存，大幅提升計算速度
            public double Vwap { get; set; }
            public double CumPV { get; set; }
            public double CumVol { get; set; } // 量大時 double 也比 int/long 運算快一點點，且防溢位

            public void Reset(DateTime date)
            {
                TradingDate = date.Date;
                Vwap = 0;
                CumPV = 0;
                CumVol = 0;
            }
        }
    }
}
