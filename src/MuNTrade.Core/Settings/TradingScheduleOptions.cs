using System;

namespace MuNTrade.Core.Settings
{
    /// <summary>
    /// 排程規劃器的設定值，方便之後在 appsettings.json 調整。
    /// </summary>
    public sealed class TradingScheduleOptions
    {
        /// <summary>
        /// 早盤前刷新靜態資料（昨收/昨量/漲跌停）的時間（預設 08:35）。
        /// </summary>
        public TimeSpan MorningStaticDataRefreshTime { get; set; } = new(8, 35, 0);

        /// <summary>
        /// 盤後（或夜盤前）刷新靜態資料的時間（預設 14:45）。
        /// </summary>
        public TimeSpan StaticDataRefreshTime { get; set; } = new(14, 45, 0);

        /// <summary>
        /// 夜間停用行情監控（預設 06:00）。
        /// </summary>
        public TimeSpan LeaveMonitorTime { get; set; } = new(6, 0, 0);

        /// <summary>
        /// 早盤前啟用行情監控（預設 07:30）。
        /// </summary>
        public TimeSpan EnterMonitorTime { get; set; } = new(7, 30, 0);
    }
}

