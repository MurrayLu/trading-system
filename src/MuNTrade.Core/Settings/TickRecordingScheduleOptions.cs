using System;
using System.Collections.Generic;

namespace MuNTrade.Core.Settings
{
    public sealed class TickRecordingScheduleOptions
    {
        public bool Enabled { get; set; } = false;
        public TimeSpan TriggerTime { get; set; } = new(14, 45, 0);

        public List<string> Symbols { get; set; } = new();
        public List<string> SymbolsFilePaths { get; set; } = new();

        public int MaxSymbolsPerRun { get; set; } = 0; // 0 = all
        public int HistoryIdleMs { get; set; } = 1500;
        public int MaxWaitMs { get; set; } = 20000;
        public int BetweenSymbolsDelayMs { get; set; } = 200;
        public int StaticDataWaitMs { get; set; } = 1500;
        public bool AllowUnsubscribedStaticData { get; set; } = true;
        public bool ExclusiveMode { get; set; } = false;
        public bool RestorePreviousSubscriptions { get; set; } = true;
        public int ExclusivePreDelayMs { get; set; } = 500;
        public int ExclusivePostDelayMs { get; set; } = 500;
        public int UnsubscribeRetryCount { get; set; } = 3;
        public int UnsubscribeRetryDelayMs { get; set; } = 200;

        public bool SkipIfAlreadySubscribed { get; set; } = true;

        // 若為 true，凡是有訂閱 ticks 的品種都會自動寫檔（平常也會存）
        public bool AlwaysRecordSubscribedTicks { get; set; } = false;

        // [Memory-only Session Snapshot] 純記憶體 session 模式（不產生 .csv.part）
        // tick 全部保留在記憶體，閒置超過門檻時直接覆寫 .csv.gz。
        // 若 process crash，最後一次 snapshot 之後的 tick 會遺失。
        public int TickIdleSnapshotSeconds { get; set; } = 5;

        // 即使持續有 tick，也至少每 N 秒強制寫一次快照。
        public int TickPeriodicSnapshotSeconds { get; set; } = 60;

        // 保留此設定讓 FileTickPlayer 在讀取時仍可選擇是否 fallback 到 .part（migration 用）
        public bool TickAllowReadLivePartialFiles { get; set; } = false;
    }
}

