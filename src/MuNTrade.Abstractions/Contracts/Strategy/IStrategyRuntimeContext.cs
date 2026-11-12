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
        string AccountKey { get; }
        IStrategyOutputRouter OutputRouter { get; }
    }
}

