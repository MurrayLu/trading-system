// MuNTrade.Infrastructure/DataAccess/Exporters/CsvResultExporter.cs (v7.5 - 扁平化修正版)

using CsvHelper;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace MuNTrade.Infrastructure.DataAccess.Exporters
{
    public class CsvResultExporter : IResultExporter
    {
        private readonly string _outputPath;
        private readonly IClock _clock;

        public CsvResultExporter(IOptions<ExporterSettings> exporterOptions, IClock clock)
        {
            _outputPath = exporterOptions.Value?.CsvOutputPath ?? ".";
            _clock = clock;
        }

        public async Task ExportAsync(IEnumerable<OptimizationResult> results, CancellationToken ct = default)
        {
            if (!results.Any()) return;

            Directory.CreateDirectory(_outputPath);
            var fileName = $"OptResults_{_clock.Now:yyyyMMdd_HHmmss}.csv";
            var filePath = Path.Combine(_outputPath, fileName);
            using var writer = new StreamWriter(filePath);
            using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

            csv.WriteField("JobId");
            csv.WriteField("GrossPnl"); 
            csv.WriteField("NetPnl");
            csv.WriteField("Sharpe");
            csv.WriteField("TotalTrades");
            csv.WriteField("WinRate");
            csv.WriteField(nameof(OptimizationResult.TradingMode)); // <-- 新增
            // -- Strategy Params --
            csv.WriteField(nameof(OptimizationResult.D1));
            csv.WriteField(nameof(OptimizationResult.N1));
            csv.WriteField(nameof(OptimizationResult.SL));
            csv.WriteField(nameof(OptimizationResult.TP));
            await csv.NextRecordAsync();

            foreach (var r in results)
            {
                ct.ThrowIfCancellationRequested();

                csv.WriteField(r.JobId);
                csv.WriteField(r.GrossPnL);
                csv.WriteField(r.NetPnL);
                csv.WriteField(r.Sharpe);
                csv.WriteField(r.TotalTrades);
                csv.WriteField(r.WinRate);
                csv.WriteField(r.TradingMode.ToString()); // <-- 新增 (使用 .ToString() 轉為字串)
                // -- Strategy Params --
                csv.WriteField(r.D1);
                csv.WriteField(r.N1);
                csv.WriteField(r.SL);
                csv.WriteField(r.TP);
                await csv.NextRecordAsync();
            }
        }
    }
}

