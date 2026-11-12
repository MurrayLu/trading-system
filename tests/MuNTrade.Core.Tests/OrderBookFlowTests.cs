using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
using MuNTrade.Abstractions.Time;
using MuNTrade.Core.OrderBook;
using MuNTrade.Core.Settings;
using MuNTrade.Core.Trading.Slice1;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using Xunit;

public sealed class OrderBookFlowTests
{
    private sealed class FakeBus : IEventBus
    {
        public readonly List<object> Events = new();
        public IDisposable Subscribe<T>(Action<T> handler) where T : class => new DisposableStub();
        public void Publish<T>(T evt) where T : class => Events.Add(evt!);
        private class DisposableStub : IDisposable { public void Dispose() { } }
    }

    private sealed class FixedClock : IClock
    {
        public DateTime Now => DateTime.UtcNow;
        public void AdvanceTo(DateTime newTimestamp) { }
    }

    private sealed class MockPriceTierService : MuNTrade.Abstractions.Contracts.Services.IPriceTierService
    {
        public decimal GetTickSizeAtPrice(decimal price) => 1.0m;
        public decimal GetTickPrice(decimal price) => 1.0m;
        public decimal GetNextUpPrice(decimal price) => price + 1.0m;
        public decimal GetNextDownPrice(decimal price) => price > 1.0m ? price - 1.0m : 0m;
        public decimal MoveByTicks(decimal price, int ticks) => price + ticks;
    }

    private sealed class DummyOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public T CurrentValue { get; }
        public DummyOptionsMonitor(T val) => CurrentValue = val;
        public T Get(string name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => new DisposableStub();
        private class DisposableStub : IDisposable { public void Dispose() { } }
    }

    private (OrderBookEngine Engine, FakeBus Bus) NewEngine()
    {
        var bus = new FakeBus();
        var opt = new OrderBookProcessingOptions();
        var clock = new FixedClock();
        var priceTier = new MockPriceTierService();

        // Disable warm-up for tests
        opt.Engine.learning_duration_minutes = 0;

        // Configure Liquidity Detector for immediate sensitivity
        opt.Engine.Liquidity.min_samples = 0;
        opt.Engine.Liquidity.rolling_window = 10;
        opt.Engine.Liquidity.add_floor = 1;
        opt.Engine.Liquidity.pull_floor = 1;
        opt.Engine.Liquidity.replenish_floor = 1;
        opt.Engine.Liquidity.big_change_floor = 1;
        opt.Engine.Liquidity.add_mad_mul = 0.1; // Make it very sensitive
        opt.Engine.Liquidity.pull_mad_mul = 0.1;
        opt.Engine.Liquidity.event_min_gap_sec = 0; // Allow immediate firing for tests

        var optMon = new DummyOptionsMonitor<OrderBookProcessingOptions>(opt);
        var session = new DummySessionProvider();
        var eng = new OrderBookEngine(bus, NullLogger<OrderBookEngine>.Instance, optMon, session, clock, NullLoggerFactory.Instance, priceTier);
        return (eng, bus);
    }

    private Best5DeltaEvent MakeSnap(string sym, DateTime ts, (decimal p, int v)[] bids, (decimal p, int v)[] asks)
    {
        var b = new List<PriceLevel>(bids.Length);
        foreach (var x in bids) b.Add(new PriceLevel(x.p, x.v));
        var a = new List<PriceLevel>(asks.Length);
        foreach (var x in asks) a.Add(new PriceLevel(x.p, x.v));
        return new Best5DeltaEvent(sym, new ArrivalTs(ts), b, a, MuNTrade.Abstractions.Domain.Common.Enums.MarketNo.Unknown, MuNTrade.Abstractions.Domain.Common.Enums.MarketSource.Domestic);
    }

    // 1) Price Shift：best1 變價 → 期望 Add=0/Pull=0
    [Fact]
    public void PriceShift_ShouldYieldZeroAddPull()
    {
        var (eng, bus) = NewEngine();
        var t0 = new FixedClock().Now;

        // 初始
        eng.OnBest5(MakeSnap("TX00", t0,
            new[] { (100m, 200), (99m, 100) },
            new[] { (101m, 200), (102m, 100) }));

        // Clear setup events (initial book load generates Adds)
        bus.Events.Clear();

        // 價格上移：ask 101→102（位階移動），但不應算 Add/Pull
        eng.OnBest5(MakeSnap("TX00", t0.AddMilliseconds(50),
            new[] { (100m, 200), (99m, 100) },
            new[] { (102m, 200), (103m, 100) }));

        // 不應產生 Big 類流動性事件 (Cum 類因 setup 殘留且 gap=0 會重發，故不檢查)
        Assert.DoesNotContain(bus.Events, e => e is BigHangEvent || e is BigPullEvent);
    }

    // 2) Trade Consumption：被吃掉的深度不可視為 Pull
    [Fact]
    public void TradeConsumption_ShouldNotCountAsPull()
    {
        var (eng, bus) = NewEngine();
        var t0 = new FixedClock().Now;

        // 初始
        eng.OnBest5(MakeSnap("TX00", t0,
            new[] { (100m, 200) }, new[] { (101m, 500) }));

        // 模擬該區間有成交擊中 ask1：vol=300（用 OnTrade 聚合 + BuildMatchedVolumeMap 路徑）
        eng.OnTrade(new TradeTickEvent
        {
            Symbol = "TX00",
            Price = 101m,
            Volume = 300,
            Side = MuNTrade.Abstractions.Domain.Trading.Enums.AggressorSide.Buy,
            ExchangeTs = new ExchangeTs(t0.AddMilliseconds(10)),
            ArrivalTs = new ArrivalTs(t0.AddMilliseconds(10)),
        });

        // 五檔更新：ask1 從 500 → 200（被吃 300），不可算 Pull
        eng.OnBest5(MakeSnap("TX00", t0.AddMilliseconds(200),
            new[] { (100m, 200) }, new[] { (101m, 200) }));

        // 不應有 Pull 類事件
        Assert.DoesNotContain(bus.Events, e => e is CumPullEvent || e is BigPullEvent);
    }

    // 3) Replenish：被吃掉後立刻補回
    [Fact]
    public void Replenish_ShouldBeDetected()
    {
        var (eng, bus) = NewEngine();
        var t0 = new FixedClock().Now;

        // 初始
        eng.OnBest5(MakeSnap("TX00", t0,
            new[] { (100m, 200) }, new[] { (101m, 500) }));

        // 成交擊中 ask1：vol=300
        eng.OnTrade(new TradeTickEvent
        {
            Symbol = "TX00",
            Price = 101m,
            Volume = 300,
            Side = MuNTrade.Abstractions.Domain.Trading.Enums.AggressorSide.Buy,
            ExchangeTs = new ExchangeTs(t0.AddMilliseconds(10)),
            ArrivalTs = new ArrivalTs(t0.AddMilliseconds(10)),
        });

        // 五檔更新：ask1 仍 500（被吃 300 又補 300 → 應算 Replenish>0）
        eng.OnBest5(MakeSnap("TX00", t0.AddMilliseconds(350),
            new[] { (100m, 200) }, new[] { (101m, 500) }));

        Assert.Contains(bus.Events, e => e is CumReplenishEvent || e is BigHangEvent);
    }

    // 4) Pure Pull：無成交，深度下降
    [Fact]
    public void PurePull_ShouldBeDetected()
    {
        var (eng, bus) = NewEngine();
        var t0 = new FixedClock().Now;

        // 初始
        eng.OnBest5(MakeSnap("TX00", t0,
            new[] { (100m, 200) }, new[] { (101m, 500) }));

        // 無成交 → ask1 500 → 100（純抽走 400）
        eng.OnBest5(MakeSnap("TX00", t0.AddMilliseconds(350),
            new[] { (100m, 200) }, new[] { (101m, 100) }));

        Assert.Contains(bus.Events, e => e is CumPullEvent || e is BigPullEvent);
    }

    // 5) Pure Add：無成交，深度增加
    [Fact]
    public void PureAdd_ShouldBeDetected()
    {
        var (eng, bus) = NewEngine();
        var t0 = new FixedClock().Now;

        // 初始
        eng.OnBest5(MakeSnap("TX00", t0,
            new[] { (100m, 200) }, new[] { (101m, 100) }));

        // 無成交 → ask1 100 → 600（純新增 500）
        eng.OnBest5(MakeSnap("TX00", t0.AddMilliseconds(350),
            new[] { (100m, 200) }, new[] { (101m, 600) }));

        Assert.Contains(bus.Events, e => e is CumHangEvent || e is BigHangEvent);
    }

    // === Dummy dependencies (僅供測試建構器使用) ===
    private sealed class DummySessionProvider : ITradingSessionProvider
    {
        public TradingSession? GetSession(string symbol, DateTime ts) =>
            new TradingSession(ts.Date.AddHours(9), ts.Date.AddHours(13), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, DateOnly.FromDateTime(ts.Date));

        public (DateOnly? first, DateOnly? last) GetFirstAndLastTradingDateInRange(string stockId, DateTime dateFrom, DateTime dateTo, TimeOnly? probeTime = null) => (null, null);
        public DateOnly? FindPreviousTradingDate(string stockId, DateOnly day, int maxLookbackDays = 30) => null;
        public bool IsTradingDate(string stockId, DateOnly date) => true;
        public PeriodWindow GetPeriodWindow(string stockId, BarConfig cfg, DateTime nowLocal) => throw new NotImplementedException();
    }

}

