using System;

namespace MuNTrade.Core.Settings
{
    /// <summary>
    /// TickStreamOptions 控制逐筆成交在後端的緩衝與發佈節奏。
    /// </summary>
    public sealed class TickStreamOptions
    {
        /// <summary>
        /// 當成交時間落後目前伺服器時間超過此秒數時視為回放狀態。
        /// </summary>
        public int CatchupLagThresholdSec { get; set; } = 10;

        /// <summary>
        /// 從回放轉回即時時允許的時間差。
        /// </summary>
        public int CatchupResumeSlackSec { get; set; } = 2;

        /// <summary>
        /// 回放模式下每批最大筆數。
        /// </summary>
        public int CatchupBatchSize { get; set; } = 500;

        /// <summary>
        /// 回放模式下最長多久要送出一批，即使未達批量。
        /// </summary>
        public int CatchupFlushIntervalMs { get; set; } = 200;

        /// <summary>
        /// 即時模式下每批最大筆數。
        /// </summary>
        public int LiveBatchSize { get; set; } = 60;

        /// <summary>
        /// 即時模式下最長多久要送出一批。
        /// </summary>
        public int LiveFlushIntervalMs { get; set; } = 50;

        /// <summary>
        /// 每檔在記憶體保留的最大逐筆數量，超過則淘汰最舊資料。
        /// </summary>
        public int MaxTicksPerSymbol { get; set; } = 120_000;

        /// <summary>
        /// 針對 UI 額外資訊（例如進度指示）多久發一次。
        /// </summary>
        public int ProgressUpdateIntervalMs { get; set; } = 500;
    }
}

