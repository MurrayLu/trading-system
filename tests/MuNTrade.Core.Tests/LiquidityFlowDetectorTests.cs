using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.OrderBook;
using MuNTrade.Core.OrderBook;
using MuNTrade.Core.Settings;
using MuNTrade.Core.Trading.Slice1;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Contracts.Time;
using Xunit;

namespace MuNTrade.Core.Tests
{
    public class LiquidityFlowDetectorTests
    {
        private sealed class FixedClock : IClock
        {
            public DateTime Now => DateTime.UtcNow;
            public void AdvanceTo(DateTime newTimestamp) { }
        }
        private sealed class DummyOptionsMonitor<T> : IOptionsMonitor<T>
        {
            public T CurrentValue { get; }
            public DummyOptionsMonitor(T val) => CurrentValue = val;
            public T Get(string name) => CurrentValue;
            public IDisposable OnChange(Action<T, string> listener) => new DisposableStub();
            private class DisposableStub : IDisposable { public void Dispose() { } }
        }

        private LiquidityFlowDetector CreateSut()
        {
            var opt = new OrderBookProcessingOptions();
            var monitor = new DummyOptionsMonitor<OrderBookProcessingOptions>(opt);
            return new LiquidityFlowDetector(NullLogger<LiquidityFlowDetector>.Instance, monitor);
        }

        private LiquidityTimeGuards StrictGuards => new LiquidityTimeGuards(
            ExposeMs: 200, ShiftMs: 200, RestMs: 300, BacktrackMs: 500, RemovalDelayMs: 300
        );

        [Fact]
        public void ExistenceRule_BlockAddOnSameTick()
        {
            var sut = CreateSut();
            var now = new FixedClock().Now;
            var symbol = "TX00";

            var evts = sut.Process(symbol, now,
                new List<PriceLevel> { new(100m, 10) },
                new List<PriceLevel>(),
                new Dictionary<decimal, double>(),
                addFloor: 5, pullFloor: 5, isBroken: false, p95Spread: 1, guards: StrictGuards, flowStats: FlowWindowStats.Zero);

            var suppressed = Assert.IsType<BigHangEvent>(Assert.Single(evts));
            Assert.True(suppressed.IsSuppressed);
            Assert.Equal("NewLevel", suppressed.Reason.ToString());
        }

        [Fact]
        public void ShiftRule_BlockAddDuringShift()
        {
            var sut = CreateSut();
            var now = new FixedClock().Now;
            var symbol = "TX00";

            sut.Process(symbol, now,
                new List<PriceLevel> { new(100m, 100) },
                new List<PriceLevel>(),
                new Dictionary<decimal, double>(),
                5, 5, false, 1, StrictGuards, FlowWindowStats.Zero);

            var t1 = now.AddMilliseconds(50);
            var evts = sut.Process(symbol, t1,
                new List<PriceLevel> { new(101m, 150) },
                new List<PriceLevel>(),
                new Dictionary<decimal, double>(),
                5, 5, false, 1, StrictGuards, FlowWindowStats.Zero);

            Assert.Empty(evts);
        }

        [Fact]
        public void RestingRule_BlockPullTooYoung()
        {
            var sut = CreateSut();
            var now = new FixedClock().Now;
            var symbol = "TX00";

            sut.Process(symbol, now,
                new List<PriceLevel> { new(100m, 100) },
                new List<PriceLevel>(),
                new Dictionary<decimal, double>(),
                5, 5, false, 1, StrictGuards, FlowWindowStats.Zero);

            var t1 = now.AddMilliseconds(100);
            var evts = sut.Process(symbol, t1,
                new List<PriceLevel> { new(100m, 50) },
                new List<PriceLevel>(),
                new Dictionary<decimal, double>(),
                5, 5, false, 1, StrictGuards, FlowWindowStats.Zero);

            var suppressed = Assert.IsType<BigPullEvent>(Assert.Single(evts));
            Assert.True(suppressed.IsSuppressed);
            Assert.Equal("Young", suppressed.Reason.ToString());
        }

        [Fact]
        public void BacktrackRule_BlockPullAfterRecentAdd()
        {
            var sut = CreateSut();
            var now = new FixedClock().Now;
            var symbol = "TX00";

            sut.Process(symbol, now,
                new List<PriceLevel> { new(100m, 100) },
                new List<PriceLevel>(),
                new Dictionary<decimal, double>(),
                5, 5, false, 1, StrictGuards, FlowWindowStats.Zero);

            var t1 = now.AddMilliseconds(300);
            var evts1 = sut.Process(symbol, t1,
                new List<PriceLevel> { new(100m, 150) },
                new List<PriceLevel>(),
                new Dictionary<decimal, double>(),
                5, 5, false, 1, StrictGuards, FlowWindowStats.Zero);
            Assert.Single(evts1);

            var t2 = t1.AddMilliseconds(100);
            var evts2 = sut.Process(symbol, t2,
                new List<PriceLevel> { new(100m, 100) },
                new List<PriceLevel>(),
                new Dictionary<decimal, double>(),
                5, 5, false, 1, StrictGuards, FlowWindowStats.Zero);

            var suppressed = Assert.IsType<BigPullEvent>(Assert.Single(evts2));
            Assert.True(suppressed.IsSuppressed);
            Assert.Equal("Absorbed", suppressed.Reason.ToString());
        }

        [Fact]
        public void ValidAdd_AfterExpose()
        {
            var sut = CreateSut();
            var now = new FixedClock().Now;
            var symbol = "TX00";

            sut.Process(symbol, now, new List<PriceLevel> { new(100m, 100) },
                new List<PriceLevel>(), new Dictionary<decimal, double>(), 5, 5, false, 1, StrictGuards, FlowWindowStats.Zero);

            var t1 = now.AddMilliseconds(300);
            var evts = sut.Process(symbol, t1,
                new List<PriceLevel> { new(100m, 150) },
                new List<PriceLevel>(), new Dictionary<decimal, double>(), 5, 5, false, 1, StrictGuards, FlowWindowStats.Zero);

            Assert.Single(evts);
            Assert.IsType<BigHangEvent>(evts[0]);
        }
    }
}

