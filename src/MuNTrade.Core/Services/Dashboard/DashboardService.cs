using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Core.Services.EquityCurve;
using MuNTrade.Core.Trading.Gateways;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;


namespace MuNTrade.Core.Services.Dashboard
{
    public interface IDashboardService
    {
        HudSummaryDto GetHud();
        IReadOnlyList<PositionDto> GetPositions();
    }

    public sealed class DashboardService : IDashboardService
    {
        private readonly ILogger<DashboardService> _logger;
        private readonly IEventBus _bus;
        private readonly IOptions<ApplicationSettings> _app;
        private readonly IOptions<DayTradingRules> _rules;
        private readonly IEquityCurveService _equity;
        private readonly IClock _clock;
        private readonly IPositionsSnapshotProvider _posSnapshot;
        private readonly IOrderGateway _orderGateway;

        private readonly ConcurrentQueue<BrokerReportDto> _reports = new();

        public DashboardService(
            ILogger<DashboardService> logger,
            IEventBus bus,
            IOptions<ApplicationSettings> app,
            IOptions<DayTradingRules> rules,
            IEquityCurveService equity,
            IClock clock,
            IPositionsSnapshotProvider posSnapshot,
            IOrderGateway orderGateway)
        {
            _logger = logger;
            _bus = bus;
            _app = app;
            _rules = rules;
            _equity = equity;
            _clock = clock;
            _posSnapshot = posSnapshot;
            _orderGateway = orderGateway;

            _bus.Subscribe<BrokerReportDto>(OnReport);
        }

        private void OnReport(BrokerReportDto r)
        {
            _reports.Enqueue(r);
            // 以 IClock.Now 作為窗口基準，避免系統時間漂移
            while (_reports.TryPeek(out var head) &&
                   (_clock.Now - head.ArrivalTs.Value).TotalMinutes > 15)
            {
                _reports.TryDequeue(out _);
            }
        }

        public HudSummaryDto GetHud()
        {
            var positions = _posSnapshot.GetAll();

            // === 估算數值（你原本就有） ===
            var unreal = positions.Sum(p => p.UnrealizedPnL);
            var realized = 0m;
            var costBasis = positions.Sum(p => p.AverageCost * p.Quantity);
            var equity = costBasis + unreal;
            _equity.UpdateEquity(equity);

            // ✅ 只看實際 Gateway 來決定 HUD 的帳戶類型
            var exec = _orderGateway is PaperTradingGateway
                ? "PAPER"
                : "LIVE";

            return new HudSummaryDto
            {
                TradingMode = exec,
                TradingDate = _clock.Now.Date,       // 以 IClock 提供的日期
                RealizedPnL = realized,
                UnrealizedPnL = unreal,
                Equity = equity,
                MaxDrawdown = _equity.MaxDrawdownToday,
                MaxDrawdownPct = _equity.MaxDrawdownPctToday,
                BuyingPowerUtilStock = equity > 0m
                    ? positions.Sum(p => Math.Abs(p.MarketValue)) / equity
                    : 0m,
                BuyingPowerUtilFutures = 0m,
                TradesUsed = CountTradesUsedToday(),
                TradesLimit = _rules.Value.DailyTradeLimit,
                SymbolsActive = positions.Select(p => p.Symbol)
                                         .Where(s => !string.IsNullOrWhiteSpace(s))
                                         .Distinct(StringComparer.OrdinalIgnoreCase)
                                         .Count(),
                Exec = ComputeExecStats()
            };
        }

        public IReadOnlyList<PositionDto> GetPositions() => _posSnapshot.GetAll();

        private int CountTradesUsedToday()
        {
            var today = DateOnly.FromDateTime(_clock.Now);
            return _reports.Count(r =>
                DateOnly.FromDateTime(r.ExchangeTs.Value) == today &&
                string.Equals(r.ReportType, "Filled", StringComparison.OrdinalIgnoreCase));
        }

        private static int PInt(int pct, int[] arr)
        {
            if (arr == null || arr.Length == 0) return 0;
            if (pct <= 0) return arr[0];
            if (pct >= 100) return arr[^1];
            var rank = (int)Math.Ceiling(pct / 100.0 * arr.Length) - 1;
            if (rank < 0) rank = 0;
            if (rank >= arr.Length) rank = arr.Length - 1;
            return arr[rank];
        }

        private static decimal PDec(int pct, decimal[] arr)
        {
            if (arr == null || arr.Length == 0) return 0m;
            if (pct <= 0) return arr[0];
            if (pct >= 100) return arr[^1];
            var rank = (int)Math.Ceiling(pct / 100.0 * arr.Length) - 1;
            if (rank < 0) rank = 0;
            if (rank >= arr.Length) rank = arr.Length - 1;
            return arr[rank];
        }

        private LatencySlippageStats ComputeExecStats()
        {
            var today = DateOnly.FromDateTime(_clock.Now);
            var window = _reports.Where(r => DateOnly.FromDateTime(r.ExchangeTs.Value) == today).ToArray();

            var lat = window.Where(r => r.LatencyMs.HasValue)
                            .Select(r => r.LatencyMs!.Value)
                            .OrderBy(x => x)
                            .ToArray();

            var slip = window.Where(r => r.Slippage.HasValue)
                             .Select(r => r.Slippage!.Value)
                             .OrderBy(x => x)
                             .ToArray();

            return new LatencySlippageStats
            {
                P50LatencyMs = PInt(50, lat),
                P95LatencyMs = PInt(95, lat),
                P50Slippage = PDec(50, slip),
                P95Slippage = PDec(95, slip)
            };
        }

        // ======= 新增的小工具方法們（無外部依賴，安全） =======

        // 行情來源：你目前是實盤來源，先固定 LIVE（之後要讀設定再改這裡即可）
        private static string DetermineFeedMode() => "LIVE";

        // 時鐘來源：型別名包含 "Broker" 視為 BROKER，否則 SIM
        private string DetermineClockMode()
        {
            var typeName = _clock.GetType().Name;
            return typeName.IndexOf("Broker", StringComparison.OrdinalIgnoreCase) >= 0
                ? "BROKER"
                : "SIM";
        }
    }
}

