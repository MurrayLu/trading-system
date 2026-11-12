// MuNTrade.Abstractions/Interfaces/Services/IHistoricalKlineDownloader.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;

namespace MuNTrade.Abstractions.Contracts.Warmup
{
    public interface IHistoricalKlineDownloader
    {
        /// <summary>
        /// 從 broker 下載 [start..lcd]（可能切月多次請求），回傳整段列 K。
        /// 完成後請自行保存月度滾動檔，並 Upsert inventory 到 [該月首個交易日..LCD]。
        /// </summary>
        Task<IReadOnlyList<JsonKLineRecord>> DownloadAsync(
            string symbol, string timeframeKey, DateOnly start, DateOnly lcd, CancellationToken ct = default);
    }
}

