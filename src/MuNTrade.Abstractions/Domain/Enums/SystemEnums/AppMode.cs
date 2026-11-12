using System;

namespace MuNTrade.Abstractions.Domain.Enums.SystemEnums
{
    /// <summary>
    /// 定義應用程式的核心運行模式。
    /// </summary>
    public enum AppMode
    {
        /// <summary>
        /// 執行模式 (包含實盤 LIVE、模擬 PAPER、與歷史重播 REPLAY)
        /// </summary>
        Execution,

        /// <summary>
        /// Replay Training 模式：由 CLI 啟動訓練器 UI，使用歷史資料做人工盤感與進出場訓練。
        /// 不連 broker、不啟動 Execution、不啟動正式策略 host。
        /// </summary>
        ReplayTraining,

        /// <summary>
        /// 回測模式 (批量跑分)
        /// </summary>
        Backtest,

        /// <summary>
        /// Daytrade governed experimentation 的本地 console 宿主模式。
        /// 不屬於 execution host，且不包含 UI、broker、live trading services。
        /// </summary>
        GovernedDaytradeBatch,

        /// <summary>
        /// 最佳化模式 (參數尋優)
        /// </summary>
        Optimize
    }
}

