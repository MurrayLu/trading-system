using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Contracts.Eventing;
using MuNTrade.Abstractions.Contracts.Strategy;

namespace MuNTrade.Abstractions.Contracts.Strategy
{
    public interface IStrategyEventStore
    {
        Task AppendAsync(StrategyInstanceId id, IMuEvent evt, CancellationToken ct = default);
        IAsyncEnumerable<IMuEvent> ReadTailAsync(StrategyInstanceId id, int n, CancellationToken ct = default);
    }
}
