using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Warmup;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Core.Settings;

namespace MuNTrade.Core.Backtesting.Warmup
{
    /// <summary>
    /// 敺璈′蝣???K 璉風?脰???銝血蝻箏摮?? inventory/downloader
    /// 撠?甇?? warmup 瘚???firstTradableDate 銵??    /// </summary>
    public sealed class LocalDiskKLineProvider : IKLineHistoryProvider
    {
        private static readonly JsonSerializerOptions KLineJsonOptions = new(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true
        };

        private readonly ILogger<LocalDiskKLineProvider> _logger;
        private readonly IOptionsMonitor<DataSourceSettings> _dataOptions;
        private readonly IDataInventoryService _inventory;
        private readonly IHistoricalKlineDownloader _downloader;
        private readonly ITradingSessionProvider _sessions;
        private readonly IHolidayCalendar _calendar;
        private readonly IClock _clock;

        public LocalDiskKLineProvider(
            ILogger<LocalDiskKLineProvider> logger,
            IOptionsMonitor<DataSourceSettings> dataOptions,
            IDataInventoryService inventory,
            IHistoricalKlineDownloader downloader,
            ITradingSessionProvider sessions,
            IHolidayCalendar calendar,
            IClock clock)
        {
            _logger = logger;
            _dataOptions = dataOptions;
            _inventory = inventory;
            _downloader = downloader;
            _sessions = sessions;
            _calendar = calendar;
            _clock = clock;
        }

        public async Task<IReadOnlyList<DynamicBar>> PullHistoryAsync(
            string symbol,
            TimeSpan timeFrame,
            DateOnly from,
            DateOnly to,
            int maxLookback,
            CancellationToken ct = default)
        {
            var basePath = _dataOptions.CurrentValue.KLineJsonBasePath;
            if (string.IsNullOrWhiteSpace(basePath))
            {
                _logger.LogWarning("[LocalDiskKLineProvider] KLineJsonBasePath is not set.");
                return Array.Empty<DynamicBar>();
            }

            var dir = Path.Combine(basePath, symbol);
            if (!Directory.Exists(dir))
            {
                _logger.LogWarning("[LocalDiskKLineProvider] Directory not found: {Dir}", dir);
            }

            var timeframeKey = ToTimeframeKey(timeFrame);
            var tfName = ToTimeframeDisplayName(timeFrame);

            await EnsureCoverageAsync(symbol, timeframeKey, from, to, ct);

            var effectiveFrom = await ResolveEffectiveFromAsync(symbol, timeframeKey, from, to, ct);
            if (effectiveFrom > to)
            {
                _logger.LogInformation(
                    "[LocalDiskKLineProvider] Requested range {From} to {To} for {Symbol} ({Tf}) falls before first tradable date. Skip.",
                    from, to, symbol, tfName);
                return Array.Empty<DynamicBar>();
            }

            var prefix = ToTimeframeFilePrefix(timeFrame);
            var files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, $"{prefix}*.json", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();

            var result = new List<DynamicBar>();
            var fromTime = effectiveFrom.ToDateTime(TimeOnly.MinValue);
            var toTime = to.ToDateTime(TimeOnly.MaxValue);

            var targetFiles = new List<string>();
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var parts = name.Split('_');
                if (parts.Length >= 3 &&
                    DateTime.TryParseExact(parts[^2], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var fileStart) &&
                    DateTime.TryParseExact(parts[^1], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var fileEnd) &&
                    fileStart <= toTime &&
                    fileEnd >= fromTime)
                {
                    targetFiles.Add(file);
                }
            }

            if (!targetFiles.Any())
            {
                _logger.LogWarning("[LocalDiskKLineProvider] No KLine files match range {From} to {To} for {Symbol} ({Tf})", effectiveFrom, to, symbol, tfName);
                return Array.Empty<DynamicBar>();
            }

            foreach (var file in targetFiles)
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    var records = await JsonSerializer.DeserializeAsync<List<JsonKLineRecord>>(stream, KLineJsonOptions, ct);
                    if (records == null)
                    {
                        continue;
                    }

                    foreach (var r in records)
                    {
                        if (r.KlineTime >= fromTime && r.KlineTime <= toTime)
                        {
                            result.Add(DynamicBar.FromSnapshot(
                                stockId: symbol,
                                cfg: null!,
                                start: new MuNTrade.Abstractions.Time.ExchangeTs(r.KlineTime),
                                end: new MuNTrade.Abstractions.Time.ExchangeTs(r.KlineTime.Add(timeFrame)),
                                o: (decimal)r.Open,
                                h: (decimal)r.High,
                                l: (decimal)r.Low,
                                c: (decimal)r.Close,
                                vol: r.Qty,
                                ticks: 1,
                                lastTs: new MuNTrade.Abstractions.Time.ExchangeTs(r.KlineTime.Add(timeFrame))));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[LocalDiskKLineProvider] Error reading KLine file {File}", file);
                }
            }

            var ordered = result.OrderBy(x => x.StartTime.Value).ToList();
            if (ordered.Count > maxLookback)
            {
                ordered = ordered.Skip(ordered.Count - maxLookback).ToList();
            }

            _logger.LogInformation("[LocalDiskKLineProvider] Loaded {Count} bars for {Symbol} ({Tf}) from {From} to {To}", ordered.Count, symbol, tfName, effectiveFrom, to);
            return ordered;
        }

        private async Task EnsureCoverageAsync(string symbol, string timeframeKey, DateOnly requestedFrom, DateOnly requestedTo, CancellationToken ct)
        {
            var cov = await _inventory.GetCoverageAsync(symbol, timeframeKey, ct)
                ?? await _inventory.RescanFromDiskAsync(symbol, timeframeKey, ct);

            if (cov.FirstTradableDate is DateOnly firstTradable && requestedTo < firstTradable)
            {
                return;
            }

            var effectiveRequestedFrom = cov.FirstTradableDate is DateOnly ft && requestedFrom < ft
                ? ft
                : requestedFrom;
            if (effectiveRequestedFrom > requestedTo)
            {
                return;
            }

            var requiredDays = Math.Max(1, requestedTo.DayNumber - effectiveRequestedFrom.DayNumber + 1);
            var lcd = GetLastClosedDate(symbol);
            var gap = _inventory.ComputeMissingByDay(cov, requestedTo < lcd ? requestedTo : lcd, requiredDays);
            if (gap is null)
            {
                return;
            }

            var downloadFrom = gap.Value.GapFrom < effectiveRequestedFrom
                ? effectiveRequestedFrom
                : gap.Value.GapFrom;
            if (downloadFrom > requestedTo)
            {
                return;
            }

            _logger.LogInformation(
                "[LocalDiskKLineProvider] Missing coverage detected for {Symbol} ({Tf}) {From}~{To}. Downloading {DownloadFrom}~{DownloadTo}.",
                symbol, timeframeKey, effectiveRequestedFrom, requestedTo, downloadFrom, requestedTo);

            await _downloader.DownloadAsync(symbol, timeframeKey, downloadFrom, requestedTo, ct);
        }

        private async Task<DateOnly> ResolveEffectiveFromAsync(string symbol, string timeframeKey, DateOnly requestedFrom, DateOnly requestedTo, CancellationToken ct)
        {
            var cov = await _inventory.GetCoverageAsync(symbol, timeframeKey, ct)
                ?? await _inventory.RescanFromDiskAsync(symbol, timeframeKey, ct);

            if (cov.FirstTradableDate is DateOnly firstTradable && requestedFrom < firstTradable)
            {
                _logger.LogDebug(
                    "[LocalDiskKLineProvider] Applying firstTradableDate={FirstTradable} for {Symbol} ({Tf}). Request {RequestedFrom} -> {EffectiveFrom}.",
                    firstTradable, symbol, timeframeKey, requestedFrom, firstTradable);
                return firstTradable;
            }

            return requestedFrom;
        }

        private DateOnly GetLastClosedDate(string symbol)
        {
            var nowLocal = _clock.Now;
            var today = DateOnly.FromDateTime(nowLocal);
            var sessNow = _sessions.GetSession(symbol, nowLocal);
            var probeTime = new DateTime(today.Year, today.Month, today.Day, 15, 0, 0);
            var sessDay = _sessions.GetSession(symbol, probeTime);
            var isTradingDayToday = !_calendar.IsHoliday(today);

            bool afterSessionEnd;
            if (sessDay != null && sessDay.TradeDate == today)
            {
                afterSessionEnd = nowLocal >= sessDay.EndTime;
            }
            else if (sessNow != null && sessNow.TradeDate == today)
            {
                afterSessionEnd = nowLocal >= sessNow.EndTime;
            }
            else
            {
                afterSessionEnd = nowLocal.TimeOfDay >= new TimeSpan(14, 0, 0);
            }

            return isTradingDayToday && afterSessionEnd
                ? today
                : _calendar.GetPreviousTradingDay(today);
        }

        private static string ToTimeframeKey(TimeSpan timeFrame) => timeFrame.TotalMinutes switch
        {
            1 => "Time_1",
            5 => "Time_5",
            60 => "Time_60",
            _ => timeFrame.Ticks == TimeSpan.TicksPerDay
                ? "Day"
                : timeFrame.Ticks == TimeSpan.TicksPerDay * 7
                    ? "Week"
                    : throw new ArgumentOutOfRangeException(nameof(timeFrame), timeFrame, "Unsupported backtest warmup timeframe.")
        };

        private static string ToTimeframeDisplayName(TimeSpan timeFrame) => timeFrame.TotalMinutes switch
        {
            1 => "1 分",
            5 => "5 分",
            60 => "60 分",
            _ => timeFrame.Ticks == TimeSpan.TicksPerDay
                ? "日"
                : timeFrame.Ticks == TimeSpan.TicksPerDay * 7
                    ? "週"
                    : $"{timeFrame.TotalMinutes} 分"
        };

        private static string ToTimeframeFilePrefix(TimeSpan timeFrame) => timeFrame.TotalMinutes switch
        {
            1 => "1 分 K 線",
            5 => "5 分 K 線",
            60 => "60 分 K 線",
            _ => timeFrame.Ticks == TimeSpan.TicksPerDay
                ? "日 K 線"
                : timeFrame.Ticks == TimeSpan.TicksPerDay * 7
                    ? "週 K 線"
                    : throw new ArgumentOutOfRangeException(nameof(timeFrame), timeFrame, "Unsupported backtest warmup timeframe.")
        };
    }
}
