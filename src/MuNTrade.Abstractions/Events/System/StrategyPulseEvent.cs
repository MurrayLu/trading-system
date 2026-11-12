using System;
using MuNTrade.Abstractions.Contracts.Eventing;

namespace MuNTrade.Abstractions.Events.System
{
    public sealed record StrategyPulseEvent(
        int Seq,
        string? Note,
        DateTimeOffset OccurredAt,
        string? CorrelationId
    ) : IMuEvent;
}
