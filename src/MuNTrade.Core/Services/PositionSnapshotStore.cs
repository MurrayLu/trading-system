using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Core.Capital;
using MuNTrade.Abstractions.Contracts.Time;

namespace MuNTrade.Core.Services
{
    public sealed class PositionSnapshotStore
    {
        private readonly ILogger<PositionSnapshotStore> _logger;
        private readonly IClock _clock;
        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        public PositionSnapshotStore(ILogger<PositionSnapshotStore> logger, IClock clock)
        {
            _logger = logger;
            _clock = clock;
        }

        public IReadOnlyList<Position> LoadPositions(string accountKey)
        {
            var path = GetSnapshotPath(accountKey);
            if (!File.Exists(path))
            {
                var legacy = GetLegacySnapshotPath(accountKey);
                if (File.Exists(legacy))
                    path = legacy;
                else
                    return Array.Empty<Position>();
            }

            try
            {
                var json = File.ReadAllText(path);
                var snapshot = JsonSerializer.Deserialize<PositionSnapshotFile>(json, _jsonOptions);
                return snapshot?.Positions?.ToList() ?? new List<Position>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PositionSnapshotStore] Failed to load snapshot: {Path}", path);
                return Array.Empty<Position>();
            }
        }

        public void SavePositions(string accountKey, IEnumerable<Position> positions)
        {
            var path = GetSnapshotPath(accountKey);
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var snapshot = new PositionSnapshotFile
                {
                    SchemaVersion = 2,
                    AccountKey = accountKey ?? string.Empty,
                    UpdatedAt = _clock.Now,
                    Source = "LocalSnapshot",
                    Positions = positions?.ToList() ?? new List<Position>()
                };

                var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PositionSnapshotStore] Failed to save snapshot: {Path}", path);
            }
        }

        private string GetSnapshotPath(string accountKey)
        {
            return HistoryPath.ResolvePositionsPath(accountKey);
        }

        private static string GetLegacySnapshotPath(string accountKey)
        {
            var baseDir = HistoryPath.ResolveBaseDir(accountKey);
            var historyDir = Path.Combine(baseDir, "History");
            var safeAccount = HistoryPath.SanitizeFileName(string.IsNullOrWhiteSpace(accountKey) ? "Unknown" : accountKey);
            return Path.Combine(historyDir, $"positions_{safeAccount}.json");
        }

        private sealed class PositionSnapshotFile
        {
            public int SchemaVersion { get; set; } = 2;
            public string AccountKey { get; set; } = string.Empty;
            public string Source { get; set; } = "LocalSnapshot";
            public DateTime UpdatedAt { get; set; }
            public List<Position> Positions { get; set; } = new();
        }
    }
}

