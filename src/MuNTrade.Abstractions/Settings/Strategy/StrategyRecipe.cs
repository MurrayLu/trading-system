using System.Collections.Generic;
using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Abstractions.Settings.Strategy
{
    public sealed record StrategyRecipe(
        StrategyRecipeId Id,
        StrategyTemplateId TemplateId,
        string Version,
        IReadOnlyList<StrategyParamValue> Params,
        StrategyRiskProfile Risk,
        StrategyExecutionProfile Execution);
}

