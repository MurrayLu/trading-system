using System;
using System.Collections.Concurrent;
using MuNTrade.Abstractions.Contracts.Indicators;

namespace MuNTrade.Core.Indicators.Intraday
{
    public sealed class OrbVwapAggregator : IOrbVwapReader
    {
        private readonly IOrbTracker _orb;
        private readonly IVwapTracker _vwap;

        public OrbVwapAggregator(IOrbTracker orb, IVwapTracker vwap)
        { _orb = orb; _vwap = vwap; }

        public void OnTrade(string symbol, DateTime tsLocal, decimal last, int qty, DateTime tradingDateLocal)
        {
            // 寫入路徑 (Hot Path)：只做最單純的轉發，零記憶體配置 (Zero Allocation)
            _orb.OnTrade(symbol, tsLocal, last, qty, tradingDateLocal);
            _vwap.OnTrade(symbol, tsLocal, last, qty, tradingDateLocal);
        }

        public OrbVwapCombined? Get(string symbol)
        {
            // 讀取路徑 (Cold Path)：只有 UI 或策略需要時才組裝
            // 這樣即使一秒進來 1000 筆 Tick，只要沒人讀取，就不會產生垃圾物件
            var o = _orb.Get(symbol);
            var v = _vwap.Get(symbol);

            if (o != null && v != null)
            {
                return new OrbVwapCombined(o.TradingDate, o.High, o.Low, o.Closed, v.Vwap);
            }
            return null;
        }

        public void Reset(string symbol, DateTime tradingDateLocal)
        {
            _orb.Reset(symbol, tradingDateLocal);
            _vwap.Reset(symbol, tradingDateLocal);
        }
    }
}

