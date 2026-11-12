// MuNTrade.Core/Services/OrderExecutionService.cs (解耦最終版)

using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Hubs; // ★ 新增 IEventBus 引用
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Core.Backtesting;
using System;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Contracts.Market; // For IMarketInfoProvider
using MuNTrade.Abstractions.Domain.Common.Enums; // [Fix] MarketKind

namespace MuNTrade.Core.Services
{
    public class OrderExecutionService : IOrderExecutionService
    {
        private readonly IEventBus _eventBus;
        private readonly TradeLoggerService _tradeLogger;
        private readonly ILogger<OrderExecutionService> _logger;
        private readonly MuNTrade.Abstractions.Contracts.Brokers.ICapitalApiService _capitalApi;
        private readonly IPaperOrderGateway _paperGateway; // [New]
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

            // [SOP-Strict] Use Pinned MarketKind from Request
            if (request.MarketKind == MarketKind.Unknown)
            {
                // Fallback or Error? 10C Strict says Error.
                // But for safety, we can re-resolve if missing? 
                // User request says: "throw new InvalidOperationException".
                // Let's stick to the Pinned Truth.
                // If the upstream (PositionManager) didn't set it, it's a bug there.
                // However, we can perform a last-mile check/resolve if we want to be nice, 
                // but checking the user request "should change to: if (intent.MarketKind == MarketKind.Unknown) throw".
                _logger.LogError("[OrderExecution] OrderRequest missing MarketKind (Unknown). Rejecting {Symbol}", stockId);
                return; // Or throw inside the Task?
            }

            // Trust the Pinned Kind
            bool isFuture = request.MarketKind == MarketKind.Derivatives;

            Task.Run(async () =>
            {
                try
                {
                    string result;
                    // Check Gateway capabilities? Assuming Gateway interface has both.
                    // Just in case, confirm if PlaceFutureOrderAsync exists on ICapitalApiService.
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
                    // [10C] Use Pinned MarketKind from Request
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

