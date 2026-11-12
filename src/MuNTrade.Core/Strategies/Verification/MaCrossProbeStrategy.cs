using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Domain.Common.Enums;
using MuNTrade.Abstractions.Domain.Trading.Enums;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Time;
using MuNTrade.Core.StrategyRuntime;

namespace MuNTrade.Core.Strategies.Verification
{
    public sealed class MaCrossProbeStrategy : IStrategy, IStrategyIngressSpec
    {
        private const string PeriodKey = "Time_1";
        private const int FastPeriod = 5;
        private const int SlowPeriod = 10;
        private const int Quantity = 1;

        public StrategyInstanceId InstanceId { get; }
        public StrategyTemplateId TemplateId { get; }
        public string Version => "1.0.0-probe";

        private readonly IStrategyRuntimeContext _context;
        private readonly string _strategyId;
        private readonly Dictionary<string, SymbolState> _states = new(StringComparer.OrdinalIgnoreCase);

        public MaCrossProbeStrategy(
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

        public IReadOnlyCollection<string>? Symbols => null;
        public IReadOnlyCollection<string>? Periods => new[] { PeriodKey };
        public IReadOnlyCollection<string>? IndicatorKeys => null;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _context.OutputRouter.EmitPacket(
                TemplateId,
                InstanceId,
                StrategyScope.System,
                "MaCrossProbe",
                StrategyCategory.System,
                StrategySeverity.Info,
                "PROBE_START",
                $"MA probe started. period={PeriodKey} fast={FastPeriod} slow={SlowPeriod} strategyId={_strategyId}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _context.OutputRouter.EmitPacket(
                TemplateId,
                InstanceId,
                StrategyScope.System,
                "MaCrossProbe",
                StrategyCategory.System,
                StrategySeverity.Info,
                "PROBE_STOP",
                $"MA probe stopped. strategyId={_strategyId}");
            return Task.CompletedTask;
        }

        public ValueTask OnEventAsync(IMuEvent evt, CancellationToken cancellationToken = default)
        {
            if (evt is KLineClosedEvent kline)
            {
                OnKLine(kline);
            }
            else if (evt is IndicatorsUpdatedEvent indicators)
            {
                OnIndicators(indicators);
            }
            else if (evt is StrategyPulseEvent pulse)
            {
                OnPulse(pulse);
            }

            return ValueTask.CompletedTask;
        }

        private void OnPulse(StrategyPulseEvent pulse)
        {
            if (pulse.Seq % 20 != 0) return;
            _context.OutputRouter.EmitPacket(
                TemplateId,
                InstanceId,
                StrategyScope.System,
                "MaCrossProbe",
                StrategyCategory.Debug,
                StrategySeverity.Info,
                "PROBE_ALIVE",
                $"pulse={pulse.Seq} trackedSymbols={_states.Count}");
        }

        private void OnKLine(KLineClosedEvent evt)
        {
            if (evt.Bar == null) return;
            var bar = evt.Bar;
            var symbol = bar.StockId;
            if (string.IsNullOrWhiteSpace(symbol)) return;
            if (!string.Equals(bar.Config?.Key, PeriodKey, StringComparison.OrdinalIgnoreCase)) return;

            var state = GetOrCreate(symbol);
            state.BarCount++;
            state.LastClose = bar.Close;
            state.LastBarTime = bar.EndTime.Value;

            _context.OutputRouter.EmitPacket(
                TemplateId,
                InstanceId,
                StrategyScope.Symbol,
                symbol,
                StrategyCategory.Observation,
                StrategySeverity.Info,
                "PROBE_BAR",
                $"bar={state.BarCount} ts={state.LastBarTime:yyyy-MM-dd HH:mm:ss} close={state.LastClose} fast={Fmt(state.Fast)} slow={Fmt(state.Slow)} pos={state.Position}");
        }

        private void OnIndicators(IndicatorsUpdatedEvent evt)
        {
            if (evt.IsPartial) return;
            if (!string.Equals(evt.Period, PeriodKey, StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrWhiteSpace(evt.Symbol)) return;

            var symbol = evt.Symbol;
            var state = GetOrCreate(symbol);
            var prevFast = state.Fast;
            var prevSlow = state.Slow;

            if (TryGetSma(evt.Values, FastPeriod, out var fast))
            {
                state.Fast = fast;
            }

            if (TryGetSma(evt.Values, SlowPeriod, out var slow))
            {
                state.Slow = slow;
            }

            if (!state.Fast.HasValue || !state.Slow.HasValue)
            {
                _context.OutputRouter.EmitPacket(
                    TemplateId,
                    InstanceId,
                    StrategyScope.Symbol,
                    symbol,
                    StrategyCategory.Debug,
                    StrategySeverity.Warning,
                    "PROBE_IND_NOT_READY",
                    $"period={evt.Period} keys=[{string.Join(',', evt.Values.Keys)}] fast={Fmt(state.Fast)} slow={Fmt(state.Slow)}");
                return;
            }

            var prevDiff = (prevFast.HasValue && prevSlow.HasValue) ? prevFast.Value - prevSlow.Value : (decimal?)null;
            var newDiff = state.Fast.Value - state.Slow.Value;

            var crossUp = prevDiff.HasValue && prevDiff.Value <= 0m && newDiff > 0m;
            var crossDown = prevDiff.HasValue && prevDiff.Value >= 0m && newDiff < 0m;

            _context.OutputRouter.EmitPacket(
                TemplateId,
                InstanceId,
                StrategyScope.Symbol,
                symbol,
                StrategyCategory.Decision,
                StrategySeverity.Info,
                "PROBE_MA_STATE",
                $"close={Fmt(state.LastClose)} fast={state.Fast.Value:F4} slow={state.Slow.Value:F4} crossUp={crossUp} crossDown={crossDown} pos={state.Position}");

            if (!prevDiff.HasValue) return;

            if (crossUp && state.Position == PositionState.Flat)
            {
                EmitIntent(symbol, OrderSide.Buy, "MA_PROBE_ENTRY", state);
                state.Position = PositionState.Long;
                return;
            }

            if (crossDown && state.Position == PositionState.Long)
            {
                EmitIntent(symbol, OrderSide.Sell, "MA_PROBE_EXIT", state);
                state.Position = PositionState.Flat;
            }
        }

        private void EmitIntent(string symbol, OrderSide side, string kind, SymbolState state)
        {
            var plan = new ExecutionPlan(
                Quantity: Quantity,
                OrderType: OrderType.Market,
                Tif: TimeInForce.IOC,
                LimitPrice: null,
                AllowSlipUpOneTick: true,
                MaxRetries: 1,
                StopLossPrice: null,
                TakeProfitPrice: null);

            var cid = Guid.NewGuid().ToString("N");
            var intent = new OrderIntent(
                Symbol: symbol,
                Side: side,
                Plan: plan,
                CorrelationId: cid,
                CreatedAt: new ArrivalTs(_context.Clock.Now),
                StrategyId: _strategyId,
                AccountKey: _context.AccountKey,
                MarketKind: MarketKind.Securities,
                MarketNo: MarketNo.Listing,
                IntentKind: kind);

            _context.OutputRouter.EmitIntent(InstanceId, TemplateId, kind, intent);
            _context.OutputRouter.EmitPacket(
                TemplateId,
                InstanceId,
                StrategyScope.Symbol,
                symbol,
                StrategyCategory.Intent,
                StrategySeverity.Info,
                "PROBE_INTENT",
                $"kind={kind} cid={cid} side={side} qty={Quantity} ts={state.LastBarTime:yyyy-MM-dd HH:mm:ss} close={Fmt(state.LastClose)} fast={Fmt(state.Fast)} slow={Fmt(state.Slow)} account={_context.AccountKey}");
        }

        private static bool TryGetSma(IReadOnlyDictionary<string, decimal> values, int period, out decimal value)
        {
            if (values.TryGetValue($"SMA{period}", out value)) return true;
            if (values.TryGetValue($"SMA_{period}", out value)) return true;
            if (values.TryGetValue($"sma{period}", out value)) return true;
            if (values.TryGetValue($"sma_{period}", out value)) return true;
            return false;
        }

        private SymbolState GetOrCreate(string symbol)
        {
            if (_states.TryGetValue(symbol, out var state)) return state;
            state = new SymbolState();
            _states[symbol] = state;
            return state;
        }

        private static string Fmt(decimal? value) => value.HasValue ? value.Value.ToString("F4") : "null";

        private sealed class SymbolState
        {
            public int BarCount { get; set; }
            public DateTime LastBarTime { get; set; }
            public decimal? LastClose { get; set; }
            public decimal? Fast { get; set; }
            public decimal? Slow { get; set; }
            public PositionState Position { get; set; } = PositionState.Flat;
        }

        private enum PositionState
        {
            Flat = 0,
            Long = 1
        }
    }
}

