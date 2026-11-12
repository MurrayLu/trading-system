using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Events; // Added
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Core.Settings;
using MuNTrade.Core.Trading.Slice1;

namespace MuNTrade.Core.OrderBook
{
    public class OrderBookReplayHarness
    {
        private readonly OrderBookEngine _engine;
        private readonly CapturingEventBus _bus;
        private readonly MockSessionProvider _sessionProvider;
        private readonly IOptionsMonitor<OrderBookProcessingOptions> _options;

        public OrderBookReplayHarness(OrderBookEngineOptions? options = null)
        {
            _bus = new CapturingEventBus();
            var logger = NullLogger<OrderBookEngine>.Instance;
            _options = new MockOptionsMonitor(options ?? new OrderBookEngineOptions());
            _sessionProvider = new MockSessionProvider();

            _sessionProvider = new MockSessionProvider();

            var clock = new MockClock();
            _engine = new OrderBookEngine(_bus, logger, _options, _sessionProvider, clock, NullLoggerFactory.Instance, new MockPriceTierService());
        }

        public void Run(IEnumerable<object> events)
        {
            // Sort events by ArrivalTs to ensure strict time ordering
            var sortedEvents = events.OrderBy(e => GetArrivalTs(e)).ToList();

            foreach (var evt in sortedEvents)
            {
                if (evt is TradeTickEvent trade)
                {
                    _engine.OnTrade(trade);
                }
                else if (evt is Best5DeltaEvent best5)
                {
                    _engine.OnBest5(best5);
                }
                else if (evt is JoinerHealthEvent joiner)
                {
                    _engine.OnJoinerHealth(joiner);
                }
            }
        }
        // ...
        // Please DO NOT replace everything.
        // The tool says SINGLE CONTIGUOUS block.
        // I will split.
        public void RunTwiceAndCompare(IEnumerable<object> events)
        {
            // Run 1
            var harness1 = new OrderBookReplayHarness(_options.CurrentValue.Engine);
            harness1.Run(events);
            var results1 = harness1.GetEvents<object>();

            // Run 2
            var harness2 = new OrderBookReplayHarness(_options.CurrentValue.Engine);
            harness2.Run(events);
            var results2 = harness2.GetEvents<object>();

            // Compare
            if (results1.Count != results2.Count)
                throw new Exception($"Determinism failed: Count mismatch {results1.Count} vs {results2.Count}");

            for (int i = 0; i < results1.Count; i++)
            {
                var e1 = results1[i];
                var e2 = results2[i];
                if (!e1.Equals(e2))
                    throw new Exception($"Determinism failed at index {i}: {e1} vs {e2}");
            }
        }

        public void VerifyKillSwitchLogic(IEnumerable<object> events)
        {
            Run(events);
            var captured = GetEvents<object>();

            foreach (var evt in captured)
            {
                if (evt is OrderBookQualityEvent q)
                {
                    if (q.Tier == OrderBookQualityTier.Bad)
                    {
                        // Check for any forbidden events with same timestamp
                        var forbidden = captured.Where(e => GetArrivalTs(e) == q.ArrivalTimestamp.Value && IsDetectorEvent(e)).ToList();
                        if (forbidden.Any())
                            throw new Exception($"Kill Switch Failed: Found detector events during Bad tier at {q.ArrivalTimestamp}");
                    }
                    else if (q.Tier == OrderBookQualityTier.Degraded)
                    {
                        // Suppress Iceberg/Cum
                        var forbidden = captured.Where(e => GetArrivalTs(e) == q.ArrivalTimestamp.Value && IsSuppressedInDegraded(e)).ToList();
                        if (forbidden.Any())
                            throw new Exception($"Kill Switch Failed: Found suppressed events during Degraded tier at {q.ArrivalTimestamp}");

                        // Verify Big* events have SuppressedByQuality = true
                        var bigEvents = captured.Where(e => GetArrivalTs(e) == q.ArrivalTimestamp.Value && (e is BigHangEvent || e is BigPullEvent)).ToList();
                        foreach (var big in bigEvents)
                        {
                            bool isSuppressed = false;
                            if (big is BigHangEvent bh) isSuppressed = bh.SuppressedByQuality;
                            if (big is BigPullEvent bp) isSuppressed = bp.SuppressedByQuality;

                            if (!isSuppressed)
                                throw new Exception($"Kill Switch Failed: Big event at {q.ArrivalTimestamp} in Degraded tier was NOT marked as SuppressedByQuality");
                        }
                    }
                }
            }
        }

        private bool IsDetectorEvent(object e)
        {
            return e is IcebergSuspectEvent || e is IcebergConfirmedEvent ||
                   e is CumHangEvent || e is CumPullEvent || e is CumReplenishEvent ||
                   e is BigHangEvent || e is BigPullEvent;
        }

        private bool IsSuppressedInDegraded(object e)
        {
            return e is IcebergSuspectEvent || e is IcebergConfirmedEvent ||
                   e is CumHangEvent || e is CumPullEvent || e is CumReplenishEvent;
        }

        public List<T> GetEvents<T>()
        {
            return _bus.GetEvents<T>();
        }

        private DateTime GetArrivalTs(object evt)
        {
            if (evt is TradeTickEvent t) return t.ArrivalTs.Value;
            if (evt is Best5DeltaEvent b) return b.ArrivalTs.Value;
            if (evt is OrderBookQualityEvent q) return q.ArrivalTimestamp.Value;
            if (evt is IcebergSuspectEvent i) return i.ArrivalTimestamp.Value;
            if (evt is JoinerHealthEvent j) return j.Timestamp.Value;
            if (evt is BigHangEvent bh) return bh.ArrivalTimestamp.Value;
            if (evt is BigPullEvent bp) return bp.ArrivalTimestamp.Value;
            if (evt is CumHangEvent ch) return ch.Ts;
            if (evt is CumPullEvent cp) return cp.Ts;
            if (evt is CumReplenishEvent cr) return cr.Ts;
            if (evt is IcebergConfirmedEvent ic) return ic.ArrivalTimestamp.Value;

            return DateTime.MaxValue;
        }

        // --- Mocks ---

        private class CapturingEventBus : IEventBus
        {
            private readonly List<object> _captured = new();

            public void Publish<TEvent>(TEvent @event) where TEvent : class
            {
                _captured.Add(@event);
            }

            public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
            {
                return new DisposableAction(() => { });
            }

            public List<T> GetEvents<T>()
            {
                return _captured.OfType<T>().ToList();
            }
        }

        private class DisposableAction : IDisposable
        {
            private readonly Action _action;
            public DisposableAction(Action action) => _action = action;
            public void Dispose() => _action();
        }

        private class MockOptionsMonitor : IOptionsMonitor<OrderBookProcessingOptions>
        {
            public OrderBookProcessingOptions CurrentValue { get; }

            public MockOptionsMonitor(OrderBookEngineOptions engineOpts)
            {
                CurrentValue = new OrderBookProcessingOptions { Engine = engineOpts };
            }

            public OrderBookProcessingOptions Get(string? name) => CurrentValue;
            public IDisposable OnChange(Action<OrderBookProcessingOptions, string> listener) => new DisposableAction(() => { });
        }

        private class MockSessionProvider : ITradingSessionProvider
        {
            public TradingSession? GetSession(string symbol, DateTime exchangeTs)
            {
                // Always return a valid session for testing
                // Constructor: (DateTime st, DateTime et, TimeSpan anchor, TimeSpan auctionStart, TimeSpan auctionExt, DateOnly tradeDate)
                var date = DateOnly.FromDateTime(exchangeTs.Date);
                return new TradingSession(
                    exchangeTs.Date.AddHours(8).AddMinutes(45),
                    exchangeTs.Date.AddHours(13).AddMinutes(45),
                    TimeSpan.FromHours(9),
                    TimeSpan.FromHours(13).Add(TimeSpan.FromMinutes(25)),
                    TimeSpan.FromMinutes(1),
                    date
                );
            }

            public (DateOnly? first, DateOnly? last) GetFirstAndLastTradingDateInRange(string stockId, DateTime dateFrom, DateTime dateTo, TimeOnly? probeTime = null)
            {
                return (DateOnly.FromDateTime(dateFrom), DateOnly.FromDateTime(dateTo));
            }

            public DateOnly? FindPreviousTradingDate(string stockId, DateOnly day, int maxLookbackDays = 30)
            {
                return day.AddDays(-1);
            }

            public bool IsTradingDate(string stockId, DateOnly date)
            {
                return true;
            }

            public PeriodWindow GetPeriodWindow(string stockId, BarConfig cfg, DateTime nowLocal)
            {
                return new PeriodWindow(nowLocal, nowLocal.AddMinutes(5), DateOnly.FromDateTime(nowLocal), DateOnly.FromDateTime(nowLocal));
            }

        }

        private class MockPriceTierService : MuNTrade.Abstractions.Contracts.Services.IPriceTierService
        {
            public decimal GetTickSizeAtPrice(decimal price) => 1.0m;
            public decimal GetTickPrice(decimal price) => 1.0m;
            public decimal GetNextUpPrice(decimal price) => price + 1.0m;
            public decimal GetNextDownPrice(decimal price) => price > 1.0m ? price - 1.0m : 0m;
            public decimal MoveByTicks(decimal price, int ticks) => price + ticks;
        }

        private sealed class MockClock : IClock
        {
            public DateTime Now => DateTime.Now;
            public void AdvanceTo(DateTime newTimestamp) { }
        }
    }
}

