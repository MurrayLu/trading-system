using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Warmup;
using MuNTrade.Core.Settings;
using MuNTrade.Core.Utils.Converters;
using MuNTrade.Infrastructure.DataAccess.FileSystem;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Infrastructure.DataAccess.Warmup
{
    /// <summary>
    /// 重新掃描歷史 JSON 檔以建立/維護 Coverage（From/To/IsClosed）。
    /// Determines IsClosed and LCD with the trading session provider and holiday calendar.
    /// </summary>
    public sealed class JsonDataInventoryService : IDataInventoryService
    {
        private readonly ILogger<JsonDataInventoryService> _log;
        private readonly ITradingSessionProvider _sessions;
        private readonly IHolidayCalendar _calendar;
        private readonly IClock _clock;
        private readonly HistoricalDataReader _reader;
        private readonly string _inventoryRoot;
        private readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public JsonDataInventoryService(
            ILogger<JsonDataInventoryService> log,
            HistoricalDataReader reader,
            IOptions<DataSourceSettings> options,
            ITradingSessionProvider sessions,
            IHolidayCalendar calendar,
            IClock clock)
        {
            _log = log;
            _reader = reader;
            _sessions = sessions;
            _calendar = calendar;
            _clock = clock;

            // inventory = KLineJsonBasePath\_inventory
            var klineBase = options.Value.KLineJsonBasePath;
            if (string.IsNullOrWhiteSpace(klineBase))
                klineBase = Path.Combine(AppContext.BaseDirectory, "K線資料");

            _inventoryRoot = Path.Combine(klineBase, "_inventory");
            Directory.CreateDirectory(_inventoryRoot);

            _json.Converters.Add(new DateOnlyJsonConverter());

            _log.LogDebug("[庫存] BasePath = {Path}", _inventoryRoot);
        }

        private string ManifestPath(string symbol, string tf)
        {
            var dir = Path.Combine(_inventoryRoot, symbol);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"{tf}.manifest.json");
        }

        public async Task<CoverageInfo?> GetCoverageAsync(string symbol, string timeframeKey, CancellationToken ct = default)
        {
            var path = ManifestPath(symbol, timeframeKey);
            if (!File.Exists(path))
            {
                _log.LogDebug("[庫存:讀取] 未找到 manifest：{Path}", path);
                return null;
            }

            try
            {
                await using var fs = File.OpenRead(path);
                var cov = await JsonSerializer.DeserializeAsync<CoverageInfo>(fs, _json, ct);
                _log.LogDebug("[庫存:讀取] 成功載入 manifest {Sym}|{Tf} => From={From}, To={To}, Closed={Closed}",
                    symbol, timeframeKey, cov?.From, cov?.To, cov?.IsClosed);
                return cov;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[庫存:讀取] 反序列化 manifest 失敗：{Path}", path);
                return null;
            }
        }

        public async Task SaveCoverageAsync(string symbol, string timeframeKey, CoverageInfo info, CancellationToken ct = default)
        {
            try
            {
                var path = ManifestPath(symbol, timeframeKey);
                _log.LogDebug("[庫存:寫入] 將寫入 manifest {Path}。From={From}, To={To}, Closed={Closed}",
                    path, info?.From, info?.To, info?.IsClosed);
                await using var fs = File.Create(path);
                await JsonSerializer.SerializeAsync(fs, info, _json, ct);
                _log.LogDebug("[庫存:寫入] 已儲存 manifest {Sym}|{Tf}", symbol, timeframeKey);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[庫存:寫入] 儲存 manifest 失敗：{Sym}|{Tf}", symbol, timeframeKey);
                throw;
            }
        }

        public async Task<CoverageInfo> RescanFromDiskAsync(string symbol, string timeframeKey, CancellationToken ct = default)
        {
            var existing = await GetCoverageAsync(symbol, timeframeKey, ct);
            var basePath = _readerPathBase();
            _log.LogDebug("[庫存:Rescan] 讀取器 base path = {BasePath}", basePath);

            var dir = Path.Combine(basePath, symbol);
            var prefix = HistoricalDataReader.GetPrefix(timeframeKey);
            var files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, $"{prefix}_*.json").OrderBy(x => x).ToArray()
                : Array.Empty<string>();

            _log.LogDebug("[庫存:Rescan] 開始掃描 {Sym}|{Tf}，路徑：{Dir}，候選檔案數量={Count}",
                symbol, timeframeKey, dir, files.Length);

            if (files.Length == 0)
            {
                _log.LogInformation("[庫存:Rescan] 找不到任何檔案：{Sym}|{Tf}", symbol, timeframeKey);
                return new CoverageInfo
                {
                    From = null,
                    To = null,
                    FirstTradableDate = existing?.FirstTradableDate,
                    IsClosed = false,
                    Timeframe = timeframeKey
                };
            }

            DateOnly? minDate = null;
            DateOnly? maxDate = null;

            foreach (var f in files)
            {
                DateOnly? validFrom = null;
                DateOnly? validTo = null;

                var name = Path.GetFileNameWithoutExtension(f);

                // 1) 先試檔名
                try
                {
                    var parts = name.Split('_');
                    if (parts.Length >= 3 &&
                        DateTime.TryParseExact(parts[^2], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var df) &&
                        DateTime.TryParseExact(parts[^1], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt))
                    {
                        validFrom = DateOnly.FromDateTime(df);
                        validTo = DateOnly.FromDateTime(dt);
                    }
                    else
                    {
                        var matches = Regex.Matches(name, @"\d{8}")
                                           .Cast<Match>()
                                           .Select(m =>
                                           {
                                               if (DateTime.TryParseExact(m.Value, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d))
                                                   return (DateOnly?)DateOnly.FromDateTime(d);
                                               return null;
                                           })
                                           .Where(d => d.HasValue).Select(d => d!.Value).ToArray();

                        if (matches.Length >= 2)
                        {
                            validFrom = matches[0];
                            validTo = matches[1];
                        }
                        else if (matches.Length == 1)
                        {
                            validFrom = matches[0];
                            if (timeframeKey.StartsWith("Month", StringComparison.OrdinalIgnoreCase))
                            {
                                var d = matches[0].ToDateTime(TimeOnly.MinValue);
                                validTo = DateOnly.FromDateTime(new DateTime(d.Year, d.Month,
                                                       DateTime.DaysInMonth(d.Year, d.Month)));
                            }
                            else if (timeframeKey.StartsWith("Week", StringComparison.OrdinalIgnoreCase))
                            {
                                validTo = matches[0].AddDays(6);
                            }
                            else
                            {
                                validTo = matches[0];
                            }
                        }
                    }
                }
                catch (Exception exName)
                {
                    _log.LogWarning(exName, "[庫存:Rescan] 檔名解析出錯，改由檔案內容解析：{File}", f);
                }

                // 2) 檔名不完整 → 讀內容做 fallback
                if (validFrom == null || validTo == null)
                {
                    try
                    {
                        var rows = await _reader.ReadFileAsync(f, ct);
                        if (rows != null && rows.Count > 0)
                        {
                            var hasKlineTime = rows.Any(r => r.KlineTime != default);
                            if (hasKlineTime)
                            {
                                validFrom ??= DateOnly.FromDateTime(rows.Min(r => r.KlineTime));
                                validTo ??= DateOnly.FromDateTime(rows.Max(r => r.KlineTime));
                            }
                            else
                            {
                                var hasTo = rows.Any(r => r.To != default);
                                var hasFrom = rows.Any(r => r.From != default);
                                if (hasTo)
                                {
                                    validFrom ??= DateOnly.FromDateTime(rows.Min(r => r.To));
                                    validTo ??= DateOnly.FromDateTime(rows.Max(r => r.To));
                                }
                                else if (hasFrom)
                                {
                                    validFrom ??= DateOnly.FromDateTime(rows.Min(r => r.From));
                                    validTo ??= DateOnly.FromDateTime(rows.Max(r => r.From));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "[庫存:Rescan] 讀取檔案內容失敗：{File}", f);
                    }
                }

                // 3) 聚合 min/max
                if (validFrom != null && (minDate == null || validFrom < minDate)) minDate = validFrom;
                if (validTo != null && (maxDate == null || validTo > maxDate)) maxDate = validTo;
            }

            _log.LogDebug("[庫存:Rescan] 掃描結束，minDate={Min}, maxDate={Max}", minDate, maxDate);

            if (minDate == null || maxDate == null)
            {
                _log.LogWarning("[庫存:Rescan] 無法決定全域 min/max 日期，回傳空 coverage：{Sym}|{Tf}", symbol, timeframeKey);
                return new CoverageInfo
                {
                    From = null,
                    To = null,
                    FirstTradableDate = existing?.FirstTradableDate,
                    IsClosed = false,
                    Timeframe = timeframeKey
                };
            }

            // 4) 以「交易時段 + 交易日曆」計算 LCD，並依 timeframe 判斷是否封閉
            var now = _clock.Now;
            if (now <= DateTime.MinValue.AddDays(1))
            {
                now = DateTime.UtcNow;
                _log.LogWarning(
                    "[庫存:Rescan] IClock 尚未錨定，改用系統時間 {FallbackNow:o} 進行 coverage 掃描。",
                    now);
            }

            var today = DateOnly.FromDateTime(now);
            var sess = _sessions.GetSession(symbol, now);

            DateOnly lcd;
            if (sess != null && now >= sess.EndTime && !_calendar.IsHoliday(sess.TradeDate))
            {
                lcd = sess.TradeDate; // 今日已完全收盤
            }
            else
            {
                lcd = _calendar.GetPreviousTradingDay(today); // 取前一個交易日
            }

            bool isClosed;
            try
            {
                switch (timeframeKey)
                {
                    case "Day":
                    case "Time_1":
                    case "Time_5":
                    case "Time_60":
                        // 分K/日K：只要檔案覆蓋到的最後「日期」<= LCD，即視為封閉
                        isClosed = maxDate.Value <= lcd;
                        break;

                    case "Week":
                        {
                            var periodEnd = GetPeriodEnd(maxDate.Value, "Week");
                            isClosed = lcd >= periodEnd;
                            break;
                        }
                    case "Month":
                        {
                            var periodEnd = GetPeriodEnd(maxDate.Value, "Month");
                            isClosed = lcd >= periodEnd;
                            break;
                        }
                    default:
                        isClosed = maxDate.Value <= lcd;
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[庫存:Rescan] 判斷 isClosed 時發生錯誤：{Sym}|{Tf}", symbol, timeframeKey);
                isClosed = false;
            }

            var cov = new CoverageInfo
            {
                From = minDate,
                To = maxDate,
                FirstTradableDate = existing?.FirstTradableDate,
                IsClosed = isClosed,
                Timeframe = timeframeKey
            };

            await SaveCoverageAsync(symbol, timeframeKey, cov, ct);

            _log.LogInformation(
                "[庫存] Rescan {Sym}|{Tf}: 檔案數={Count}, From={From}, To={To}, Closed={Closed}.",
                symbol, timeframeKey, files.Length, minDate, maxDate, isClosed
            );

            return cov;
        }

        public (DateOnly GapFrom, DateOnly GapTo)? ComputeMissingByDay(CoverageInfo cov, DateOnly lcd, int requiredDays)
        {
            _log.LogDebug("[庫存:ComputeMissingByDay] cov.From={From}, cov.To={To}, lcd={LCD}, requiredDays={Days}",
                cov.From, cov.To, lcd, requiredDays);

            if (cov.From == null || cov.To == null)
            {
                var fallbackFrom = lcd.AddDays(-(requiredDays - 1));
                _log.LogWarning("[庫存:ComputeMissingByDay] coverage 缺失，請求區間：{From} ~ {To}", fallbackFrom, lcd);
                return (fallbackFrom, lcd);
            }

            var needFrom = lcd.AddDays(-(requiredDays - 1));
            var needTo = lcd;
            var effectiveNeedFrom = needFrom;
            if (cov.FirstTradableDate is DateOnly ft && effectiveNeedFrom < ft)
            {
                _log.LogDebug(
                    "[庫存:ComputeMissingByDay] 使用 firstTradableDate={FirstTradable} 裁切理論需求起點 {NeedFrom} -> {EffectiveNeedFrom}",
                    ft, needFrom, ft);
                effectiveNeedFrom = ft;
            }

            if (effectiveNeedFrom > needTo)
            {
                _log.LogDebug("[庫存:ComputeMissingByDay] 理論需求區間完全落在上市前，視為無缺口");
                return null;
            }

            if (cov.From.Value > effectiveNeedFrom)
            {
                return (effectiveNeedFrom, cov.From.Value.AddDays(-1));
            }
            if (cov.To.Value < needTo)
            {
                return (cov.To.Value.AddDays(1), needTo);
            }

            _log.LogDebug("[庫存:ComputeMissingByDay] coverage 已完整覆蓋所需區間");
            return null;
        }

        public async Task UpsertAsync(string symbol, string timeframeKey, DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            var cov = await GetCoverageAsync(symbol, timeframeKey, ct) ?? new CoverageInfo { Timeframe = timeframeKey };

            if (cov.From == null || from < cov.From) cov.From = from;
            if (cov.To == null || to > cov.To) cov.To = to;

            await SaveCoverageAsync(symbol, timeframeKey, cov, ct);
        }

        public async Task ConfirmFirstTradableDateAsync(string symbol, string timeframeKey, DateOnly firstTradableDate, CancellationToken ct = default)
        {
            var cov = await GetCoverageAsync(symbol, timeframeKey, ct) ?? new CoverageInfo { Timeframe = timeframeKey };

            if (cov.FirstTradableDate == null || firstTradableDate < cov.FirstTradableDate)
            {
                cov.FirstTradableDate = firstTradableDate;
                await SaveCoverageAsync(symbol, timeframeKey, cov, ct);
                _log.LogInformation("[庫存] ConfirmFirstTradableDate {Sym}|{Tf} => {FirstTradableDate}",
                    symbol, timeframeKey, firstTradableDate);
            }
        }

        private string _readerPathBase()
        {
            var field = typeof(HistoricalDataReader)
                .GetField("_basePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (field == null)
                throw new InvalidOperationException("HistoricalDataReader 不包含 private 欄位 '_basePath'。");

            var val = field.GetValue(_reader);
            if (val == null)
                throw new InvalidOperationException("無法從 HistoricalDataReader 取得 _basePath 的值（null）。");

            return val.ToString()!;
        }

        /// <summary>
        /// 期末計算：
        ///Week：回傳該日期所屬週的週五（遇六/日就回前一個週五）
        ///Month：回傳該月最後一天
        /// </summary>
        private static DateOnly GetPeriodEnd(DateOnly anyDateInPeriod, string timeframe)
        {
            if (timeframe.Equals("Week", StringComparison.OrdinalIgnoreCase))
            {
                // 先找該日期所屬週的周一，然後 +4 天為該週的週五
                var dt = anyDateInPeriod.ToDateTime(TimeOnly.MinValue);
                int dow = (int)dt.DayOfWeek; // Sunday=0 .. Saturday=6
                                             // 計算 offset 到星期一（若是 Sunday，offsetToMonday = -6）
                int offsetToMonday = (dow == 0) ? -6 : (1 - dow);
                var monday = anyDateInPeriod.AddDays(offsetToMonday);
                return monday.AddDays(4); // Monday + 4 = Friday
            }
            else // Month
            {
                var dt = anyDateInPeriod.ToDateTime(TimeOnly.MinValue);
                var last = DateTime.DaysInMonth(dt.Year, dt.Month);
                return DateOnly.FromDateTime(new DateTime(dt.Year, dt.Month, last));
            }
        }
    }
}

