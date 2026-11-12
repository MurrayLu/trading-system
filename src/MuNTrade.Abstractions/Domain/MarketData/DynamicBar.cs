using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Abstractions.Time;
using System;

using MuNTrade.Abstractions.Contracts.Market; // IMarketStamped
using MuNTrade.Abstractions.Domain.Common.Enums; // MarketNo, MarketSource

namespace MuNTrade.Abstractions.Domain.MarketData
{
    public sealed class DynamicBar : IMarketStamped
    {
        //對外公開屬性（唯讀） ----
        public string StockId { get; private set; } = "";
        public MarketNo MarketNo { get; set; }
        public MarketSource Source { get; set; }
        public decimal Open { get; private set; }
        public decimal High { get; private set; }
        public decimal Low { get; private set; }
        public decimal Close { get; private set; }
        public long Volume { get; private set; }
        public int TickCount { get; private set; }
        public ExchangeTs StartTime { get; private set; }
        public ExchangeTs EndTime { get; private set; }
        public ExchangeTs LastTickTime { get; private set; }
        public BarConfig? Config { get; private set; }

        //建構子 ----
        private DynamicBar() { } // for snapshot/clone

        public DynamicBar(TickData first)
        {
            StockId = first.StockId;
            MarketNo = first.MarketNo;
            Source = first.Source;
            Open = High = Low = Close = first.Price;
            Volume = first.Volume;
            TickCount = 1;
            StartTime = first.ExchangeTs;  // Builder 會再用 SealBoundary 修正
            EndTime = first.ExchangeTs;
            LastTickTime = first.ExchangeTs;
        }

        //聚合 ----
        public void AddTick(TickData t, long? volDeltaIfCumulative = null)
        {
            High = Math.Max(High, t.Price);
            Low = Math.Min(Low, t.Price);
            Close = t.Price;
            Volume += volDeltaIfCumulative ?? t.Volume;
            TickCount++;
            LastTickTime = t.ExchangeTs;
        }

        //Builder / Store 專用 API ----
        internal void ForceStockId(string id) => StockId = id;
        internal void ForceMarketInfo(MarketNo mkt, MarketSource src) { MarketNo = mkt; Source = src; }
        internal void ForceConfig(BarConfig cfg) => Config = cfg;
        internal void ForceBoundary(ExchangeTs start, ExchangeTs end)
        {
            StartTime = start;
            EndTime = end;
        }
        internal void ForceOhlcv(decimal o, decimal h, decimal l, decimal c, long v)
        {
            Open = o; High = h; Low = l; Close = c; Volume = v;
        }
        internal void ForceTickCount(int n) => TickCount = n;
        internal void ForceLastTickTime(ExchangeTs ts) => LastTickTime = ts;

        public DynamicBar SealBoundary(ExchangeTs start, ExchangeTs end)
        {
            ForceBoundary(start, end);
            return this;
        }

        //快照工具 ----
        public static DynamicBar FromSnapshot(string stockId, BarConfig cfg,
                                              ExchangeTs start, ExchangeTs end,
                                              decimal o, decimal h, decimal l, decimal c,
                                              long vol, int ticks, ExchangeTs lastTs,
                                              MarketNo marketNo = MarketNo.Unknown, MarketSource source = MarketSource.Domestic)
        {
            return new DynamicBar
            {
                StockId = stockId,
                MarketNo = marketNo,
                Source = source,
                Config = cfg,
                StartTime = start,
                EndTime = end,
                Open = o,
                High = h,
                Low = l,
                Close = c,
                Volume = vol,
                TickCount = ticks,
                LastTickTime = lastTs
            };
        }

        public DynamicBar Clone()
        {
            return FromSnapshot(StockId, Config!, StartTime, EndTime,
                                Open, High, Low, Close, Volume,
                                TickCount, LastTickTime, MarketNo, Source);
        }
    }
}

