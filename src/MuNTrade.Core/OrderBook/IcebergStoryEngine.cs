using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Events.OrderBook.Iceberg;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Contracts.Hubs;

namespace MuNTrade.Core.OrderBook
{
    public class IcebergStoryEngine
    {
        private readonly IEventBus _bus;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<(string, bool, decimal), LinkedList<string>> _histories = new();
        private const int MaxHistory = 5;

        public IcebergStoryEngine(IEventBus bus, ILogger<IcebergStoryEngine> logger)
        {
            _bus = bus;
            _logger = logger;

            _bus.Subscribe<IcebergConfirmedEvent>(OnConfirmed);
            _bus.Subscribe<IcebergDefendedEvent>(OnDefended);
            _bus.Subscribe<IcebergEscalatedEvent>(OnEscalated);
            _bus.Subscribe<IcebergBreachedEvent>(OnBreached);
            _bus.Subscribe<IcebergRetreatedEvent>(OnRetreated);
            _bus.Subscribe<IcebergClearedEvent>(OnCleared);
        }

        private void OnConfirmed(IcebergConfirmedEvent e) => AppendAndCheck(e.Symbol, e.IsBid, e.Price, "Confirmed", e.ArrivalTimestamp);
        private void OnDefended(IcebergDefendedEvent e) => AppendAndCheck(e.Symbol, e.IsBid, e.Price, "Defended", e.ArrivalTimestamp);
        private void OnEscalated(IcebergEscalatedEvent e) => AppendAndCheck(e.Symbol, e.IsBid, e.Price, "Escalated", e.ArrivalTimestamp);
        private void OnBreached(IcebergBreachedEvent e) => AppendAndCheck(e.Symbol, e.IsBid, e.Price, "Breached", e.ArrivalTimestamp);
        private void OnRetreated(IcebergRetreatedEvent e) => AppendAndCheck(e.Symbol, e.IsBid, e.Price, "Retreated", e.ArrivalTimestamp);
        private void OnCleared(IcebergClearedEvent e) => _histories.TryRemove((e.Symbol, e.IsBid, e.Price), out _);

        private void AppendAndCheck(string symbol, bool isBid, decimal price, string eventType, MuNTrade.Abstractions.Time.ArrivalTs time)
        {
            var key = (symbol, isBid, price);
            var history = _histories.GetOrAdd(key, k => new LinkedList<string>());

            lock (history)
            {
                if (history.Count >= MaxHistory) history.RemoveFirst();
                history.AddLast(eventType);

                CheckPatterns(symbol, isBid, price, history, time);
            }
        }

        private readonly ConcurrentDictionary<(string, bool, decimal, string), MuNTrade.Abstractions.Time.ArrivalTs> _throttle = new();

        private bool ShouldThrottle(string symbol, bool isBid, decimal price, string story, MuNTrade.Abstractions.Time.ArrivalTs now)
        {
            var key = (symbol, isBid, price, story);
            if (_throttle.TryGetValue(key, out var last))
            {
                if ((now - last).TotalSeconds < 5.0) return true;
            }
            _throttle[key] = now;
            return false;
        }

        private void CheckPatterns(string symbol, bool isBid, decimal price, LinkedList<string> history, MuNTrade.Abstractions.Time.ArrivalTs time)
        {
            // Convert to list for easier matching
            var seq = history.ToList();
            if (seq.Count < 2) return;

            // Pattern 1: Blowoff (Pressure Cooker)
            // Confirmed -> ... -> Escalated -> Defended -> Breached
            if (EndsWith(seq, "Breached"))
            {
                if (seq.Contains("Escalated") && seq.Contains("Defended") && seq.Contains("Confirmed"))
                {
                    // Simple check: Last is Breached. 
                    // Check if Escalated occurred before.
                    if (ShouldThrottle(symbol, isBid, price, "IcebergBlowoff", time)) return;
                    _bus.Publish(new IcebergStoryEvent(symbol, price, isBid, time, "IcebergBlowoff", "Pressure Cooker Explosion", 1.0)
                    {
                        OccurredAt = new DateTimeOffset(time.Value)
                    });
                    return;
                }
            }

            // Pattern 2: Fakeout
            // Confirmed -> Defended -> Retreated
            if (EndsWith(seq, "Retreated"))
            {
                // Must have Defended then Retreated, WITHOUT Breached
                if (seq.Contains("Defended") && !seq.Contains("Breached"))
                {
                    if (ShouldThrottle(symbol, isBid, price, "IcebergFakeout", time)) return;
                    _bus.Publish(new IcebergStoryEvent(symbol, price, isBid, time, "IcebergFakeout", "Defended then Retreated", 0.8)
                    {
                        OccurredAt = new DateTimeOffset(time.Value)
                    });
                    return;
                }
            }
        }

        private bool EndsWith(List<string> seq, string type)
        {
            return seq.Count > 0 && seq[^1] == type;
        }
    }
}

