using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Infrastructure.DataAccess.Warmup
{
    internal static class WarmupHelpers
    {
        /// <summary>
        /// 根據 minBars (== requiredBars) 與 timeframe，計算回推開始日，並把 Week/Month 都對齊到該月份的 1 號（你要的檔名一致性）。
        /// - referenceEndDate 應為 LCD（last complete trading day）或你要的區間結束日
        /// - minBars 必須 >= 1
        /// - maxCapBars 用來保險避免意外回推過久
        /// </summary>
        public static DateOnly GetAlignedStartByMinBars(DateOnly referenceEndDate, string timeframeKey, int minBars, int maxCapBars = 600)
        {
            if (minBars <= 1) return referenceEndDate;
            var bars = Math.Min(Math.Max(minBars, 1), maxCapBars);


            if (timeframeKey.Equals("Month", StringComparison.OrdinalIgnoreCase))
            {
                // 往回 bars-1 個月，然後對齊到那個月的 1 號
                var dt = referenceEndDate.ToDateTime(TimeOnly.MinValue).AddMonths(-(bars - 1));
                return DateOnly.FromDateTime(new DateTime(dt.Year, dt.Month, 1));
            }


            if (timeframeKey.Equals("Week", StringComparison.OrdinalIgnoreCase))
            {
                // 往回 bars-1 個週（每週 7 日），然後對齊到該月的 1 號（你要求月首一致）
                var dt = referenceEndDate.AddDays(-(bars - 1) * 7).ToDateTime(TimeOnly.MinValue);
                return DateOnly.FromDateTime(new DateTime(dt.Year, dt.Month, 1));
            }


            // 日 / 分K：以天為單位回推，不做額外對齊
            var raw = referenceEndDate.AddDays(-(bars - 1));
            return raw;
        }
    }
}

