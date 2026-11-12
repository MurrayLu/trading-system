using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.MarketData.Enums;
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
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Indicators;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Time;
using MuNTrade.Core.Indicators.KLine;          // IKLineIndicator
using MuNTrade.Core.Indicators.KLine.SMA;      // SmaIndicator
using MuNTrade.Core.Indicators.KLine.ATR;      // AtrIndicator
using MuNTrade.Core.Settings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

public sealed class IndicatorCalculatorService : IIndicatorCalculator, IDisposable
{
    private readonly ILogger<IndicatorCalculatorService> _log;
    private readonly IEventBus _bus;
    private readonly IOptionsMonitor<IndicatorSettings> _opt;
    private readonly IClock _clock;

    private readonly ConcurrentDictionary<IndKey, IndPublishState> _lastIndPublish = new();

    // 每檔 × 每 TF → 該 TF 啟用的指標實例
    private readonly ConcurrentDictionary<string,
        ConcurrentDictionary<string, List<IKLineIndicator>>> _inds
        = new(StringComparer.OrdinalIgnoreCase);

    // Tick/Best5 狀態（非 K 線型指標）
    private readonly ConcurrentDictionary<string, SymbolState> _legacyStates = new();

    // 12-3: Warmup Suppression Tracker (Symbol -> Suppressed Count)
    private readonly ConcurrentDictionary<string, int> _initializingSymbols = new();

    private readonly List<IDisposable> _subs = new();

    public IndicatorCalculatorService(
        ILogger<IndicatorCalculatorService> log,
        IEventBus bus,
        IOptionsMonitor<IndicatorSettings> opt,
        IClock clock)
    {
        _log = log;
        _bus = bus;
        _opt = opt;
        _clock = clock;

        _subs.Add(_bus.Subscribe<KLineClosedEvent>(e => ProcessClosedKLine(e.Bar, null)));
        _subs.Add(_bus.Subscribe<KLinePartialUpdatedEvent>(e => ProcessPartialKLine(e.PartialBar, null)));
        _subs.Add(_bus.Subscribe<IndicatorInitializeRequest>(OnIndicatorInitialize));
    }

    // ── IIndicatorCalculator 顯式入口 ──
    public void OnKLineClosed(KLine bar, IEnumerable<string>? enabledIndicators) => ProcessClosedKLine(bar, enabledIndicators);
    public void OnKLinePartial(KLine bar, IEnumerable<string>? enabledIndicators) => ProcessPartialKLine(bar, enabledIndicators);

    public void OnTick(TradeTickEvent tick, IEnumerable<string>? enabledIndicators)
    {
        if (tick == null || string.IsNullOrEmpty(tick.Symbol)) return;
        var s = _legacyStates.GetOrAdd(tick.Symbol, _ => new SymbolState());
        s.LastTradePrice = tick.Price;
        s.LastTradeVolume = tick.Volume;

        var values = new Dictionary<string, decimal>
        {
            ["last_price"] = tick.Price,
            ["last_vol"] = tick.Volume
        };

        if (s.LastBest5 != null)
        {
            var b5 = s.LastBest5;
            var bidSum = b5.Bids.Sum(x => (decimal)x.Volume);
            var askSum = b5.Asks.Sum(x => (decimal)x.Volume);
            var denom = bidSum + askSum;
            if (denom > 0) values["ofi"] = (bidSum - askSum) / denom;
            if (b5.Bids.Count > 0 && b5.Asks.Count > 0) values["spread"] = b5.Asks[0].Price - b5.Bids[0].Price;
        }

        if (tick.FeedKind == TickFeedKind.RealTime)
        {
            Publish(new IndicatorsUpdatedEvent
            {
                OccurredAt = new DateTimeOffset(_clock.Now),
                Symbol = tick.Symbol,
                Period = "tick",
                IsPartial = true,
                ExchangeTs = tick.ExchangeTs,
                ArrivalTs = tick.ArrivalTs,
                Values = values
            });
        }
    }

    public void OnBest5(QuoteBest5Event b5, IEnumerable<string>? enabledIndicators)
    {
        if (b5 == null || string.IsNullOrEmpty(b5.Symbol)) return;
        var s = _legacyStates.GetOrAdd(b5.Symbol, _ => new SymbolState());
        s.LastBest5 = b5;

        var values = new Dictionary<string, decimal>();
        var bidSum = b5.Bids.Sum(x => (decimal)x.Volume);
        var askSum = b5.Asks.Sum(x => (decimal)x.Volume);
        var denom = bidSum + askSum;
        if (denom > 0) values["ofi"] = (bidSum - askSum) / denom;
        if (b5.Bids.Count > 0 && b5.Asks.Count > 0) values["spread"] = b5.Asks[0].Price - b5.Bids[0].Price;

        Publish(new IndicatorsUpdatedEvent
        {
            OccurredAt = new DateTimeOffset(_clock.Now),
            Symbol = b5.Symbol,
            Period = "b5",
            IsPartial = true,
            ExchangeTs = default, // Best5 usually doesn't have ExchangeTs in this context, or we could use b5.ArrivalTs as fallback if needed, but strict separation suggests default.
            ArrivalTs = b5.ArrivalTs,
            Values = values
        });
    }

    // ── Warmup 初始化：把歷史交給指標封裝器 ──
    public void OnIndicatorInitialize(IndicatorInitializeRequest req)
    {
        if (req == null) return;

        // 12-3: Start Suppression Window
        _initializingSymbols[req.Symbol] = 0;

        _log.LogDebug("[IND] Start IndicatorInitializeRequest for {Sym}. Suppression Window OPEN.", req.Symbol);

        try
        {
            foreach (var (tfRaw, bars) in req.BarsByTimeframe ?? new Dictionary<string, IReadOnlyList<JsonKLineRecord>>())
            {
                var tf = CanonicalFromKey(tfRaw);
                _log.LogDebug("[IND]   TF={Tf} bars={Cnt}", tf, bars?.Count ?? 0);

                var list = GetOrCreateIndicators(req.Symbol, tf, requested: null);
                var mergedSeries = new Dictionary<string, IReadOnlyList<IndicatorSeriesPoint>>();

                foreach (var ind in list)
                {
                    var historyBars = bars ?? Array.Empty<JsonKLineRecord>();
                    var partial = ind.Initialize(req.Symbol, tf, historyBars);
                    if (partial != null)
                    {
                        foreach (var kv in partial)
                            mergedSeries[kv.Key] = kv.Value;
                    }
                }

                if (mergedSeries.Count > 0)
                {
                    _bus.Publish(new IndicatorsWarmupSeriesEvent
                    {
                        OccurredAt = new DateTimeOffset(_clock.Now),
                        Symbol = req.Symbol,
                        Period = tf,
                        Series = mergedSeries
                    });
                    _log.LogDebug("[IND.WarmupSeries] {Sym}/{Tf} -> {Keys}", req.Symbol, tf, string.Join(",", mergedSeries.Keys));
                }
            }
        }
        finally
        {
            // 12-3: End Suppression Window
            _initializingSymbols.TryRemove(req.Symbol, out int suppressedCount);
            _log.LogDebug("[IND.Ready] {Symbol} initialized. Suppressed {Count} events during warmup.", req.Symbol, suppressedCount);
        }
    }

    // ── K 線事件：分派給各指標，聚合輸出 ──
    private void ProcessClosedKLine(KLine bar, IEnumerable<string>? requested)
    {
        var (symbol, tf, o, h, l, c, ts) = ExtractOHLC(bar);
        if (string.IsNullOrEmpty(symbol)) return;

        var list = GetOrCreateIndicators(symbol, tf, requested);
        var bag = new Dictionary<string, decimal>(8);

        foreach (var ind in list)
            foreach (var kv in ind.OnClosed(symbol, tf, o, h, l, c, ts))
                bag[kv.Key] = kv.Value;

        if (bag.Count > 0)
        {
            // 12-3: Warmup Suppression Gate (Hard Verification)
            if (_initializingSymbols.ContainsKey(symbol))
            {
                _initializingSymbols.AddOrUpdate(symbol, 1, (_, current) => current + 1);
                // Suppressed.
                return;
            }

            Publish(new IndicatorsUpdatedEvent
            {
                OccurredAt = new DateTimeOffset(_clock.Now),
                Symbol = symbol,
                Period = tf,
                IsPartial = false,
                ExchangeTs = ts,
                ArrivalTs = default,
                Values = bag,
                SourceBar = bar
            });
        }
    }

    private void ProcessPartialKLine(KLine bar, IEnumerable<string>? requested)
    {
        var (symbol, tf, o, h, l, c, ts) = ExtractOHLC(bar);
        if (string.IsNullOrEmpty(symbol)) return;

        var list = GetOrCreateIndicators(symbol, tf, requested);
        var bag = new Dictionary<string, decimal>(8);

        foreach (var ind in list)
            foreach (var kv in ind.OnPartial(symbol, tf, o, h, l, c, ts))
                bag[kv.Key] = kv.Value;

        if (bag.Count > 0)
            Publish(new IndicatorsUpdatedEvent
            {
                OccurredAt = new DateTimeOffset(_clock.Now),
                Symbol = symbol,
                Period = tf,
                IsPartial = true,
                ExchangeTs = ts,
                ArrivalTs = default,
                Values = bag,
                SourceBar = bar // <--- 在此處填充
            });
    }

    // ── 依設定（+可選 requested）產生該 TF 的指標集合（不修改任何 init-only 設定） ──
    private List<IKLineIndicator> GetOrCreateIndicators(string symbol, string tf, IEnumerable<string>? requested)
    {
        var byTf = _inds.GetOrAdd(symbol, _ => new(StringComparer.OrdinalIgnoreCase));
        return byTf.GetOrAdd(tf, _ =>
        {
            var (smaEnabled, smaPeriods, atrEnabled, atrPeriod) = ResolveEffective(symbol, tf, requested);
            var list = new List<IKLineIndicator>(2);
            if (smaEnabled && smaPeriods.Length > 0)
                list.Add(new SmaIndicator(smaPeriods));
            if (atrEnabled && atrPeriod > 1)
                list.Add(new AtrIndicator(atrPeriod));
            return list;
        });
    }

    /// <summary>
    /// 決定某一 symbol + timeframe 的最終指標設定（採用「取代」策略：TimeframeOverride > SymbolOverride > Defaults）
    /// 不做 union/合併（防止不同 TF 被混合）
    /// </summary>
    private (bool smaEnabled, int[] smaPeriods, bool atrEnabled, int atrPeriod)
        ResolveEffective(string symbol, string tf, IEnumerable<string>? requested)
    {
        var cfg = _opt.CurrentValue;
        var defaults = cfg.Defaults ?? new DefaultIndicatorSet();

        // 事先宣告並初始化，避免 out var 在條件存取時沒有被指派
        DefaultIndicatorSet? tfOv = null;
        DefaultIndicatorSet? symOv = null;

        if (cfg.TimeframeOverrides != null)
            cfg.TimeframeOverrides.TryGetValue(tf, out tfOv);

        if (cfg.SymbolOverrides != null)
            cfg.SymbolOverrides.TryGetValue(symbol, out symOv);

        // 決定來源：TF override 有就用 TF；否則 Symbol 有就用 Symbol；否則用 Defaults
        var source = tfOv ?? symOv ?? defaults;

        bool smaEnabled = source.Sma?.Enabled == true;
        int[] smaPeriods = source.Sma?.Periods?.Where(n => n > 0).Distinct().OrderBy(n => n).ToArray()
                           ?? Array.Empty<int>();
        bool atrEnabled = source.Atr?.Enabled == true;
        int atrPeriod = source.Atr?.Period ?? 14;

        // 如果有 requested (臨時要求)，就用 requested 做額外過濾（保留原有的 requested 支援）
        if (requested != null)
        {
            var tokens = new HashSet<string>(requested.Select(x => x?.Trim()?.ToUpperInvariant() ?? ""));

            // SMA requested?
            var wantSma = tokens.Any(t => t == "SMA" || t.StartsWith("SMA"));
            if (!wantSma)
            {
                smaEnabled = false;
                smaPeriods = Array.Empty<int>();
            }
            else
            {
                var reqPeriods = tokens.Where(t => t.StartsWith("SMA"))
                                       .Select(t => int.TryParse(t.AsSpan(3), out var n) ? n : (int?)null)
                                       .Where(n => n.HasValue).Select(n => n!.Value).ToHashSet();
                if (reqPeriods.Count > 0)
                    smaPeriods = smaPeriods.Where(reqPeriods.Contains).ToArray();
                smaEnabled = smaPeriods.Length > 0;
            }

            // ATR requested?
            var wantAtr = tokens.Any(t => t == "ATR" || t.StartsWith("ATR"));
            if (!wantAtr) atrEnabled = false;
            else
            {
                var req = tokens.Where(t => t.StartsWith("ATR"))
                                .Select(t => int.TryParse(t.AsSpan(3), out var n) ? n : (int?)null)
                                .FirstOrDefault(n => n.HasValue);
                if (req.HasValue) atrPeriod = req.Value;
                atrEnabled = atrEnabled && atrPeriod > 1;
            }
        }

        var srcName = tfOv != null ? "TimeframeOverride" : (symOv != null ? "SymbolOverride" : "Defaults");
        _log.LogDebug("[IND.Resolve] {Sym}/{Tf} -> Source={Src}, SMAenabled={Sma}, Periods=[{Periods}], ATRenabled={Atr}, ATRperiod={AtrP}",
            symbol, tf, srcName, smaEnabled, string.Join(',', smaPeriods), atrEnabled, atrPeriod);

        return (smaEnabled, smaPeriods, atrEnabled, atrPeriod);
    }


    // 不會觸發 init-only：因為是「建立新物件」時做物件初始設定式
    private static DefaultIndicatorSet Merge(DefaultIndicatorSet a, DefaultIndicatorSet b) => new()
    {
        Sma = b.Sma ?? a.Sma,
        Atr = b.Atr ?? a.Atr
    };

    // Day/Week/Month 不帶 _1；其他用 Type_Period
    private static string CanonicalTfKey(string type, int period)
        => (period == 1 && (type == "Day" || type == "Week" || type == "Month")) ? type : $"{type}_{period}";

    private static string CanonicalFromKey(string tfKey)
    {
        if (string.IsNullOrWhiteSpace(tfKey)) return tfKey;
        var key = tfKey.Trim();
        var idx = key.LastIndexOf('_');
        if (idx > 0 && idx < key.Length - 1 && int.TryParse(key.AsSpan(idx + 1), out var p))
            return CanonicalTfKey(key.Substring(0, idx), p);
        return key; // 已是 Day/Week/Month 或 Time_x
    }

    private (string symbol, string tf, decimal o, decimal h, decimal l, decimal c, ExchangeTs ts) ExtractOHLC(KLine bar)
    {
        var symbol = bar.StockId;
        var tf = (bar.Config != null) ? CanonicalTfKey(bar.Config.Type, bar.Config.Period) : "UnknownKLine";
        return (symbol, tf, bar.Open, bar.High, bar.Low, bar.Close, bar.EndTime);
    }

    private void Publish(IndicatorsUpdatedEvent evt)
    {
        if (evt.Values == null || evt.Values.Count == 0) return;

        LogDuplicateIfAny(evt);

        var valuesString = string.Join(", ", evt.Values.Select(kv => $"{kv.Key}={kv.Value:F2}"));
        _log.LogDebug("[指標計算] {Symbol}|{Period}|Ts={Ts:yyyy-MM-dd HH:mm}|Partial={IsPartial} | {Values}",
            evt.Symbol, evt.Period, evt.ExchangeTs.Value, evt.IsPartial, valuesString);
        _bus.Publish(evt);
    }

    private void LogDuplicateIfAny(IndicatorsUpdatedEvent evt)
    {
        var ts = evt.ExchangeTs.Value != default ? evt.ExchangeTs.Value : evt.ArrivalTs.Value;
        long ticks = ts.Ticks;
        if (ticks <= 0) return;

        var key = new IndKey(evt.Symbol, evt.Period, evt.IsPartial);
        int hash = ComputeIndicatorHash(evt.Values);

        if (_lastIndPublish.TryGetValue(key, out var prev) && prev.Ticks == ticks && prev.Hash == hash)
        {
            var count = prev.Count + 1;
            _lastIndPublish[key] = new IndPublishState(ticks, hash, count);
            _log.LogDebug(
                "[IND.Duplicate] Sym={Sym} Tf={Tf} Ts={Ts:HH:mm:ss.fff} Partial={Partial} Count={Count} Keys={Keys}",
                evt.Symbol, evt.Period, ts, evt.IsPartial, count,
                string.Join(",", evt.Values.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)));
            return;
        }

        _lastIndPublish[key] = new IndPublishState(ticks, hash, 1);
    }

    private static int ComputeIndicatorHash(IReadOnlyDictionary<string, decimal> values)
    {
        unchecked
        {
            int hash = 17;
            foreach (var kv in values.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(kv.Key);
                hash = hash * 31 + kv.Value.GetHashCode();
            }
            return hash;
        }
    }

    private sealed record IndKey(string Sym, string Tf, bool Partial);

    private readonly record struct IndPublishState(long Ticks, int Hash, int Count);

    public void Dispose()
    {
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
    }

    private sealed class SymbolState
    {
        public decimal LastTradePrice;
        public long LastTradeVolume;
        public QuoteBest5Event? LastBest5;
    }
}

