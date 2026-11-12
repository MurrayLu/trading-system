using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Contracts.Time;
using MuNTrade.Abstractions.Domain.MarketData;

namespace MuNTrade.Core.Backtesting.Warmup
{
    /// <summary>
    /// 提供回測增量預熱時，從硬碟冷儲存讀取指定區間與數量的歷史 K 棒
    /// </summary>
    public interface IKLineHistoryProvider
    {
        /// <summary>
        /// 讀取給定商品、週期介於 from 與 to (包含) 的歷史 K 線。若數量為 0 或超過 maxLookback 則以 maxLookback 為準裁剪。
        /// </summary>
        Task<IReadOnlyList<DynamicBar>> PullHistoryAsync(
            string symbol,
            TimeSpan timeFrame,
            DateOnly from,
            DateOnly to,
            int maxLookback,
            CancellationToken ct = default);
    }
}

