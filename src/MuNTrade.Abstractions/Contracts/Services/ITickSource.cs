
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using System;
using System.Collections.Generic;
using System.Threading;

namespace MuNTrade.Abstractions.Contracts.Services
{
    /// <summary>
    /// 定義一個歷史數據源需要提供的「非同步串流」功能合約。
    /// </summary>
    public interface ITickSource
    {
        /// <summary>
        /// 以非同步串流的方式，根據商品ID和日期範圍，逐筆回傳 Tick 資料。
        /// </summary>
        IAsyncEnumerable<TickData> StreamTicksAsync(
            string symbol,
            DateTime from,
            DateTime to,
            CancellationToken cancellationToken);

        /// <summary>
        /// 以非同步串流的方式，根據商品ID、時間週期和日期，逐筆回傳 K線 資料。
        /// </summary>
        IAsyncEnumerable<DynamicBar> StreamKLinesAsync(
            string stockId,
            int timeFrame,
            DateTime date,
            CancellationToken cancellationToken);
    }
}
