using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Core.Settings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MuNTrade.Infrastructure.DataAccess.Selectors
{
    public class TurnoverSymbolSelector : ITurnoverSymbolSelector
    {
        private readonly DataSourceSettings _dataSource;
        private readonly ILogger<TurnoverSymbolSelector> _logger;
        private readonly IHolidayCalendar _holidayCalendar;
        private const int _defaultTopN = 20;

        public TurnoverSymbolSelector(
            IOptions<DataSourceSettings> dataSourceOptions,
            IHolidayCalendar holidayCalendar,
            ILogger<TurnoverSymbolSelector> logger)
        {
            _dataSource = dataSourceOptions.Value;
            _holidayCalendar = holidayCalendar;
            _logger = logger;
        }

        public async Task<IReadOnlyList<string>> GetSymbolsAsync(DateTime tradeDate, int topN = _defaultTopN)
        {
            var snapshot = await GetSnapshotAsync(DateOnly.FromDateTime(tradeDate), topN).ConfigureAwait(false);
            return snapshot.Symbols;
        }

        public async Task<TurnoverSelectionSnapshotResult> GetSnapshotAsync(DateOnly targetTradeDate, int topN = _defaultTopN, CancellationToken ct = default)
        {
            var rankingSnapshotDate = _holidayCalendar.GetPreviousTradingDay(targetTradeDate);
            var filePath = ResolveTurnoverCsvPath(rankingSnapshotDate);

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                _logger.LogWarning(
                    "周轉率排行快照檔案不存在: target_trade_date={TargetTradeDate} ranking_snapshot_date={SnapshotDate} path={Path}",
                    targetTradeDate.ToString("yyyy-MM-dd"),
                    rankingSnapshotDate.ToString("yyyy-MM-dd"),
                    filePath);
                return new TurnoverSelectionSnapshotResult
                {
                    TargetTradeDate = targetTradeDate,
                    RankingSnapshotDate = rankingSnapshotDate,
                    SnapshotFound = false,
                    SnapshotPath = filePath,
                    Symbols = Array.Empty<string>()
                };
            }

            var fileContent = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            using var reader = new StringReader(fileContent);
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
            });

            var symbols = csv.GetRecords<TurnoverRecord>()
                             .OrderByDescending(r => r.Turnover)
                             .Take(topN)
                             .Select(r => r.Symbol.Trim())
                             .ToList();

            _logger.LogInformation(
                "於 target_trade_date={TargetTradeDate} 使用 ranking_snapshot_date={SnapshotDate} 取得 {SymbolCount} 檔標的.",
                targetTradeDate.ToString("yyyy-MM-dd"),
                rankingSnapshotDate.ToString("yyyy-MM-dd"),
                symbols.Count);

            return new TurnoverSelectionSnapshotResult
            {
                TargetTradeDate = targetTradeDate,
                RankingSnapshotDate = rankingSnapshotDate,
                SnapshotFound = true,
                SnapshotPath = filePath,
                Symbols = symbols
            };
        }

        private string ResolveTurnoverCsvPath(DateOnly tradeDate)
        {
            var basePath = _dataSource.TurnoverCsvBasePath;
            if (string.IsNullOrWhiteSpace(basePath))
                return string.Empty;

            var lower = Path.Combine(basePath, $"turnover_{tradeDate:yyyyMMdd}.csv");
            if (File.Exists(lower))
                return lower;

            var upper = Path.Combine(basePath, $"Turnover_{tradeDate:yyyyMMdd}.csv");
            if (File.Exists(upper))
                return upper;

            return lower;
        }
    }
}

