using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Contracts.Eventing;

namespace MuNTrade.Abstractions.Contracts.Strategy
{
    public interface IStrategyHost
    {
        Task<StrategyInstanceId> StartInstanceAsync(StrategyRecipeId recipeId, string accountKey, CancellationToken cancellationToken = default);
        Task StopInstanceAsync(StrategyInstanceId instanceId, CancellationToken cancellationToken = default);
        IEnumerable<IStrategy> GetActiveInstances();
        IEnumerable<StrategyInstanceStatus> GetInstanceStatuses();
        // Refined 10B: Hot Path Snapshot (Zero Allocation)
        IReadOnlyList<StrategyInstanceStatus> GetRunningInstanceSnapshot();
        ValueTask DeliverAsync(StrategyInstanceId instanceId, IMuEvent evt, CancellationToken cancellationToken = default);
        void ReportStrategyCrash(StrategyInstanceId instanceId, Exception ex);

        // Service lifecycle
        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
    }
}

