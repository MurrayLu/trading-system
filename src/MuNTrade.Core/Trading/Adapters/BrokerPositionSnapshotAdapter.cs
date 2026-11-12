using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
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
using MuNTrade.Abstractions.Contracts.Time;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MuNTrade.Core.Trading.Adapters
{
    // [Patch 4] Modified to handle BrokerPositionQueryCompletedEvent and force final flush
    public sealed class BrokerPositionSnapshotAdapter : IDisposable
    {
        private readonly IEventBus _bus;
        private readonly ILogger<BrokerPositionSnapshotAdapter> _logger;
        private readonly IClock _clock;

        // Accumulate positions by (Account, MarketKind, SyncId)
        // We do not use Timer anymore, relaying on QueryCompleted event.
        private readonly ConcurrentDictionary<(string Account, MarketKind Kind, string? SyncId), List<PositionDto>> _buffers = new();
        private readonly List<IDisposable> _tokens = new();

        public BrokerPositionSnapshotAdapter(IEventBus bus, ILogger<BrokerPositionSnapshotAdapter> logger, IClock clock)
        {
            _bus = bus;
            _logger = logger;
            _clock = clock;

            _tokens.Add(_bus.Subscribe<StockInventoryEvent>(OnStockInventory));
            _tokens.Add(_bus.Subscribe<OpenInterestEvent>(OnOpenInterest));
            _tokens.Add(_bus.Subscribe<BrokerPositionQueryCompletedEvent>(OnQueryCompleted));
        }

        private void OnQueryCompleted(BrokerPositionQueryCompletedEvent evt)
        {
            // Resolve Key
            var key = MuNTrade.Core.Capital.CapitalConfigService.ResolveAccountKey(evt.Account);
            _logger.LogInformation("[BrokerSnapshot] Query Completed: {Kind} for {Account}->{Key} (SyncId={SyncId})", evt.MarketKind, evt.Account, key, evt.CorrelationId);
            FlushBuffer(key, evt.Account, evt.MarketKind, evt.CorrelationId, isFinal: true);
        }

        private void OnStockInventory(StockInventoryEvent evt)
        {
            var side = evt.InventoryType.Equals("L", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Sell
                : OrderSide.Buy;

            var qty = Math.Abs(evt.RealtimeQty);

            // Resolve Key immediately
            var key = MuNTrade.Core.Capital.CapitalConfigService.ResolveAccountKey(evt.AccountNo);

            BufferPosition(key, evt.AccountNo, MarketKind.Securities, evt.CorrelationId, new PositionDto
            {
                Account = key, // DTO also needs update? PositionDto.Account usually was just string. Let's assume it should hold the Key now.
                Symbol = evt.StockId,
                Side = side,
                Quantity = qty,
                AverageCost = 0m,
                LastPrice = 0m
            });
        }

        private void OnOpenInterest(OpenInterestEvent evt)
        {
            var side = evt.Side.StartsWith("B", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Buy
                : OrderSide.Sell;

            // Resolve Key
            var key = MuNTrade.Core.Capital.CapitalConfigService.ResolveAccountKey(evt.Account);

            BufferPosition(key, evt.Account, MarketKind.Derivatives, evt.CorrelationId, new PositionDto
            {
                Account = key,
                Symbol = evt.Symbol,
                Side = side,
                Quantity = Math.Abs(evt.OpenInterest),
                AverageCost = evt.AverageCost,
                LastPrice = 0m
            });
        }

        // Buffer Key is now (AccountKey, Kind, SyncId)
        private void BufferPosition(string accountKey, string brokerAccount, MarketKind kind, string? syncId, PositionDto pos)
        {
            // Normalize just in case, though usually resolved already
            if (string.IsNullOrWhiteSpace(accountKey)) accountKey = "Unknown";

            var key = (accountKey, kind, syncId);

            _buffers.AddOrUpdate(key,
                k => new List<PositionDto> { pos },
                (k, list) =>
                {
                    lock (list)
                    {
                        list.Add(pos);
                    }
                    return list;
                });
        }

        private void FlushBuffer(string accountKey, string brokerAccountNo, MarketKind kind, string? syncId, bool isFinal)
        {
            List<PositionDto> snapshot = new();
            if (_buffers.TryRemove((accountKey, kind, syncId), out var list))
            {
                lock (list)
                {
                    snapshot = list.ToList();
                }
            }

            if (snapshot.Count == 0 && !isFinal) return;

            if (snapshot.Count > 0)
            {
                snapshot = snapshot
                    .GroupBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Last())
                    .ToList();
            }

            // Masking for Log
            string masked = "MASKED";
            if (!string.IsNullOrEmpty(brokerAccountNo) && brokerAccountNo.Length > 4)
                masked = "*" + brokerAccountNo.Substring(brokerAccountNo.Length - 4);
            else masked = brokerAccountNo ?? "N/A";

            _logger.LogInformation("[BrokerSnapshot] Publishing Snapshot: {Count} items for Key={Key} (Acc={Masked}) {Kind} (SyncId={SyncId}, Final={Final})",
                snapshot.Count, accountKey, masked, kind, syncId, isFinal);

            _bus.Publish(new BrokerPositionSnapshotEvent
            {
                AccountKey = accountKey,
                BrokerAccountNo = brokerAccountNo, // Trace only
                Positions = snapshot,
                IsFinal = isFinal,
                Scope = kind,
                CorrelationId = syncId,
                OccurredAt = new DateTimeOffset(_clock.Now)
            });
        }

        public void Dispose()
        {
            foreach (var t in _tokens) t.Dispose();
        }
    }
}

