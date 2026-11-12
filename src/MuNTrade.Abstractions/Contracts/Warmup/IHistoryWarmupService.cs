// MuNTrade.Abstractions/Interfaces/Services/IHistoryWarmupService.cs
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
    public interface IHistoryWarmupService
    {
        /// <summary>
        /// 依據規劃 (IWarmupPlanner) 與 Inventory，確保資料齊備到 LCD，
        /// 並回傳每個 timeframe 最近 N 根列 K（給上游要不要發事件由上游決定）。
        /// </summary>
        Task<Dictionary<string, List<JsonKLineRecord>>> WarmupAsync(
            string symbol,
            CancellationToken ct = default);
    }
}

