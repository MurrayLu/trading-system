using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Domain.Trading;

namespace MuNTrade.Core.Trading
{
    public sealed class OrderManagementService : IOrderManagementService
    {
        private readonly ILiveOrderGateway _live;
        private readonly IPaperOrderGateway _paper;
        private readonly IOrderIdentityMap _identityMap;
        private readonly ILogger<OrderManagementService> _logger;

        public OrderManagementService(
            ILiveOrderGateway live,
            IPaperOrderGateway paper,
            IOrderIdentityMap identityMap,
            ILogger<OrderManagementService> logger)
        {
            _live = live;
            _paper = paper;
            _identityMap = identityMap;
            _logger = logger;
        }

        public async Task<string> CancelAsync(OrderKey orderKey)
        {
            if (orderKey == null) return "ERR:OrderKey is null";

            var resolved = Resolve(orderKey);
            var paperKey = ResolvePaperKey(resolved);
            var gatewayName = resolved.IsPaper ? _paper.GetType().Name : _live.GetType().Name;
            var startedAtUtc = DateTime.UtcNow;

            _logger.LogInformation("[OMS] Cancel requested Key={Key} Paper={Paper} Futures={Futures} Gateway={Gateway} BrokerBookNo={BookNo} BrokerSeqNo={SeqNo} PaperOrderId={PaperId}",
                resolved.Key, resolved.IsPaper, resolved.IsFutures, gatewayName, resolved.BrokerBookNo, resolved.BrokerSeqNo, resolved.PaperOrderId);

            string result;
            if (resolved.IsPaper)
            {
                if (string.IsNullOrEmpty(paperKey))
                    return "ERR:MissingPaperKey";

                result = resolved.IsFutures
                    ? await _paper.OverSeaCancelOrderBySeqNoAsync(paperKey)
                    : await _paper.CancelOrderBySeqNoAsync(paperKey);

                TriggerReportCheck(resolved.Key, "Cancel", startedAtUtc, result);
                return result;
            }

            if (resolved.IsFutures)
            {
                if (!string.IsNullOrEmpty(resolved.BrokerBookNo))
                    result = await _live.OverSeaCancelOrderByBookNoAsync(resolved.BrokerBookNo);
                else if (!string.IsNullOrEmpty(resolved.BrokerSeqNo))
                    result = await _live.OverSeaCancelOrderBySeqNoAsync(resolved.BrokerSeqNo);
                else
                    return "ERR:MissingBrokerKey";
            }
            else
            {
                if (!string.IsNullOrEmpty(resolved.BrokerBookNo))
                    result = await _live.CancelOrderByBookNoAsync(resolved.BrokerBookNo);
                else if (!string.IsNullOrEmpty(resolved.BrokerSeqNo))
                    result = await _live.CancelOrderBySeqNoAsync(resolved.BrokerSeqNo);
                else
                    return "ERR:MissingBrokerKey";
            }

            TriggerReportCheck(resolved.Key, "Cancel", startedAtUtc, result);
            return result;
        }

        public async Task<string> ReduceAsync(OrderKey orderKey, int qty)
        {
            if (orderKey == null) return "ERR:OrderKey is null";
            if (qty <= 0) return "ERR:Reduce qty must be > 0";

            var resolved = Resolve(orderKey);
            var paperKey = ResolvePaperKey(resolved);
            var gatewayName = resolved.IsPaper ? _paper.GetType().Name : _live.GetType().Name;
            var startedAtUtc = DateTime.UtcNow;

            _logger.LogInformation("[OMS] Reduce requested Key={Key} Paper={Paper} Futures={Futures} Gateway={Gateway} BrokerSeqNo={SeqNo} PaperOrderId={PaperId} Qty={Qty}",
                resolved.Key, resolved.IsPaper, resolved.IsFutures, gatewayName, resolved.BrokerSeqNo, resolved.PaperOrderId, qty);

            string result;
            if (resolved.IsPaper)
            {
                if (string.IsNullOrEmpty(paperKey))
                    return "ERR:MissingPaperKey";

                result = resolved.IsFutures
                    ? await _paper.OverSeaDecreaseOrderBySeqNoAsync(paperKey, qty)
                    : await _paper.DecreaseOrderBySeqNoAsync(paperKey, qty);

                TriggerReportCheck(resolved.Key, "Reduce", startedAtUtc, result);
                return result;
            }

            if (string.IsNullOrEmpty(resolved.BrokerSeqNo))
                return "ERR:MissingBrokerKey";

            result = resolved.IsFutures
                ? await _live.OverSeaDecreaseOrderBySeqNoAsync(resolved.BrokerSeqNo, qty)
                : await _live.DecreaseOrderBySeqNoAsync(resolved.BrokerSeqNo, qty);

            TriggerReportCheck(resolved.Key, "Reduce", startedAtUtc, result);
            return result;
        }

        public async Task<string> ReplacePriceAsync(OrderKey orderKey, decimal newPrice)
        {
            if (orderKey == null) return "ERR:OrderKey is null";

            var resolved = Resolve(orderKey);
            var paperKey = ResolvePaperKey(resolved);
            var gatewayName = resolved.IsPaper ? _paper.GetType().Name : _live.GetType().Name;
            var startedAtUtc = DateTime.UtcNow;

            _logger.LogInformation("[OMS] ReplacePrice requested Key={Key} Paper={Paper} Futures={Futures} Gateway={Gateway} BrokerBookNo={BookNo} BrokerSeqNo={SeqNo} PaperOrderId={PaperId} Price={Price}",
                resolved.Key, resolved.IsPaper, resolved.IsFutures, gatewayName, resolved.BrokerBookNo, resolved.BrokerSeqNo, resolved.PaperOrderId, newPrice);

            string result;
            if (resolved.IsPaper)
            {
                if (string.IsNullOrEmpty(paperKey))
                    return "ERR:MissingPaperKey";

                result = resolved.IsFutures
                    ? await _paper.OverSeaCorrectPriceByBookNoAsync(paperKey, newPrice)
                    : await _paper.CorrectPriceBySeqNoAsync(paperKey, newPrice);

                TriggerReportCheck(resolved.Key, "ReplacePrice", startedAtUtc, result);
                return result;
            }

            if (resolved.IsFutures)
            {
                if (string.IsNullOrEmpty(resolved.BrokerBookNo))
                    return "ERR:MissingBrokerKey";

                result = await _live.OverSeaCorrectPriceByBookNoAsync(resolved.BrokerBookNo, newPrice);
            }
            else
            {
                if (!string.IsNullOrEmpty(resolved.BrokerBookNo))
                    result = await _live.CorrectPriceByBookNoAsync(resolved.BrokerBookNo, newPrice);
                else if (!string.IsNullOrEmpty(resolved.BrokerSeqNo))
                    result = await _live.CorrectPriceBySeqNoAsync(resolved.BrokerSeqNo, newPrice);
                else
                    return "ERR:MissingBrokerKey";
            }

            TriggerReportCheck(resolved.Key, "ReplacePrice", startedAtUtc, result);
            return result;
        }

        private OrderKey Resolve(OrderKey input)
        {
            if (!string.IsNullOrEmpty(input.Key) && _identityMap.TryGetByKey(input.Key, out var mapped))
                return mapped;

            return input;
        }

        private static string? ResolvePaperKey(OrderKey orderKey)
        {
            if (!string.IsNullOrWhiteSpace(orderKey.PaperOrderId)) return orderKey.PaperOrderId;
            return null;
        }

        private void TriggerReportCheck(string key, string op, DateTime startedAtUtc, string result)
        {
            if (!result.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                return;

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (_identityMap.TryGetLastUpdateUtc(key, out var last) && last > startedAtUtc)
                    return;

                _logger.LogWarning("[OMS] Operation returned OK but no report update observed for Key={Key} Op={Op}", key, op);
            });
        }
    }
}

