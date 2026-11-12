using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Dtos.Broker;

namespace MuNTrade.Infrastructure.Trading
{
    public sealed class OrderIdentityMap : IOrderIdentityMap, IDisposable
    {
        private readonly ConcurrentDictionary<string, OrderKey> _byKey = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _byClientKey = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _byBookNo = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _byBrokerSeqNo = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _byCorrelationId = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _byPaperOrderId = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _lastUpdateUtc = new(StringComparer.Ordinal);
        private readonly object _sync = new();
        private readonly ILogger<OrderIdentityMap> _logger;
        private readonly IDisposable _subscription;

        public OrderIdentityMap(IEventBus eventBus, ILogger<OrderIdentityMap> logger)
        {
            _logger = logger;
            _subscription = eventBus.Subscribe<BrokerReportDto>(OnReport);
        }

        public OrderKey UpdateFromReport(BrokerReportDto dto)
        {
            if (dto == null) throw new ArgumentNullException(nameof(dto));

            var clientKey = Normalize(dto.ClientKey);
            var bookNo = Normalize(dto.BookNo);
            var seqNo = Normalize(dto.SeqNo);
            var corrId = Normalize(dto.CorrelationId);
            var isPaper = ResolveIsPaper(dto);
            var isFutures = ResolveIsFutures(dto);
            var symbol = dto.Symbol ?? string.Empty;
            var validBookNo = OrderKeyHelpers.IsValidBookNo(bookNo);
            if (!validBookNo) bookNo = null;
            var brokerBookNo = isPaper ? null : bookNo;
            var brokerSeqNo = isPaper ? null : seqNo;
            var paperOrderId = isPaper ? bookNo : null;

            lock (_sync)
            {
                var existingKey = ResolveExistingKey(clientKey, bookNo, brokerSeqNo, corrId, paperOrderId);
                var key = existingKey ?? Guid.NewGuid().ToString("N");

                _byKey.TryGetValue(key, out var existing);

                var merged = new OrderKey
                {
                    Key = key,
                    IsPaper = isPaper || existing?.IsPaper == true,
                    IsFutures = isFutures || existing?.IsFutures == true,
                    ClientKey = clientKey ?? existing?.ClientKey,
                    BrokerBookNo = brokerBookNo ?? existing?.BrokerBookNo,
                    BrokerSeqNo = brokerSeqNo ?? existing?.BrokerSeqNo,
                    PaperOrderId = paperOrderId ?? existing?.PaperOrderId,
                    CorrelationId = corrId ?? existing?.CorrelationId,
                    Symbol = !string.IsNullOrWhiteSpace(symbol) ? symbol : existing?.Symbol ?? string.Empty
                };

                _byKey[key] = merged;

                if (clientKey != null) _byClientKey[clientKey] = key;
                if (bookNo != null) _byBookNo[bookNo] = key;
                if (brokerSeqNo != null) _byBrokerSeqNo[brokerSeqNo] = key;
                if (corrId != null) _byCorrelationId[corrId] = key;
                if (paperOrderId != null) _byPaperOrderId[paperOrderId] = key;
                _lastUpdateUtc[key] = DateTime.UtcNow;

                return merged;
            }
        }

        public bool TryGetByKey(string key, out OrderKey orderKey)
            => _byKey.TryGetValue(key, out orderKey);

        public bool TryGetLastUpdateUtc(string key, out DateTime lastUpdateUtc)
            => _lastUpdateUtc.TryGetValue(key, out lastUpdateUtc);

        public void Dispose()
        {
            _subscription.Dispose();
        }

        private void OnReport(BrokerReportDto dto)
        {
            try
            {
                UpdateFromReport(dto);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OrderIdentityMap] Failed to update from report.");
            }
        }

        private string? ResolveExistingKey(string? clientKey, string? bookNo, string? brokerSeqNo, string? corrId, string? paperOrderId)
        {
            if (clientKey != null && _byClientKey.TryGetValue(clientKey, out var keyFromClient))
                return keyFromClient;
            if (bookNo != null && _byBookNo.TryGetValue(bookNo, out var keyFromBook))
                return keyFromBook;
            if (brokerSeqNo != null && _byBrokerSeqNo.TryGetValue(brokerSeqNo, out var keyFromSeq))
                return keyFromSeq;
            if (corrId != null && _byCorrelationId.TryGetValue(corrId, out var keyFromCorr))
                return keyFromCorr;
            if (paperOrderId != null && _byPaperOrderId.TryGetValue(paperOrderId, out var keyFromPaper))
                return keyFromPaper;
            return null;
        }

        private static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return value.Trim();
        }

        private static bool ResolveIsPaper(BrokerReportDto dto)
        {
            if (!string.IsNullOrWhiteSpace(dto.ReportType))
                return dto.ReportType.Equals("Paper", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(dto.Account))
            {
                return dto.Account.Contains("Paper", StringComparison.OrdinalIgnoreCase)
                       || dto.Account.Contains("Sim", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static bool ResolveIsFutures(BrokerReportDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Market)) return false;

            var market = dto.Market.Trim();
            return market.Contains("\u671F", StringComparison.OrdinalIgnoreCase)
                   || market.Contains("Future", StringComparison.OrdinalIgnoreCase);
        }
    }
}

