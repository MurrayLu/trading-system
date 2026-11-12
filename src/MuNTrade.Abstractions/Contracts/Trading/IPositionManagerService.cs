// 建議路徑: MuNTrade.Abstractions/Interfaces/Services/IPositionManagerService.cs

using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Contracts.Trading
{
    public interface IPositionManagerService
    {
        /// <summary>
        /// ★★★ 新增：定義處理策略訊號的公開方法 ★★★
        /// 這是 BacktestEngine 或其他訊號來源呼叫的入口點。
        /// </summary>
        void ProcessSignal(SignalEvent signal);

        /// <summary>
        /// 獲取指定商品的當前倉位。返回非 null 的 Position 物件。
        /// </summary>
        Position GetCurrentPosition(string stockId);

        /// <summary>
        /// 根據成交回報更新倉位狀態。
        /// </summary>
        void UpdatePositionOnFill(OrderFillEvent fill);

        /// <summary>
        /// 獲取所有未平倉的部位。
        /// </summary>
        IEnumerable<Position> GetAllOpenPositions();
    }
}
