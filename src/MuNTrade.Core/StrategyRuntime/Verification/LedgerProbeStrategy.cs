using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Market;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Events.System; // StrategyPulseEvent
using MuNTrade.Abstractions.Domain.Trading.Enums; // [Fix] OrderSide namespace
using MuNTrade.Abstractions.Domain.Common.Enums; // [Fix] MarketKind/MarketNo namespace

namespace MuNTrade.Core.StrategyRuntime.Verification
{
    public sealed class LedgerProbeStrategy : IStrategy
    {
        public StrategyInstanceId InstanceId { get; }
        public StrategyTemplateId TemplateId { get; }
        public string Version => "1.0.0-10C-ledger-probe";

        private readonly IStrategyRuntimeContext _context;
        private readonly ILogger _log;
        private readonly IMarketInfoProvider _market;

        private int _emitted; // 0/1 gate

        private const string SymFut = "TX00";
        private const string SymSec = "2344";

        public LedgerProbeStrategy(
            StrategyInstanceId instanceId,
            StrategyTemplateId templateId,
            IStrategyRuntimeContext context,
            ILogger<LedgerProbeStrategy> logger,
            IMarketInfoProvider market)
        {
            InstanceId = instanceId;
            TemplateId = templateId;
            _context = context;
            _log = logger;
            _market = market;
        }

        public IReadOnlyCollection<Type> Subscriptions => new[] { typeof(StrategyPulseEvent) };

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _context.OutputRouter.EmitPacket(
                TemplateId, InstanceId,
                scope: MuNTrade.Abstractions.Events.Strategy.StrategyScope.System,
                scopeKey: "10C",
                category: MuNTrade.Abstractions.Events.Strategy.StrategyCategory.System, // [Fix] Use System
                severity: MuNTrade.Abstractions.Events.Strategy.StrategySeverity.Info,
                actionCode: "10C_PROBE_START",
                payload: $"LedgerProbe started. AccountKey pinned by host. Waiting market info for {SymFut}/{SymSec}."
            );
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async ValueTask OnEventAsync(IMuEvent evt, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _emitted) != 0) return;

            if (!_market.TryGetMarketInfo(SymFut, out var futInfo)) return;
            if (!_market.TryGetMarketInfo(SymSec, out var secInfo)) return;

            if (Interlocked.Exchange(ref _emitted, 1) != 0) return;

            _log.LogInformation("[10C-PROBE] MarketInfo ready. {Fut}={FutKind}/{FutNo}, {Sec}={SecKind}/{SecNo}",
                SymFut, futInfo.Kind, futInfo.MarketNo,
                SymSec, secInfo.Kind, secInfo.MarketNo);

            EmitProbeIntent(SymFut, qty: 1);
            EmitProbeIntent(SymSec, qty: 1000);

            _context.OutputRouter.EmitPacket(
                TemplateId, InstanceId,
                scope: MuNTrade.Abstractions.Events.Strategy.StrategyScope.System,
                scopeKey: "10C",
                category: MuNTrade.Abstractions.Events.Strategy.StrategyCategory.System, // [Fix] Use System
                severity: MuNTrade.Abstractions.Events.Strategy.StrategySeverity.Info,
                actionCode: "10C_PROBE_INTENTS_EMITTED",
                payload: $"Emitted intents for {SymFut} and {SymSec}. Check Risk log."
            );

            await Task.CompletedTask; // [Fix] Async await
        }

        private void EmitProbeIntent(string symbol, long qty)
        {
            // [Fix] OrderIntent is a positional record
            var intent = new OrderIntent(
                Symbol: symbol,
                Side: qty > 0 ? OrderSide.Buy : OrderSide.Sell,
                Plan: null!,
                CorrelationId: Guid.NewGuid().ToString(),
                CreatedAt: new MuNTrade.Abstractions.Time.ArrivalTs(DateTime.UtcNow),
                StrategyId: MuNTrade.Core.StrategyRuntime.StrategyIdentity.BuildStrategyId(TemplateId.Value, InstanceId.Value),
                AccountKey: "",
                MarketKind: MarketKind.Unknown,
                MarketNo: MarketNo.Unknown,
                IntentKind: "10C_PROBE"
            );

            _context.OutputRouter.EmitIntent(
                InstanceId,
                TemplateId,
                intentKind: "10C_PROBE",
                intent
            );
        }
    }
}

