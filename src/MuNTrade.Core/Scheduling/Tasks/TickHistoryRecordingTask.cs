using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Brokers;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Scheduling;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Core.Settings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace MuNTrade.Core.Scheduling.Tasks
{
    public sealed class TickHistoryRecordingTask : DailyTradingScheduleTask, IDisposable
    {
        private readonly ISubscriptionManager _subMgr;
        private readonly IEventBus _bus;
        private readonly IClock _clock;
        private readonly ILogger<TickHistoryRecordingTask> _log;
        private readonly TickRecordingScheduleOptions _opt;
        private readonly DataSourceSettings _dataOpt;
        private readonly IDisposable _manualToken;
        private readonly IDisposable? _alwaysTickToken;
        private readonly SemaphoreSlim _runGate = new(1, 1);
        private readonly object _alwaysRecorderLock = new();

        private readonly object _sync = new();
        private string? _currentSymbol;
        private bool _sawHistory;
        private DateTime _lastHistoryUtc;
        private TickFileRecorder? _recorder;
        private TickFileRecorder? _alwaysRecorder;

        //  錄檔用 bounded channel，OnTick 改為 O(1) enqueue-only
        private readonly Channel<TradeTickEvent> _alwaysRecordChannel =
            Channel.CreateBounded<TradeTickEvent>(new BoundedChannelOptions(262_144)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        private long _alwaysRecordEnqueued;
        private long _alwaysRecordProcessed;
        private long _alwaysRecordHighWatermarkHits;
        private Task? _alwaysRecordWorker;
        private CancellationTokenSource? _alwaysRecordCts;

        // RunInternal 批次路徑使用的 channel（每次 RunInternalAsync 重建）
        private Channel<TradeTickEvent>? _batchRecordChannel;
        private long _batchRecordEnqueued;
        private long _batchRecordProcessed;
        private long _batchRecordHighWatermarkHits;

        public TickHistoryRecordingTask(
            ISubscriptionManager subMgr,
            IEventBus bus,
            IClock clock,
            ILogger<TickHistoryRecordingTask> log,
            IOptions<TickRecordingScheduleOptions> options,
            IOptions<DataSourceSettings> dataOptions)
        {
            _subMgr = subMgr;
            _bus = bus;
            _clock = clock;
            _log = log;
            _opt = options?.Value ?? new TickRecordingScheduleOptions();
            _dataOpt = dataOptions?.Value ?? new DataSourceSettings();

            _manualToken = _bus.Subscribe<TickHistoryRecordingRequestedEvent>(__ => _ = RunManualAsync());
            if (_opt.AlwaysRecordSubscribedTicks)
            {
                _alwaysTickToken = _bus.Subscribe<TradeTickEvent>(OnTick);
                //  啟動常駐錄檔 worker，OnTick 只 enqueue，I/O 在 worker 執行
                _alwaysRecordCts = new CancellationTokenSource();
                _alwaysRecordWorker = Task.Run(() => RunAlwaysRecordWorkerAsync(_alwaysRecordCts.Token));
            }
        }

        public override string Name => "TickHistoryRecording";

        protected override TimeSpan TriggerTime => _opt.TriggerTime;

        public override TimeSpan PollingInterval => TimeSpan.FromSeconds(5);

        protected override async Task RunAsync(DateTime now, CancellationToken cancellationToken)
        {
            if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _log.LogInformation("[Scheduler] TickHistoryRecording already running; skip.");
                return;
            }

            try
            {
                await RunInternalAsync(now, cancellationToken, ignoreEnabled: false).ConfigureAwait(false);
            }
            finally
            {
                _runGate.Release();
            }
        }

        public async Task<bool> RunManualAsync(CancellationToken cancellationToken = default)
        {
            if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _log.LogInformation("[Scheduler] TickHistoryRecording already running; skip manual.");
                return false;
            }

            try
            {
                await RunInternalAsync(_clock.Now, cancellationToken, ignoreEnabled: true).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _runGate.Release();
            }
        }

        private async Task RunInternalAsync(DateTime now, CancellationToken cancellationToken, bool ignoreEnabled)
        {
            if (!_opt.Enabled && !ignoreEnabled)
            {
                _log.LogInformation("[Scheduler] TickHistoryRecording disabled.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_dataOpt.TickDataBasePath))
            {
                _log.LogWarning("[Scheduler] TickDataBasePath not configured; skip tick recording.");
                return;
            }

            var symbols = ResolveSymbols();
            if (symbols.Count == 0)
            {
                _log.LogWarning("[Scheduler] TickHistoryRecording has no symbols to process.");
                return;
            }

            if (_opt.MaxSymbolsPerRun > 0)
                symbols = symbols.Take(_opt.MaxSymbolsPerRun).ToList();

            Directory.CreateDirectory(_dataOpt.TickDataBasePath);

            TickFileRecorder? recorder = null;
            if (!_opt.AlwaysRecordSubscribedTicks)
            {
                recorder = new TickFileRecorder(
                    _dataOpt.TickDataBasePath,
                    _opt.TickIdleSnapshotSeconds,
                    _opt.TickPeriodicSnapshotSeconds,
                    _log);
                _recorder = recorder;
            }

            IDisposable? token = null;
            Task? batchWorker = null;
            CancellationTokenSource? batchWorkerCts = null;

            if (!_opt.AlwaysRecordSubscribedTicks)
            {
                //  建立本次批次錄檔的獨立 channel
                Interlocked.Exchange(ref _batchRecordEnqueued, 0);
                Interlocked.Exchange(ref _batchRecordProcessed, 0);
                Interlocked.Exchange(ref _batchRecordHighWatermarkHits, 0);
                _batchRecordChannel = Channel.CreateBounded<TradeTickEvent>(new BoundedChannelOptions(262_144)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
                batchWorkerCts = new CancellationTokenSource();
                batchWorker = Task.Run(() => RunBatchRecordWorkerAsync(recorder!, batchWorkerCts.Token));
                token = _bus.Subscribe<TradeTickEvent>(OnTick);
            }

            List<string> previousTickSymbols = new();
            IDisposable? exclusiveScope = null;

            try
            {
                _log.LogInformation("[Scheduler] TickHistoryRecording start. Symbols={Count} Manual={Manual}", symbols.Count, ignoreEnabled);
                _bus.Publish(new TickHistoryRecordingProgressEvent(
                    Phase: "Start",
                    Symbol: null,
                    Index: 0,
                    Total: symbols.Count,
                    Message: "開始錄檔",
                    IsRunning: true));

                if (_opt.ExclusiveMode)
                {
                    exclusiveScope = _subMgr.BeginExclusiveTickSubscription("TickHistoryRecording");
                    previousTickSymbols = _subMgr.CurrentTickSubscriptions
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (previousTickSymbols.Count > 0)
                    {
                        _log.LogInformation("[Scheduler] ExclusiveMode: unsubscribe {Count} tick subscriptions.", previousTickSymbols.Count);
                        foreach (var sym in previousTickSymbols)
                        {
                            try
                            {
                                await TryUnsubscribeTicksAsync(sym, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "[Scheduler] ExclusiveMode: UnsubscribeTicks failed: {Symbol}", sym);
                            }
                        }
                    }

                    var remaining = _subMgr.CurrentTickSubscriptions.Count;
                    if (remaining > 0)
                        _log.LogWarning("[Scheduler] ExclusiveMode: {Count} tick subscriptions still active after unsubscribe.", remaining);

                    if (_opt.ExclusivePreDelayMs > 0)
                        await Task.Delay(_opt.ExclusivePreDelayMs, cancellationToken).ConfigureAwait(false);
                }

                for (int i = 0; i < symbols.Count; i++)
                {
                    var symbol = symbols[i];
                    int index = i + 1;
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (_opt.SkipIfAlreadySubscribed && !_opt.ExclusiveMode && _subMgr.CurrentTickSubscriptions.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                        {
                            _log.LogDebug("[Scheduler] Skip {Symbol}: already subscribed.", symbol);
                            _bus.Publish(new TickHistoryRecordingProgressEvent(
                                Phase: "Skip",
                                Symbol: symbol,
                                Index: index,
                                Total: symbols.Count,
                                Message: "已訂閱，略過",
                                IsRunning: true));
                            continue;
                        }

                        bool hasStatic = await EnsureStaticDataAsync(symbol, cancellationToken).ConfigureAwait(false);
                        if (!hasStatic)
                        {
                            _log.LogWarning("[Scheduler] Static data not ready for {Symbol}. Continue without it.", symbol);
                        }

                        SetCurrentSymbol(symbol);

                        _bus.Publish(new TickHistoryRecordingProgressEvent(
                            Phase: "SymbolStart",
                            Symbol: symbol,
                            Index: index,
                            Total: symbols.Count,
                            Message: "訂閱/回補中",
                            IsRunning: true));

                        bool subscribed = _opt.ExclusiveMode
                            ? await _subMgr.SubscribeTicksExclusiveAsync(symbol, cancellationToken).ConfigureAwait(false)
                            : await _subMgr.SubscribeTicksAsync(symbol, cancellationToken).ConfigureAwait(false);
                        if (!subscribed)
                        {
                            _log.LogWarning("[Scheduler] SubscribeTicks failed: {Symbol}", symbol);
                            _bus.Publish(new TickHistoryRecordingProgressEvent(
                                Phase: "SymbolFailed",
                                Symbol: symbol,
                                Index: index,
                                Total: symbols.Count,
                                Message: "訂閱失敗",
                                IsRunning: true));
                            continue;
                        }

                        bool finished = await WaitForHistoryAsync(cancellationToken).ConfigureAwait(false);
                        _log.LogDebug("[Scheduler] History {Symbol} done={Done}", symbol, finished);
                        _bus.Publish(new TickHistoryRecordingProgressEvent(
                            Phase: "SymbolDone",
                            Symbol: symbol,
                            Index: index,
                            Total: symbols.Count,
                            Message: finished ? "完成" : "逾時",
                            IsRunning: true));

                        try
                        {
                            bool unsubscribed = await TryUnsubscribeTicksAsync(symbol, cancellationToken).ConfigureAwait(false);
                            if (!unsubscribed)
                                _log.LogWarning("[Scheduler] UnsubscribeTicks retry exhausted: {Symbol}", symbol);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "[Scheduler] UnsubscribeTicks failed: {Symbol}", symbol);
                        }
                    }
                    finally
                    {
                        ClearCurrentSymbol();

                        if (_opt.BetweenSymbolsDelayMs > 0)
                            await Task.Delay(_opt.BetweenSymbolsDelayMs, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                // Drain pending batches before disposing the recorder.
                await DrainBatchRecorderAsync(token, batchWorker, batchWorkerCts, recorder).ConfigureAwait(false);

                _recorder = null;
                ClearCurrentSymbol();

                if (_opt.ExclusiveMode && _opt.RestorePreviousSubscriptions && previousTickSymbols.Count > 0)
                {
                    try
                    {
                        if (_opt.ExclusivePostDelayMs > 0)
                            await Task.Delay(_opt.ExclusivePostDelayMs, CancellationToken.None).ConfigureAwait(false);

                        _log.LogInformation("[Scheduler] ExclusiveMode: restore {Count} tick subscriptions.", previousTickSymbols.Count);
                        foreach (var sym in previousTickSymbols)
                        {
                            try
                            {
                                await _subMgr.SubscribeTicksExclusiveAsync(sym, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "[Scheduler] ExclusiveMode: Restore SubscribeTicks failed: {Symbol}", sym);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "[Scheduler] ExclusiveMode: Restore failed.");
                    }
                }
                exclusiveScope?.Dispose();
                _bus.Publish(new TickHistoryRecordingProgressEvent(
                    Phase: "Done",
                    Symbol: null,
                    Index: symbols.Count,
                    Total: symbols.Count,
                    Message: "結束",
                    IsRunning: false));
            }
        }

        private void OnTick(TradeTickEvent tick)
        {
            //  O(1) enqueue only — 絕對不做 I/O、不鎖、不 flush
            if (tick == null || tick.IsSimulated || string.IsNullOrWhiteSpace(tick.Symbol))
                return;

            if (_opt.AlwaysRecordSubscribedTicks)
            {
                Interlocked.Increment(ref _alwaysRecordEnqueued);
                RecordAlwaysHighWatermark();
                _alwaysRecordChannel.Writer.TryWrite(tick); // DropOldest，永遠成功
            }
            else
            {
                var batchCh = _batchRecordChannel;
                if (batchCh != null)
                {
                    Interlocked.Increment(ref _batchRecordEnqueued);
                    RecordBatchHighWatermark();
                    batchCh.Writer.TryWrite(tick);
                }
            }
        }

        private void RecordAlwaysHighWatermark()
        {
            var inFlight = Interlocked.Read(ref _alwaysRecordEnqueued)
                         - Interlocked.Read(ref _alwaysRecordProcessed);
            if (inFlight > 196_608L) // 262,144 * 0.75
                Interlocked.Increment(ref _alwaysRecordHighWatermarkHits);
        }

        private void RecordBatchHighWatermark()
        {
            var inFlight = Interlocked.Read(ref _batchRecordEnqueued)
                         - Interlocked.Read(ref _batchRecordProcessed);
            if (inFlight > 196_608L)
                Interlocked.Increment(ref _batchRecordHighWatermarkHits);
        }

        /// <summary>常駐路徑的 I/O worker，從 _alwaysRecordChannel 讀取並寫檔。</summary>
        private async Task RunAlwaysRecordWorkerAsync(CancellationToken ct)
        {
            _log.LogInformation("[TickRecording.AlwaysPath] worker started");
            var maintenanceInterval = TimeSpan.FromSeconds(5);
            try
            {
                var reader = _alwaysRecordChannel.Reader;
                while (!ct.IsCancellationRequested)
                {
                    // 使用 WhenAny 確保在沒有 tick 時也能進入維護週期
                    var waitTask = reader.WaitToReadAsync(ct).AsTask();
                    var delayTask = Task.Delay(maintenanceInterval, ct);
                    await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);

                    if (waitTask.IsCompleted && await waitTask.ConfigureAwait(false))
                    {
                        while (reader.TryRead(out var tick))
                        {
                            ProcessTickToAlwaysRecorder(tick);
                            Interlocked.Increment(ref _alwaysRecordProcessed);
                        }
                    }

                    // 背景維護：即使沒有新 tick，也能 flush 或快照
                    _alwaysRecorder?.FlushIdleIfNeeded(DateTime.UtcNow);
                    _alwaysRecorder?.SnapshotIdleIfNeeded(DateTime.UtcNow);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "[TickRecording.AlwaysPath] worker crashed"); }
            finally
            {
                _log.LogInformation(
                    "[TickRecording.AlwaysPath] worker stopped. Enqueued={E} Processed={P} HW={HW}",
                    Interlocked.Read(ref _alwaysRecordEnqueued),
                    Interlocked.Read(ref _alwaysRecordProcessed),
                    Interlocked.Read(ref _alwaysRecordHighWatermarkHits));
            }
        }

        /// <summary>原 OnTick 中 AlwaysRecord 的 I/O 邏輯，移到 worker 執行緒。</summary>
        private void ProcessTickToAlwaysRecorder(TradeTickEvent tick)
        {
            var alwaysRecorder = EnsureAlwaysRecorder();
            if (alwaysRecorder == null) return;
            var ts = ResolveTickTime(tick);
            string source = tick.FeedKind == TickFeedKind.History ? "HIST" : "RT";
            alwaysRecorder.Record(tick.Symbol, ts, tick.BidAtTrade, tick.AskAtTrade,
                                  tick.Price, tick.Volume, source, tick.MarketNo);
        }

        /// <summary>批次錄檔路徑的 I/O worker。</summary>
        private async Task DrainBatchRecorderAsync(IDisposable? busToken, Task? worker, CancellationTokenSource? cts, TickFileRecorder? recorder)
        {
            if (_opt.AlwaysRecordSubscribedTicks) return;

            _log.LogInformation("[TickRecording.BatchPath] Draining batch recorder...");

            // 1. 先停訂閱
            busToken?.Dispose();

            // 2. 宣告 channel 結束
            _batchRecordChannel?.Writer.TryComplete();

            // 3. 等待 worker 結束 (不限時，直到資料全數 drain 完)
            if (worker != null)
            {
                try
                {
                    await worker.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[TickRecording.BatchPath] worker encountered error during drain");
                }
            }

            // 4. 清理資源
            cts?.Dispose();
            _batchRecordChannel = null;

            // 5. 最後才 Dispose recorder (這會觸發最後的檔案 finalization)
            recorder?.Dispose();

            _log.LogInformation(
                "[TickRecording.BatchPath] batch drain completed. Enqueued={E} Processed={P} HW={HW}",
                Interlocked.Read(ref _batchRecordEnqueued),
                Interlocked.Read(ref _batchRecordProcessed),
                Interlocked.Read(ref _batchRecordHighWatermarkHits));
        }

        private async Task RunBatchRecordWorkerAsync(TickFileRecorder recorder, CancellationToken ct)
        {
            _log.LogInformation("[TickRecording.BatchPath] worker started");
            var maintenanceInterval = TimeSpan.FromSeconds(2);
            try
            {
                var reader = _batchRecordChannel?.Reader;
                if (reader == null) return;

                while (!ct.IsCancellationRequested)
                {
                    var waitTask = reader.WaitToReadAsync(ct).AsTask();
                    var delayTask = Task.Delay(maintenanceInterval, ct);
                    await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);

                    bool hasData = waitTask.IsCompleted && await waitTask.ConfigureAwait(false);
                    if (hasData)
                    {
                        while (reader.TryRead(out var tick))
                        {
                            if (tick == null || string.IsNullOrWhiteSpace(tick.Symbol)) continue;

                            string? current;
                            lock (_sync) { current = _currentSymbol; }
                            if (current == null || !string.Equals(current, tick.Symbol, StringComparison.OrdinalIgnoreCase))
                            {
                                Interlocked.Increment(ref _batchRecordProcessed);
                                continue;
                            }

                            var ts = ResolveTickTime(tick);
                            string source = tick.FeedKind == TickFeedKind.History ? "HIST" : "RT";
                            recorder.Record(tick.Symbol, ts, tick.BidAtTrade, tick.AskAtTrade,
                                            tick.Price, tick.Volume, source, tick.MarketNo);
                            Interlocked.Increment(ref _batchRecordProcessed);

                            if (tick.FeedKind == TickFeedKind.History)
                            {
                                lock (_sync)
                                {
                                    _sawHistory = true;
                                    _lastHistoryUtc = DateTime.UtcNow;
                                }
                            }
                        }
                    }

                    // 批次路徑維護：由於批次路徑通常在高壓後結束，故僅需快速 flush 與快照
                    recorder.FlushIdleIfNeeded(DateTime.UtcNow);
                    recorder.SnapshotIdleIfNeeded(DateTime.UtcNow);

                    // 若 channel 已完成且已讀空，則退出
                    if (!hasData && reader.Completion.IsCompleted) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "[TickRecording.BatchPath] worker crashed"); }
        }

        public void Dispose()
        {
            _manualToken?.Dispose();
            _alwaysTickToken?.Dispose();

            _log.LogInformation("[TickRecording.AlwaysPath] Disposing task...");

            // 1. 先停 channel
            _alwaysRecordChannel.Writer.TryComplete();

            // 2. 等待 worker 結束 (同步等，因為 Dispose 不支援 async)
            if (_alwaysRecordWorker != null)
            {
                try
                {
                    // 這裡如果 worker 在 I/O 阻塞，可能需要 Cancel 輔助，但原則上 TryComplete 後讀完就會結束
                    // 先給一點時間自動退出，不行再 Cancel
                    if (!_alwaysRecordWorker.Wait(TimeSpan.FromSeconds(10)))
                    {
                        _log.LogWarning("[TickRecording.AlwaysPath] worker did not stop gracefully; canceling CTS");
                        _alwaysRecordCts?.Cancel();
                        _alwaysRecordWorker.Wait(TimeSpan.FromSeconds(5));
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[TickRecording.AlwaysPath] worker exit error during Dispose");
                }
            }

            _alwaysRecordCts?.Dispose();
            _alwaysRecorder?.Dispose();

            _log.LogInformation(
                "[TickRecording.AlwaysPath] Dispose completed. Enqueued={E} Processed={P} HW={HW}",
                Interlocked.Read(ref _alwaysRecordEnqueued),
                Interlocked.Read(ref _alwaysRecordProcessed),
                Interlocked.Read(ref _alwaysRecordHighWatermarkHits));
        }

        private TickFileRecorder? EnsureAlwaysRecorder()
        {
            if (!_opt.AlwaysRecordSubscribedTicks)
                return null;

            if (_alwaysRecorder != null)
                return _alwaysRecorder;

            if (string.IsNullOrWhiteSpace(_dataOpt.TickDataBasePath))
                return null;

            lock (_alwaysRecorderLock)
            {
                if (_alwaysRecorder == null)
                {
                    Directory.CreateDirectory(_dataOpt.TickDataBasePath);
                    _alwaysRecorder = new TickFileRecorder(
                        _dataOpt.TickDataBasePath,
                        _opt.TickIdleSnapshotSeconds,
                        _opt.TickPeriodicSnapshotSeconds,
                        _log);
                }
            }

            return _alwaysRecorder;
        }

        private async Task<bool> WaitForHistoryAsync(CancellationToken ct)
        {
            var maxWait = TimeSpan.FromMilliseconds(Math.Max(500, _opt.MaxWaitMs));
            var idle = TimeSpan.FromMilliseconds(Math.Max(50, _opt.HistoryIdleMs));
            var start = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5, ct).ConfigureAwait(false);

                bool saw;
                DateTime last;
                lock (_sync)
                {
                    saw = _sawHistory;
                    last = _lastHistoryUtc;
                }

                if (saw && DateTime.UtcNow - last >= idle)
                    return true;

                if (DateTime.UtcNow - start >= maxWait)
                    return false;
            }

            return false;
        }

        private void SetCurrentSymbol(string symbol)
        {
            lock (_sync)
            {
                _currentSymbol = symbol;
                _sawHistory = false;
                _lastHistoryUtc = DateTime.UtcNow;
            }
        }

        private void ClearCurrentSymbol()
        {
            lock (_sync)
            {
                _currentSymbol = null;
                _sawHistory = false;
                _lastHistoryUtc = DateTime.MinValue;
            }
        }

        private DateTime ResolveTickTime(TradeTickEvent tick)
        {
            var ts = tick.ExchangeTs.Value;
            if (ts == default)
                ts = tick.ArrivalTs.Value;
            if (ts == default)
                ts = _clock.Now;
            if (ts.Kind == DateTimeKind.Utc)
                return ts.ToLocalTime();
            if (ts.Kind == DateTimeKind.Unspecified)
                return DateTime.SpecifyKind(ts, DateTimeKind.Local);
            return ts;
        }

        private async Task<bool> EnsureStaticDataAsync(string symbol, CancellationToken ct)
        {
            if (_subMgr.TryGetInitialState(symbol, out _))
                return true;

            try
            {
                await _subMgr.RefreshStaticDataAsync(new[] { symbol }, "TickHistoryRecording", _opt.AllowUnsubscribedStaticData).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Scheduler] RefreshStaticDataAsync failed for {Symbol}", symbol);
            }

            int waitMs = Math.Max(0, _opt.StaticDataWaitMs);
            if (waitMs == 0)
                return _subMgr.TryGetInitialState(symbol, out _);

            var start = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                if (_subMgr.TryGetInitialState(symbol, out _))
                    return true;

                if ((DateTime.UtcNow - start).TotalMilliseconds >= waitMs)
                    return false;

                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            return false;
        }

        private async Task<bool> TryUnsubscribeTicksAsync(string symbol, CancellationToken ct)
        {
            int attempts = Math.Max(1, _opt.UnsubscribeRetryCount);
            int delayMs = Math.Max(0, _opt.UnsubscribeRetryDelayMs);

            for (int i = 0; i < attempts; i++)
            {
                bool ok = await _subMgr.UnsubscribeTicksAsync(symbol, ct).ConfigureAwait(false);
                if (ok && !_subMgr.CurrentTickSubscriptions.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                    return true;

                if (delayMs > 0)
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }

            return !_subMgr.CurrentTickSubscriptions.Contains(symbol, StringComparer.OrdinalIgnoreCase);
        }

        private List<string> ResolveSymbols()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_opt.Symbols != null)
            {
                foreach (var s in _opt.Symbols)
                {
                    var sym = s?.Trim();
                    if (!string.IsNullOrWhiteSpace(sym))
                        set.Add(sym);
                }
            }

            if (_opt.SymbolsFilePaths != null)
            {
                foreach (var path in _opt.SymbolsFilePaths)
                {
                    var list = LoadSymbolsFromFile(path);
                    foreach (var sym in list)
                        set.Add(sym);
                }
            }

            if (set.Count == 0)
            {
                // fallback to bundled resource lists (CLI)
                var baseDir = ResolveStockListBasePath();
                var listing = Path.Combine(baseDir, "上市個股清單.json");
                var otc = Path.Combine(baseDir, "上櫃個股清單.json");
                foreach (var sym in LoadSymbolsFromFile(listing))
                    set.Add(sym);
                foreach (var sym in LoadSymbolsFromFile(otc))
                    set.Add(sym);
            }

            return set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private string ResolveStockListBasePath()
        {
            if (!string.IsNullOrWhiteSpace(_dataOpt.StockListBasePath))
                return _dataOpt.StockListBasePath;

            var baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, "Resources", "StockLists");
        }

        private static List<string> LoadSymbolsFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new List<string>();

            string finalPath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path);

            if (!File.Exists(finalPath))
                return new List<string>();

            try
            {
                var json = File.ReadAllText(finalPath, Encoding.UTF8);
                var items = System.Text.Json.JsonSerializer.Deserialize<List<StockListItem>>(json, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (items == null)
                    return new List<string>();

                return items.Select(x => x.StockNo?.Trim())
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Where(s => s!.Length == 4 && s.All(char.IsDigit))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList()!;
            }
            catch
            {
                return new List<string>();
            }
        }

        private sealed class StockListItem
        {
            public string? StockNo { get; set; }
        }

        private sealed class TickFileRecorder : IDisposable
        {
            // Memory-only trade-off: ticks written after the last snapshot are held in RAM only.
            // If the process crashes before the next snapshot, those ticks will be lost.
            // Adjust TickIdleSnapshotSeconds to control maximum data loss window.

            private readonly object _lock = new();
            private readonly List<string> _sessionLines = new(65536);
            private readonly string _baseDir;
            private readonly int _idleSnapSec;
            private readonly int _periodicSnapSec;
            private readonly ILogger _log;

            private string? _currentGzPath;
            private DateTime _lastActivityUtc = DateTime.MinValue;
            private DateTime _lastSnapshotUtc = DateTime.MinValue;
            private bool _dirtySinceLastSnapshot = false;
            private bool _sessionActive = false;

            private DateTime _currentTradeDate;
            private string _currentTag = "";
            private string _currentSymbol = "";

            public TickFileRecorder(string baseDir, int idleSnapshotSeconds, int periodicSnapshotSeconds, ILogger log)
            {
                _baseDir = baseDir;
                _idleSnapSec = idleSnapshotSeconds > 0 ? idleSnapshotSeconds : 5;
                _periodicSnapSec = periodicSnapshotSeconds > 0 ? periodicSnapshotSeconds : 60;
                _log = log;
            }

            public void Record(string symbol, DateTime ts, decimal bid, decimal ask, decimal last, long qty, string source, MarketNo marketNo)
            {
                var tradeDate = GetTradingDate(ts, marketNo);
                var tag = IsNightSession(ts, marketNo) ? "_N" : "_D";

                lock (_lock)
                {
                    if (ShouldSwitchSession(symbol, tradeDate, tag))
                    {
                        FinalizeCurrentSessionLocked();
                        StartNewSessionLocked(tradeDate, tag, symbol);
                    }

                    _lastActivityUtc = DateTime.UtcNow;

                    string line = string.Join(',',
                        ts.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        bid.ToString(CultureInfo.InvariantCulture),
                        ask.ToString(CultureInfo.InvariantCulture),
                        last.ToString(CultureInfo.InvariantCulture),
                        qty.ToString(CultureInfo.InvariantCulture),
                        source);

                    _sessionLines.Add(line);

                    // 第一次有新資料時，建立 periodic snapshot 的計時起點
                    if (!_dirtySinceLastSnapshot && _lastSnapshotUtc == DateTime.MinValue)
                    {
                        _lastSnapshotUtc = DateTime.UtcNow;
                    }

                    _dirtySinceLastSnapshot = true;
                }
            }

            private bool ShouldSwitchSession(string symbol, DateTime tradeDate, string tag)
            {
                if (!_sessionActive) return true;
                if (!string.Equals(symbol, _currentSymbol, StringComparison.OrdinalIgnoreCase)) return true;
                if (tradeDate != _currentTradeDate || tag != _currentTag) return true;
                return false;
            }

            private void StartNewSessionLocked(DateTime tradeDate, string tag, string symbol)
            {
                string symbolDir = Path.Combine(_baseDir, symbol);
                Directory.CreateDirectory(symbolDir);

                string baseName = $"{tradeDate:yyyyMMdd}{tag}_{symbol}";
                _currentGzPath = Path.Combine(symbolDir, baseName + ".csv.gz");

                _currentTradeDate = tradeDate;
                _currentTag = tag;
                _currentSymbol = symbol;
                _sessionActive = true;

                _sessionLines.Clear();
                _sessionLines.Add("DateTime,Bid,Ask,Last,Qty,Source");

                _lastSnapshotUtc = DateTime.MinValue;
                _dirtySinceLastSnapshot = false;
                _lastActivityUtc = DateTime.MinValue;
            }

            // Memory-only mode: no intermediate disk flush target.
            // Snapshots are triggered via SnapshotIdleIfNeeded.
            public void FlushIdleIfNeeded(DateTime utcNow) { }

            public void SnapshotIdleIfNeeded(DateTime utcNow)
            {
                lock (_lock)
                {
                    // 沒有活躍 session，直接不做
                    if (!_sessionActive) return;

                    // 上次 snapshot 之後沒有新資料，不做
                    if (!_dirtySinceLastSnapshot) return;

                    // 尚未有任何有效活動時間，不做
                    if (_lastActivityUtc == DateTime.MinValue) return;

                    bool isIdle =
                        (utcNow - _lastActivityUtc).TotalSeconds >= _idleSnapSec;

                    bool isPeriodic =
                        _lastSnapshotUtc != DateTime.MinValue &&
                        (utcNow - _lastSnapshotUtc).TotalSeconds >= _periodicSnapSec;

                    if (isIdle || isPeriodic)
                    {
                        WriteGzLocked();
                    }
                }
            }

            /// <summary>
            /// Writes all in-memory session lines directly to .csv.gz (atomic overwrite via .tmp).
            /// On failure, the existing .gz is preserved and _sessionLines is not cleared.
            /// </summary>
            private void WriteGzLocked()
            {
                if (_currentGzPath == null || _sessionLines.Count == 0) return;

                string tmpPath = _currentGzPath + ".tmp";
                try
                {
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);

                    using (var fsOut = File.Create(tmpPath))
                    using (var gz = new GZipStream(fsOut, CompressionLevel.Optimal))
                    using (var sw = new StreamWriter(gz, Encoding.UTF8) { AutoFlush = false })
                    {
                        foreach (var line in _sessionLines)
                            sw.WriteLine(line);
                    }

                    File.Move(tmpPath, _currentGzPath!, overwrite: true);

                    _lastSnapshotUtc = DateTime.UtcNow;
                    _dirtySinceLastSnapshot = false;
                }
                catch
                {
                    // Do NOT clear _sessionLines — preserve in-memory data for next snapshot attempt.
                    // Clean up stale .tmp if it exists.
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* ignore */ }
                }
            }

            private void FinalizeCurrentSessionLocked()
            {
                if (!_sessionActive) return;

                if (_dirtySinceLastSnapshot)
                    WriteGzLocked();

                _sessionLines.Clear();
                _sessionActive = false;
                _currentTradeDate = default;
                _currentTag = "";
                _currentSymbol = "";
                _currentGzPath = null;
                _dirtySinceLastSnapshot = false;
                _lastActivityUtc = DateTime.MinValue;
                _lastSnapshotUtc = DateTime.MinValue;
            }

            private static bool IsNightSession(DateTime ts, MarketNo marketNo)
            {
                if (marketNo != MarketNo.Futures && marketNo != MarketNo.Options)
                    return false;
                return ts.Hour >= 15 || ts.Hour < 5;
            }

            private static DateTime GetTradingDate(DateTime ts, MarketNo marketNo)
            {
                if (marketNo != MarketNo.Futures && marketNo != MarketNo.Options)
                    return ts.Date;

                if (ts.Hour >= 15)
                    return ts.Date.AddDays(1);

                return ts.Date;
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    FinalizeCurrentSessionLocked();
                }
            }
        }
    }
}

