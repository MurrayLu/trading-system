using MuNTrade.Abstractions.Dtos.Trading;
using MuNTrade.Abstractions.Dtos.Broker;
using MuNTrade.Abstractions.Dtos.Capital;
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Events.Broker
{
    public sealed class BrokerPositionSnapshotEvent : MuNTrade.Abstractions.Contracts.Eventing.IMuEvent
    {
        public global::System.DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
        public string AccountKey { get; init; } = ""; // Primary Key (e.g., "Real")
        public string? BrokerAccountNo { get; init; }  // Trace Only (e.g., "918W...")
        public bool IsFinal { get; init; }
        public MuNTrade.Abstractions.Domain.Common.Enums.MarketKind? Scope { get; init; }
        public List<PositionDto> Positions { get; init; } = new();
    }
}

