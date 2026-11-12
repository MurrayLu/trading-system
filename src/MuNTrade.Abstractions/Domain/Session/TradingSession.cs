// TradingSession.cs
using System;

namespace MuNTrade.Abstractions.Domain.Session
{
    /// <summary>代表某一天、某商品的完整交易時段。</summary>
    public class TradingSession
    {
        public DateTime StartTime { get; }   // 09:00
        public DateTime EndTime { get; }   // 13:30
        public TimeSpan Anchor { get; }   // 09:00

        // ★ 新增兩個欄位：收盤集合競價起點 & 允許延伸多久
        public TimeSpan PreCloseAuctionStart { get; }   // 13:25
        public TimeSpan PreCloseExtension { get; }   // 00:01 (→ 13:26)

        /* 新增：這段 Session 應落在哪一天的「交易日」帳務 */
        public DateOnly TradeDate { get; }

        public TradingSession(DateTime st, DateTime et,
                              TimeSpan anchor,
                              TimeSpan auctionStart, TimeSpan auctionExt,
                              DateOnly tradeDate)
        {
            StartTime = st;
            EndTime = et;
            Anchor = anchor;
            PreCloseAuctionStart = auctionStart;
            PreCloseExtension = auctionExt;
            TradeDate = tradeDate;
        }

        /// <summary>
        /// 判斷指定的時間戳是否落在這個交易時段的開始與結束之間。
        /// </summary>
        /// <param name="timestamp">要檢查的時間戳。</param>
        /// <returns>如果在時段內，則為 true；否則為 false。</returns>
        public bool IsDuringMarketHours(DateTime timestamp)
        {
            // 因為 StartTime 和 EndTime 都是 DateTime，可以直接進行比較
            return timestamp >= this.StartTime && timestamp <= this.EndTime;
        }
    }
}

