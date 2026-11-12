using MuNTrade.Abstractions.Domain.MarketData.Enums;
using MuNTrade.Abstractions.Contracts.Eventing;
using System;

namespace MuNTrade.Abstractions.Events.System
{
    public sealed record TickAttachStateChangedEvent : IMuEvent
    {
        public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.Now;
        public string Symbol { get; init; } = "";
        
        /// <summary>
        /// None, Attaching, Attached, RetryPending, Failed, Disabled
        /// 枚舉可以定義在 MuNTrade.Abstractions.Domain.MarketData.Enums 中或重用 string
        /// 但是目前我們把它作為字串或整數傳遞？
        /// 我們自己定一個 string state
        /// </summary>
        public string NewState { get; init; } = "";

        public string? Reason { get; init; }
        public int AttemptVersion { get; init; }
        public string? CorrelationId { get; init; }
    }
}

