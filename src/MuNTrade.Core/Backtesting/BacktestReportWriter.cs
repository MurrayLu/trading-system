using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MuNTrade.Core.Settings;

namespace MuNTrade.Core.Backtesting
{
    public sealed record BacktestRunSummary(
        DateOnly TradeDate,
        string Symbol,
        int Trades,
        decimal NetPnL,
        double WinRate,
        string Stage,
        string ReportMode,
        bool Skipped,
        string? SkipReason);

    /// <summary>
    /// 回測報表輸出（C1/C2 共用 summary；C1 另輸出詳細 jsonl）。
    /// </summary>
    public sealed class BacktestReportWriter
    {
        private readonly IOptionsMonitor<BacktestSettings> _options;
        private readonly object _sync = new();

        public BacktestReportWriter(IOptionsMonitor<BacktestSettings> options)
        {
            _options = options;
        }

        public Task AppendSummaryAsync(BacktestRunSummary summary, CancellationToken ct = default)
        {
            var root = ResolveRoot();
            Directory.CreateDirectory(root);
            var filePath = Path.Combine(root, "backtest_summary.csv");

            lock (_sync)
            {
                var writeHeader = !File.Exists(filePath);
                using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, Encoding.UTF8);

                if (writeHeader)
                {
                    writer.WriteLine("TradeDate,Symbol,Trades,NetPnL,WinRate,Stage,ReportMode,Skipped,SkipReason");
                }

                var line = string.Join(",",
                    summary.TradeDate.ToString("yyyy-MM-dd"),
                    summary.Symbol,
                    summary.Trades.ToString(CultureInfo.InvariantCulture),
                    summary.NetPnL.ToString(CultureInfo.InvariantCulture),
                    summary.WinRate.ToString(CultureInfo.InvariantCulture),
                    summary.Stage,
                    summary.ReportMode,
                    summary.Skipped ? "1" : "0",
                    Escape(summary.SkipReason));

                writer.WriteLine(line);
            }

            return Task.CompletedTask;
        }

        public string GetDetailPath(DateOnly tradeDate, string symbol)
        {
            var root = ResolveRoot();
            var dir = Path.Combine(root, "details", tradeDate.ToString("yyyyMMdd"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"{symbol}.jsonl");
        }

        public string GetSummaryPath()
        {
            var root = ResolveRoot();
            return Path.Combine(root, "backtest_summary.csv");
        }

        public Task WriteDetailAsync(string path, BacktestEventJournal journal, CancellationToken ct = default)
        {
            journal.FlushTo(path);
            return Task.CompletedTask;
        }

        private static string Escape(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var v = value.Replace("\"", "\"\"");
            return $"\"{v}\"";
        }

        private string ResolveRoot()
        {
            var root = _options.CurrentValue.ReportRoot;
            return string.IsNullOrWhiteSpace(root)
                ? Path.Combine(Directory.GetCurrentDirectory(), "Reports")
                : root;
        }
    }
}

