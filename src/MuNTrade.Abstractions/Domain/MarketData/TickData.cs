using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using System;

namespace MuNTrade.Abstractions.Domain.MarketData
{
    /// <summary>
    /// 代表一筆從 API 接收到的原始 Tick 資料。
    /// </summary>
    /// <summary>
    /// 代表一筆從 API 接收到的原始 Tick 資料。
    /// </summary>
    public class TickData : MuNTrade.Abstractions.Contracts.Market.IMarketStamped
    {
        // 低效方式，需要之後把取得ID改為使用預先快取
        public string StockId { get; set; } = string.Empty;

        /// <summary>
        /// 市場編號
        /// </summary>
        public MarketNo MarketNo { get; set; }

        /// <summary>
        /// 資料來源
        /// </summary>
        public MarketSource Source { get; set; }

        public ExchangeTs ExchangeTs { get; set; }
        public ArrivalTs ArrivalTs { get; set; }

        public TickFeedKind FeedKind { get; set; } = TickFeedKind.RealTime;

        public decimal Price { get; set; }
        public long Volume { get; set; }
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public bool IsSimulated { get; set; }
        public bool IsSessionEnd { get; set; } = false;
    }
}

