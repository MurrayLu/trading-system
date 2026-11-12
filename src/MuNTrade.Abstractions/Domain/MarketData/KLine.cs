using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy; // 為了使用 BarConfig
using MuNTrade.Abstractions.Time;
using System;

namespace MuNTrade.Abstractions.Domain.MarketData
{
    /// <summary>
    /// 代表一根已完成、標準化的 K 線。
    /// 這是系統中用於傳遞和分析的標準 K 線模型。
    /// </summary>
    public class KLine : MuNTrade.Abstractions.Contracts.Market.IMarketStamped
    {
        /// <summary>
        /// 商品代號。
        /// </summary>
        public string StockId { get; set; }

        public MuNTrade.Abstractions.Domain.Common.Enums.MarketNo MarketNo { get; set; }
        public MuNTrade.Abstractions.Domain.Common.Enums.MarketSource Source { get; set; }

        /// <summary>
        /// 開盤價。
        /// </summary>
        public decimal Open { get; set; }

        /// <summary>
        /// 最高價。
        /// </summary>
        public decimal High { get; set; }

        /// <summary>
        /// 最低價。
        /// </summary>
        public decimal Low { get; set; }

        /// <summary>
        /// 收盤價。
        /// </summary>
        public decimal Close { get; set; }

        /// <summary>
        /// 總成交量。
        /// </summary>
        public long Volume { get; set; }

        /// <summary>
        /// 這根 K 線的開始時間。
        /// </summary>
        public ExchangeTs StartTime { get; set; }

        /// <summary>
        /// 這根 K 線的結束時間。
        /// </summary>
        public ExchangeTs EndTime { get; set; }

        /// <summary>
        /// 這根 K 線的設定（例如：週期、類型）。
        /// </summary>
        public BarConfig? Config { get; set; }

        public bool IsPartial { get; set; } = false;

        /// <summary>
        /// 轉換建構函式：從一個動態的 DynamicBar 建立一根已完成的 KLine。
        /// </summary>
        /// <param name="bar">已完成的 DynamicBar 物件。</param>
        public KLine(DynamicBar bar)
        {
            StockId = bar.StockId;
            Open = bar.Open;
            High = bar.High;
            Low = bar.Low;
            Close = bar.Close;
            Volume = bar.Volume;
            StartTime = bar.StartTime;
            EndTime = bar.EndTime;
            Config = bar.Config;
        }
    }
}
