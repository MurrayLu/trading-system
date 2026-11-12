using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;

namespace MuNTrade.Abstractions.Contracts.Trading
{
    public interface IRiskManagerService
    {
        /// <summary>
        /// 評估一個交易信號，如果通過風險檢查，則產生一個訂單請求。
        /// </summary>
        /// <param name="request">由 PositionManager 建立的訂單請求</param>
        /// <param name="finalOrder">如果通過，返回填充了 SL/TP 的最終訂單</param>
        /// <returns>如果信號通過風控則回傳 true，否則回傳 false。</returns>
        bool AssessOrder(OrderRequest request, Position? currentPosition, out OrderRequest? finalOrder);
    }
}
