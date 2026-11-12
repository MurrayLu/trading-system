using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Domain.Trading;
using System;
using System.Threading.Tasks;

namespace MuNTrade.Infrastructure.Trading.Gateways
{
    /// <summary>
    /// 同一個 process 同時支援 Sim 與 Live：
    ///request.AccountKey 以 "Sim:" 開頭 => Paper
    ///request.AccountKey 以 "Live:" 開頭 => Live
    /// </summary>
    public sealed class RoutingOrderGateway : IOrderGateway
    {
        private readonly IPaperOrderGateway _paper;
        private readonly ILiveOrderGateway? _live; // 允許沒有安裝/沒註冊 broker
        private readonly ILogger<RoutingOrderGateway> _log;

        public RoutingOrderGateway(
            IPaperOrderGateway paper,
            ILogger<RoutingOrderGateway> log,
            ILiveOrderGateway? live = null)
        {
            _paper = paper;
            _live = live;
            _log = log;
        }

        private IOrderGateway Resolve(string? accountKey)
        {
            if (string.IsNullOrWhiteSpace(accountKey))
            {
                return _paper;
            }

            if (accountKey.StartsWith("Sim:", StringComparison.OrdinalIgnoreCase))
                return _paper;

            if (accountKey.StartsWith("Live:", StringComparison.OrdinalIgnoreCase))
            {
                if (_live == null)
                    throw new InvalidOperationException("Live gateway not available (ILiveOrderGateway is not registered).");
                return _live;
            }

            // 防呆：未知前綴，寧可拒單也不要亂送到 LIVE
            throw new InvalidOperationException($"Unknown AccountKey prefix: {accountKey}");
        }

        public Task<string> PlaceStockOrderAsync(OrderRequest request)
            => Resolve(request.AccountKey).PlaceStockOrderAsync(request);

        public Task<string> PlaceFutureOrderAsync(OrderRequest request)
            => Resolve(request.AccountKey).PlaceFutureOrderAsync(request);

        public Task<decimal> GetBuyingPowerAsync(string symbol)
            => _paper.GetBuyingPowerAsync(symbol);

        //IOrderGateway Implementation (Order Management is not supported here) ---

        public Task<string> CancelOrderBySeqNoAsync(string seqNo)
            => ThrowOmsRequired(nameof(CancelOrderBySeqNoAsync));

        public Task<string> CancelOrderByBookNoAsync(string bookNo)
            => ThrowOmsRequired(nameof(CancelOrderByBookNoAsync));

        public Task<string> CancelOrderByStockNoAsync(string symbol)
            => ThrowOmsRequired(nameof(CancelOrderByStockNoAsync));

        public Task<string> CancelOrderByStockNoAdvanceAsync(string symbol, MuNTrade.Abstractions.Domain.Trading.Enums.OrderSide side, decimal? price)
            => ThrowOmsRequired(nameof(CancelOrderByStockNoAdvanceAsync));

        public Task<string> CorrectPriceBySeqNoAsync(string seqNo, decimal newPrice)
            => ThrowOmsRequired(nameof(CorrectPriceBySeqNoAsync));

        public Task<string> CorrectPriceByBookNoAsync(string bookNo, decimal newPrice)
            => ThrowOmsRequired(nameof(CorrectPriceByBookNoAsync));

        public Task<string> DecreaseOrderBySeqNoAsync(string seqNo, int reduceQty)
            => ThrowOmsRequired(nameof(DecreaseOrderBySeqNoAsync));

        //Overseas ---
        public Task<string> OverSeaCancelOrderBySeqNoAsync(string seqNo)
            => ThrowOmsRequired(nameof(OverSeaCancelOrderBySeqNoAsync));

        public Task<string> OverSeaCancelOrderByBookNoAsync(string bookNo)
            => ThrowOmsRequired(nameof(OverSeaCancelOrderByBookNoAsync));

        public Task<string> OverSeaCorrectPriceByBookNoAsync(string bookNo, decimal newPrice)
            => ThrowOmsRequired(nameof(OverSeaCorrectPriceByBookNoAsync));

        public Task<string> OverSeaDecreaseOrderBySeqNoAsync(string seqNo, int reduceQty)
            => ThrowOmsRequired(nameof(OverSeaDecreaseOrderBySeqNoAsync));

        private Task<string> ThrowOmsRequired(string method)
        {
            _log.LogError("[RoutingOrderGateway] {Method} is not supported. Use IOrderManagementService.", method);
            throw new NotSupportedException($"RoutingOrderGateway does not support {method}. Use IOrderManagementService.");
        }
    }
}

