using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.Broker
{
    /// <summary>
    /// 委託/成交核心回報（對應 SKReplyLib_OnNewData）。
    /// </summary>
    public sealed record OrderReportEvent : IMuEvent
    {
        public ArrivalTs ArrivalTs { get; init; }
        public string UserId { get; init; } = "";
        public string Raw { get; init; } = "";
        public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}
