using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Settings.Strategy;

namespace MuNTrade.Abstractions.Contracts.Strategy
{
    /// <summary>
    /// Legacy parameter schema provider from the pre-governed parameter path.
    /// It is frozen for compatibility only and is not an official Daytrade governed entry.
    /// AI adapters must not depend on this provider.
    /// </summary>
    [Obsolete("Legacy strategy parameter schema provider. Do not use for Daytrade parameter governance.")]
    public interface IStrategyParamSchemaProvider
    {
        Task<StrategyParamSchema?> GetSchemaAsync(StrategyTemplateId templateId, string version, CancellationToken cancellationToken = default);
    }
}

