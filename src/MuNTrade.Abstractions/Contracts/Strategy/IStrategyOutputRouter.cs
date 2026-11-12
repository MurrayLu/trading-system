using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Events.Strategy;

namespace MuNTrade.Abstractions.Contracts.Strategy
{
    public interface IStrategyOutputRouter
    {
        void EmitPacket(
            StrategyTemplateId templateId,
            StrategyInstanceId instanceId,
            StrategyScope scope,
            string? scopeKey,
            StrategyCategory category,
            StrategySeverity severity,
            string actionCode,
            object? payload = null);

        void Emit(StrategyPacketEmittedEvent evt);

        void EmitStateChange(
            StrategyInstanceId instanceId,
            string symbol,
            StrategyState from,
            StrategyState to,
            string? reason = null);

        void EmitIntent(StrategyInstanceId instanceId, StrategyTemplateId templateId, string intentKind, object intent);

        // Allow raw generic events for flexibility
        void Publish(IMuEvent evt);
    }
}
