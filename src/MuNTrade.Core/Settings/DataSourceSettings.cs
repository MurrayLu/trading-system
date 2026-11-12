namespace MuNTrade.Core.Settings
{
    public class DataSourceSettings
    {
        public string TickDataBasePath { get; set; } = string.Empty;
        public string KLineJsonBasePath { get; set; } = string.Empty;
        public string WebCsvBasePath { get; set; } = string.Empty;
        public string HolidayCalendarPath { get; set; } = string.Empty;
        public string TurnoverCsvBasePath { get; init; } = string.Empty;
        public string StockListBasePath { get; init; } = string.Empty;

        // New Configuration Paths
        public string SimAccountDataPath { get; set; } = string.Empty;
        public string LiveAccountDataPath { get; set; } = string.Empty;
        public string TradeRulesDataPath { get; set; } = string.Empty;

        // [Segmented Recording] 讀取設定 (R3)
        public bool TickAllowReadLivePartialFiles { get; set; } = false;
    }
}

