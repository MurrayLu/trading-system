using System;

namespace MuNTrade.Abstractions.Contracts.Eventing
{
    public interface IMuEvent
    {
        DateTimeOffset OccurredAt { get; }
        string? CorrelationId { get; }
    }
}

