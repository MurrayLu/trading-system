namespace MuNTrade.Abstractions.Settings.Trading
{
    public sealed class DayTradingRules
    {
        public int DailyTradeLimit { get; set; } = 12;
        public int MaxConsecutiveLosses { get; set; } = 3;
        public decimal MaxLossPerTrade { get; set; } = 800m;
        public decimal MaxIntradayDrawdownPct { get; set; } = 0.02m;
        public decimal PerSymbolExposureLimit { get; set; } = 0.35m;
        public int AutoLockDurationSeconds { get; set; } = 1800;
        public int MinLatencyGuardMs { get; set; } = 1500;
        public int SlippageGuardTicks { get; set; } = 8;
    }
}

