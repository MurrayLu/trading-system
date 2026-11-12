using System;
using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Time;
using MuNTrade.Abstractions.Domain.MarketData; // For KLine

namespace MuNTrade.Abstractions.Events.Strategy
{
    public enum StrategyState
    {
        // Lifecycle
        Initializing, Running, Stopping, Stopped, Faulted,
        // Trade Lifecycle
        Idle, Candidate, Validated, Armed, Filled, Managing, Closed, Rejected
    }
}
