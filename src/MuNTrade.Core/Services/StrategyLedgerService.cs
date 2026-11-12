using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.MarketData;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Hubs;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using MuNTrade.Core.Capital; // Patch 18: Path unification
using MuNTrade.Core.Settings;

namespace MuNTrade.Core.Services
{
    public class StrategyLedgerService : IDisposable
    {
        private readonly IEventBus _eventBus;
        private readonly ILogger<StrategyLedgerService> _logger;
        private readonly string _paperRoot;
        private readonly string _liveRoot;
        private readonly string? _configuredPaperRoot;
        private readonly ConcurrentQueue<CompletedTrade> _paramQueue = new();
        private Task? _workerTask;
        private readonly System.Threading.CancellationTokenSource _cts = new();
        private int _inflight = 0;

        private const int LedgerSchemaVersion = 1;
        private static readonly JsonSerializerOptions _ledgerJsonOptions = new()
        {
            WriteIndented = false
        };

        private sealed record StrategyLedgerEnvelope(
            int SchemaVersion,
            DateTimeOffset RecordedAt,
            CompletedTrade Trade);

        // Patch 25: Internal in-memory cache for ViewModel UI
        // Since this is just a ledger service, keeping all strategy trades in memory is 
        // acceptable for a session-based desktop app (unless millions of trades).
        private readonly ConcurrentDictionary<string, List<CompletedTrade>> _strategyTrades = new();

        // Patch 25: Strategy Stats & Diagnostics
        private readonly ConcurrentDictionary<string, StrategyStats> _strategyStats = new();

        private long _unattributedCount = 0;
        private long _conflictedCount = 0;
        private long _registryMissCount = 0;

        public StrategyLedgerService(
            IEventBus eventBus,
            ILogger<StrategyLedgerService> logger,
            IOptionsMonitor<BacktestSettings> backtestSettings)
        {
            _eventBus = eventBus;
            _logger = logger;

            var configuredRoot = backtestSettings.CurrentValue.StrategyEventRoot;
            _configuredPaperRoot = string.IsNullOrWhiteSpace(configuredRoot)
                ? null
                : Path.GetFullPath(configuredRoot);

            string simPath;
            string livePath;
            if (_configuredPaperRoot is null)
            {
                simPath = CapitalConfigService.GetSimAccountsPath();
                livePath = CapitalConfigService.GetLiveAccountsPath();
                _paperRoot = HistoryPath.ResolveStrategiesRoot("Sim:Paper:All:Default", ensureExists: true);
                _liveRoot = HistoryPath.ResolveStrategiesRoot("Real", ensureExists: true);
            }
            else
            {
                simPath = _configuredPaperRoot;
                livePath = _configuredPaperRoot;
                _paperRoot = _configuredPaperRoot;
                _liveRoot = _configuredPaperRoot;
                Directory.CreateDirectory(_configuredPaperRoot);
            }

            _eventBus.Subscribe<CompletedTrade>(OnCompletedTrade);
            _logger.LogInformation("[StrategyLedger] Started. PaperRoot={PaperRoot} LiveRoot={LiveRoot} SimPath={SimPath} LivePath={LivePath}",
                _paperRoot, _liveRoot, simPath, livePath);

            _workerTask = Task.Run(ProcessQueueAsync);
        }

        private void OnCompletedTrade(CompletedTrade trade)
        {
            // Update in-memory cache
            var sid = string.IsNullOrEmpty(trade.StrategyId) ? "Unknown" : trade.StrategyId;

            // Diagnostic Counters
            if (IsMissingStrategyId(trade.StrategyId))
            {
                Interlocked.Increment(ref _unattributedCount);
                _logger.LogWarning("[StrategyLedger] Received trade with missing StrategyId. TradeId={TradeId}", trade.ExitCorrelationId);
            }
            if (trade.AttributionConflict)
                Interlocked.Increment(ref _conflictedCount);

            _strategyTrades.AddOrUpdate(sid,
                new List<CompletedTrade> { trade },
                (key, list) =>
                {
                    lock (list) { list.Add(trade); }
                    return list;
                });

            // Update Stats
            if (_strategyTrades.TryGetValue(sid, out var currentList))
            {
                // Note: currentList is locked inside AddOrUpdate factory, but safely accessible here? 
                // Wait, GetOrAdd returns the value. Concurrency is tricky.
                // We should take a snapshot for stats calculation to avoid extended locking.
                List<CompletedTrade> snapshot;
                lock (currentList) { snapshot = currentList.ToList(); }

                var stats = ComputeStats(snapshot);
                _strategyStats[sid] = stats;
            }

            var accountKey = ResolveAccountKey(trade);
            var strategiesRoot = ResolveStrategiesRoot(accountKey);
            var safeKey = ToSafeStrategyKey(ExtractStrategyDisplayName(trade.StrategyId));
            var exit = trade.ExitTime == default ? DateTimeOffset.Now : trade.ExitTime;
            var month = exit.ToString("yyyyMM");
            var filePath = Path.Combine(strategiesRoot, safeKey, $"{month}.jsonl");
            _logger.LogInformation("[StrategyLedger] Enqueue Stock={Stock} StrategyId={StrategyId} Exit={Exit} Path={Path}",
                trade.StockId, trade.StrategyId, exit, filePath);

            _paramQueue.Enqueue(trade);
        }

        private StrategyStats ComputeStats(List<CompletedTrade> trades)
        {
            if (trades.Count == 0) return new StrategyStats(0, 0, 0, 0, null);

            var totalPnL = trades.Sum(t => t.NetPnL);
            var wins = trades.Count(t => t.NetPnL > 0);
            var winRate = (decimal)wins / trades.Count;
            var avgTrade = totalPnL / trades.Count;
            var lastTime = trades.Max(t => t.ExitTime);

            return new StrategyStats(trades.Count, totalPnL, winRate, avgTrade, lastTime);
        }

        private async Task ProcessQueueAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                // Simple batch processing
                if (_paramQueue.TryDequeue(out var trade))
                {
                    try
                    {
                        Interlocked.Increment(ref _inflight);
                        await WriteTradeAsync(trade);
                    }
                    catch (Exception ex)
                    {
                        var accountKey = ResolveAccountKey(trade);
                        var strategiesRoot = ResolveStrategiesRoot(accountKey);
                        var safeKey = ToSafeStrategyKey(ExtractStrategyDisplayName(trade?.StrategyId));
                        var exit = trade.ExitTime == default ? DateTimeOffset.Now : trade.ExitTime;
                        var month = exit.ToString("yyyyMM");
                        var filePath = Path.Combine(strategiesRoot, safeKey, $"{month}.jsonl");
                        _logger.LogError(ex,
                            "Failed to write trade to strategy ledger. StrategyId={StrategyId} SafeKey={SafeKey} Path={Path}",
                            trade?.StrategyId, safeKey, filePath);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inflight);
                    }
                }
                else
                {
                    await Task.Delay(100, _cts.Token);
                }
            }
        }

        private async Task WriteTradeAsync(CompletedTrade trade)
        {
            // {HistoryDir}\Strategies\{SafeStrategyKey}\{yyyyMM}.jsonl
            var accountKey = ResolveAccountKey(trade);
            var strategiesRoot = ResolveStrategiesRoot(accountKey);
            var safeKey = ToSafeStrategyKey(ExtractStrategyDisplayName(trade?.StrategyId));
            var strategyDir = Path.Combine(strategiesRoot, safeKey);
            Directory.CreateDirectory(strategyDir);

            var exit = trade.ExitTime == default ? DateTimeOffset.Now : trade.ExitTime;
            var month = exit.ToString("yyyyMM");
            var filePath = Path.Combine(strategyDir, $"{month}.jsonl");

            var envelope = new StrategyLedgerEnvelope(
                SchemaVersion: LedgerSchemaVersion,
                RecordedAt: DateTimeOffset.UtcNow,
                Trade: trade);

            var json = JsonSerializer.Serialize(envelope, _ledgerJsonOptions);

            try
            {
                await File.AppendAllTextAsync(
                    filePath,
                    json + Environment.NewLine,
                    Encoding.UTF8,
                    _cts.Token);
                _logger.LogInformation("[StrategyLedger] Appended {Path}", filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to write trade to strategy ledger. StrategyId={StrategyId} SafeKey={SafeKey} Path={Path}",
                    trade?.StrategyId, safeKey, filePath);
                throw;
            }
        }

        private string ResolveStrategiesRoot(string? accountKey)
        {
            if (_configuredPaperRoot is not null && HistoryPath.IsPaperAccountKey(accountKey))
            {
                Directory.CreateDirectory(_configuredPaperRoot);
                return _configuredPaperRoot;
            }

            return HistoryPath.ResolveStrategiesRoot(accountKey, ensureExists: true);
        }

        private static string ExtractStrategyDisplayName(string? strategyId)
        {
            // Fixes truncation issue: "Verification.CIS.OrbP95.Mur" should NOT become "Mur"
            if (string.IsNullOrWhiteSpace(strategyId)) return "UnknownStrategy";

            var s = strategyId.Trim();

            // 1. Remove Instance ID part (after #)
            var hashIndex = s.IndexOf('#');
            if (hashIndex > 0)
                s = s.Substring(0, hashIndex);

            // 2. Remove Session ID part if any (after :) - keeping Template ID intact
            // CAUTION: Some formats might use colons in the ID itself, verify if this is safe.
            // Assuming standard format "TemplateId:InstanceId" or similar.
            // If the template ID itself contains dots (Verification.CIS...), we want to KEEP them.

            // The previous logic was:
            // var colonIndex = s.IndexOf(':'); if > 0 substring...
            // var dotIndex = s.LastIndexOf('.'); if > 0 substring... <-- THIS WAS THE CULPRIT

            // We will simplify: just strip the dynamic instance suffix (usually after # or maybe :)
            // If we want to clean up "Verification.CIS.OrbP95.Mur", we should keep it as is.

            // If there is a need to remove namespaces but keep the last meaningful part + variant:
            // usage: "Verification.CIS.OrbP95.Mur" -> "Verification.CIS.OrbP95.Mur" (No change)

            return string.IsNullOrWhiteSpace(s) ? "UnknownStrategy" : s;
        }

        private static bool IsMissingStrategyId(string? strategyId)
        {
            return string.IsNullOrWhiteSpace(strategyId)
                || strategyId.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                || strategyId.Equals("UnknownStrategy", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveAccountKey(CompletedTrade trade)
        {
            if (!string.IsNullOrWhiteSpace(trade.AccountKey) &&
                !trade.AccountKey.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return trade.AccountKey;
            }
            if (!string.IsNullOrWhiteSpace(trade.Account)) return trade.Account;
            return "Real";
        }

        private static string ToSafeStrategyKey(string? strategyId)
        {
            if (string.IsNullOrWhiteSpace(strategyId))
                return "UnknownStrategy";

            var s = strategyId.Trim();

            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');

            s = s.Replace(':', '_').Replace('#', '_');

            while (s.Contains("  ")) s = s.Replace("  ", " ");
            s = s.Trim();

            if (string.IsNullOrWhiteSpace(s))
                s = "UnknownStrategy";

            const int maxLen = 80;
            if (s.Length > maxLen) s = s.Substring(0, maxLen);

            return s;
        }

        // Patch 25: Public API for ViewModel
        public IEnumerable<string> GetStrategyIds()
            => _strategyTrades.Keys.OrderBy(x => x);

        public IEnumerable<CompletedTrade> GetTrades(string strategyId)
            => _strategyTrades.TryGetValue(strategyId, out var list)
                // Return a copy to be safe
                ? list.ToList()
                : Enumerable.Empty<CompletedTrade>();

        public IReadOnlyList<EquityPoint> GetEquityCurve(string strategyId)
        {
            if (!_strategyTrades.TryGetValue(strategyId, out var list))
                return Array.Empty<EquityPoint>();

            List<CompletedTrade> snapshot;
            lock (list) { snapshot = list.OrderBy(t => t.ExitTime).ToList(); }

            var points = new List<EquityPoint>(snapshot.Count);
            decimal equity = 0m;
            foreach (var t in snapshot)
            {
                equity += t.NetPnL;
                points.Add(new EquityPoint(t.ExitTime, equity));
            }
            return points;
        }

        public StrategyStats? GetStats(string id) => _strategyStats.TryGetValue(id, out var s) ? s : null;

        public long UnattributedCount => Interlocked.Read(ref _unattributedCount);
        public long ConflictedCount => Interlocked.Read(ref _conflictedCount);
        public long RegistryMissCount => Interlocked.Read(ref _registryMissCount);

        // Optional: 外部回報 registry miss
        public void ReportRegistryMiss() => Interlocked.Increment(ref _registryMissCount);

        public async Task FlushAsync(CancellationToken ct = default)
        {
            while (!ct.IsCancellationRequested)
            {
                if (_paramQueue.IsEmpty && Volatile.Read(ref _inflight) == 0)
                    return;
                await Task.Delay(50, ct);
            }
        }

        public void Dispose()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                FlushAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // ignore flush errors on dispose
            }

            _cts.Cancel();
            try { _workerTask?.Wait(1000); } catch { }
            _cts.Dispose();
        }
    }

    // Patch 25: Strategy Stats Record
    public sealed record StrategyStats(
        int TradeCount,
        decimal TotalNetPnL,
        decimal WinRate,
        decimal AvgTrade,
        DateTime? LastTradeTime
    );

    // Patch 29: Equity Point Record
    public sealed record EquityPoint(DateTime Time, decimal Equity);
}

