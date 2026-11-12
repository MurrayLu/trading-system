using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.UI
{
    public interface IUiIndicatorFacade
    {
        /// <summary>從快取取一個原始指標鍵值（ex: "SMA13", "ATR14"）。timeframe 請用 "Time_5"、"Day"...</summary>
        bool TryGet(string symbol, string timeframe, string key, out decimal value);

        /// <summary>5 分鐘 ATR（優先取 "ATR14@Time_5"）</summary>
        decimal? GetAtr5m(string symbol);

        /// <summary>5 分鐘 EMA13（若無 EMA13，退而求其次取 SMA13）</summary>
        decimal? GetEma13_5m(string symbol);

        /// <summary>5 分鐘 SMA5</summary>
        decimal? GetSma5_5m(string symbol);

        /// <summary>5 分鐘 SMA10</summary>
        decimal? GetSma10_5m(string symbol);

        /// <summary>5 分鐘 SMA20</summary>
        decimal? GetSma20_5m(string symbol);

        /// <summary>5 分鐘 Donchian 20（取近 20 根 K 的最高/最低，不做計算，只讀 K 倉儲）</summary>
        (decimal? High, decimal? Low)? GetDonchian20(string symbol);

        /// <summary>昨日高低（由 Day 線最後兩根推：倒數第 2 根視為昨）</summary>
        (decimal? YHigh, decimal? YLow)? GetYesterdayHL(string symbol);

        /// 通知某個 symbol 的某個 timeframe 的指標已更新
        event Action<string, string> IndicatorsChanged;
    }
}

