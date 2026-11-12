using MuNTrade.Abstractions.Settings.Common;
using MuNTrade.Abstractions.Settings.Data;
using MuNTrade.Abstractions.Settings.Trading;
using MuNTrade.Abstractions.Settings.Capital;
using MuNTrade.Abstractions.Settings.Backtest;
using MuNTrade.Abstractions.Settings.Strategy;

namespace MuNTrade.Abstractions.Contracts.Trading
{
    /// <summary>
    /// Legacy strategy parameter service from the pre-governed parameter path.
    /// It is frozen for compatibility only and is not an official Daytrade governed entry.
    /// AI adapters must not depend on this service.
    /// </summary>
    public interface IStrategyParamService
    {
        StrategyParamSchema GetSchema(string strategyId);
        StrategyParameters GetParameters(string strategyId);
        void UpdateParameters(string strategyId, StrategyParameters parameters);
        void SetScopedOverride(string strategyId, StrategyParameters parameters);
        void ClearScopedOverride(string strategyId);
    }
}

