using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Strategy
{
    public interface IStrategy
    {
        StrategyInstanceId InstanceId { get; }
        StrategyTemplateId TemplateId { get; }
        string Version { get; }

        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 宣告此策略實例感興趣的事件類型。
        /// Router 會根據此清單決定是否呼叫 OnEventAsync。
        /// </summary>
        IReadOnlyCollection<Type> Subscriptions { get; }

        /// <summary>
        /// 處理訂閱的事件。
        /// </summary>
        ValueTask OnEventAsync(MuNTrade.Abstractions.Contracts.Eventing.IMuEvent evt, CancellationToken cancellationToken = default);
    }
}

