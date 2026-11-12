using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Infrastructure.DataAccess.FileSystem;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Warmup;

namespace MuNTrade.Infrastructure.DataAccess.Warmup
{
    public sealed class HistoryWarmupService : IHistoryWarmupService
    {
        private readonly ILogger<HistoryWarmupService> _log;
        private readonly IWarmupPlanner _planner;
        private readonly IDataInventoryService _inventory;
        private readonly IHistoricalKlineDownloader _downloader;
        private readonly HistoricalDataReader _reader;
        private readonly ITradingSessionProvider _sessions;
        private readonly IHolidayCalendar _calendar;
        private readonly IClock _clock;

        public HistoryWarmupService(
            ILogger<HistoryWarmupService> log,
            IWarmupPlanner planner,
            IDataInventoryService inventory,
            IHistoricalKlineDownloader downloader,
            HistoricalDataReader reader,
            ITradingSessionProvider sessions,  // 交易時段提供者（含夜盤處理）
            IHolidayCalendar calendar,         // 交易日曆（含休市/補班）
            IClock clock)
        {
            _log = log;
            _planner = planner;
            _inventory = inventory;
            _downloader = downloader;
            _reader = reader;
            _sessions = sessions;
            _calendar = calendar;
            _clock = clock;
        }

        /// <summary>
        /// 主暖機流程：
        /// 1) 依 planner 取得每個 timeframe 的 WarmupPlan
        /// 2) 讀取 coverage (manifest)，若無則重掃
        /// 3) 計算缺口 (ComputeMissingByDay) —— 以「交易日曆 + 交易時段」計算 LCD
        /// 4) 若有缺口，依 timeframe 與 RequiredBars 調整起始日（已存在 coverage -> 僅補缺口，無 coverage -> cold start 回推）
        ///    注意：因為檔案是以「月份」為單位儲存，所以在需要對齊時會把 start 對齊到該月第一日，left-gap 的 end 也會對齊到對應月份的最後一日
        /// 5) 下載後重掃 manifest，最後讀取並回傳最近 RequiredBars 根 K
        /// </summary>
        public async Task<Dictionary<string, List<JsonKLineRecord>>> WarmupAsync(string symbol, CancellationToken ct = default)
        {
            var nowLocal = _clock.Now;
            var today = DateOnly.FromDateTime(nowLocal);

            // 取得當下 session（可能是夜盤）
            var sessNow = _sessions.GetSession(symbol, nowLocal);
            _log.LogDebug("[Warmup][DEBUG] sessionNow: TradeDate={TradeDate}, Start={Start}, End={End}",
                sessNow?.TradeDate, sessNow?.StartTime, sessNow?.EndTime);

            // 判斷今天是否為交易日
            bool isTradingDayToday = !_calendar.IsHoliday(today);

            // 嘗試取得「今日的日盤 session」：用 probeTime（15:00）去抓屬於今天的那一個 session（若 provider 支援）
            var probeTime = new DateTime(today.Year, today.Month, today.Day, 15, 0, 0);
            var sessDay = _sessions.GetSession(symbol, probeTime);
            _log.LogDebug("[Warmup][DEBUG] sessionDay(probe {ProbeTime}): TradeDate={TradeDate}, Start={Start}, End={End}",
                probeTime, sessDay?.TradeDate, sessDay?.StartTime, sessDay?.EndTime);

            // 判斷今天日盤是否已完全收盤（優先使用 sessDay）
            bool afterSessionEnd;
            if (sessDay != null && sessDay.TradeDate == today)
            {
                // 若存在今天的日盤 session，使用它的 EndTime 判斷
                afterSessionEnd = nowLocal >= sessDay.EndTime;
            }
            else if (sessNow != null && sessNow.TradeDate == today)
            {
                // 否則如果當下 session 的 tradeDate 剛好是今天，使用它判斷
                afterSessionEnd = nowLocal >= sessNow.EndTime;
            }
            else
            {
                // fallback：保守啟發式（和既有的一致）
                afterSessionEnd = nowLocal.TimeOfDay >= new TimeSpan(14, 0, 0);
            }

            DateOnly lcd = (isTradingDayToday && afterSessionEnd)
                ? today
                : _calendar.GetPreviousTradingDay(today);

            _log.LogDebug("[Warmup] {Symbol} 使用交易日曆計算 LCD={LCD} (isTradingDayToday={IsTradingDayToday}, afterSessionEnd={AfterSessionEnd})",
                symbol, lcd, isTradingDayToday, afterSessionEnd);

            var result = new Dictionary<string, List<JsonKLineRecord>>(StringComparer.OrdinalIgnoreCase);

            foreach (var plan in _planner.GetPlans(symbol))
            {
                ct.ThrowIfCancellationRequested();

                // 1) 取得 coverage（若不存在就重掃）
                var cov = await _inventory.GetCoverageAsync(symbol, plan.TimeframeKey, ct)
                          ?? await _inventory.RescanFromDiskAsync(symbol, plan.TimeframeKey, ct);

                // 2) 計算缺口（以日為單位對 LCD）
                var gap = _inventory.ComputeMissingByDay(cov, lcd, plan.RequiredDays);

                if (gap is { } g)
                {
                    var originalGapFrom = g.GapFrom;
                    var originalGapTo = g.GapTo;
                    var requiredBars = plan.RequiredBars;
                    DateOnly start;
                    DateOnly downloadEnd; // 實際要傳給 downloader 的結束日

                    if (cov?.From is DateOnly coverageFrom && cov.To is DateOnly coverageTo)
                    {
                        // 判斷 gap 在 coverage 的哪一側
                        bool gapIsLeft = originalGapTo < coverageFrom;   // 缺在 coverage 左側（更早的歷史）
                        bool gapIsRight = originalGapFrom > coverageTo;  // 缺在 coverage 右側（更新到最新的日）

                        if (gapIsLeft)
                        {
                            // 若缺早期歷史（left-gap），只下載缺失區間（但以月份為單位對齊）
                            // 因為所有週期都是按月份存檔，所以對齊到月份首/末，確保每個月檔案是完整的
                            start = new DateOnly(originalGapFrom.Year, originalGapFrom.Month, 1);
                            var lastDay = DateTime.DaysInMonth(originalGapTo.Year, originalGapTo.Month);
                            downloadEnd = new DateOnly(originalGapTo.Year, originalGapTo.Month, lastDay);

                            _log.LogDebug("[Warmup] {Sym}|{Tf} 已有 coverage，缺早期歷史 (left-gap) {A}~{B}，start(aligned)={Start}, end(aligned)={End}",
                                symbol, plan.TimeframeKey, originalGapFrom, originalGapTo, start, downloadEnd);
                        }
                        else if (gapIsRight)
                        {
                            // 缺最新日（right-gap）：需要把當月整個檔案補到 LCD（避免只補一天導致月檔不完整）
                            start = new DateOnly(originalGapFrom.Year, originalGapFrom.Month, 1); // 對齊到該月第一日
                            downloadEnd = lcd;

                            _log.LogDebug("[Warmup] {Sym}|{Tf} 已有 coverage，缺最新資料 (right-gap) {A}~{B}，start(aligned)={Start}, end=LCD({End})",
                                symbol, plan.TimeframeKey, originalGapFrom, originalGapTo, start, downloadEnd);
                        }
                        else
                        {
                            // 若 gap 與 cov 重疊或無法明確分類（比較罕見），採取保守策略：從 gap.From 開始下載到 LCD（並對齊 start 到該月第一日以確保月檔完整）
                            start = new DateOnly(originalGapFrom.Year, originalGapFrom.Month, 1);
                            downloadEnd = lcd;

                            _log.LogDebug("[Warmup] {Sym}|{Tf} 已有 coverage，但 gap 與 coverage 有重疊或未預期情形，採保守下載 {A}~LCD，start(aligned)={Start}",
                                symbol, plan.TimeframeKey, originalGapFrom, start);
                        }
                    }
                    else
                    {
                        // Cold start（沒有 coverage）：回推 requiredBars（保守策略），然後對齊至月份第一日
                        // 這能確保我們取得足夠資料來計算指標，同時月檔完整
                        DateOnly candidateStart;
                        if (plan.TimeframeKey.Equals("Month", StringComparison.OrdinalIgnoreCase))
                        {
                            var candidate = lcd.AddMonths(-(requiredBars - 1));
                            candidateStart = new DateOnly(candidate.Year, candidate.Month, 1);
                        }
                        else if (plan.TimeframeKey.Equals("Week", StringComparison.OrdinalIgnoreCase))
                        {
                            // Align the requested range to the first day of the month for monthly storage.
                            var candidate = lcd.AddDays(-(requiredBars - 1) * 7);
                            candidateStart = new DateOnly(candidate.Year, candidate.Month, 1);
                        }
                        else
                        {
                            // 日/分K：回推 days，然後對齊到月份第一天
                            candidateStart = originalGapFrom.AddDays(-(requiredBars - 1));
                            if (candidateStart > originalGapFrom) candidateStart = originalGapFrom;
                            candidateStart = new DateOnly(candidateStart.Year, candidateStart.Month, 1);
                        }

                        start = candidateStart;
                        downloadEnd = lcd;

                        _log.LogDebug("[Warmup] {Sym}|{Tf} 無 coverage（cold start），回推 requiredBars，start(aligned)={Start}, end=LCD({End})",
                            symbol, plan.TimeframeKey, start, downloadEnd);
                    }

                    _log.LogDebug("[Warmup] {Sym}|{Tf} 將下載範圍 {Start} ~ {End}（requiredBars={RB}）。",
                        symbol, plan.TimeframeKey, start, downloadEnd, requiredBars);

                    await _downloader.DownloadAsync(symbol, plan.TimeframeKey, start, downloadEnd, ct);

                    // downloader 已在儲存後直接 upsert manifest，避免再做一次全磁碟 rescan。
                    cov = await _inventory.GetCoverageAsync(symbol, plan.TimeframeKey, ct) ?? cov;
                }

                // 3) 讀取最後 N 根 K 回傳（N = RequiredBars）
                var last = await _reader.ReadLastNBarsAsync(symbol, plan.TimeframeKey, plan.RequiredBars, ct);
                result[plan.TimeframeKey] = last;

                if (cov?.FirstTradableDate is DateOnly firstTradable && last.Count < plan.RequiredBars)
                {
                    _log.LogWarning(
                        "[Warmup] {Sym}|{Tf} 受 FirstTradableDate={FirstTradable} 限制，僅取得 {ActualBars}/{RequiredBars} 根 K。",
                        symbol, plan.TimeframeKey, firstTradable, last.Count, plan.RequiredBars);
                }

                _log.LogDebug("[Warmup] {Sym}|{Tf} 就緒：回傳 {N} 根列 K。", symbol, plan.TimeframeKey, last.Count);
            }

            return result;
        }
    }
}

