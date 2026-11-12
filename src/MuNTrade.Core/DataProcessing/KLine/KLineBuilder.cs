using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using MuNTrade.Abstractions.Contracts.Data;
using MuNTrade.Abstractions.Contracts.Market; // Added for IMarketInfoProvider
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;
using MuNTrade.Abstractions.Time;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MuNTrade.Core.DataProcessing.KLine
{
    /// <summary>
    /// 穩健版 KLineBuilder（依賴 ITradingSessionProvider 作為唯一 session/period 真相）
    /// - Day/Week/Month 的 boundary 全部由 provider.GetPeriodWindow 決定
    /// - Time TF 仍在 builder 做 AlignUp，但 Anchor 來源優先用 provider.GetSession 的 Anchor（若有）
    /// - 對於時間 Kind 統一使用 Local（與 log +08:00 相符）
    /// </summary>
    public sealed class KLineBuilder : IKLineBuilder
    {
        public event Action<DynamicBar> OnBarClosed = delegate { };
        public event Action<DynamicBar>? OnBarPartial;

        private readonly ITradingSessionProvider _sessionProvider;
        private readonly IMarketInfoProvider _marketInfo; // Injected
        private readonly ILogger<KLineBuilder>? _logger;
        private readonly IHolidayCalendar _cal;
        private readonly IClock _clock;

        private sealed class BuilderState
        {
            public BarConfig Config { get; }
            public ConcurrentDictionary<string, DynamicBar> CurrentBars { get; } = new();
            public BuilderState(BarConfig config) { Config = config; }
        }

        private readonly ConcurrentDictionary<string, BuilderState> _builderStates = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastTickTimes = new();

        public KLineBuilder(ITradingSessionProvider sessionProvider, IMarketInfoProvider marketInfo, IClock clock, IHolidayCalendar cal, ILogger<KLineBuilder>? logger = null)
        {
            _sessionProvider = sessionProvider;
            _marketInfo = marketInfo;
            _logger = logger;
            _cal = cal;
            _clock = clock;
        }

        // ---------------------- 公用工具 ----------------------
        private static TimeSpan TruncateToSecond(TimeSpan t)
            => new TimeSpan(t.Days, t.Hours, t.Minutes, t.Seconds);

        private static DateTime TruncateToMinute(DateTime dt)
            => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, DateTimeKind.Local);

        /// <summary>保證 boundary 嚴格晚於 baseTime（避免建棒後立刻被判定跨邊界）。</summary>
        private static DateTime EnsureStrictlyAfter(DateTime baseTime, DateTime boundary)
            => (boundary <= baseTime) ? baseTime.AddTicks(1) : boundary;

        // 統一成 Local 時間表示（系統顯示與 log 都用 Local）
        private static DateTime AsLocal(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Local);

        // 夜盤起算時間（可由 provider 決定；此處僅保留常數供 fallback，通常不會用到）
        private static readonly TimeSpan NightStart = new(15, 0, 0);

        /// <summary>將 ts 依 period 與 anchor 向上對齊至下一個結束點；Anchor 自動去毫秒並處理跨日。</summary>
        private static DateTime AlignUp(DateTime ts, int periodMin, TimeSpan anchorRaw)
        {
            // 確保 kinds 為 Local
            ts = DateTime.SpecifyKind(ts, DateTimeKind.Local);

            // 1) Anchor 去毫秒（保留到秒）
            var anchor = TruncateToSecond(anchorRaw);

            // 2) 以 ts 當日組出 anchorPoint，並確保 anchorPoint <= ts（跨日情境往回找）
            DateTime anchorPoint = ts.Date + anchor;
            while (anchorPoint > ts)
                anchorPoint = anchorPoint.AddDays(-1);

            long periodTicks = TimeSpan.FromMinutes(periodMin).Ticks;
            long elapsedTicks = ts.Ticks - anchorPoint.Ticks;

            // ceil(elapsed/period) * period；注意 elapsed=0 時要進下一段
            long alignedTicks = ((elapsedTicks - 1) / periodTicks + 1) * periodTicks;

            // 3) 回傳對齊結果並整分化
            var result = anchorPoint.AddTicks(alignedTicks);
            return TruncateToMinute(DateTime.SpecifyKind(result, DateTimeKind.Local));
        }

        // ---------------------- Compute Expected Boundary ----------------------
        /// <summary>
        /// 計算「應該正在進行的那根」的 start/end。
        /// - Time: 使用 AlignUp（anchor 來源優先從 session）
        /// - Day/Week/Month: 從 provider.GetPeriodWindow 取 WindowStart/WindowEnd（唯一真相）
        /// </summary>
        private (DateTime start, DateTime end) ComputeExpectedBoundary(
            string symbol,
            BarConfig cfg,
            TradingSession sess,   // session 仍保留供 Time 使用（或 fallback）
            DateTime refTs)
        {
            // 確保 refTs 為 Local
            refTs = AsLocal(refTs);

            if (cfg.Type == "Time")
            {
                // Time bars 仍在 builder 計算，但 Anchor 來源優先用 session（若 sess 為 null 則嘗試從 provider 取得）
                TimeSpan anchor = sess?.Anchor ?? TimeSpan.Zero;
                if (sess == null)
                {
                    var s = _sessionProvider.GetSession(symbol, refTs);
                    if (s != null) anchor = s.Anchor;
                }

                var end = AlignUp(refTs, cfg.Period, anchor);
                end = EnsureStrictlyAfter(refTs, end);
                var start = end.AddMinutes(-cfg.Period);

                return (TruncateToMinute(start), TruncateToMinute(end));
            }

            // Day / Week / Month 由 provider 決定 window（唯一來源）
            var window = _sessionProvider.GetPeriodWindow(symbol, cfg, refTs);
            if (window == null)
            {
                // provider 未能回傳時的保守 fallback（理論上不會發生）
                _logger?.LogWarning("[KLineBuilder] GetPeriodWindow returned null for {Sym} {Cfg} at {Ts:O}", symbol, $"{cfg.Type}_{cfg.Period}", refTs);
                DateOnly td = DateOnly.FromDateTime(refTs);

                // 改用 MarketInfo 判斷衍生品 (Futures/Options)
                // [SOP-Strict] Fallback logic
                bool isDeriv = false;
                if (_marketInfo.TryGetMarketInfo(symbol, out var info))
                {
                    isDeriv = info.IsDerivatives;
                }

                DateTime fallbackStart = isDeriv
                    ? (FindPreviousTradingDateLocal(symbol, td) ?? td).ToDateTime(new TimeOnly(15, 0))
                    : td.ToDateTime(new TimeOnly(9, 0));
                DateTime fallbackEnd = td.ToDateTime(isDeriv ? new TimeOnly(13, 45) : new TimeOnly(13, 30));
                return (TruncateToMinute(fallbackStart), TruncateToMinute(EnsureStrictlyAfter(refTs, fallbackEnd)));
            }

            var startWin = AsLocal(window.WindowStart);
            var endWin = AsLocal(window.WindowEnd);

            // 確保 end > refTs（避免建棒後立即被判定跨邊界）
            endWin = EnsureStrictlyAfter(refTs, endWin);

            return (TruncateToMinute(startWin), TruncateToMinute(endWin));
        }

        // ---------------------- IKLineBuilder：註冊/反註冊 ----------------------
        public void RegisterConfiguration(BarConfig config)
        {
            string key = $"{config.Type}_{config.Period}";
            _builderStates.TryAdd(key, new BuilderState(config));
        }

        public void UnregisterConfiguration(BarConfig config)
        {
            string key = $"{config.Type}_{config.Period}";
            _builderStates.TryRemove(key, out _);
        }

        // ---------------------- Tick 主流程 ----------------------
        public void ProcessTick(TickData tick)
        {
            if (tick == null) return;
            if (tick.IsSimulated) return;

            // 先檢查是否有其他 symbol 因跨 session 需要 flush
            CheckAndFlushEndOfSession(tick);

            var session = _sessionProvider.GetSession(tick.StockId, tick.ExchangeTs.Value);
            if (session == null)
            {
                // 不在任何 session，不處理（避免假日誤判）
                return;
            }

            // 更新最後 tick 時間
            _lastTickTimes[tick.StockId] = AsLocal(tick.ExchangeTs.Value);

            foreach (var state in _builderStates.Values)
            {
                ProcessTickForConfig(tick, state, session);
            }

            // 若 broker 提供的 tick 標示為 session end（例如夜盤收盤），則馬上 flush
            if (tick.IsSessionEnd)
            {
                FlushOpenBars(tick.StockId);
                _lastTickTimes.TryRemove(tick.StockId, out _);
            }
        }

        /// <summary>
        /// 檢查是否有其他 symbol 的最後一筆 tick 已落在已結束的 session 之後，若是則 flush。
        /// （避免當前 symbol 的 tick 導致其他 symbol 未及時 flush）
        /// </summary>
        private void CheckAndFlushEndOfSession(TickData tick)
        {
            var symbolsToCheck = _lastTickTimes.Keys.Where(symbol => symbol != tick.StockId).ToList();
            foreach (var symbol in symbolsToCheck)
            {
                var lastTickTime = _lastTickTimes[symbol];
                var lastSession = _sessionProvider.GetSession(symbol, lastTickTime);
                if (lastSession != null && tick.ExchangeTs.Value >= lastSession.EndTime)
                {
                    FlushOpenBars(symbol);
                    _lastTickTimes.TryRemove(symbol, out _);
                }
            }
        }

        private void ProcessTickForConfig(TickData tick, BuilderState state, TradingSession session)
        {
            // 使用 Local 時間進行所有比較與建棒
            var ts = AsLocal(tick.ExchangeTs.Value);

            if (!state.CurrentBars.TryGetValue(tick.StockId, out var cur))
            {
                // 建新棒：僅計算邊界（Day/Week/Month 交由 provider），然後把第一個 tick 加進去
                cur = CreateNewBar(tick, state.Config, session);
                state.CurrentBars[tick.StockId] = cur;
                cur.AddTick(tick);
                OnBarPartial?.Invoke(cur);
                return;
            }

            // 忽略比當前 bar 起點更早的 tick（避免回補/亂序 tick 污染 open bar）
            if (ts < cur.StartTime.Value)
            {
                _logger?.LogDebug(
                    "[KLineBuilder] Drop out-of-order tick. Sym={Sym} Per={Per} TickTs={Tick:O} CurStart={Start:O}",
                    tick.StockId, $"{state.Config.Type}_{state.Config.Period}", tick.ExchangeTs.Value, cur.StartTime.Value);
                return;
            }

            // ---------------- Pre-close（僅 1 分）區間處理 ----------------
            bool isOneMinute = state.Config.Type == "Time" && state.Config.Period == 1;
            if (isOneMinute)
            {
                var pcStart = TruncateToMinute(ts.Date + TruncateToSecond(session.PreCloseAuctionStart));
                var pcEnd = pcStart + session.PreCloseExtension;

                // 為避免毫秒誤差，把 cur.EndTime 整分化後再比對
                if (TruncateToMinute(cur.EndTime.Value).TimeOfDay == pcStart.TimeOfDay)
                {
                    if (ts >= pcStart && ts < pcEnd)
                    {
                        cur.AddTick(tick);
                        OnBarPartial?.Invoke(cur);
                        return; // 全部塞回 pre-close 這根
                    }
                }
            }
            // ----------------------------------------------------------------

            // 如果超過或等於 end，關棒並建下一根
            // 注意：cur.EndTime 是 ExchangeTs，ts 是 DateTime，比較時取 Value
            if (ts >= cur.EndTime.Value)
            {
                _logger?.LogDebug(
                    "[KLineBuilder.CloseTrigger] Sym={Sym} Per={Per} TickTs={Tick:O} CurStart={Start:O} CurEnd={End:O}",
                    tick.StockId, $"{state.Config.Type}_{state.Config.Period}", tick.ExchangeTs.Value, cur.StartTime.Value, cur.EndTime.Value);

                OnBarClosed?.Invoke(cur);
                var nxt = CreateNewBar(tick, state.Config, session);
                state.CurrentBars[tick.StockId] = nxt;
                nxt.AddTick(tick);
                OnBarPartial?.Invoke(nxt);
            }
            else
            {
                cur.AddTick(tick);
                OnBarPartial?.Invoke(cur);
            }
        }

        // ---------------------- 建棒與邊界計算 ----------------------
        private DynamicBar CreateNewBar(TickData tick, BarConfig cfg, TradingSession sess)
        {
            var ts = AsLocal(tick.ExchangeTs.Value);

            var (start, end) = ComputeExpectedBoundary(tick.StockId, cfg, sess, ts);

            decimal p = tick.Price;

            return DynamicBar.FromSnapshot(
                tick.StockId, cfg,
                new ExchangeTs(start), new ExchangeTs(end),
                p, p, p, p,
                tick.Volume, 1, new ExchangeTs(ts),
                tick.MarketNo, tick.Source
            );
        }

        // ---------------------- Flush API ----------------------
        public void FlushOpenBars()
        {
            _logger?.LogWarning("[FlushAll] ByApi AllSymbols");

            foreach (var state in _builderStates.Values)
            {
                foreach (var bar in state.CurrentBars.Values)
                {
                    OnBarClosed?.Invoke(bar);
                }
                state.CurrentBars.Clear();
            }
        }

        public void FlushOpenBars(string stockId)
        {
            _logger?.LogDebug("[FlushSymbol] ByApi Sym={Sym}", stockId);

            foreach (var state in _builderStates.Values)
            {
                if (state.CurrentBars.TryRemove(stockId, out var bar))
                {
                    OnBarClosed?.Invoke(bar);
                }
            }
        }

        /// <summary>
        /// 將到期（或超過牆鐘）的 open bars 關掉。此 function 會被外部的 Finalizer 呼叫。
        /// </summary>
        public void FlushBarsExpiredBefore(ExchangeTs cutoff)
        {
            var wallNow = DateTime.SpecifyKind(cutoff.Value, DateTimeKind.Local);

            static TimeSpan SafetyLagFor(BarConfig cfg) =>
                cfg.Type == "Time"
                    ? (cfg.Period <= 1 ? TimeSpan.FromSeconds(5)
                                       : TimeSpan.FromSeconds(10))
                    : TimeSpan.FromMinutes(5);

            foreach (var state in _builderStates.Values)
            {
                var safetyLag = SafetyLagFor(state.Config);
                var snapshot = state.CurrentBars.ToList();

                foreach (var kvp in snapshot)
                {
                    var symbol = kvp.Key;
                    var bar = kvp.Value;

                    if (!_lastTickTimes.TryGetValue(symbol, out var lastTickTs))
                        continue;

                    var normLastTick = DateTime.SpecifyKind(lastTickTs, DateTimeKind.Local);
                    var normEnd = DateTime.SpecifyKind(bar.EndTime.Value, DateTimeKind.Local);

                    var crossedByTick = normLastTick >= normEnd;
                    var crossedByWall = wallNow >= normEnd + safetyLag;

                    if (!(crossedByTick || crossedByWall))
                        continue;

                    if (!crossedByTick && (state.Config.Type != "Time") && (wallNow < normEnd + TimeSpan.FromMinutes(3)))
                        continue;

                    if (state.CurrentBars.TryRemove(kvp))
                    {
                        _logger?.LogDebug(
                            "[FinalizerClose] Sym={Sym} Per={Per} End={End:O} WallNow={Now:O} LastTick={LT:O} ByTick={BT} ByWall={BW}",
                            symbol, $"{state.Config.Type}_{state.Config.Period}",
                            bar.EndTime.Value, wallNow, lastTickTs, crossedByTick, crossedByWall);

                        OnBarClosed?.Invoke(bar);
                    }
                }
            }
        }

        // ---------------------- Seed API ----------------------
        /// <summary>
        /// 種子注入（使用 provider 作為 Day/Week/Month 的唯一 window 決策）
        /// </summary>
        public void SeedCurrentBar(string symbol, BarConfig cfg, DynamicBar snapshot)
        {
            string key = $"{cfg.Type}_{cfg.Period}";
            if (!_builderStates.TryGetValue(key, out var state))
            {
                state = new BuilderState(cfg);
                _builderStates[key] = state;
                _logger?.LogWarning("[Seed] Config not registered: {Key}. Registered on the fly.", key);
            }

            var refTs = snapshot.LastTickTime.Value == default ? _clock.Now : snapshot.LastTickTime.Value;
            refTs = AsLocal(refTs);

            if (cfg.Type == "Time")
            {
                var sess = _sessionProvider.GetSession(symbol, refTs);
                if (sess == null)
                {
                    _logger?.LogWarning("[Seed] No session for {Sym} at {Ts:O}. Seed ignored.", symbol, refTs);
                    return;
                }

                var (expStart, expEnd) = ComputeExpectedBoundary(symbol, cfg, sess, refTs);

                var seeded = DynamicBar.FromSnapshot(
                    symbol,
                    cfg,
                    new ExchangeTs(expStart), new ExchangeTs(expEnd),
                    snapshot.Open, snapshot.High, snapshot.Low, snapshot.Close,
                    snapshot.Volume,
                    snapshot.TickCount,
                    snapshot.LastTickTime.Value == default ? new ExchangeTs(expStart) : snapshot.LastTickTime
                );

                state.CurrentBars[symbol] = seeded;

                _logger?.LogInformation(
                    "[Seed] Current bar seeded for {Sym} {Type}_{Per}. Start={Start:O}, End={End:O}, Vol={Vol}, Ticks={Ticks}",
                    symbol, cfg.Type, cfg.Period, seeded.StartTime.Value, seeded.EndTime.Value, seeded.Volume, seeded.TickCount
                );

                return;
            }

            // Day/Week/Month -> 直接詢問 provider 的 PeriodWindow
            var window = _sessionProvider.GetPeriodWindow(symbol, cfg, refTs);
            if (window == null)
            {
                _logger?.LogWarning("[Seed] GetPeriodWindow returned null for {Sym} {Cfg} at {Ts:O}. Seed ignored.", symbol, cfg.Type, refTs);
                return;
            }

            var expStartWin = AsLocal(window.WindowStart);
            var expEndWin = AsLocal(window.WindowEnd);

            var seeded2 = DynamicBar.FromSnapshot(
                symbol,
                cfg,
                new ExchangeTs(TruncateToMinute(expStartWin)), new ExchangeTs(TruncateToMinute(expEndWin)),
                snapshot.Open, snapshot.High, snapshot.Low, snapshot.Close,
                snapshot.Volume,
                snapshot.TickCount,
                snapshot.LastTickTime.Value == default ? new ExchangeTs(TruncateToMinute(expStartWin)) : snapshot.LastTickTime
            );

            state.CurrentBars[symbol] = seeded2;

            _logger?.LogInformation(
                "[Seed] Current bar seeded (window) for {Sym} {Type}_{Per}. Start={Start:O}, End={End:O}, Vol={Vol}, Ticks={Ticks}",
                symbol, cfg.Type, cfg.Period, seeded2.StartTime.Value, seeded2.EndTime.Value, seeded2.Volume, seeded2.TickCount
            );
        }

        // ---------------------- 小工具 ----------------------
        // 當 provider 不可用且需要 fallback 時，嘗試從 provider 取得前一交易日（回傳 DateOnly?）
        // 這只是輔助用，平常應由 provider 的 GetPeriodWindow/FindPreviousTradingDate 提供
        private DateOnly? FindPreviousTradingDateLocal(string symbol, DateOnly day, int maxLookbackDays = 30)
        {
            for (int i = 1; i <= maxLookbackDays; i++)
            {
                var cand = day.AddDays(-i);
                var probe = cand.ToDateTime(new TimeOnly(9, 0));
                var s = _sessionProvider.GetSession(symbol, probe);
                if (s != null) return cand;
            }
            return null;
        }
    }
}

