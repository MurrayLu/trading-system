using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Contracts.Trading;
using MuNTrade.Core.Settings;
using System;
using System.Collections.Concurrent;

namespace MuNTrade.Core.Operations.DataQuality
{
    public class DataHealthGuard : IDataHealthGuard
    {
        private readonly IClock _clock;
        private readonly ILogger<DataHealthGuard> _logger;
        private readonly IOptionsMonitor<FactorStrategyOptions> _options;
        private readonly ConcurrentDictionary<string, SymbolHealthState> _states = new(StringComparer.OrdinalIgnoreCase);

        public DataHealthGuard(
            IClock clock,
            ILogger<DataHealthGuard> logger,
            IOptionsMonitor<FactorStrategyOptions> options)
        {
            _clock = clock;
            _logger = logger;
            _options = options;
        }

        public DataHealth Check(MarketSnapshot snapshot, DateTime? nowOverride = null)
        {
            var now = nowOverride ?? _clock.Now;
            var healthOpt = _options.CurrentValue.Health ?? new DataHealthOptions();

            var warmupWindow = TimeSpan.FromSeconds(Math.Max(0, healthOpt.warmup_sec));
            var staleYellow = TimeSpan.FromSeconds(Math.Max(0, healthOpt.stale_yellow_sec));
            var staleRed = TimeSpan.FromSeconds(Math.Max(staleYellow.TotalSeconds, healthOpt.stale_red_sec));
            var maxTickWithoutL2 = TimeSpan.FromMilliseconds(Math.Max(0, healthOpt.max_tick_without_l2_ms));
            var maxClockSkew = TimeSpan.FromMilliseconds(Math.Max(0, healthOpt.max_clock_skew_ms));
            var logThrottle = TimeSpan.FromSeconds(Math.Max(0.5, healthOpt.log_throttle_sec));

            var symbol = snapshot.Symbol ?? string.Empty;
            var state = _states.GetOrAdd(symbol, _ => new SymbolHealthState(now));
            state.Update(now);

            bool lastTradeSim = snapshot.LastTradeIsSimulated;
            bool lastBest5Sim = snapshot.LastBest5IsSimulated;

            // 1) Warmup window
            if (warmupWindow > TimeSpan.Zero && state.IsInWarmup(warmupWindow))
            {
                return new DataHealth(HealthLevel.Green);
            }

            // 2) Latency check (use freshest non-simulated timestamp)
            var freshest = snapshot.FreshTs == default ? snapshot.ArrivalTs : snapshot.FreshTs;
            var latency = freshest == default ? TimeSpan.Zero : now - freshest.Value;

            if (freshest != default)
            {
                if (latency > staleRed)
                    return new DataHealth(HealthLevel.Red, $"資料到達延遲 {latency.TotalSeconds:F1} 秒");
                if (latency > staleYellow)
                    return new DataHealth(HealthLevel.Yellow, $"資料到達延遲 {latency.TotalSeconds:F1} 秒");
            }

            // If no timestamps yet, nothing more to check
            if (snapshot.LastTradeArrivalTs == default || snapshot.LastBest5ArrivalTs == default)
            {
                return new DataHealth(HealthLevel.Green);
            }

            // 3) Tick vs L2 alignment (skip when either side is simulated)
            var lastTradeTs = lastTradeSim ? default : snapshot.LastTradeArrivalTs;
            var lastBest5Ts = lastBest5Sim ? default : snapshot.LastBest5ArrivalTs;

            if (lastTradeTs != default && lastBest5Ts != default)
            {
                var delta = lastTradeTs - lastBest5Ts;
                if (delta > maxTickWithoutL2)
                {
                    var msg = $"成交快於五檔 (Ticks ahead) {delta.TotalMilliseconds:F0} 毫秒";
                    if (state.ShouldLog(SymbolHealthState.LogKind.TradeAhead, logThrottle, now))
                    {
                        _logger.LogWarning("[資料健康檢查] {Symbol} {Message}", snapshot.Symbol, msg);
                    }
                    return new DataHealth(HealthLevel.Yellow, msg);
                }
            }

            // 4) Clock skew (prefer non-sim reference)
            var reference = lastBest5Ts != default ? lastBest5Ts : lastTradeTs;

            if (reference != default)
            {
                var skew = Math.Abs((reference.Value - now).TotalMilliseconds);
                if (skew > maxClockSkew.TotalMilliseconds)
                {
                    var msg = $"偵測到系統時間偏移 ({skew:F0} 毫秒)";
                    if (state.ShouldLog(SymbolHealthState.LogKind.ClockSkew, logThrottle, now))
                    {
                        _logger.LogWarning("[資料健康檢查] {Symbol} {Message}", snapshot.Symbol, msg);
                    }
                    return new DataHealth(HealthLevel.Yellow, msg);
                }
            }

            // 5) Basic consistency checks

            // (A) 有深度但成交價為 0
            if (snapshot.LastPrice <= 0 && snapshot.Best5.Bids.Count > 0)
                return new DataHealth(HealthLevel.Yellow, "有深度但成交價為 0（可能尚未開盤或無成交）");

            // (B) Crossed market
            if (snapshot.Best5.Bids.Count > 0 && snapshot.Best5.Asks.Count > 0 &&
                            snapshot.Best5.Bids[0].Price >= snapshot.Best5.Asks[0].Price)
            {
                return new DataHealth(HealthLevel.Red, "買一價大於等於賣一價（交叉盤異常）");
            }

            return new DataHealth(HealthLevel.Green);
        }

        // Per-symbol state for throttling logs and warmup
        private sealed class SymbolHealthState
        {
            public enum LogKind { TradeAhead, L2Ahead, ClockSkew }

            private DateTime _lastTradeAheadLog;
            private DateTime _lastL2AheadLog;
            private DateTime _lastClockSkewLog;

            public DateTime FirstSeen { get; private set; }
            public DateTime LastSeen { get; private set; }

            public SymbolHealthState(DateTime initial)
            {
                FirstSeen = LastSeen = initial;
            }

            public void Update(DateTime now)
            {
                // Reset warmup window if data was absent for a while
                if (now < FirstSeen || now < LastSeen || (now - LastSeen) > TimeSpan.FromMinutes(1))
                {
                    FirstSeen = now;
                }

                LastSeen = now;
            }

            public bool IsInWarmup(TimeSpan window)
            {
                if (window <= TimeSpan.Zero) return false;
                return (LastSeen - FirstSeen) < window;
            }

            public bool ShouldLog(LogKind kind, TimeSpan throttle, DateTime now)
            {
                ref DateTime target = ref _lastTradeAheadLog;
                if (kind == LogKind.L2Ahead)
                    target = ref _lastL2AheadLog;
                else if (kind == LogKind.ClockSkew)
                    target = ref _lastClockSkewLog;

                if ((now - target) >= throttle)
                {
                    target = now;
                    return true;
                }

                return false;
            }
        }
    }
}

