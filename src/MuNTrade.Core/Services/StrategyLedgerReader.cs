using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.Trading;

namespace MuNTrade.Core.Services
{
    public sealed class StrategyLedgerReader
    {
        private readonly ILogger<StrategyLedgerReader> _logger;
        private readonly string _historyRoot;
        private readonly string? _legacyHistoryRoot;

        private sealed record StrategyLedgerEnvelope(int SchemaVersion, DateTimeOffset RecordedAt, CompletedTrade Trade);

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false
        };

        public StrategyLedgerReader(ILogger<StrategyLedgerReader> logger, string historyRoot)
        {
            _logger = logger;
            _historyRoot = historyRoot;
            _legacyHistoryRoot = ResolveLegacyRoot(historyRoot);
        }

        public string StrategiesRoot => Path.Combine(_historyRoot, "strategies");

        public IEnumerable<string> ListStrategies()
        {
            var roots = GetStrategyRoots();
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (!string.IsNullOrWhiteSpace(name))
                        dirs.Add(name);
                }
            }

            return dirs.OrderBy(x => x);
        }

        public IEnumerable<CompletedTrade> ReadTrades(string safeStrategyKey, int? yyyymmFrom = null, int? yyyymmTo = null)
        {
            if (string.IsNullOrWhiteSpace(safeStrategyKey))
                yield break;

            var roots = GetStrategyRoots();
            foreach (var root in roots)
            {
                var dir = Path.Combine(root, safeStrategyKey);
                if (!Directory.Exists(dir))
                    continue;

                var files = Directory.GetFiles(dir, "*.jsonl")
                    .OrderBy(f => f);

                foreach (var file in files)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (int.TryParse(name, out var yyyymm))
                    {
                        if (yyyymmFrom.HasValue && yyyymm < yyyymmFrom.Value) continue;
                        if (yyyymmTo.HasValue && yyyymm > yyyymmTo.Value) continue;
                    }

                    foreach (var line in File.ReadLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        CompletedTrade? tradeToYield = null;

                        try
                        {
                            var env = JsonSerializer.Deserialize<StrategyLedgerEnvelope>(line, _jsonOptions);
                            if (env?.Trade != null)
                            {
                                tradeToYield = env.Trade;
                            }
                        }
                        catch
                        {
                            // fallthrough to legacy format
                        }

                        if (tradeToYield != null)
                        {
                            yield return tradeToYield;
                            continue;
                        }

                        try
                        {
                            var trade = JsonSerializer.Deserialize<CompletedTrade>(line, _jsonOptions);
                            if (trade != null)
                            {
                                tradeToYield = trade;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Skip malformed strategy-ledger line. File={File}", file);
                        }

                        if (tradeToYield != null)
                        {
                            yield return tradeToYield;
                        }
                    }
                }
            }
        }

        private IEnumerable<string> GetStrategyRoots()
        {
            var roots = new List<string>
            {
                Path.Combine(_historyRoot, "strategies")
            };

            if (!string.IsNullOrWhiteSpace(_legacyHistoryRoot))
            {
                roots.Add(Path.Combine(_legacyHistoryRoot, "strategies"));
                roots.Add(Path.Combine(_legacyHistoryRoot, "Strategies"));
                roots.Add(Path.Combine(_legacyHistoryRoot, "Strategy"));
            }

            return roots;
        }

        private static string? ResolveLegacyRoot(string historyRoot)
        {
            var trimmed = historyRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(trimmed);
            if (leaf.Equals("paper", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("live", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(trimmed);
            }

            return null;
        }
    }
}

