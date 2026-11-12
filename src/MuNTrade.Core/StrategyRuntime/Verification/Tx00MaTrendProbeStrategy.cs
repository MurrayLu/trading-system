using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Time;
using MuNTrade.Core.StrategyRuntime;

namespace MuNTrade.Core.StrategyRuntime.Verification
{
    public class Tx00MaTrendProbeStrategy : IStrategy, IStrategyIngressSpec
    {
        private const string Symbol = "TX00";
        private const string PeriodKey = "Time_1";
        private const string PaperAccountKey = "Sim:Paper:All:Default";
        private const int DefaultQuantity = 1;

        public StrategyInstanceId InstanceId { get; }
        public StrategyTemplateId TemplateId { get; }
        public string Version => "1.0.0-probe";

        private readonly IStrategyRuntimeContext _context;
        private readonly string _strategyId;

        // State
        private decimal? _lastClose1m;
        private decimal? _sma5;
        private decimal? _sma10;
        private BiasState _currentBias = BiasState.Neutral;
        private int _position; // -1 short, 0 flat, 1 long

        // Debug state for warning context
        private string? _debugLastIndKeys;
        private string? _debugLastIndPeriod;

        // Ticks for throttling
        private long _lastPacketTicks;
        private long _lastIntentTicks;
        // Decrease interval to 10s for easier debugging/aliveness check in Probe
        private static readonly long AliveIntervalTicks = TimeSpan.FromSeconds(10).Ticks;
        private static readonly long IntentCooldownTicks = TimeSpan.FromSeconds(1).Ticks;

        public Tx00MaTrendProbeStrategy(
            StrategyInstanceId instanceId,
            StrategyTemplateId templateId,
            IStrategyRuntimeContext context)
        {
            InstanceId = instanceId;
            TemplateId = templateId;
            _context = context;
            _strategyId = StrategyIdentity.BuildStrategyId(templateId.Value, instanceId.Value);
        }

        public IReadOnlyCollection<Type> Subscriptions => new[]
        {
            typeof(KLineClosedEvent),
            typeof(IndicatorsUpdatedEvent),
            typeof(StrategyPulseEvent)
        };

        public IReadOnlyCollection<string>? Symbols { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Symbol };
        public IReadOnlyCollection<string>? Periods { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PeriodKey };
        public IReadOnlyCollection<string>? IndicatorKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SMA5", "SMA10" };

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            EmitStatusPacket(0, 0, 0, BiasState.Neutral, false, $"Strategy Started (Waiting for {Symbol} {PeriodKey}...) sid={_strategyId}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public ValueTask OnEventAsync(IMuEvent evt, CancellationToken cancellationToken = default)
        {
            if (evt is KLineClosedEvent k) OnIngress(k);
            else if (evt is IndicatorsUpdatedEvent i) OnIngress(i);
            else if (evt is StrategyPulseEvent p) OnPulse(p);
            return ValueTask.CompletedTask;
        }

        private void OnPulse(StrategyPulseEvent p)
        {
            long now = DateTime.UtcNow.Ticks;
            if ((now - _lastPacketTicks) > AliveIntervalTicks)
            {
                EmitStatusPacket(0, 0, 0, BiasState.Neutral, false, $"[PULSE] seq={p.Seq} waiting for {Symbol} {PeriodKey}... sid={_strategyId}");
                _lastPacketTicks = now;
            }
        }

        private void OnIngress(KLineClosedEvent evt)
        {
            // [DEBUG-PROBE] Promiscuous Logging: Use Logger.LogInformation to ensure visibility
            _context.Logger.LogDebug("[DEBUG-KLINE] Sym={Sym} Key={Key} Period={Period} Type={Type}",
                evt.Bar?.StockId, evt.Bar?.Config?.Key, evt.Bar?.Config?.Period, evt.Bar?.Config?.Type);

            if (evt.Bar == null) return;
            if (!string.Equals(evt.Bar.StockId, Symbol, StringComparison.OrdinalIgnoreCase)) return;

            // Check Period Key (Safe check)
            var configKey = evt.Bar.Config?.Key;
            if (string.IsNullOrEmpty(configKey) || !string.Equals(configKey, PeriodKey, StringComparison.OrdinalIgnoreCase))
                return;

            _lastClose1m = evt.Bar.Close;
            EvaluateBias();
        }

        private void OnIngress(IndicatorsUpdatedEvent evt)
        {
            // Capture debug info before any filtering
            _debugLastIndKeys = string.Join(",", evt.Values.Keys);
            _debugLastIndPeriod = evt.Period;

            // [DEBUG-PROBE] Promiscuous Logging
            _context.Logger.LogDebug("[DEBUG-IND] Sym={Sym} Period={Period} IsPartial={Partial} Keys={Keys}",
                evt.Symbol, evt.Period, evt.IsPartial, _debugLastIndKeys);

            if (!string.Equals(evt.Symbol, Symbol, StringComparison.OrdinalIgnoreCase)) return;

            // Period is string "Time_1"
            if (!string.Equals(evt.Period, PeriodKey, StringComparison.OrdinalIgnoreCase)) return;

            // Prioritize "SMA5", fallback to "SMA_5"
            if (!evt.Values.TryGetValue("SMA5", out var s5))
                evt.Values.TryGetValue("SMA_5", out s5);

            if (!evt.Values.TryGetValue("SMA10", out var s10))
                evt.Values.TryGetValue("SMA_10", out s10);

            if (s5 != 0) _sma5 = s5;
            if (s10 != 0) _sma10 = s10;

            EvaluateBias();
        }

        private void EvaluateBias()
        {
            if (_lastClose1m == null || _sma5 == null || _sma10 == null)
            {
                // Report missing SMA data when a close value is available.
                if (_lastClose1m.HasValue && (_sma5 == null || _sma10 == null))
                {
                    var msg = $"Warning: Missing SMA data. Close={_lastClose1m}. LastInd: Period={_debugLastIndPeriod}, Keys={_debugLastIndKeys}";
                    EmitStatusPacket(0, 0, 0, BiasState.Neutral, true, msg);
                }
                return;
            }

            decimal close = _lastClose1m.Value;
            decimal s5 = _sma5.Value;
            decimal s10 = _sma10.Value;

            var newBias = BiasState.Neutral;

            if (close > s5 && close > s10) newBias = BiasState.Long;
            else if (close < s5 && close < s10) newBias = BiasState.Short;

            bool changed = newBias != _currentBias;
            _currentBias = newBias;

            long now = DateTime.UtcNow.Ticks;
            bool timeToAlive = (now - _lastPacketTicks) > AliveIntervalTicks;

            if (changed || timeToAlive)
            {
                EmitStatusPacket(close, s5, s10, newBias, changed);
                _lastPacketTicks = now;
            }

            TryEmitIntent(newBias);
        }

        private void EmitStatusPacket(decimal c, decimal s5, decimal s10, BiasState bias, bool changed, string? extraMsg = null)
        {
            StrategySeverity severity = StrategySeverity.Info;

            if (extraMsg != null && extraMsg.StartsWith("Warning"))
                severity = StrategySeverity.Warning;
            else if (!changed && extraMsg == null)
                severity = StrategySeverity.Info;

            var msg = extraMsg ?? $"1m Close={c:F0} | SMA5={s5:F2} | SMA10={s10:F2} | Bias={bias}";
            if (!msg.Contains("sid="))
                msg = $"{msg} | sid={_strategyId}";

            _context.OutputRouter.EmitPacket(
                TemplateId,
                InstanceId,
                StrategyScope.Symbol,
                Symbol,
                StrategyCategory.Observation,
                severity,
                "PROBE_UPDATE",
                msg
            );
        }

        private void TryEmitIntent(BiasState desiredBias)
        {
            int targetPos = desiredBias switch
            {
                BiasState.Long => 1,
                BiasState.Short => -1,
                _ => 0
            };

            if (_position == targetPos) return;

            long nowTicks = _context.Clock.Now.Ticks;
            if (_lastIntentTicks != 0 && (nowTicks - _lastIntentTicks) < IntentCooldownTicks)
                return;

            int step = Math.Sign(targetPos - _position);
            var side = step > 0 ? OrderSide.Buy : OrderSide.Sell;
            int qty = DefaultQuantity;

            var cid = Guid.NewGuid().ToString("N");
            var plan = new ExecutionPlan(
                Quantity: qty,
                OrderType: OrderType.Market,
                Tif: TimeInForce.IOC,
                LimitPrice: null,
                AllowSlipUpOneTick: true,
                MaxRetries: 1,
                StopLossPrice: null,
                TakeProfitPrice: null
            );

            var intent = new OrderIntent(
                Symbol: Symbol,
                Side: side,
                Plan: plan,
                CorrelationId: cid,
                CreatedAt: new ArrivalTs(_context.Clock.Now),
                StrategyId: _strategyId,
                AccountKey: PaperAccountKey,
                IntentKind: "TX00_MA_TREND_PROBE",
                MarketKind: MuNTrade.Abstractions.Domain.Common.Enums.MarketKind.Derivatives
            );

            _context.OutputRouter.EmitIntent(
                InstanceId,
                TemplateId,
                intentKind: "TX00_MA_TREND_PROBE",
                intent
            );

            _context.Logger.LogInformation(
                "[INTENT] cid={Cid} sid={Sid} {Symbol} {Period} bias={Bias} {Side} qty={Qty} account={Account}",
                cid, _strategyId, Symbol, PeriodKey, desiredBias, side, qty, PaperAccountKey);

            _context.OutputRouter.EmitPacket(
                TemplateId,
                InstanceId,
                StrategyScope.Symbol,
                Symbol,
                StrategyCategory.Intent,
                StrategySeverity.Info,
                "INTENT_EMIT",
                $"cid={cid} sid={_strategyId} bias={desiredBias} {side} qty={qty} account={PaperAccountKey}"
            );

            _lastIntentTicks = nowTicks;
            _position += step;
        }

        private enum BiasState { Neutral, Long, Short }
    }
}

