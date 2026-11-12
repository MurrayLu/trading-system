using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Contracts.Brokers;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Contracts.Time;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MuNTrade.Core.Trading.Adapters;

namespace MuNTrade.Core.Trading
{
    public class LiveOrderExecutionService : ITradeExecutionManager
    {
        // ★ 核心修改：依賴 IOrderGateway 而不是任何具體的券商服務
        private readonly IOrderGateway _gateway;
        private readonly IEventBus _eventBus;
        private readonly ILogger<LiveOrderExecutionService> _logger;
        private readonly IExecutionTraceRegistry _traceRegistry;
        private readonly IClock _clock;

        public LiveOrderExecutionService(
            IOrderGateway gateway, // ★ 核心修改：注入 IOrderGateway
            IEventBus eventBus,
            ILogger<LiveOrderExecutionService> logger,
            IExecutionTraceRegistry traceRegistry,
            IClock clock)
        {
            _gateway = gateway;
            _eventBus = eventBus;
            _logger = logger;
            _traceRegistry = traceRegistry;
            _clock = clock;
        }

        public async Task<OrderResult> SubmitAsync(OrderIntent intent, CancellationToken ct = default)
        {
            if (intent == null)
            {
                return new OrderResult("N/A", false, false, 0, null, "Intent was null.");
            }

            if (intent.FeedKind != TickFeedKind.RealTime)
            {
                return new OrderResult(
                    intent.CorrelationId ?? "N/A",
                    false,
                    false,
                    0,
                    null,
                    $"Blocked non-live intent (FeedKind={intent.FeedKind}). Only realtime signals are allowed.");
            }

            // --- Safety Gate: only allow near-real-time intents ---
            // Prevent history replay or reconnection bursts from triggering orders.
            var now = _clock.Now;
            var createdAt = intent.CreatedAt.Value;
            if (createdAt != default)
            {
                var ageSec = (now - createdAt).TotalSeconds;
                if (ageSec > 2)
                {
                    return new OrderResult(
                        intent.CorrelationId ?? "N/A",
                        false,
                        false,
                        0,
                        null,
                        $"Blocked non-live intent (ageSec={ageSec:F3}s). Only realtime signals are allowed.");
                }
            }

            // --- 步驟 1: 前置檢查 ---
            // IOrderGateway 內部應負責連線狀態的檢查，這裡可以簡化。
            // 如果需要，可以在 IOrderGateway 介面加上 CurrentState 屬性。
            // 為保持此服務的純粹性，我們假設 Gateway 會處理。

            try
            {
                // --- 步驟 2: 將 OrderIntent 翻譯成 OrderRequest ---
                // 這部分的邏輯與之前完全相同，因為它是這個服務的核心職責。
                var signal = new SignalEvent
                {
                    StockId = intent.Symbol,
                    SignalTime = intent.CreatedAt.Value,
                    Price = intent.Plan.LimitPrice.GetValueOrDefault(0),
                    Reason = intent.CorrelationId,
                    Tag = "Slice1_Entry",
                    TargetPositionRatio = (intent.Side == OrderSide.Buy) ? 1.0m : -1.0m,
                    StrategyId = intent.StrategyId,
                    AccountKey = intent.AccountKey
                };

                var trace = new ExecutionTrace
                {
                    CorrelationId = intent.CorrelationId ?? Guid.NewGuid().ToString("N"),
                    IntentTimestamp = _clock.Now,
                    DecisionPrice = intent.Plan.LimitPrice,
                    StrategyId = intent.StrategyId ?? "Unknown",
                    AccountKey = intent.AccountKey ?? "Unknown",
                    Origin = "Strategy"
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
                    AccountKey = trace.AccountKey
                };

                _logger.LogInformation("Submitting order via Gateway for {Symbol}: {Side} {Qty} @ {Price}",
                    intent.Symbol, request.Side, request.Quantity, request.OriginalSignal.Price);

                // --- 步驟 3: ★ 核心修改：呼叫 IOrderGateway 執行證券下單 ★ ---
                string gatewayResponse = await _gateway.PlaceStockOrderAsync(request);

                _logger.LogInformation("Gateway response for {Symbol}: {Response}", intent.Symbol, gatewayResponse);

                // --- 步驟 4: 根據 Gateway 回應，返回初步結果 ---
                // 這裡的邏輯與之前相同，但現在我們知道回應來自一個通用的閘道，而不是特定券商。
                bool accepted = !gatewayResponse.StartsWith("ERR:", StringComparison.OrdinalIgnoreCase)
                    && !gatewayResponse.Contains("失敗")
                    && !gatewayResponse.Contains("無法");

                var result = new OrderResult(
                    OrderId: intent.CorrelationId,
                    Accepted: accepted,
                    FullyFilled: false,
                    FilledQuantity: 0,
                    AvgFillPrice: null,
                    RejectReason: accepted ? null : gatewayResponse
                );

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit order via Gateway for {Symbol}.", intent.Symbol);
                return new OrderResult(intent.CorrelationId, false, false, 0, null, $"Exception: {ex.Message}");
            }
        }

        public Task CancelAsync(string orderId, CancellationToken ct = default)
        {
            _logger.LogInformation("CancelAsync called for OrderId: {OrderId}. Not yet implemented.", orderId);
            // TODO: 實作呼叫 IOrderGateway 的取消訂單方法
            // await _gateway.CancelStockOrderAsync(orderId, ct);
            return Task.CompletedTask;
        }
    }
}

