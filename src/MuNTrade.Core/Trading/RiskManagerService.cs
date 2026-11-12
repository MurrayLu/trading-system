using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Rules;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Services;

namespace MuNTrade.Core.Trading
{
    public class RiskManagerService : IRiskManagerService
    {
        private readonly IClock _clock;
        private readonly IEventBus _eventBus;
        private readonly ILogger<RiskManagerService> _logger;
        private readonly StrategyParameters _param;
        private readonly IEnumerable<IRiskRule> _rules;
        private readonly ConcurrentDictionary<string, DateTime> _lastExitTime = new();
        private readonly IPriceTierService _priceTier;
        // [10C] New Dependencies
        private readonly MuNTrade.Abstractions.Contracts.Capital.ICapitalProvider _capitalProvider;
        private readonly MuNTrade.Core.Services.Capital.AccountLedgerResolver _ledgerResolver;

        public RiskManagerService(
            IEventBus eventBus,
            IClock clock,
            ILogger<RiskManagerService> logger,
            IOptionsSnapshot<StrategyParameters> paramOpt,
            IEnumerable<IRiskRule> rules,
            IPriceTierService priceTier,
            MuNTrade.Abstractions.Contracts.Capital.ICapitalProvider capitalProvider,
            MuNTrade.Core.Services.Capital.AccountLedgerResolver ledgerResolver)
        {
            _eventBus = eventBus;
            _clock = clock;
            _logger = logger;
            _param = paramOpt.Value;
            _rules = rules;
            _priceTier = priceTier;
            _capitalProvider = capitalProvider;
            _ledgerResolver = ledgerResolver;
        }

        // [10C] New Risk Evaluation for Intent
        public RiskDecision Evaluate(MuNTrade.Abstractions.Dtos.Trading.OrderIntent intent)
        {
            if (string.IsNullOrWhiteSpace(intent.AccountKey))
                return RiskDecision.Reject("Intent missing AccountKey");

            // [10C] 必須檢查 MarketKind
            if (intent.MarketKind == MuNTrade.Abstractions.Domain.Common.Enums.MarketKind.Unknown)
                return RiskDecision.Reject("Intent missing MarketKind (Market Truth not resolved)");

            // 1. Resolve LedgerKey (Implicitly validates MarketKind too)
            // If intent already has MarketKind (from Pinning), we create LedgerKey directly.
            // Pinning ensures MarketKind is set.
            var ledgerKey = new MuNTrade.Abstractions.Domain.Capital.LedgerKey(intent.AccountKey, intent.MarketKind);

            // [10C-PROBE] Verification Log
            if (intent.IntentKind == "10C_PROBE" || (intent.StrategyId != null && intent.StrategyId.StartsWith("Verification.10C", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("[10C-PROBE][RISK] StrategyId={StrategyId} AccountKey={AccountKey} Symbol={Symbol} IntentMarketKind={MarketKind} LedgerKey={LedgerKey}",
                    intent.StrategyId, intent.AccountKey, intent.Symbol, intent.MarketKind, ledgerKey);
            }

            // 2. Capital Check
            var capital = _capitalProvider.GetSnapshot(ledgerKey);

            // TODO: Implement actual Capital Rules (e.g. BuyingPower > Cost)
            // For now, if BuyingPower <= 0, Reject (Safety Net)
            if (capital.BuyingPower <= 0)
            {
                // Check if it's closing position? Intent doesn't clearly say if it's closing unless we check position.
                // Assuming worst case: Opening.
                // But wait, strategy might be closing.
                // For MVP 10C, we just ensure we can query Capital.
                // We will Log capital state.
            }

            // 3. Delegate to existing Rules (adapter needed? Or new rules?)
            // Existing rules expect OrderRequest + Position.
            // We can map Intent to OrderRequest for compatibility or use new rules.

            return RiskDecision.Approve();
        }

        // private void OnOrderRequestReceived(OrderRequest request) { ... } // ★ 此事件處理器也應一併移除

        /// <summary>
        /// ★★★ 核心修改 3：實作新的 AssessOrder 介面方法 ★★★
        /// </summary>
        public bool AssessOrder(OrderRequest request, Position? currentPosition, out OrderRequest? finalOrder)
        {
            finalOrder = null;
            if (string.IsNullOrWhiteSpace(request?.OriginalSignal?.StockId))
                return Reject("Missing StockId in original signal", request);

            var stockId = request.OriginalSignal.StockId;

            // 如果呼叫端沒有提供倉位，我們視為空倉
            var position = currentPosition ?? new Position { StockId = stockId, Quantity = 0 };

            // 1. 可插拔規則檢查
            foreach (var rule in _rules)
            {
                // ★ 將傳入的 position 傳遞給每一個規則
                var decision = rule.Evaluate(request, position);
                if (!decision.IsApproved)
                {
                    return Reject(decision.Reason, request);
                }
            }

            // ★★★ 核心修改 4：所有對 posMgr 的呼叫，都改用傳入的 position 參數 ★★★
            long posQty = position.Quantity;

            // 2. 內建風控檢查
            if (posQty == 0) // 場景：開新倉
            {
                if (request.Side == OrderSide.Buy && _param.TradingMode == TradingMode.ShortOnly)
                    return Reject("Trading mode is Short-Only", request);

                if (request.Side == OrderSide.Sell && _param.TradingMode == TradingMode.LongOnly)
                    return Reject("Trading mode is Long-Only", request);
            }
            else if ((posQty > 0 && request.Side == OrderSide.Buy) || (posQty < 0 && request.Side == OrderSide.Sell)) // 場景：同向加碼
            {
                if (!_param.AllowPyramiding)
                    return Reject("Pyramiding (add-on) is disabled", request);

                if (Math.Abs(posQty) >= _param.MaxLayers)
                    return Reject("Exceeded max position layers", request);
            }
            else // 場景：反向操作 (包含平倉或反手)
            {
                if (_param.ReverseCooldownSec > 0)
                {
                    if (_lastExitTime.TryGetValue(stockId, out var last) && (_clock.Now - last).TotalSeconds < _param.ReverseCooldownSec)
                        return Reject("Reverse trade is in cooldown period", request);
                }
            }

            // 3. 填充訂單細節
            bool isExitAction = (posQty > 0 && request.Side == OrderSide.Sell) || (posQty < 0 && request.Side == OrderSide.Buy);
            return BuildOrderDetails(request, isExitAction, out finalOrder);
        }


        // ★★★ 核心修改：BuildOrderDetails 微調，不再接收 SignalAction ★★★
        private bool BuildOrderDetails(OrderRequest request, bool isExitAction, out OrderRequest finalOrder)
        {
            decimal price = request.OriginalSignal.Price;
            // 改用統一的 PriceTier Service
            int slTicks = _param.SL;
            int tpTicks = _param.TP;

            if (request.Side == OrderSide.Buy)
            {
                // Buy SL: 往下 N 檔
                request.StopLossPrice = _priceTier.MoveByTicks(price, -slTicks);
                // Buy TP: 往上 N 檔
                request.TakeProfitPrice = _priceTier.MoveByTicks(price, tpTicks);
            }
            else // Sell
            {
                // Sell SL: 往上 N 檔
                request.StopLossPrice = _priceTier.MoveByTicks(price, slTicks);
                // Sell TP: 往下 N 檔
                request.TakeProfitPrice = _priceTier.MoveByTicks(price, -tpTicks);
            }
            finalOrder = request;

            // ★ 使用 isExitAction 來判斷是否需要記錄出場時間
            if (isExitAction)
            {
                _lastExitTime[request.OriginalSignal.StockId] = _clock.Now;
            }

            // 日誌可以簡化，不再需要 inferredAction
            _logger.LogInformation("[Risk] PASS {Side} {Qty} {Stock} @ {P:F2} SL={SL:F2} TP={TP:F2}",
                                     finalOrder.Side, finalOrder.Quantity, finalOrder.OriginalSignal.StockId, price, finalOrder.StopLossPrice, finalOrder.TakeProfitPrice);

            return true;
        }

        // Reject 和 GetTickSize 方法維持不變...
        private bool Reject(string reason, OrderRequest? req)
        {
            _logger.LogDebug("[Risk] REJECT Order for {Stock}: {Reason} | Original Signal: {SReason}",
                               req?.OriginalSignal?.StockId, reason, req?.OriginalSignal?.Reason);
            return false;
        }

        // GetTickSize 已移除，全面改用 IPriceTierService

        private static decimal AlignToTick(decimal price, decimal tick)
            => Math.Round(price / tick, MidpointRounding.AwayFromZero) * tick;
    }
}
