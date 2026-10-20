using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using System;
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Events.MarketData
{
    public class QuoteSnapshotEvent
    {
        public string Symbol { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }

        // --- ★ 新增的靜態資料欄位 ---
        public string Name { get; set; } = string.Empty;       // 商品名稱
        public decimal ReferencePrice { get; set; }              // 昨收/參考價
        public long YesterdayVolume { get; set; }               // 昨量

        // 價格
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal LastPrice { get; set; }

        // 量：單筆 int、累積 long
        public int Vol { get; set; }   // 最近一筆量（若無，填 0）
        public long TVol { get; set; }   // 當日總量

        // 最佳一檔
        public decimal BidPrice { get; set; }
        public int BidQty { get; set; }
        public decimal AskPrice { get; set; }
        public int AskQty { get; set; }

        // 當沖
        public DayTradeStatus? DayTradeStatus { get; set; }

        // ★ 新增 IsSimulated 屬性
        public bool IsSimulated { get; set; }

        public int StockIndex { get; set; }           // 商品自定索引代號
        public short Decimal { get; set; }            // 小數位數
        public short TypeNo { get; set; }             // (證券)類股別
        public long TotalBidQty { get; set; }         // 買盤量(即外盤量)
        public long TotalAskQty { get; set; }         // 賣盤量(即內盤量)
        public long FutureOI { get; set; }            // 期貨未平倉 OI
        public decimal UpLimit { get; set; }          // 漲停價
        public decimal DownLimit { get; set; }        // 跌停價
        public int TradingDay { get; set; }           // 交易日(YYYYMMDD)
        public int DealTime { get; set; }             // 成交時間(hhmmss)

        // 來源：Quote(券商快照) or SnapshotBuilder(由 Tick/Best5 衍生)
        public QuoteSnapshotOrigin Origin { get; set; } = QuoteSnapshotOrigin.Quote;

        public MarketNo MarketNo { get; set; }
        public MarketSource Source { get; set; } = MarketSource.Domestic;
    }

    public enum QuoteSnapshotOrigin
    {
        Quote = 0,
        SnapshotBuilder = 1
    }
}

