using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Dtos.Trading;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Core.Trading.Gateways
{
    /// <summary>
    /// 進階模擬交易所 (Paper Trading Gateway) - 擬真撮合版
    /// 撮合邏輯：
    /// 1. 市價單：立即吃掉對手盤 (Buy->Ask1, Sell->Bid1)。
    /// 2. 限價單：等待 Tick 價格觸價，或掛單價優於對手盤時立即成交。
    /// 3. 狀態回報：即時發送 New / Filled 狀態。
    /// </summary>
    public class PaperTradingGateway : IPaperOrderGateway, IDisposable
    {
        private readonly IEventBus _eventBus;
        private readonly ILogger<PaperTradingGateway> _logger;
        private readonly IClock _clock;
        private readonly MarketSnapshotBuilder _snapshots;

        // 存放掛單的虛擬委託簿 (Key: Symbol)
        private readonly ConcurrentDictionary<string, List<PaperOrderContext>> _orderBook = new();

        // 即時最佳買賣價快取 (Key: Symbol)
        private readonly ConcurrentDictionary<string, MarketQuote> _quoteCache = new();

        // 內部報價結構
        private struct MarketQuote
        {
            public decimal BestBid; // 買一價
            public decimal BestAsk; // 賣一價
            public DateTime Time;
        }

        private readonly List<IDisposable> _tokens = new();
        private long _demoSequence;
        

        public PaperTradingGateway(
            IEventBus eventBus,
            ILogger<PaperTradingGateway> logger,
            IClock clock,
            MarketSnapshotBuilder snapshots)
        {
            _eventBus = eventBus;
            _logger = logger;
            _clock = clock;
            _snapshots = snapshots;

            // 訂閱所有可能更新報價的事件，確保快取最新
            _tokens.Add(_eventBus.Subscribe<TradeTickEvent>(OnTickReceived));
            _tokens.Add(_eventBus.Subscribe<QuoteSnapshotEvent>(OnQuoteSnapshot));
            _tokens.Add(_eventBus.Subscribe<QuoteBest5Event>(OnBest5Received));
        }

        private class PaperOrderContext
        {
            public string OrderId { get; set; } = "";
            public string BookNo { get; set; } = "";
            // Original error: "OrderRequest properties need to be set".
            // Fix: Remove new() since OrderRequest has required members. Use 'required' or ensure initialization.
            public required OrderRequest Request { get; set; }
            public string CorrelationId { get; set; } = "";
            public DateTime SubmitTime { get; set; }
        }

        public Task<string> PlaceStockOrderAsync(OrderRequest request) => ProcessOrder(request);
        public Task<string> PlaceFutureOrderAsync(OrderRequest request) => ProcessOrder(request);
        public Task<string> PlaceOptionOrderAsync(OrderRequest request) => ProcessOrder(request);

        // [New] 實作 IOrderGateway 缺少的成員
        public Task<decimal> GetBuyingPowerAsync(string symbol)
        {
            // 模擬擁有無限購買力或固定額度
            return Task.FromResult(10_000_000m);
        }

        private Task<string> ProcessOrder(OrderRequest request)
        {
            if (request.IsLive) throw new InvalidOperationException("LIVE_DISABLED");
            if (request.Quantity <= 0) throw new InvalidOperationException("INVALID_QUANTITY");
            // [Fix] OrderRequest 沒有直接的 StockId/Price，需從 OriginalSignal 取得
            var stockId = request.OriginalSignal?.StockId ?? "UNKNOWN";
            var price = request.OriginalSignal?.Price ?? 0m;
            // 如果請求是針對 Limit 且 OriginalSignal.Price 為 0，可能需要檢查策略邏輯，這裡暫時信任 Signal

            // 注意：我們需要把這些資訊傳遞給後續邏輯
            // 但為了不修改 request 對象 (它是 input)，我們在 Context 或 SendReport 時處理

            var seqNo = GenerateSeqNo();
            if (string.IsNullOrWhiteSpace(seqNo))
            {
                _logger.LogError("[Paper] GenerateSeqNo returned empty.");
                throw new InvalidOperationException("PaperTradingGateway failed to generate order BookNo.");
            }
            var corrId = request.Trace?.CorrelationId ?? "";

            var context = new PaperOrderContext
            {
                OrderId = seqNo,
                BookNo = seqNo,
                Request = request,
                CorrelationId = corrId,
                SubmitTime = _clock.Now
            };
            if (string.IsNullOrWhiteSpace(context.BookNo))
            {
                _logger.LogError("[Paper] ProcessOrder created empty BookNo. Symbol={Symbol} CorrId={CorrId}", stockId, corrId);
                throw new InvalidOperationException("PaperTradingGateway created empty BookNo.");
            }

            // 1. 先發送「新單 (New)」回報
            var isMarket = request.OrderType == OrderType.Market;
            var newOrderPrice = isMarket ? 0m : price;
            SendReport(stockId, request.Side, newOrderPrice, request.Quantity, "N", context.BookNo, corrId);

            // 2. 進入撮合流程
            if (request.OrderType == OrderType.Market)
            {
                MatchMarketOrder(context);
            }
            else
            {
                AddToOrderBook(context);
                CheckLimitOrderImmediate(context);
            }

            return Task.FromResult($"OK:{seqNo}");
        }

        public Task CancelOrderAsync(string orderId) => CancelOrderBySeqNoAsync(orderId);

        // --- New Interface Implementation ---

        public Task<string> CancelOrderBySeqNoAsync(string seqNo)
        {
            return CancelOrderInternal(seqNo, "CancelBySeqNo");
        }

        public Task<string> CancelOrderByBookNoAsync(string bookNo)
        {
            // Paper Mode 使用本機 BookNo (等同 OrderId) 追蹤
            return CancelOrderInternal(bookNo, "CancelByBookNo");
        }

        public Task<string> CancelOrderByStockNoAsync(string symbol)
        {
            // 刪除該 Symbol 所有委託
            if (_orderBook.TryGetValue(symbol, out var list))
            {
                lock (list)
                {
                    var targets = list.ToList();
                    foreach (var t in targets)
                    {
                        list.Remove(t);
                        var price = t.Request.OriginalSignal?.Price ?? 0m;
                        SendReport(symbol, t.Request.Side, price, t.Request.Quantity, "C", t.BookNo, t.CorrelationId);
                    }
                }
            }
            return Task.FromResult("OK:BulkCancel");
        }

        public Task<string> CancelOrderByStockNoAdvanceAsync(string symbol, OrderSide side, decimal? price)
        {
            if (_orderBook.TryGetValue(symbol, out var list))
            {
                lock (list)
                {
                    // 簡易篩選
                    var targets = list.Where(x => x.Request.Side == side).ToList();
                    if (price.HasValue)
                        targets = targets.Where(x => (x.Request.OriginalSignal?.Price ?? 0) == price.Value).ToList();

                    foreach (var t in targets)
                    {
                        list.Remove(t);
                        var p = t.Request.OriginalSignal?.Price ?? 0m;
                        SendReport(symbol, t.Request.Side, p, t.Request.Quantity, "C", t.BookNo, t.CorrelationId);
                    }
                }
            }
            return Task.FromResult("OK:AdvanceCancel");
        }

        public Task<string> CorrectPriceBySeqNoAsync(string seqNo, decimal newPrice)
        {
            // 模擬改價
            foreach (var kvp in _orderBook)
            {
                lock (kvp.Value)
                {
                    var target = kvp.Value.FirstOrDefault(x => IsOrderMatch(x, seqNo));
                    if (target != null)
                    {
                        LogOrderMatched("CorrectPrice", target);
                        SendReport(kvp.Key, target.Request.Side, newPrice, target.Request.Quantity, "U", target.BookNo, target.CorrelationId);
                        return Task.FromResult("OK:PriceCorrected");
                    }
                }
            }
            LogOrderBookNotFound("CorrectPrice", seqNo);
            return Task.FromResult("ERR:OrderNotFound");
        }

        public Task<string> CorrectPriceByBookNoAsync(string bookNo, decimal newPrice) => CorrectPriceBySeqNoAsync(bookNo, newPrice); // Mock

        public Task<string> DecreaseOrderBySeqNoAsync(string seqNo, int reduceQty)
        {
            foreach (var kvp in _orderBook)
            {
                lock (kvp.Value)
                {
                    var target = kvp.Value.FirstOrDefault(x => IsOrderMatch(x, seqNo));
                    if (target != null)
                    {
                        LogOrderMatched("Reduce", target);
                        target.Request.Quantity -= reduceQty;
                        if (target.Request.Quantity <= 0)
                        {
                            kvp.Value.Remove(target);
                            SendReport(kvp.Key, target.Request.Side, 0, 0, "C", target.BookNo, target.CorrelationId);
                        }
                        else
                        {
                            var p = target.Request.OriginalSignal?.Price ?? 0m;
                            SendReport(kvp.Key, target.Request.Side, p, reduceQty, "U", target.BookNo, target.CorrelationId);
                        }
                        return Task.FromResult("OK:Decreased");
                    }
                }
            }
            LogOrderBookNotFound("Reduce", seqNo);
            return Task.FromResult("ERR:OrderNotFound");
        }

        public Task<string> OverSeaCancelOrderBySeqNoAsync(string seqNo)
            => CancelOrderInternal(seqNo, "OverSeaCancelBySeqNo");

        public Task<string> OverSeaCancelOrderByBookNoAsync(string bookNo)
            => CancelOrderInternal(bookNo, "OverSeaCancelByBookNo");

        public Task<string> OverSeaCorrectPriceByBookNoAsync(string bookNo, decimal newPrice)
            => CorrectPriceBySeqNoAsync(bookNo, newPrice);

        public Task<string> OverSeaDecreaseOrderBySeqNoAsync(string seqNo, int reduceQty)
            => DecreaseOrderBySeqNoAsync(seqNo, reduceQty);

        private Task<string> CancelOrderInternal(string orderId, string reason)
        {
            foreach (var kvp in _orderBook)
            {
                lock (kvp.Value)
                {
                    var target = kvp.Value.FirstOrDefault(x => IsOrderMatch(x, orderId));
                    if (target != null)
                    {
                        LogOrderMatched(reason, target);
                        kvp.Value.Remove(target);
                        var stockId = kvp.Key;
                        var price = target.Request.OriginalSignal?.Price ?? 0m;

                        SendReport(stockId, target.Request.Side, price, target.Request.Quantity, "C", target.BookNo, target.CorrelationId);
                        _logger.LogInformation("[Paper] Order Cancelled: {Symbol} #{SeqNo} ({Reason})", stockId, target.OrderId, reason);
                        return Task.FromResult("OK");
                    }
                }
            }
            LogOrderBookNotFound(reason, orderId);
            return Task.FromResult("ERR:NotFound");
        }

        // === [Core] 擬真撮合邏輯 ===

        private void MatchMarketOrder(PaperOrderContext order)
        {
            var symbol = order.Request.OriginalSignal?.StockId ?? "";
            // [Fix] Ensure strictly using Order Book logic
            var fillPrice = ResolveMarketFillPrice(order.Request.Side, symbol);

            if (fillPrice > 0)
            {
                ExecuteFill(order, fillPrice);
                return;
            }

            AddToOrderBook(order);
            _logger.LogWarning("[Paper] Market order queued (No Quote): {Symbol}", symbol);
        }

        private void CheckLimitOrderImmediate(PaperOrderContext order)
        {
            var symbol = order.Request.OriginalSignal?.StockId ?? "";
            var limitPrice = order.Request.OriginalSignal?.Price ?? 0m;

            if (!_quoteCache.TryGetValue(symbol, out var quote)) return;

            bool filled = false;
            decimal fillPrice = 0;

            if (order.Request.Side == OrderSide.Buy)
            {
                // 限價買單 >= 賣一 (Ask 1) -> 立即成交
                if (quote.BestAsk > 0 && limitPrice >= quote.BestAsk)
                {
                    filled = true;
                    fillPrice = quote.BestAsk;
                }
            }
            else // Sell
            {
                // 限價賣單 <= 買一 (Bid 1) -> 立即成交
                if (quote.BestBid > 0 && limitPrice <= quote.BestBid)
                {
                    filled = true;
                    fillPrice = quote.BestBid;
                }
            }

            if (filled)
            {
                if (_orderBook.TryGetValue(symbol, out var list))
                {
                    lock (list)
                    {
                        if (list.Remove(order))
                        {
                            ExecuteFill(order, fillPrice);
                        }
                    }
                }
            }
        }

        private void OnTickReceived(TradeTickEvent tick)
        {
            UpdateQuoteCache(tick.Symbol, tick.BidAtTrade, tick.AskAtTrade);

            if (!_orderBook.TryGetValue(tick.Symbol, out var orders)) return;

            lock (orders)
            {
                for (int i = orders.Count - 1; i >= 0; i--)
                {
                    var order = orders[i];
                    var limitPrice = order.Request.OriginalSignal?.Price ?? 0m;

                    bool filled = false;
                    decimal fillPrice = 0;

                    if (order.Request.OrderType == OrderType.Market)
                    {
                        // [Fix] Market uses Best Quote, NOT Tick Price directly
                        var fill = ResolveMarketFillPrice(order.Request.Side, tick.Symbol);
                        if (fill <= 0) continue; // No quote available, keep queuing

                        filled = true;
                        fillPrice = fill;
                    }
                    else // Limit
                    {
                        if (order.Request.Side == OrderSide.Buy)
                        {
                            if (tick.Price <= limitPrice)
                            {
                                filled = true;
                                fillPrice = tick.Price;
                            }
                        }
                        else // Sell
                        {
                            if (tick.Price >= limitPrice)
                            {
                                filled = true;
                                fillPrice = tick.Price;
                            }
                        }
                    }

                    if (filled)
                    {
                        ExecuteFill(order, fillPrice);
                        orders.RemoveAt(i);
                    }
                }
            }
        }

        private void OnQuoteSnapshot(QuoteSnapshotEvent e)
        {
            UpdateQuoteCache(e.Symbol, e.BidPrice, e.AskPrice);
            TryFillQueuedMarketOrders(e.Symbol);
        }

        private void OnBest5Received(QuoteBest5Event e)
        {
            // [Fix] QuoteBest5Event 使用 List<PriceLevel> Bids/Asks
            decimal bid1 = (e.Bids != null && e.Bids.Count > 0) ? e.Bids[0].Price : 0;
            decimal ask1 = (e.Asks != null && e.Asks.Count > 0) ? e.Asks[0].Price : 0;
            UpdateQuoteCache(e.Symbol, bid1, ask1);
            TryFillQueuedMarketOrders(e.Symbol);
        }

        private void UpdateQuoteCache(string symbol, decimal bid, decimal ask)
        {
            _quoteCache.AddOrUpdate(symbol,
                new MarketQuote { BestBid = bid, BestAsk = ask, Time = _clock.Now },
                (key, old) =>
                {
                    var newQ = old;
                    if (bid > 0) newQ.BestBid = bid;
                    if (ask > 0) newQ.BestAsk = ask;
                    newQ.Time = _clock.Now;
                    return newQ;
                });
        }

        // [New] Attempt to fill queued market orders when quotes update
        private void TryFillQueuedMarketOrders(string symbol)
        {
            if (!_orderBook.TryGetValue(symbol, out var orders)) return;
            if (!TryGetBestQuote(symbol, out var bid, out var ask)) return;

            lock (orders)
            {
                for (int i = orders.Count - 1; i >= 0; i--)
                {
                    var order = orders[i];
                    if (order.Request.OrderType != OrderType.Market) continue;

                    var fillPrice = order.Request.Side == OrderSide.Buy ? ask : bid;
                    if (fillPrice <= 0) continue;

                    ExecuteFill(order, fillPrice);
                    orders.RemoveAt(i);
                }
            }
        }

        private bool TryGetBestQuote(string symbol, out decimal bestBid, out decimal bestAsk)
        {
            bestBid = 0m;
            bestAsk = 0m;

            if (_quoteCache.TryGetValue(symbol, out var q))
            {
                bestBid = q.BestBid;
                bestAsk = q.BestAsk;
            }

            if ((bestBid <= 0 || bestAsk <= 0) && _snapshots.GetLatestSnapshot(symbol) is { Best5: var best5 } && best5 != null)
            {
                if (bestBid <= 0 && best5.Bids != null && best5.Bids.Count > 0) bestBid = best5.Bids[0].Price;
                if (bestAsk <= 0 && best5.Asks != null && best5.Asks.Count > 0) bestAsk = best5.Asks[0].Price;
            }

            return bestBid > 0 || bestAsk > 0;
        }

        private decimal ResolveMarketFillPrice(OrderSide side, string symbol)
        {
            // [Fix] Strictly use Best Bid/Ask from Order Book (Snapshot/Best5)
            // Do NOT use Tick's AskAtTrade/BidAtTrade as they often reflect "Last Price" or "Matched Price"
            if (TryGetBestQuote(symbol, out var bid, out var ask))
                return side == OrderSide.Buy ? ask : bid;

            return 0m;
        }

        private void AddToOrderBook(PaperOrderContext order)
        {
            var symbol = order.Request.OriginalSignal?.StockId ?? "";
            var price = order.Request.OriginalSignal?.Price ?? 0m;

            var list = _orderBook.GetOrAdd(symbol, _ => new List<PaperOrderContext>());
            lock (list) list.Add(order);
            _logger.LogInformation("[Paper] Order Queued: {Side} {Symbol} @ {Price} (#{Seq})",
                order.Request.Side, symbol, price, order.OrderId);
        }

        private void ExecuteFill(PaperOrderContext order, decimal fillPrice)
        {
            var stockId = order.Request.OriginalSignal?.StockId ?? "UNKNOWN";

            var fill = new OrderFillEvent
            {
                StockId = stockId,
                FillTime = _clock.Now,
                Side = order.Request.Side,
                FillPrice = fillPrice,
                FillQuantity = order.Request.Quantity,
                OriginalReason = order.Request.OriginalSignal?.Reason,
                OrderId = order.OrderId,
                Account = "Paper", // Explicitly mark as Paper trade
                StrategyId = order.Request.Trace?.StrategyId ?? "Manual#Sim:Paper:All:Default",
                AccountKey = order.Request.Trace?.AccountKey ?? "Sim:Paper:All:Default",
                CorrelationId = order.Request.Trace?.CorrelationId ?? ""
            };
            _eventBus.Publish(fill);

            SendReport(stockId, order.Request.Side, fillPrice, order.Request.Quantity, "D", order.BookNo, order.CorrelationId);

            _logger.LogInformation("[Paper] Order Filled: {Symbol} {Side} @ {Price}", stockId, order.Request.Side, fillPrice);
        }

        private void SendReport(string symbol, OrderSide side, decimal price, long qty, string type, string bookNo, string corrId)
        {
            if (string.IsNullOrWhiteSpace(bookNo))
            {
                _logger.LogError("[Paper] SendReport rejected empty BookNo. Type={Type} Symbol={Symbol} CorrId={CorrId}", type, symbol, corrId);
                throw new InvalidOperationException("PaperTradingGateway SendReport requires BookNo.");
            }

            var sideCode = side == OrderSide.Buy ? "B" : "S";
            var status = type switch
            {
                "N" => "ACCEPTED",
                "D" => "FILLED",
                "C" => "CANCELLED",
                "U" => "UPDATED",
                _ => type
            };

            _logger.LogInformation("[PAPER] type={Type} symbol={Symbol} qty={Qty} price={Price} bookNo={BookNo} corrId={CorrId} status={Status}",
                type, symbol, qty, price, bookNo, corrId, status);

            var timeStr = _clock.Now.ToString("o");
            var reportStr = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6},{7}",
                type, symbol, sideCode, price, qty, bookNo, timeStr, corrId);
            _eventBus.Publish(new BrokerReportEvent(reportStr)
            {
                OccurredAt = new DateTimeOffset(_clock.Now)
            });
        }

        private string GenerateSeqNo() => "PAPER-" + Interlocked.Increment(ref _demoSequence).ToString(CultureInfo.InvariantCulture);

        private static bool IsOrderMatch(PaperOrderContext ctx, string key)
        {
            return ctx.OrderId == key || ctx.BookNo == key || ctx.CorrelationId == key;
        }

        private void LogOrderMatched(string action, PaperOrderContext target)
        {
            _logger.LogInformation("[Paper] {Action} matched OrderId={OrderId} BookNo={BookNo} CorrId={CorrId}",
                action, target.OrderId, target.BookNo, target.CorrelationId);
        }

        private void LogOrderBookNotFound(string action, string key)
        {
            var total = 0;
            var perSymbol = new List<string>();
            foreach (var kvp in _orderBook)
            {
                int count;
                lock (kvp.Value) count = kvp.Value.Count;
                total += count;
                perSymbol.Add($"{kvp.Key}:{count}");
            }

            _logger.LogWarning("[Paper] {Action} not found key={Key} TotalOrders={Total} PerSymbol={PerSymbol}",
                action, key, total, string.Join(", ", perSymbol));
        }

        public void Dispose()
        {
            foreach (var t in _tokens) t.Dispose();
            _tokens.Clear();
        }
    }
}

