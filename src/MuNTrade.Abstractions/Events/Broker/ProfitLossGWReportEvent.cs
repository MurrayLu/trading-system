using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Dtos.Broker; // For ProfitLossRow
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.Broker
{
    /// <summary>
    /// 未平倉損益回報（對應 SKOrderLib_OnProfitLossGWReport）。
    /// </summary>
    public sealed record ProfitLossGWReportEvent : IMuEvent
    {
        public ArrivalTs ArrivalTs { get; init; }
        public string ResultCode { get; init; } = "";
        public string ResultMessage { get; init; } = "";
        public ProfitLossQueryContext? Context { get; init; }
        public IReadOnlyList<ProfitLossRow> Rows { get; init; } = Array.Empty<ProfitLossRow>();

        public DateTimeOffset OccurredAt { get; init; } = default;
        public string? CorrelationId { get; init; } = null;
    }
}

