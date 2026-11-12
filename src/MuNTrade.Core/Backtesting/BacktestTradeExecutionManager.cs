using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Core.Trading.Adapters;

namespace MuNTrade.Core.Backtesting
{
    /// <summary>
    /// Backtest 專用執行器：不限制 FeedKind/時間延遲，使用最後成交價做市價成交基準。
    /// </summary>
    public sealed class BacktestTradeExecutionManager : ITradeExecutionManager
    {
        private readonly IOrderGateway _gateway;
        private readonly BacktestPriceCache _priceCache;
        private readonly IExecutionTraceRegistry _traceRegistry;
        private readonly IClock _clock;
        private readonly ILogger<BacktestTradeExecutionManager> _logger;

        public BacktestTradeExecutionManager(
            IOrderGateway gateway,
            BacktestPriceCache priceCache,
            IExecutionTraceRegistry traceRegistry,
            IClock clock,
            ILogger<BacktestTradeExecutionManager> logger)
        {
            _gateway = gateway;
            _priceCache = priceCache;
            _traceRegistry = traceRegistry;
            _clock = clock;
            _logger = logger;
        }

        public async Task<OrderResult> SubmitAsync(OrderIntent intent, CancellationToken ct = default)
        {
            if (intent == null)
                return new OrderResult("N/A", false, false, 0, null, "Intent was null.");

            if (intent.Plan == null || intent.Plan.Quantity <= 0)
                return new OrderResult(intent.CorrelationId ?? "N/A", false, false, 0, null, "Invalid plan/quantity.");

            var price = intent.Plan.LimitPrice.GetValueOrDefault(0m);
            if (price <= 0m && _priceCache.TryGetLastPrice(intent.Symbol, out var last))
                price = last;

            if (price <= 0m)
            {
                return new OrderResult(intent.CorrelationId ?? "N/A", false, false, 0, null, "No valid price for backtest execution.");
            }

            var signal = new SignalEvent
            {
                StockId = intent.Symbol,
                SignalTime = intent.CreatedAt.Value,
                Price = price,
                Reason = intent.CorrelationId,
                Tag = "Backtest",
                TargetPositionRatio = (intent.Side == OrderSide.Buy) ? 1.0m : -1.0m,
                StrategyId = intent.StrategyId,
                AccountKey = intent.AccountKey
            };

            var trace = new ExecutionTrace
            {
                CorrelationId = intent.CorrelationId ?? Guid.NewGuid().ToString("N"),
                IntentTimestamp = _clock.Now,
                DecisionPrice = price,
                StrategyId = intent.StrategyId ?? "Unknown",
                AccountKey = intent.AccountKey ?? "Unknown",
                Origin = "Backtest"
            };
            _traceRegistry.Put(trace);

            var request = new OrderRequest
            {
                OriginalSignal = signal,
                Side = intent.Side,
                Quantity = intent.Plan.Quantity,
                StopLossPrice = intent.Plan.StopLossPrice.GetValueOrDefault(0),
                TakeProfitPrice = intent.Plan.TakeProfitPrice.GetValueOrDefault(0),
                Trace = trace,
                StrategyId = trace.StrategyId,
                CorrelationId = trace.CorrelationId,
                AccountKey = trace.AccountKey,
                OrderType = intent.Plan.OrderType,
                TimeInForce = intent.Plan.Tif,
                MarketKind = intent.MarketKind
            };

            try
            {
                string resp;
                if (intent.MarketKind == MarketKind.Derivatives)
                    resp = await _gateway.PlaceFutureOrderAsync(request);
                else
                    resp = await _gateway.PlaceStockOrderAsync(request);

                var accepted = !resp.StartsWith("ERR:", StringComparison.OrdinalIgnoreCase);
                return new OrderResult(request.CorrelationId, accepted, false, 0, null, accepted ? null : resp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backtest execution failed for {Symbol}.", intent.Symbol);
                return new OrderResult(request.CorrelationId, false, false, 0, null, ex.Message);
            }
        }

        public Task CancelAsync(string orderId, CancellationToken ct = default)
        {
            // Backtest MVP: no-op cancel
            return Task.CompletedTask;
        }
    }
}

