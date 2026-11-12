using System;
using System.Collections.Generic;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session; // For OrderFillEvent
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums; // For OrderSide

namespace MuNTrade.Core.Trading.Adapters
{
    /// <summary>
    /// 負責將即時回報 (BrokerReportDto) 轉換為標準成交事件 (OrderFillEvent)，
    /// 讓 LivePositionManager 能正確接收並產生 CompletedTrade。
    /// </summary>
    public class BrokerReportTranslator
    {
        private readonly IEventBus _eventBus;
        private readonly ILogger<BrokerReportTranslator> _logger;
        private readonly IMemoryCache _dedupCache;
        private readonly IExecutionTraceRegistry _traceRegistry;
        private readonly MuNTrade.Core.Services.StrategyLedgerService? _ledger;

        public BrokerReportTranslator(
            IEventBus eventBus,
            ILogger<BrokerReportTranslator> logger,
            IExecutionTraceRegistry traceRegistry,
            IServiceProvider sp) // Use ServiceProvider to resolve optional dependency to avoid circularity if any (though unlikely here)
        {
            _eventBus = eventBus;
            _logger = logger;
            _traceRegistry = traceRegistry;
            _ledger = sp.GetService(typeof(MuNTrade.Core.Services.StrategyLedgerService)) as MuNTrade.Core.Services.StrategyLedgerService;

            // 用於短時間去重 (Short TTL)
            _dedupCache = new MemoryCache(new MemoryCacheOptions());

            _eventBus.Subscribe<BrokerReportDto>(OnBrokerReport);
            _logger.LogInformation("BrokerReportTranslator 已啟動，開始監聽 Live 成交回報。");
        }

        private void OnBrokerReport(BrokerReportDto dto)
        {
            try
            {
                // 1. 只處理 Live (Real) 回報
                if (dto.ReportType != "Real") return;

                // 2. 只處理成交 (Action == "成交" 或 Status 包含 "成交")
                bool isTrade = dto.Action == "成交" || dto.Status.Contains("成交");
                if (!isTrade) return;

                // 必須有成交量 (Delta > 0)，否則可能是純狀態變更
                if (dto.Quantity <= 0) return;

                // 3. 去重檢查
                // Key: CorrelationId|ExchangeTs|Price|Quantity|Side
                // 注意：ExchangeTs 必須精確，若 API 回傳由 BrokerReportDto 標準化過，應該可信
                string dedupKey = $"{dto.CorrelationId}|{dto.ExchangeTs.Value.Ticks}|{dto.Price}|{dto.Quantity}|{dto.Side}";

                if (_dedupCache.TryGetValue(dedupKey, out _))
                {
                    _logger.LogDebug("[Dedup] 忽略重複成交回報: {Key}", dedupKey);
                    return;
                }

                // 設定 5 秒 TTL，避免同一筆回報被短時間重複處理
                _dedupCache.Set(dedupKey, true, TimeSpan.FromSeconds(5));

                // 4. 解析方向
                OrderSide side;
                if ("Buy".Equals(dto.Side, StringComparison.OrdinalIgnoreCase) || "買".Equals(dto.Side))
                    side = OrderSide.Buy;
                else if ("Sell".Equals(dto.Side, StringComparison.OrdinalIgnoreCase) || "賣".Equals(dto.Side))
                    side = OrderSide.Sell;
                else
                    return; // 無效方向，忽略

                // 5. 建立 OrderFillEvent
                var fillEvent = new OrderFillEvent
                {
                    StockId = dto.Symbol,
                    FillPrice = dto.Price,
                    FillQuantity = dto.Quantity, // 這是 Delta
                    Side = side,
                    FillTime = dto.ExchangeTs.Value,
                    OrderId = !string.IsNullOrEmpty(dto.CorrelationId) ? dto.CorrelationId : dto.BookNo,
                    Account = !string.IsNullOrEmpty(dto.Account) ? dto.Account : "Real",
                    OriginalReason = dto.Message
                };

                // Fill in missing attribution fields.
                if (!string.IsNullOrEmpty(dto.CorrelationId) && _traceRegistry.TryGet(dto.CorrelationId, out var trace))
                {
                    fillEvent.StrategyId = trace.StrategyId;
                    fillEvent.AccountKey = trace.AccountKey;
                    fillEvent.CorrelationId = trace.CorrelationId;
                }
                else
                {
                    // Fallback to Real for un-traced live orders
                    _ledger?.ReportRegistryMiss();
                    fillEvent.StrategyId = "Unknown";
                    fillEvent.AccountKey = !string.IsNullOrEmpty(dto.Account) ? dto.Account : "Real";
                    // If correlation ID is missing, we can't do much.
                }

                // 6.發布

                // 6.發布
                _eventBus.Publish(fillEvent);

                _logger.LogInformation("[LiveFill] 轉換成交: {Symbol} {Side} {Qty}@{Price} (Order: {OrderId})",
                    fillEvent.StockId, fillEvent.Side, fillEvent.FillQuantity, fillEvent.FillPrice, fillEvent.OrderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "轉換成交回報失敗: {Raw}", dto.Message);
            }
        }
    }
}

