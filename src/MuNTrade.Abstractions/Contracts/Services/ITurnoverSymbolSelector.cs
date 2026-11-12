using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MuNTrade.Abstractions.Contracts.Services
{
    public sealed record TurnoverSelectionSnapshotResult
    {
        public DateOnly TargetTradeDate { get; init; }
        public DateOnly RankingSnapshotDate { get; init; }
        public bool SnapshotFound { get; init; }
        public string? SnapshotPath { get; init; }
        public IReadOnlyList<string> Symbols { get; init; } = Array.Empty<string>();
    }

    public interface ITurnoverSymbolSelector
    {
        /// <summary>
        /// 根據指定的交易日，取得前一交易日的周轉率 Top-N 標的清單。
        /// </summary>
        /// <param name="tradeDate">要進行交易的日期。</param>
        /// <param name="topN">要取得的標的數量。</param>
        /// <returns>標的代碼列表。</returns>
        Task<IReadOnlyList<string>> GetSymbolsAsync(DateTime tradeDate, int topN = 20);

        /// <summary>
        /// 取得正式 research universe 所使用的 ranking snapshot 與符號清單。
        /// target_trade_date = T，ranking_snapshot_date = previous_trading_day(T)。
        /// </summary>
        Task<TurnoverSelectionSnapshotResult> GetSnapshotAsync(DateOnly targetTradeDate, int topN = 20, CancellationToken ct = default);
    }
}

