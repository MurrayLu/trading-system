using System.Collections.Generic;

namespace MuNTrade.Abstractions.Contracts.Strategy
{
    public interface IStrategyRegistry
    {
        IEnumerable<IStrategyTemplate> Templates { get; }
    }
}

