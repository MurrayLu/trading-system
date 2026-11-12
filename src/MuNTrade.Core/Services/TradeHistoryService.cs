using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Core.Capital;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MuNTrade.Abstractions.Contracts.Time;

namespace MuNTrade.Core.Services
{
    /// <summary>
    /// Trade history store.
    /// - In-memory for UI
    /// - JSONL append for persistence
    /// </summary>
    public class TradeHistoryService
    {
        private readonly IEventBus _bus;
        private readonly ILogger<TradeHistoryService> _logger;
        private readonly IClock _clock;

        private readonly ConcurrentDictionary<string, ConcurrentBag<CompletedTrade>> _trades = new();
        private readonly object _fileLock = new();
        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

        public TradeHistoryService(IEventBus bus, ILogger<TradeHistoryService> logger, IClock clock)
        {
            _bus = bus;
            _logger = logger;
            _clock = clock;

            LoadRecentTrades(2000);

            _bus.Subscribe<CompletedTrade>(OnTradeCompleted);
            _logger.LogInformation("TradeHistoryService Started.");
        }

        private void OnTradeCompleted(CompletedTrade trade)
        {
            var accountKey = ResolveAccountKey(trade);
            var bag = _trades.GetOrAdd(accountKey, _ => new ConcurrentBag<CompletedTrade>());
            bag.Add(trade);
            AppendTrade(trade);
            _logger.LogInformation("[History] Trade Recorded [{Account}]: {Symbol} {Side} PnL:{PnL}",
                accountKey, trade.StockId, trade.Direction, trade.NetPnL);
        }

        public IEnumerable<CompletedTrade> GetHistory(string account = "Real")
        {
            if (_trades.TryGetValue(account, out var bag))
            {
                return bag.OrderByDescending(t => t.ExitTime);
            }
            return Enumerable.Empty<CompletedTrade>();
        }

        private void AppendTrade(CompletedTrade trade)
        {
            try
            {
                var date = (trade.ExitTime == default ? _clock.Now : trade.ExitTime).Date;
                var acct = ResolveAccountKey(trade);
                var path = GetTradeFilePath(acct, date);

                var json = JsonSerializer.Serialize(trade, _jsonOptions);
                lock (_fileLock)
                {
                    File.AppendAllText(path, json + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TradeHistoryService] Failed to append trade.");
            }
        }

        private void LoadRecentTrades(int max)
        {
            int loaded = 0;
            var paperRoot = HistoryPath.ResolveHistoryRoot("Sim:Paper:All:Default", ensureExists: false);
            var liveRoot = HistoryPath.ResolveHistoryRoot("Real", ensureExists: false);
            var legacyPaperRoot = HistoryPath.ResolveLegacyHistoryRoot("Sim:Paper:All:Default", ensureExists: false);
            var legacyLiveRoot = HistoryPath.ResolveLegacyHistoryRoot("Real", ensureExists: false);

            var roots = new List<string>();
            var paperHasNew = HasTrades(paperRoot);
            var liveHasNew = HasTrades(liveRoot);

            if (paperHasNew) roots.Add(paperRoot);
            else roots.Add(legacyPaperRoot);

            if (liveHasNew) roots.Add(liveRoot);
            else roots.Add(legacyLiveRoot);

            foreach (var root in roots.Where(Directory.Exists))
            {
                var files = Directory.GetFiles(root, "trades_*.jsonl")
                                     .OrderByDescending(f => f);

                foreach (var file in files)
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var trade = JsonSerializer.Deserialize<CompletedTrade>(line, _jsonOptions);
                            if (trade != null)
                            {
                                var acct = ResolveAccountKey(trade);
                                var bag = _trades.GetOrAdd(acct, _ => new ConcurrentBag<CompletedTrade>());
                                bag.Add(trade);

                                loaded++;
                                if (loaded >= max) return;
                            }
                        }
                        catch
                        {
                            // Skip malformed lines
                        }
                    }
                }
            }
        }

        private string GetTradeFilePath(string account, DateTime date)
        {
            var safeAccount = HistoryPath.SanitizeFileName(account);
            var root = HistoryPath.ResolveHistoryRoot(account, ensureExists: true);
            return Path.Combine(root, $"trades_{safeAccount}_{date:yyyyMMdd}.jsonl");
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

        private static bool HasTrades(string root)
        {
            if (!Directory.Exists(root)) return false;
            return Directory.GetFiles(root, "trades_*.jsonl").Length > 0;
        }
    }
}

