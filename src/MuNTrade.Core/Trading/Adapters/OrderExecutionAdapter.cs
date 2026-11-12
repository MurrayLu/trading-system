using Microsoft.Extensions.Logging;
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
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Trading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Core.Trading.Adapters
{
    public class OrderExecutionAdapter : ITradeExecutionManager
    {
        private readonly IOrderExecutionService _oldExecutionService;
        private readonly IEventBus _eventBus;
        private readonly ILogger<OrderExecutionAdapter> _logger;
        private readonly IClock _clock;
        private readonly IExecutionTraceRegistry _traceRegistry;

        public OrderExecutionAdapter(
            IOrderExecutionService oldExecutionService,
            IEventBus eventBus,
            ILogger<OrderExecutionAdapter> logger,
             IClock clock,
             IExecutionTraceRegistry traceRegistry)
        {
            _oldExecutionService = oldExecutionService;
            _eventBus = eventBus;
            _logger = logger;
            _clock = clock;
            _traceRegistry = traceRegistry;
        }

        /// <summary>
        /// 接收新系統的 OrderIntent，將其翻譯並交給舊的執行服務。
        /// </summary>
        public Task<OrderResult> SubmitAsync(OrderIntent intent, CancellationToken ct = default)
        {
            if (intent == null)
            {
                _logger.LogWarning("SubmitAsync received a null intent.");
                return Task.FromResult(new OrderResult("N/A", false, false, 0, null, "Intent was null."));
            }

            try
            {
                // --- 步驟 1: 建立新的 SignalEvent ---
                // 這是最關鍵的轉換。我們的 OrderIntent 是「指令式」的(買/賣 N 股)，
                // 但新的 SignalEvent 是「宣告式」的(目標倉位比例)。
                // 我們需要做一個合理的推斷。
                var compatibleSignal = new SignalEvent
                {
                    StockId = intent.Symbol,
                    SignalTime = intent.CreatedAt.Value,
                    Price = intent.Plan.LimitPrice.GetValueOrDefault(0),
                    Reason = intent.CorrelationId,
                    Tag = "Slice1_Entry", // 給予一個標籤
                                          // ★ 核心推斷：既然是進場指令，我們假設目標是滿倉
                    TargetPositionRatio = (intent.Side == OrderSide.Buy) ? 1.0m : -1.0m
                };

                // --- 步驟 2: 建立新的 OrderRequest ---
                var oldRequest = new OrderRequest
                {
                    OriginalSignal = compatibleSignal, // 賦值給 required 成員
                    Side = intent.Side,                // Enum 已統一，可以直接賦值
                    Quantity = intent.Plan.Quantity,
                    StopLossPrice = intent.Plan.StopLossPrice.GetValueOrDefault(0),
                    TakeProfitPrice = intent.Plan.TakeProfitPrice.GetValueOrDefault(0)
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

                oldRequest.Trace = trace;
                oldRequest.StrategyId = trace.StrategyId;
                oldRequest.CorrelationId = trace.CorrelationId;
                oldRequest.AccountKey = trace.AccountKey;
                _traceRegistry.Put(trace);

                _logger.LogInformation("Adapter is submitting a translated order for {Symbol} with SL={SL} and TP={TP}.",
                    intent.Symbol, oldRequest.StopLossPrice, oldRequest.TakeProfitPrice);

                // --- 步驟 3: 呼叫舊的下單服務 ---
                _oldExecutionService.ExecuteOrder(oldRequest);

                // --- 步驟 4: 返回初步結果 (此處邏輯不變) ---
                var result = new OrderResult(
                    OrderId: intent.CorrelationId,
                    Accepted: true,
                    FullyFilled: false,
                    FilledQuantity: 0,
                    AvgFillPrice: null,
                    RejectReason: null
                );

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to translate and submit OrderIntent for {Symbol}.", intent.Symbol);
                return Task.FromResult(new OrderResult(intent.CorrelationId, false, false, 0, null, $"Exception: {ex.Message}"));
            }
        }

        public Task CancelAsync(string orderId, CancellationToken ct = default)
        {
            _logger.LogInformation("CancelAsync called for OrderId: {OrderId}. Not yet implemented.", orderId);
            // TODO: 實作取消訂單的邏輯，可能需要呼叫一個舊的取消服務
            return Task.CompletedTask;
        }
    }
}

