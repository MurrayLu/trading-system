using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using System.Collections.Generic;

namespace MuNTrade.Abstractions.Contracts.Hubs
{
    /// <summary>
    /// 提供逐筆成交的快照查詢與狀態資訊，給 UI 或其他服務取用。
    /// </summary>
    public interface ITickStreamHub
    {
        /// <summary>
        /// 取得指定商品的逐筆快照。
        /// </summary>
        /// <param name="symbol">商品代碼。</param>
        /// <param name="maxCount">限制回傳筆數（從最新往回取），若為 null 則回傳全部。</param>
        IReadOnlyList<TradeTickEvent> GetSnapshot(string symbol, int? maxCount = null);
    }
}

