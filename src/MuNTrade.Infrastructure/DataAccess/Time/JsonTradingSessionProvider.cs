using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Contracts.Market; // Added
using MuNTrade.Core.Settings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MuNTrade.Infrastructure.DataAccess.Time
{
    /// <summary>
    /// JSON-based TradingSession provider.
    ///提供 GetSession、GetFirstAndLastTradingDateInRange、FindPreviousTradingDate、IsTradingDate、GetPeriodWindow。
    ///內部會使用 IHolidayCalendar 判斷交易日（由 DI 注入）。
    /// </summary>
    public class JsonTradingSessionProvider : ITradingSessionProvider
    {
        private readonly ILogger<JsonTradingSessionProvider> _logger;
        private readonly IHolidayCalendar _holidayCalendar;
        private readonly IMarketInfoProvider _marketInfo;
        private readonly Dictionary<string, List<SessionTemplate>> _templates;

        public JsonTradingSessionProvider(
            ILogger<JsonTradingSessionProvider> logger,
            IConfiguration configuration,
            IMarketInfoProvider marketInfo,
            IHolidayCalendar holidayCalendar)
        {
            _logger = logger;
            _holidayCalendar = holidayCalendar;
            _marketInfo = marketInfo;

            _templates = configuration.GetSection("TradingSessions")
                                      .Get<Dictionary<string, List<SessionTemplate>>>()
                                      ?? new Dictionary<string, List<SessionTemplate>>();

            if (!_templates.Any())
            {
                _logger.LogWarning("未從 TradingSessions.json 載入任何交易時段設定。");
            }
        }

        // =========================
        // Public interface methods
        // =========================

        public TradingSession? GetSession(string id, DateTime ts)
        {
            var tplList = GetTemplatesForSymbol(id);
            if (!tplList.Any()) return null;

            // Normalize to local to avoid DateTimeKind surprises
            ts = EnsureLocal(ts);
            var calDay = DateOnly.FromDateTime(ts);

            foreach (var tpl in tplList)
            {
                // 處理跨日夜盤
                if (tpl.CrossesMidnight)
                {
                    // 建立 candidate start/end（以 ts.Date 為 base，再視情況加一天）
                    var sessionStartCandidate = ts.Date + tpl.Start;
                    var sessionEndCandidate = ts.Date + tpl.End;

                    // 如果 end 的時間小於 start（例如 05:00 < 15:00），表示真正的 end 是隔天
                    if (sessionEndCandidate.TimeOfDay < sessionStartCandidate.TimeOfDay)
                        sessionEndCandidate = sessionEndCandidate.AddDays(1);

                    // 如果 ts 的時間在 tpl.Start 之前（例如 ts 為 01:00 而 tpl.Start=15:00），
                    // 代表該 ts 實際上屬於「前一天」開始的夜盤，故把 window 往前移一天
                    if (ts.TimeOfDay < tpl.Start)
                    {
                        sessionStartCandidate = sessionStartCandidate.AddDays(-1);
                        sessionEndCandidate = sessionEndCandidate.AddDays(-1);
                    }

                    // 最終判定 ts 是否落在該 session 區間
                    if (ts >= sessionStartCandidate && ts <= sessionEndCandidate)
                    {
                        // tradeDate 的基準為「夜盤收盤之那一天」
                        var candidateTradeDate = DateOnly.FromDateTime(sessionEndCandidate);

                        // 如果 candidateTradeDate 本身就是交易日 -> 直接使用
                        // 如果不是 -> 明確往後推到「下一個交易日」
                        DateOnly tradeDate;
                        if (!_holidayCalendar.IsHoliday(candidateTradeDate))
                        {
                            tradeDate = candidateTradeDate;
                        }
                        else
                        {
                            // 明確推到下一個交易日（非模稜兩可的 fallback）
                            tradeDate = _holidayCalendar.GetNextTradingDay(candidateTradeDate);
                        }

                        return new TradingSession(sessionStartCandidate, sessionEndCandidate, tpl.Anchor, tpl.PreCloseAuctionStart, tpl.PreCloseExtension, tradeDate);
                    }
                }
                else // 日盤（不跨日）
                {
                    var start = calDay.ToDateTime(TimeOnly.FromTimeSpan(tpl.Start));
                    var end = calDay.ToDateTime(TimeOnly.FromTimeSpan(tpl.End));

                    if (ts >= start && ts <= end)
                    {
                        // 若當日是假日/週末，視為沒有 session（避免把假日誤判為交易日）
                        if (_holidayCalendar.IsHoliday(calDay))
                        {
                            continue;
                        }

                        var tradeDate = calDay;
                        return new TradingSession(start, end, tpl.Anchor, tpl.PreCloseAuctionStart, tpl.PreCloseExtension, tradeDate);
                    }
                }
            }

            return null;
        }

        public (DateOnly? first, DateOnly? last) GetFirstAndLastTradingDateInRange(string symbol, DateTime dateFrom, DateTime dateTo, TimeOnly? probeTime = null)
        {
            if (dateTo < dateFrom) return (null, null);

            // 我們用 IsTradingDate（基於假日日曆）來判斷「該日是否為交易日」，
            // probeTime 保留但僅做為可能的 future extension（目前不需要用 GetSession 去 probe）。
            DateOnly? first = null;
            DateOnly? last = null;

            for (var d = dateFrom.Date; d <= dateTo.Date; d = d.AddDays(1))
            {
                var day = DateOnly.FromDateTime(d);
                if (IsTradingDate(symbol, day))
                {
                    first = day;
                    break;
                }
            }

            for (var d = dateTo.Date; d >= dateFrom.Date; d = d.AddDays(-1))
            {
                var day = DateOnly.FromDateTime(d);
                if (IsTradingDate(symbol, day))
                {
                    last = day;
                    break;
                }
            }

            _logger.LogDebug("[TradingHelper] GetFirstLast symbol={Sym} range={From:yyyy-MM-dd}..{To:yyyy-MM-dd} first={F} last={L}",
                symbol, dateFrom.Date, dateTo.Date, first?.ToString() ?? "<null>", last?.ToString() ?? "<null>");

            return (first, last);
        }

        public DateOnly? FindPreviousTradingDate(string symbol, DateOnly day, int maxLookbackDays = 30)
        {
            for (int i = 1; i <= maxLookbackDays; i++)
            {
                var cand = day.AddDays(-i);
                if (IsTradingDate(symbol, cand))
                {
                    _logger.LogDebug("[TradingHelper] FindPrevious symbol={Sym} day={Day} foundPrev={Prev}", symbol, day, cand);
                    return cand;
                }
            }

            _logger.LogWarning("[TradingHelper] FindPrevious symbol={Sym} day={Day} 找不到前一個交易日 (lookback {LB} 天)", symbol, day, maxLookbackDays);
            return null;
        }

        public bool IsTradingDate(string symbol, DateOnly date)
        {
            // 以假日日曆為準（週末或假日視為非交易日）
            // 若未來需要更精細（例如某個 symbol 特殊不交易日），可以再擴充此邏輯。
            return !_holidayCalendar.IsHoliday(date);
        }

        public PeriodWindow GetPeriodWindow(string stockId, BarConfig cfg, DateTime nowLocal)
        {
            // 正規化為 Local 時間，避免 DateTimeKind 帶來誤差
            nowLocal = EnsureLocal(nowLocal);

            //共通第一步：先用 GetSession 取得當下的 session（用以得 referenceTradeDate） ----------
            // 為何要這一步：若現在為夜盤（跨日），tradeDate 會被歸屬到 sessionEnd.Date，
            // 所以必須以 provider 的 single-source-of-truth（GetSession）作為 reference。
            var currentSession = GetSession(stockId, nowLocal);
            DateOnly referenceTradeDate = currentSession?.TradeDate ?? DateOnly.FromDateTime(nowLocal);
            // currentSession 為 null 時，退回使用 nowLocal 的 calendar date。

            // Use IMarketInfoProvider to check if it's Derivatives (replacing IsFuturesSymbol)
            // [SOP-Strict]
            bool isDeriv = _marketInfo.TryGetMarketInfo(stockId, out var info) && info.IsDerivatives;


            // Day TF：First = Last = referenceTradeDate

            if (cfg.Type == "Day")
            {
                var tradingDate = referenceTradeDate;

                // Day window start:
                //期貨：從 tradingDate 的「前一交易日」15:00 開始（若找不到前一日則以 tradingDate 的 15:00 或 fallback）
                //非期貨：從 tradingDate 的 09:00 開始
                DateOnly startTradeDateForWindow = isDeriv
                    ? (FindPreviousTradingDate(stockId, tradingDate) ?? tradingDate)
                    : tradingDate;

                DateTime windowStart = startTradeDateForWindow.ToDateTime(isDeriv ? new TimeOnly(15, 0) : new TimeOnly(9, 0));
                DateTime windowEnd = isDeriv
                    ? tradingDate.ToDateTime(new TimeOnly(13, 45))
                    : tradingDate.ToDateTime(new TimeOnly(13, 30));

                return new PeriodWindow(windowStart, windowEnd, tradingDate, tradingDate, $"Day window (ref tradeDate={tradingDate})");
            }


            // Week TF：以 referenceTradeDate 所在週為基準

            if (cfg.Type == "Week")
            {
                // 以 referenceTradeDate 對應的週（週一為開始）
                var mondayOfWeek = StartOfWeekMonday(referenceTradeDate.ToDateTime(new TimeOnly(0, 0)));
                var sundayOfWeek = mondayOfWeek.AddDays(6);

                // 在該週內找第一與最後一個有 session 的交易日（probe 預設 09:00）
                var (first, last) = GetFirstAndLastTradingDateInRange(stockId, mondayOfWeek.Date, sundayOfWeek.Date);

                if (first == null || last == null)
                {
                    // 極端 fallback：使用 calendar week 的預設時間
                    var fallbackStart = TruncMin(mondayOfWeek + new TimeSpan(9, 0, 0));
                    var fallbackEnd = TruncMin(sundayOfWeek + (isDeriv ? new TimeSpan(13, 45, 0) : new TimeSpan(13, 30, 0)));
                    _logger.LogWarning("[GetPeriodWindow][Week] 找不到交易日，使用 fallback Start={S:O} End={E:O}", fallbackStart, fallbackEnd);
                    return new PeriodWindow(fallbackStart, fallbackEnd, DateOnly.FromDateTime(mondayOfWeek), DateOnly.FromDateTime(sundayOfWeek), "fallback week");
                }

                var firstTrading = first.Value;
                var lastTrading = last.Value;

                // WindowStart：
                //期貨：以 firstTrading 的「前一交易日」15:00 為起點（若找不到前一日則以 firstTrading 本身）
                //非期貨：以 firstTrading 的 09:00 為起點
                DateOnly startTradeDate = isDeriv
                    ? (FindPreviousTradingDate(stockId, firstTrading) ?? firstTrading)
                    : firstTrading;

                DateTime windowStart = startTradeDate.ToDateTime(isDeriv ? new TimeOnly(15, 0) : new TimeOnly(9, 0));
                DateTime windowEnd = lastTrading.ToDateTime(isDeriv ? new TimeOnly(13, 45) : new TimeOnly(13, 30));

                return new PeriodWindow(windowStart, windowEnd, firstTrading, lastTrading, "week");
            }


            // Month TF：以 referenceTradeDate 所在月為基準

            if (cfg.Type == "Month")
            {
                var firstOfMonth = new DateTime(referenceTradeDate.Year, referenceTradeDate.Month, 1);
                var lastOfMonth = firstOfMonth.AddMonths(1).AddDays(-1);

                var (first, last) = GetFirstAndLastTradingDateInRange(stockId, firstOfMonth.Date, lastOfMonth.Date);

                if (first == null || last == null)
                {
                    var fallbackStart = TruncMin(firstOfMonth + new TimeSpan(9, 0, 0));
                    var fallbackEnd = TruncMin(lastOfMonth + (isDeriv ? new TimeOnly(13, 45).ToTimeSpan() : new TimeOnly(13, 30).ToTimeSpan()));
                    _logger.LogWarning("[GetPeriodWindow][Month] 找不到交易日，使用 fallback Start={S:O} End={E:O}", fallbackStart, fallbackEnd);
                    return new PeriodWindow(fallbackStart, fallbackEnd, DateOnly.FromDateTime(firstOfMonth), DateOnly.FromDateTime(lastOfMonth), "fallback month");
                }

                var firstTrading = first.Value;
                var lastTrading = last.Value;

                DateOnly startTradeDate = isDeriv
                    ? (FindPreviousTradingDate(stockId, firstTrading) ?? firstTrading)
                    : firstTrading;

                DateTime windowStart = startTradeDate.ToDateTime(isDeriv ? new TimeOnly(15, 0) : new TimeOnly(9, 0));
                DateTime windowEnd = lastTrading.ToDateTime(isDeriv ? new TimeOnly(13, 45) : new TimeOnly(13, 30));

                return new PeriodWindow(windowStart, windowEnd, firstTrading, lastTrading, "month");
            }

            // 不支援的 TF 類型
            throw new NotSupportedException($"TF type {cfg.Type} not supported");
        }

        // =========================
        // Private helpers
        // =========================

        private IEnumerable<SessionTemplate> GetTemplatesForSymbol(string stockId)
        {
            // [SOP-Strict]
            bool isDeriv = _marketInfo.TryGetMarketInfo(stockId, out var info) && info.IsDerivatives;

            if (isDeriv)
            {
                return _templates.GetValueOrDefault("TXF_DAY", new List<SessionTemplate>())
                                 .Concat(_templates.GetValueOrDefault("TXF_NIGHT", new List<SessionTemplate>()));
            }

            return _templates.GetValueOrDefault("TWSE", new List<SessionTemplate>());
        }

        private static DateTime EnsureLocal(DateTime dt) =>
            dt.Kind switch
            {
                DateTimeKind.Local => dt,
                DateTimeKind.Utc => dt.ToLocalTime(),
                _ => DateTime.SpecifyKind(dt, DateTimeKind.Local)
            };

        private static DateTime TruncMin(DateTime dt) => new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, DateTimeKind.Local);

        private static DateTime StartOfWeekMonday(DateTime localDate)
        {
            int diff = (7 + (int)localDate.DayOfWeek - (int)DayOfWeek.Monday) % 7;
            var d = localDate.Date.AddDays(-diff);
            return DateTime.SpecifyKind(d, DateTimeKind.Local);
        }
    }
}

