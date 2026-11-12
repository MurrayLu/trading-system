using MuNTrade.Abstractions.Domain.MarketData;
using MuNTrade.Abstractions.Domain.Trading;
using MuNTrade.Abstractions.Domain.Capital;
using MuNTrade.Abstractions.Domain.Optimization;
using MuNTrade.Abstractions.Domain.Session;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Services
{
    /// <summary>
    /// 定義了將優化結果匯出的服務介面。
    /// </summary>
    public interface IResultExporter
    {
        /// <summary>
        /// 非同步地將一系列優化結果匯出到指定的目的地 (例如 CSV 檔案)。
        /// </summary>
        /// <param name="results">要匯出的優化結果集合。</param>
        /// <param name="ct">用於取消操作的 CancellationToken。</param>
        /// <returns>代表非同步操作的 Task。</returns>
        Task ExportAsync(IEnumerable<OptimizationResult> results, CancellationToken ct = default);
    }
}
