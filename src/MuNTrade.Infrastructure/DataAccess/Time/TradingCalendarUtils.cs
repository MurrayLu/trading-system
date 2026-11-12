// MuNTrade.Infrastructure/Time/TradingCalendarUtils.cs
using MuNTrade.Abstractions.Contracts.Time;
using System;
using System.Collections.Generic;

namespace MuNTrade.Infrastructure
{
    public static class TradingCalendarUtils
    {
        /// <summary>是否為交易日（預設 Mon..Fri；你可在外層加上假日表過濾）。</summary>
        public static bool IsTradingDay(DateOnly d, HashSet<DateOnly>? holidays = null)
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
            return holidays is null || !holidays.Contains(d);
        }

        /// <summary>回傳上一個交易日（若 today 非交易日會一路往前）。</summary>
        public static DateOnly PreviousTradingDay(DateOnly d, HashSet<DateOnly>? holidays = null)
        {
            do { d = d.AddDays(-1); } while (!IsTradingDay(d, holidays));
            return d;
        }

        /// <summary>今日是否可視為已「收盤完畢」；這裡預設 18:00（可自行傳入 cutoff）。</summary>
        public static bool IsTodayComplete(DateTime nowLocal, TimeOnly cutoffLocal)
            => TimeOnly.FromDateTime(nowLocal) >= cutoffLocal;

        /// <summary>最後一個已完成交易日（LCD）。</summary>
        public static DateOnly GetLastCompleteTradingDay(DateTime nowLocal, TimeOnly? cutoffLocal = null, HashSet<DateOnly>? holidays = null)
        {
            var cutoff = cutoffLocal ?? new TimeOnly(18, 00);
            var today = DateOnly.FromDateTime(nowLocal.Date);
            if (!IsTradingDay(today, holidays)) return PreviousTradingDay(today, holidays);
            return IsTodayComplete(nowLocal, cutoff) ? today : PreviousTradingDay(today, holidays);
        }

        /// <summary>某日所在月份的首個交易日。</summary>
        public static DateOnly GetMonthFirstTradingDay(DateOnly anyDay, HashSet<DateOnly>? holidays = null)
        {
            var first = new DateOnly(anyDay.Year, anyDay.Month, 1);
            while (!IsTradingDay(first, holidays)) first = first.AddDays(1);
            return first;
        }

        /// <summary>將 [from..to] 切成多個「按月分段」的 [s..e]。</summary>
        public static List<(DateOnly Start, DateOnly End)> SplitByMonth(DateOnly from, DateOnly to)
        {
            var outp = new List<(DateOnly, DateOnly)>();
            var cur = new DateOnly(from.Year, from.Month, 1);
            if (cur > from) cur = from;

            var end = to;
            var s = from;
            while (s <= end)
            {
                var monthEnd = new DateOnly(s.Year, s.Month, DateTime.DaysInMonth(s.Year, s.Month));
                var e = monthEnd < end ? monthEnd : end;
                outp.Add((s, e));
                s = e.AddDays(1);
            }
            return outp;
        }

        /// <summary>
        /// LCD（上一個交易日）：完全以交易日曆判定，不看時間切換。
        /// </summary>
        public static DateOnly GetLastCompleteTradingDay(DateTime nowLocal, IHolidayCalendar cal)
        {
            var today = DateOnly.FromDateTime(nowLocal);
            return cal.GetPreviousTradingDay(today);
        }


        /// <summary>本週第一個交易日（考慮連假）。</summary>
        public static DateOnly GetWeekFirstTradingDay(DateOnly anyDay, IHolidayCalendar cal)
        {
            // 找到該週星期一
            int delta = ((int)anyDay.DayOfWeek + 6) % 7; // Monday=1 → delta=0
            var monday = anyDay.AddDays(-delta);
            var d = monday;
            while (cal.IsHoliday(d)) d = d.AddDays(1);
            return d;
        }


        /// <summary>該月第一個交易日（考慮連假）。</summary>
        public static DateOnly GetMonthFirstTradingDay(DateOnly anyDay, IHolidayCalendar cal)
        {
            var first = new DateOnly(anyDay.Year, anyDay.Month, 1);
            var d = first;
            while (cal.IsHoliday(d)) d = d.AddDays(1);
            return d;
        }
    }
}

