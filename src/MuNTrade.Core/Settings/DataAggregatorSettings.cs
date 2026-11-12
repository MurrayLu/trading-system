using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy; // <-- BarConfig
using System.Collections.Generic;

namespace MuNTrade.Core.Settings
{
    /// <summary>
    /// 這個類別只負責從 appsettings.json 讀取聚合器的設定。
    /// </summary>
    public class DataAggregatorSettings
    {
        /// <summary>要訂閱/處理的商品清單（LIVE 模式用）</summary>
        public List<string> EnabledSymbols { get; set; } = new List<string>();
        /// <summary>
        /// 啟用的 Bar 設定 (來自契約專案的 BarConfig)
        /// </summary>
        public List<BarConfig> EnabledBarConfigs { get; set; } = new List<BarConfig>();

        /// <summary>
        /// partial 的最小節流間隔 (ms)
        /// </summary>
        public int PartialMinIntervalMs { get; set; } = 100;

        /// <summary>
        /// 控制要 publish 哪些 type 的 partial。
        /// - 空陣列或 null 表示允許所有 type（預設行為）。
        /// - 若設定為 e.g. ["Day","Week","Month"]，則會只發這些 type 的 partial（Time 會被過濾掉）。
        /// </summary>
        public string[] PartialPublishTypes { get; set; } = new string[0];

        /// <summary>
        /// Best5 節流間隔 (ms)。預設 30ms。
        /// </summary>
        public int Best5ThrottleMs { get; set; } = 30;
    }
}

