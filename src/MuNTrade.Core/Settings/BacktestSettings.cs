using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using System.Collections.Generic;

namespace MuNTrade.Core.Settings
{
    public class BacktestSettings
    {
        public string DataSourceType { get; set; } = "Tick";
        public string Stage { get; set; } = "Explore"; // Explore | Strict
        public string TimeFrame { get; set; } = "1 分 K 線";
        public List<string> StockIds { get; set; } = new List<string>();
        public string StartDate { get; set; } = string.Empty;
        public string EndDate { get; set; } = string.Empty;
        public string Session { get; set; } = "D";
        public FillSettings FillSettings { get; set; } = new FillSettings();
        /// <summary>
        /// 存放此次回測要使用的策略參數。
        /// </summary>
        public StrategyParameters StrategyParameters { get; set; } = new();

        // --- Backtest Host Extensions ---
        public string UniverseMode { get; set; } = "SingleSymbol"; // SingleSymbol | TurnoverList
        public string TimeMode { get; set; } = "Range";            // SingleDay | Range
        public string ReportMode { get; set; } = "C2";             // C1 | C2
        public int TurnoverTopN { get; set; } = 50;
        public string StrategyTemplateId { get; set; } = string.Empty;
        public string AccountKey { get; set; } = "Sim:Paper:All:Default";
        public string ReportRoot { get; set; } = "Reports";
        /// <summary>Optional root for paper strategy-ledger events; null preserves the legacy Config/History path.</summary>
        public string? StrategyEventRoot { get; set; }
        public bool SkipMissingData { get; set; } = true;
        public BacktestRiskOptions Risk { get; set; } = new();
        public BacktestExecutionOptions Execution { get; set; } = new();
    }

    public class FillSettings
    {
        public InstrumentFillSettings Futures { get; set; } = new InstrumentFillSettings();
        public InstrumentFillSettings Stocks { get; set; } = new InstrumentFillSettings();
    }

    public class InstrumentFillSettings
    {
        public decimal CommissionPerContract { get; set; }
        public decimal CommissionRate { get; set; }
        public decimal TaxRate { get; set; }
        public decimal SlippagePoints { get; set; }
        public decimal SlippagePercent { get; set; }
    }

    public class BacktestRiskOptions
    {
        public decimal DailyLossLimit { get; set; } = 0m;
        public int ConsecutiveLossLimit { get; set; } = 0;
        public int MaxTradesPerDay { get; set; } = 0;
        public int DelayOpenMinutes { get; set; } = 0;
        public int StopNewEntryAfterMinutes { get; set; } = 180;
        public string OverLossBehavior { get; set; } = "Lock"; // Lock | LogOnly
    }

    public class BacktestExecutionOptions
    {
        public string FillModel { get; set; } = "LastPrice"; // LastPrice | ConservativeSlippageTicks
        public int LimitOffsetTicks { get; set; } = 0;
        public int TtlSeconds { get; set; } = 3;
        public decimal ConservativeSlippageTicks { get; set; } = 0m;
        public decimal Fees { get; set; } = 0m;
        public decimal Tax { get; set; } = 0m;
    }

}

