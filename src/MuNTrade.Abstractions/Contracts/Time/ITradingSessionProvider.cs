using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using System;

namespace MuNTrade.Abstractions.Contracts.Time
{
    public interface ITradingSessionProvider
    {
        /// <summary>
        /// 根據 timestamp 與商品代碼解析出其所屬的交易時段（session）。
        /// 若 timestamp 不屬於任何已知 session，回傳 null。
        /// 回傳的 TradingSession 必須包含 TradeDate (DateOnly)。
        /// </summary>
        TradingSession? GetSession(string stockId, DateTime timestamp);

        /// <summary>
        /// 在指定日期區間（inclusive）內找出第一個與最後一個有 session 的交易日（回傳 DateOnly?）。
        /// probeTime 可留空，預設使用 09:00。
        /// </summary>
        (DateOnly? first, DateOnly? last) GetFirstAndLastTradingDateInRange(string stockId, DateTime dateFrom, DateTime dateTo, TimeOnly? probeTime = null);

        /// <summary>
        /// 往前找某個交易日的前一個有 session 的交易日（最多往前查 maxLookbackDays）。
        /// 回傳 null 表示找不到。
        /// </summary>
        DateOnly? FindPreviousTradingDate(string stockId, DateOnly day, int maxLookbackDays = 30);

        /// <summary>
        /// 判斷某日是否為交易日（以 probeTime 09:00 判定）。
        /// </summary>
        bool IsTradingDate(string stockId, DateOnly date);

        /// <summary>
        /// 取得指定 TF（BarConfig）的週期 window（包含 WindowStart/WindowEnd 與該週期的第一/最後交易日）。
        /// MarketDataPipeline 可直接呼叫此方法取得要 seed 的 expStart/expEnd。
        /// </summary>
        PeriodWindow GetPeriodWindow(string stockId, BarConfig cfg, DateTime nowLocal);
    }
}

