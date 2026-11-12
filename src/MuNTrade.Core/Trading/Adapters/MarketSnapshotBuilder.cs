using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;

using Microsoft.Extensions.Logging;
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
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Time;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;

namespace MuNTrade.Core.Trading.Adapters
{
    /// <summary>
    /// 撣敹怎瑽遣?具?    /// [2026-03-19 ?寥淫 撌脫??Channel + Background Worker 璅∪?隞亥圾??EventBus ?郊??    /// 蝣箔??桐??撌乩?靽???嚗?銝?憭曹遙雿?Ingress 鞈? (Unbounded)??    /// </summary>
    public class MarketSnapshotBuilder : IHostedService, IDisposable
    {
        private readonly IEventBus _bus;
        private readonly IQuoteStateService _quoteStateService;
        private readonly IClock _clock;
        private readonly ILogger<MarketSnapshotBuilder> _logger;
        private readonly IPriceTierService _priceTierService;

        private readonly ConcurrentDictionary<string, SymbolSnapshotState> _states = new();
        private readonly ConcurrentDictionary<string, long> _lastBoardPublishTicksBySymbol = new(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _minBoardPublishInterval = TimeSpan.FromMilliseconds(200);

        // --- Channel & Worker Infrastructure ---
        private readonly Channel<SnapshotWorkItem> _channel = Channel.CreateBounded<SnapshotWorkItem>(new BoundedChannelOptions(65_536)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        private const long SnapshotChannelCapacity = 65_536;

        private CancellationTokenSource? _workerCts;
        private Task? _workerTask;

        // --- Diagnostics ---
        private long _enqueuedCount;
        private long _processedCount;
        private long _snapshotHighWatermarkHits;
        private readonly Stopwatch _diagStopwatch = Stopwatch.StartNew();

        private class SymbolSnapshotState
        {
            public MarketSnapshot Snapshot = default!;
            public readonly object UpdateLock = new object();
            public readonly Queue<TradeTickEvent> RecentTicks = new Queue<TradeTickEvent>();
            public readonly int OfiWindowSize = 20;

            // [New] Local OHLC & Vol Tracking
            public decimal Open;
            public decimal High;
            public decimal Low;
            public long CumVolume;
            public int TradingDayYmd;
            public MarketNo MarketNo = MarketNo.Unknown;
            public MarketSource Source = MarketSource.Unknown;
            public string Name = "";
        }

        private sealed record SnapshotWorkItem(
            string Kind,
            TradeTickEvent? Tick = null,
            QuoteBest5Event? Best5 = null,
            QuoteSnapshotEvent? RawSnapshot = null);

        public MarketSnapshotBuilder(
            IEventBus bus,
            IQuoteStateService quoteStateService,
            IClock clock,
            ILogger<MarketSnapshotBuilder> logger,
            IPriceTierService priceTierService)
        {
            _bus = bus;
            _quoteStateService = quoteStateService;
            _clock = clock;
            _logger = logger;
            _priceTierService = priceTierService;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[SnapshotBuilder.Worker] Starting background processing loop...");
            _workerCts = new CancellationTokenSource();
            _workerTask = RunWorkerAsync(_workerCts.Token);

            // ??敺???閮
            _bus.Subscribe<TradeTickEvent>(OnTick);
            _bus.Subscribe<QuoteBest5Event>(OnBest5);
            _bus.Subscribe<QuoteSnapshotEvent>(OnRawQuoteSnapshot);

            _ = ReportDiagnosticsAsync(_workerCts.Token);

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[SnapshotBuilder.Worker] Stopping background processing loop...");
            _workerCts?.Cancel();
            if (_workerTask != null)
            {
                await Task.WhenAny(_workerTask, Task.Delay(2000, cancellationToken));
            }
        }

        public void Dispose()
        {
            _workerCts?.Cancel();
            _workerCts?.Dispose();
        }

        private async Task RunWorkerAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var item in _channel.Reader.ReadAllAsync(ct))
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        switch (item.Kind)
                        {
                            case "Tick":
                                ProcessTradeTickCore(item.Tick!);
                                break;
                            case "Best5":
                                ProcessBest5Core(item.Best5!);
                                break;
                            case "RawSnapshot":
                                ProcessRawQuoteSnapshotCore(item.RawSnapshot!);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[SnapshotBuilder.Worker] Error processing {Kind}", item.Kind);
                    }
                    finally
                    {
                        Interlocked.Increment(ref _processedCount);
                        sw.Stop();
                        if (sw.ElapsedMilliseconds > 20)
                        {
                            var sym = item.Tick?.Symbol ?? item.Best5?.Symbol ?? item.RawSnapshot?.Symbol ?? "Unknown";
                            _logger.LogWarning("[SnapshotBuilder.Slow] kind={Kind} symbol={Symbol} elapsedMs={ElapsedMs}",
                                item.Kind, sym, sw.ElapsedMilliseconds);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "[SnapshotBuilder.Worker] Fatal worker error.");
            }
        }

        private async Task ReportDiagnosticsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, ct);
                    var elapsed = _diagStopwatch.Elapsed.TotalSeconds;
                    if (elapsed < 0.1) continue;

                    var enqueued = Interlocked.Exchange(ref _enqueuedCount, 0);
                    var processed = Interlocked.Exchange(ref _processedCount, 0);
                    var len = _channel.Reader.Count;

                    _logger.LogInformation("[SnapshotBuilder.Diagnostics] len={Len} enqueuedPerSec={EnqRate:F1} processedPerSec={ProcRate:F1} HWHits={HW}",
                        len, enqueued / elapsed, processed / elapsed, Interlocked.Read(ref _snapshotHighWatermarkHits));

                    _diagStopwatch.Restart();
                }
                catch (OperationCanceledException) { }
            }
        }

        // ========= EventBus Callbacks (Enqueue Only) =========

        private void CheckSnapshotHighWatermark()
        {
            var inFlight = Interlocked.Read(ref _enqueuedCount)
                         - Interlocked.Read(ref _processedCount);
            if (inFlight > (long)(SnapshotChannelCapacity * 0.75))
                Interlocked.Increment(ref _snapshotHighWatermarkHits);
        }

        private void OnTick(TradeTickEvent tick)
        {
            if (tick.FeedKind != MuNTrade.Abstractions.Domain.MarketData.Enums.TickFeedKind.RealTime) return;
            Interlocked.Increment(ref _enqueuedCount);
            CheckSnapshotHighWatermark();
            _channel.Writer.TryWrite(new SnapshotWorkItem("Tick", Tick: tick));
        }

        private void OnBest5(QuoteBest5Event best5Data)
        {
            if (best5Data.FeedKind != MuNTrade.Abstractions.Domain.MarketData.Enums.TickFeedKind.RealTime) return;
            Interlocked.Increment(ref _enqueuedCount);
            CheckSnapshotHighWatermark();
            _channel.Writer.TryWrite(new SnapshotWorkItem("Best5", Best5: best5Data));
        }

        private void OnRawQuoteSnapshot(QuoteSnapshotEvent evt)
        {
            if (evt.Origin != QuoteSnapshotOrigin.Quote) return;
            Interlocked.Increment(ref _enqueuedCount);
            CheckSnapshotHighWatermark();
            _channel.Writer.TryWrite(new SnapshotWorkItem("RawSnapshot", RawSnapshot: evt));
        }

        // ========= Core Processing Logic (Detached from Bus Thread) =========

        private void ProcessTradeTickCore(TradeTickEvent tick)
        {
            var symbol = ResolveSymbol(tick.Symbol);
            var state = GetOrCreateState(symbol);
            if (state == null) return;

            lock (state.UpdateLock)
            {
                state.RecentTicks.Enqueue(tick);
                if (state.RecentTicks.Count > state.OfiWindowSize)
                    state.RecentTicks.Dequeue();

                var ofi = CalculateOFI(state.RecentTicks, state.Snapshot.Best5);
                var newTickPrice = _priceTierService.GetTickPrice(tick.Price);
                var tickPriceChanged = newTickPrice != state.Snapshot.TickPrice;
                var spreadTicks = state.Snapshot.SpreadTicks;

                if (tickPriceChanged && state.Snapshot.Best5.Bids.Any() && state.Snapshot.Best5.Asks.Any())
                {
                    var bestAsk = state.Snapshot.Best5.Asks[0].Price;
                    var bestBid = state.Snapshot.Best5.Bids[0].Price;
                    spreadTicks = newTickPrice > 0
                        ? (int)Math.Round((bestAsk - bestBid) / newTickPrice, MidpointRounding.AwayFromZero)
                        : 0;
                }

                state.Snapshot = state.Snapshot with
                {
                    ExchangeTs = tick.ExchangeTs,
                    ArrivalTs = tick.ArrivalTs,
                    LastTradeExchangeTs = tick.ExchangeTs,
                    LastTradeArrivalTs = tick.ArrivalTs,
                    LastTradeIsSimulated = tick.IsSimulated,
                    LastPrice = tick.Price,
                    LastVolume = tick.Volume,
                    OfiShortTerm = ofi,
                    TickPrice = newTickPrice,
                    SpreadTicks = spreadTicks
                };

                TryPublishBoardSnapshot_NoThrow(state);
            }
        }

        private void ProcessBest5Core(QuoteBest5Event best5Data)
        {
            var symbol = ResolveSymbol(best5Data.Symbol);
            var state = GetOrCreateState(symbol);
            if (state == null) return;

            lock (state.UpdateLock)
            {
                var bids = best5Data.Bids?.Select(b => new BestLevel(b.Price, b.Volume)).ToList() ?? new List<BestLevel>();
                var asks = best5Data.Asks?.Select(a => new BestLevel(a.Price, a.Volume)).ToList() ?? new List<BestLevel>();

                var tickPrice = state.Snapshot.TickPrice;
                var spreadTicks = 0;

                if (bids.Count > 0 && asks.Count > 0)
                {
                    spreadTicks = tickPrice > 0
                        ? (int)Math.Round((asks[0].Price - bids[0].Price) / tickPrice, MidpointRounding.AwayFromZero)
                        : 0;
                }

                state.Snapshot = state.Snapshot with
                {
                    ArrivalTs = best5Data.ArrivalTs,
                    LastBest5ArrivalTs = best5Data.ArrivalTs,
                    LastBest5IsSimulated = best5Data.IsSimulated,
                    Best5 = new Best5(bids, asks),
                    SpreadTicks = spreadTicks,
                    BestBidDepthSum = bids.Sum(b => b.Quantity),
                    BestAskDepthSum = asks.Sum(a => a.Quantity),
                    IsAuctionOrVI = best5Data.IsSimulated
                };

                TryPublishBoardSnapshot_NoThrow(state);
            }
        }

        private void ProcessRawQuoteSnapshotCore(QuoteSnapshotEvent evt)
        {
            var symbol = ResolveSymbol(evt.Symbol);
            var state = GetOrCreateState(symbol);
            if (state == null) return;

            lock (state.UpdateLock)
            {
                state.Name = evt.Name;
                state.Open = evt.Open;
                state.High = evt.High;
                state.Low = evt.Low;
                state.CumVolume = (long)evt.TVol;
                state.MarketNo = evt.MarketNo;
                state.Source = evt.Source;
                state.Snapshot = state.Snapshot with
                {
                    LastPrice = evt.LastPrice,
                    LastVolume = (int)evt.Vol,
                    LastTradeExchangeTs = new ExchangeTs(_clock.Now),
                    LastTradeArrivalTs = new ArrivalTs(_clock.Now)
                };
            }
        }

        private string ResolveSymbol(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return symbol;
            if (symbol.EndsWith("AM", StringComparison.OrdinalIgnoreCase))
            {
                var baseSymbol = symbol.Substring(0, symbol.Length - 2);
                if (baseSymbol.Length >= 2 &&
                   (baseSymbol.StartsWith("TX", StringComparison.OrdinalIgnoreCase) ||
                    baseSymbol.StartsWith("MTX", StringComparison.OrdinalIgnoreCase) ||
                    baseSymbol.StartsWith("TM", StringComparison.OrdinalIgnoreCase)))
                {
                    return baseSymbol;
                }
            }
            return symbol;
        }

        private void TryPublishBoardSnapshot_NoThrow(SymbolSnapshotState state)
        {
            try
            {
                var now = Environment.TickCount64;
                if (_lastBoardPublishTicksBySymbol.TryGetValue(state.Snapshot.Symbol, out var last) && (now - last < _minBoardPublishInterval.TotalMilliseconds))
                    return;

                _lastBoardPublishTicksBySymbol[state.Snapshot.Symbol] = now;

                var snap = new QuoteSnapshotEvent
                {
                    Symbol = state.Snapshot.Symbol,
                    Name = state.Name,
                    ReferencePrice = state.Snapshot.LastPrice > 0 ? state.Snapshot.LastPrice : 0,
                    Open = state.Open,
                    High = state.High,
                    Low = state.Low,
                    LastPrice = state.Snapshot.LastPrice,
                    TVol = (int)state.CumVolume,
                    Vol = (int)state.Snapshot.LastVolume,
                    BidPrice = state.Snapshot.Best5.Bids.FirstOrDefault()?.Price ?? 0,
                    BidQty = state.Snapshot.Best5.Bids.FirstOrDefault()?.Quantity ?? 0,
                    AskPrice = state.Snapshot.Best5.Asks.FirstOrDefault()?.Price ?? 0,
                    AskQty = state.Snapshot.Best5.Asks.FirstOrDefault()?.Quantity ?? 0,
                    MarketNo = state.MarketNo,
                    Source = state.Source,
                    IsSimulated = state.Snapshot.LastTradeIsSimulated,
                    Origin = QuoteSnapshotOrigin.SnapshotBuilder
                };

                _bus.Publish(snap);
            }
            catch { }
        }

        public MarketSnapshot? GetLatestSnapshot(string symbol)
        {
            _states.TryGetValue(symbol, out var state);
            return state?.Snapshot;
        }

        private SymbolSnapshotState? GetOrCreateState(string symbol)
        {
            if (_states.TryGetValue(symbol, out var state))
                return state;

            if (_quoteStateService.TryGetState(symbol, out var staticData))
            {
                _logger.LogDebug("Creating new snapshot state for {Symbol} from QuoteStateService.", symbol);
                var initialTickPrice = _priceTierService.GetTickPrice(staticData.ReferencePrice);

                var now = _clock.Now;
                var initialState = new SymbolSnapshotState
                {
                    Snapshot = new MarketSnapshot(
                        Symbol: symbol,
                        ExchangeTs: new ExchangeTs(now),
                        ArrivalTs: new ArrivalTs(now),
                        LastPrice: staticData.ReferencePrice,
                        LastVolume: 0, Best5: new Best5(new List<BestLevel>(), new List<BestLevel>()),
                        PriceDecimals: staticData.PriceDecimals, LotSize: staticData.LotSize,
                        TickPrice: initialTickPrice,
                        SpreadTicks: 0, BestBidDepthSum: 0, BestAskDepthSum: 0,
                        OfiShortTerm: 0, AvwapDeltaPct: 0, IsAuctionOrVI: false
                    )
                    with
                    {
                        LastTradeExchangeTs = new ExchangeTs(now),
                        LastTradeArrivalTs = new ArrivalTs(now),
                        LastBest5ArrivalTs = new ArrivalTs(now)
                    }
                };
                return _states.GetOrAdd(symbol, initialState);
            }

            _logger.LogWarning("Could not create snapshot state for {Symbol}. Static data not available in QuoteStateService yet.", symbol);
            return null;
        }

        private decimal CalculateOFI(IEnumerable<TradeTickEvent> recentTicks, Best5 best5)
        {
            if (best5 == null || !best5.Bids.Any() || !best5.Asks.Any()) return 0;
            decimal bestBid = best5.Bids[0].Price;
            decimal bestAsk = best5.Asks[0].Price;
            long buyVolume = 0;
            long sellVolume = 0;
            foreach (var tick in recentTicks)
            {
                if (tick.Price >= bestAsk) buyVolume += tick.Volume;
                else if (tick.Price <= bestBid) sellVolume += tick.Volume;
            }
            long totalVolume = buyVolume + sellVolume;
            return totalVolume == 0 ? 0 : (decimal)(buyVolume - sellVolume) / totalVolume;
        }
    }
}
