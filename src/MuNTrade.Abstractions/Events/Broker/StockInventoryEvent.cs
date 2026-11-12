using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;

namespace MuNTrade.Abstractions.Events.Broker
{
    /// <summary>
    /// 證券庫存回報（對應 SKOrderLib_OnRealBalanceReport）。
    /// </summary>
    public sealed record StockInventoryEvent : IMuEvent
    {
        public ArrivalTs ArrivalTs { get; init; }
        public global::System.DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }
        public string RawCsv { get; init; } = "";
        public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();

        // 1~19
        public string StockId { get; init; } = "";           // 1
        public string InventoryType { get; init; } = "";     // 2: T/C/L
        public decimal MarginQuotaOriginal { get; init; }    // 3
        public decimal MarginQuotaAvailable { get; init; }   // 4
        public decimal ShortQuotaOriginal { get; init; }     // 5
        public decimal ShortQuotaAvailable { get; init; }    // 6
        public long YesterdayQty { get; init; }              // 7
        public long TodayBuyOrders { get; init; }            // 8
        public long TodaySellOrders { get; init; }           // 9
        public long TodayBuyFilled { get; init; }            // 10
        public long TodaySellFilled { get; init; }           // 11
        public long AvailableToSell { get; init; }           // 12
        public long DayTradeBuyableQty { get; init; }        // 13
        public long DayTradeSellableQty { get; init; }       // 14
        public long RealtimeQty { get; init; }               // 15
        public string ReservedX { get; init; } = "";         // 16
        public decimal MaintenanceRate { get; init; }        // 17
        public string LoginId { get; init; } = "";           // 18
        public string AccountNo { get; init; } = "";         // 19


    }
}
