using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Core.Settings;
using MuNTrade.Abstractions.Time;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Infrastructure.DataAccess.FileSystem;

public class FileTickPlayer : ITickSource
{
    private readonly ILogger<FileTickPlayer> _logger;
    private readonly DataSourceSettings _settings;

    public FileTickPlayer(ILogger<FileTickPlayer> logger, IOptions<DataSourceSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    public async IAsyncEnumerable<TickData> StreamTicksAsync(
            string symbol, DateTime startDate, DateTime endDate, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            foreach (var filePath in EnumerateCandidateTickPaths(symbol, date))
            {
                if (cancellationToken.IsCancellationRequested) yield break;

                if (!File.Exists(filePath)) continue;

                _logger.LogDebug("[FileTickPlayer] Reading ticks from: {Path}", filePath);

                using var reader = OpenTickReader(filePath);
                // 跳過檔頭
                await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                {
                    if (cancellationToken.IsCancellationRequested) yield break;

                    var parts = line.Trim().TrimStart('\uFEFF').Split(',');
                    if (parts.Length < 5) continue;

                    if (DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                    {
                        if (timestamp > endDate)
                        {
                            break;
                        }

                        if (timestamp >= startDate && timestamp <= endDate)
                        {
                            yield return new TickData
                            {
                                StockId = symbol,
                                ExchangeTs = new ExchangeTs(timestamp),
                                ArrivalTs = new ArrivalTs(timestamp),
                                Price = decimal.Parse(parts[3], CultureInfo.InvariantCulture),
                                Volume = long.Parse(parts[4], CultureInfo.InvariantCulture)
                            };
                        }
                    }
                }
            }
        }
    }

    private IEnumerable<string> EnumerateCandidateTickPaths(string symbol, DateTime date)
    {
        string symbolDir = Path.Combine(_settings.TickDataBasePath, symbol);
        if (!Directory.Exists(symbolDir)) yield break;

        var sessions = new[] { "_D", "_N" };
        foreach (var sess in sessions)
        {
            string prefix = $"{date:yyyyMMdd}{sess}_{symbol}";

            string sessionGz = Path.Combine(symbolDir, prefix + ".csv.gz");

            // A. 主路徑：正式 .csv.gz（memory-only 模式的唯一正式輸出）
            if (File.Exists(sessionGz))
            {
                yield return sessionGz;
                continue;
            }

            // B. Migration fallback：只在 TickAllowReadLivePartialFiles = true 且正式 .gz 不存在時
            // Read legacy .csv.part files for compatibility; the current recorder does not create them.
            string sessionPart = Path.Combine(symbolDir, prefix + ".csv.part");
            if (_settings.TickAllowReadLivePartialFiles && File.Exists(sessionPart))
            {
                yield return sessionPart;
                continue;
            }

            // C. 若單檔 session 格式皆不存在，最後才 Fallback 到舊的分段式格式
            var filesInDir = Directory.GetFiles(symbolDir, prefix + "_*"); // 加上 _ 避免匹配到單檔格式
            var segmentedGzs = new Dictionary<string, string>();
            foreach (var f in filesInDir)
            {
                var name = Path.GetFileName(f);
                if (IsSegmentedTickFileName(name, prefix, out var key))
                {
                    segmentedGzs[key] = f;
                }
            }

            if (segmentedGzs.Count > 0)
            {
                foreach (var k in segmentedGzs.Keys.OrderBy(k => k))
                {
                    yield return segmentedGzs[k];
                }
            }
        }
    }

    private static bool IsSegmentedTickFileName(string fileName, string prefix, out string segmentKey)
    {
        segmentKey = "";
        if (!fileName.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase)) return false;
        if (!fileName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)) return false;

        var hhmmss = fileName.Substring(prefix.Length + 1, 6);
        if (hhmmss.Length == 6 && long.TryParse(hhmmss, out _))
        {
            segmentKey = hhmmss;
            return true;
        }
        return false;
    }

    private StreamReader OpenTickReader(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            var gs = new GZipStream(fs, CompressionMode.Decompress);
            return new StreamReader(gs, Encoding.UTF8);
        }
        return new StreamReader(fs, Encoding.UTF8);
    }

    public async IAsyncEnumerable<DynamicBar> StreamKLinesAsync(
                string stockId, int timeFrame, DateTime date, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string targetFile = Path.Combine(_settings.KLineJsonBasePath, stockId, $"{timeFrame}", $"{date:yyyyMMdd}.json");
        if (!File.Exists(targetFile)) yield break;

        List<JsonKLineRecord>? records = null;
        try
        {
            var json = await File.ReadAllTextAsync(targetFile, cancellationToken);
            records = JsonSerializer.Deserialize<List<JsonKLineRecord>>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "讀取或解析 K線 JSON 檔案時發生錯誤: {FilePath}", targetFile);
        }

        if (records is null) yield break;

        var cfg = new BarConfig { Type = "Time", Period = timeFrame };

        foreach (var record in records)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var start = record.KlineTime;
            var end = start.AddMinutes(timeFrame);

            // 用 FromSnapshot 建 bar（不會觸發任何事件；只是資料載入）
            var bar = DynamicBar.FromSnapshot(
                stockId, cfg,
                new ExchangeTs(start), new ExchangeTs(end),
                record.Open, record.High, record.Low, record.Close,
                record.Volume,
                ticks: 1,
                lastTs: new ExchangeTs(start)
            );

            yield return bar;
        }
    }

    private record JsonKLineRecord(DateTime KlineTime, decimal Open, decimal High, decimal Low, decimal Close, long Volume);
}

