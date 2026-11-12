using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Core.Settings;
using MuNTrade.Abstractions.Settings.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MuNTrade.Core.Backtesting
{
    /// <summary>
    /// Backtest order gateway with TTL + limit matching.
    /// </summary>
    public sealed class BacktestOrderGateway : IOrderGateway
    {
        private sealed class PendingOrder
        {
            public string Id { get; }
            public OrderRequest Request { get; }
            public DateTime PlacedAt { get; }
            public DateTime ExpiresAt { get; }
            public InstrumentFillSettings FillSettings { get; }
            public decimal EffectiveLimitPrice { get; }
            public bool Done { get; set; }

            public PendingOrder(
                string id,
                OrderRequest request,
                DateTime placedAt,
                DateTime expiresAt,
                InstrumentFillSettings fillSettings,
                decimal effectiveLimitPrice)
            {
                Id = id;
                Request = request;
                PlacedAt = placedAt;
                ExpiresAt = expiresAt;
                FillSettings = fillSettings;
                EffectiveLimitPrice = effectiveLimitPrice;
            }
        }

        private readonly ILogger<BacktestOrderGateway> _logger;
        private readonly IEventBus _bus;
        private readonly IClock _clock;
        private readonly IOptionsMonitor<BacktestSettings> _backtestOptions;
        private readonly IOptionsMonitor<ApplicationSettings> _appSettings;
        private readonly object _sync = new();
        private readonly List<PendingOrder> _pending = new();

        public BacktestOrderGateway(
            ILogger<BacktestOrderGateway> logger,
            IEventBus bus,
            IClock clock,
            IOptionsMonitor<BacktestSettings> backtestOptions,
            IOptionsMonitor<ApplicationSettings> appSettings)
        {
            _logger = logger;
            _bus = bus;
            _clock = clock;
            _backtestOptions = backtestOptions;
            _appSettings = appSettings;
        }

        public Task<string> PlaceFutureOrderAsync(OrderRequest orderRequest)
        {
            BacktestSafetyGuard.EnsurePaperOnly(_appSettings.CurrentValue);
            var settings = _backtestOptions.CurrentValue.FillSettings.Futures;
            return Task.FromResult(Enqueue(orderRequest, settings));
        }

        public Task<string> PlaceStockOrderAsync(OrderRequest orderRequest)
        {
            BacktestSafetyGuard.EnsurePaperOnly(_appSettings.CurrentValue);
            var settings = _backtestOptions.CurrentValue.FillSettings.Stocks;
            return Task.FromResult(Enqueue(orderRequest, settings));
        }

        private string Enqueue(OrderRequest orderRequest, InstrumentFillSettings settings)
        {
            if (orderRequest?.OriginalSignal == null || string.IsNullOrEmpty(orderRequest.OriginalSignal.StockId))
                return "ERR:Backtest_Order_InvalidSignal";

            var now = _clock.Now;
            var exec = _backtestOptions.CurrentValue.Execution;
            var ttlSeconds = Math.Max(1, exec.TtlSeconds);
            var expiresAt = now.AddSeconds(ttlSeconds);
            var id = Guid.NewGuid().ToString("N");
            var limit = ResolveEffectiveLimit(orderRequest, exec);

            lock (_sync)
            {
                _pending.Add(new PendingOrder(id, orderRequest, now, expiresAt, settings, limit));
            }

            _logger.LogInformation("[BacktestBroker] Order queued id={Id} sym={Sym} side={Side} qty={Qty} limit={Limit} ttl={Ttl}s",
                id, orderRequest.OriginalSignal.StockId, orderRequest.Side, orderRequest.Quantity, limit, ttlSeconds);

            return id;
        }

        public void OnTick(TradeTickEvent tick)
        {
            if (tick == null || string.IsNullOrWhiteSpace(tick.Symbol)) return;

            List<PendingOrder> snapshot;
            lock (_sync)
            {
                snapshot = _pending
                    .Where(o => !o.Done && string.Equals(o.Request.OriginalSignal.StockId, tick.Symbol, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (snapshot.Count == 0) return;

            var now = _clock.Now;
            foreach (var order in snapshot)
            {
                if (now >= order.ExpiresAt)
                {
                    order.Done = true;
                    _logger.LogDebug("[BacktestBroker] Order expired id={Id} sym={Sym}", order.Id, tick.Symbol);
                    continue;
                }

                if (!CanFill(order, tick.Price)) continue;

                var exec = _backtestOptions.CurrentValue.Execution;
                var fillPrice = ApplyFillModel(tick.Price, order.Request.Side, order.FillSettings, exec);
                var fill = new OrderFillEvent
                {
                    OrderId = order.Id,
                    StockId = order.Request.OriginalSignal.StockId,
                    FillTime = now,
                    Side = order.Request.Side,
                    FillPrice = fillPrice,
                    FillQuantity = order.Request.Quantity,
                    OriginalReason = order.Request.OriginalSignal.Reason ?? string.Empty,
                    StrategyId = order.Request.StrategyId ?? string.Empty,
                    CorrelationId = order.Request.CorrelationId ?? string.Empty,
                    AccountKey = order.Request.AccountKey ?? string.Empty,
                    Account = order.Request.AccountKey ?? "Sim"
                };

                order.Done = true;
                _bus.Publish(fill);

                _logger.LogInformation("[BacktestBroker] FILLED id={Id} {Side} {Qty} {Sym} @ {Price}",
                    order.Id, fill.Side, fill.FillQuantity, fill.StockId, fill.FillPrice);
            }

            lock (_sync)
            {
                _pending.RemoveAll(o => o.Done);
            }
        }

        private static bool CanFill(PendingOrder order, decimal lastPrice)
        {
            var limit = order.EffectiveLimitPrice;
            if (limit <= 0) return true;

            return order.Request.Side == OrderSide.Buy
                ? lastPrice <= limit
                : lastPrice >= limit;
        }

        private static decimal ApplyFillModel(decimal price, OrderSide side, InstrumentFillSettings settings, BacktestExecutionOptions exec)
        {
            decimal slippage = 0m;
            if (settings.SlippagePoints > 0)
            {
                slippage = side == OrderSide.Buy ? settings.SlippagePoints : -settings.SlippagePoints;
            }
            else if (settings.SlippagePercent > 0)
            {
                slippage = price * (settings.SlippagePercent / 100m) * (side == OrderSide.Buy ? 1 : -1);
            }

            if (string.Equals(exec.FillModel, "ConservativeSlippageTicks", StringComparison.OrdinalIgnoreCase)
                && exec.ConservativeSlippageTicks > 0)
            {
                slippage += side == OrderSide.Buy ? exec.ConservativeSlippageTicks : -exec.ConservativeSlippageTicks;
            }
            return price + slippage;
        }

        private static decimal ResolveEffectiveLimit(OrderRequest request, BacktestExecutionOptions exec)
        {
            // Market orders in backtest should not be constrained by signal price.
            // Fill price is handled by ApplyFillModel(lastPrice, ...).
            if (request?.OrderType == OrderType.Market)
                return 0m;

            var limit = request?.OriginalSignal?.Price ?? 0m;
            if (limit <= 0 || exec.LimitOffsetTicks == 0) return limit;
            return request.Side == OrderSide.Buy
                ? limit + exec.LimitOffsetTicks
                : limit - exec.LimitOffsetTicks;
        }

        public Task<decimal> GetBuyingPowerAsync(string symbol)
        {
            return Task.FromResult(99999999m);
        }

        public Task<string> CancelOrderBySeqNoAsync(string seqNo) => Task.FromResult("OK:Backtest_Stub_Cancel");
        public Task<string> CancelOrderByBookNoAsync(string bookNo) => Task.FromResult("OK:Backtest_Stub_Cancel");
        public Task<string> CancelOrderByStockNoAsync(string symbol) => Task.FromResult("OK:Backtest_Stub_Cancel");
        public Task<string> CancelOrderByStockNoAdvanceAsync(string symbol, OrderSide side, decimal? price) => Task.FromResult("OK:Backtest_Stub_Cancel");
        public Task<string> CorrectPriceBySeqNoAsync(string seqNo, decimal newPrice) => Task.FromResult("OK:Backtest_Stub_Correct");
        public Task<string> CorrectPriceByBookNoAsync(string bookNo, decimal newPrice) => Task.FromResult("OK:Backtest_Stub_Correct");
        public Task<string> DecreaseOrderBySeqNoAsync(string seqNo, int reduceQty) => Task.FromResult("OK:Backtest_Stub_Decrease");

        public Task<string> OverSeaCancelOrderBySeqNoAsync(string seqNo) => Task.FromResult("OK:Backtest_Stub_OverSea");
        public Task<string> OverSeaCancelOrderByBookNoAsync(string bookNo) => Task.FromResult("OK:Backtest_Stub_OverSea");
        public Task<string> OverSeaCorrectPriceByBookNoAsync(string bookNo, decimal newPrice) => Task.FromResult("OK:Backtest_Stub_OverSea");
        public Task<string> OverSeaDecreaseOrderBySeqNoAsync(string seqNo, int reduceQty) => Task.FromResult("OK:Backtest_Stub_OverSea");
    }
}

