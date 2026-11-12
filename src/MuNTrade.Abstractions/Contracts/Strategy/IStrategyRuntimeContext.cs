using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Time;
using Microsoft.Extensions.Logging;

namespace MuNTrade.Abstractions.Contracts.Strategy
{
    public interface IStrategyRuntimeContext
    {
        IEventBus EventBus { get; }
        IClock Clock { get; }
        ILogger Logger { get; }
        string AccountKey { get; } // [New] 10C Pinning
        IStrategyOutputRouter OutputRouter { get; }
    }
}

