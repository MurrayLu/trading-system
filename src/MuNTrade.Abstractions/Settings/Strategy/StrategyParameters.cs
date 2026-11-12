// MuNTrade.Abstractions/Settings/StrategyParameters.cs  (extended)
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;

namespace MuNTrade.Abstractions.Settings.Strategy
{
    /// <summary>
    /// Legacy strategy parameter bag from the pre-governed optimization path.
    /// It is frozen for compatibility only.
    /// Do not add new Daytrade governance or AI adapter features here.
    /// </summary>
    [Obsolete("Legacy strategy parameter bag. Do not use for Daytrade parameter governance.")]
    public record StrategyParameters
    {
        // 事件 E1
        public double D1 { get; init; } = 60.0;
        public int N1 { get; init; } = 2;

        // 事件 E2
        public int C1 { get; init; } = 3;

        // 事件 E3
        public int T1 { get; init; } = 4;
        public int T2 { get; init; } = 15;
        public int R1 { get; init; } = 3;

        // 事件 E5
        public double D2 { get; init; } = 15.0;

        // 你新增/優化要用的欄位 ↓
        public int TickLimitBase { get; init; } = 100;
        public long VolumeLimit { get; init; } = 10000;

        // 風險管理
        public int SL { get; init; } = 11;
        public int TP { get; init; } = 30;
        public double ATR_M { get; init; } = 2.0;
        public int ATR_L { get; init; } = 20;

        // 部位管理
        public double POS_S { get; init; } = 1.0;
        public double POS_T { get; init; } = 0.5;
        public double POS_A { get; init; } = 0.5;

        // 多空操作
        public TradingMode TradingMode { get; set; } = TradingMode.LongOnly;

        // 統計窗口
        public int LOOKBACK { get; init; } = 5;

        // Pyramiding
        public bool AllowPyramiding { get; set; } = false;
        public int MaxLayers { get; init; } = 3;

        // 反手邏輯
        public bool ForceFlatBeforeReverse { get; init; } = true;
        public int ReverseCooldownSec { get; init; } = 30;

    }
}

