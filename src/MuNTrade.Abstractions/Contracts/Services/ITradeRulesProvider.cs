using MuNTrade.Abstractions.Domain.Capital;
using System;

namespace MuNTrade.Abstractions.Contracts.Services
{
    public interface ITradeRulesProvider
    {
        TradeRulesConfig Current { get; }
        event Action<TradeRulesConfig> Changed;
        void Reload();
    }
}

