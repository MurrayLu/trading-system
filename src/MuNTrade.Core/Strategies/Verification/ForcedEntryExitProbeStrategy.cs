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
    public sealed class ForcedEntryExitProbeStrategy : IStrategy, IStrategyIngressSpec
    {
        private const string PeriodKey = "Time_1";
        private const int EntryBarIndex = 3;
        private const int ExitBarIndex = 10;
        private const int Quantity = 1;

        public StrategyInstanceId InstanceId { get; }
        public StrategyTemplateId TemplateId { get; }
        public string Version => "1.0.0-probe";

        private readonly IStrategyRuntimeContext _context;
        private readonly string _strategyId;
        private readonly Dictionary<string, SymbolState> _states = new(StringComparer.OrdinalIgnoreCase);

        public ForcedEntryExitProbeStrategy(
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
                "ForcedEntryExitProbe",
                StrategyCategory.System,
                StrategySeverity.Info,
                "PROBE_START",
                $"Probe started. period={PeriodKey} entryBar={EntryBarIndex} exitBar={ExitBarIndex} strategyId={_strategyId}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _context.OutputRouter.EmitPacket(
                TemplateId,
                InstanceId,
                StrategyScope.System,
                "ForcedEntryExitProbe",
                StrategyCategory.System,
                StrategySeverity.Info,
                "PROBE_STOP",
                $"Probe stopped. strategyId={_strategyId}");
            return Task.CompletedTask;
        }

        public ValueTask OnEventAsync(IMuEvent evt, CancellationToken cancellationToken = default)
        {
            if (evt is KLineClosedEvent kline)
            {
                OnKLine(kline);
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
                "ForcedEntryExitProbe",
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

            _context.OutputRouter.EmitPacket(
                TemplateId,
                InstanceId,
                StrategyScope.Symbol,
                symbol,
                StrategyCategory.Observation,
                StrategySeverity.Info,
                "PROBE_BAR",
                $"bar={state.BarCount} ts={bar.EndTime.Value:yyyy-MM-dd HH:mm:ss} close={bar.Close} inPosition={state.InPosition}");

            if (!state.InPosition && state.BarCount == EntryBarIndex)
            {
                EmitIntent(symbol, OrderSide.Buy, "FORCED_ENTRY_PROBE_ENTRY", bar.Close, state.BarCount);
                state.InPosition = true;
                state.EntryBar = state.BarCount;
                return;
            }

            if (state.InPosition && state.BarCount == ExitBarIndex)
            {
                EmitIntent(symbol, OrderSide.Sell, "FORCED_ENTRY_PROBE_EXIT", bar.Close, state.BarCount);
                state.InPosition = false;
            }
        }

        private void EmitIntent(string symbol, OrderSide side, string kind, decimal close, int barCount)
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
                $"kind={kind} cid={cid} side={side} qty={Quantity} bar={barCount} close={close} account={_context.AccountKey}");
        }

        private SymbolState GetOrCreate(string symbol)
        {
            if (_states.TryGetValue(symbol, out var state)) return state;
            state = new SymbolState();
            _states[symbol] = state;
            return state;
        }

        private sealed class SymbolState
        {
            public int BarCount { get; set; }
            public bool InPosition { get; set; }
            public int EntryBar { get; set; }
            public decimal LastClose { get; set; }
        }
    }
}

