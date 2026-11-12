using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.Enums.SystemEnums;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Events.System.Warmup;
using MuNTrade.Abstractions.Contracts.Brokers;
using MuNTrade.Abstractions.Contracts.Data;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Indicators;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Warmup;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Core.Services;
using MuNTrade.Core.Settings;
using MuNTrade.Abstractions.Time;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Domain.Common.Enums; // Required for MarketNo

namespace MuNTrade.Core.DataProcessing
{
    /// <summary>
    /// MarketDataPipeline（最小化版）
    /// - 所有「週期 window」來源僅使用 ITradingSessionProvider.GetPeriodWindow(...)
    /// - Seed 流程直接用 provider 回傳的 WindowStart/WindowEnd 做處理（open snapshot / latest / empty）
    /// - 不再於 pipeline 內自行推算 tradingDate 或呼叫 GetSession
    /// - 加回 Best5 節流 pump：把 QuoteBest5Event 轉成 Best5ThrottledEvent（interval 可調）
    /// </summary>
    public sealed class MarketDataPipeline : IMarketDataPipeline, IDisposable
    {
        private readonly ISubscriptionManager _subMgr;
        private readonly IEventBus _bus;
        private readonly ILogger<MarketDataPipeline> _log;
        private readonly IKLineBuilder _builder;
        private readonly IHistoryWarmupService _warmup;
        private readonly IKLineStore _store;
        private readonly IBrokerConnectivity _api;
        private readonly ITradingSessionProvider _sess; // provider still injected, but pipeline won't call GetSession directly
        private readonly IClock _clock;
        private readonly IStaWorkPressureMonitor _staPressure;
        private readonly MuNTrade.Core.Operations.Dashboard.AttackDashboardService? _dash;
        private readonly IOrbVwapReader _orbVwap;
        private readonly DataAggregatorSettings _aggOpt;

        private readonly List<IDisposable> _subs = new();
        private readonly ConcurrentDictionary<string, SymbolPipe> _pipes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _quoteSubd = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _warmupStarted = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _seeded = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _cts = new();

        // 全域 gate：避免多檔同時訂閱歷史 Ticks，造成 STA message loop 飢餓
        private static readonly SemaphoreSlim _tickHistoryGate = new(1, 1);

        // 同一檔在短時間內重複 Disable 時，避免重覆 Flush/Unsub 的節流表
        private readonly ConcurrentDictionary<string, DateTime> _lastDisabledAt = new();
        private static readonly TimeSpan DisableDebounce = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MarketLiveReadyMaxWindow = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan HistoryDetailReadyMaxWindow = TimeSpan.FromSeconds(8);

        private volatile bool _brokerReady;

        // 移除：全域 Best5 pump（改為 per-symbol worker）

        // 現有的 TF keys
        private static readonly string[] TfAll = { "Time_1", "Time_5", "Time_60", "Day", "Week", "Month" };

        private sealed class SymbolPipe
        {
            public string Symbol { get; init; } = "";
            public bool TicksEnabled;
            public bool KLineEnabled;

            public readonly Channel<TradeTickEvent> TickCh =
                Channel.CreateBounded<TradeTickEvent>(new BoundedChannelOptions(262_144)
                {
                    SingleWriter = false,
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.DropOldest
                });

            public CancellationTokenSource Cts { get; } = new();
            public Task? TickWorker { get; set; }

            // ----- Best5 related -----
            // 最新 snapshot（volatile 參考，寫入時替換整個 object）
            public volatile QuoteBest5Event? Best5Latest;
            // 版本號：每次 UpdateBest5 都會 +1（用 Interlocked）
            public int Best5Version;
            // 已經被 pump 發佈的版本（pump 發佈後會把這個設為 Best5Version）
            public int Best5PublishedVersion;

            // 新增：per-symbol Best5 節流通道（容量 1，DropOldest）
            public readonly Channel<QuoteBest5Event> Best5Ch =
                Channel.CreateBounded<QuoteBest5Event>(new BoundedChannelOptions(1)
                {
                    SingleWriter = false,
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.DropOldest
                });

            // 新增：per-symbol Best5 worker
            public Task? Best5Worker { get; set; }

            public IDisposable? MarketPathScope { get; set; }
            public Stopwatch? MarketLiveStopwatch { get; set; }
            public Stopwatch? HistoryDetailStopwatch { get; set; }
            public int ActivationHistoryTickCount { get; set; }
            public bool HistoryDetailBufferingLogged { get; set; }

            // 可選：哪些 indicators 要在 Best5 上運算（若無需求可移除）
            public readonly HashSet<string> IndicatorsEnabled = new(StringComparer.OrdinalIgnoreCase);

            // ★ P0-3: Tick Attach 狀態追蹤
            public TickAttachState AttachState { get; set; } = TickAttachState.None;
            public int ActivationVersion { get; set; } = 0;
            public string? LastTickAttachFailureReason { get; set; }
            public DateTimeOffset? LastTickAttachFailureAt { get; set; }
            public int RetryCount { get; set; } = 0;

            // ★ R3: per-symbol TickCh 診斷指標
            public long TickChEnqueued;   // OnTickReceived 每次 TryWrite 前 +1
            public long TickChProcessed;  // TickWorkerLoop 每筆處理後 +1
            public long TickChHWHits;     // enqueue 時 inFlight > 75% 容量時 +1
            public long TickChHistoryReceived;  // FeedKind==History 的 enqueued 計數
            public long TickChHistoryProcessed; // worker 中 FeedKind==History 的已處理計數
            public const long TickChCapacity = 262_144;

            public long TickChInFlight =>
                Interlocked.Read(ref TickChEnqueued) - Interlocked.Read(ref TickChProcessed);

            public void UpdateBest5(QuoteBest5Event snap)
            {
                // 儲存快照（整個物件替換），並升版本
                Best5Latest = snap;
                Interlocked.Increment(ref Best5Version);
                // 丟入 per-symbol 節流通道（容量 1，保留最新）
                Best5Ch.Writer.TryWrite(snap);
            }
        }

        // 只 seed 週/月；若設定沒提供就 fallback
        private readonly BarConfig[] _seedTargets;

        public MarketDataPipeline(
            ISubscriptionManager subMgr,
            IEventBus bus,
            ILogger<MarketDataPipeline> log,
            IKLineBuilder builder,
            IHistoryWarmupService warmup,
            IKLineStore store,
            IBrokerConnectivity api,
            ITradingSessionProvider sessionProvider,
            IStaWorkPressureMonitor staPressure,
            IOptions<DataAggregatorSettings> aggOpt,
            IClock clock,
            IOrbVwapReader orbVwap,
            MuNTrade.Core.Operations.Dashboard.AttackDashboardService? dash = null)
        {
            _subMgr = subMgr;
            _bus = bus;
            _log = log;
            _builder = builder;
            _warmup = warmup;
            _store = store;
            _api = api;
            _sess = sessionProvider;
            _staPressure = staPressure;
            _clock = clock;
            _orbVwap = orbVwap;
            _dash = dash;
            _aggOpt = aggOpt?.Value ?? new DataAggregatorSettings();

            _brokerReady = _api.CurrentState == ConnectionState.Connected;
            _api.StateChanged += OnBrokerStateChanged;

            var cfgs = (aggOpt?.Value?.EnabledBarConfigs ?? new List<BarConfig>()).ToArray();
            var wkmo = cfgs.Where(c => c.Type is "Week" or "Month").ToArray();
            _seedTargets = wkmo.Length > 0
                ? wkmo
                : new[] { new BarConfig { Type = "Week", Period = 1 }, new BarConfig { Type = "Month", Period = 1 } };

            _subs.Add(_bus.Subscribe<TradeTickEvent>(OnTickReceived));
            _subs.Add(_bus.Subscribe<QuoteBest5Event>(OnBest5Received));
            _subs.Add(_bus.Subscribe<SubscriptionFailedEvent>(OnSubscriptionFailed));

            // 移除全域 Best5 pump，改為每 symbol worker 在 EnsureSubscribeTicks 啟動
        }

        private void OnBrokerStateChanged(ConnectionState state, string? msg)
        {
            _log.LogInformation("[Pipeline] 券商連線狀態改變: {State} ({Msg})", state, msg);
            _brokerReady = state == ConnectionState.Connected;

            // ★ 重連後的訂閱恢復現在由 SubscriptionManager 統一處理
            // 不再需要在這裡手動補訂，避免重複訂閱
        }


        public void EnableSymbol(string symbol, bool ticksEnabled = false, bool klineEnabled = true, IEnumerable<string>? indicators = null)
        {
            // Fire-and-forget async wrapper
            _ = EnableSymbolAsync(symbol, ticksEnabled, klineEnabled, indicators);
        }

        private async Task EnableSymbolAsync(string symbol, bool ticksEnabled, bool klineEnabled, IEnumerable<string>? indicators)
        {
            _log.LogInformation("[Pipeline] (Async) 收到處理指令: Symbol={Symbol}, TicksEnabled={TicksEnabled}, KLineEnabled={KLineEnabled}", symbol, ticksEnabled, klineEnabled);

            if (!_brokerReady)
            {
                _log.LogInformation("[Pipeline] Broker 未連線，指令先記錄，不立刻訂閱 ticks。");
            }

            // ★ Async Subscribe call into STA with timeout protection
            try
            {
                bool success = await _subMgr.SubscribeQuoteAsync(symbol, _cts.Token);
                if (!success)
                {
                    _log.LogDebug("[Pipeline] 無法取得 {Sym} 靜態資料(Async)，取消啟用流程。", symbol);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[Pipeline] SubscribeQuoteAsync for {Sym} failed.", symbol);
                return;
            }

            var pipe = _pipes.GetOrAdd(symbol, s => new SymbolPipe { Symbol = s });
            lock (pipe)
            {
                bool needWarmup = klineEnabled && !pipe.KLineEnabled;
                bool wantTicks = ticksEnabled;

                // 記錄原來 ticks 狀態以判斷是否從 "開" 變 "關"
                bool prevTicks = pipe.TicksEnabled;

                // apply flags
                pipe.KLineEnabled |= klineEnabled;
                if (ticksEnabled)
                {
                    pipe.TicksEnabled = true;
                }
                // apply indicators requested by caller (optional)
                if (indicators is not null)
                {
                    foreach (var id in indicators) pipe.IndicatorsEnabled.Add(id);
                }

                if (_quoteSubd.TryAdd(symbol, true))
                {
                    // Quote subscription handled by SubscribeQuoteAsync above
                    _log.LogInformation("[Pipeline] Quote checked: {Symbol}", symbol);
                }

                // publish "symbol subscribed" event so UI knows user requested this symbol
                _bus.Publish(new SymbolSubscribedEvent
                {
                    Symbol = symbol,
                    OccurredAt = NowOffset()
                });

                // 如果 caller 要關掉 ticks，而先前是真，則真正去 broker 取消訂閱並發事件
                if (!ticksEnabled && prevTicks)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _subMgr.UnsubscribeTicksAsync(symbol, _cts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "[Pipeline] UnsubscribeTicksAsync failed for {Symbol}", symbol);
                        }
                    });
                    _bus.Publish(new TicksUnsubscribedEvent
                    {
                        Symbol = symbol,
                        OccurredAt = NowOffset()
                    });

                    CompleteSymbolActivation(pipe, "ticks-disabled");

                    // 清 Best5 快取（避免 UI 誤以為仍在更新）
                    pipe.Best5Latest = null;
                    pipe.Best5Version = 0;
                    pipe.Best5PublishedVersion = 0;
                }

                if (needWarmup && _warmupStarted.TryAdd(symbol, 1))
                {
                    _log.LogInformation("[Warmup] {Sym} start...", symbol);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var byTf = await _warmup.WarmupAsync(symbol, _cts.Token).ConfigureAwait(false);
                            foreach (var kv in byTf)
                            {
                                _store.PutInitial(symbol, kv.Key, kv.Value);
                                _store.MarkTfReady(symbol, kv.Key);
                                _log.LogInformation("[Warmup] {Sym} loaded {Count} bars for TF={Tf}", symbol, kv.Value.Count, kv.Key);

                                // publish per-timeframe ready event
                                _bus.Publish(new TfReadyEvent(symbol, kv.Key, kv.Value.Count)
                                {
                                    OccurredAt = NowOffset()
                                });
                            }


                            // ★★★ 改成：先修剪 Week/Month 尾筆（若屬當期 window 的快照就剔除）再初始化指標
                            var nowLocal = EnsureLocal(_clock.Now);
                            var trimmedByTf = new Dictionary<string, IReadOnlyList<JsonKLineRecord>>(StringComparer.OrdinalIgnoreCase);

                            foreach (var (tfKey, list) in byTf)
                            {
                                // 固定排序成「舊→新」
                                var ordered = (list ?? new List<JsonKLineRecord>())
                                              .OrderBy(b => b.KlineTime)
                                              .ToList();

                                if (ordered.Count > 0 &&
                                    (tfKey.Equals("Week", StringComparison.OrdinalIgnoreCase) ||
                                     tfKey.Equals("Month", StringComparison.OrdinalIgnoreCase)))
                                {
                                    var cfgForTf = new BarConfig { Type = tfKey, Period = 1 };
                                    var window = _sess.GetPeriodWindow(symbol, cfgForTf, nowLocal);

                                    var lastRec = ordered[^1];
                                    var lastDo = DateOnly.FromDateTime(EnsureLocal(lastRec.KlineTime));

                                    // Week/Month 的 KlineTime 代表該期「起始交易日」
                                    // 若等於本期 window 的 first → 代表這根是「當期未收線快照」，不要灌進指標
                                    if (lastDo == window.FirstTradingDate)
                                    {
                                        _log.LogInformation("[IND.InitTrim] {Sym}/{Tf}: drop tail snapshot {Last} (current window first={First})",
                                            symbol, tfKey, lastDo, window.FirstTradingDate);
                                        ordered.RemoveAt(ordered.Count - 1);
                                    }
                                }

                                trimmedByTf[tfKey] = ordered; // 其他 TF 原樣丟入
                            }

                            // 用「已修剪過的歷史（全部已收線）」做指標暖機
                            _bus.Publish(new IndicatorInitializeRequest(symbol, trimmedByTf)
                            {
                                OccurredAt = NowOffset()
                            });


                            if (_store.IsAllTfReady(symbol, TfAll))
                            {
                                _log.LogInformation("[Warmup] {Sym} 全 TF Ready。", symbol);

                                // 先 Seed（同步完成，避免時序）—— 使用 IClock local
                                await SeedWeekMonthAsync(symbol, _cts.Token).ConfigureAwait(false);

                                // publish symbol ready
                                _bus.Publish(new SymbolReadyEvent(symbol)
                                {
                                    OccurredAt = NowOffset()
                                });

                                if (wantTicks)
                                {
                                    _log.LogInformation("[Warmup] {Sym} Seed 完成 → SubscribeTicks", symbol);
                                    await EnsureSubscribeTicksAsync(symbol, pipe);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "[Warmup] {Sym} 發生錯誤", symbol);
                            _warmupStarted.TryRemove(symbol, out _);
                        }
                    });
                }

                // 若 Store 已有全部 TF 的歷史資料，確保進行 seed（若需）並發出 SymbolReadyEvent
                if (_store.IsAllTfReady(symbol, TfAll))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // 如果尚未 seed（避免每次重複 seed），才執行
                            if (!_seeded.ContainsKey(symbol))
                            {
                                await SeedWeekMonthAsync(symbol, _cts.Token).ConfigureAwait(false);

                                // 正確：_seeded 是 ConcurrentDictionary<string,bool>，要放 true
                                _seeded.TryAdd(symbol, true);
                            }

                            // 發送 Ready 事件給 UI（保證 UI 得到 SeedDoneAndReady=true）
                            _bus.Publish(new SymbolReadyEvent(symbol)
                            {
                                OccurredAt = NowOffset()
                            });
                            _log.LogInformation("[Pipeline] {Sym} store 已準備好；已 publish SymbolReadyEvent", symbol);

                            // 如果 caller 要 Ticks，就訂閱；否則不動
                            if (wantTicks)
                            {
                                _log.LogInformation("[Pipeline] {Sym} wantTicks=true -> SubscribeTicks", symbol);
                                await EnsureSubscribeTicksAsync(symbol, pipe);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "[Pipeline] 在 EnableSymbol 的 quick-seed 處理過程發生錯誤: {Sym}", symbol);
                        }
                    });
                }
                else if (wantTicks)
                {
                    _log.LogInformation("[Pipeline] {Sym} ticksEnabled=true，但 TF 未全備；等待 Warmup 後再訂閱。", symbol);
                }

            }
        }

        public void DisableSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            symbol = symbol.Trim().ToUpperInvariant();

            var now = _clock.Now;
            if (_lastDisabledAt.TryGetValue(symbol, out var lastAt) && (now - lastAt) < DisableDebounce)
            {
                _log.LogDebug("[Pipeline] DisableSymbol 跳過（{Symbol} 防抖 {Delta}）", symbol, now - lastAt);
                return; // ★ 防抖：短時間內避免重複 Disable → Flush 風暴
            }
            _lastDisabledAt[symbol] = now;

            _log.LogInformation("[Pipeline] 停用 {Symbol} 的所有處理", symbol);

            // === 1) 先處理 pipe（若存在） ===
            if (_pipes.TryRemove(symbol, out var pipe))
            {
                // 停掉 ticks 訂閱（只在真的有開時做一次）
                if (pipe.TicksEnabled)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _subMgr.UnsubscribeTicksAsync(symbol, _cts.Token).ConfigureAwait(false);
                            _bus.Publish(new TicksUnsubscribedEvent
                            {
                                Symbol = symbol,
                                OccurredAt = NowOffset()
                            });
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "[Pipeline] UnsubscribeTicksAsync 例外: {Symbol}", symbol);
                        }
                    });
                }

                // 停止背景 worker
                try { pipe.Cts.Cancel(); } catch { /* ignore */ }
                
                SetTickAttachState(pipe, TickAttachState.Disabled, "user-disabled");
                CompleteSymbolActivation(pipe, "disabled");

                // ★ 保留一次性 Flush：確保舊 bar 不會在重連後延續
                try { _builder.FlushOpenBars(symbol); }
                catch (Exception ex) { _log.LogWarning(ex, "[Pipeline] FlushOpenBars 例外: {Symbol}", symbol); }

                // 讓下次重新啟用可重新 Warmup/Seed
                _seeded.TryRemove(symbol, out _);
                _warmupStarted.TryRemove(symbol, out _);

                // 清 Best5 快取，避免 UI 殘影
                pipe.Best5Latest = null;
                pipe.Best5Version = 0;
                pipe.Best5PublishedVersion = 0;
            }
            else
            {
                _log.LogDebug("[Pipeline] DisableSymbol: pipe 不存在（可能已停用）: {Symbol}", symbol);
                // pipe 不在也不再 Flush，避免無限刷；真正需要 Flush 的那次在上面已處理
            }

            // === 2) 移除 quote 訂閱（若有） ===
            if (_quoteSubd.TryRemove(symbol, out _))
            {
                _ = Task.Run(async () =>
                {
                    try { await _subMgr.UnsubscribeAsync(symbol, _cts.Token).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "[Pipeline] UnsubscribeAsync 例外: {Symbol}", symbol);
                    }
                });
            }

            // === 3) 發佈 UI 事件（只發一次沒關係，重入會被 UI 去重） ===
            _bus.Publish(new SymbolUnsubscribedEvent
            {
                Symbol = symbol,
                OccurredAt = NowOffset()
            });

            _dash?.ClearState(symbol);
        }

        private async Task EnsureSubscribeTicksAsync(string symbol, SymbolPipe pipe)
        {
            var ct = _cts.Token;
            var entered = false;
            var subscribed = false;
            BeginSymbolActivation(pipe);

            try
            {
                await _tickHistoryGate.WaitAsync(ct).ConfigureAwait(false);
                entered = true;

                // 等待期間可能已取消訂閱
                if (!_quoteSubd.ContainsKey(symbol) || !pipe.TicksEnabled)
                {
                    CompleteSymbolActivation(pipe, "activation-cancelled");
                    return;
                }

                // 微小延遲，錯開搶鎖與 UI 更新
                await Task.Delay(200, ct).ConfigureAwait(false);

                _log.LogInformation("[{Symbol}] Begin SubscribeTicks (gated)", symbol);
                LogActivationSubscribeSent(pipe);
                subscribed = await _subMgr.SubscribeTicksAsync(symbol, ct).ConfigureAwait(false);

                if (!subscribed) return;

                // Cooldown：讓 STA 有時間消化歷史 tick 洪水
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CompleteSymbolActivation(pipe, "activation-cancelled");
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[{Symbol}] EnsureSubscribeTicksAsync failed", symbol);
                CompleteSymbolActivation(pipe, "subscribe-failed");
                return;
            }
            finally
            {
                if (entered) _tickHistoryGate.Release();
            }

            if (!subscribed)
            {
                CompleteSymbolActivation(pipe, "subscribe-not-started");
                return;
            }

            SetTickAttachState(pipe, TickAttachState.Attached, "attached");
            lock(pipe) { pipe.RetryCount = 0; }

            // 如果訂閱管理器支援，也確保在券商層級訂閱 Best5
            // （通常 SubscribeTicks 也會啟用券商 COM 層的 Best5）
            // 如果您的系統有單獨的 SubscribeBest5 方法，請在此處呼叫
            _bus.Publish(new TicksSubscribedEvent
            {
                Symbol = symbol,
                OccurredAt = NowOffset()
            });

            if (pipe.TickWorker == null)
            {
                // ★ R3: 用 wrapper 提供 crash 重啟保護
                pipe.TickWorker = Task.Run(() => RunTickWorkerWithRestartAsync(pipe, pipe.Cts.Token));
            }

            // 啟動 per-symbol Best5 worker
            if (pipe.Best5Worker == null)
            {
                pipe.Best5Worker = Task.Factory.StartNew(
                    () => Best5WorkerLoop(pipe),
                    pipe.Cts.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            MarkMarketLiveReady(pipe, "worker-ready");
        }

        private void OnTickReceived(TradeTickEvent e)
        {
            if (_pipes.TryGetValue(e.Symbol, out var p) && p.TicksEnabled)
            {
                if (HasPendingHistoryDetail(p))
                {
                    if (e.FeedKind == TickFeedKind.RealTime)
                    {
                        MarkMarketLiveReady(p, "first-live-tick");
                        MarkHistoryDetailReady(p, "first-live-tick", e);
                    }
                    else
                    {
                        RegisterActivationHistoryTick(p);
                    }
                }

                // ★ R3: TickCh 診斷計數
                Interlocked.Increment(ref p.TickChEnqueued);
                if (e.FeedKind == TickFeedKind.History)
                    Interlocked.Increment(ref p.TickChHistoryReceived);

                // 高水位壓力記錄（近似）
                var inFlight = Interlocked.Read(ref p.TickChEnqueued)
                             - Interlocked.Read(ref p.TickChProcessed);
                if (inFlight > (long)(SymbolPipe.TickChCapacity * 0.75))
                    Interlocked.Increment(ref p.TickChHWHits);

                // DropOldest 已設定，TryWrite 失敗時會丟掉最舊的
                p.TickCh.Writer.TryWrite(e);
            }
        }

        private void OnBest5Received(QuoteBest5Event e)
        {
            if (_pipes.TryGetValue(e.Symbol, out var p) && p.TicksEnabled)
            {
                // [修正] 確保 Bids 和 Asks 不為 null，且型別正確
                var bids = e.Bids != null
                    ? new List<PriceLevel>(e.Bids)
                    : new List<PriceLevel>(5);

                var asks = e.Asks != null
                    ? new List<PriceLevel>(e.Asks)
                    : new List<PriceLevel>(5);

                var snap = new QuoteBest5Event
                {
                    Symbol = e.Symbol,
                    ArrivalTs = e.ArrivalTs,
                    OccurredAt = e.OccurredAt,
                    FeedKind = e.FeedKind,
                    MarketNo = e.MarketNo,
                    Source = e.Source,
                    StockIndex = e.StockIndex,
                    Bids = bids, // 這裡傳入已經處理過的 List
                    Asks = asks, // 這裡傳入已經處理過的 List
                    ExtendBidPrice = e.ExtendBidPrice,
                    ExtendBidVolume = e.ExtendBidVolume,
                    ExtendAskPrice = e.ExtendAskPrice,
                    ExtendAskVolume = e.ExtendAskVolume,
                    IsSimulated = e.IsSimulated
                };
                p.UpdateBest5(snap);
                PublishBest5Delta(snap);
            }
        }

        private void PublishBest5Delta(QuoteBest5Event snap)
        {
            // 直接將 snap.Bids 和 snap.Asks (List<PriceLevel>) 傳給新的事件
            // 這樣 OrderBookEngine 就能拿到完整的價量資訊
            var delta = new Best5DeltaEvent(
                snap.Symbol,
                snap.ArrivalTs,
                snap.Bids,
                snap.Asks,
                snap.MarketNo,
                snap.Source
            )
            {
                FeedKind = snap.FeedKind
            };

            _bus.Publish(delta);
        }

        private void OnSubscriptionFailed(SubscriptionFailedEvent evt)
        {
            if (evt == null || string.IsNullOrEmpty(evt.Symbol)) return;

            // 如果是 TickOnly，則進入 P0-3 / P1-2 的重試與降級邏輯
            if (evt.FailureScope == FailureScope.TickOnly)
            {
                _log.LogWarning("[TickAttach][Fail] symbol={Sym} kind={Kind} scope=TickOnly attempt={Att}", evt.Symbol, evt.FailureKind, evt.Attempt);

                if (_pipes.TryGetValue(evt.Symbol, out var tickPipe))
                {
                    lock (tickPipe)
                    {
                        tickPipe.LastTickAttachFailureReason = evt.Reason;
                        tickPipe.LastTickAttachFailureAt = NowOffset();
                    }
                    SetTickAttachState(tickPipe, TickAttachState.Failed, evt.Reason);

                    lock (tickPipe)
                    {
                        // 中止本次 activation attempt
                        CompleteSymbolActivation(tickPipe, "tick-attach-failed");
                    }

                    if (evt.ShouldRetry)
                    {
                        ScheduleTickRetry(tickPipe);
                    }
                }
                
                // 不論重試與否，發布一條事件讓 UI 知道此檔 degraded
                // （我們不 publish SymbolUnsubscribedEvent）
                return; // 不繼續往下走 DisableSymbol
            }

            // 否則，若是 QuoteOnly 或 Lifecycle，進入 Hard Disable 流程
            _log.LogWarning("[Pipeline][SubFail] Symbol={Sym} Reason={Reason} (Scope={Scope}) -> Hard Eviction", evt.Symbol, evt.Reason, evt.FailureScope);
            
            // 完整移除
            DisableSymbol(evt.Symbol);
        }

        private void ScheduleTickRetry(SymbolPipe pipe)
        {
            var delayMs = 3000;
            var maxRetries = 5;
            
            lock (pipe)
            {
                if (pipe.RetryCount >= maxRetries)
                {
                    _log.LogWarning("[TickAttach][Fallback] symbol={Sym} max retries ({Max}) reached, giving up.", pipe.Symbol, maxRetries);
                    SetTickAttachState(pipe, TickAttachState.Failed, "max-retries-reached");
                    return;
                }
                
                pipe.RetryCount++;
            }
            SetTickAttachState(pipe, TickAttachState.RetryPending, $"retry-scheduled-{pipe.RetryCount}");

            _log.LogInformation("[TickAttach][RetryScheduled] symbol={Sym} delayMs={Delay} attempt={Att}", pipe.Symbol, delayMs, pipe.RetryCount);

            _ = Task.Run(async () => 
            {
                try
                {
                    await Task.Delay(delayMs, pipe.Cts.Token).ConfigureAwait(false);

                    lock (pipe)
                    {
                        if (pipe.AttachState != TickAttachState.RetryPending)
                            return; // 如果已經手動 disable 或其他原因改變了狀態
                    }

                    _log.LogInformation("[TickAttach][RetryStart] symbol={Sym} attempt={Att}", pipe.Symbol, pipe.RetryCount);

                    // 再次呼叫 EnsureSubscribeTicksAsync 重新發起 attach
                    // 不要新建 pipe，只是重新走 tick 訂閱流程
                    await EnsureSubscribeTicksAsync(pipe.Symbol, pipe).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            });
        }
        
        private void SetTickAttachState(SymbolPipe pipe, TickAttachState newState, string? reason)
        {
            int version;
            bool changed = false;
            TickAttachState oldState;
            lock(pipe)
            {
                oldState = pipe.AttachState;
                if (oldState != newState)
                {
                    pipe.AttachState = newState;
                    changed = true;
                }
                version = pipe.ActivationVersion;
            }

            if (changed)
            {
                _bus.Publish(new TickAttachStateChangedEvent
                {
                    Symbol = pipe.Symbol,
                    NewState = newState.ToString(),
                    Reason = reason,
                    AttemptVersion = version,
                    OccurredAt = NowOffset()
                });
            }
        }

        private static TickData Map(TradeTickEvent e)
        {
            // Strict Mode: 資料流必須有明確的 MarketNo
            if (e.MarketNo == MarketNo.Unknown)
                throw new InvalidOperationException($"StrictMode: Tick missing MarketNo. Symbol={e.Symbol}");

            return new TickData
            {
                StockId = e.Symbol,
                ExchangeTs = e.ExchangeTs,
                ArrivalTs = e.ArrivalTs,
                FeedKind = e.FeedKind,
                Price = e.Price,
                Volume = e.Volume,
                MarketNo = e.MarketNo,
                Source = e.Source,
                IsSimulated = e.IsSimulated
            };
        }

        /// <summary>
        /// ★ R3: TickWorkerLoop 的 crash 重啟 wrapper。
        /// worker crash 後最多重試 3 次，超過則停止並 log critical。
        /// </summary>
        private async Task RunTickWorkerWithRestartAsync(SymbolPipe pipe, CancellationToken ct)
        {
            const int MaxRestarts = 3;
            const int RestartDelayMs = 1000;
            int restarts = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await TickWorkerLoop(pipe).ConfigureAwait(false);
                    // 正常結束（CancellationToken triggered），直接退出
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return; // 正常 shutdown
                }
                catch (Exception ex)
                {
                    restarts++;
                    if (restarts > MaxRestarts)
                    {
                        _log.LogCritical(ex,
                            "[Worker.DEAD] Symbol={Symbol} crashed {Restarts} times, giving up. " +
                            "This symbol will no longer receive tick processing until restart.",
                            pipe.Symbol, restarts);
                        return;
                    }

                    _log.LogError(ex,
                        "[Worker.Restart] Symbol={Symbol} crashed, restarting ({Attempt}/{Max}) after {Delay}ms",
                        pipe.Symbol, restarts, MaxRestarts, RestartDelayMs);

                    try { await Task.Delay(RestartDelayMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private async Task TickWorkerLoop(SymbolPipe p)
        {
            _log.LogInformation("[Worker] Tick worker started for {Symbol}", p.Symbol);
            var batch = new List<TradeTickEvent>(1024);

            // ★ R3: latency 量測
            var sw = new System.Diagnostics.Stopwatch();
            long totalBatches = 0;
            long totalTicksProcessed = 0;
            long slowBatchCount = 0;          // 單批耗時 > 50ms
            long lastDiagLogMs = Environment.TickCount64;
            const long DiagIntervalMs = 60_000;
            const long SlowBatchThresholdMs = 50;

            try
            {
                await foreach (var first in p.TickCh.Reader.ReadAllAsync(p.Cts.Token))
                {
                    batch.Clear();
                    batch.Add(first);
                    while (p.TickCh.Reader.TryRead(out var more) && batch.Count < 4096)
                        batch.Add(more);

                    sw.Restart();

                    if (p.KLineEnabled)
                    {
                        for (int i = 0; i < batch.Count; i++)
                        {
                            var ev = batch[i];
                            _builder.ProcessTick(Map(ev));

                            // ★ R3: 計數
                            Interlocked.Increment(ref p.TickChProcessed);
                            if (ev.FeedKind == TickFeedKind.History)
                                Interlocked.Increment(ref p.TickChHistoryProcessed);
                        }
                    }

                    sw.Stop();
                    totalBatches++;
                    totalTicksProcessed += batch.Count;
                    if (sw.ElapsedMilliseconds > SlowBatchThresholdMs)
                    {
                        slowBatchCount++;
                        _log.LogWarning(
                            "[Worker.Slow] Symbol={Symbol} batchSize={Size} elapsedMs={Ms} totalSlowBatches={Slow}",
                            p.Symbol, batch.Count, sw.ElapsedMilliseconds, slowBatchCount);
                    }

                    // ★ R3: 定時診斷 log
                    var nowMs = Environment.TickCount64;
                    if (nowMs - lastDiagLogMs >= DiagIntervalMs)
                    {
                        lastDiagLogMs = nowMs;
                        var histReceived = Interlocked.Read(ref p.TickChHistoryReceived);
                        var histProcessed = Interlocked.Read(ref p.TickChHistoryProcessed);
                        var enqueued = Interlocked.Read(ref p.TickChEnqueued);
                        var processed = Interlocked.Read(ref p.TickChProcessed);

                        _log.LogInformation(
                            "[Worker.Diag] Symbol={Symbol} " +
                            "Enqueued={E} Processed={P} InFlight={InFlight} HWHits={HW} " +
                            "HistReceived={HR} HistProcessed={HP} HistDelta={HD} " +
                            "TotalBatches={Batches} SlowBatches={Slow}",
                            p.Symbol,
                            enqueued, processed, enqueued - processed,
                            Interlocked.Read(ref p.TickChHWHits),
                            histReceived, histProcessed, histReceived - histProcessed,
                            totalBatches, slowBatchCount);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "[Worker] Tick worker crashed for {Symbol}", p.Symbol);
                throw; // ★ R3: 讓外層 restart 機制感知到
            }
            finally
            {
                // ★ R3: 最終對帳 log
                var histReceived = Interlocked.Read(ref p.TickChHistoryReceived);
                var histProcessed = Interlocked.Read(ref p.TickChHistoryProcessed);
                var enqueued = Interlocked.Read(ref p.TickChEnqueued);
                var processed = Interlocked.Read(ref p.TickChProcessed);

                _log.LogInformation(
                    "[Worker] Tick worker stopped for {Symbol}. " +
                    "FinalEnqueued={E} FinalProcessed={P} FinalInFlight={InFlight} " +
                    "HistReceived={HR} HistProcessed={HP} HistMissed={HM} " +
                    "TotalBatches={Batches} SlowBatches={Slow}",
                    p.Symbol,
                    enqueued, processed, enqueued - processed,
                    histReceived, histProcessed, histReceived - histProcessed,
                    totalBatches, slowBatchCount);
            }
        }

        // ----------------------------- Best5 per-symbol Worker（節流發佈） -----------------------------
        private async Task Best5WorkerLoop(SymbolPipe p)
        {
            var token = p.Cts.Token;
            var intervalMs = _aggOpt.Best5ThrottleMs; // 可調

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 讀取最新（capacity=1 + DropOldest）
                    var latest = await p.Best5Ch.Reader.ReadAsync(token);
                    while (p.Best5Ch.Reader.TryRead(out var more))
                        latest = more;

                    if (!p.TicksEnabled)
                        continue;

                    _bus.Publish(new Best5ThrottledEvent(latest)
                    {
                        OccurredAt = latest.OccurredAt
                    });
                    p.Best5PublishedVersion = p.Best5Version;

                    await Task.Delay(intervalMs, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "[Best5Worker] Worker crashed for {Symbol}", p.Symbol);
            }
        }
        // -------------------------------------------------------------------------------

        private DateTimeOffset NowOffset() => new DateTimeOffset(_clock.Now);

        private void BeginSymbolActivation(SymbolPipe pipe)
        {
            var shouldScheduleMarketTimeout = false;
            var shouldScheduleHistoryTimeout = false;
            int version = 0;
            bool needsStateEvent = false;

            lock (pipe)
            {
                pipe.ActivationVersion++;
                version = pipe.ActivationVersion;

                if (pipe.AttachState == TickAttachState.None || pipe.AttachState == TickAttachState.RetryPending || pipe.AttachState == TickAttachState.Failed)
                {
                    pipe.AttachState = TickAttachState.Attaching;
                    needsStateEvent = true;
                }

                if (pipe.MarketPathScope != null || pipe.HistoryDetailStopwatch != null)
                {
                    // 若已在 activation 中，則提早結束但不擋事件
                }
                else
                {
                    pipe.MarketPathScope = _staPressure.BeginMarketPathActivity($"SymbolActivation:{pipe.Symbol}");
                    pipe.MarketLiveStopwatch = Stopwatch.StartNew();
                    pipe.HistoryDetailStopwatch = Stopwatch.StartNew();
                    pipe.ActivationHistoryTickCount = 0;
                    pipe.HistoryDetailBufferingLogged = false;
                    shouldScheduleMarketTimeout = true;
                    shouldScheduleHistoryTimeout = true;
                }
            }

            if (needsStateEvent)
            {
                _bus.Publish(new TickAttachStateChangedEvent
                {
                    Symbol = pipe.Symbol,
                    NewState = TickAttachState.Attaching.ToString(),
                    Reason = "activation-begin",
                    AttemptVersion = version,
                    OccurredAt = NowOffset()
                });
            }

            if (!shouldScheduleMarketTimeout && !shouldScheduleHistoryTimeout)
                return;

            _log.LogInformation("[Activation] start symbol={Symbol} attempt={Att}", pipe.Symbol, version);

            if (shouldScheduleMarketTimeout)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(MarketLiveReadyMaxWindow, _cts.Token).ConfigureAwait(false);
                        MarkMarketLiveReady(pipe, "timeout", version);
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore
                    }
                });
            }

            if (shouldScheduleHistoryTimeout)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(HistoryDetailReadyMaxWindow, _cts.Token).ConfigureAwait(false);
                        MarkHistoryDetailReady(pipe, "timeout", null, version);
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore
                    }
                });
            }
        }

        private void LogActivationSubscribeSent(SymbolPipe pipe)
        {
            lock (pipe)
            {
                if (pipe.MarketLiveStopwatch == null)
                    return;

                _log.LogInformation(
                    "[Activation] subscribe-sent symbol={Symbol} totalMs={TotalMs}",
                    pipe.Symbol,
                    pipe.MarketLiveStopwatch.ElapsedMilliseconds);
            }
        }

        private bool HasPendingHistoryDetail(SymbolPipe pipe)
        {
            lock (pipe)
            {
                return pipe.HistoryDetailStopwatch != null;
            }
        }

        private void RegisterActivationHistoryTick(SymbolPipe pipe)
        {
            lock (pipe)
            {
                if (pipe.HistoryDetailStopwatch == null)
                    return;

                pipe.ActivationHistoryTickCount++;
                if (!pipe.HistoryDetailBufferingLogged)
                {
                    pipe.HistoryDetailBufferingLogged = true;
                    _log.LogInformation(
                        "[Activation] history-detail-buffering symbol={Symbol} historyTicks={HistoryTicks}",
                        pipe.Symbol,
                        pipe.ActivationHistoryTickCount);
                }
            }
        }

        private void CompleteSymbolActivation(SymbolPipe pipe, string reason, TradeTickEvent? liveTick = null)
        {
            int version;
            lock(pipe) { version = pipe.ActivationVersion; }
            MarkMarketLiveReady(pipe, reason, version);
            MarkHistoryDetailReady(pipe, reason, liveTick, version);
        }

        private void MarkMarketLiveReady(SymbolPipe pipe, string reason, int attemptVersion = -1)
        {
            IDisposable? scope = null;
            Stopwatch? sw = null;

            lock (pipe)
            {
                if (attemptVersion != -1 && pipe.ActivationVersion != attemptVersion)
                {
                    _log.LogInformation("[Activation] ready-skipped symbol={Symbol} attempt={Att} reason=stale-attempt", pipe.Symbol, attemptVersion);
                    return;
                }

                if (pipe.AttachState == TickAttachState.Failed || pipe.AttachState == TickAttachState.Disabled)
                {
                    _log.LogInformation("[Activation] aborted symbol={Symbol} attempt={Att} reason=state-{State}", pipe.Symbol, attemptVersion, pipe.AttachState);
                    return;
                }

                scope = pipe.MarketPathScope;
                sw = pipe.MarketLiveStopwatch;
                pipe.MarketPathScope = null;
                pipe.MarketLiveStopwatch = null;
            }

            if (scope == null)
                return;

            sw?.Stop();
            scope.Dispose();
            _log.LogInformation(
                "[Activation] market-live-ready symbol={Symbol} reason={Reason} totalMs={TotalMs}",
                pipe.Symbol,
                reason,
                sw?.ElapsedMilliseconds ?? 0);
        }

        private void MarkHistoryDetailReady(SymbolPipe pipe, string reason, TradeTickEvent? liveTick = null, int attemptVersion = -1)
        {
            Stopwatch? sw = null;
            var historyTickCount = 0;

            lock (pipe)
            {
                if (attemptVersion != -1 && pipe.ActivationVersion != attemptVersion)
                    return;

                if (pipe.AttachState == TickAttachState.Failed || pipe.AttachState == TickAttachState.Disabled)
                    return;

                sw = pipe.HistoryDetailStopwatch;
                historyTickCount = pipe.ActivationHistoryTickCount;
                pipe.HistoryDetailStopwatch = null;
                pipe.ActivationHistoryTickCount = 0;
                pipe.HistoryDetailBufferingLogged = false;
            }

            if (sw == null)
                return;

            if (liveTick != null)
            {
                sw.Stop();
                _log.LogInformation(
                    "[Activation] history-detail-ready symbol={Symbol} reason={Reason} totalMs={TotalMs} historyTicks={HistoryTicks} firstLiveTs={ExchangeTs:HH:mm:ss.fff}",
                    pipe.Symbol,
                    reason,
                    sw.ElapsedMilliseconds,
                    historyTickCount,
                    liveTick.ExchangeTs);
            }
            else
            {
                sw.Stop();
                _log.LogInformation(
                    "[Activation] history-detail-ready symbol={Symbol} reason={Reason} totalMs={TotalMs} historyTicks={HistoryTicks} (no live tick)",
                    pipe.Symbol,
                    reason,
                    sw.ElapsedMilliseconds,
                    historyTickCount);
            }
        }

        // ----------------------------- Seed（每 symbol 一次） -----------------------------
        private Task SeedWeekMonthAsync(string symbol, CancellationToken ct)
        {
            var nowLocal = EnsureLocal(_clock.Now);
            return SeedWeekMonthCoreAsync(symbol, nowLocal, ct);
        }

        private async Task SeedWeekMonthCoreAsync(string symbol, DateTime nowLocal, CancellationToken ct)
        {
            if (_seeded.ContainsKey(symbol)) return;

            // Normalize
            nowLocal = EnsureLocal(nowLocal);

            foreach (var cfg in _seedTargets)
            {
                if (ct.IsCancellationRequested) break;

                // 由 provider 決定整個 period window（唯一來源）
                var window = _sess.GetPeriodWindow(symbol, cfg, nowLocal);
                var expStart = EnsureLocal(window.WindowStart);
                var expEnd = EnsureLocal(window.WindowEnd);

                // debug：印出我們用的上下文（均為 Local）
                _log.LogDebug("[SeedDebug] ===== Seed Context: {Sym} {Type}_{Per} =====", symbol, cfg.Type, cfg.Period);
                _log.LogDebug("[SeedDebug] now(local)={Now:O} windowFirst={F} windowLast={L}", nowLocal, window.FirstTradingDate, window.LastTradingDate);
                _log.LogDebug("[SeedDebug] expStart(local)={S:O}  expEnd(local)={E:O}", expStart, expEnd);

                // 1) 優先：Store 的 open 快照
                if (_store.TryGetOpenBar(symbol, cfg, out var open) && open != null)
                {
                    _builder.SeedCurrentBar(symbol, cfg, open);
                    _log.LogDebug("[Pipeline][Seed] 用 Store 快照接回 {Sym} {Type}_{Per}  O={O} H={H} L={L} C={C} V={V}",
                        symbol, cfg.Type, cfg.Period, open.Open, open.High, open.Low, open.Close, open.Volume);
                    continue;
                }

                // 2) 直接用最新在前的尾筆來判斷（只比「起始日」）
                var latestFirst = GetLatestFirstNormalizedBars(symbol, cfg.Type, depth: 512);
                var latest = latestFirst.FirstOrDefault();

                if (latest.Raw != null)
                {
                    var r = latest.Raw;
                    // normalize stored record time to local and get DateOnly (because stored KlineTime in files is date-based e.g. Mon or 1st)
                    var latestDateOnly = DateOnly.FromDateTime(EnsureLocal(r.KlineTime));

                    // window.FirstTradingDate / LastTradingDate 是 provider 回傳的 DateOnly
                    var windowFirst = window.FirstTradingDate;
                    var windowLast = window.LastTradingDate;

                    _log.LogDebug("[SeedDiag] latestDateOnly={LD} windowFirst={WF} windowLast={WL} cfg={Tf}", latestDateOnly, windowFirst, windowLast, cfg.Type);

                    if (cfg.Type == "Week")
                    {
                        // 同語意比較：若最新的 bar 日期等於 window.first → 用該筆 seed
                        if (latestDateOnly == windowFirst)
                        {
                            var seed = DynamicBar.FromSnapshot(
                                stockId: symbol, cfg: cfg,
                                start: new ExchangeTs(expStart), end: new ExchangeTs(expEnd),
                                o: Convert.ToDecimal(r.Open),
                                h: Convert.ToDecimal(r.High),
                                l: Convert.ToDecimal(r.Low),
                                c: Convert.ToDecimal(r.Close),
                                vol: r.Volume,
                                ticks: 1,
                                lastTs: new ExchangeTs(r.KlineTime)
                            );
                            _builder.SeedCurrentBar(symbol, cfg, seed);
                            _log.LogInformation("[Pipeline][Seed] Week: 用尾筆(同起始日 DateOnly)接回 {Sym} {Type}_{Per}", symbol, cfg.Type, cfg.Period);
                            continue;
                        }

                        if (latestDateOnly < windowFirst)
                        {
                            // 用 latest.Close 延續
                            decimal val = Convert.ToDecimal(r.Close);
                            var seedFromLast = DynamicBar.FromSnapshot(
                                stockId: symbol, cfg: cfg,
                                start: new ExchangeTs(expStart), end: new ExchangeTs(expEnd),
                                o: val, h: val, l: val, c: val,
                                vol: 0, ticks: 0, lastTs: new ExchangeTs(r.KlineTime));
                            _builder.SeedCurrentBar(symbol, cfg, seedFromLast);
                            _log.LogInformation("[Pipeline][Seed] Week: 尾筆 DateOnly < windowFirst，用 Close 延續 {Sym} {Type}_{Per} (Close={Close})",
                                symbol, cfg.Type, cfg.Period, val);
                            continue;
                        }

                        // latestDateOnly > windowFirst -> gap，改用空殼
                        _log.LogInformation("[Pipeline][Seed] Week: 尾筆 DateOnly 在 windowFirst 之後（不安全）→ 改用空殼。");
                    }
                    else // Month
                    {
                        // Month: 檔案裡 KlineTime 是每月 1 日（DateOnly），所以直接比 DateOnly
                        var firstOfExpMonth = windowFirst; // provider 的 FirstTradingDate 對應到月的第一個交易日 (通常就是 1 號或當月第一交易日)
                        if (latestDateOnly == firstOfExpMonth)
                        {
                            var seed = DynamicBar.FromSnapshot(
                                stockId: symbol, cfg: cfg,
                                start: new ExchangeTs(expStart), end: new ExchangeTs(expEnd),
                                o: Convert.ToDecimal(r.Open),
                                h: Convert.ToDecimal(r.High),
                                l: Convert.ToDecimal(r.Low),
                                c: Convert.ToDecimal(r.Close),
                                vol: r.Volume,
                                ticks: 1,
                                lastTs: new ExchangeTs(r.KlineTime)
                            );
                            _builder.SeedCurrentBar(symbol, cfg, seed);
                            _log.LogInformation("[Pipeline][Seed] Month: 用尾筆(同起始日 DateOnly)接回 {Sym} {Type}_{Per}", symbol, cfg.Type, cfg.Period);
                            continue;
                        }

                        if (latestDateOnly < firstOfExpMonth)
                        {
                            decimal val = Convert.ToDecimal(r.Close);
                            var seedFromLast = DynamicBar.FromSnapshot(
                                stockId: symbol, cfg: cfg,
                                start: new ExchangeTs(expStart), end: new ExchangeTs(expEnd),
                                o: val, h: val, l: val, c: val,
                                vol: 0, ticks: 0, lastTs: new ExchangeTs(r.KlineTime));
                            _builder.SeedCurrentBar(symbol, cfg, seedFromLast);
                            _log.LogInformation("[Pipeline][Seed] Month: 尾筆 DateOnly < firstOfMonth，用 Close 延續 {Sym} {Type}_{Per} (Close={Close})",
                                symbol, cfg.Type, cfg.Period, val);
                            continue;
                        }

                        _log.LogInformation("[Pipeline][Seed] Month: 尾筆 DateOnly 在 firstOfMonth 之後（不安全）→ 改用空殼。");
                    }
                }

                else
                {
                    _log.LogInformation("[SeedDebug] {Sym} {Tf} tail empty（沒有任何候選）", symbol, cfg.Type);
                }

                // 3) fallback：空殼（把窗框好，等待第一筆 tick）
                var empty = DynamicBar.FromSnapshot(
                    stockId: symbol, cfg: cfg,
                    start: new ExchangeTs(expStart), end: new ExchangeTs(expEnd),
                    o: 0m, h: 0m, l: 0m, c: 0m,
                    vol: 0, ticks: 0, lastTs: new ExchangeTs(expStart));
                _builder.SeedCurrentBar(symbol, cfg, empty);
                _log.LogInformation("[Pipeline][Seed] 用空殼窗口接回 {Sym} {Type}_{Per}  Start={Start:O} End={End:O}",
                    symbol, cfg.Type, cfg.Period, expStart, expEnd);
            }

            _seeded[symbol] = true;
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            _log.LogInformation("[Pipeline] Disposing...");
            _api.StateChanged -= OnBrokerStateChanged;

            _cts.Cancel();
            var tasks = new List<Task>();
            foreach (var p in _pipes.Values)
            {
                try { p.Cts.Cancel(); } catch { }
                if (p.TickWorker != null) tasks.Add(p.TickWorker);
                if (p.Best5Worker != null) tasks.Add(p.Best5Worker);
            }
            foreach (var s in _subs) s.Dispose();
            _pipes.Clear();

            try { Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(2)); } catch { }
        }

        // ---------- Helpers（保留必要的） ----------
        private static DateTime EnsureLocal(DateTime dt) =>
            dt.Kind switch
            {
                DateTimeKind.Local => dt,
                DateTimeKind.Utc => dt.ToLocalTime(),
                _ => DateTime.SpecifyKind(dt, DateTimeKind.Local) // Unspecified 視為本地
            };

        // Normalize 任意 KlineTime 到 Local
        private DateTime NormalizeToLocal(DateTime dt) => EnsureLocal(dt);

        // 取 latest-first（並把 KlineTime normalize 成 Local）
        private List<(DateTime LocalKlineTime, JsonKLineRecord Raw)> GetLatestFirstNormalizedBars(
            string symbol, string tfKey, int depth = 512)
        {
            var raw = _store.GetTail(symbol, tfKey, depth);
            return raw.Select(r => (LocalKlineTime: NormalizeToLocal(r.KlineTime), Raw: r))
                      .OrderByDescending(x => x.LocalKlineTime)
                      .ToList();
        }
    }
}

