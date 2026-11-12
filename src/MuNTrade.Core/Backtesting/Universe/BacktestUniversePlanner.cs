using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Time;

namespace MuNTrade.Core.Backtesting.Universe
{
    public sealed class SymbolUsageInfo
    {
        public DateOnly FirstSeen { get; set; }
        public DateOnly LastSeen { get; set; }
        public Dictionary<DateOnly, DateOnly?> NextUseByDate { get; } = new();
    }

    public sealed class BacktestUniversePlan
    {
        public List<DateOnly> TradingDates { get; } = new();
        public List<UniversePlanningDayResult> DayResults { get; } = new();
        public Dictionary<DateOnly, string[]> SymbolsByDate { get; } = new();
        public Dictionary<DateOnly, UniversePlanningTrace> PlanningTraceByDate { get; } = new();
        public List<UniversePlanningDayResult> PlanningIssues { get; } = new();
        public Dictionary<string, SymbolUsageInfo> UsageBySymbol { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public int TotalTradingDays => TradingDates.Count;
        public int ExecutableTradingDays => SymbolsByDate.Count;
    }

    public sealed record UniversePlanningTrace
    {
        public required string UniverseMode { get; init; }
        public DateOnly? RankingSnapshotDate { get; init; }
        public string? RankingSnapshotPath { get; init; }
    }

    public sealed record UniversePlanningDayResult
    {
        public required DateOnly TradeDate { get; init; }
        public required bool IsSuccessful { get; init; }
        public IReadOnlyList<string> Symbols { get; init; } = Array.Empty<string>();
        public DateOnly? RankingSnapshotDate { get; init; }
        public string? RankingSnapshotPath { get; init; }
        public string? MissingReason { get; init; }
        public string Severity { get; init; } = "none";
        public string? Notes { get; init; }
        public UniversePlanningTrace Trace => new()
        {
            UniverseMode = UniverseMode,
            RankingSnapshotDate = RankingSnapshotDate,
            RankingSnapshotPath = RankingSnapshotPath
        };
        public required string UniverseMode { get; init; }
    }

    public interface IBacktestUniversePlanner
    {
        Task<BacktestUniversePlan> BuildPlanAsync(
            BacktestRunRequest request,
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken ct = default);
    }

    public sealed class BacktestUniversePlanner : IBacktestUniversePlanner
    {
        private readonly ILogger<BacktestUniversePlanner> _logger;
        private readonly ITradingSessionProvider _sessionProvider;
        private readonly ITurnoverSymbolSelector _turnoverSelector;

        public BacktestUniversePlanner(
            ILogger<BacktestUniversePlanner> logger,
            ITradingSessionProvider sessionProvider,
            ITurnoverSymbolSelector turnoverSelector)
        {
            _logger = logger;
            _sessionProvider = sessionProvider;
            _turnoverSelector = turnoverSelector;
        }

        public async Task<BacktestUniversePlan> BuildPlanAsync(
            BacktestRunRequest request,
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken ct = default)
        {
            _logger.LogInformation(
                "[UniversePlanner] WARMUP_BEGIN: Scanning universe plan from {Start} to {End}...",
                startDate, endDate);

            var plan = new BacktestUniversePlan();
            var dates = GetTradingDates(startDate, endDate);

            if (dates.Count == 0)
            {
                _logger.LogWarning(
                    "[UniversePlanner] No valid trading dates found between {Start} and {End}.",
                    startDate, endDate);
                return plan;
            }

            foreach (var date in dates)
            {
                plan.TradingDates.Add(date);
                var resolution = await ResolveSymbolsForDateAsync(date, request, ct).ConfigureAwait(false);
                plan.DayResults.Add(resolution);
                plan.PlanningTraceByDate[date] = resolution.Trace;

                if (!resolution.IsSuccessful)
                {
                    plan.PlanningIssues.Add(resolution);
                    continue;
                }

                var symbols = resolution.Symbols.Where(s => !string.IsNullOrWhiteSpace(s))
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToArray();

                plan.SymbolsByDate[date] = symbols;

                foreach (var sym in symbols)
                {
                    if (!plan.UsageBySymbol.TryGetValue(sym, out var info))
                    {
                        info = new SymbolUsageInfo
                        {
                            FirstSeen = date,
                            LastSeen = date
                        };
                        plan.UsageBySymbol[sym] = info;
                    }
                    else
                    {
                        if (date < info.FirstSeen) info.FirstSeen = date;
                        if (date > info.LastSeen) info.LastSeen = date;
                    }
                }
            }

            foreach (var (sym, info) in plan.UsageBySymbol)
            {
                var activeDates = plan.SymbolsByDate
                    .Where(kvp => kvp.Value.Contains(sym, StringComparer.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Key)
                    .OrderBy(d => d)
                    .ToList();

                for (var i = 0; i < activeDates.Count; i++)
                {
                    var current = activeDates[i];
                    DateOnly? next = (i + 1 < activeDates.Count) ? activeDates[i + 1] : null;
                    info.NextUseByDate[current] = next;
                }
            }

            _logger.LogInformation(
                "[UniversePlanner] UNIVERSE_PLAN Build Complete. Total {Days} trading days, {ExecutableDays} executable days, {Count} unique symbols.",
                plan.TotalTradingDays, plan.ExecutableTradingDays, plan.UsageBySymbol.Count);

            return plan;
        }

        private List<DateOnly> GetTradingDates(DateOnly start, DateOnly end)
        {
            var list = new List<DateOnly>();
            var current = start;

            while (current <= end)
            {
                var session = _sessionProvider.GetSession("TX00", current.ToDateTime(new TimeOnly(12, 0)));
                if (session != null)
                    list.Add(current);

                current = current.AddDays(1);
            }

            return list;
        }

        private async Task<UniversePlanningDayResult> ResolveSymbolsForDateAsync(
            DateOnly date,
            BacktestRunRequest request,
            CancellationToken ct)
        {
            var mode = request.UniverseMode ?? "SingleSymbol";

            if (string.Equals(mode, "SingleSymbol", StringComparison.OrdinalIgnoreCase))
            {
                return new UniversePlanningDayResult
                {
                    TradeDate = date,
                    IsSuccessful = true,
                    Symbols = !string.IsNullOrWhiteSpace(request.Symbol) ? new[] { request.Symbol.Trim() } : Array.Empty<string>(),
                    UniverseMode = mode
                };
            }

            if (string.Equals(mode, "TurnoverTopN", StringComparison.OrdinalIgnoreCase))
            {
                var snapshot = await _turnoverSelector.GetSnapshotAsync(date, request.TurnoverTopN, ct).ConfigureAwait(false);
                if (!snapshot.SnapshotFound)
                {
                    _logger.LogWarning(
                        "[UniversePlanner] Missing turnover snapshot for target_trade_date={TradeDate} snapshot_date={SnapshotDate}. Marking the day as skipped.",
                        date,
                        snapshot.RankingSnapshotDate);
                    return new UniversePlanningDayResult
                    {
                        TradeDate = date,
                        IsSuccessful = false,
                        UniverseMode = mode,
                        RankingSnapshotDate = snapshot.RankingSnapshotDate,
                        RankingSnapshotPath = snapshot.SnapshotPath,
                        MissingReason = "MissingPreviousTradingDayTurnoverSnapshot",
                        Severity = "missing_data",
                        Notes = $"target_trade_date={date:yyyy-MM-dd}; expected_snapshot={snapshot.RankingSnapshotDate:yyyy-MM-dd}"
                    };
                }

                _logger.LogInformation(
                    "[UniversePlanner] target_trade_date={TradeDate} uses ranking_snapshot_date={SnapshotDate} for TurnoverTopN universe planning.",
                    date,
                    snapshot.RankingSnapshotDate);
                return new UniversePlanningDayResult
                {
                    TradeDate = date,
                    IsSuccessful = true,
                    UniverseMode = mode,
                    RankingSnapshotDate = snapshot.RankingSnapshotDate,
                    RankingSnapshotPath = snapshot.SnapshotPath,
                    Symbols = snapshot.Symbols.ToArray()
                };
            }

            if (string.Equals(mode, "ManualList", StringComparison.OrdinalIgnoreCase))
            {
                return new UniversePlanningDayResult
                {
                    TradeDate = date,
                    IsSuccessful = true,
                    UniverseMode = mode,
                    Symbols = request.ManualList != null ? request.ManualList.ToArray() : Array.Empty<string>()
                };
            }

            return new UniversePlanningDayResult
            {
                TradeDate = date,
                IsSuccessful = true,
                UniverseMode = mode,
                Symbols = Array.Empty<string>()
            };
        }
    }
}

