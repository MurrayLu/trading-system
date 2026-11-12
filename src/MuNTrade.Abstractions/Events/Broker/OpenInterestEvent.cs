using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.Broker
{
    public sealed record OpenInterestEvent : IMuEvent
    {
        public ArrivalTs ArrivalTs { get; init; }
        public global::System.DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
        public string RawCsv { get; init; } = "";
        public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

        // 1~11
        public string Market { get; init; } = "";            // 1
        public string Account { get; init; } = "";           // 2
        public string Symbol { get; init; } = "";            // 3
        public string Side { get; init; } = "";              // 4 (B/S)
        public long OpenInterest { get; init; }              // 5
        public long DayTradeOpenInterest { get; init; }      // 6
        public decimal AverageCost { get; init; }            // 7
        public decimal PointValue { get; init; }             // 8
        public decimal FeePerLot { get; init; }              // 9
        public decimal TaxPerTenThousand { get; init; }      // 10
        public string LoginId { get; init; } = "";           // 11


    }
}
