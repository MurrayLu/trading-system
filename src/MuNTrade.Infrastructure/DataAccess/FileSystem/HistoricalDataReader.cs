using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Core.Settings;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Infrastructure.DataAccess.FileSystem
{
    public partial class HistoricalDataReader
    {
        private readonly ILogger<HistoricalDataReader> _log;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly string _basePath;

        // ✅ 改為 IOptions<DataSourceSettings>
        public HistoricalDataReader(
            ILogger<HistoricalDataReader> log,
            IOptions<DataSourceSettings> options)
        {
            _log = log;
            _basePath = options.Value.KLineJsonBasePath;

            if (string.IsNullOrWhiteSpace(_basePath))
            {
                // 後備：App 執行目錄下的 "K線資料"
                _basePath = Path.Combine(AppContext.BaseDirectory, "K線資料");
            }

            _jsonOptions = new JsonSerializerOptions
            {
                // 改為 null：尊重類別上的 [JsonPropertyName]，避免編碼產生意外的 camelCase
                PropertyNamingPolicy = null,
                WriteIndented = true,
                // 讀取時容錯大小寫（如果你想保留）可在 Deserialize 時用不同 options
            };
        }

        public static string GetPrefix(string timeframeKey) => timeframeKey switch
        {
            "Time_1" => "1 分 K 線",
            "Time_5" => "5 分 K 線",
            "Time_60" => "60 分 K 線",
            "Day" => "日 K 線",
            "Week" => "週 K 線",
            "Month" => "月 K 線",
            _ => timeframeKey
        };

        // 寬鬆讀取選項（允許尾逗號、忽略註解）
        private static readonly JsonSerializerOptions _looseReadOptions = new()
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true // 讀時容忍大小寫或不同命名（過渡期）
        };

        /// <summary>
        /// 單檔讀取（容錯 + 自動修復）
        /// </summary>
        public async Task<List<JsonKLineRecord>> ReadFileAsync(string filePath, CancellationToken ct = default)
        {
            try
            {
                // 優先直接反序列化為 canonical 型別（支援大小寫差異由 _looseReadOptions 負責）
                await using var fs = File.OpenRead(filePath);
                var data = await JsonSerializer.DeserializeAsync<List<JsonKLineRecord>>(fs, _looseReadOptions, ct);
                if (data != null && data.Count > 0)
                    return data;
            }
            catch (JsonException jex)
            {
                _log.LogWarning(jex, "[K線讀取] JSON parse failed, will try sanitize/normalize: {File}", filePath);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[K線讀取] Read JSON failed: {File}", filePath);
            }

            // fallback：讀文字，sanitize，嘗試辨識其他 schema 並 map 成 JsonKLineRecord
            try
            {
                var text = await File.ReadAllTextAsync(filePath, ct);
                var sanitized = SanitizeJson(text);

                // 若檔案看起來是 B 格式（含 KlineTime），直接轉成 JsonKLineRecord（大小寫不敏感）
                if (sanitized.IndexOf("\"KlineTime\"", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var listB = JsonSerializer.Deserialize<List<JsonKLineRecord>>(sanitized, _looseReadOptions);
                    if (listB != null && listB.Count > 0) return listB;
                }

                // 若是舊 A 格式 (from/to/open/volume)，嘗試解析成匿名型別再 map
                // 這裡可加入更複雜的 normalize（見之前提供的 TryNormalize 範例）
                _log.LogWarning("[K線讀取] ReadFile fallback 未能解析成 canonical: {File}", filePath);
                return new List<JsonKLineRecord>();
            }
            catch (Exception ex2)
            {
                _log.LogWarning(ex2, "[K線讀取] Read JSON failed after sanitize: {File}", filePath);
                return new List<JsonKLineRecord>();
            }
        }

        // 簡單的尾逗號修復器
        private static string SanitizeJson(string text)
        {
            // 去掉 BOM
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text.TrimStart('\uFEFF');

            // 把陣列/物件結尾的 ",]" / ",}" 換掉
            text = Regex.Replace(text, @",(\s*])", "$1");
            text = Regex.Replace(text, @",(\s*})", "$1");

            // 最後一行多逗號
            text = Regex.Replace(text, @",(?!\s*[\}\]])\s*$", "", RegexOptions.Multiline);

            return text;
        }

        /// <summary>讀取最近 N 根列 K（會跨多個月檔案往回讀）。</summary>
        public async Task<List<JsonKLineRecord>> ReadLastNBarsAsync(
            string symbol, string timeframeKey, int n, CancellationToken ct = default)
        {
            var prefix = GetPrefix(timeframeKey);
            var dir = Path.Combine(_basePath, symbol);
            if (!Directory.Exists(dir)) return new List<JsonKLineRecord>();

            // 從新到舊讀，湊滿最近 N 根就停，避免歷史越多越慢。
            var files = Directory.GetFiles(dir, $"{prefix}_*.json")
                                 .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                                 .ToArray();

            // 以最新檔優先；若跨檔重疊，保留較新的檔案內容。
            var map = new Dictionary<DateTime, JsonKLineRecord>();
            foreach (var f in files)
            {
                var rows = await ReadFileAsync(f, ct);
                for (int i = rows.Count - 1; i >= 0; i--)
                {
                    var r = rows[i];
                    var key = ResolveRecordTime(r);
                    if (key == default) continue;

                    map.TryAdd(key, r);
                }

                if (map.Count >= n)
                    break;
            }

            if (map.Count == 0) return new List<JsonKLineRecord>();

            return map.OrderBy(kv => kv.Key)
                      .Select(kv => kv.Value)
                      .TakeLast(n)
                      .ToList();
        }


        /// <summary>讀取 [from..to]（以 Json 內容時間範圍篩選）。</summary>
        public async Task<List<JsonKLineRecord>> ReadRangeAsync(
            string symbol, string timeframeKey, DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            var prefix = GetPrefix(timeframeKey);
            var dir = Path.Combine(_basePath, symbol);
            if (!Directory.Exists(dir)) return new();

            var files = Directory.GetFiles(dir, $"{prefix}_*.json").OrderBy(x => x).ToArray();
            var outp = new List<JsonKLineRecord>(8192);

            DateTime fromDt = from.ToDateTime(TimeOnly.MinValue);
            DateTime toDt = to.ToDateTime(TimeOnly.MaxValue);

            foreach (var f in files)
            {
                var rows = await ReadFileAsync(f, ct);
                foreach (var r in rows)
                {
                    if (r.To >= fromDt && r.From <= toDt)
                        outp.Add(r);
                }
            }

            return outp.OrderBy(x => x.To).ToList();
        }

        private static DateTime ResolveRecordTime(JsonKLineRecord record)
        {
            if (record.To != default) return record.To;
            if (record.KlineTime != default) return record.KlineTime;
            return record.From;
        }
    }
}

