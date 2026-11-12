using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Core.Capital;

namespace MuNTrade.Core.Services
{
    public sealed class StrategyParamService : IStrategyParamService
    {
        private readonly StrategyParameters _defaults;
        private readonly StrategyParamSchema _schema;
        private readonly ConcurrentDictionary<string, StrategyParameters> _cache = new();
        private readonly ConcurrentDictionary<string, StrategyParameters> _scopedOverrides = new(StringComparer.OrdinalIgnoreCase);
        private readonly IOptionsMonitor<ApplicationSettings> _appSettings;

        public StrategyParamService(IOptions<StrategyParameters> options, IOptionsMonitor<ApplicationSettings> appSettings)
        {
            _defaults = options.Value;
            _schema = BuildSchema();
            _appSettings = appSettings;
        }

        public StrategyParamSchema GetSchema(string strategyId) => _schema;

        public StrategyParameters GetParameters(string strategyId)
        {
            var safeKey = NormalizeKey(strategyId);
            if (_scopedOverrides.TryGetValue(safeKey, out var scoped))
                return scoped;

            var (accountKey, baseDir, legacyBaseDir, legacyAltBaseDir) = ResolveDirs();
            var cacheKey = BuildCacheKey(accountKey, safeKey);

            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;

            var loaded = LoadOrDefault(safeKey, baseDir, legacyBaseDir, legacyAltBaseDir);
            _cache[cacheKey] = loaded;
            return loaded;
        }

        public void UpdateParameters(string strategyId, StrategyParameters parameters)
        {
            var safeKey = NormalizeKey(strategyId);
            var (accountKey, baseDir, _, _) = ResolveDirs();
            var cacheKey = BuildCacheKey(accountKey, safeKey);
            _cache[cacheKey] = parameters;
            Save(safeKey, parameters, baseDir);
        }

        public void SetScopedOverride(string strategyId, StrategyParameters parameters)
        {
            var safeKey = NormalizeKey(strategyId);
            _scopedOverrides[safeKey] = parameters;
        }

        public void ClearScopedOverride(string strategyId)
        {
            var safeKey = NormalizeKey(strategyId);
            _scopedOverrides.TryRemove(safeKey, out _);
        }

        private StrategyParamSchema BuildSchema()
        {
            var fields = typeof(StrategyParameters)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => new StrategyParamField(
                    p.Name,
                    MapType(p.PropertyType),
                    p.PropertyType.IsEnum ? p.PropertyType.FullName : null))
                .ToList();
            return new StrategyParamSchema(fields);
        }

        private static StrategyParamFieldType MapType(Type t)
        {
            if (t == typeof(int) || t == typeof(long)) return StrategyParamFieldType.Integer;
            if (t == typeof(double) || t == typeof(decimal) || t == typeof(float)) return StrategyParamFieldType.Decimal;
            if (t == typeof(bool)) return StrategyParamFieldType.Boolean;
            if (t.IsEnum) return StrategyParamFieldType.Enum;
            return StrategyParamFieldType.String;
        }

        private StrategyParameters LoadOrDefault(string key, string baseDir, string legacyBaseDir, string legacyAltBaseDir)
        {
            var path = GetParamPath(baseDir, key);
            if (!File.Exists(path))
            {
                var legacy = GetParamPath(legacyBaseDir, key);
                if (!File.Exists(legacy))
                {
                    legacy = GetParamPath(legacyAltBaseDir, key);
                }

                if (File.Exists(legacy))
                    path = legacy;
                else
                    return _defaults;
            }

            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<StrategyParameters>(json);
                return loaded ?? _defaults;
            }
            catch
            {
                return _defaults;
            }
        }

        private void Save(string key, StrategyParameters parameters, string baseDir)
        {
            Directory.CreateDirectory(baseDir);
            var path = GetParamPath(baseDir, key);
            var json = JsonSerializer.Serialize(parameters);
            File.WriteAllText(path, json);
        }

        private static string GetParamPath(string dir, string key) => Path.Combine(dir, $"params_{key}.json");

        private (string AccountKey, string BaseDir, string LegacyBaseDir, string LegacyAltBaseDir) ResolveDirs()
        {
            var accountKey = ResolveAccountKey(_appSettings.CurrentValue.TradingGateway);
            var baseDir = ResolveBaseDir(accountKey);
            var legacyBaseDir = ResolveLegacyBaseDir(accountKey);
            var legacyAltBaseDir = ResolveLegacyAltBaseDir(accountKey);
            return (accountKey, baseDir, legacyBaseDir, legacyAltBaseDir);
        }

        private static string BuildCacheKey(string accountKey, string safeKey)
            => $"{accountKey}|{safeKey}";

        private static string ResolveBaseDir(string accountKey)
        {
            return HistoryPath.ResolveStrategiesRoot(accountKey, ensureExists: true);
        }

        private static string ResolveLegacyBaseDir(string accountKey)
        {
            var baseDir = HistoryPath.ResolveBaseDir(accountKey);
            return Path.Combine(baseDir, "History", "Strategy");
        }

        private static string ResolveLegacyAltBaseDir(string accountKey)
        {
            var baseDir = HistoryPath.ResolveBaseDir(accountKey);
            return Path.Combine(baseDir, "History", "Strategies");
        }

        private static string NormalizeKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "unknown";

            var safe = key.Trim()
                .Replace(":", "_")
                .Replace("#", "_")
                .Replace("/", "_")
                .Replace("\\", "_")
                .Replace(" ", "_");

            foreach (var c in Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');

            safe = safe.ToLowerInvariant();

            return safe.Length > 80 ? safe.Substring(0, 80) : safe;
        }

        private static string ResolveAccountKey(string? tradingGateway)
        {
            if (string.IsNullOrWhiteSpace(tradingGateway)) return "Real";
            var gate = tradingGateway.Trim();
            if (gate.Contains("PAPER", StringComparison.OrdinalIgnoreCase) ||
                gate.Contains("SIM", StringComparison.OrdinalIgnoreCase))
            {
                return "Sim:Paper:All:Default";
            }
            return "Real";
        }
    }
}

