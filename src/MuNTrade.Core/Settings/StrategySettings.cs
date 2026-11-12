using System;
using System.Collections.Generic;

namespace MuNTrade.Core.Settings
{
    public class StrategySettings
    {
        public List<string> Watchlist { get; set; } = new List<string>();
        public double DefaultStopLossPercentage { get; set; }
        public double DefaultTakeProfitPercentage { get; set; }

        /// <summary>
        /// 允許的最早進場時間 (時:分:秒)。
        /// </summary>
        public TimeSpan FirstEntryTime { get; set; } = new(9, 0, 0); // 預設 09:03:00

        /// <summary>
        /// 允許的最後進場時間 (時:分:秒)。
        /// </summary>
        public TimeSpan LastEntryTime { get; set; } = new(13, 0, 0); // 預設 10:00:00
    }
}
