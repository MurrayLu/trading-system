using System;
using System.Collections.Generic;
using MuNTrade.Core.Settings;

namespace MuNTrade.Core.Backtesting
{
    public enum BacktestProgressPhase
    {
        Idle = 0,
        Loading = 1,
        Running = 2,
        WritingReport = 3,
        Done = 4,
        Error = 5
    }

    [Obsolete("Legacy backtest request. Do not use for Daytrade entry.")]
    public sealed record BacktestRunRequest
    {
        public string ReportMode { get; init; } = "C2";
        public string Stage { get; init; } = "Explore";
        public string TimeMode { get; init; } = "Range";
        public DateOnly StartDate { get; init; }
        public DateOnly EndDate { get; init; }
        public string UniverseMode { get; init; } = "SingleSymbol";
        public string? Symbol { get; init; }
        public List<string> ManualList { get; init; } = new();
        public int TurnoverTopN { get; init; } = 50;
        public string StrategyTemplateId { get; init; } = string.Empty;
        public string AccountKey { get; init; } = "Sim:Paper:All:Default";
        public string ReportRoot { get; init; } = "Reports";
        public string? DatasetSegment { get; init; }
        public string? ReplaySource { get; init; }
        public bool SkipMissingData { get; init; } = true;
        public BacktestRiskOptions Risk { get; init; } = new();
        public BacktestExecutionOptions Execution { get; init; } = new();
    }

    public sealed record BacktestExecutionTrace
    {
        public string? DatasetSegment { get; set; }
        public string? ReplaySource { get; set; }
        public string? UniverseMode { get; set; }
        public int? TurnoverTopNBoundary { get; set; }
        public DateOnly? RankingSnapshotDate { get; set; }
        public string? RankingSnapshotPath { get; set; }
        public bool TurnoverSelectionActivated { get; set; }
        public bool WarmupActivated { get; set; }
        public bool DataInventoryActivated { get; set; }
        public bool TickReplayActivated { get; set; }
        public BacktestMissingDataSummary MissingDataSummary { get; set; } = new();
    }

    public sealed record BacktestMissingDataSummary
    {
        public int TotalTradingDays { get; init; }
        public int ExecutedTradingDays { get; init; }
        public int SkippedTradingDays { get; init; }
        public int MissingTurnoverSnapshotDays { get; init; }
        public int MissingTickReplayDays { get; init; }
        public int MissingWarmupDays { get; init; }
        public IReadOnlyList<BacktestMissingDataDay> Details { get; init; } = Array.Empty<BacktestMissingDataDay>();
    }

    public sealed record BacktestMissingDataDay
    {
        public DateOnly TradeDate { get; init; }
        public DateOnly? RankingSnapshotDate { get; init; }
        public string? ExpectedPath { get; init; }
        public required string MissingReason { get; init; }
        public required string Severity { get; init; }
        public string? Notes { get; init; }
    }

    public sealed record BacktestProgress
    {
        public BacktestProgressPhase Phase { get; init; } = BacktestProgressPhase.Idle;
        public int DayIndex { get; init; }
        public int DayTotal { get; init; }
        public int SymbolIndex { get; init; }
        public int SymbolTotal { get; init; }
        public long TicksProcessed { get; init; }
        public DateOnly? CurrentTradeDate { get; init; }
        public string? CurrentSymbol { get; init; }
        public DateTime? CurrentTickTs { get; init; }
        public int Trades { get; init; }
        public decimal NetPnL { get; init; }
        public double WinRate { get; init; }
        public int SuppressedRisk { get; init; }
        public int SuppressedTime { get; init; }
        public int SuppressedNeutral { get; init; }
        public int SuppressedTTL { get; init; }
        public int SuppressedNoData { get; init; }
        public int Skipped { get; init; }
        public string? Message { get; init; }
    }

    public sealed record BacktestSkipInfo(DateOnly TradeDate, string Symbol, string Reason);

    public sealed record BacktestResult
    {
        public bool Succeeded { get; init; }
        public string? SummaryPath { get; init; }
        public BacktestExecutionTrace? ExecutionTrace { get; set; }
        public List<string> DetailPaths { get; init; } = new();
        public List<string> EventJournalPaths { get; init; } = new();
        public List<BacktestRunSummary> Summaries { get; init; } = new();
        public List<BacktestSkipInfo> Skips { get; init; } = new();
        public List<string> Errors { get; init; } = new();
    }
}

