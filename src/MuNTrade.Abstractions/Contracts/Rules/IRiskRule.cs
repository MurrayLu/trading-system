using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;

namespace MuNTrade.Abstractions.Contracts.Rules
{
    /// <summary>
    /// 定義單一風控規則的契約。
    /// </summary>
    public interface IRiskRule
    {
        /// <summary>
        /// ★★★ 進階修改：讓規則可以感知當前倉位 ★★★
        /// </summary>
        RiskDecision Evaluate(OrderRequest request, Position currentPosition);
    }
}
