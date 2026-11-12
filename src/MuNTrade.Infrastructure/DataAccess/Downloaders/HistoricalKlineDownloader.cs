// MuNTrade.Infrastructure/Services/HistoricalKlineDownloader.cs
using System;
using System.Collections.Generic;
using System.Globalization; // 確保檔案頂端有這個 using
using System.Linq; // for GroupBy / LINQ
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Contracts.Brokers;   // 你的 Broker 介面
using MuNTrade.Infrastructure.DataAccess.FileSystem;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Infrastructure;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Warmup;
using MuNTrade.Abstractions.Contracts.Time;

namespace MuNTrade.Infrastructure.DataAccess.Downloaders
{
    public sealed class HistoricalKlineDownloader : IHistoricalKlineDownloader
    {
        private readonly ILogger<HistoricalKlineDownloader> _log;
        private readonly IBrokerKlineClient _broker;
        private readonly HistoricalDataReader _reader;
        private readonly IDataInventoryService _inventory;
        private readonly IClock _clock;
        private readonly IStaWorkPressureMonitor _pressure;

        public HistoricalKlineDownloader(
            ILogger<HistoricalKlineDownloader> log,
            IBrokerKlineClient broker,
            HistoricalDataReader reader,
            IDataInventoryService inventory,
            IClock clock,
            IStaWorkPressureMonitor pressure)
        {
            _log = log;
            _broker = broker;
            _reader = reader;
            _inventory = inventory;
            _clock = clock;
            _pressure = pressure;
        }

        /// <summary>
        /// 下載指定 symbol/timeframeKey 的 K 線資料 (start ~ lcd)
        /// 修正重點：即使 Broker 回傳範圍超出請求區間，我們也會在 parse 後強制 clip 到 [start, lcd]。
        /// 同時在儲存時以月份為單位覆寫同月舊檔（HistoricalDataReader.SaveMonthlyRollingAsync 的行為）。
        /// </summary>
        public async Task<IReadOnlyList<JsonKLineRecord>> DownloadAsync(
            string symbol, string timeframeKey, DateOnly start, DateOnly lcd, CancellationToken ct = default)
        {
            var lines = new List<(string sym, string line)>(1 << 14);
            void OnLine(string sym, string line)
            {
                if (sym.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                    lines.Add((sym, line));
            }

            _broker.KLineDataReceived += OnLine;
            try
            {
                var (kType, minute, prefix) = Map(timeframeKey);

                // 1) 嘗試 single request（一次請足整段 start~lcd）
                var singleSucceeded = false;
                try
                {
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var lastReceived = _clock.Now;
                    object lockObj = new();

                    // 內部 handler：更新 lastReceived
                    void OnLineForTcs(string sym, string line)
                    {
                        if (!sym.Equals(symbol, StringComparison.OrdinalIgnoreCase)) return;
                        lock (lockObj) { lastReceived = _clock.Now; }
                    }

                    _broker.KLineDataReceived += OnLineForTcs;

                    // 發一次 request（全範圍）
                    _log.LogDebug("[K線下載  ] {Sym}|{Tf} single-request {S}~{E}", symbol, timeframeKey, start, lcd);
                    ExecuteKlineRequest(symbol, timeframeKey, kType, start, lcd, minute);

                    // 監控：若超過 idleTimeout 毫秒沒收到新資料，視為完成
                    var idleTimeoutMs = 1200; // 1.2s idle = done (可調)
                    var globalTimeoutMs = 60_000; // 單次最大等待（避免永遠等）
                    var watch = Task.Run(async () =>
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < globalTimeoutMs && !ct.IsCancellationRequested)
                        {
                            await Task.Delay(200, CancellationToken.None);
                            DateTime last;
                            lock (lockObj) { last = lastReceived; }
                            if ((_clock.Now - last).TotalMilliseconds > idleTimeoutMs)
                            {
                                tcs.TrySetResult(true);
                                return;
                            }
                        }
                        tcs.TrySetResult(true);
                    });

                    // 等待完成或 cancel
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        var completed = await Task.WhenAny(tcs.Task, Task.Delay(globalTimeoutMs, linked.Token));
                        // 無論如何，視為 single-call 結束（若有收到資料則 lines.Count > 0）
                    }

                    _broker.KLineDataReceived -= OnLineForTcs;

                    if (lines.Count > 0)
                    {
                        singleSucceeded = true;
                        _log.LogDebug("[K線下載] {Sym}|{Tf} single-request returned {Count} lines", symbol, timeframeKey, lines.Count);
                    }
                    else
                    {
                        _log.LogInformation("[K線下載] {Sym}|{Tf} single-request returned no lines, will fallback to chunked.", symbol, timeframeKey);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[K線下載] single-request failed, fallback to chunking.");
                    // fallback below
                }

                // 2) fallback: 如果 single 失敗或沒資料，使用 SplitByMonth chunk 逐月下載
                if (lines.Count == 0)
                {
                    _log.LogInformation("[K線下載] {Sym}|{Tf} using monthly chunk fallback {S}~{E}", symbol, timeframeKey, start, lcd);
                    var chunks = TradingCalendarUtils.SplitByMonth(start, lcd);
                    using var chunkScope = _pressure.BeginKlineChunkFallback(symbol, timeframeKey);
                    foreach (var (s, e) in chunks)
                    {
                        ct.ThrowIfCancellationRequested();
                        _log.LogInformation("[DL] {Sym}|{Tf} chunk {S}~{E}", symbol, timeframeKey, s, e);
                        ExecuteKlineRequest(symbol, timeframeKey, kType, s, e, minute);
                        // 原本是 await Task.Delay(500): 為了簡單保守保留，但建議改成事件完成 Signal（如同 single-case）
                        await Task.Delay(500, ct);
                    }
                }

                // 3) parse collected lines → records
                var records = ParseToRecords(lines, timeframeKey);

                // 4) **關鍵修正**：強制過濾(clip)到 [start, lcd] 範圍內的紀錄
                DateTime startDt = start.ToDateTime(TimeOnly.MinValue);
                DateTime lcdDt = lcd.ToDateTime(new TimeOnly(23, 59, 59)); // 包含當日整天
                var clipped = records.Where(r => r.KlineTime >= startDt && r.KlineTime <= lcdDt).ToList();

                if (clipped.Count != records.Count)
                {
                    _log.LogWarning("[K線下載] {Sym}|{Tf} 共解析 {Total} 行，已裁切不在 {S}~{E} 範圍的 {Dropped} 行，剩 {Remain} 行要儲存。",
                        symbol, timeframeKey, records.Count, start, lcd, records.Count - clipped.Count, clipped.Count);
                }

                // 5) save month-rolling (reader will split by month internally) — 使用 clipped
                await _reader.SaveMonthlyRollingAsync(symbol, _readerPrefix(timeframeKey), lcd, clipped, ct);

                // 6) upsert inventory per month — 使用 clipped 來計算每月 coverage
                var groups = clipped
                             .GroupBy(r => new { Year = r.KlineTime.Year, Month = r.KlineTime.Month })
                             .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month);

                foreach (var g in groups)
                {
                    var from = DateOnly.FromDateTime(g.Min(r => r.KlineTime));
                    var to = DateOnly.FromDateTime(g.Max(r => r.KlineTime));
                    await _inventory.UpsertAsync(symbol, timeframeKey, from, to, ct);
                }

                await ConfirmFirstTradableDateAfterDownloadAttemptAsync(symbol, timeframeKey, start, clipped, ct);

                return (IReadOnlyList<JsonKLineRecord>)clipped;
            }
            finally
            {
                _broker.KLineDataReceived -= OnLine;
                lines.Clear();
            }
        }


        private static (short kType, short minute, string prefix) Map(string tf) => tf switch
        {
            "Time_1" => (0, 1, "1 分 K 線"),
            "Time_5" => (0, 5, "5 分 K 線"),
            "Time_60" => (0, 60, "60 分 K 線"),
            "Day" => (4, 1, "日 K 線"),
            "Week" => (5, 1, "週 K 線"),
            "Month" => (6, 1, "月 K 線"),
            _ => throw new ArgumentOutOfRangeException(nameof(tf), tf, "Unsupported timeframe")
        };

        private static string _readerPrefix(string tf) => HistoricalDataReader.GetPrefix(tf);

        private static DateOnly NormalizeFirstTradableDate(string timeframeKey, DateOnly date) => timeframeKey switch
        {
            "Month" => new DateOnly(date.Year, date.Month, 1),
            _ => date
        };

        private async Task ConfirmFirstTradableDateAfterDownloadAttemptAsync(
            string symbol,
            string timeframeKey,
            DateOnly requestStart,
            IReadOnlyList<JsonKLineRecord> clipped,
            CancellationToken ct)
        {
            DateOnly? earliestFromDownload = null;
            if (clipped.Count > 0)
            {
                earliestFromDownload = NormalizeFirstTradableDate(
                    timeframeKey,
                    DateOnly.FromDateTime(clipped.Min(r => r.KlineTime)));
            }

            // 這裡允許使用磁碟掃描結果，只因為「本次已經發生過一次正式下載流程」；
            // 不在一般 rescan 時自動補 FirstTradableDate，避免舊檔案直接亂封口。
            var rescanned = await _inventory.RescanFromDiskAsync(symbol, timeframeKey, ct);
            DateOnly? earliestFromDisk = rescanned.From is DateOnly fromDisk
                ? NormalizeFirstTradableDate(timeframeKey, fromDisk)
                : null;

            DateOnly? candidate = null;
            if (earliestFromDownload is DateOnly downloadDate && earliestFromDisk is DateOnly diskDate)
            {
                candidate = downloadDate <= diskDate ? downloadDate : diskDate;
            }
            else
            {
                candidate = earliestFromDownload ?? earliestFromDisk;
            }

            if (candidate is DateOnly firstTradable && requestStart < firstTradable)
            {
                await _inventory.ConfirmFirstTradableDateAsync(symbol, timeframeKey, firstTradable, ct);
            }
        }

        private void ExecuteKlineRequest(string symbol, string timeframeKey, short kType, DateOnly start, DateOnly end, short minute)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                _broker.RequestKLineByDate(symbol, kType, start, end, minute);
                sw.Stop();
                _pressure.RecordKlineRequestCompletion("KLine.RequestByDate", sw.ElapsedMilliseconds, timedOut: false);
            }
            catch (TimeoutException)
            {
                sw.Stop();
                _pressure.RecordKlineRequestCompletion("KLine.RequestByDate", sw.ElapsedMilliseconds, timedOut: true);
                throw;
            }
        }

        // 解析 broker 回傳的行格式（簡化版），產生 JsonKLineRecord（From/To 為唯讀，使用 KlineTime）
        private static List<JsonKLineRecord> ParseToRecords(List<(string sym, string line)> lines, string tf)
        {
            var list = new List<JsonKLineRecord>(lines.Count);
            foreach (var (_, ln) in lines)
            {
                try
                {
                    var parts = ln.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length < 6) continue;

                    DateTime klineTime;

                    // 情況 A: "From,To,O,H,L,C,Vol"
                    if (parts.Length >= 7 &&
                        DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts0) &&
                        DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts1))
                    {
                        klineTime = ts1;

                        if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var o)) continue;
                        if (!double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var h)) continue;
                        if (!double.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var l)) continue;
                        if (!double.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var c)) continue;
                        if (!long.TryParse(parts[6], NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) continue;

                        list.Add(new JsonKLineRecord
                        {
                            KlineTime = klineTime,
                            Open = o,
                            High = h,
                            Low = l,
                            Close = c,
                            Qty = v
                        });
                    }
                    else
                    {
                        // 情況 B: "Date[(Time)], O,H,L,C,Vol"
                        if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out klineTime))
                            continue;

                        if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var o2)) continue;
                        if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var h2)) continue;
                        if (!double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var l2)) continue;
                        if (!double.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var c2)) continue;
                        if (!long.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var v2)) continue;

                        list.Add(new JsonKLineRecord
                        {
                            KlineTime = klineTime,
                            Open = o2,
                            High = h2,
                            Low = l2,
                            Close = c2,
                            Qty = v2
                        });
                    }
                }
                catch (Exception)
                {
                    // 單行解析失敗不讓整個流程中斷；可加 debug log 追蹤
                }
            }

            list.Sort((a, b) => a.KlineTime.CompareTo(b.KlineTime));
            return list;
        }
    }
}

