using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Dtos.Rank;
using MuNTrade.Core.Settings;

namespace MuNTrade.Infrastructure.DataAccess.Rankings
{
    public sealed class JsonStockMasterIndexProvider : IStockMasterIndexProvider
    {
        private const string ListingMarket = "上市";
        private const string OtcMarket = "上櫃";

        private readonly ILogger<JsonStockMasterIndexProvider> _logger;
        private readonly Dictionary<string, StockMasterEntry> _byStockNo = new(StringComparer.OrdinalIgnoreCase);
        private readonly DataSourceSettings _dataSettings;
        private bool _loaded;

        public JsonStockMasterIndexProvider(
            ILogger<JsonStockMasterIndexProvider> logger,
            IOptions<DataSourceSettings> dataSettings)
        {
            _logger = logger;
            _dataSettings = dataSettings?.Value ?? new DataSourceSettings();
            LoadOnce();
        }

        public bool TryGet(string stockNo, out StockMasterEntry entry)
        {
            LoadOnce();
            return _byStockNo.TryGetValue(stockNo, out entry!);
        }

        private void LoadOnce()
        {
            if (_loaded)
                return;

            _loaded = true;

            var baseDir = ResolveStockListBasePath();
            var listingPath = Path.Combine(baseDir, "上市個股清單.json");
            var otcPath = Path.Combine(baseDir, "上櫃個股清單.json");

            LoadFile(listingPath);
            LoadFile(otcPath);

            _logger.LogInformation("[StockMasterIndex] Loaded {Count} entries.", _byStockNo.Count);
        }

        private string ResolveStockListBasePath()
        {
            if (!string.IsNullOrWhiteSpace(_dataSettings.StockListBasePath))
                return _dataSettings.StockListBasePath;

            var baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, "Resources", "StockLists");
        }

        private void LoadFile(string path)
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("[StockMasterIndex] File not found: {Path}", path);
                return;
            }

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var items = JsonSerializer.Deserialize<List<StockListItem>>(json, JsonOptions);
                if (items == null || items.Count == 0)
                {
                    _logger.LogWarning("[StockMasterIndex] Empty list: {Path}", path);
                    return;
                }

                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.StockNo))
                        continue;

                    var market = MapMarket(item.MarketNo);
                    if (market == "-")
                        continue;

                    var entry = new StockMasterEntry
                    {
                        StockNo = item.StockNo,
                        StockName = item.StockName ?? "-",
                        Market = market
                    };

                    if (_byStockNo.TryGetValue(item.StockNo, out var existing))
                    {
                        if (string.Equals(existing.Market, entry.Market, StringComparison.OrdinalIgnoreCase))
                        {
                            if (existing.StockName == "-" && entry.StockName != "-")
                                _byStockNo[item.StockNo] = entry;
                            continue;
                        }

                        if (existing.Market == "-" && entry.Market != "-")
                        {
                            _byStockNo[item.StockNo] = entry;
                            continue;
                        }

                        _logger.LogDebug(
                            "[StockMasterIndex] Duplicate StockNo={StockNo} ({OldMarket} vs {NewMarket}), keeping existing.",
                            item.StockNo, existing.Market, entry.Market);
                        continue;
                    }

                    _byStockNo[item.StockNo] = entry;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StockMasterIndex] Load failed: {Path}", path);
            }
        }

        private sealed class StockListItem
        {
            public string? StockNo { get; set; }
            public string? StockName { get; set; }
            public int? MarketNo { get; set; }
        }

        private static string MapMarket(int? marketNo)
        {
            return marketNo switch
            {
                0 => ListingMarket,
                1 => OtcMarket,
                _ => "-"
            };
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };
    }
}

