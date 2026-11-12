using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Events.Strategy;
using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Contracts.Time;

namespace MuNTrade.Core.StrategyRuntime.Routing
{
    public sealed class StrategyOutputRouter : IStrategyOutputRouter
    {
        private readonly IEventBus _eventBus;
        private readonly IClock _clock;
        private readonly MuNTrade.Abstractions.Contracts.Market.IMarketInfoProvider _marketInfo; // [10C]
        private readonly string _pinnedAccountKey; // [10C]
        private Func<StrategyRuntimeState>? _stateAccessor;

        public StrategyOutputRouter(
            IEventBus eventBus,
            IClock clock,
            MuNTrade.Abstractions.Contracts.Market.IMarketInfoProvider marketInfo,
            string accountKey)
        {
            _eventBus = eventBus;
            _clock = clock;
            _marketInfo = marketInfo;
            _pinnedAccountKey = accountKey;
        }

        public void ConfigureStateAccessor(Func<StrategyRuntimeState> stateAccessor)
        {
            _stateAccessor = stateAccessor;
        }

        public void EmitPacket(
            StrategyTemplateId templateId,
            StrategyInstanceId instanceId,
            StrategyScope scope,
            string? scopeKey,
            StrategyCategory category,
            StrategySeverity severity,
            string actionCode,
            object? payload = null)
        {
            // Helper to bridge payload object to dictionaries if needed, or just allow flexible construction
            // For now, adhere to Strict Event contract
            var evt = new StrategyPacketEmittedEvent(
                templateId,
                instanceId,
                scope,
                scopeKey,
                category,
                severity,
                ActionCode: actionCode,
                HumanReason: payload?.ToString(),
                OccurredAt: _clock.Now
            );

            _eventBus.Publish(evt);
        }

        public void Emit(StrategyPacketEmittedEvent evt)
        {
            _eventBus.Publish(evt);
        }

        public void EmitStateChange(
            StrategyInstanceId instanceId,
            string symbol,
            StrategyState from,
            StrategyState to,
            string? reason = null)
        {
            var now = new MuNTrade.Abstractions.Time.ArrivalTs(_clock.Now);
            var evt = new StrategyStateChangedEvent(
                now,
                instanceId,
                symbol,
                from,
                to,
                reason
            )
            {
                OccurredAt = new DateTimeOffset(_clock.Now)
            };
            _eventBus.Publish(evt);
        }

        // Backward compatibility / Transitional
        public void EmitIntent(StrategyInstanceId instanceId, StrategyTemplateId templateId, string intentKind, object intent)
        {
            // GATE: Check if strict runtime is Running
            if (_stateAccessor != null)
            {
                var currentState = _stateAccessor();
                if (currentState != StrategyRuntimeState.Running)
                {
                    // Rejected: Strategy Runtime is not running (e.g. Stopping/Stopped/Faulted/Initializing).
                    // Hard-block.
                    return;
                }
            }

            // ATTRIBUTION: Enforce StrategyId and AccountKey + Market pinning
            object payloadToEmit = intent;
            if (intent is MuNTrade.Abstractions.Dtos.Trading.OrderIntent orderIntent)
            {
                // 1. AccountKey Pinning
                var finalAccountKey = string.IsNullOrWhiteSpace(orderIntent.AccountKey)
                    ? _pinnedAccountKey
                    : orderIntent.AccountKey;

                // Enforce strictly
                if (string.IsNullOrWhiteSpace(finalAccountKey))
                {
                    // Log or just drop? Strict mode says reject.
                    // Since we can't emit rejection event back easily here without structure, we just drop/log.
                    // Ideally we should log a System Error packet OR throw if we want to be noisy.
                    // For safety, we drop.
                    return;
                }

                // 2. Market Pinning [10C]
                // We must perform Market Truth Resolution here.
                // If this fails (throws), it will crash the strategy logic safely (stop it).
                // GetMarketInfoOrThrow is appropriate for Execution path.
                var marketInfo = _marketInfo.GetMarketInfoOrThrow(
                    orderIntent.Symbol,
                    MuNTrade.Abstractions.Contracts.Market.MarketInfoPurpose.Risk // Using Risk strictness for Intent
                );

                // 3. Patch Intent
                // Even if StrategyId invalid, we fix it
                var strategyId = !string.IsNullOrEmpty(orderIntent.StrategyId)
                    ? orderIntent.StrategyId
                    : MuNTrade.Core.StrategyRuntime.StrategyIdentity.BuildStrategyId(templateId.Value, instanceId.Value);

                payloadToEmit = orderIntent with
                {
                    StrategyId = strategyId,
                    AccountKey = finalAccountKey,
                    MarketKind = marketInfo.Kind,
                    MarketNo = marketInfo.MarketNo
                };
            }

            _eventBus.Publish(new StrategyIntentEmittedEvent(
                instanceId,
                templateId,
                intentKind,
                payloadToEmit,
                _clock.Now
            ));
        }

        public void Publish(MuNTrade.Abstractions.Contracts.Eventing.IMuEvent evt)
        {
            _eventBus.Publish(evt);
        }
    }
}

