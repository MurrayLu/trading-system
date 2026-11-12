using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Contracts.Market;
using MuNTrade.Abstractions.Contracts.Capital;
using MuNTrade.Core.Trading.Adapters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Core.Services;
using MuNTrade.Abstractions.Contracts.Time;
using Microsoft.Extensions.DependencyInjection; // For IServiceProvider
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Events.Strategy;

namespace MuNTrade.Core.Services
{
    /// <summary>
    /// 負責：接收訊號 -> 計算差異 -> 呼叫風控 -> 呼叫執行 -> 更新倉位。
    /// </summary>
    public class PositionManagerService : IPositionManagerService
    {
        private readonly ILogger<PositionManagerService> _logger;
        private readonly IEventBus _eventBus;
        private readonly ICapitalManager _capitalManager;

        private readonly IRiskManagerService _riskManager;
        private readonly IMarketInfoProvider _marketInfoProvider;
        private readonly IOrderExecutionService _orderExecutionService;
        private readonly IExecutionTraceRegistry _traceRegistry;
        private readonly ITradeRulesProvider _tradeRulesProvider;
        private readonly IClock _clock;
        private readonly IStrategyHost? _strategyHost;

        private readonly ConcurrentDictionary<string, Position> _positions = new();
        private readonly ConcurrentDictionary<string, SignalEvent> _lastEntrySignal = new();

        public PositionManagerService(
            ILogger<PositionManagerService> logger,
            IEventBus eventBus,
            ICapitalManager capitalManager,
            IRiskManagerService riskManager,
            IOrderExecutionService orderExecutionService,
            IExecutionTraceRegistry traceRegistry,
            ITradeRulesProvider tradeRulesProvider,
            IClock clock,
            IServiceProvider serviceProvider,
            IMarketInfoProvider marketInfoProvider) // Inject
        {
            _logger = logger;
            _eventBus = eventBus;
            _capitalManager = capitalManager;
            _riskManager = riskManager;
            _orderExecutionService = orderExecutionService;
            _traceRegistry = traceRegistry;
            _tradeRulesProvider = tradeRulesProvider;
            _clock = clock;
            _marketInfoProvider = marketInfoProvider;

            // Resolve StrategyHost optionally (it might not exist in Backtest environment)
            _strategyHost = serviceProvider.GetService<IStrategyHost>();

            _eventBus.Subscribe<SignalEvent>(OnSignalReceived);
            _eventBus.Subscribe<OrderFillEvent>(OnOrderFilled);
        }

        #region --- IPositionManagerService 介面實作 ---

        public void ProcessSignal(SignalEvent signal) => OnSignalReceived(signal);

        public void UpdatePositionOnFill(OrderFillEvent fill) => OnOrderFilled(fill);

        public Position GetCurrentPosition(string stockId)
        {
            // Returns the stored position or an empty position for this symbol.
            if (_positions.TryGetValue(stockId, out var pos)) return pos;
            return new Position { StockId = stockId };
        }

        public IEnumerable<Position> GetAllOpenPositions() =>
            _positions.Values.Where(p => p.Quantity != 0).ToList();

        #endregion

        #region --- 私有核心邏輯 (Private Core Logic) ---

        private async void OnSignalReceived(SignalEvent signal)
        {
            if (string.IsNullOrEmpty(signal.StockId)) return;

            // [HARD GATE] Validate Attribution and Strategy State
            bool hasStrategyId = !string.IsNullOrEmpty(signal.StrategyId);

            // 1. Attribution Check
            if (!hasStrategyId)
            {
                // We only warn here if it *looks* like a strategy signal but missing ID.
                // For safety in this strict mode: Reject if missing logic attribution in a managed context.
                if (signal.Reason != "Manual")
                {
                    _logger.LogWarning("[Gate] Reject signal from non-manual source with missing StrategyId. StockId={StockId}", signal.StockId);
                    return;
                }
            }

            // 2. State Check (Only if StrategyId is present and Host is available)
            if (hasStrategyId && _strategyHost != null)
            {
                // signal.StrategyId is likely 'Template#Instance' or just GUID.
                var targetId = signal.StrategyId;
                var statuses = _strategyHost.GetInstanceStatuses();

                // Flexible match: Exact match OR Composite match
                var match = statuses.FirstOrDefault(s =>
                    s.InstanceId.Value == targetId ||
                    $"{s.TemplateId}#{s.InstanceId}" == targetId);

                if (match != null)
                {
                    // [STRICT GATE] Check Runtime Lifecycle State
                    if (match.RuntimeState != StrategyRuntimeState.Running)
                    {
                        _logger.LogWarning("[Gate] Reject signal: Strategy {Strat} is {State}. (Stock={Stock})", targetId, match.RuntimeState, signal.StockId);
                        return;
                    }
                }
                else
                {
                    // If ID looks like a composite ID (contains #) but not found in Host -> It effectively doesn't exist/Stopped.
                    if (targetId!.Contains("#"))
                    {
                        _logger.LogWarning("[Gate] Reject signal: Strategy {Strat} not found in Host. (Stock={Stock})", targetId, signal.StockId);
                        return;
                    }
                }
            }

            var currentPosition = this.GetCurrentPosition(signal.StockId);
            long currentQuantity = currentPosition.Quantity;

            long maxQuantity = await _capitalManager.GetMaxPositionSizeAsync(signal.StockId, signal.Price);
            long targetQuantity = (long)Math.Floor(maxQuantity * signal.TargetPositionRatio);
            long tradeQuantity = targetQuantity - currentQuantity;

            if (tradeQuantity == 0) return;

            // Create the execution trace.
            var stratId = signal.StrategyId ?? "Unknown";
            var accKey = signal.AccountKey ?? "Live:SKCOM:Unknown:Unknown";
            var trace = new ExecutionTrace
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
                IntentTimestamp = _clock.Now,
                DecisionPrice = signal.Price,
                StrategyId = stratId,
                AccountKey = accKey,
                Origin = "Strategy",
                Symbol = signal.StockId,
                Side = tradeQuantity > 0 ? OrderSide.Buy : OrderSide.Sell,
                Quantity = Math.Abs(tradeQuantity)
            };

            _traceRegistry.Put(trace);

            var marketInfo = _marketInfoProvider.GetMarketInfoOrThrow(signal.StockId, MarketInfoPurpose.Execution);
            var marketKind = marketInfo.IsDerivatives ? MarketKind.Derivatives : MarketKind.Securities;

            var orderRequest = new OrderRequest
            {
                OriginalSignal = signal,
                Side = tradeQuantity > 0 ? OrderSide.Buy : OrderSide.Sell,
                Quantity = Math.Abs(tradeQuantity),
                StrategyId = stratId,
                AccountKey = accKey,
                CorrelationId = trace.CorrelationId,
                Trace = trace,
                MarketKind = marketKind
            };

            _logger.LogInformation(
                "[PositionManager] Signal received for {StockId}. Current: {currentQty}, Target: {targetQty}, Delta: {Side} {absQty}. Strat={StratId}",
                signal.StockId, currentQuantity, targetQuantity, orderRequest.Side, orderRequest.Quantity, stratId);

            if (_riskManager.AssessOrder(orderRequest, currentPosition, out var finalOrder) && finalOrder != null)
            {
                _logger.LogInformation("[PositionManager] Order for {StockId} passed risk assessment. Sending to execution.", signal.StockId);
                _orderExecutionService.ExecuteOrder(finalOrder);
            }
            else
            {
                _logger.LogWarning("[PositionManager] Order for {StockId} was rejected by RiskManager.", signal.StockId);
            }

            if (currentQuantity == 0 && targetQuantity != 0)
            {
                _lastEntrySignal[signal.StockId] = signal;
            }
        }

        private void OnOrderFilled(OrderFillEvent fill)
        {
            if (string.IsNullOrEmpty(fill.StockId)) return;

            var position = GetCurrentPosition(fill.StockId);
            long currentQuantity = position.Quantity;
            decimal currentAvgPrice = position.AveragePrice;

            long signedFillQuantity = fill.Side == OrderSide.Buy ? fill.FillQuantity : -fill.FillQuantity;
            long newQuantity = currentQuantity + signedFillQuantity;

            // [SOP-Strict] Determine type via Provider
            bool isDerivatives = _marketInfoProvider.GetMarketInfoOrThrow(fill.StockId, MarketInfoPurpose.Execution).IsDerivatives;

            if (newQuantity == 0)
            {
                if (_positions.TryRemove(fill.StockId, out _))
                {
                    _lastEntrySignal.TryGetValue(fill.StockId, out var entrySignal);

                    long absQty = Math.Abs(position.Quantity);
                    bool wasLong = position.Quantity > 0;

                    decimal entryFee = EstimateOneSideCost(fill.StockId, position.AveragePrice, absQty, true, wasLong);
                    decimal exitFee = EstimateOneSideCost(fill.StockId, fill.FillPrice, absQty, false, wasLong);

                    decimal tax = 0m;
                    var rules = _tradeRulesProvider.Current;

                    if (!isDerivatives) // Stock
                    {
                        decimal multiplier = 1000m;
                        decimal exitValue = fill.FillPrice * absQty * multiplier;
                        bool isDayTrade = position.EntryTime != DateTime.MinValue &&
                                          position.EntryTime.Date == fill.FillTime.Date;
                        var taxRate = isDayTrade ? (rules.Stock?.DayTradeTaxRate ?? 0.0015m) : (rules.Stock?.TaxRate ?? 0.003m);
                        tax = Math.Floor(exitValue * taxRate);
                    }

                    decimal multiplierCalc = 1.0m;
                    if (!isDerivatives)
                    {
                        multiplierCalc = 1000m;
                    }
                    else
                    {
                        var f = ResolveFutureSpec(fill.StockId, rules);
                        if (f != null)
                        {
                            multiplierCalc = f.PointValue;
                        }
                        else
                        {
                            multiplierCalc = 200m; // Default
                        }
                    }

                    decimal rawDiff = (fill.FillPrice - position.AveragePrice) * (wasLong ? 1 : -1);
                    decimal grossPnL = rawDiff * absQty * multiplierCalc;
                    decimal netPnL = grossPnL - entryFee - exitFee - tax;

                    var trade = new CompletedTrade
                    {
                        StockId = fill.StockId,
                        EntryPrice = position.AveragePrice,
                        ExitPrice = fill.FillPrice,
                        EntryTime = position.EntryTime,
                        ExitTime = fill.FillTime,
                        Direction = position.Quantity > 0 ? OrderSide.Buy : OrderSide.Sell,
                        Quantity = Math.Abs(position.Quantity),
                        EntryReason = entrySignal?.Reason ?? "N/A",
                        ExitReason = fill.OriginalReason ?? "Position Closed",
                        Commission = entryFee + exitFee,
                        Tax = tax,
                        GrossPnL = grossPnL,
                        NetPnL = netPnL,
                        Account = fill.Account ?? "Real",
                        StrategyId = fill.StrategyId ?? position.StrategyId, // Propagate attribution
                        AccountKey = fill.AccountKey ?? position.AccountKey
                    };
                    _eventBus.Publish(trade);
                    _lastEntrySignal.TryRemove(fill.StockId, out _);

                    _logger.LogWarning("[PositionManager] Position closed for {StockId}. NetPnL={NetPnL}, Fee={Fee}, Tax={Tax}", fill.StockId, netPnL, entryFee + exitFee, tax);
                }
            }
            else
            {
                decimal newAvgPrice;
                DateTime newEntryTime = position.EntryTime == DateTime.MinValue ? fill.FillTime : position.EntryTime;

                var sameDirectionFill = currentQuantity == 0 || Math.Sign(currentQuantity) == Math.Sign(signedFillQuantity);
                if (sameDirectionFill)
                {
                    // Increase position in the same direction (or open from flat): weighted average.
                    newAvgPrice = ((currentAvgPrice * Math.Abs(currentQuantity)) + (fill.FillPrice * fill.FillQuantity))
                                  / Math.Abs(newQuantity);
                    if (currentQuantity == 0)
                        newEntryTime = fill.FillTime;
                }
                else
                {
                    // Opposite-side fill: either reduce existing position or flip direction.
                    var fillAbs = Math.Abs(signedFillQuantity);
                    var posAbs = Math.Abs(currentQuantity);

                    if (fillAbs < posAbs)
                    {
                        // Partial close: remaining lot keeps original average cost.
                        newAvgPrice = currentAvgPrice;
                    }
                    else
                    {
                        // Flip direction (newQuantity already computed and non-zero here):
                        // remaining position is newly opened at fill price.
                        newAvgPrice = fill.FillPrice;
                        newEntryTime = fill.FillTime;
                    }
                }

                var updatedPosition = new Position
                {
                    StockId = fill.StockId,
                    Quantity = newQuantity,
                    AveragePrice = newAvgPrice,
                    EntryTime = newEntryTime,
                    LastPrice = fill.FillPrice,
                    // Persist Attribution
                    StrategyId = fill.StrategyId ?? position.StrategyId,
                    AccountKey = fill.AccountKey ?? position.AccountKey
                };
                _positions[fill.StockId] = updatedPosition;

                _logger.LogInformation("[PositionManager] Position updated for {StockId}: New Qty={newQty}, New AvgPrice={newAvgPrice:F2}",
                    fill.StockId, newQuantity, newAvgPrice);
            }
        }

        private decimal EstimateOneSideCost(string symbol, decimal price, long qty, bool isEntry, bool isLong)
        {
            decimal value = price * Math.Abs(qty);
            var rules = _tradeRulesProvider.Current;

            // [SOP-Strict]
            bool isDerivatives = _marketInfoProvider.GetMarketInfoOrThrow(symbol, MarketInfoPurpose.Execution).IsDerivatives;

            if (!isDerivatives) // Stock
            {
                value *= 1000m;
                decimal fee = Math.Floor(value * 0.001425m * (rules.Stock?.FeeDiscount ?? 1m));
                return Math.Max(20m, fee);
            }
            else
            {
                var f = ResolveFutureSpec(symbol, rules);
                if (f != null)
                {
                    // The configured fee is round-trip, so charge half per side.
                    // 參考使用者說明：TX00 設定 200，單邊應收 100
                    return (f.FeePerOrder / 2m) * Math.Abs(qty);
                }
                return 25m * Math.Abs(qty); // Default fallback
            }
        }

        private MuNTrade.Abstractions.Domain.Capital.FutureContractSpec? ResolveFutureSpec(string symbol, MuNTrade.Abstractions.Domain.Capital.TradeRulesConfig rules)
        {
            if (rules.Futures == null) return null;

            if (rules.Futures.TryGetValue(symbol, out var spec)) return spec;

            if (symbol.StartsWith("MTX") || symbol.StartsWith("MX"))
            {
                if (rules.Futures.TryGetValue("MTX00", out var mtx)) return mtx;
            }
            if (symbol.StartsWith("TX") && !symbol.StartsWith("TXF"))
            {
                if (rules.Futures.TryGetValue("TX00", out var tx)) return tx;
            }
            if (symbol.StartsWith("TM"))
            {
                if (rules.Futures.TryGetValue("TM00", out var tm)) return tm;
            }

            return rules.Futures.Values.FirstOrDefault(v => symbol.Contains(v.Name));
        }

        #endregion
    }
}

