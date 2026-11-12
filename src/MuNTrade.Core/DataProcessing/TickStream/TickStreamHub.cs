//  OnTick 完整脫鉤 EventBus 同步鏈
//        callback 只做 TryWrite（真正無鎖 O(1)），ring buffer 維護在獨立 worker
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Core.Settings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MuNTrade.Core.DataProcessing.TickStream
{
    /// <summary>
    ///  TickStreamHub 完整脫鉤 EventBus 同步鏈。
    /// OnTick callback 只做 Channel.TryWrite（無鎖，幾百奈秒）。
    /// ring buffer 維護、GetOrAdd 全部移到獨立的 background worker。
    /// </summary>
    public sealed class TickStreamHub : ITickStreamHub, IDisposable
    {
        private readonly IEventBus _bus;
        private readonly ILogger<TickStreamHub> _log;
        private readonly int _maxTicksPerSymbol;
        private readonly IDisposable _token;

        // ── Ingest channel（EventBus 同步鏈的唯一接觸點）──────────────
        private readonly Channel<TradeTickEvent> _ingestChannel;

        // ── Per-symbol ring buffer store（只由 worker 存取）──────────
        private readonly ConcurrentDictionary<string, SymbolState> _states =
            new(StringComparer.OrdinalIgnoreCase);

        // ── Background worker ─────────────────────────────────────────
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;

        // ── Diagnostics ───────────────────────────────────────────────
        private long _enqueuedCount;
        private long _processedCount;
        private long _hwHits;
        private long _lastDiagLogUtcMs;
        private const long DiagLogIntervalMs = 60_000;
        private const long IngestCapacity = 262_144;

        private sealed class SymbolState
        {
            public readonly object Lock = new();
            public readonly TickRingBuffer<TradeTickEvent> Ring;
            public SymbolState(int capacity) => Ring = new TickRingBuffer<TradeTickEvent>(capacity);
        }

        public TickStreamHub(
            IEventBus bus,
            IClock clock,           // 保留注入避免 DI 錯誤，目前未使用
            ILogger<TickStreamHub> log,
            IOptions<TickStreamOptions> options)
        {
            _bus = bus;
            _log = log;
            _maxTicksPerSymbol = options?.Value?.MaxTicksPerSymbol ?? 120_000;

            _ingestChannel = Channel.CreateBounded<TradeTickEvent>(new BoundedChannelOptions((int)IngestCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

            _worker = Task.Run(() => RunIngestWorkerAsync(_cts.Token));
            _token = _bus.Subscribe<TradeTickEvent>(OnTick);
        }

        /// <summary>
        /// Handles the TickStreamHub work on the synchronous EventBus path.
        /// 只做一次 TryWrite，無鎖、無 GetOrAdd、無 ring 操作。
        /// 耗時：幾百奈秒。
        /// </summary>
        private void OnTick(TradeTickEvent tick)
        {
            Interlocked.Increment(ref _enqueuedCount);

            // 高水位壓力記錄（近似，非精準 drop count）
            var inFlight = Interlocked.Read(ref _enqueuedCount)
                         - Interlocked.Read(ref _processedCount);
            if (inFlight > (long)(IngestCapacity * 0.75))
                Interlocked.Increment(ref _hwHits);

            // DropOldest 模式，TryWrite 永遠成功
            _ingestChannel.Writer.TryWrite(tick);
        }

        /// <summary>
        /// Background worker：從 _ingestChannel 讀取，維護 per-symbol ring buffer。
        /// 完全脫離 EventBus 同步鏈。
        /// </summary>
        private async Task RunIngestWorkerAsync(CancellationToken ct)
        {
            _log.LogInformation("[TickStreamHub] ingest worker started");
            try
            {
                await foreach (var tick in _ingestChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    // GetOrAdd 和 lock + Push 現在在 worker 執行緒，不影響 EventBus 鏈
                    var state = _states.GetOrAdd(tick.Symbol, s => new SymbolState(_maxTicksPerSymbol));
                    lock (state.Lock)
                        state.Ring.Push(tick);

                    var processed = Interlocked.Increment(ref _processedCount);
                    if (processed % 50_000 == 0)
                        MaybeLogDiag();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "[TickStreamHub] ingest worker crashed");
            }
            finally
            {
                _log.LogInformation(
                    "[TickStreamHub] ingest worker stopped. Enqueued={E} Processed={P} HWHits={HW}",
                    Interlocked.Read(ref _enqueuedCount),
                    Interlocked.Read(ref _processedCount),
                    Interlocked.Read(ref _hwHits));
            }
        }

        public IReadOnlyList<TradeTickEvent> GetSnapshot(string symbol, int? maxCount = null)
        {
            if (!_states.TryGetValue(symbol, out var state))
                return Array.Empty<TradeTickEvent>();

            lock (state.Lock)
                return state.Ring.Snapshot(maxCount ?? 0);
        }

        public void ClearSymbol(string symbol)
        {
            if (_states.TryGetValue(symbol, out var state))
                lock (state.Lock)
                    state.Ring.Clear();
        }

        private void MaybeLogDiag()
        {
            var nowMs = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastDiagLogUtcMs);
            if (nowMs - last < DiagLogIntervalMs) return;
            if (Interlocked.CompareExchange(ref _lastDiagLogUtcMs, nowMs, last) != last) return;

            _log.LogInformation(
                "[TickStreamHub.Diag] Enqueued={E} Processed={P} InFlight={InFlight} HWHits={HW} Symbols={S}",
                Interlocked.Read(ref _enqueuedCount),
                Interlocked.Read(ref _processedCount),
                Interlocked.Read(ref _enqueuedCount) - Interlocked.Read(ref _processedCount),
                Interlocked.Read(ref _hwHits),
                _states.Count);
        }

        public void Dispose()
        {
            _token.Dispose();
            _cts.Cancel();
            _ingestChannel.Writer.TryComplete();
            try { _worker.Wait(TimeSpan.FromSeconds(3)); }
            catch (Exception) { }
            _cts.Dispose();
        }
    }
}

