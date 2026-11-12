using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Core.Settings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MuNTrade.Core.OrderBook
{
    public sealed class OrderBookRuntimeWiringService : IHostedService, IDisposable
    {
        private const int QueuePressureThreshold = 2000;

        private readonly IEventBus _bus;
        private readonly OrderBookEngine _orderBookEngine;
        private readonly ITradingSessionProvider _sessionProvider;
        private readonly ITradeQuoteJoiner _joiner;
        private readonly IOrderBookAnalytics _analytics;
        private readonly IOptionsMonitor<OrderBookProcessingOptions> _orderBookOptions;
        private readonly ILogger<OrderBookRuntimeWiringService> _logger;
        private readonly IDisposable _tradeToken;
        private readonly IDisposable _best5Token;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<string, JoinedWindow> _joinedWindows = new(StringComparer.OrdinalIgnoreCase);
        private readonly Channel<ObservationWorkItem> _channel;

        private Task? _worker;
        private long _queueLength;
        private long _lastQueuePressureLogTicks;
        private long _observationEnqueuedCount;
        private long _observationProcessedCount;
        private long _observationHighWatermarkHits;

        public OrderBookRuntimeWiringService(
            IEventBus bus,
            OrderBookEngine orderBookEngine,
            ITradingSessionProvider sessionProvider,
            ITradeQuoteJoiner joiner,
            IOrderBookAnalytics analytics,
            IOptionsMonitor<OrderBookProcessingOptions> orderBookOptions,
            ILogger<OrderBookRuntimeWiringService> logger)
        {
            _bus = bus;
            _orderBookEngine = orderBookEngine;
            _sessionProvider = sessionProvider;
            _joiner = joiner;
            _analytics = analytics;
            _orderBookOptions = orderBookOptions;
            _logger = logger;
            _channel = Channel.CreateBounded<ObservationWorkItem>(new BoundedChannelOptions(65_536)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _tradeToken = bus.Subscribe<TradeTickEvent>(OnTrade);
            _best5Token = bus.Subscribe<Best5DeltaEvent>(OnBest5);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _worker ??= Task.Run(() => RunWithRestartAsync(_cts.Token), CancellationToken.None);
            _logger.LogInformation("[OrderBookObs] runtime lane started");
            return Task.CompletedTask;
        }

        private async Task RunWithRestartAsync(CancellationToken ct)
        {
            const int MaxRestarts = 5;
            const int RestartDelayMs = 2000;
            int restarts = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunAsync(ct).ConfigureAwait(false);
                    return; // 正常結束
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    restarts++;
                    if (restarts > MaxRestarts)
                    {
                        _logger.LogCritical(ex,
                            "[OrderBookObs] DEAD after {Restarts} crashes, giving up.",
                            restarts);
                        return;
                    }
                    _logger.LogError(ex,
                        "[OrderBookObs] crashed, restarting ({Attempt}/{Max}) after {Delay}ms",
                        restarts, MaxRestarts, RestartDelayMs);
                    try { await Task.Delay(RestartDelayMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _channel.Writer.TryComplete();
            _cts.Cancel();

            if (_worker != null)
            {
                try
                {
                    await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _logger.LogInformation("[OrderBookObs] runtime lane stopped");
        }

        private void OnTrade(TradeTickEvent tick)
        {
            Enqueue(new ObservationWorkItem(ObservationWorkItemKind.Trade, tick, null));
        }

        private void OnBest5(Best5DeltaEvent best5)
        {
            Enqueue(new ObservationWorkItem(ObservationWorkItemKind.Best5, null, best5));
        }

        private void Enqueue(ObservationWorkItem item)
        {
            if (_cts.IsCancellationRequested) return;

            Interlocked.Increment(ref _observationEnqueuedCount);
            var inFlight = Interlocked.Read(ref _observationEnqueuedCount)
                         - Interlocked.Read(ref _observationProcessedCount);
            if (inFlight > 49_152L)
                Interlocked.Increment(ref _observationHighWatermarkHits);

            _channel.Writer.TryWrite(item);

            if (inFlight >= QueuePressureThreshold)
            {
                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref _lastQueuePressureLogTicks);
                if (now - last >= 5000)
                {
                    Interlocked.Exchange(ref _lastQueuePressureLogTicks, now);
                    _logger.LogWarning("[OrderBookObs] queue pressure inFlight={InFlight} hwHits={HW}",
                        inFlight, Interlocked.Read(ref _observationHighWatermarkHits));
                }
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    Interlocked.Decrement(ref _queueLength);
                    Interlocked.Increment(ref _observationProcessedCount);

                    switch (item.Kind)
                    {
                        case ObservationWorkItemKind.Trade when item.Trade is not null:
                            ProcessTrade(item.Trade);
                            break;
                        case ObservationWorkItemKind.Best5 when item.Best5 is not null:
                            ProcessBest5(item.Best5);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OrderBookObs] runtime lane crashed");
                throw; // ★ 讓 RunWithRestartAsync 感知
            }
        }

        private void ProcessTrade(TradeTickEvent tick)
        {
            var tsLocal = tick.ExchangeTs.Value;
            var session = _sessionProvider.GetSession(tick.Symbol, tsLocal);
            if (session is null || tsLocal < session.StartTime || tsLocal > session.EndTime)
                return;

            _orderBookEngine.OnTrade(tick);

            var lookahead = _joiner.GetLookaheadBuffer(tick.Symbol);
            var delayRequired = lookahead > TimeSpan.Zero;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (delayRequired)
                        await Task.Delay(lookahead, _cts.Token).ConfigureAwait(false);

                    var kind = _joiner.TryJoin(tick, out var joined, out _);
                    if (kind != JoinMatchKind.None && joined is not null)
                        HandleJoinedQuote(joined);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Joiner] Failed to process trade for {Symbol}", tick.Symbol);
                }
            }, _cts.Token);
        }

        private void ProcessBest5(Best5DeltaEvent best5)
        {
            var tsLocal = best5.ArrivalTs.Value;
            var session = _sessionProvider.GetSession(best5.Symbol, tsLocal);
            if (session is null || tsLocal < session.StartTime || tsLocal > session.EndTime)
                return;

            _joiner.OnBest5(best5);
            _orderBookEngine.OnBest5(best5);
        }

        private void HandleJoinedQuote(JoinedTradeQuote joinedTradeQuote)
        {
            var analyticsOpt = _orderBookOptions.CurrentValue.Analytics;
            var window = _joinedWindows.GetOrAdd(joinedTradeQuote.Symbol, _ =>
            {
                var capacity = Math.Max(analyticsOpt.window_count, analyticsOpt.min_count);
                return new JoinedWindow(capacity);
            });

            MicrostructureScores? scores = null;
            var capacityTarget = Math.Max(analyticsOpt.window_count, analyticsOpt.min_count);
            var minSamples = Math.Max(1, analyticsOpt.min_count);
            var minDuration = TimeSpan.FromSeconds(Math.Max(0, analyticsOpt.min_seconds));

            lock (window.Sync)
            {
                var queue = window.Buffer;
                queue.Enqueue(joinedTradeQuote);
                while (queue.Count > capacityTarget)
                {
                    queue.Dequeue();
                }

                if (queue.Count > 0)
                {
                    var scratch = window.Scratch;
                    scratch.Clear();
                    scratch.EnsureCapacity(queue.Count);
                    foreach (var item in queue)
                    {
                        scratch.Add(item);
                    }

                    scratch.Sort((a, b) => a.ArrivalTs.CompareTo(b.ArrivalTs));

                    var oldestTs = scratch[0].ArrivalTs;
                    var newestTs = scratch[^1].ArrivalTs;
                    var duration = newestTs - oldestTs;

                    if (scratch.Count >= minSamples || duration >= minDuration)
                    {
                        try
                        {
                            var span = CollectionsMarshal.AsSpan(scratch);
                            scores = _analytics.Score(span);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[Joiner] Failed to score microstructure for {Symbol}", joinedTradeQuote.Symbol);
                        }
                    }
                }
            }

            if (scores != null)
            {
                _bus.Publish(scores);
            }
        }

        public void Dispose()
        {
            _channel.Writer.TryComplete();
            _cts.Cancel();
            _tradeToken.Dispose();
            _best5Token.Dispose();
            _cts.Dispose();
        }

        private sealed class JoinedWindow
        {
            public JoinedWindow(int capacity)
            {
                Buffer = new Queue<JoinedTradeQuote>(capacity);
                Scratch = new List<JoinedTradeQuote>(capacity);
            }

            public Queue<JoinedTradeQuote> Buffer { get; }
            public List<JoinedTradeQuote> Scratch { get; }
            public object Sync { get; } = new();
        }

        private enum ObservationWorkItemKind
        {
            Trade,
            Best5
        }

        private readonly record struct ObservationWorkItem(
            ObservationWorkItemKind Kind,
            TradeTickEvent? Trade,
            Best5DeltaEvent? Best5);
    }
}

