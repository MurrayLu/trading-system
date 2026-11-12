using MuNTrade.Abstractions.Domain.Enums.SystemEnums;

namespace MuNTrade.Abstractions.Contracts.Eventing
{
    public sealed record EventEnvelope(
        IMuEvent Payload,
        AppMode AppMode,
        ExecutionMode? ExecutionMode = null,
        string? Producer = null,
        string? TraceId = null) : IMuEvent
    {
        public System.DateTimeOffset OccurredAt => Payload.OccurredAt;
        public string? CorrelationId => Payload.CorrelationId;
    }
}

