using System;

namespace MuNTrade.Abstractions.Contracts.Time
{
    /// <summary>
    /// 契約：定義查詢交易日曆的職責。
    /// </summary>
    public interface IHolidayCalendar
    {
        /// <summary>
        /// 檢查指定日期是否為休假日。
        /// </summary>
        bool IsHoliday(DateOnly date);

        /// <summary>
        /// 獲取給定日期之後的下一個有效交易日。
        /// </summary>
        DateOnly GetNextTradingDay(DateOnly date);

        /// <summary>
        /// 獲取給定日期之前的上一個有效交易日。
        /// </summary>
        DateOnly GetPreviousTradingDay(DateOnly date);
    }
}
