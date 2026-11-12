using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Contracts.Brokers;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Services;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Abstractions.Helpers;
using MuNTrade.Core.Settings;
using MuNTrade.Core.Services;
using MuNTrade.Core.Backtesting;
using MuNTrade.Core.Backtesting.Universe;
using MuNTrade.Core.Backtesting.Warmup;
using MuNTrade.Core.StrategyRuntime;
using MuNTrade.Core.StrategyRuntime.Routing;
using MuNTrade.Core.Trading.Adapters;

namespace MuNTrade.Core.Backtesting
{
    /// <summary>
    /// Backtest Host：以現有 EventBus/Strategy/ Risk/ Ledger 語義執行回測。
    /// </summary>
    public sealed class BacktestHost
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IClock _clock;
        private readonly ITurnoverSymbolSelector _turnoverSelector;
        private readonly ITradingSessionProvider _sessionProvider;
        private readonly IHolidayCalendar _holidayCalendar;
        private readonly IBacktestUniversePlanner _universePlanner;
        private readonly IWarmupCacheManager _warmupCacheManager;
        private readonly IOptions<BacktestSettings> _backtestOptions;
        private readonly IOptions<DataSourceSettings> _dataOptions;
        private readonly BacktestReportWriter _reportWriter;
        private readonly ILogger<BacktestHost> _logger;

        public BacktestHost(
            IServiceScopeFactory scopeFactory,
            IClock clock,
            ITurnoverSymbolSelector turnoverSelector,
            ITradingSessionProvider sessionProvider,
            IHolidayCalendar holidayCalendar,
            IBacktestUniversePlanner universePlanner,
            IWarmupCacheManager warmupCacheManager,
            IOptions<BacktestSettings> backtestOptions,
            IOptions<DataSourceSettings> dataOptions,
            BacktestReportWriter reportWriter,
            ILogger<BacktestHost> logger)
        {
            _scopeFactory = scopeFactory;
            _clock = clock;
            _turnoverSelector = turnoverSelector;
            _sessionProvider = sessionProvider;
            _holidayCalendar = holidayCalendar;
            _universePlanner = universePlanner;
            _warmupCacheManager = warmupCacheManager;
            _backtestOptions = backtestOptions;
            _dataOptions = dataOptions;
            _reportWriter = reportWriter;
            _logger = logger;
        }

        public async Task RunAsync(CancellationToken ct = default)
        {
            var opt = _backtestOptions.Value;
            if (!TryParseDate(opt.StartDate, out var start) || !TryParseDate(opt.EndDate, out var end))
            {
                _logger.LogError("[BacktestHost] StartDate/EndDate invalid. Please use yyyy-MM-dd.");
                return;
            }

            var request = new BacktestRunRequest
            {
                ReportMode = Normalize(opt.ReportMode, "C2"),
                Stage = string.IsNullOrWhiteSpace(opt.Stage) ? "Explore" : opt.Stage.Trim(),
                TimeMode = Normalize(opt.TimeMode, "Range"),
                StartDate = start,
                EndDate = end,
                UniverseMode = Normalize(opt.UniverseMode, "SingleSymbol"),
                ManualList = opt.StockIds ?? new List<string>(),
                TurnoverTopN = opt.TurnoverTopN,
                StrategyTemplateId = opt.StrategyTemplateId,
                AccountKey = opt.AccountKey,
                ReportRoot = opt.ReportRoot,
                SkipMissingData = opt.SkipMissingData,
                Risk = opt.Risk ?? new BacktestRiskOptions(),
                Execution = opt.Execution ?? new BacktestExecutionOptions()
            };

            _ = await RunAsync(request, progress: null, ct);
        }

        public async Task<BacktestResult> RunAsync(
            BacktestRunRequest request,
            IProgress<BacktestProgress>? progress,
            CancellationToken ct = default)
        {
            var result = new BacktestResult();
            var executionTrace = new BacktestExecutionTrace
            {
                DatasetSegment = request.DatasetSegment,
                ReplaySource = request.ReplaySource,
                UniverseMode = Normalize(request.UniverseMode, "SingleSymbol"),
                TurnoverTopNBoundary = request.TurnoverTopN > 0 ? request.TurnoverTopN : null,
                TurnoverSelectionActivated = string.Equals(request.UniverseMode, "TurnoverTopN", StringComparison.OrdinalIgnoreCase),
                MissingDataSummary = new BacktestMissingDataSummary()
            };
            result.ExecutionTrace = executionTrace;
            var throttler = new ProgressThrottler(progress);
            var counters = new AggregateCounters();
            var missingDataDays = new List<BacktestMissingDataDay>();
            var executedTradingDays = 0;

            var reportMode = Normalize(request.ReportMode, "C2");
            var stage = string.IsNullOrWhiteSpace(request.Stage) ? "Explore" : request.Stage.Trim();

            var start = request.StartDate;
            var end = request.EndDate;
            if (start == default || end == default)
            {
                var opt = _backtestOptions.Value;
                if (!TryParseDate(opt.StartDate, out start) || !TryParseDate(opt.EndDate, out end))
                {
                    var msg = "[BacktestHost] StartDate/EndDate invalid. Please use yyyy-MM-dd.";
                    _logger.LogError(msg);
                    result.Errors.Add(msg);
                    return result with { Succeeded = false };
                }
            }

            var timeMode = Normalize(request.TimeMode, "Range");
            if (timeMode.Equals("SingleDay", StringComparison.OrdinalIgnoreCase))
                end = start;

            // [Phase A] Universe Planning Pass
            var plan = await _universePlanner.BuildPlanAsync(request, start, end, ct);
            if (plan.TotalTradingDays == 0)
            {
                var msg = "[BacktestHost] No symbols planned for the specified date range according to Universe Plan.";
                _logger.LogWarning(msg);
                result.Errors.Add(msg);
                return result with { Succeeded = false };
            }

            var dayTotal = plan.TotalTradingDays;
            int dayIndex = 0;

            throttler.Report(new BacktestProgress
            {
                Phase = BacktestProgressPhase.Loading,
                DayIndex = 0,
                DayTotal = dayTotal
            }, force: true);

            // [Phase B] Day Bucket Execution
            foreach (var day in plan.DayResults.OrderBy(x => x.TradeDate))
            {
                if (ct.IsCancellationRequested) break;

                var tradeDate = day.TradeDate;
                var symbols = day.IsSuccessful && plan.SymbolsByDate.TryGetValue(tradeDate, out var plannedSymbols)
                    ? plannedSymbols
                    : Array.Empty<string>();
                executionTrace.RankingSnapshotDate = day.RankingSnapshotDate;
                executionTrace.RankingSnapshotPath = day.RankingSnapshotPath;
                dayIndex++;

                if (!day.IsSuccessful)
                {
                    RegisterMissingDataDay(
                        result,
                        missingDataDays,
                        tradeDate,
                        day.RankingSnapshotDate,
                        day.RankingSnapshotPath,
                        day.MissingReason ?? "MissingData",
                        day.Notes);
                    counters.SuppressedNoData++;
                    throttler.Report(counters.ToProgress(dayIndex, dayTotal, 0, 0, tradeDate, null, phase: BacktestProgressPhase.Running), force: true);
                    continue;
                }

                _logger.LogInformation("[BacktestHost] === Run Date: {Date} | Mode: {Mode} | Symbols ({Count}) ===",
                    tradeDate, request.UniverseMode, symbols.Length);
                if (symbols.Length > 0)
                {
                    _logger.LogDebug("[BacktestHost] Symbols for {Date}: {SymbolList}", tradeDate, string.Join(", ", symbols));
                }

                if (_holidayCalendar.IsHoliday(tradeDate))
                {
                    // 為了效能與日誌乾淨度，遇到假日直接跳過，不再寫入 NON_TRADING_DAY 紀錄
                    continue;
                }

                // [Phase C] 呼叫 Warmup Cache Manager (由其內部發佈 IND_READY 等日誌)
                // 從 DI 取得 StrategyRegistry，找出這次指定的 Strategy Template 及其預熱需求
                using (var prepScope = _scopeFactory.CreateScope())
                {
                    // 在 warmup 開始前先將回測時鐘錨定到本次交易日的有效時刻，
                    // 避免 LocalDiskKLineProvider 讀到 SimClock 預設值而把日期算到 0001-01-01。
                    _clock.AdvanceTo(new DateTime(tradeDate.Year, tradeDate.Month, tradeDate.Day, 9, 0, 0, DateTimeKind.Utc));

                    var registry = prepScope.ServiceProvider.GetRequiredService<MuNTrade.Abstractions.Contracts.Strategy.IStrategyRegistry>();
                    var template = registry.Templates.FirstOrDefault(t => t.TemplateId.Value == request.StrategyTemplateId);
                    var reqs = template?.WarmupRequirements ?? Array.Empty<MuNTrade.Abstractions.Contracts.Strategy.WarmupRequirement>();
                    var warmupTimeFrames = reqs.Select(r => r.TimeFrame).Distinct().ToArray();
                    var maxLookback = reqs.Any() ? reqs.Max(r => r.LookbackRows) : 0;

                    if (warmupTimeFrames.Length > 0 && maxLookback > 0)
                    {
                        executionTrace.WarmupActivated = true;
                        executionTrace.DataInventoryActivated = true;
                        var warmupReport = await _warmupCacheManager.PrepareAsync(tradeDate, symbols, warmupTimeFrames, maxLookback, plan, ct);
                        if (!warmupReport.Succeeded)
                        {
                            var notes = string.Join(
                                " | ",
                                warmupReport.MissingIssues.Select(x =>
                                    $"symbol={x.Symbol};tf={x.TimeFrame};range={x.ExpectedFrom:yyyy-MM-dd}..{x.ExpectedTo:yyyy-MM-dd};reason={x.Reason}"));
                            RegisterMissingDataDay(
                                result,
                                missingDataDays,
                                tradeDate,
                                day.RankingSnapshotDate,
                                day.RankingSnapshotPath,
                                "MissingWarmupData",
                                notes);
                            counters.SuppressedNoData++;
                            throttler.Report(counters.ToProgress(dayIndex, dayTotal, 0, symbols.Length, tradeDate, null, phase: BacktestProgressPhase.Running), force: true);
                            continue;
                        }
                    }
                }

                var symbolTotal = symbols.Length;
                if (symbolTotal == 0)
                {
                    var skip = new BacktestRunSummary(
                        tradeDate,
                        "-",
                        0,
                        0m,
                        0,
                        stage,
                        reportMode,
                        true,
                        "NO_SYMBOLS");
                    result.Summaries.Add(skip);
                    result.Skips.Add(new BacktestSkipInfo(tradeDate, "-", "NO_SYMBOLS"));
                    counters.SuppressedNoData++;
                    await _reportWriter.AppendSummaryAsync(skip, ct);
                    throttler.Report(counters.ToProgress(dayIndex, dayTotal, 0, symbolTotal, tradeDate, "-", phase: BacktestProgressPhase.Running), force: true);
                    continue;
                }

                var missingTickPaths = symbols
                    .Select(sym => new { Symbol = sym, Path = GetTickPath(tradeDate, sym), Exists = HasTickFile(tradeDate, sym) })
                    .Where(x => !x.Exists)
                    .ToArray();

                if (missingTickPaths.Length > 0)
                {
                    var details = string.Join(" | ", missingTickPaths.Select(x => $"{x.Symbol}:{x.Path}"));
                    if (!request.SkipMissingData)
                    {
                        var msg = $"Missing tick replay tradeDate={tradeDate:yyyy-MM-dd}; expected_paths={details}";
                        _logger.LogError("[BacktestHost] {Message}", msg);
                        result.Errors.Add(msg);
                        throttler.Report(counters.ToProgress(dayIndex, dayTotal, 0, symbolTotal, tradeDate, null, phase: BacktestProgressPhase.Error), force: true);
                        throw new FileNotFoundException(msg);
                    }

                    RegisterMissingDataDay(
                        result,
                        missingDataDays,
                        tradeDate,
                        day.RankingSnapshotDate,
                        string.Join(";", missingTickPaths.Select(x => x.Path)),
                        "MissingTickReplay",
                        details);
                    counters.SuppressedNoData++;
                    throttler.Report(counters.ToProgress(dayIndex, dayTotal, 0, symbolTotal, tradeDate, null, phase: BacktestProgressPhase.Running), force: true);
                    continue;
                }

                using (var dayScope = _scopeFactory.CreateScope())
                {
                    var freezer = dayScope.ServiceProvider.GetRequiredService<BacktestDailyLossFreezer>();
                    freezer.EnsureFrozen(tradeDate);
                }

                int symbolIndex = 0;
                var executedThisDay = false;
                foreach (var sym in symbols)
                {
                    if (ct.IsCancellationRequested) break;
                    symbolIndex++;

                    throttler.Report(counters.ToProgress(dayIndex, dayTotal, symbolIndex, symbolTotal, tradeDate, sym, phase: BacktestProgressPhase.Running), force: true);

                    executionTrace.TickReplayActivated = true;
                    var run = await RunSingleSymbolOneDayAsync(
                        tradeDate,
                        sym,
                        request,
                        stage,
                        reportMode,
                        plan,
                        tickTs =>
                        {
                            counters.TicksProcessed++;
                            throttler.Report(counters.ToProgress(dayIndex, dayTotal, symbolIndex, symbolTotal, tradeDate, sym, tickTs, BacktestProgressPhase.Running));
                        },
                        ct);

                    executedThisDay = true;
                    counters.Merge(run.Counters);
                    result.Summaries.Add(run.Summary);
                    if (!string.IsNullOrWhiteSpace(run.DetailPath))
                    {
                        result.DetailPaths.Add(run.DetailPath);
                        result.EventJournalPaths.Add(run.DetailPath);
                    }

                    throttler.Report(counters.ToProgress(dayIndex, dayTotal, symbolIndex, symbolTotal, tradeDate, sym, phase: BacktestProgressPhase.WritingReport), force: true);
                    await _reportWriter.AppendSummaryAsync(run.Summary, ct);
                    throttler.Report(counters.ToProgress(dayIndex, dayTotal, symbolIndex, symbolTotal, tradeDate, sym, phase: BacktestProgressPhase.Running), force: true);
                }

                if (executedThisDay)
                    executedTradingDays++;
            }

            executionTrace.MissingDataSummary = BuildMissingDataSummary(plan.TotalTradingDays, executedTradingDays, missingDataDays);

            if (executedTradingDays == 0 && missingDataDays.Count > 0 && result.Errors.Count == 0)
            {
                result.Errors.Add(
                    $"Layer1 research did not execute any valid trading day. executed_trading_days=0 total_trading_days={plan.TotalTradingDays} skipped_trading_days={missingDataDays.Count}.");
            }

            result = result with
            {
                Succeeded = result.Errors.Count == 0,
                SummaryPath = _reportWriter.GetSummaryPath()
            };

            throttler.Report(counters.ToProgress(dayTotal, dayTotal, 0, 0, null, null, phase: BacktestProgressPhase.Done), force: true);
            return result;
        }

        private async Task<BacktestRunResult> RunSingleSymbolOneDayAsync(
            DateOnly tradeDate,
            string symbol,
            BacktestRunRequest request,
            string stage,
            string reportMode,
            BacktestUniversePlan plan,
            Action<DateTime>? onTick,
            CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;

            var bus = sp.GetRequiredService<IEventBus>();
            var clock = sp.GetRequiredService<IClock>();
            var sessionFilter = sp.GetRequiredService<SessionFilterService>();
            var klineSvc = sp.GetRequiredService<MuNTrade.Core.DataProcessing.KLine.KLineBuilderService>();
            var router = sp.GetRequiredService<MarketEventRouter>();
            var strategyHost = sp.GetRequiredService<IStrategyHost>();
            var subMgr = sp.GetRequiredService<ISubscriptionManager>();
            if (subMgr is not BacktestSubscriptionManager)
                throw new InvalidOperationException($"Backtest requires {nameof(BacktestSubscriptionManager)} but resolved {subMgr.GetType().Name}.");
            var orderGateway = sp.GetRequiredService<BacktestOrderGateway>();
            var ledger = sp.GetRequiredService<StrategyLedgerService>();
            var tickSource = sp.GetRequiredService<ITickSource>();
            _ = sp.GetRequiredService<IPositionManagerService>();
            _ = sp.GetRequiredService<BacktestRiskCounterService>();
            _ = sp.GetRequiredService<StrategyIntentExecutionAdapter>();
            _ = sp.GetRequiredService<BacktestPriceCache>();
            _ = sp.GetRequiredService<MuNTrade.Abstractions.Contracts.Indicators.IIndicatorCalculator>();
            var strategyParamService = sp.GetRequiredService<IStrategyParamService>();

            using var journal = new BacktestEventJournal(bus);
            var counters = new BacktestRunCounters(bus);
            var universeInjected = false;
            var strategyHostStarted = false;
            var instanceStarted = false;
            StrategyInstanceId instanceId = default;
            string scopedStrategyId = string.Empty;
            long pulseCount = 0;

            try
            {
                await router.StartAsync(ct);
                await strategyHost.StartAsync(ct);
                strategyHostStarted = true;

                var baseParams = strategyParamService.GetParameters(request.StrategyTemplateId);
                var effectiveParams = baseParams;
                strategyParamService.SetScopedOverride(request.StrategyTemplateId, effectiveParams);

                // 訂閱該 symbol 作為 universe

                // [Phase C] 拿取預熱資料塞入 IKLineStore 與 IndicatorCalculator
                var klineStore = sp.GetRequiredService<MuNTrade.Abstractions.Contracts.Data.IKLineStore>();
                var indicatorCalc = sp.GetRequiredService<MuNTrade.Abstractions.Contracts.Indicators.IIndicatorCalculator>();
                var registry = sp.GetRequiredService<MuNTrade.Abstractions.Contracts.Strategy.IStrategyRegistry>();
                var template = registry.Templates.FirstOrDefault(t => t.TemplateId.Value == request.StrategyTemplateId);
                var reqs = template?.WarmupRequirements ?? Array.Empty<MuNTrade.Abstractions.Contracts.Strategy.WarmupRequirement>();

                var warmupBarsByTf = new Dictionary<string, IReadOnlyList<JsonKLineRecord>>();

                foreach (var tf in reqs.Select(r => r.TimeFrame).Distinct())
                {
                    var bars = _warmupCacheManager.GetWarmedUpBars(symbol, tf);
                    var tfStr = tf.TotalMinutes == 1440 ? "1d" : $"{tf.TotalMinutes}m";

                    if (bars.Count > 0)
                    {
                        var records = bars.Select(b => new JsonKLineRecord
                        {
                            KlineTime = b.StartTime.Value,
                            Open = (double)b.Open,
                            High = (double)b.High,
                            Low = (double)b.Low,
                            Close = (double)b.Close,
                            Qty = b.Volume
                        }).ToList();

                        warmupBarsByTf[tfStr] = records;
                        klineStore.PutInitial(symbol, tfStr, records);
                    }
                    else
                    {
                        warmupBarsByTf[tfStr] = Array.Empty<JsonKLineRecord>();
                    }

                    klineStore.MarkTfReady(symbol, tfStr);
                    bus.Publish(new MuNTrade.Abstractions.Events.System.Warmup.TfReadyEvent(symbol, tfStr, bars.Count));
                }

                // [Phase C.2] 發送指標初始化用的請求 (P4 TODO 17)
                var indReq = new MuNTrade.Abstractions.Events.MarketData.IndicatorInitializeRequest(symbol, warmupBarsByTf)
                {
                    OccurredAt = ToSafeOffset(clock.Now)
                };
                // 直流與 EventBus 雙發以涵蓋兩種可能監聽模式
                indicatorCalc.OnIndicatorInitialize(indReq);
                bus.Publish(indReq);

                // 指標計算模組通常監聽此事件或 KLineStore 快照
                bus.Publish(new MuNTrade.Abstractions.Events.System.Warmup.SymbolReadyEvent(symbol));

                // Inject backtest universe before strategy StartInstance so StartAsync() can read watchlist.
                subMgr.SubscribeTicks(symbol);
                universeInjected = true;
                _logger.LogInformation("[BacktestHost] BT.UNIVERSE.INJECT symbol={Symbol}", symbol);
                _logger.LogInformation(
                    "[BacktestHost] BT.STRATEGY.START_SCOPE symbol={Symbol} tickSubsCount={Count} tickSubs={TickSubs}",
                    symbol,
                    subMgr.CurrentTickSubscriptions.Count,
                    string.Join(",", subMgr.CurrentTickSubscriptions));

                // 確保預熱跟就緒事件都完成後，才讓策略實體啟動 (P1 TODO 8)
                var templateId = new StrategyRecipeId(request.StrategyTemplateId);
                instanceId = await strategyHost.StartInstanceAsync(templateId, request.AccountKey, ct);
                instanceStarted = true;
                scopedStrategyId = StrategyIdentity.BuildStrategyId(request.StrategyTemplateId, instanceId.Value);
                counters.SetScope(scopedStrategyId, instanceId.Value);

                // resolve session window (改用 GetPeriodWindow 以取得完整的日盤與夜盤時間，解決期貨跨日問題)
                var dayCfg = new MuNTrade.Abstractions.Settings.Data.BarConfig { Type = "Day", Period = 1 };
                var window = _sessionProvider.GetPeriodWindow(symbol, dayCfg, tradeDate.ToDateTime(new TimeOnly(12, 0)));
                var fromTime = window.WindowStart;
                var toTime = window.WindowEnd;
                var sessionOpen = ResolveSessionOpenForDiagnostics(symbol, fromTime);
                var entryWindow = ResolveEntryWindowForDiagnostics(request.Risk, sessionOpen);
                counters.SetEntryWindow(entryWindow.SessionOpen, entryWindow.EntryOpen, entryWindow.EntryClose, entryWindow.EntryCloseMode);

                long totalTicks = 0;
                long publishedTicks = 0;
                long lastPulseSecond = -1;
                int pulseSeq = 0;
                var pulseSessionId = $"BT-{tradeDate:yyyyMMdd}-{symbol}";

                _logger.LogInformation(
                    "[BacktestHost] {Symbol} @ {Date} replay starting. Window: {Start:HH:mm:ss} - {End:HH:mm:ss}. Path: {Path}",
                    symbol, tradeDate, fromTime, toTime, GetTickPath(tradeDate, symbol));
                    _logger.LogInformation("[BacktestHost] Pulse mode enabled. mode=PerSimSecond symbol={Symbol}", symbol);

                    await foreach (var tick in tickSource.StreamTicksAsync(symbol, fromTime, toTime, ct))
                    {
                    totalTicks++;
                    if (ct.IsCancellationRequested) break;

                    clock.AdvanceTo(tick.ExchangeTs.Value);
                    onTick?.Invoke(tick.ExchangeTs.Value);
                    var filtered = sessionFilter.ProcessTick(tick);
                    if (filtered == null) continue;

                    var tradeEvent = new TradeTickEvent
                    {
                        Symbol = filtered.StockId,
                        ExchangeTs = filtered.ExchangeTs,
                        ArrivalTs = filtered.ArrivalTs,
                        OccurredAt = ToSafeOffset(clock.Now),
                        Price = filtered.Price,
                        Volume = (int)Math.Clamp(filtered.Volume, 0, int.MaxValue),
                        FeedKind = TickFeedKind.History,
                        IsSimulated = true,
                        Source = MarketSource.Domestic,
                        MarketNo = MarketNo.Listing
                    };

                    // 先讓 KLine 聚合 (這可能會拋出 Bar 結束的事件)
                    klineSvc.ProcessTick(filtered);

                    // 然後 Publish 本根 Tick 事件給策略，這樣順序才是:
                    // 1. Bar Close (上一根結束) -> 2. 新 Tick進來
                    bus.Publish(tradeEvent);
                    publishedTicks++;

                    // 每個模擬秒最多一個 pulse；不補空秒，僅在跨秒時發出。
                    var pulseSecond = tick.ExchangeTs.Value.Ticks / TimeSpan.TicksPerSecond;
                    if (pulseSecond != lastPulseSecond)
                    {
                        lastPulseSecond = pulseSecond;
                        pulseSeq++;
                        pulseCount++;
                        bus.Publish(new StrategyPulseEvent(
                            pulseSeq,
                            "BacktestPulse",
                            ToSafeOffset(clock.Now),
                            pulseSessionId));
                    }

                    // 最後才由模擬撮合處裡看有沒有觸價
                    orderGateway.OnTick(tradeEvent);
                }

                _logger.LogInformation(
                    "[BacktestHost] {Symbol} @ {Date} replay ended. Processed: {Total} / Published: {Published} ticks.",
                    symbol, tradeDate, totalTicks, publishedTicks);
                counters.SetPulseCount(pulseCount);

                // 強制平倉
                ForceClosePositions(
                    sp,
                    bus,
                    clock,
                    symbol,
                    scopedStrategyId,
                    request.AccountKey);

                // flush bars
                klineSvc.BuilderCore?.FlushOpenBars();

                await strategyHost.StopInstanceAsync(instanceId, ct);
                instanceStarted = false;
                await strategyHost.StopAsync(ct);
                strategyHostStarted = false;
                subMgr.UnsubscribeTicks(symbol);
                universeInjected = false;
                _logger.LogInformation("[BacktestHost] BT.UNIVERSE.RELEASE symbol={Symbol}", symbol);

                if (journal.HasEntries)
                {
                    var detailPath = _reportWriter.GetDetailPath(tradeDate, symbol);
                    await _reportWriter.WriteDetailAsync(detailPath, journal, ct);
                    return new BacktestRunResult(
                        BuildSummary(tradeDate, symbol, stage, reportMode, ledger, request.StrategyTemplateId, instanceId),
                        counters,
                        detailPath);
                }

                return new BacktestRunResult(
                    BuildSummary(tradeDate, symbol, stage, reportMode, ledger, request.StrategyTemplateId, instanceId),
                    counters,
                    null);
            }
            finally
            {
                if (instanceStarted)
                {
                    try
                    {
                        await strategyHost.StopInstanceAsync(instanceId, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[BacktestHost] Failed to stop strategy instance during cleanup. symbol={Symbol}", symbol);
                    }
                }

                if (strategyHostStarted)
                {
                    try
                    {
                        await strategyHost.StopAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[BacktestHost] Failed to stop strategy host during cleanup. symbol={Symbol}", symbol);
                    }
                }

                if (universeInjected)
                {
                    try
                    {
                        subMgr.UnsubscribeTicks(symbol);
                        _logger.LogInformation("[BacktestHost] BT.UNIVERSE.RELEASE symbol={Symbol}", symbol);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[BacktestHost] Failed to release backtest universe. symbol={Symbol}", symbol);
                    }
                }

                counters.SetPulseCount(pulseCount);
                _logger.LogInformation(
                    "[BacktestHost] BT.RUN.SUMMARY symbol={Symbol} pulseCount={PulseCount} bars5mCountAtFirstEvaluablePulse={Bars5m} entryCandidateFirst={EntryCandidateFirst} entryCandidateLast={EntryCandidateLast} entryCandidateBeforeCloseCount={EntryCandidateBeforeCloseCount} entryCandidateAfterCloseCount={EntryCandidateAfterCloseCount} blockedReasons={BlockedReasons} lockReasons={LockReasons} timeWindowLockFirst={TimeWindowLockFirst} strategyParamsActive={StrategyParamsActive} entryWindow.sessionOpen={SessionOpen} entryWindow.entryOpen={EntryOpen} entryWindow.entryClose={EntryClose} entryWindow.entryCloseMode={EntryCloseMode} suppressedUniverse={SuppressedUniverse} notInWatchlist={NotInWatchlist} suppressedNeutral={SuppressedNeutral} suppressedRisk={SuppressedRisk} suppressedTime={SuppressedTime} suppressedTtl={SuppressedTtl} entryCandidateCount={EntryCandidateCount} entryIntentCount={EntryIntentCount} completedTrades={CompletedTrades}",
                    symbol,
                    counters.PulseCount,
                    counters.Bars5mCountAtFirstEvaluablePulse,
                    counters.EntryCandidateFirstTimeText,
                    counters.EntryCandidateLastTimeText,
                    counters.EntryCandidateBeforeCloseCount,
                    counters.EntryCandidateAfterCloseCount,
                    counters.BlockedReasonsSummary,
                    counters.LockReasonsSummary,
                    counters.TimeWindowLockFirstText,
                    counters.StrategyParamsActiveSummary,
                    counters.SessionOpenText,
                    counters.EntryOpenText,
                    counters.EntryCloseText,
                    counters.EntryCloseMode,
                    counters.SuppressedUniverse,
                    counters.SuppressedNotInWatchlist,
                    counters.SuppressedNeutral,
                    counters.SuppressedRisk,
                    counters.SuppressedTime,
                    counters.SuppressedTtl,
                    counters.EntryCandidateCount,
                    counters.EntryIntentCount,
                    counters.Trades);
                strategyParamService.ClearScopedOverride(request.StrategyTemplateId);

                counters.Dispose();
                journal?.Dispose();
                await router.StopAsync(ct);
            }
        }

        private static void RegisterMissingDataDay(
            BacktestResult result,
            List<BacktestMissingDataDay> missingDataDays,
            DateOnly tradeDate,
            DateOnly? rankingSnapshotDate,
            string? expectedPath,
            string missingReason,
            string? notes)
        {
            missingDataDays.Add(new BacktestMissingDataDay
            {
                TradeDate = tradeDate,
                RankingSnapshotDate = rankingSnapshotDate,
                ExpectedPath = expectedPath,
                MissingReason = missingReason,
                Severity = "day_skip",
                Notes = notes
            });

            result.Skips.Add(new BacktestSkipInfo(tradeDate, "-", missingReason));
        }

        private static BacktestMissingDataSummary BuildMissingDataSummary(
            int totalTradingDays,
            int executedTradingDays,
            IReadOnlyList<BacktestMissingDataDay> details)
        {
            return new BacktestMissingDataSummary
            {
                TotalTradingDays = totalTradingDays,
                ExecutedTradingDays = executedTradingDays,
                SkippedTradingDays = details.Count,
                MissingTurnoverSnapshotDays = details.Count(x => string.Equals(x.MissingReason, "MissingPreviousTradingDayTurnoverSnapshot", StringComparison.Ordinal)),
                MissingTickReplayDays = details.Count(x => string.Equals(x.MissingReason, "MissingTickReplay", StringComparison.Ordinal)),
                MissingWarmupDays = details.Count(x => string.Equals(x.MissingReason, "MissingWarmupData", StringComparison.Ordinal)),
                Details = details.ToArray()
            };
        }

        private bool HasTickFile(DateOnly date, string symbol)
        {
            var path = GetTickPath(date, symbol);
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }

        private string GetTickPath(DateOnly date, string symbol)
        {
            var root = _dataOptions.Value.TickDataBasePath;
            if (string.IsNullOrWhiteSpace(root)) return string.Empty;
            return Path.Combine(root, symbol, $"{date:yyyyMMdd}_D_{symbol}.csv.gz");
        }

        private async Task<List<string>> ResolveSymbolsAsync(BacktestRunRequest request, DateOnly tradeDate, CancellationToken ct)
        {
            var mode = Normalize(request.UniverseMode, "SingleSymbol");
            if (mode.Equals("TurnoverTopN", StringComparison.OrdinalIgnoreCase) ||
                mode.Equals("TurnoverList", StringComparison.OrdinalIgnoreCase))
            {
                var list = await _turnoverSelector.GetSymbolsAsync(tradeDate.ToDateTime(TimeOnly.MinValue), request.TurnoverTopN);
                return list.ToList();
            }

            if (mode.Equals("ManualList", StringComparison.OrdinalIgnoreCase))
            {
                return request.ManualList?.Where(s => !string.IsNullOrWhiteSpace(s))
                           .Select(s => s.Trim())
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToList()
                       ?? new List<string>();
            }

            if (!string.IsNullOrWhiteSpace(request.Symbol))
                return new List<string> { request.Symbol.Trim() };

            return request.ManualList?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                   ?? new List<string>();
        }

        private BacktestRunSummary BuildSummary(
            DateOnly tradeDate,
            string symbol,
            string stage,
            string reportMode,
            StrategyLedgerService ledger,
            string templateId,
            StrategyInstanceId instanceId)
        {
            var strategyId = StrategyIdentity.BuildStrategyId(templateId, instanceId.Value);
            var trades = ledger.GetTrades(strategyId).ToList();
            var netPnL = trades.Sum(t => t.NetPnL);
            var wins = trades.Count(t => t.NetPnL > 0);
            var winRate = trades.Count > 0 ? (double)wins / trades.Count : 0d;

            return new BacktestRunSummary(
                tradeDate,
                symbol,
                trades.Count,
                netPnL,
                winRate,
                stage,
                reportMode,
                false,
                null);
        }

        private static void ForceClosePositions(
            IServiceProvider sp,
            IEventBus bus,
            IClock clock,
            string symbol,
            string strategyId,
            string accountKey)
        {
            var posMgr = sp.GetService<IPositionManagerService>();
            if (posMgr == null) return;

            foreach (var pos in posMgr.GetAllOpenPositions())
            {
                if (pos.Quantity == 0) continue;
                if (!string.Equals(pos.StockId, symbol, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(pos.AccountKey) &&
                    !string.Equals(pos.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(pos.StrategyId) &&
                    !string.Equals(pos.StrategyId, strategyId, StringComparison.OrdinalIgnoreCase)) continue;

                var side = pos.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy;
                var fill = new OrderFillEvent
                {
                    StockId = pos.StockId,
                    FillTime = clock.Now,
                    Side = side,
                    FillPrice = pos.LastPrice,
                    FillQuantity = Math.Abs(pos.Quantity),
                    OriginalReason = "BacktestForceClose",
                    Account = string.IsNullOrWhiteSpace(pos.Account) ? accountKey : pos.Account,
                    AccountKey = string.IsNullOrWhiteSpace(pos.AccountKey) ? accountKey : pos.AccountKey,
                    StrategyId = string.IsNullOrWhiteSpace(pos.StrategyId) ? strategyId : pos.StrategyId,
                    CorrelationId = $"BT_FORCECLOSE_{pos.StockId}_{clock.Now:yyyyMMddHHmmssfff}"
                };

                bus.Publish(fill);
            }
        }

        private static bool TryParseDate(string text, out DateOnly date)
        {
            return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        private static DateTimeOffset ToSafeOffset(DateTime value)
        {
            if (value == DateTime.MinValue)
            {
                return DateTimeOffset.MinValue;
            }

            return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        }

        private static string Normalize(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            return value.Trim();
        }

        private TimeSpan ResolveSessionOpenForDiagnostics(string symbol, DateTime probeTime)
        {
            var lookupSymbol = ResolveSessionLookupSymbol(symbol);
            var session = _sessionProvider.GetSession(lookupSymbol, probeTime);
            if (session != null)
                return session.StartTime.TimeOfDay;

            try
            {
                var dayCfg = new MuNTrade.Abstractions.Settings.Data.BarConfig { Type = "Day", Period = 1 };
                var window = _sessionProvider.GetPeriodWindow(lookupSymbol, dayCfg, probeTime);
                if (window.WindowStart != default)
                    return window.WindowStart.TimeOfDay;
            }
            catch
            {
                // best effort diagnostics fallback
            }

            return IsDerivativesSessionSymbol(lookupSymbol) ? new TimeSpan(15, 0, 0) : new TimeSpan(9, 0, 0);
        }

        private static string ResolveSessionLookupSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return "TX00";
            var normalized = symbol.Trim();
            return IsDerivativesSessionSymbol(normalized) ? "TX00" : normalized;
        }

        private static bool IsDerivativesSessionSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            return symbol.StartsWith("TX", StringComparison.OrdinalIgnoreCase)
                || symbol.StartsWith("MTX", StringComparison.OrdinalIgnoreCase);
        }

        private static EntryWindowInfo ResolveEntryWindowForDiagnostics(BacktestRiskOptions risk, TimeSpan sessionOpen)
        {
            var delayOpen = TimeSpan.FromMinutes(Math.Max(0, risk.DelayOpenMinutes));
            var entryOpen = sessionOpen.Add(delayOpen);
            var stopRaw = Math.Max(0, risk.StopNewEntryAfterMinutes);
            if (stopRaw <= 0)
                return new EntryWindowInfo(sessionOpen, entryOpen, null, "Disabled");

            var mode = stopRaw >= (int)sessionOpen.TotalMinutes ? "AbsoluteClock" : "RelativeAfterOpen";
            var entryClose = mode == "AbsoluteClock"
                ? TimeSpan.FromMinutes(stopRaw)
                : sessionOpen.Add(TimeSpan.FromMinutes(stopRaw));
            return new EntryWindowInfo(sessionOpen, entryOpen, entryClose, mode);
        }

        private sealed record BacktestRunResult(BacktestRunSummary Summary, BacktestRunCounters Counters, string? DetailPath);
        private sealed record EntryWindowInfo(TimeSpan SessionOpen, TimeSpan EntryOpen, TimeSpan? EntryClose, string EntryCloseMode);

        private sealed class BacktestRunCounters : IDisposable
        {
            private readonly List<IDisposable> _subs = new();
            private readonly Dictionary<string, int> _blockedReasons = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _lockReasons = new(StringComparer.OrdinalIgnoreCase);

            public int Trades { get; private set; }
            public int Wins { get; private set; }
            public decimal NetPnL { get; private set; }
            public int SuppressedRisk { get; private set; }
            public int SuppressedTime { get; private set; }
            public int SuppressedNeutral { get; private set; }
            public int SuppressedTtl { get; private set; }
            public int SuppressedUniverse { get; private set; }
            public int SuppressedNotInWatchlist { get; private set; }
            public int EntryCandidateCount { get; private set; }
            public int EntryIntentCount { get; private set; }
            public int Bars5mCountAtFirstEvaluablePulse { get; private set; } = -1;
            public long PulseCount { get; private set; }
            public TimeSpan? EntryCandidateFirstTime { get; private set; }
            public TimeSpan? EntryCandidateLastTime { get; private set; }
            public int EntryCandidateBeforeCloseCount { get; private set; }
            public int EntryCandidateAfterCloseCount { get; private set; }
            public TimeSpan SessionOpen { get; private set; } = new TimeSpan(9, 0, 0);
            public TimeSpan EntryOpen { get; private set; } = new TimeSpan(9, 0, 0);
            public TimeSpan? EntryClose { get; private set; }
            public string EntryCloseMode { get; private set; } = "Unknown";
            public TimeSpan? TimeWindowLockFirst { get; private set; }
            public string StrategyParamsActiveSummary { get; private set; } = "None";
            private string? _expectedStrategyId;
            private string? _expectedInstanceId;

            public string EntryCandidateFirstTimeText => FormatTime(EntryCandidateFirstTime);
            public string EntryCandidateLastTimeText => FormatTime(EntryCandidateLastTime);
            public string TimeWindowLockFirstText => FormatTime(TimeWindowLockFirst);
            public string SessionOpenText => FormatTime(SessionOpen);
            public string EntryOpenText => FormatTime(EntryOpen);
            public string EntryCloseText => FormatTime(EntryClose);
            public string BlockedReasonsSummary => FormatCounts(_blockedReasons);
            public string LockReasonsSummary => FormatCounts(_lockReasons);

            public BacktestRunCounters(IEventBus bus)
            {
                _subs.Add(bus.Subscribe<StrategyPacketEmittedEvent>(OnPacket));
                _subs.Add(bus.Subscribe<StrategyIntentEmittedEvent>(OnIntent));
                _subs.Add(bus.Subscribe<CompletedTrade>(OnTrade));
            }

            public void SetScope(string strategyId, string instanceId)
            {
                _expectedStrategyId = strategyId;
                _expectedInstanceId = instanceId;
            }

            private void OnPacket(StrategyPacketEmittedEvent evt)
            {
                if (evt == null) return;
                if (string.IsNullOrWhiteSpace(_expectedInstanceId)) return;
                if (!string.Equals(evt.InstanceId.Value, _expectedInstanceId, StringComparison.OrdinalIgnoreCase)) return;

                var code = evt.ActionCode ?? string.Empty;
                if (code.Equals("SUPPRESSED_BY_RISK", StringComparison.OrdinalIgnoreCase))
                {
                    SuppressedRisk++;
                    return;
                }

                if (code.Equals("ENTRY_TTL_EXPIRED", StringComparison.OrdinalIgnoreCase))
                {
                    SuppressedTtl++;
                    return;
                }

                if (code.Equals("SUPPRESSED_BY_UNIVERSE", StringComparison.OrdinalIgnoreCase))
                {
                    SuppressedUniverse++;
                    var msg = evt.HumanReason ?? evt.Message ?? string.Empty;
                    if (msg.Contains("NOT_IN_WATCHLIST", StringComparison.OrdinalIgnoreCase))
                        SuppressedNotInWatchlist++;
                    return;
                }

                if (code.Equals("ENTRY_CANDIDATE", StringComparison.OrdinalIgnoreCase))
                {
                    EntryCandidateCount++;
                    var msg = evt.HumanReason ?? evt.Message ?? string.Empty;
                    if (TryReadTimeField(msg, "evalTs", out var evalTs))
                    {
                        if (!EntryCandidateFirstTime.HasValue || evalTs < EntryCandidateFirstTime.Value)
                            EntryCandidateFirstTime = evalTs;
                        if (!EntryCandidateLastTime.HasValue || evalTs > EntryCandidateLastTime.Value)
                            EntryCandidateLastTime = evalTs;

                        if (EntryClose.HasValue && evalTs >= EntryClose.Value) EntryCandidateAfterCloseCount++;
                        else EntryCandidateBeforeCloseCount++;
                    }

                    var blocked = ReadTokenField(msg, "blockedReason") ?? "PassedToIntent";
                    CountReason(_blockedReasons, blocked);
                    if (msg.Contains("blockedReason=TrendBiasNeutral", StringComparison.OrdinalIgnoreCase))
                        SuppressedNeutral++;
                    else if (msg.Contains("blockedReason=Locked", StringComparison.OrdinalIgnoreCase))
                        SuppressedTime++;
                    return;
                }

                if (code.Equals("STATE_CHANGED", StringComparison.OrdinalIgnoreCase))
                {
                    var msg = evt.HumanReason ?? evt.Message ?? string.Empty;
                    var state = ReadTokenField(msg, "state");
                    if (string.Equals(state, "Locked", StringComparison.OrdinalIgnoreCase))
                    {
                        var reason = ReadTokenField(msg, "reason") ?? "Unknown";
                        CountReason(_lockReasons, reason);
                    }
                    return;
                }

                if (code.Equals("BC.TIME_WINDOW.LOCK", StringComparison.OrdinalIgnoreCase))
                {
                    var msg = evt.HumanReason ?? evt.Message ?? string.Empty;
                    if (!TimeWindowLockFirst.HasValue && TryReadTimeField(msg, "now", out var lockTime))
                        TimeWindowLockFirst = lockTime;
                    return;
                }

                if (code.Equals("BC.PARAMS.ACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(StrategyParamsActiveSummary, "None", StringComparison.Ordinal))
                        return;
                    var msg = evt.HumanReason ?? evt.Message ?? string.Empty;
                    StrategyParamsActiveSummary = string.IsNullOrWhiteSpace(msg) ? "None" : msg;
                    return;
                }

                if (code.Equals("BC.FIRST_EVAL", StringComparison.OrdinalIgnoreCase))
                {
                    if (Bars5mCountAtFirstEvaluablePulse >= 0) return;
                    var msg = evt.HumanReason ?? evt.Message ?? string.Empty;
                    if (TryReadIntField(msg, "bars5m", out var bars))
                        Bars5mCountAtFirstEvaluablePulse = bars;
                }
            }

            private void OnIntent(StrategyIntentEmittedEvent evt)
            {
                if (evt == null) return;
                if (string.IsNullOrWhiteSpace(_expectedInstanceId)) return;
                if (!string.Equals(evt.InstanceId.Value, _expectedInstanceId, StringComparison.OrdinalIgnoreCase)) return;

                if (!string.IsNullOrWhiteSpace(evt.IntentKind) &&
                    evt.IntentKind.StartsWith("ENTRY_", StringComparison.OrdinalIgnoreCase))
                {
                    EntryIntentCount++;
                }
            }

            private void OnTrade(CompletedTrade trade)
            {
                if (string.IsNullOrWhiteSpace(_expectedStrategyId)) return;
                if (!string.Equals(trade.StrategyId, _expectedStrategyId, StringComparison.OrdinalIgnoreCase)) return;

                Trades++;
                NetPnL += trade.NetPnL;
                if (trade.NetPnL > 0) Wins++;
            }

            public void SetPulseCount(long pulseCount)
            {
                PulseCount = pulseCount;
            }

            public void SetEntryWindow(TimeSpan sessionOpen, TimeSpan entryOpen, TimeSpan? entryClose, string entryCloseMode)
            {
                SessionOpen = sessionOpen;
                EntryOpen = entryOpen;
                EntryClose = entryClose;
                EntryCloseMode = string.IsNullOrWhiteSpace(entryCloseMode) ? "Unknown" : entryCloseMode;
            }

            private static bool TryReadIntField(string message, string key, out int value)
            {
                value = 0;
                if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(key))
                    return false;

                var token = key + "=";
                var idx = message.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;
                idx += token.Length;
                if (idx >= message.Length) return false;

                var end = idx;
                while (end < message.Length && char.IsDigit(message[end]))
                    end++;

                if (end <= idx) return false;
                return int.TryParse(message[idx..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }

            private static bool TryReadTimeField(string message, string key, out TimeSpan time)
            {
                time = default;
                var raw = ReadTokenField(message, key);
                if (string.IsNullOrWhiteSpace(raw)) return false;
                return TimeSpan.TryParseExact(raw, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out time);
            }

            private static string? ReadTokenField(string message, string key)
            {
                if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(key)) return null;
                var token = key + "=";
                var idx = message.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;
                idx += token.Length;
                if (idx >= message.Length) return null;
                var end = idx;
                while (end < message.Length && !char.IsWhiteSpace(message[end]))
                    end++;
                if (end <= idx) return null;
                return message[idx..end];
            }

            private static void CountReason(Dictionary<string, int> map, string reason)
            {
                if (string.IsNullOrWhiteSpace(reason)) reason = "Unknown";
                if (!map.TryAdd(reason, 1))
                    map[reason]++;
            }

            private static string FormatCounts(Dictionary<string, int> map)
            {
                if (map.Count == 0) return "None";
                return string.Join(",",
                    map.OrderByDescending(x => x.Value)
                       .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                       .Select(x => $"{x.Key}:{x.Value}"));
            }

            private static string FormatTime(TimeSpan? value)
            {
                return value.HasValue ? value.Value.ToString(@"hh\:mm\:ss") : "None";
            }

            public void Dispose()
            {
                foreach (var sub in _subs)
                    sub.Dispose();
                _subs.Clear();
            }
        }

        private sealed class AggregateCounters
        {
            public long TicksProcessed { get; set; }
            public int Trades { get; private set; }
            public int Wins { get; private set; }
            public decimal NetPnL { get; private set; }
            public int SuppressedRisk { get; private set; }
            public int SuppressedTime { get; private set; }
            public int SuppressedNeutral { get; private set; }
            public int SuppressedTtl { get; private set; }
            public int SuppressedNoData { get; set; }

            public void Merge(BacktestRunCounters counters)
            {
                Trades += counters.Trades;
                Wins += counters.Wins;
                NetPnL += counters.NetPnL;
                SuppressedRisk += counters.SuppressedRisk;
                SuppressedTime += counters.SuppressedTime;
                SuppressedNeutral += counters.SuppressedNeutral;
                SuppressedTtl += counters.SuppressedTtl;
            }

            public BacktestProgress ToProgress(
                int dayIndex,
                int dayTotal,
                int symbolIndex,
                int symbolTotal,
                DateOnly? tradeDate,
                string? symbol,
                DateTime? tickTs = null,
                BacktestProgressPhase phase = BacktestProgressPhase.Running)
            {
                var winRate = Trades > 0 ? (double)Wins / Trades : 0d;
                return new BacktestProgress
                {
                    Phase = phase,
                    DayIndex = dayIndex,
                    DayTotal = dayTotal,
                    SymbolIndex = symbolIndex,
                    SymbolTotal = symbolTotal,
                    TicksProcessed = TicksProcessed,
                    CurrentTradeDate = tradeDate,
                    CurrentSymbol = symbol,
                    CurrentTickTs = tickTs,
                    Trades = Trades,
                    NetPnL = NetPnL,
                    WinRate = winRate,
                    SuppressedRisk = SuppressedRisk,
                    SuppressedTime = SuppressedTime,
                    SuppressedNeutral = SuppressedNeutral,
                    SuppressedTTL = SuppressedTtl,
                    SuppressedNoData = SuppressedNoData,
                    Skipped = SuppressedNoData
                };
            }
        }

        private sealed class ProgressThrottler
        {
            private readonly IProgress<BacktestProgress>? _progress;
            private DateTime _lastReport = DateTime.MinValue;
            private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(200);

            public ProgressThrottler(IProgress<BacktestProgress>? progress)
            {
                _progress = progress;
            }

            public void Report(BacktestProgress progress, bool force = false)
            {
                if (_progress == null) return;
                var now = DateTime.UtcNow;
                if (!force && (now - _lastReport) < MinInterval) return;
                _lastReport = now;
                _progress.Report(progress);
            }
        }
    }
}

