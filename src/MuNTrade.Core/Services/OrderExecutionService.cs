
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Core.Backtesting;
using System;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Contracts.Market; // For IMarketInfoProvider
using MuNTrade.Abstractions.Domain.Common.Enums; // MarketKind

namespace MuNTrade.Core.Services
{
    public class OrderExecutionService : IOrderExecutionService
    {
        private readonly IEventBus _eventBus;
        private readonly TradeLoggerService _tradeLogger;
        private readonly ILogger<OrderExecutionService> _logger;
        private readonly MuNTrade.Abstractions.Contracts.Brokers.ICapitalApiService _capitalApi;
        private readonly IPaperOrderGateway _paperGateway;
        private readonly IMarketInfoProvider _marketInfoProvider; // [SOP-Strict]

        public OrderExecutionService(
            IEventBus eventBus,
            TradeLoggerService tradeLogger,
            ILogger<OrderExecutionService> logger,
            MuNTrade.Abstractions.Contracts.Brokers.ICapitalApiService capitalApi,
            IPaperOrderGateway paperGateway,
            IMarketInfoProvider marketInfoProvider)
        {
            _eventBus = eventBus;
            _tradeLogger = tradeLogger;
            _logger = logger;
            _capitalApi = capitalApi;
            _paperGateway = paperGateway;
            _marketInfoProvider = marketInfoProvider;

            // Bridge: Listen to ALL fill events (Paper or Live) and record them
            _eventBus.Subscribe<OrderFillEvent>(_tradeLogger.RecordFill);
        }

        public void ExecuteOrder(OrderRequest request)
        {
            if (request == null || request.Quantity == 0) return;

            // Route based on Mode
            if (request.IsLive)
            {
                ExecuteLiveOrder(request);
            }
            else
            {
                ExecuteSimulatedOrder(request);
            }
        }

        public Task RunAsync(CancellationToken stoppingToken) => Task.CompletedTask;

        private void ExecuteLiveOrder(OrderRequest request)
        {
            _logger.LogInformation("[LIVE EXECUTION] Submitting Order: {Side} {Qty} {Stock} (TIF:{TIF}, Type:{Type}, TradeType:{TradeType})",
                request.Side, request.Quantity, request.OriginalSignal.StockId, request.TimeInForce, request.OrderType, request.TradeType);

            var stockId = request.OriginalSignal.StockId ?? "";

            // Use the market kind pinned to the request.
            if (request.MarketKind == MarketKind.Unknown)
            {
                // Reject unknown markets before order submission.

                _logger.LogError("[OrderExecution] OrderRequest missing MarketKind (Unknown). Rejecting {Symbol}", stockId);
                return;
            }

            bool isFuture = request.MarketKind == MarketKind.Derivatives;

            Task.Run(async () =>
            {
                try
                {
                    string result;
                    // The gateway interface exposes both order cancellation methods.
                    // MainViewModel Mock had it.
                    if (isFuture)
                        result = await _capitalApi.PlaceFutureOrderAsync(request);
                    else
                        result = await _capitalApi.PlaceStockOrderAsync(request);

                    if (result.StartsWith("ERR:", StringComparison.OrdinalIgnoreCase))
                        _logger.LogError("[LIVE EXECUTION] Submit failed: {Error}", result);
                    else
                        _logger.LogInformation("[LIVE EXECUTION] ThreadId: {ThreadId}", result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[LIVE EXECUTION] Error submitting order");
                }
            });
        }

        private void ExecuteSimulatedOrder(OrderRequest request)
        {
            _logger.LogInformation("[PAPER EXECUTION] Routing to Gateway: {Side} {Qty} {Stock}", request.Side, request.Quantity, request.OriginalSignal.StockId);

            Task.Run(async () =>
            {
                try
                {
                    string result;
                    // Use Pinned MarketKind from Request
                    if (request.MarketKind == MarketKind.Unknown)
                    {
                        _logger.LogError("[OrderExecution] OrderRequest missing MarketKind (Unknown). Rejecting {Symbol}", request.OriginalSignal.StockId);
                        return;
                    }

                    // Trust the Pinned Kind
                    bool isFuture = request.MarketKind == MarketKind.Derivatives;

                    if (isFuture)
                        result = await _paperGateway.PlaceFutureOrderAsync(request);
                    else
                        result = await _paperGateway.PlaceStockOrderAsync(request);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[PAPER EXECUTION] Error");
                }
            });
        }
    }
}

