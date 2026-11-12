using MuNTrade.Abstractions.Events.MarketData;
using MuNTrade.Abstractions.Events.OrderBook;
using MuNTrade.Abstractions.Dtos.OrderBook;
using MuNTrade.Abstractions.Domain.OrderBook.Enums;
using MuNTrade.Abstractions.Events.Broker;
using MuNTrade.Abstractions.Events.Capital;
using MuNTrade.Abstractions.Events.System;
using MuNTrade.Abstractions.Events.Strategy;
using System.Diagnostics.CodeAnalysis;

namespace MuNTrade.Abstractions.Contracts.Services
{
    /// <summary>
    /// 提供一個可查詢的、儲存最新商品靜態資料快照的服務。
    /// 它的主要目的是為了解決後端服務與晚啟動的 UI 之間的時序問題。
    /// </summary>
    public interface IQuoteStateService
    {
        /// <summary>
        /// 嘗試獲取指定商品的最新靜態資料。
        /// </summary>
        /// <param name="symbol">商品代碼。</param>
        /// <param name="state">如果找到，則為該商品的靜態資料事件物件。</param>
        /// <returns>如果快取中存在該商品的資料，則為 true；否則為 false。</returns>
        bool TryGetState(string symbol, [NotNullWhen(true)] out SymbolStaticDataLoadedEvent? state);
    }
}
