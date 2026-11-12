using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.Enums;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MuNTrade.Abstractions.Contracts.Hubs;

namespace MuNTrade.Core.Services
{
    public class TradeLoggerService
    {
        #region 內部輔助類別
        private class OpenPositionState
        {
            // ▼▼▼ 成本計算規則常數 (維持不變) ▼▼▼
            private const decimal BROKER_FEE_RATE = 0.001425m;
            private const decimal BROKER_FEE_DISCOUNT = 0.25m;
            private const decimal MIN_BROKER_FEE = 20m;
            private const decimal DAY_TRADE_TAX_RATE = 0.0015m;
            private const decimal NORMAL_TAX_RATE = 0.003m;

            public OrderSide Direction { get; private set; }
            public long TotalQuantity { get; private set; }
            public decimal AveragePrice { get; private set; }
            public DateTime FirstEntryTime { get; }
            public string FirstEntryReason { get; }
            public string StrategyId { get; }
            public string AccountKey { get; }
            public string EntryCorrelationId { get; }

            public OpenPositionState(OrderFillEvent firstFill, long? remainingQty = null)
            {
                // OrderFillEvent provides the trade direction, so SignalAction is not needed here.
                this.Direction = firstFill.Side;

                this.TotalQuantity = remainingQty ?? firstFill.FillQuantity;
                this.AveragePrice = firstFill.FillPrice;
                this.FirstEntryTime = firstFill.FillTime;

                this.FirstEntryReason = firstFill.OriginalReason ?? string.Empty;

                // Track trade attribution.
                this.StrategyId = firstFill.StrategyId ?? "";
                this.AccountKey = firstFill.AccountKey ?? "";
                this.EntryCorrelationId = firstFill.CorrelationId ?? "";
            }

            public void AddFill(OrderFillEvent fill)
            {
                // 如果成交方向 (fill.Side) 與目前倉位方向 (this.Direction) 相同，就是加碼。
                bool isCorrectScaleIn = (this.Direction == fill.Side);

                if (!isCorrectScaleIn) return;

                var existingValue = AveragePrice * TotalQuantity;
                var newValue = fill.FillPrice * fill.FillQuantity;
                TotalQuantity += fill.FillQuantity;
                if (TotalQuantity != 0)
                {
                    AveragePrice = (existingValue + newValue) / TotalQuantity;
                }
            }

            private decimal CalculateBrokerFee(decimal transactionValue)
            {
                decimal fee = transactionValue * BROKER_FEE_RATE * BROKER_FEE_DISCOUNT;
                decimal roundedFee = Math.Round(fee, MidpointRounding.AwayFromZero);
                return Math.Max(MIN_BROKER_FEE, roundedFee);
            }

            public CompletedTrade CloseWith(OrderFillEvent fill, bool partial = false)
            {
                long closingQty = partial ? this.TotalQuantity : fill.FillQuantity;
                const int sharesPerLot = 1000;
                long closingShares = closingQty * sharesPerLot;

                // PnL 計算邏輯維持不變，因為它本來就是基於 Direction (OrderSide)
                decimal entryValue, exitValue;
                if (this.Direction == OrderSide.Buy)
                {
                    entryValue = this.AveragePrice * closingShares;
                    exitValue = fill.FillPrice * closingShares;
                }
                else
                {
                    entryValue = fill.FillPrice * closingShares;
                    exitValue = this.AveragePrice * closingShares;
                }

                decimal buyCommission = CalculateBrokerFee(entryValue);
                decimal sellCommission = CalculateBrokerFee(exitValue);
                decimal totalCommission = buyCommission + sellCommission;
                bool isDayTrade = this.FirstEntryTime.Date == fill.FillTime.Date;
                var taxRate = isDayTrade ? DAY_TRADE_TAX_RATE : NORMAL_TAX_RATE;
                decimal transactionTax = Math.Round(exitValue * taxRate, MidpointRounding.AwayFromZero);

                decimal grossPnL = exitValue - entryValue;
                decimal netPnL = grossPnL - totalCommission - transactionTax;

                return new CompletedTrade
                {
                    StockId = fill.StockId,
                    Direction = this.Direction,
                    Quantity = this.TotalQuantity,
                    EntryPrice = this.AveragePrice,
                    ExitPrice = fill.FillPrice,
                    EntryTime = this.FirstEntryTime,
                    ExitTime = fill.FillTime,
                    EntryReason = this.FirstEntryReason,
                    ExitReason = fill.OriginalReason ?? string.Empty,
                    Commission = totalCommission,
                    Tax = transactionTax,
                    GrossPnL = grossPnL,
                    NetPnL = netPnL,
                    // Preserve trade attribution.
                    StrategyId = this.StrategyId,
                    AccountKey = this.AccountKey,
                    EntryCorrelationId = this.EntryCorrelationId,
                    ExitCorrelationId = fill.CorrelationId,
                    AttributionConflict = !string.IsNullOrEmpty(fill.StrategyId) && fill.StrategyId != "Unknown" && !string.Equals(this.StrategyId, fill.StrategyId)
                };
            }
        }
        #endregion

        private readonly ILogger<TradeLoggerService> _logger;
        private readonly IEventBus _bus;
        private readonly ConcurrentDictionary<string, OpenPositionState> _openTrades = new();
        public List<CompletedTrade> Trades { get; } = new List<CompletedTrade>();
        private readonly List<decimal> _equityCurve = new List<decimal> { 1000000m };
        private readonly object _syncRoot = new object();

        public TradeLoggerService(ILogger<TradeLoggerService> logger, IEventBus bus)
        {
            _logger = logger;
            _bus = bus;
        }

        public void RecordFill(OrderFillEvent fill)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrEmpty(fill.StockId)) return;

                // 不再需要 switch(fill.Action)，直接根據 Side 判斷。
                // Buy 為正，Sell 為負。
                long signedDelta = (fill.Side == OrderSide.Buy) ? fill.FillQuantity : -fill.FillQuantity;

                if (_openTrades.TryGetValue(fill.StockId, out var pos))
                {
                    long signedPos = (pos.Direction == OrderSide.Buy ? 1 : -1) * pos.TotalQuantity;
                    long signedNew = signedPos + signedDelta;

                    if (signedNew != 0 && Math.Sign(signedNew) == Math.Sign(signedPos)) // 加碼
                    {
                        pos.AddFill(fill);
                        _logger.LogInformation("[TradeLogger] 加碼 {StockId}, 新數量={Qty}", fill.StockId, pos.TotalQuantity);
                    }
                    else if (signedNew == 0) // 平倉
                    {
                        var trade = pos.CloseWith(fill);
                        Trades.Add(trade);
                        _equityCurve.Add(_equityCurve.Last() + trade.NetPnL);
                        _openTrades.TryRemove(fill.StockId, out _);
                        _logger.LogWarning("[TradeLogger] 平倉 {StockId}, 淨損益={Pnl:F2}", trade.StockId, trade.NetPnL);
                    }
                    else // 反手
                    {
                        var trade = pos.CloseWith(fill, partial: true);
                        Trades.Add(trade);
                        _equityCurve.Add(_equityCurve.Last() + trade.NetPnL);

                        var newPos = new OpenPositionState(fill, remainingQty: Math.Abs(signedNew));
                        _openTrades[fill.StockId] = newPos;
                        _logger.LogInformation("[TradeLogger] 反手 {StockId} -> 新方向={Dir}, 新數量={Qty}", fill.StockId, newPos.Direction, newPos.TotalQuantity);
                    }
                }
                else // 開新倉
                {
                    // 如果沒有現有倉位，任何成交都視為開新倉，不再需要檢查 Action。
                    var newPos = new OpenPositionState(fill);
                    _openTrades.TryAdd(fill.StockId, newPos);
                    _logger.LogInformation("[TradeLogger] 開新倉 {StockId}, 方向={Dir}, 數量={Qty}", fill.StockId, newPos.Direction, newPos.TotalQuantity);
                }
            }
        }

        public BacktestPerformance CalculatePerformance()
        {
            lock (_syncRoot)
            {
                var netPnlSum = Trades.Sum(t => t.NetPnL);
                var grossPnlSum = Trades.Sum(t => t.GrossPnL);

                if (Trades.Count < 2)
                {
                    return new BacktestPerformance(
                        trades: Trades.Count,
                        netPnL: netPnlSum,
                        grossPnL: grossPnlSum,
                        maxDrawDown: 0m,
                        sharpRatio: 0,
                        winRate: Trades.Any(t => t.NetPnL > 0) ? 1 : 0
                    );
                }

                var pnlDoubles = Trades.Select(t => (double)t.NetPnL).ToArray();
                int wins = pnlDoubles.Count(p => p > 0);
                double winRate = (double)wins / Trades.Count;

                decimal maxDrawdownPct = 0m;
                decimal peak = _equityCurve[0];
                decimal maxDdValue = 0m;
                foreach (var eq in _equityCurve)
                {
                    peak = Math.Max(peak, eq);
                    maxDdValue = Math.Max(maxDdValue, peak - eq);
                }
                decimal highest = _equityCurve.Max();
                if (highest > 0) maxDrawdownPct = maxDdValue / highest;

                double sharpRatio = 0;
                double avg = pnlDoubles.Average();
                double ss = pnlDoubles.Select(v => (v - avg) * (v - avg)).Sum();
                double sd = Math.Sqrt(ss / (pnlDoubles.Length - 1));
                if (sd > 0) sharpRatio = avg / sd;

                return new BacktestPerformance(
                    trades: Trades.Count,
                    netPnL: netPnlSum,
                    grossPnL: grossPnlSum,
                    maxDrawDown: maxDrawdownPct,
                    sharpRatio: sharpRatio,
                    winRate: winRate
                );
            }
        }

        public void GenerateReport()
        {
            _logger.LogCritical("--- Backtest Performance Report ---");
            var perf = CalculatePerformance();
            if (perf.Trades == 0)
            {
                _logger.LogInformation("No trades were executed.");
                _logger.LogCritical("---------------------------------");
                return;
            }
            _logger.LogInformation("Total Trades      : {Count}", perf.Trades);
            _logger.LogInformation("Total Gross Profit: {GrossPnL:F2}", perf.GrossPnL);
            _logger.LogInformation("Total Net Profit  : {NetPnL:F2}", perf.NetPnL);
            _logger.LogInformation("Max Drawdown      : {MaxDrawDown:P2}", perf.MaxDrawDown);
            _logger.LogInformation("Sharpe Ratio      : {SharpRatio:F2}", perf.SharpRatio);
            _logger.LogInformation("Win Rate          : {WinRate:P2}", perf.WinRate);
            _logger.LogCritical("---------------------------------");
        }
    }
}

