using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Settings.Strategy;

namespace MuNTrade.Abstractions.Contracts.Strategy
{
    public interface IStrategyRecipeStore
    {
        Task<StrategyRecipe?> GetAsync(StrategyRecipeId id, CancellationToken cancellationToken = default);
        Task SaveAsync(StrategyRecipe recipe, CancellationToken cancellationToken = default);
    }
}

