using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Brokers;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;
using MuNTrade.Abstractions.Dtos.Rank;
using MuNTrade.Core.Settings;

namespace MuNTrade.Infrastructure.DataAccess.Rankings
{
    public sealed class StockListDownloadService : IStockListDownloadService
    {
        private static readonly Encoding Big5 = Encoding.GetEncoding(950);
        private readonly ILogger<StockListDownloadService> _logger;
        private readonly ICapitalApiService? _capitalApi;
        private readonly DataSourceSettings _dataSettings;

        public StockListDownloadService(
            ILogger<StockListDownloadService> logger,
            IOptions<DataSourceSettings> dataSettings,
            ICapitalApiService? capitalApi = null)
        {
            _logger = logger;
            _capitalApi = capitalApi;
            _dataSettings = dataSettings?.Value ?? new DataSourceSettings();
        }

        public async Task<StockListDownloadResult> DownloadAsync(CancellationToken ct = default)
        {
            var outDir = ResolveStockListBasePath();
            Directory.CreateDirectory(outDir);

            if (_capitalApi == null || _capitalApi.CurrentState != ConnectionState.Connected)
                throw new InvalidOperationException("SKCOM 未連線。請確認連線正常後再下載。");

            var (listingItems, otcItems) = await DownloadFromSkcomAsync(ct).ConfigureAwait(false);

            if (listingItems.Count == 0)
                _logger.LogWarning("[StockListDownload] Parsed 0 listed items.");
            if (otcItems.Count == 0)
                _logger.LogWarning("[StockListDownload] Parsed 0 otc items.");

            var listingPath = Path.Combine(outDir, "上市個股清單.json");
            var otcPath = Path.Combine(outDir, "上櫃個股清單.json");

            await SaveJsonAsync(listingPath, listingItems, ct).ConfigureAwait(false);
            await SaveJsonAsync(otcPath, otcItems, ct).ConfigureAwait(false);

            _logger.LogInformation("[StockListDownload] Saved listed={Listed} otc={Otc} dir={Dir}",
                listingItems.Count, otcItems.Count, outDir);

            return new StockListDownloadResult(listingItems.Count, otcItems.Count, listingPath, otcPath);
        }

        private string ResolveStockListBasePath()
        {
            if (!string.IsNullOrWhiteSpace(_dataSettings.StockListBasePath))
                return _dataSettings.StockListBasePath;

            var baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, "Resources", "StockLists");
        }

        private async Task<(List<StockListItem> Listing, List<StockListItem> Otc)> DownloadFromSkcomAsync(CancellationToken ct)
        {
            var capitalApi = _capitalApi ?? throw new InvalidOperationException("SKCOM 未連線。請確認連線正常後再下載。");
            var listingMap = new Dictionary<string, StockListItem>(StringComparer.OrdinalIgnoreCase);
            var otcMap = new Dictionary<string, StockListItem>(StringComparer.OrdinalIgnoreCase);
            var issuanceMap = LoadIssuanceMap();

            var listingTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var otcTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handle(short marketNo, string data)
            {
                if (marketNo != 0 && marketNo != 1)
                    return;
                if (string.IsNullOrWhiteSpace(data))
                    return;

                var trimmed = DecodeBig5IfNeeded(data).Trim();

                bool isDone = trimmed.StartsWith("##", StringComparison.Ordinal);
                string payload = trimmed;
                if (!isDone)
                {
                    var endIdx = trimmed.IndexOf("##", StringComparison.Ordinal);
                    if (endIdx >= 0)
                    {
                        isDone = true;
                        payload = trimmed.Substring(0, endIdx);
                    }
                }

                if (!string.IsNullOrWhiteSpace(payload))
                {
                    var items = ParseSkcomPayload(payload, marketNo, issuanceMap);
                    var target = marketNo == 0 ? listingMap : otcMap;
                    foreach (var item in items)
                    {
                        if (string.IsNullOrWhiteSpace(item.StockNo)) continue;
                        if (!target.ContainsKey(item.StockNo))
                            target[item.StockNo] = item;
                    }
                }

                if (isDone)
                {
                    if (marketNo == 0)
                        listingTcs.TrySetResult(true);
                    else if (marketNo == 1)
                        otcTcs.TrySetResult(true);
                }
            }

            capitalApi.StockListDataReceived += Handle;
            try
            {
                _ = Task.Run(() => capitalApi.RequestStockList(0), ct);
                _ = Task.Run(() => capitalApi.RequestStockList(1), ct);

                var timeout = Task.Delay(TimeSpan.FromSeconds(15), ct);
                var allDone = Task.WhenAll(listingTcs.Task, otcTcs.Task);

                var done = await Task.WhenAny(allDone, timeout).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (done == timeout)
                    throw new TimeoutException("等待 StockList 回傳逾時。");

                return (
                    listingMap.Values.OrderBy(x => x.StockNo, StringComparer.OrdinalIgnoreCase).ToList(),
                    otcMap.Values.OrderBy(x => x.StockNo, StringComparer.OrdinalIgnoreCase).ToList()
                );
            }
            finally
            {
                capitalApi.StockListDataReceived -= Handle;
            }
        }

        private static IEnumerable<StockListItem> ParseSkcomPayload(
            string payload,
            short marketNo,
            IReadOnlyDictionary<string, double?> issuanceMap)
        {
            var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var lineRaw in lines)
            {
                var line = lineRaw.Replace("\r", "").Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var rows = line.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var raw in rows)
                {
                    var entry = raw.Trim();
                    if (string.IsNullOrWhiteSpace(entry)) continue;

                    var parts = entry.Split(',');
                    if (parts.Length < 2) continue;

                    var code = parts[0].Trim().ToUpperInvariant();
                    var name = parts[1].Trim();
                    if (code.Length < 4 || code.Length > 6) continue;
                    if (!code.All(ch => char.IsDigit(ch) || (ch >= 'A' && ch <= 'Z'))) continue;

                    issuanceMap.TryGetValue(code, out var issuance);

                    yield return new StockListItem
                    {
                        StockNo = code,
                        StockName = string.IsNullOrWhiteSpace(name) ? "-" : name,
                        MarketNo = marketNo,
                        IssuanceCount = issuance
                    };
                }
            }
        }

        private static Task SaveJsonAsync(string path, List<StockListItem> items, CancellationToken ct)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(items, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            return File.WriteAllTextAsync(path, json, Encoding.UTF8, ct);
        }

        private sealed class StockListItem
        {
            public string? StockNo { get; set; }
            public string? StockName { get; set; }
            public int? MarketNo { get; set; }
            public double? IssuanceCount { get; set; }
        }

        private static string DecodeBig5IfNeeded(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var decoded = DecodeBig5Raw(input);
            return CountCjk(decoded) > CountCjk(input) ? decoded : input;
        }

        private static string DecodeBig5Raw(string input)
        {
            var raw = new byte[input.Length];
            for (int i = 0; i < input.Length; i++)
                raw[i] = (byte)(input[i] & 0xFF);
            return Big5.GetString(raw);
        }

        private static int CountCjk(string s)
        {
            int count = 0;
            foreach (var ch in s)
            {
                if (ch >= 0x4E00 && ch <= 0x9FFF) count++;
                else if (ch >= 0x3400 && ch <= 0x4DBF) count++;
                else if (ch >= 0xF900 && ch <= 0xFAFF) count++;
            }
            return count;
        }

        private Dictionary<string, double?> LoadIssuanceMap()
        {
            try
            {
                var baseDir = ResolveStockListBasePath();
                var path = Path.Combine(baseDir, "上市上櫃個股發行張數.json");
                if (!File.Exists(path))
                    return new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);

                var json = File.ReadAllText(path, Encoding.UTF8);
                List<IssuanceItem>? items = null;
                try
                {
                    items = System.Text.Json.JsonSerializer.Deserialize<List<IssuanceItem>>(json, new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch
                {
                    items = ParseIssuanceFileFallback(path);
                }

                if (items == null || items.Count == 0)
                    return new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);

                var map = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    var code = item.StockNo?.Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    map[code] = item.IssuanceCount;
                }
                return map;
            }
            catch
            {
                return new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static List<IssuanceItem> ParseIssuanceFileFallback(string path)
        {
            var list = new List<IssuanceItem>();
            string? currentStockNo = null;

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                var commentIdx = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIdx >= 0)
                    line = line.Substring(0, commentIdx).Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.Contains("\"StockNo\"", StringComparison.OrdinalIgnoreCase))
                {
                    var value = ExtractJsonString(line, "StockNo");
                    if (!string.IsNullOrWhiteSpace(value))
                        currentStockNo = value.Trim().ToUpperInvariant();
                }

                if (line.Contains("\"IssuanceCount\"", StringComparison.OrdinalIgnoreCase))
                {
                    var value = ExtractJsonNumber(line, "IssuanceCount");
                    if (!string.IsNullOrWhiteSpace(currentStockNo))
                    {
                        list.Add(new IssuanceItem
                        {
                            StockNo = currentStockNo,
                            IssuanceCount = ParseNullableDouble(value)
                        });
                        currentStockNo = null;
                    }
                }
            }

            return list;
        }

        private static string? ExtractJsonString(string line, string key)
        {
            var keyIdx = line.IndexOf($"\"{key}\"", StringComparison.OrdinalIgnoreCase);
            if (keyIdx < 0) return null;
            var colonIdx = line.IndexOf(':', keyIdx);
            if (colonIdx < 0) return null;
            var firstQuote = line.IndexOf('"', colonIdx + 1);
            if (firstQuote < 0) return null;
            var secondQuote = line.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0) return null;
            return line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }

        private static string? ExtractJsonNumber(string line, string key)
        {
            var keyIdx = line.IndexOf($"\"{key}\"", StringComparison.OrdinalIgnoreCase);
            if (keyIdx < 0) return null;
            var colonIdx = line.IndexOf(':', keyIdx);
            if (colonIdx < 0) return null;
            var tail = line.Substring(colonIdx + 1).Trim().TrimEnd(',').Trim();
            return tail;
        }

        private static double? ParseNullableDouble(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)) return null;
            if (double.TryParse(value, out var v)) return v;
            return null;
        }

        private sealed class IssuanceItem
        {
            public string? StockNo { get; set; }
            public double? IssuanceCount { get; set; }
        }
    }
}

