using MuNTrade.Abstractions.Contracts.Hubs;
using MuNTrade.Abstractions.Contracts.Strategy;
using MuNTrade.Abstractions.Contracts.Time;
using Microsoft.Extensions.Logging;

namespace MuNTrade.Core.StrategyRuntime.Hosting
{
    public class StrategyRuntimeContext : IStrategyRuntimeContext
    {
        public IEventBus EventBus { get; }
        public IClock Clock { get; }
        public ILogger Logger { get; }
        public string AccountKey { get; }
        public IStrategyOutputRouter OutputRouter { get; }

        public StrategyRuntimeContext(IEventBus eventBus, IClock clock, ILogger logger, IStrategyOutputRouter outputRouter, string accountKey)
        {
            EventBus = eventBus;
            Clock = clock;
            Logger = logger;
            OutputRouter = outputRouter;
            AccountKey = accountKey;
        }
    }
}
