using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;

namespace MuNTrade.Abstractions.Events.Broker
{
    /// <summary>
    /// 代表從交易閘道 (真實或模擬) 收到的原始回報事件。
    /// </summary>
    public class BrokerReportEvent : IMuEvent
    {
        /// <summary>
        /// 原始的回報字串。
        /// </summary>
        public string RawReport { get; }
        public DateTimeOffset OccurredAt { get; init; }
        public string? CorrelationId { get; init; }

        public BrokerReportEvent(string rawReport)
        {
            RawReport = rawReport;
        }
    }
}

